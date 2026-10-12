using System.Globalization;
using Millway.Core.Gcode;
using Millway.Core.Grbl;

namespace Millway.Core.Machine;

public enum MachineState { Disconnected, Connecting, NotInitialized, Idle, Busy, Running, Paused, Alarm }

public sealed record Prompt(string Title, string Message, params string[] Buttons);

public enum RapidPosition { NorthWest, North, NorthEast, West, Center, East, SouthWest, South, SouthEast }

/// <summary>
/// The machine as the operator sees it: connect, initialize, jog, zero, measure
/// tools and run jobs. Every multi-step cycle is an async method that sends
/// G-code and awaits the controller, so only one can run at a time.
/// </summary>
public sealed class MachineController : IDisposable
{
    const double SafeZ = -5;          // machine Z used for all traverses
    const double FastProbeFeed = 800; // BitSetter first touch, mm/min
    const double SlowProbeFeed = 200; // BitSetter second touch
    const double PlateProbeFeed = 100;
    const double PlateProbeTravel = 12;

    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Dictionary<int, double> _grbl = new();
    GrblClient? _client;
    CancellationTokenSource? _jobCts;
    (Vec3 Pos, bool Hit)? _probe;
    Vec3? _lastPos;
    volatile string? _jobError;
    int _jobDone;
    double _spindleRpm;
    bool _expectReset;
    volatile bool _probing;

    public MachineController(AppSettings settings) => Settings = settings;

    public AppSettings Settings { get; }
    public MachineState State { get; private set; } = MachineState.Disconnected;
    public GrblStatus Status { get; private set; } = new();
    public string GrblVersion { get; private set; } = "";
    public string PortName { get; private set; } = "";
    public bool Homed { get; private set; }
    public bool ToolMeasured { get; private set; }
    public bool DoorOpen => Status.State == GrblState.Door || Status.Door;
    public string BusyText { get; private set; } = "";
    public string LastError { get; private set; } = "";
    public IReadOnlyDictionary<int, double> GrblSettings => _grbl;

    public GcodeProgram? Job { get; private set; }
    public int JobLinesDone => _jobDone;
    public DateTime JobStarted { get; private set; }
    public TimeSpan JobElapsed => State is MachineState.Running or MachineState.Paused ? DateTime.Now - JobStarted : _lastJobTime;
    TimeSpan _lastJobTime;

    /// <summary>Raised on any change of state, position or progress. May fire on any thread.</summary>
    public event Action? Changed;
    public event Action<string>? Log;
    /// <summary>Asks the operator a question; returns the index of the chosen button.</summary>
    public Func<Prompt, Task<int>> PromptHandler { get; set; } = _ => Task.FromResult(0);

    public bool HomingEnabled => _grbl.GetValueOrDefault(22) != 0;

    /// <summary>Axis travel in mm, from the controller's $130-$132.</summary>
    public Vec3 Travel => new(_grbl.GetValueOrDefault(130), _grbl.GetValueOrDefault(131), _grbl.GetValueOrDefault(132));

    GrblClient Client => _client ?? throw new GrblException("Not connected.");

    static string F(double v) => GrblText.F(v);

    void SetState(MachineState s, string busy = "")
    {
        State = s;
        BusyText = busy;
        Changed?.Invoke();
    }

    // ---- connection ---------------------------------------------------------------

