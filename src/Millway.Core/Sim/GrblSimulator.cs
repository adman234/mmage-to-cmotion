using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Millway.Core.Grbl;

namespace Millway.Core.Sim;

/// <summary>
/// An in-process stand-in for a GRBL 1.1 controller, used by the tests and by the
/// app's demo mode. It models the parts of GRBL the sender depends on: the receive
/// buffer and planner back-pressure, synchronous commands, homing lock, feed hold,
/// jogging, probing and work offsets. Motion is simulated in scaled real time.
/// </summary>
public sealed partial class GrblSimulator : ITransport
{
    const int PlannerSize = 15;
    const int RxSize = 128;

    enum Seg { Move, Jog }
    sealed record Segment(Vec3 Target, double Rate, Seg Kind);
    sealed class Op { public Func<double, bool> Step = _ => true; public Action Done = () => { }; }

    readonly object _lock = new();
    readonly Queue<string> _input = new();
    readonly Queue<Segment> _planner = new();
    readonly StringBuilder _line = new();
    readonly Dictionary<int, string> _settings = new();
    readonly System.Collections.Concurrent.BlockingCollection<byte[]> _outbox = [];
    Thread? _thread;
    volatile bool _stop;
    int _rxBytes;
    Op? _op;
    Vec3 _mpos, _wco;
    GrblState _state = GrblState.Alarm;
    bool _absolute = true, _inches, _hold, _spindleOn;
    int _motion;
    double _feed, _rpm;
    int _feedOv = 100;

    /// <summary>Simulated seconds per real second.</summary>
    public double TimeScale { get; set; } = 1;
    /// <summary>Machine Z at or below which the probe input reads closed. Null means nothing to touch.</summary>
    public double? ProbeTriggerZ { get; set; }
    /// <summary>Forces the probe input closed regardless of position.</summary>
    public bool ProbeStuck { get; set; }
    public bool RespondToReset { get; set; } = true;
    /// <summary>Every line received, in order.</summary>
    public List<string> Received { get; } = [];
    public bool RxOverflowed { get; private set; }
    public Vec3 MachinePos { get { lock (_lock) return _mpos; } }
    public Vec3 Wco { get { lock (_lock) return _wco; } }
    public bool SpindleOn => _spindleOn;
    public GrblState State => _state;
    public IReadOnlyDictionary<int, string> Settings => _settings;

    public string Name => "simulator";
    public event Action<byte[], int>? DataReceived;
#pragma warning disable CS0067
    public event Action<string>? Closed;
#pragma warning restore CS0067

    public GrblSimulator()
    {
        // A controller as Carbide Motion configures a Shapeoko 4 with a Z-Plus.
        foreach (var line in Machine.MachineModel.Shapeoko4.BuildConfig(Machine.ZAxisType.ZPlus))
        {
            var kv = line[1..].Split('=');
            _settings[int.Parse(kv[0])] = kv[1];
        }
    }

    /// <summary>Changes a stored setting directly, as if it had been written earlier.</summary>
    public void SetSetting(int number, string value)
    {
        lock (_lock) _settings[number] = value;
    }

