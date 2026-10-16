using Millway.Core;
using Millway.Core.Gcode;
using Millway.Core.Grbl;
using Millway.Core.Machine;
using Millway.Core.Sim;

namespace Millway.Tests;

public class ProtocolTests
{
    [Fact]
    public void ParsesFullStatusReport()
    {
        var s = GrblStatus.Parse("<Hold:1|MPos:-10.500,-20.000,-3.250|Bf:14,120|FS:1200,18000|Pn:PXD|WCO:1.000,2.000,3.000|Ov:110,50,90>", null)!;
        Assert.Equal(GrblState.Hold, s.State);
        Assert.Equal(1, s.SubState);
        Assert.Equal(new Vec3(-10.5, -20, -3.25), s.MachinePos);
        Assert.Equal(new Vec3(-11.5, -22, -6.25), s.WorkPos);
        Assert.Equal(1200, s.Feed);
        Assert.True(s.Probe);
        Assert.True(s.Door);
        Assert.True(s.AnyLimit);
        Assert.Equal(110, s.FeedOverride);
    }

    [Fact]
    public void StatusKeepsOffsetsBetweenReports()
    {
        var a = GrblStatus.Parse("<Idle|MPos:0,0,0|WCO:5,6,7>", null);
        var b = GrblStatus.Parse("<Run|MPos:1,1,1|FS:100,0>", a)!;
        Assert.Equal(new Vec3(5, 6, 7), b.Wco);
        Assert.False(b.Probe);
    }

    [Fact]
    public void StatusAcceptsWorkPosition()
    {
        var s = GrblStatus.Parse("<Idle|WPos:1,2,3|WCO:10,10,10>", null)!;
        Assert.Equal(new Vec3(11, 12, 13), s.MachinePos);
    }
}

public class ProgramTests
{
    [Fact]
    public void ComputesExtentsToolsAndStripsComments()
    {
        var p = GcodeProgram.Parse("t.nc",
        [
            "%", "(TOOL #102: 1/8 End)", "G21 G90", "M6 T102", "M3 S18000 ; spin", "G0 X0 Y0 Z5",
            "G1 Z-1 F100", "G1 X50 Y25 F600", "G2 X60 Y25 I5 J0", "G0 Z5", "M5", "M30",
        ]);
        Assert.Equal(new Vec3(0, 0, -1), p.Min);
        Assert.Equal(60, p.Max.X, 3);
        Assert.Equal(30, p.Max.Y, 2); // the arc bulges 5 mm past Y25
        Assert.Single(p.Tools);
        Assert.Equal(102, p.Tools[0].Number);
        Assert.Equal("1/8 End", p.Tools[0].Description);
        Assert.DoesNotContain(p.Lines, l => l.Text.Contains('(') || l.Text.Contains(';'));
        Assert.Equal(LineKind.ToolChange, p.Lines[1].Kind);
        Assert.Equal(LineKind.ProgramEnd, p.Lines[^1].Kind);
        Assert.True(p.Estimate > TimeSpan.Zero);
    }

    [Fact]
    public void ConvertsInchesAndIncrementalMoves()
    {
        var p = GcodeProgram.Parse("t.nc", ["G20 G91", "G1 X1 F10", "G1 Y2", "G1 X-0.5"]);
        Assert.Equal(25.4, p.Max.X, 3);
        Assert.Equal(50.8, p.Max.Y, 3);
    }

    [Fact]
    public void RemovesToolLengthWordsGrblRejects()
    {
        var p = GcodeProgram.Parse("t.nc", ["G43 H1", "G43 H1 Z10", "G1 X1 F100"]);
        Assert.Equal(2, p.Lines.Count);
        Assert.Equal("Z10", p.Lines[0].Text);
    }

    [Fact]
    public void ArcsInTheXzPlaneExtendZ()
    {
        // A half circle in G18 from X0 to X20 bulges 10 mm in Z.
        var p = GcodeProgram.Parse("t.nc", ["G18 G0 X0 Y0 Z0", "G2 X20 Z0 I10 K0 F100"]);
        Assert.Equal(20, p.Max.X, 3);
        Assert.Equal(10, Math.Max(Math.Abs(p.Min.Z), Math.Abs(p.Max.Z)), 2);
        Assert.Equal(0, p.Max.Y, 6);
    }

