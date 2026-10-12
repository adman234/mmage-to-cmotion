using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Millway.App;

public sealed record Options(bool Simulate, bool FullScreen, string? OpenFile, string? SettingsPath, (int W, int H)? Size, string Page = "run", double SimSpeed = 1)
{
    public static Options Parse(string[] args)
    {
        bool sim = false, full = false;
        string? open = null, settings = null;
        string page = "run";
        double speed = 1;
        (int, int)? size = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--simulate") sim = true;
            else if (a == "--fullscreen") full = true;
            else if (a == "--open" && i + 1 < args.Length) open = args[++i];
            else if (a == "--settings" && i + 1 < args.Length) settings = args[++i];
            else if (a.StartsWith("--page=")) page = a[7..];
            else if (a.StartsWith("--sim-speed=")) double.TryParse(a[12..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out speed);
            else if (a.StartsWith("--size="))
            {
                var p = a[7..].Split('x');
                if (p.Length == 2 && int.TryParse(p[0], out int w) && int.TryParse(p[1], out int h)) size = (w, h);
            }
            else if (!a.StartsWith('-') && File.Exists(a)) open = a;
        }
        return new Options(sim, full, open, settings, size, page, speed <= 0 ? 1 : speed);
    }
}

public sealed class App : Application
{
    public static Options Options { get; set; } = new(false, false, null, null, null);

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(Options);
        base.OnFrameworkInitializationCompleted();
    }
}

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine(typeof(Program).Assembly.GetName().Version?.ToString(3));
            return 0;
        }
        App.Options = Options.Parse(args);
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }
}