    public async Task ConnectAsync(ITransport transport)
    {
        if (_client != null) Disconnect();
        SetState(MachineState.Connecting);
        var client = new GrblClient(transport);
        try
        {
            client.LineReceived += OnLine;
            client.LineSent += l => Log?.Invoke("> " + l);
            client.StatusReceived += OnStatus;
            client.Alarm += OnAlarm;
            client.Restarted += OnRestarted;
            client.Disconnected += reason => { Log?.Invoke("Disconnected: " + reason); Disconnect(); };
            client.Open();
            _client = client;
            PortName = transport.Name;

            string welcome;
            // Raising DTR restarts some controllers on its own, so a second banner
            // can follow the one our reset produces. Neither is a fault.
            _expectReset = true;
            try
            {
                var w = client.WaitForWelcomeAsync(TimeSpan.FromSeconds(5));
                client.Realtime(0x18);
                welcome = await w.ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new GrblException("The machine controller did not respond. Please make sure your machine is powered on.");
            }
            var m = GrblText.Welcome().Match(welcome);
            GrblVersion = m.Groups[1].Value + m.Groups[2].Value;
            if (double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) < 1.1)
                throw new GrblException("GRBL versions prior to 1.1 are not supported.");

            await Task.Delay(500).ConfigureAwait(false); // let a second banner and the unlock message arrive
            _expectReset = false;
            client.StartPolling();
            await ReadGrblSettingsAsync().ConfigureAwait(false);
            Homed = false;
            ToolMeasured = false;
            LastError = "";
            if (HomingEnabled) SetState(MachineState.NotInitialized);
            else
            {
                await client.CommandAsync("$X").ConfigureAwait(false);
                await client.CommandAsync("G21 G90 G54").ConfigureAwait(false);
                SetState(MachineState.Idle);
            }
        }
        catch
        {
            _expectReset = false;
            client.Dispose();
            _client = null;
            SetState(MachineState.Disconnected);
            throw;
        }
    }

    public void Disconnect()
    {
        var c = _client;
        _client = null;
        _jobCts?.Cancel();
        c?.Dispose();
        Homed = false;
        SetState(MachineState.Disconnected);
    }

    async Task ReadGrblSettingsAsync()
    {
        _grbl.Clear();
        await Client.CommandAsync("$$").ConfigureAwait(false);
    }

    void OnLine(string line)
    {
        Log?.Invoke(line);
        var s = GrblText.Setting().Match(line);
        if (s.Success)
        {
            lock (_grbl) _grbl[int.Parse(s.Groups[1].Value)] = double.Parse(s.Groups[2].Value, CultureInfo.InvariantCulture);
            return;
        }
        var p = GrblText.ProbeResult().Match(line);
        if (p.Success && GrblStatus.TryVec(p.Groups[1].Value, out var pos))
            _probe = (pos, p.Groups[2].Value == "1");
    }

    void OnStatus(GrblStatus st)
    {
        if (_lastPos is { } last && Homed)
        {
            Settings.TravelXmm += Math.Abs(st.MachinePos.X - last.X);
            Settings.TravelYmm += Math.Abs(st.MachinePos.Y - last.Y);
            Settings.TravelZmm += Math.Abs(st.MachinePos.Z - last.Z);
        }
        _lastPos = st.MachinePos;
        Status = st;
        if (State == MachineState.Running && st.State is GrblState.Hold or GrblState.Door) State = MachineState.Paused;
        Changed?.Invoke();
    }

    void OnAlarm(int code)
    {
        // Probe alarms are part of a probing cycle and are reported by the cycle itself.
        if (code is 4 or 5 && _probing) return;
        LastError = $"GRBL alarm {code}: {GrblText.AlarmText(code)}";
        Log?.Invoke(LastError);
        _jobCts?.Cancel();
        if (code is 1 or 3 or 6 or 7 or 8 or 9 or 10) Homed = false;
        SetState(MachineState.Alarm);
    }

    void OnRestarted(string banner)
    {
        if (_expectReset) return;
        LastError = "The machine controller restarted unexpectedly and may be in an undefined state.";
        Log?.Invoke(LastError);
        _jobCts?.Cancel();
        Homed = false;
        SetState(HomingEnabled ? MachineState.NotInitialized : MachineState.Idle);
    }

    // ---- cycle plumbing -----------------------------------------------------------

    /// <summary>Runs one exclusive operation, restoring a sensible state however it ends.</summary>
    async Task RunCycle(string name, Func<Task> body, MachineState[]? allowed = null)
    {
        allowed ??= [MachineState.Idle];
        if (!allowed.Contains(State)) throw new GrblException($"Cannot {name} while the machine is {State}.");
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) throw new GrblException("The machine is busy.");
        var before = State;
        try
        {
            SetState(MachineState.Busy, name);
            await body().ConfigureAwait(false);
            if (State == MachineState.Busy) SetState(MachineState.Idle);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Log?.Invoke("ERROR: " + e.Message);
            if (State == MachineState.Busy) SetState(before == MachineState.Idle && _client != null ? MachineState.Idle : before);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    Task Cmd(string line) => Client.CommandAsync(line);

    /// <summary>Waits until everything sent so far has finished moving.</summary>
    async Task SyncAsync()
    {
        await Cmd("G4 P0.01").ConfigureAwait(false);
        await Client.WaitForStatusAsync(s => s.State == GrblState.Idle, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
    }

    Task<GrblStatus> FreshStatusAsync() => Client.WaitForStatusAsync(_ => true, TimeSpan.FromSeconds(3));

    void RequireHomed()
    {
        if (HomingEnabled && !Homed) throw new GrblException("Initialize the machine first.");
    }

    // ---- initialize / homing --------------------------------------------------------

    public Task InitializeAsync() => RunCycle("initialize", async () =>
    {
        if (HomingEnabled)
        {
            var st = await FreshStatusAsync().ConfigureAwait(false);
            if (st.AnyLimit)
            {
                // Already sitting on a switch: back off before homing.
                await Cmd("$X").ConfigureAwait(false);
                int mask = (int)_grbl.GetValueOrDefault(23);
                var jog = "";
                for (int a = 0; a < 3; a++)
                    if (st.Pins.Contains("XYZ"[a])) jog += $"{"XYZ"[a]}{((mask & (1 << a)) != 0 ? 5 : -5)}";
                await Cmd($"$J=G91 G21 {jog} F300").ConfigureAwait(false);
                await Client.WaitForStatusAsync(s => s.State == GrblState.Idle, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                st = await FreshStatusAsync().ConfigureAwait(false);
                if (st.AnyLimit)
                    throw new GrblException("HM001: The machine could not pull off the homing switches.\n\nThis could be caused by:\n- Mechanical problems jamming a machine axis\n- A malfunctioning homing switch");
            }
            try
            {
                await Cmd("$H").ConfigureAwait(false);
            }
            catch (GrblException e)
            {
                throw new GrblException("Homing failed. " + e.Message);
            }
        }
        else await Cmd("$X").ConfigureAwait(false);

        await Cmd("G21 G90 G54").ConfigureAwait(false);
        await Cmd("G49").ConfigureAwait(false);
        Homed = true;
        ToolMeasured = false;
        LastError = "";
        if (Settings.BitSetterEnabled)
        {
            int r = await PromptHandler(new Prompt("Load Tool", "Please insert a tool. It will be measured on the BitSetter.", "Resume", "Skip")).ConfigureAwait(false);
            if (r == 0) await MeasureToolCoreAsync().ConfigureAwait(false);
        }
    }, [MachineState.NotInitialized, MachineState.Idle, MachineState.Alarm]);

    // ---- BitSetter ------------------------------------------------------------------

    /// <summary>Operator-initiated tool change: prompts for the tool, then measures it.</summary>
    public Task LoadToolAsync() => RunCycle("load tool", async () =>
    {
        RequireHomed();
        await ToolChangeCoreAsync(0).ConfigureAwait(false);
    });

    async Task ToolChangeCoreAsync(int tool)
    {
        await Cmd("M5").ConfigureAwait(false);
        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        if (Settings.BitSetterEnabled)
            await Cmd($"G53 G0 X{F(Settings.BitSetterX)} Y{F(Settings.BitSetterY)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);

        var what = tool > 0 ? $"Please insert tool #{tool}" : "Please insert a tool";
        var note = Settings.ManualSpindle ? "\n\nMake sure the spindle is off first." : "";
        var tail = Settings.BitSetterEnabled ? "" : "\n\nWithout a BitSetter the Z zero must be set again if the tool length changed.";
        int r = await PromptHandler(new Prompt("Tool Change Required", what + note + tail, "Resume", "Stop")).ConfigureAwait(false);
        if (r != 0) throw new OperationCanceledException("Stopped at tool change.");
        if (Settings.BitSetterEnabled) await MeasureToolCoreAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Measures the fitted tool and shifts the Z work offset by the change in length
    /// since the reference tool, so the Z zero stays on the stock.
    /// </summary>
    async Task MeasureToolCoreAsync()
    {
        double z = await BitSetterProbeAsync().ConfigureAwait(false);
        if (Settings.ReferenceToolZ is { } reference)
        {
            double delta = z - reference;
            var st = await FreshStatusAsync().ConfigureAwait(false);
            await Cmd($"G10 L2 P1 Z{F(st.Wco.Z + delta)}").ConfigureAwait(false);
            Log?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Tool length changed by {delta:0.000} mm"));
        }
        Settings.ReferenceToolZ = z;
        Settings.Save();
        ToolMeasured = true;
    }

    /// <summary>Two-touch probe on the BitSetter. Returns the machine Z of the slow touch.</summary>
    async Task<double> BitSetterProbeAsync()
    {
        if (!Settings.BitSetterEnabled) throw new GrblException("The BitSetter is not enabled.");
        RequireHomed();
        double travelZ = Travel.Z;
        if (travelZ <= 0) throw new GrblException("Z travel ($132) is not configured.");

        await Cmd("M5").ConfigureAwait(false);
        await Cmd("G21 G90").ConfigureAwait(false);
        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        await Cmd($"G53 G0 X{F(Settings.BitSetterX)} Y{F(Settings.BitSetterY)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
        var st = await FreshStatusAsync().ConfigureAwait(false);
        if (st.Probe)
            throw new GrblException("BS001: The BitSetter probe is already detected as closed.\n\nThis can be caused by several things:\n- The BitSetter button is jammed down.\n- If you have a BitZero, is it touching the ground clip? (Is the red LED lit?)");

        var first = await ProbeMachineZAsync(-(travelZ - 1), FastProbeFeed, st.Wco).ConfigureAwait(false)
            ?? throw new GrblException("BS002: The BitSetter probe did not activate while probing.\n\nThis can be caused by several things:\n- The Z axis is jammed, and was not able to reach the BitSetter.\n- The BitSetter button is jammed.\n- The BitSetter is unplugged.\n- The BitSetter is damaged.\n\nPlease ensure the BitSetter LED turns red when pressed.");

        await Cmd($"G53 G0 Z{F(Math.Min(first + 5, SafeZ))}").ConfigureAwait(false);
        var second = await ProbeMachineZAsync(first - 2, SlowProbeFeed, st.Wco).ConfigureAwait(false)
            ?? throw new GrblException("BS003: The BitSetter probe did not activate while probing.\n\nThe second, slow touch did not find the BitSetter.");

        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
        return second;
    }

    /// <summary>Probes straight down to a machine Z. Returns the contact Z, or null if nothing was touched.</summary>
    async Task<double?> ProbeMachineZAsync(double machineZ, double feed, Vec3 wco)
    {
        _probe = null;
        _probing = true;
        try
        {
            // G38.2 cannot be combined with G53, so convert to work coordinates.
            await Cmd($"G38.2 Z{F(machineZ - wco.Z)} F{F(feed)}").ConfigureAwait(false);
        }
        catch (GrblException)
        {
            await RecoverFromProbeAlarmAsync().ConfigureAwait(false);
            _probing = false;
            return null;
        }
        if (_probe is not { Hit: true } p)
        {
            // GRBL raises ALARM:5 for a missed probe, sometimes after the ok.
            await Task.Delay(300).ConfigureAwait(false);
            if (Status.State == GrblState.Alarm) await RecoverFromProbeAlarmAsync().ConfigureAwait(false);
            _probing = false;
            return null;
        }
        _probing = false;
        return p.Pos.Z;
    }

    async Task RecoverFromProbeAlarmAsync()
    {
        await Task.Delay(200).ConfigureAwait(false);
        await Cmd("$X").ConfigureAwait(false);
        await Cmd("G21 G90").ConfigureAwait(false);
        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    // ---- zero / probing -------------------------------------------------------------

    public Task SetZeroAsync(bool x, bool y, bool z) => RunCycle("set zero", async () =>
    {
        var axes = (x ? " X0" : "") + (y ? " Y0" : "") + (z ? " Z0" : "");
        if (axes == "") return;
        await Cmd("G10 L20 P1" + axes).ConfigureAwait(false);
        if (z) await AfterZZeroAsync().ConfigureAwait(false);
    });

    /// <summary>With a BitSetter, the tool the Z zero was set with becomes the length reference.</summary>
    async Task AfterZZeroAsync()
    {
        if (!Settings.BitSetterEnabled || !Homed) return;
        if (!ToolMeasured)
        {
            Settings.ReferenceToolZ = null;
            await MeasureToolCoreAsync().ConfigureAwait(false);
        }
    }

    public Task ClearOffsetsAsync() => RunCycle("clear offsets", () => Cmd("G10 L2 P1 X0 Y0 Z0"));

    /// <summary>Probes down onto a touch plate and sets the Z zero from it.</summary>
    public Task ProbeZAsync() => RunCycle("probe Z", async () =>
    {
        var st = await FreshStatusAsync().ConfigureAwait(false);
        if (st.Probe)
            throw new GrblException("PRB002: The probe was closed prior to movement.\n\nPlease start the probing cycle with the spindle further from the probe.");
        await Cmd("G21 G90").ConfigureAwait(false);
        var z = await ProbeMachineZAsync(st.MachinePos.Z - PlateProbeTravel, PlateProbeFeed, st.Wco).ConfigureAwait(false)
            ?? throw new GrblException("PRB005: The machine did not detect the probe within the travel distance.");
        await SyncAsync().ConfigureAwait(false);
        await Cmd($"G10 L20 P1 Z{F(Settings.ProbeThickness)}").ConfigureAwait(false);
        await Cmd($"G53 G0 Z{F(Math.Min(z + 6, -1))}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
        await AfterZZeroAsync().ConfigureAwait(false);
    });

    // ---- jogging --------------------------------------------------------------------

    /// <summary>Starts a continuous jog toward the end of travel. Call <see cref="StopJog"/> on release.</summary>
    public void StartJog(int axis, int direction, double feed)
    {
        if (State != MachineState.Idle || _client == null) return;
        if (!Homed || Travel[axis] <= 0) return; // without a known position only stepped jogs are safe
        double target = direction > 0 ? -1 : -(Travel[axis] - 1);
        _ = Client.SendAsync($"$J=G53 G21 {"XYZ"[axis]}{F(target)} F{F(feed)}");
    }

    public void StopJog() => _client?.Realtime(0x85);

    public void JogStep(int axis, int direction, double distance, double feed)
    {
        if (State != MachineState.Idle || _client == null) return;
        double d = direction * distance;
        if (Homed && Travel[axis] > 0)
        {
            double target = Math.Clamp(Status.MachinePos[axis] + d, -(Travel[axis] - 1), -1);
            d = target - Status.MachinePos[axis];
            if (Math.Abs(d) < 0.0005) return;
        }
        _ = Client.SendAsync($"$J=G91 G21 {"XYZ"[axis]}{F(d)} F{F(feed)}");
    }

    public Task RapidToAsync(RapidPosition where) => RunCycle("rapid", async () =>
    {
        RequireHomed();
        var t = Travel;
        if (t.X <= 0 || t.Y <= 0) throw new GrblException("Machine travel is not configured.");
        int col = (int)where % 3, row = (int)where / 3; // row 0 is the back of the machine
        double x = -t.X + 10 + (t.X - 20) * col / 2.0;
        double y = -10 - (t.Y - 20) * row / 2.0;
        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        await Cmd($"G53 G0 X{F(x)} Y{F(y)}").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    });

    public Task RapidToWorkXYAsync() => RunCycle("rapid", async () =>
    {
        RequireHomed();
        await Cmd($"G53 G0 Z{F(SafeZ)}").ConfigureAwait(false);
        await Cmd("G90 G0 X0 Y0").ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    });

    /// <summary>Stores the current position as the BitSetter location.</summary>
    public void SetBitSetterHere()
    {
        Settings.BitSetterX = Math.Round(Status.MachinePos.X, 3);
        Settings.BitSetterY = Math.Round(Status.MachinePos.Y, 3);
        Settings.Save();
        Changed?.Invoke();
    }

    // ---- MDI, quick actions, config -------------------------------------------------

    public Task MdiAsync(string text) => RunCycle("MDI", async () =>
    {
        text = text.Trim();
        if (text.StartsWith('/')) text = text[1..];
        if (text.Length == 0) return;
        await Cmd(text).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    });

    public async Task RunActionAsync(QuickAction action)
    {
        if (action.Code.Any(c => c.Trim().Equals("$H", StringComparison.OrdinalIgnoreCase)))
        {
            await InitializeAsync().ConfigureAwait(false);
            return;
        }
        await RunCycle(action.Name, async () =>
        {
            RequireHomed();
            foreach (var line in action.Code) await Cmd(line).ConfigureAwait(false);
            await SyncAsync().ConfigureAwait(false);
            await Cmd("G21 G90").ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Writes the selected model's GRBL configuration to the controller.</summary>
    public Task SendConfigAsync() => RunCycle("send configuration", async () =>
    {
        var lines = Settings.Model.BuildConfig(Settings.ZType, Settings.Size);
        if (lines.Count == 0) throw new GrblException("The Generic GRBL profile leaves the controller settings unchanged.");
        if (Status.State == GrblState.Alarm) await Cmd("$X").ConfigureAwait(false);
        foreach (var l in lines) await Cmd(l).ConfigureAwait(false);
        await ReadGrblSettingsAsync().ConfigureAwait(false);
        Settings.ConfigSent = true;
        Settings.Save();
        Homed = false;
        SetState(HomingEnabled ? MachineState.NotInitialized : MachineState.Idle);
    }, [MachineState.Idle, MachineState.NotInitialized, MachineState.Alarm]);

    // ---- jobs -----------------------------------------------------------------------

    public void LoadJob(GcodeProgram? job)
    {
        Job = job;
        _jobDone = 0;
        Changed?.Invoke();
    }

    /// <summary>Compares the job's extents at the current zero with the machine's travel.</summary>
    public List<string> CheckBounds()
    {
        var msgs = new List<string>();
        if (Job is not { HasMotion: true } job || !Homed) return msgs;
        var t = Travel;
        if (t.X <= 0 || t.Y <= 0) return msgs;
        var lo = job.Min + Status.Wco;
        var hi = job.Max + Status.Wco;
        string mm(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        if (lo.X < -t.X) msgs.Add($"Project will hit the left of the machine (X-).\n(The program zero is {mm(-t.X - lo.X)}mm too far left.)");
        if (hi.X > 0) msgs.Add($"Project will hit the right of the machine (X+).\n(The program zero is {mm(hi.X)}mm too far right.)");
        if (lo.Y < -t.Y) msgs.Add($"Project will hit the front of the machine (Y-).\n(The program zero is {mm(-t.Y - lo.Y)}mm too far forward.)");
        if (hi.Y > 0) msgs.Add($"Project will hit the back of the machine (Y+).\n(The program zero is {mm(hi.Y)}mm too far back.)");
        return msgs;
    }

    public async Task RunJobAsync(int startLine = 0)
    {
        var job = Job ?? throw new GrblException("No file loaded.");
        if (State != MachineState.Idle) throw new GrblException($"Cannot start a job while the machine is {State}.");
        RequireHomed();
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) throw new GrblException("The machine is busy.");
        var cts = _jobCts = new CancellationTokenSource();
        _jobError = null;
        _jobDone = startLine;
        _spindleRpm = 0;
        JobStarted = DateTime.Now;
        LastError = "";
        SetState(MachineState.Running);
        try
        {
            await Cmd("G21 G90 G54").ConfigureAwait(false);
            for (int i = startLine; i < job.Lines.Count; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                if (_jobError != null) break;
                var line = job.Lines[i];
                if (line.Rpm > 0) _spindleRpm = line.Rpm;

                switch (line.Kind)
                {
                    case LineKind.ToolChange:
                        await DrainAsync(cts.Token).ConfigureAwait(false);
                        // The first tool is already fitted when the operator has just measured it.
                        if (!(i == FirstToolChange(job) && ToolMeasured && startLine == 0))
                        {
                            await ToolChangeCoreAsync(line.Tool).ConfigureAwait(false);
                            if (Settings.ManualSpindle && _spindleRpm > 0) await SpindlePromptAsync(_spindleRpm).ConfigureAwait(false);
                        }
                        Interlocked.Increment(ref _jobDone);
                        continue;
                    case LineKind.Pause:
                        await DrainAsync(cts.Token).ConfigureAwait(false);
                        if (await PromptHandler(new Prompt("Program Paused", line.Comment == "" ? "The program requested a pause (M0)." : line.Comment, "Resume", "Stop")).ConfigureAwait(false) != 0)
                            throw new OperationCanceledException("Stopped at pause.");
                        Interlocked.Increment(ref _jobDone);
                        continue;
                    case LineKind.SpindleOn when Settings.ManualSpindle:
                        await DrainAsync(cts.Token).ConfigureAwait(false);
                        await SpindlePromptAsync(_spindleRpm).ConfigureAwait(false);
                        break;
                }

                await Client.WaitForRoomAsync(24).ConfigureAwait(false);
                int source = line.SourceLine;
                string text = line.Text;
                _ = Client.SendAsync(text).ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully && t.Result.Ok) Interlocked.Increment(ref _jobDone);
                    else if (t.IsCompletedSuccessfully)
                        _jobError ??= $"Error on line {source}: {GrblText.ErrorText(t.Result.ErrorCode)}\n{text}";
                }, TaskScheduler.Default);
                if ((i & 15) == 0) Changed?.Invoke();
                if (line.Kind == LineKind.ProgramEnd) break;
            }
            await DrainAsync(cts.Token).ConfigureAwait(false);
            if (_jobError != null) throw new GrblException(_jobError);
            _jobDone = job.Lines.Count;
            if (Settings.ManualSpindle && _spindleRpm > 0)
                await PromptHandler(new Prompt("Job Complete", "Please turn the spindle off.", "OK")).ConfigureAwait(false);
            FinishJob(MachineState.Idle);
        }
        catch (Exception e)
        {
            bool stopped = e is OperationCanceledException;
            if (!stopped) LastError = e.Message;
            Log?.Invoke(stopped ? "Job stopped." : "ERROR: " + e.Message);
            if (_client != null && State is MachineState.Running or MachineState.Paused)
                await HaltAsync().ConfigureAwait(false);
            if (State is MachineState.Running or MachineState.Paused or MachineState.Busy) FinishJob(MachineState.Idle);
            else FinishJob(State);
            if (!stopped) throw;
        }
        finally
        {
            _jobCts = null;
            _gate.Release();
        }
    }

    static int FirstToolChange(GcodeProgram job)
    {
        for (int i = 0; i < job.Lines.Count; i++)
            if (job.Lines[i].Kind == LineKind.ToolChange) return i;
        return -1;
    }

    void FinishJob(MachineState next)
    {
        _lastJobTime = DateTime.Now - JobStarted;
        Settings.RunMinutes += _lastJobTime.TotalMinutes;
        Settings.Save();
        SetState(next);
    }

    Task SpindlePromptAsync(double rpm) =>
        PromptHandler(new Prompt("Spindle RPM Change Required",
            rpm > 0 ? $"Please turn the spindle on and set it to {rpm:0} RPM." : "Please turn the spindle on.", "Resume"));

    /// <summary>Waits for every streamed line to be acknowledged and for motion to stop.</summary>
    async Task DrainAsync(CancellationToken ct)
    {
        while (Client.Outstanding > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (_jobError != null) throw new GrblException(_jobError);
            await Task.Delay(20, ct).ConfigureAwait(false);
        }
        if (_jobError != null) throw new GrblException(_jobError);
        var done = Client.SendAsync("G4 P0.01");
        await done.WaitAsync(ct).ConfigureAwait(false);
        await Client.WaitForStatusAsync(s => s.State == GrblState.Idle, TimeSpan.FromHours(12), ct).ConfigureAwait(false);
    }

    public void Pause()
    {
        if (State != MachineState.Running) return;
        _client?.Realtime((byte)'!');
        SetState(MachineState.Paused);
    }

    public void Resume()
    {
        if (State != MachineState.Paused) return;
        _client?.Realtime((byte)'~');
        SetState(MachineState.Running);
    }

    public void FeedOverride(int step)
    {
        if (_client == null) return;
        _client.Realtime(step == 0 ? (byte)0x90 : step > 0 ? (byte)0x91 : (byte)0x92);
    }

    /// <summary>Stops the running job: feed hold, wait for standstill, then reset the controller.</summary>
    public void StopJob() => _jobCts?.Cancel();

    /// <summary>
    /// Brings motion to a controlled stop and flushes the controller. Holding first
    /// keeps the steppers in step, so the machine position survives the reset.
    /// </summary>
    async Task HaltAsync()
    {
        var client = Client;
        try
        {
            client.Realtime((byte)'!');
            try
            {
                await client.WaitForStatusAsync(s => s.State is GrblState.Idle or GrblState.Alarm || (s.State == GrblState.Hold && s.SubState == 0),
                    TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
            catch (TimeoutException) { /* reset regardless; an alarm will report lost position */ }
            _expectReset = true;
            var welcome = client.WaitForWelcomeAsync(TimeSpan.FromSeconds(5));
            client.SoftReset();
            await welcome.ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);
            if (State != MachineState.Alarm) await client.CommandAsync("G21 G90 G54").ConfigureAwait(false);
        }
        catch (Exception e) when (e is TimeoutException or GrblException)
        {
            Log?.Invoke("Stop did not complete cleanly: " + e.Message);
        }
        finally
        {
            _expectReset = false;
        }
    }

    /// <summary>Immediate stop from any state. Position is lost if the machine was moving.</summary>
    public void EmergencyReset()
    {
        if (_client == null) return;
        _jobCts?.Cancel();
        _client.SoftReset();
    }

    /// <summary>Clears an alarm lock without homing.</summary>
    public async Task UnlockAsync()
    {
        if (State != MachineState.Alarm) return;
        await Cmd("$X").ConfigureAwait(false);
        await Cmd("G21 G90 G54").ConfigureAwait(false);
        LastError = "";
        SetState(HomingEnabled && !Homed ? MachineState.NotInitialized : MachineState.Idle);
    }

    public void Dispose()
    {
        Disconnect();
        _gate.Dispose();
    }
}