    [Fact]
    public void RejectsEmptyFiles() =>
        Assert.Throws<InvalidDataException>(() => GcodeProgram.Parse("t.nc", ["(nothing)", ""]));
}

public class SerialTests
{
    /// <summary>
    /// Exercises the real serial transport on Linux through a pseudo-terminal pair.
    /// The workflow creates the pair with socat and names both ends in MILLWAY_PTY.
    /// </summary>
    [Fact]
    public async Task SerialTransportTalksThroughAPseudoTerminal()
    {
        var spec = Environment.GetEnvironmentVariable("MILLWAY_PTY");
        if (string.IsNullOrEmpty(spec)) return; // only meaningful where the pair exists
        var ends = spec.Split(',');
        using var far = new FileStream(ends[1], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, false);
        using var t = new SerialTransport(ends[0]);
        using var client = new GrblClient(t);
        client.Open();

        var sent = client.SendAsync("G0 X1");
        var buf = new byte[64];
        int n = 0;
        while (n < 6) n += await far.ReadAsync(buf.AsMemory(n)).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("G0 X1\n", System.Text.Encoding.ASCII.GetString(buf, 0, n));

        far.Write("ok\r\n<Idle|MPos:1.000,0.000,0.000|FS:0,0>\r\n"u8);
        far.Flush();
        Assert.True((await sent.WaitAsync(TimeSpan.FromSeconds(5))).Ok);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (client.Status == null && DateTime.UtcNow < end) await Task.Delay(20);
        Assert.Equal(1, client.Status!.MachinePos.X);
    }
}

public class DefinitionTests
{
    // Carbide Motion build 667, Shapeoko 4 / Pro table with a Z-Plus, in the order it writes them.
    static readonly string[] VendorShapeoko4ZPlus =
    [
        "$0=10", "$1=255", "$2=0", "$3=5", "$102=200", "$112=1000", "$122=270", "$132=95",
        "$4=0", "$5=0", "$6=0", "$10=255", "$11=.02", "$12=.01", "$13=0", "$20=0", "$21=0", "$22=1", "$23=0",
        "$24=100", "$25=2000", "$26=25", "$27=3", "$30=24000", "$31=0", "$32=0", "$100=40", "$101=40",
        "$110=10000", "$111=10000", "$120=500", "$121=500", "$130=845", "$131=850",
    ];

    [Fact]
    public void Shapeoko4MatchesVendorTable()
    {
        Assert.Equal(VendorShapeoko4ZPlus, MachineModel.Shapeoko4.BuildConfig(ZAxisType.ZPlus));
        Assert.Equal(VendorShapeoko4ZPlus, MachineModel.ShapeokoPro.BuildConfig(ZAxisType.ZPlus));
        var hdz = MachineModel.Shapeoko4.BuildConfig(ZAxisType.Hdz);
        Assert.Contains("$3=5", hdz);
        Assert.Contains("$102=320", hdz);
        Assert.Contains("$132=150", hdz);
    }

    [Theory]
    [InlineData(ZAxisType.BeltDrive, "$3=6", "$102=40")]
    [InlineData(ZAxisType.ZPlus, "$3=2", "$102=200")]
    [InlineData(ZAxisType.Hdz, "$3=2", "$102=320")]
    public void Shapeoko3UsesItsOwnDirectionInvert(ZAxisType z, string dir, string steps)
    {
        var cfg = MachineModel.Shapeoko3.BuildConfig(z);
        Assert.Contains(dir, cfg);
        Assert.Contains(steps, cfg);
        Assert.Equal(cfg.Count, cfg.Select(c => c[..c.IndexOf('=')]).Distinct().Count());
    }

    [Fact]
    public void Shapeoko5ProTable()
    {
        var cfg = MachineModel.Shapeoko5Pro.BuildConfig(ZAxisType.ZPlus);
        Assert.Contains("$5=15", cfg);
        Assert.Contains("$100=80.000", cfg);
        Assert.Contains("$132=155.000", cfg);
        Assert.Equal(cfg.Count, cfg.Select(c => c[..c.IndexOf('=')]).Distinct().Count());
    }

    [Fact]
    public void GenericLeavesControllerAlone() =>
        Assert.Empty(MachineModel.Generic.BuildConfig(ZAxisType.ZPlus));

