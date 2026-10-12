using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Millway.App;
using Millway.Core.Grbl;
using Millway.Core.Machine;
using Millway.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Millway.Tests;

public class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Millway.App.App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Drives the real window, headless, against the simulator.</summary>
public class UiTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "mw-ui-" + Guid.NewGuid().ToString("N"));
    MainWindow? _window;

    public void Dispose()
    {
        _window?.Close();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    MainWindow Open(string settingsJson, string page = "run")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, settingsJson);
        _window = new MainWindow(new Options(true, false, null, path, null, page, 100));
        _window.Show();
        return _window;
    }

    const string Configured = """{"ModelId":"shapeoko4","SizeName":"XL","ZType":"ZPlus","ManualSpindle":false,"GrblTcpEnabled":false}""";

    static async Task Until(Func<bool> cond, string what, int ms = 10000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (!cond())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();
        }
    }

    static Button? Find(Window w, string text) =>
        w.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Content is TextBlock t && t.Text == text);

    static async Task Click(Window w, string text)
    {
        await Until(() => Find(w, text) is { IsEffectivelyEnabled: true }, $"button \"{text}\"");
        Find(w, text)!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    static bool Shows(Window w, string text) =>
        w.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text != null && t.Text.Contains(text));

    [AvaloniaFact]
    public async Task UnconfiguredMachineIsSentToSettings()
    {
        var w = Open("{}");
        await Until(() => Shows(w, "No configuration found"), "setup prompt");
        await Click(w, "OK");
        await Until(() => Shows(w, "Choose machine") || w.GetVisualDescendants().OfType<ComboBox>().Any(), "settings page");
        Assert.Equal(MachineState.NotInitialized, w.Machine.State);
    }

    [AvaloniaFact]
    public async Task InitializeLoadAndRunJob()
    {
        var w = Open(Configured);
        await Until(() => w.Machine.State == MachineState.NotInitialized, "connection");
        await Until(() => Find(w, "START JOB") != null, "job pane");
        Assert.False(Find(w, "START JOB")!.IsEffectivelyEnabled);

        await Click(w, "INITIALIZE MACHINE");
        await Until(() => w.Machine.State == MachineState.Idle && w.Machine.Homed, "homing");

        var file = Path.Combine(_dir, "job.nc");
        File.WriteAllLines(file, ["G21 G90", "G1 X-20 Y-20 F3000", "G1 X-40", "M30"]);
        w.LoadFile(file);
        await Until(() => Shows(w, "job.nc"), "job info");

        await Click(w, "START JOB");
        await Until(() => Shows(w, "Zero Not Set"), "zero warning");
        await Click(w, "PROCEED");
        await Until(() => Shows(w, "Job Complete"), "completion");
        await Click(w, "OK");
        Assert.Equal(MachineState.Idle, w.Machine.State);
        Assert.Equal(-40, w.Simulator!.MachinePos.X, 1);
    }

    [AvaloniaFact]
    public async Task JogButtonMovesOneIncrement()
    {
        var w = Open(Configured, "jog");
        await Until(() => w.Machine.State == MachineState.NotInitialized, "connection");
        await w.Machine.InitializeAsync();
        await Until(() => Find(w, "Y-") is { IsEffectivelyEnabled: true }, "jog pad");

        var b = Find(w, "Y-")!;
        var centre = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), w)!.Value;
        w.MouseDown(centre, MouseButton.Left);
        w.MouseUp(centre, MouseButton.Left);
        await Until(() => w.Simulator!.State == GrblState.Idle && w.Simulator.MachinePos.Y < -1.5, "jog move");
        Assert.Equal(-2, w.Simulator!.MachinePos.Y, 2);
    }

    [AvaloniaFact]
    public async Task SetZeroDialogZeroesAllAxes()
    {
        var w = Open(Configured, "jog");
        await Until(() => w.Machine.State == MachineState.NotInitialized, "connection");
        await w.Machine.InitializeAsync();
        await w.Machine.MdiAsync("G53 G0 X-30 Y-40 Z-10");
        await Click(w, "SET ZERO");
        await Click(w, "ZERO ALL");
        await Until(() => w.Simulator!.Wco.X == -30, "offset");
        Assert.Equal(new Vec3(-30, -40, -10), w.Simulator!.Wco);
    }
}
