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
    public void RejectsEmptyFiles() =>
        Assert.Throws<InvalidDataException>(() => GcodeProgram.Parse("t.nc", ["(nothing)", ""]));
}

public class DefinitionTests
{
    [Fact]
    public void Shapeoko4XlZPlusConfig()
    {
        var m = MachineModel.Shapeoko4;
        var cfg = m.BuildConfig(ZAxisType.ZPlus, m.Sizes.First(s => s.Name == "XL"));
        Assert.Contains("$100=40", cfg);
        Assert.Contains("$102=200", cfg);
        Assert.Contains("$3=2", cfg);
        Assert.Contains("$22=1", cfg);
        Assert.Contains("$130=838.2", cfg);
        Assert.Contains("$131=444.5", cfg);
        Assert.Contains("$132=95", cfg);
        Assert.Equal(cfg.Count, cfg.Select(c => c[..c.IndexOf('=')]).Distinct().Count());
    }

    [Fact]
    public void GenericLeavesControllerAlone() =>
        Assert.Empty(MachineModel.Generic.BuildConfig(ZAxisType.ZPlus, null));

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

public class MachineTests : IDisposable
{
    readonly GrblSimulator _sim = new() { TimeScale = 60 };
    readonly AppSettings _settings = new() { ManualSpindle = false };
    readonly MachineController _mc;
    readonly List<Prompt> _prompts = [];

    public MachineTests()
    {
        _mc = new MachineController(_settings);
        _mc.PromptHandler = p => { lock (_prompts) _prompts.Add(p); return Task.FromResult(0); };
    }

    public void Dispose() => _mc.Dispose();

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
        Assert.Equal(200, _mc.Travel.X);
        Assert.True(_mc.Homed);
        Assert.Contains("$H", _sim.Received);
        Assert.Equal(-1, _sim.MachinePos.Z, 3);
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
        Assert.Equal(src.Count, _sim.Received.Count(l => src.Contains(l)) >= src.Count ? src.Count : -1);
    }

    [Fact]
    public async Task StopsJobAndKeepsPosition()
    {
        await ReadyAsync();
        _sim.TimeScale = 2;
        _mc.LoadJob(GcodeProgram.Parse("slow.nc", ["G91", "G1 X-150 F600", "G1 X150", "M30"]));
        var run = _mc.RunJobAsync();
        await Until(() => _sim.MachinePos.X < -3);
        _mc.StopJob();
        await run;
        Assert.Equal(MachineState.Idle, _mc.State);
        Assert.True(_mc.Homed);
        Assert.NotEqual(GrblState.Alarm, _sim.State);
        Assert.InRange(_sim.MachinePos.X, -150, -1.01);
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
        Assert.Equal(-101, _sim.MachinePos.X, 2);
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
        _settings.BitSetterEnabled = true;
        _settings.BitSetterX = -20;
        _settings.BitSetterY = -30;
        _sim.ProbeTriggerZ = -80;
        await ReadyAsync();                      // measures the first tool
        Assert.True(_mc.ToolMeasured);
        Assert.Equal(-80, _settings.ReferenceToolZ!.Value, 2);
        Assert.Equal(-20, _sim.MachinePos.X, 3);

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
    public async Task BitSetterReportsStuckProbe()
    {
        _settings.BitSetterEnabled = true;
        _sim.ProbeStuck = true;
        await _mc.ConnectAsync(_sim);
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.InitializeAsync());
        Assert.StartsWith("BS001", e.Message);
    }

    [Fact]
    public async Task BitSetterReportsMissingProbe()
    {
        _settings.BitSetterEnabled = true;
        _sim.ProbeTriggerZ = null;
        await _mc.ConnectAsync(_sim);
        var e = await Assert.ThrowsAsync<GrblException>(() => _mc.InitializeAsync());
        Assert.StartsWith("BS002", e.Message);
        Assert.NotEqual(GrblState.Alarm, _sim.State);
        Assert.Equal(-5, _sim.MachinePos.Z, 2);
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
    public async Task ContinuousJogStopsInsideTravel()
    {
        await ReadyAsync();
        _mc.StartJog(0, -1, 60000);
        await Until(() => _sim.State == GrblState.Idle && _sim.MachinePos.X < -100);
        Assert.Equal(-199, _sim.MachinePos.X, 2);
        _sim.TimeScale = 1;
        _mc.StartJog(1, -1, 600);
        await Until(() => _sim.State == GrblState.Jog);
        _mc.StopJog();
        await Until(() => _sim.State == GrblState.Idle);
        Assert.True(_sim.MachinePos.Y > -50);
    }

    [Fact]
    public async Task JobToolChangePromptsAndMeasures()
    {
        _settings.BitSetterEnabled = true;
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

    [Fact]
    public async Task SendsModelConfiguration()
    {
        _settings.ModelId = "shapeoko4";
        _settings.SizeName = "XL";
        await _mc.ConnectAsync(_sim);
        await _mc.SendConfigAsync();
        Assert.Equal("838.2", _sim.Settings[130]);
        Assert.Equal(444.5, _mc.Travel.Y);
        Assert.True(_settings.ConfigSent);
    }
}