    [Fact]
    public void Shapeoko4XlGeometryFollowsTheVendor()
    {
        var m = MachineModel.Shapeoko4;
        var t = m.Travel(m.Sizes.Single(s => s.Name == "XL"));
        Assert.Equal(new Vec3(838.2, 444.5, 101.6), t);
        Assert.Equal((-100.0, -414.5), m.DefaultBitSetter(t));
        Assert.Equal((-5.0, -424.5), m.ToolChangePosition(t));
        Assert.Equal((-828.2, -11.0), MachineModel.RapidPosition(t, -1, 1));
        Assert.Equal((-11.0, -374.5), MachineModel.RapidPosition(t, 1, -1));
        var centre = MachineModel.RapidPosition(t, 0, 0);
        Assert.Equal(-419.6, centre.X, 6);
        Assert.Equal(-152.75, centre.Y, 6);
    }

    [Fact]
    public void LinuxDiscoveryReadsUsbIds()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-" + Guid.NewGuid().ToString("N"));
        // sysfs exposes the tty with a "device" link into the USB device tree, where the
        // IDs sit a level or two up. A plain directory gives the lookup the same shape.
        var tty = Path.Combine(root, "class", "ttyACM0");
        Directory.CreateDirectory(Path.Combine(tty, "device"));
        File.WriteAllText(Path.Combine(tty, "idVendor"), "16d0\n");
        File.WriteAllText(Path.Combine(tty, "idProduct"), "0ad8\n");
        try
        {
            var ports = PortDiscovery.ListLinux(Path.Combine(root, "class"), "/dev");
            var p = Assert.Single(ports);
            Assert.Equal("/dev/ttyACM0", p.Name.Replace('\\', '/'));
            Assert.True(p.IsCarbide);
        }
        finally { Directory.Delete(root, true); }
    }
}

public class SettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mw-set-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void KeepsABitSetterLocationSavedByVersion01()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, """{"ModelId":"shapeoko4","SizeName":"XL","BitSetterEnabled":true,"BitSetterX":-98.5,"BitSetterY":-420.25}""");
        var s = AppSettings.Load(path);
        Assert.True(s.ResetBitSetterIfMachineChanged());
        Assert.True(s.BitSetterLocated);
        Assert.Equal(-98.5, s.BitSetterX);
        Assert.Equal(-420.25, s.BitSetterY);
    }

    [Fact]
    public void ChangingSizeResetsTheBitSetterToTheDefault()
    {
        var s = new AppSettings { ModelId = "shapeoko4", SizeName = "XL" };
        s.ResetBitSetterIfMachineChanged();
        s.BitSetterX = -90;
        s.BitSetterLocated = true;
        s.SizeName = "XXL";
        Assert.True(s.ResetBitSetterIfMachineChanged());
        Assert.False(s.BitSetterLocated);
        Assert.Equal((-100.0, -808.2), (s.BitSetterX, s.BitSetterY));
    }
}

public class MachineTests : IDisposable
{
    readonly GrblSimulator _sim = new() { TimeScale = 60 };
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mw-mt-" + Guid.NewGuid().ToString("N"));
    readonly AppSettings _settings;
    readonly MachineController _mc;
    readonly List<Prompt> _prompts = [];

    public MachineTests()
    {
        Directory.CreateDirectory(_dir);
        _settings = AppSettings.Load(Path.Combine(_dir, "settings.json"));
        _settings.ModelId = "shapeoko4";
        _settings.SizeName = "XL";
        _settings.ZType = ZAxisType.ZPlus;
        _settings.ManualSpindle = false;
        _mc = new MachineController(_settings);
        _mc.PromptHandler = p => { lock (_prompts) _prompts.Add(p); return Task.FromResult(0); };
    }