    public void Open()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "grbl-sim" };
        _thread.Start();
        new Thread(Deliver) { IsBackground = true, Name = "grbl-sim-tx" }.Start();
    }

    public void Dispose()
    {
        _stop = true;
        _outbox.CompleteAdding();
        _thread?.Join(500);
    }

    public void Write(byte[] data, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                byte b = data[i];
                switch (b)
                {
                    case (byte)'?': Emit(StatusLine()); continue;
                    case (byte)'!': if (_state is GrblState.Run or GrblState.Jog) { _hold = true; _state = GrblState.Hold; } continue;
                    case (byte)'~': if (_hold) { _hold = false; _state = _planner.Count > 0 || _op != null ? GrblState.Run : GrblState.Idle; } continue;
                    case 0x18: Reset(); continue;
                    case 0x85: CancelJog(); continue;
                    case 0x90: _feedOv = 100; continue;
                    case 0x91: _feedOv = Math.Min(200, _feedOv + 10); continue;
                    case 0x92: _feedOv = Math.Max(10, _feedOv - 10); continue;
                }
                if (b >= 0x80) continue;
                _rxBytes++;
                if (_rxBytes > RxSize) RxOverflowed = true;
                if (b == '\n' || b == '\r')
                {
                    _input.Enqueue(_line.ToString());
                    _line.Clear();
                }
                else _line.Append((char)b);
            }
        }
    }

    void Emit(string line)
    {
        // Delivered in order from a separate thread, as a serial port would.
        if (!_outbox.IsAddingCompleted) _outbox.Add(Encoding.ASCII.GetBytes(line + "\r\n"));
    }

    void Deliver()
    {
        foreach (var bytes in _outbox.GetConsumingEnumerable())
            DataReceived?.Invoke(bytes, bytes.Length);
    }

    string StatusLine()
    {
        string state = _state switch
        {
            GrblState.Hold => "Hold:0",
            GrblState.Door => "Door:0",
            _ => _state.ToString(),
        };
        var pins = ProbeClosed() ? "|Pn:P" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"<{state}|MPos:{_mpos.X:0.000},{_mpos.Y:0.000},{_mpos.Z:0.000}|Bf:{PlannerSize - _planner.Count},{RxSize - _rxBytes}|FS:{(_state is GrblState.Run or GrblState.Jog ? _feed : 0):0},{(_spindleOn ? _rpm : 0):0}{pins}|WCO:{_wco.X:0.000},{_wco.Y:0.000},{_wco.Z:0.000}|Ov:{_feedOv},100,100>");
    }

    bool ProbeClosed() => ProbeStuck || (ProbeTriggerZ is { } z && _mpos.Z <= z + 1e-9);

    void Reset()
    {
        bool moving = _state is GrblState.Run or GrblState.Jog or GrblState.Home;
        _input.Clear();
        _planner.Clear();
        _line.Clear();
        _rxBytes = 0;
        _op = null;
        _hold = false;
        _spindleOn = false;
        _absolute = true;
        _inches = false;
        _motion = 0;
        _feedOv = 100;
        if (!RespondToReset) return;
        Emit("");
        Emit("Grbl 1.1f ['$' for help]");
        if (moving)
        {
            _state = GrblState.Alarm;
            Emit("ALARM:3");
        }
        else if (_state == GrblState.Alarm || _state == GrblState.Unknown)
        {
            _state = GrblState.Alarm;
            Emit("[MSG:'$H'|'$X' to unlock]");
        }
        else _state = GrblState.Idle;
    }

    void CancelJog()
    {
        if (_state != GrblState.Jog) return;
        _planner.Clear();
        _state = GrblState.Idle;
    }

    void Loop()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (!_stop)
        {
            Thread.Sleep(2);
            double now = clock.Elapsed.TotalSeconds;
            double dt = (now - last) * TimeScale;
            last = now;
            lock (_lock)
            {
                Advance(dt);
                while (_op == null && _input.Count > 0 && _planner.Count < PlannerSize)
                {
                    var line = _input.Peek();
                    if (!Execute(line)) break; // needs the planner to drain first
                    _input.Dequeue();
                    _rxBytes -= line.Length + 1;
                }
            }
        }
    }

    void Advance(double dt)
    {
        if (_hold) return;
        if (_op != null)
        {
            if (_op.Step(dt))
            {
                var op = _op;
                _op = null;
                op.Done();
            }
            return;
        }
        while (dt > 0 && _planner.Count > 0)
        {
            var seg = _planner.Peek();
            double rate = seg.Rate * (seg.Kind == Seg.Move ? _feedOv / 100.0 : 1);
            dt = MoveToward(seg.Target, rate, dt, out bool arrived);
            if (!arrived) break;
            _planner.Dequeue();
        }
        if (_planner.Count == 0 && _state is GrblState.Run or GrblState.Jog && _planner.Count == 0) _state = GrblState.Idle;
    }

    /// <summary>Moves toward the target for up to dt seconds; returns the unused time.</summary>
    double MoveToward(Vec3 target, double ratePerMin, double dt, out bool arrived)
    {
        var d = target - _mpos;
        double dist = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        double step = Math.Max(ratePerMin, 1) / 60.0 * dt;
        if (step >= dist)
        {
            _mpos = target;
            arrived = true;
            return dist == 0 ? dt : dt * (1 - dist / step);
        }
        double f = step / dist;
        _mpos = new Vec3(_mpos.X + d.X * f, _mpos.Y + d.Y * f, _mpos.Z + d.Z * f);
        arrived = false;
        return 0;
    }

    [GeneratedRegex(@"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))")]
    private static partial Regex Word();

    /// <summary>Runs one line. Returns false if it must wait for queued motion to finish.</summary>
    bool Execute(string raw)
    {
        var line = raw.Trim().ToUpperInvariant();
        if (line.Length == 0) { Received.Add(raw); Emit("ok"); return true; }

        bool sync = line[0] == '$' ? !line.StartsWith("$J=") : SyncPattern().IsMatch(line);
        if (sync && _planner.Count > 0) return false;
        Received.Add(raw.Trim());

        if (line[0] == '$') { Dollar(line); return true; }
        if (_state == GrblState.Alarm) { Emit("error:9"); return true; }

        var words = Word().Matches(line).Select(m => (L: m.Groups[1].Value[0], V: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
        if (words.Count == 0) { Emit("error:1"); return true; }

        double? x = null, y = null, z = null, p = null, l = null;
        bool g53 = false, probe = false, dwell = false, g10 = false, g92off = false;
        foreach (var (c, v) in words)
        {
            switch (c)
            {
                case 'G':
                    switch (v)
                    {
                        case 0: case 1: case 2: case 3: _motion = (int)v; break;
                        case 4: dwell = true; break;
                        case 10: g10 = true; break;
                        case 20: _inches = true; break;
                        case 21: _inches = false; break;
                        case 90: _absolute = true; break;
                        case 91: _absolute = false; break;
                        case 53: g53 = true; break;
                        case 38.2: probe = true; break;
                        case 92.1: g92off = true; break;
                        case 17: case 49: case 54: case 94: case 80: case 43.1: break;
                        default: Emit("error:20"); return true;
                    }
                    break;
                case 'M':
                    switch ((int)v)
                    {
                        case 3: case 4: _spindleOn = true; break;
                        case 5: _spindleOn = false; break;
                        case 0: case 1: case 2: case 30: case 8: case 9: case 56: break;
                        default: Emit("error:20"); return true;
                    }
                    break;
                case 'X': x = v; break;
                case 'Y': y = v; break;
                case 'Z': z = v; break;
                case 'P': p = v; break;
                case 'L': l = v; break;
                case 'F': _feed = _inches ? v * 25.4 : v; break;
                case 'S': _rpm = v; break;
                case 'N': case 'T': case 'I': case 'J': case 'K': case 'R': break;
                default: Emit("error:20"); return true;
            }
        }
        _ = g92off;

        if (dwell)
        {
            double remaining = p ?? 0;
            _op = new Op { Step = dt => (remaining -= dt) <= 0, Done = () => Emit("ok") };
            return true;
        }
        if (g10)
        {
            double u = _inches ? 25.4 : 1;
            if (l == 2)
                _wco = new Vec3(x is { } a ? a * u : _wco.X, y is { } b ? b * u : _wco.Y, z is { } c ? c * u : _wco.Z);
            else if (l == 20)
                _wco = new Vec3(x is { } a ? _mpos.X - a * u : _wco.X, y is { } b ? _mpos.Y - b * u : _wco.Y, z is { } c ? _mpos.Z - c * u : _wco.Z);
            else { Emit("error:20"); return true; }
            Emit("ok");
            return true;
        }
        if ((x ?? y ?? z) == null) { Emit("ok"); return true; }

        var target = Target(x, y, z, g53);
        if (probe)
        {
            if (ProbeClosed())
            {
                _state = GrblState.Alarm;
                Emit("ALARM:4");
                Emit("ok");
                return true;
            }
            _state = GrblState.Run;
            _op = new Op
            {
                Step = dt =>
                {
                    MoveToward(target, _feed, dt, out bool arrived);
                    // Stop exactly at the contact height however coarse the time step was.
                    if (ProbeTriggerZ is { } trig && _mpos.Z < trig) _mpos = _mpos with { Z = trig };
                    return arrived || ProbeClosed();
                },
                Done = () =>
                {
                    bool hit = ProbeClosed();
                    Emit(string.Create(CultureInfo.InvariantCulture, $"[PRB:{_mpos.X:0.000},{_mpos.Y:0.000},{_mpos.Z:0.000}:{(hit ? 1 : 0)}]"));
                    if (hit) _state = GrblState.Idle;
                    else { _state = GrblState.Alarm; Emit("ALARM:5"); }
                    Emit("ok");
                },
            };
            return true;
        }
        if (_motion == 1 && _feed <= 0) { Emit("error:22"); return true; }
        double rate = _motion == 0 ? Num(_settings[110]) : _feed;
        _planner.Enqueue(new Segment(target, rate, Seg.Move));
        _state = GrblState.Run;
        Emit("ok");
        return true;
    }

    [GeneratedRegex(@"G4(?!\d)|G10|G38|M0*[0126](?!\d)|M30")]
    private static partial Regex SyncPattern();

    Vec3 Target(double? x, double? y, double? z, bool machine)
    {
        double u = _inches ? 25.4 : 1;
        // The planner's end position is where a relative or partial move starts from.
        var from = _planner.Count > 0 ? _planner.Last().Target : _mpos;
        if (machine)
            return new Vec3(x is { } a ? a * u : from.X, y is { } b ? b * u : from.Y, z is { } c ? c * u : from.Z);
        if (_absolute)
            return new Vec3(x is { } a ? a * u + _wco.X : from.X, y is { } b ? b * u + _wco.Y : from.Y, z is { } c ? c * u + _wco.Z : from.Z);
        return new Vec3(from.X + (x ?? 0) * u, from.Y + (y ?? 0) * u, from.Z + (z ?? 0) * u);
    }

    static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    void Dollar(string line)
    {
        switch (line)
        {
            case "$$":
                foreach (var kv in _settings.OrderBy(k => k.Key)) Emit($"${kv.Key}={kv.Value}");
                Emit("ok");
                return;
            case "$#":
                Emit($"[G54:{_wco}]");
                foreach (var n in new[] { "G55", "G56", "G57", "G58", "G59", "G28", "G30", "G92" }) Emit($"[{n}:0.000,0.000,0.000]");
                Emit("[TLO:0.000]");
                Emit("[PRB:0.000,0.000,0.000:0]");
                Emit("ok");
                return;
            case "$I":
                Emit("[VER:1.1f.20170801:]");
                Emit("[OPT:V,15,128]");
                Emit("ok");
                return;
            case "$G":
                Emit("[GC:G0 G54 G17 G21 G90 G94 M5 M9 T0 F0 S0]");
                Emit("ok");
                return;
            case "$X":
                if (_state == GrblState.Alarm) { _state = GrblState.Idle; Emit("[MSG:Caution: Unlocked]"); }
                Emit("ok");
                return;
            case "$H":
                {
                    if (_settings[22] != "1") { Emit("error:5"); return; }
                    double remaining = 3;
                    _state = GrblState.Home;
                    _op = new Op
                    {
                        Step = dt => (remaining -= dt) <= 0,
                        Done = () =>
                        {
                            double pull = Num(_settings[27]);
                            _mpos = new Vec3(-pull, -pull, -pull);
                            _state = GrblState.Idle;
                            Emit("ok");
                        },
                    };
                    return;
                }
        }
        if (line.StartsWith("$J="))
        {
            if (_state is not (GrblState.Idle or GrblState.Jog)) { Emit("error:9"); return; }
            var words = Word().Matches(line[3..]).Select(m => (L: m.Groups[1].Value[0], V: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
            double? x = null, y = null, z = null;
            double f = 0;
            bool rel = false, g53 = false, inch = false;
            foreach (var (w, v) in words)
            {
                if (w == 'G' && v == 91) rel = true;
                else if (w == 'G' && v == 53) g53 = true;
                else if (w == 'G' && v == 20) inch = true;
                else if (w == 'X') x = v;
                else if (w == 'Y') y = v;
                else if (w == 'Z') z = v;
                else if (w == 'F') f = v;
            }
            if (f <= 0 || (x ?? y ?? z) == null) { Emit("error:16"); return; }
            double u = inch ? 25.4 : 1;
            var from = _planner.Count > 0 ? _planner.Last().Target : _mpos;
            Vec3 target = rel
                ? new Vec3(from.X + (x ?? 0) * u, from.Y + (y ?? 0) * u, from.Z + (z ?? 0) * u)
                : g53
                    ? new Vec3(x is { } a ? a * u : from.X, y is { } b ? b * u : from.Y, z is { } c ? c * u : from.Z)
                    : new Vec3(x is { } a2 ? a2 * u + _wco.X : from.X, y is { } b2 ? b2 * u + _wco.Y : from.Y, z is { } c2 ? c2 * u + _wco.Z : from.Z);
            _planner.Enqueue(new Segment(target, f * u, Seg.Jog));
            _state = GrblState.Jog;
            Emit("ok");
            return;
        }
        var m = GrblText.Setting().Match(line);
        if (m.Success)
        {
            if (_state is not (GrblState.Idle or GrblState.Alarm)) { Emit("error:8"); return; }
            _settings[int.Parse(m.Groups[1].Value)] = m.Groups[2].Value;
            Emit("ok");
            return;
        }
        Emit("error:3");
    }
}