    public void Dispose()
    {
        _mc.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    void UseBitSetter(double x = -20, double y = -30)
    {
        _settings.BitSetterEnabled = true;
        _settings.BitSetterX = x;
        _settings.BitSetterY = y;
        _settings.BitSetterLocated = true;
        _settings.BitSetterMachine = _settings.MachineKey;
    }

    async Task ReadyAsync()
    {
        await _mc.ConnectAsync(_sim);
        Assert.Equal(MachineState.NotInitialized, _mc.State);
        await _mc.InitializeAsync();
        Assert.Equal(MachineState.Idle, _mc.State);
    }

    static async Task Until(Func<bool> cond, int ms = 5000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ConnectsReadsSettingsAndHomes()
    {
        await ReadyAsync();
        Assert.Equal("1.1f", _mc.GrblVersion);
        Assert.Equal(new Vec3(838.2, 444.5, 101.6), _mc.Travel); // the bed size, not $130-$132
        Assert.True(_mc.Homed);
        Assert.Contains("$H", _sim.Received);
        Assert.Contains("$#", _sim.Received);
        Assert.Equal(-3, _sim.MachinePos.Z, 3); // $27 pull-off
        Assert.Empty(_mc.ConfigDifferences);
    }

    [Fact]
    public async Task ReportsSettingsThatDifferFromTheVendor()
    {
        _sim.SetSetting(3, "2"); // what version 0.1 wrote to a Shapeoko 4
        await _mc.ConnectAsync(_sim);
        var d = Assert.Single(_mc.ConfigDifferences);
        Assert.Equal(3, d.Number);
        Assert.Equal(2, d.Actual);
        Assert.Equal(5, d.Expected);
    }

    [Fact]
    public async Task SendingConfigurationBacksUpAndCanBeRestored()
    {
        _sim.SetSetting(3, "2");
        _sim.SetSetting(110, "7000");
        await _mc.ConnectAsync(_sim);
        var backup = await _mc.SendConfigAsync();
        Assert.Equal("5", _sim.Settings[3]);
        Assert.Equal("10000", _sim.Settings[110]);
        Assert.Contains("$3=2", File.ReadAllLines(backup));
        Assert.Contains("$110=7000", File.ReadAllLines(backup));
        Assert.Empty(_mc.ConfigDifferences);

        await _mc.RestoreBackupAsync(backup);
        Assert.Equal("2", _sim.Settings[3]);
        Assert.Equal("7000", _sim.Settings[110]);
        Assert.Equal(2, _mc.ConfigDifferences.Count);
    }

    [Fact]
    public async Task ReportsSilentController()
    {
        _sim.RespondToReset = false;
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.ConnectAsync(_sim));
        Assert.Contains("did not respond", e.Message);
        Assert.Equal(MachineState.Disconnected, _mc.State);
    }

    [Fact]
    public async Task RunsJobWithoutOverflowingReceiveBuffer()
    {
        await ReadyAsync();
        await _mc.SetZeroAsync(true, true, true);
        var src = new List<string> { "G21 G90", "G1 Z-1 F3000" };
        for (int i = 0; i < 400; i++) src.Add($"G1 X-{i % 40} Y-{i % 25} F6000");
        src.Add("M30");
        _mc.LoadJob(GcodeProgram.Parse("big.nc", src));
        await _mc.RunJobAsync();
        Assert.Equal(MachineState.Idle, _mc.State);
        Assert.False(_sim.RxOverflowed);
        Assert.Equal(_mc.Job!.Lines.Count, _mc.JobLinesDone);
    }

    [Fact]
    public async Task StopsJobAndKeepsPosition()
    {
        await ReadyAsync();
        _sim.TimeScale = 2;
        _mc.LoadJob(GcodeProgram.Parse("slow.nc", ["G91", "G1 X-150 F600", "G1 X150", "M30"]));
        var run = _mc.RunJobAsync();
        await Until(() => _sim.MachinePos.X < -5);
        _mc.StopJob();
        await run;
        Assert.Equal(MachineState.Idle, _mc.State);
        Assert.True(_mc.Homed);
        Assert.NotEqual(GrblState.Alarm, _sim.State);
        Assert.InRange(_sim.MachinePos.X, -153, -3.01);
    }

    [Fact]
    public async Task PauseAndResume()
    {
        await ReadyAsync();
        _sim.TimeScale = 5;
        _mc.LoadJob(GcodeProgram.Parse("p.nc", ["G91", "G1 X-100 F600", "M30"]));
        var run = _mc.RunJobAsync();
        await Until(() => _sim.State == GrblState.Run);
        _mc.Pause();
        await Until(() => _sim.State == GrblState.Hold);
        Assert.Equal(MachineState.Paused, _mc.State);
        _sim.TimeScale = 200;
        _mc.Resume();
        await run;
        Assert.Equal(-103, _sim.MachinePos.X, 2);
    }

    [Fact]
    public async Task GrblErrorStopsTheJob()
    {
        await ReadyAsync();
        _mc.LoadJob(GcodeProgram.Parse("bad.nc", ["G1 X-5 F500", "G77 X1", "G1 X-50", "M30"]));
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.RunJobAsync());
        Assert.Contains("line 2", e.Message);
        Assert.Equal(MachineState.Idle, _mc.State);
    }

    [Fact]
    public async Task BitSetterKeepsZeroAcrossToolChange()
    {
        UseBitSetter();
        _sim.ProbeTriggerZ = -80;
        await ReadyAsync();                      // measures the first tool
        Assert.True(_mc.ToolMeasured);
        Assert.Equal(-80, _settings.ReferenceToolZ!.Value, 2);
        Assert.Equal(-20, _sim.MachinePos.X, 3);
        // Lowered to Z-15, then probed fast, backed off 5 mm and touched again slowly.
        Assert.Contains("G53 G0 Z-15", _sim.Received);
        Assert.Contains(_sim.Received, l => l.StartsWith("G38.2 Z-165") && l.EndsWith("F800"));
        Assert.Contains(_sim.Received, l => l.StartsWith("G38.2 Z-82") && l.EndsWith("F200"));

        await _mc.MdiAsync("G53 G0 Z-50");
        await _mc.SetZeroAsync(false, false, true);
        Assert.Equal(-50, _sim.Wco.Z, 3);

        _sim.ProbeTriggerZ = -70;                // the next tool is 10 mm longer
        await _mc.LoadToolAsync();
        Assert.Equal(-40, _sim.Wco.Z, 2);
        Assert.Equal(-70, _settings.ReferenceToolZ!.Value, 2);
        Assert.Contains(_prompts, p => p.Title == "Tool Change Required");
    }

    [Fact]
    public async Task BitSetterReachesALowTouchLikeTheVendor()
    {
        // Version 0.1 stopped probing at the Z travel; a short tool needs the full 150 mm.
        UseBitSetter();
        _sim.ProbeTriggerZ = -99;
        await ReadyAsync();
        Assert.Equal(-99, _settings.ReferenceToolZ!.Value, 2);
    }

    [Fact]
    public async Task BitSetterNeedsAConfirmedLocation()
    {
        _settings.BitSetterEnabled = true;
        _settings.BitSetterMachine = _settings.MachineKey;
        await ReadyAsync(); // initialize skips the measurement
        Assert.False(_mc.ToolMeasured);
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.LoadToolAsync());
        Assert.Contains("location has not been set", e.Message);
    }

    [Fact]
    public async Task BitSetterLocationIsFoundFromTheDefaultAndSaved()
    {
        _settings.BitSetterEnabled = true;
        await ReadyAsync();
        Assert.Equal((-100.0, -414.5), (_settings.BitSetterX, _settings.BitSetterY));
        await _mc.MoveToBitSetterAsync(useDefault: true);
        Assert.Equal(-100, _sim.MachinePos.X, 3);
        Assert.Equal(-414.5, _sim.MachinePos.Y, 3);
        await _mc.MdiAsync("G53 G0 X-102.5 Y-418");
        _mc.SaveBitSetterHere();
        Assert.True(_settings.BitSetterLocated);
        var saved = AppSettings.Load(_settings.Path);
        Assert.Equal((-102.5, -418.0), (saved.BitSetterX, saved.BitSetterY));
    }

    [Fact]
    public async Task BitSetterReportsStuckProbe()
    {
        UseBitSetter();
        _sim.ProbeStuck = true;
        await _mc.ConnectAsync(_sim);
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.InitializeAsync());
        Assert.StartsWith("BS001", e.Message);
    }

    [Fact]
    public async Task BitSetterReportsMissingProbe()
    {
        UseBitSetter();
        _sim.ProbeTriggerZ = null;
        await _mc.ConnectAsync(_sim);
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.InitializeAsync());
        Assert.StartsWith("BS002", e.Message);
        Assert.NotEqual(GrblState.Alarm, _sim.State);
        Assert.Equal(-5, _sim.MachinePos.Z, 2);
    }

    [Fact]
    public async Task WaitsForTheSpindleBeforeMeasuring()
    {
        UseBitSetter();
        _sim.ProbeTriggerZ = -80;
        await ReadyAsync();
        _mc.LoadJob(GcodeProgram.Parse("s.nc", ["M6 T1", "M3 S10000", "G1 X-10 F5000", "M6 T2", "G1 X-20", "M5", "M30"]));
        await _mc.RunJobAsync();
        // The spindle stopped at the tool change, so the BitSetter waits up to 4 s first.
        Assert.Contains(_sim.Received, l => l.StartsWith("G4 P3.") || l.StartsWith("G4 P4"));
    }

    [Fact]
    public async Task ProbeZSetsZeroAtPlateHeight()
    {
        _settings.ProbeThickness = 13;
        await ReadyAsync();
        await _mc.MdiAsync("G53 G0 Z-30");
        _sim.ProbeTriggerZ = -36;
        await _mc.ProbeZAsync();
        Assert.Equal(-49, _sim.Wco.Z, 2);
    }

    [Fact]
    public async Task ContinuousJogReachesTheEndOfTravel()
    {
        await ReadyAsync();
        _mc.StartJog(1, -1, 60000);
        var end = DateTime.UtcNow.AddSeconds(5);
        while (_sim.MachinePos.Y > -444 && DateTime.UtcNow < end) { _mc.KeepJogAlive(); await Task.Delay(20); }
        _mc.StopJog();
        Assert.Equal(-444.5, _sim.MachinePos.Y, 2); // full travel, as Carbide Motion allows
    }

    [Fact]
    public async Task JogStopsWhenTheButtonIsNoLongerHeld()
    {
        await ReadyAsync();
        _sim.TimeScale = 1;
        _mc.StartJog(0, -1, 3000);   // 50 mm/s, and nobody keeps it alive
        await Task.Delay(1500);
        Assert.Equal(GrblState.Idle, _sim.State);
        Assert.True(_sim.MachinePos.X > -60, $"jog ran to {_sim.MachinePos.X}");
    }

    [Fact]
    public async Task StepJogStopsAtTheEndOfTravel()
    {
        await ReadyAsync();
        await Until(() => Math.Abs(_mc.Status.MachinePos.Z + 3) < 0.01);
        _mc.JogStep(2, 1, 10, 1000); // already 3 mm below the switch; only 2 mm remain
        await Until(() => _sim.State == GrblState.Idle && _sim.MachinePos.Z > -1.5);
        Assert.Equal(-1, _sim.MachinePos.Z, 3);
    }

    [Fact]
    public async Task RapidPositionsKeepClearOfTheFront()
    {
        await ReadyAsync();
        await _mc.RapidToAsync(RapidPosition.SouthWest);
        Assert.Equal(new Vec3(-828.2, -374.5, -5), _sim.MachinePos);
    }

    [Fact]
    public async Task ToolChangeWithoutBitSetterMovesToTheFrontRight()
    {
        await ReadyAsync();
        await _mc.LoadToolAsync();
        Assert.Equal(-5, _sim.MachinePos.X, 3);
        Assert.Equal(-424.5, _sim.MachinePos.Y, 3);
    }

    [Fact]
    public async Task JobToolChangePromptsAndMeasures()
    {
        UseBitSetter();
        _sim.ProbeTriggerZ = -80;
        await ReadyAsync();
        _mc.LoadJob(GcodeProgram.Parse("tc.nc", ["M6 T1", "G1 X-10 F5000", "M6 T2", "G1 X-20", "M30"]));
        await _mc.RunJobAsync();
        // The first tool was measured at initialize, so only T2 prompts.
        Assert.Single(_prompts, p => p.Title == "Tool Change Required");
        Assert.Contains("#2", _prompts.Single(p => p.Title == "Tool Change Required").Message);
        Assert.DoesNotContain(_sim.Received, l => l.Contains("M6"));
    }

    [Fact]
    public async Task BoundsCheckFlagsProgramOutsideTravel()
    {
        await ReadyAsync();
        await _mc.SetZeroAsync(true, true, false);  // zero at the home corner (back right)
        _mc.LoadJob(GcodeProgram.Parse("b.nc", ["G1 X10 Y-10 F100"]));
        await Until(() => _mc.Status.Wco.X != 0);
        var msgs = _mc.CheckBounds();
        Assert.Single(msgs);
        Assert.Contains("right of the machine", msgs[0]);
    }
}
