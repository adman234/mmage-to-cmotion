using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Millway.Core;
using Millway.Core.Gcode;
using Millway.Core.Grbl;
using Millway.Core.Machine;
using Millway.Core.Net;
using Millway.Core.Sim;

namespace Millway.App;

/// <summary>
/// The whole operator interface: a position column on the left, and one pane at a
/// time on the right (connect, job, jog, MDI, settings). Dialogs are drawn inside
/// the window so the app works full-screen on a touch panel.
/// </summary>
public sealed class MainWindow : Window
{
    static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2F81F7"));
    static readonly IBrush Good = new SolidColorBrush(Color.Parse("#2EA043"));
    static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#D29922"));
    static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#DA3633"));
    static readonly IBrush Panel = new SolidColorBrush(Color.Parse("#161B22"));
    static readonly IBrush Back = new SolidColorBrush(Color.Parse("#0D1117"));
    static readonly IBrush Dim = new SolidColorBrush(Color.Parse("#8B949E"));
    static readonly FontFamily Mono = new("Consolas, DejaVu Sans Mono, Liberation Mono, monospace");

    readonly Options _options;
    readonly AppSettings _settings;
    readonly MachineController _mc;
    readonly JobInbox _inbox = new();
    readonly List<TcpJobServer> _servers = [];
    readonly List<string> _log = [];
    readonly object _logLock = new();

    readonly ContentControl _pane = new();
    readonly Grid _overlay = new() { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)) };
    readonly TextBlock _stateText = new() { FontSize = 16, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center };
    readonly Border _stateBadge = new() { CornerRadius = new CornerRadius(6), Padding = new Thickness(14, 6), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock[] _work = new TextBlock[3];
    readonly TextBlock[] _machine = new TextBlock[3];
    readonly TextBlock _feed = new(), _override = new(), _units = new(), _tool = new(), _message = new() { TextWrapping = TextWrapping.Wrap };
    readonly Dictionary<string, Button> _nav = new();
    string _page = "run";
    string _shownKey = "";
    double _jogStep = 1;   // mm; 0 means continuous
    Action? _refreshPane;

    internal MachineController Machine => _mc;
    internal GrblSimulator? Simulator { get; private set; }
    internal AppSettings Settings => _settings;

    public MainWindow(Options options)
    {
        _options = options;
        _page = options.Page;
        _settings = AppSettings.Load(options.SettingsPath);
        _mc = new MachineController(_settings) { PromptHandler = PromptAsync };
        _mc.Log += AddLog;
        _inbox.Arrived += job => Dispatcher.UIThread.Post(() =>
        {
            AddLog($"Job received from {job.Source}: {job.Name}");
            _shownKey = "";
        });

        Title = "Millway";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://millway/Assets/icon.png")));
        Width = options.Size?.W ?? 1180;
        Height = options.Size?.H ?? 760;
        MinWidth = 900;
        MinHeight = 600;
        Background = Back;
        FontSize = 15;
        if (options.FullScreen || _settings.FullScreen) WindowState = WindowState.FullScreen;

        Content = BuildShell();
        StartServers();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Closing += (_, _) =>
        {
            foreach (var s in _servers) s.Dispose();
            _settings.Save();
            _mc.Dispose();
        };
        Opened += async (_, _) =>
        {
            if (options.OpenFile != null) LoadFile(options.OpenFile);
            if (options.Simulate) await Guard(ConnectSimulatorAsync);
        };
    }

    // ---- shell --------------------------------------------------------------------

    Control BuildShell()
    {
        var top = new Grid { ColumnDefinitions = new("Auto,*,Auto,Auto"), Background = Panel, Height = 64 };
        top.Children.Add(new TextBlock
        {
            Text = "Millway", FontSize = 22, FontWeight = FontWeight.Bold, Margin = new Thickness(18, 0, 24, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (id, label) in new[] { ("run", "RUN"), ("jog", "JOG"), ("mdi", "MDI"), ("settings", "SETTINGS"), ("log", "LOG") })
        {
            var b = Btn(label, () => { _page = id; _shownKey = ""; }, minWidth: 96, height: 44);
            _nav[id] = b;
            nav.Children.Add(b);
        }
        Grid.SetColumn(nav, 1);
        top.Children.Add(nav);
        _stateBadge.Child = _stateText;
        _stateBadge.Margin = new Thickness(0, 0, 12, 0);
        Grid.SetColumn(_stateBadge, 2);
        top.Children.Add(_stateBadge);
        var stop = Btn("STOP", () => { _mc.StopJob(); _mc.StopJog(); if (_mc.State is MachineState.Busy or MachineState.Alarm) _mc.EmergencyReset(); },
            Bad, minWidth: 110, height: 48);
        stop.Margin = new Thickness(0, 0, 12, 0);
        ToolTip.SetTip(stop, "Stop the job. During a cycle this resets the controller.");
        Grid.SetColumn(stop, 3);
        top.Children.Add(stop);

        var left = new StackPanel { Spacing = 4, Margin = new Thickness(16), Width = 250 };
        left.Children.Add(Heading("Position"));
        for (int a = 0; a < 3; a++)
        {
            var row = new Grid { ColumnDefinitions = new("36,*"), Margin = new Thickness(0, 2) };
            row.Children.Add(new TextBlock { Text = "XYZ"[a].ToString(), FontSize = 26, FontWeight = FontWeight.Bold, Foreground = Accent, VerticalAlignment = VerticalAlignment.Center });
            var col = new StackPanel();
            _work[a] = new TextBlock { FontSize = 30, FontFamily = Mono, HorizontalAlignment = HorizontalAlignment.Right };
            _machine[a] = new TextBlock { FontSize = 12, FontFamily = Mono, Foreground = Dim, HorizontalAlignment = HorizontalAlignment.Right };
            col.Children.Add(_work[a]);
            col.Children.Add(_machine[a]);
            Grid.SetColumn(col, 1);
            row.Children.Add(col);
            left.Children.Add(row);
        }
        left.Children.Add(new Border { Height = 1, Background = Dim, Opacity = 0.3, Margin = new Thickness(0, 10) });
        foreach (var t in new[] { _feed, _override, _units, _tool }) { t.Foreground = Dim; left.Children.Add(t); }
        left.Children.Add(new Border { Height = 1, Background = Dim, Opacity = 0.3, Margin = new Thickness(0, 10) });
        left.Children.Add(_message);

        var body = new Grid { ColumnDefinitions = new("Auto,*") };
        body.Children.Add(new Border { Background = Panel, Child = left, Margin = new Thickness(0, 1, 1, 0) });
        var paneHost = new Border { Padding = new Thickness(20), Child = _pane };
        Grid.SetColumn(paneHost, 1);
        body.Children.Add(paneHost);

        var root = new Grid { RowDefinitions = new("Auto,*") };
        root.Children.Add(top);
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Grid.SetRowSpan(_overlay, 2);
        root.Children.Add(_overlay);
        return root;
    }

    static TextBlock Heading(string text) => new() { Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 8) };

    Button Btn(string text, Action click, IBrush? color = null, double minWidth = 150, double height = 56)
    {
        var b = new Button
        {
            Content = new TextBlock { Text = text, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold },
            MinWidth = minWidth, MinHeight = height,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(6),
        };
        if (color != null) { b.Background = color; b.Foreground = Brushes.White; }
        b.Click += (_, _) => click();
        return b;
    }

    Button AsyncBtn(string text, Func<Task> click, IBrush? color = null, double minWidth = 150, double height = 56) =>
        Btn(text, () => _ = Guard(click), color, minWidth, height);

    /// <summary>Runs an operation and shows any failure to the operator.</summary>
    async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            AddLog("ERROR: " + e.Message);
            await PromptAsync(new Prompt("Error", e.Message, "OK"));
        }
        _shownKey = "";
    }

    void AddLog(string line)
    {
        if (line.StartsWith('<')) return;
        lock (_logLock)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
            if (_log.Count > 2000) _log.RemoveRange(0, 500);
        }
    }

    // ---- dialogs ------------------------------------------------------------------

    Task<int> PromptAsync(Prompt p)
    {
        var tcs = new TaskCompletionSource<int>();
        Dispatcher.UIThread.Post(() =>
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center };
            for (int i = 0; i < p.Buttons.Length; i++)
            {
                int index = i;
                var b = Btn(p.Buttons[i].ToUpperInvariant(), () => { _overlay.IsVisible = false; _overlay.Children.Clear(); tcs.TrySetResult(index); },
                    i == 0 ? Accent : null, minWidth: 180);
                buttons.Children.Add(b);
            }
            var box = new Border
            {
                Background = Panel, BorderBrush = Dim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(32), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Child = new StackPanel
                {
                    Spacing = 20,
                    Children =
                    {
                        new TextBlock { Text = p.Title, FontSize = 24, FontWeight = FontWeight.Bold, TextAlignment = TextAlignment.Center },
                        new TextBlock { Text = p.Message, FontSize = 17, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                        buttons,
                    },
                },
            };
            _overlay.Children.Clear();
            _overlay.Children.Add(box);
            _overlay.IsVisible = true;
        });
        return tcs.Task;
    }

    /// <summary>Shows an arbitrary panel as a dialog with a CLOSE button.</summary>
    void ShowPanel(string title, Control content)
    {
        var close = Btn("CLOSE", () => { _overlay.IsVisible = false; _overlay.Children.Clear(); }, minWidth: 160);
        close.HorizontalAlignment = HorizontalAlignment.Center;
        _overlay.Children.Clear();
        _overlay.Children.Add(new Border
        {
            Background = Panel, BorderBrush = Dim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(28), MaxWidth = 760, MaxHeight = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new DockPanel
            {
                Children =
                {
                    Dock(new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold, Margin = new Thickness(0, 0, 0, 16) }, Avalonia.Controls.Dock.Top),
                    Dock(new Border { Child = close, Margin = new Thickness(0, 16, 0, 0) }, Avalonia.Controls.Dock.Bottom),
                    new ScrollViewer { Content = content },
                },
            },
        });
        _overlay.IsVisible = true;
    }

    static T Dock<T>(T c, Dock d) where T : Control { DockPanel.SetDock(c, d); return c; }

    void ClosePanel() { _overlay.IsVisible = false; _overlay.Children.Clear(); }

    // ---- refresh ------------------------------------------------------------------

    string Fmt(double mm) => _settings.Inches
        ? (mm / 25.4).ToString("0.0000", CultureInfo.InvariantCulture)
        : mm.ToString("0.000", CultureInfo.InvariantCulture);

    void Refresh()
    {
        var st = _mc.Status;
        var wp = st.WorkPos;
        bool live = _mc.State != MachineState.Disconnected && _mc.State != MachineState.Connecting;
        for (int a = 0; a < 3; a++)
        {
            _work[a].Text = live ? Fmt(wp[a]) : "---";
            _machine[a].Text = live ? "machine " + Fmt(st.MachinePos[a]) : "";
        }
        _feed.Text = "Feed: " + (_settings.Inches ? (st.Feed / 25.4).ToString("0.0", CultureInfo.InvariantCulture) + " in/min" : st.Feed.ToString("0", CultureInfo.InvariantCulture) + " mm/min");
        _override.Text = $"Feed override: {st.FeedOverride}%";
        _units.Text = "Units: " + (_settings.Inches ? "inch" : "mm");
        _tool.Text = _settings.BitSetterEnabled ? (_mc.ToolMeasured ? "Tool measured" : "Tool not measured") : "BitSetter off";

        var (label, color) = _mc.State switch
        {
            MachineState.Disconnected => ("NOT CONNECTED", Dim),
            MachineState.Connecting => ("CONNECTING", Warn),
            MachineState.NotInitialized => ("NOT INITIALIZED", Warn),
            MachineState.Idle => ("IDLE", Good),
            MachineState.Busy => ("BUSY", Accent),
            MachineState.Running => ("RUNNING", Accent),
            MachineState.Paused => (_mc.DoorOpen ? "DOOR OPEN" : "PAUSED", Warn),
            _ => ("ALARM", Bad),
        };
        _stateText.Text = label;
        _stateBadge.Background = color;
        _message.Text = _mc.State == MachineState.Busy ? "Working: " + _mc.BusyText : _mc.LastError;
        _message.Foreground = _mc.State == MachineState.Busy ? Dim : Bad;

        foreach (var (id, b) in _nav)
        {
            b.BorderBrush = id == _page ? Accent : Brushes.Transparent;
            b.BorderThickness = new Thickness(0, 0, 0, 3);
        }

        // Rebuild the pane only when what it shows has to change.
        bool running = _mc.State is MachineState.Running or MachineState.Paused;
        string key = $"{_page}|{_mc.State}|{_mc.Job?.Name}|{_mc.Job?.Loaded.Ticks}|{_inbox.Jobs.Count}|{_mc.Homed}|{_settings.Inches}";
        if (key != _shownKey)
        {
            _shownKey = key;
            _refreshPane = null;
            _pane.Content = _page switch
            {
                "settings" => SettingsPane(),
                "log" => LogPane(),
                _ when _mc.State is MachineState.Disconnected or MachineState.Connecting => ConnectPane(),
                "jog" => JogPane(),
                "mdi" => MdiPane(),
                _ => running ? RunningPane() : IdlePane(),
            };
        }
        _refreshPane?.Invoke();
    }

    // ---- connect ------------------------------------------------------------------

    Control ConnectPane()
    {
        var ports = PortDiscovery.List();
        var combo = new ComboBox { ItemsSource = ports, MinWidth = 420, MinHeight = 48, HorizontalAlignment = HorizontalAlignment.Center };
        combo.SelectedItem = ports.FirstOrDefault(p => p.Name == _settings.LastPort) ?? ports.FirstOrDefault(p => p.IsCarbide) ?? ports.FirstOrDefault();
        var panel = new StackPanel { Spacing = 18, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = "Not Connected", FontSize = 30, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = ports.Count == 0
                ? "No machine found.\nPlease connect your machine and turn the power on."
                : ports.Any(p => p.IsCarbide) ? "Machine found. Press connect to begin." : "No Carbide 3D machine was recognised. Choose a port to connect anyway.",
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Dim,
        });
        panel.Children.Add(combo);
        var connect = AsyncBtn("CONNECT TO CUTTER", async () =>
        {
            if (combo.SelectedItem is not PortInfo port) return;
            _settings.LastPort = port.Name;
            if (!_settings.IsConfigured && MachineModel.FromUsb(port.Vid, port.Pid) is { } guess) _settings.ModelId = guess.Id;
            await Task.Run(() => _mc.ConnectAsync(new SerialTransport(port.Name)));
            await AfterConnectAsync();
        }, Accent);
        connect.IsEnabled = ports.Count > 0 && _mc.State == MachineState.Disconnected;
        panel.Children.Add(connect);
        panel.Children.Add(Btn("REFRESH PORTS", () => _shownKey = ""));
        panel.Children.Add(AsyncBtn("USE SIMULATOR", ConnectSimulatorAsync));
        if (OperatingSystem.IsLinux())
            panel.Children.Add(new TextBlock
            {
                Text = "If the port cannot be opened, add your user to the dialout group and log in again.",
                TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Dim, FontSize = 13,
            });
        return panel;
    }

    async Task ConnectSimulatorAsync()
    {
        var sim = new GrblSimulator { TimeScale = _options.SimSpeed, ProbeTriggerZ = -70 };
        Simulator = sim;
        await Task.Run(() => _mc.ConnectAsync(sim));
        await AfterConnectAsync();
    }

    async Task AfterConnectAsync()
    {
        if (!_settings.IsConfigured)
        {
            await PromptAsync(new Prompt("Machine Setup", "No configuration found for the attached machine.\nPlease choose your machine in Settings.", "OK"));
            _page = "settings";
        }
    }

    // ---- idle / job ---------------------------------------------------------------

    Control IdlePane()
    {
        var job = _mc.Job;
        var info = new StackPanel { Spacing = 6 };
        info.Children.Add(Heading("Job Info"));
        void Row(string k, string v) => info.Children.Add(new Grid
        {
            ColumnDefinitions = new("140,*"),
            Children = { new TextBlock { Text = k, Foreground = Dim }, At(new TextBlock { Text = v, TextWrapping = TextWrapping.Wrap }, 1) },
        });
        if (job == null) info.Children.Add(new TextBlock { Text = "No file loaded", Foreground = Dim });
        else
        {
            Row("Filename", job.Name);
            Row("Time Loaded", job.Loaded.ToString("h:mm tt", CultureInfo.InvariantCulture));
            Row("Lines", job.Lines.Count.ToString("N0", CultureInfo.InvariantCulture));
            if (job.HasMotion)
            {
                Row("Size X", Fmt(job.Size.X));
                Row("Size Y", Fmt(job.Size.Y));
                Row("Size Z", Fmt(job.Size.Z));
                Row("Cut depth", Fmt(Math.Max(0, -job.Min.Z)));
            }
            Row("Runtime", job.Estimate.TotalMinutes < 2 ? "Under 2 minutes" : job.Estimate.TotalHours >= 1
                ? $"{(int)job.Estimate.TotalHours} hours, {job.Estimate.Minutes} minutes" : $"{(int)job.Estimate.TotalMinutes} minutes");
            Row("Tools Used", job.Tools.Count == 0 ? "No tool changes" : string.Join(", ", job.Tools.Select(t => "#" + t.Number + (t.Description == "" ? "" : " " + t.Description))));
            if (job.Warnings.Count > 0) Row("Warnings", string.Join("\n", job.Warnings.Take(4)));
        }

        var buttons = new StackPanel { Spacing = 10, Width = 260 };
        buttons.Children.Add(AsyncBtn("LOAD NEW FILE", PickFileAsync));
        int waiting = _inbox.Jobs.Count;
        if (waiting > 0) buttons.Children.Add(Btn($"RECEIVED JOBS ({waiting})", ShowInbox, Warn));
        bool needInit = _mc.State is MachineState.NotInitialized or MachineState.Alarm || (_mc.HomingEnabled && !_mc.Homed);
        if (needInit) buttons.Children.Add(AsyncBtn("INITIALIZE MACHINE", () => _mc.InitializeAsync(), Accent));
        if (_mc.State == MachineState.Alarm) buttons.Children.Add(AsyncBtn("CLEAR ALARM", () => _mc.UnlockAsync()));
        var load = AsyncBtn("LOAD NEW TOOL", () => _mc.LoadToolAsync());
        var start = AsyncBtn("START JOB", StartJobAsync, Good);
        var actions = Btn("QUICK ACTIONS", ShowQuickActions);
        bool idle = _mc.State == MachineState.Idle && !needInit;
        load.IsEnabled = idle;
        actions.IsEnabled = idle;
        start.IsEnabled = idle && job != null;
        buttons.Children.Add(load);
        buttons.Children.Add(start);
        buttons.Children.Add(actions);

        var grid = new Grid { ColumnDefinitions = new("*,Auto"), RowDefinitions = new("Auto,*") };
        grid.Children.Add(info);
        Grid.SetColumn(buttons, 1);
        Grid.SetRowSpan(buttons, 2);
        buttons.Margin = new Thickness(20, 0, 0, 0);
        grid.Children.Add(buttons);
        var preview = new Border { Background = Panel, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 16, 0, 0), Child = new ToolpathView { Program = job } };
        Grid.SetRow(preview, 1);
        grid.Children.Add(preview);
        return grid;
    }

    static T At<T>(T c, int col) where T : Control { Grid.SetColumn(c, col); return c; }

    async Task PickFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open File",
            FileTypeFilter = [new FilePickerFileType("G-code files") { Patterns = ["*.nc", "*.gcode", "*.tap", "*.ngc", "*.gc"] }, FilePickerFileTypes.All],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) LoadFile(path);
    }

    internal void LoadFile(string path)
    {
        try { _mc.LoadJob(GcodeProgram.Load(path)); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _ = PromptAsync(new Prompt("Cannot open file", e.Message, "OK"));
        }
        _shownKey = "";
    }

    void ShowInbox()
    {
        var list = new StackPanel { Spacing = 10, MinWidth = 560 };
        foreach (var job in _inbox.Jobs)
        {
            var row = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
            row.Children.Add(new TextBlock
            {
                Text = $"{job.Name}\n{job.Lines.Count:N0} lines from {job.Source} at {job.Received:h:mm tt}" + (job.Complete ? "" : $"\nIncomplete: {job.Note}"),
                VerticalAlignment = VerticalAlignment.Center, Foreground = job.Complete ? Brushes.White : Warn,
            });
            row.Children.Add(At(Btn("LOAD", () =>
            {
                try
                {
                    _mc.LoadJob(GcodeProgram.Parse(job.Name, job.Lines));
                    _inbox.Remove(job);
                }
                catch (InvalidDataException e) { AddLog("ERROR: " + e.Message); }
                ClosePanel();
                _shownKey = "";
            }, Accent, minWidth: 110), 1));
            var del = Btn("DELETE", () => { _inbox.Remove(job); ClosePanel(); _shownKey = ""; }, minWidth: 110);
            del.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(At(del, 2));
            list.Children.Add(row);
        }
        ShowPanel("Received Jobs", list);
    }

    void ShowQuickActions()
    {
        var list = new StackPanel { Spacing = 10, MinWidth = 520 };
        foreach (var action in AppSettings.BuiltInActions.Concat(_settings.UserActions))
        {
            var a = action;
            list.Children.Add(Btn(a.Name + (a.Description == "" ? "" : "\n" + a.Description), () =>
            {
                ClosePanel();
                _ = Guard(async () =>
                {
                    int r = await PromptAsync(new Prompt("Quick Action", "Are you sure you want to run the following action:\n\n" + a.Name + "\n" + string.Join("\n", a.Code), "Run", "Cancel"));
                    if (r == 0) await _mc.RunActionAsync(a);
                });
            }, height: 64));
        }
        ShowPanel("Quick Actions", list);
    }

    async Task StartJobAsync()
    {
        var job = _mc.Job;
        if (job == null) return;
        if (_mc.Status.Wco == Vec3.Zero &&
            await PromptAsync(new Prompt("Zero Not Set", "You do not appear to have a valid program zero. Do you want to proceed?", "Cancel", "Proceed")) == 0) return;
        var bounds = _mc.CheckBounds();
        if (bounds.Count > 0 &&
            await PromptAsync(new Prompt("Bounds Check", string.Join("\n\n", bounds) + "\n\nTo resolve this error, please confirm that your program zero has been set correctly.\n\nDo you want to continue?", "Cancel", "Continue")) == 0) return;
        if (_settings.BitSetterEnabled && !_mc.ToolMeasured && job.Tools.Count == 0)
        {
            await PromptAsync(new Prompt("Tool Not Measured", "This program contains no tool changes, and a tool has not yet been measured.\n\nPlease use the Load Tool command before continuing.", "OK"));
            return;
        }
        _page = "run";
        await _mc.RunJobAsync();
        if (_mc.LastError == "") await PromptAsync(new Prompt("Job Complete", $"{job.Name} finished in {Span(_mc.JobElapsed)}.", "OK"));
    }

    static string Span(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    Control RunningPane()
    {
        var file = new TextBlock { FontSize = 20, FontWeight = FontWeight.SemiBold };
        var line = new TextBlock();
        var time = new TextBlock();
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 22 };
        var pause = Btn("PAUSE", () => { if (_mc.State == MachineState.Paused) _mc.Resume(); else _mc.Pause(); }, Warn, height: 72);
        var stop = Btn("STOP", _mc.StopJob, Bad, height: 72);
        var feedRow = new Grid { ColumnDefinitions = new("*,*,*"), Margin = new Thickness(0, 12, 0, 0) };
        feedRow.Children.Add(Btn("FEEDRATE -10%", () => _mc.FeedOverride(-1)));
        feedRow.Children.Add(At(Btn("RESET FEEDRATE", () => _mc.FeedOverride(0)), 1));
        feedRow.Children.Add(At(Btn("FEEDRATE +10%", () => _mc.FeedOverride(1)), 2));
        foreach (var c in feedRow.Children) c.Margin = new Thickness(4, 0);
        var main = new Grid { ColumnDefinitions = new("*,*"), Margin = new Thickness(0, 16, 0, 0) };
        pause.Margin = new Thickness(4, 0);
        stop.Margin = new Thickness(4, 0);
        main.Children.Add(pause);
        main.Children.Add(At(stop, 1));

        _refreshPane = () =>
        {
            var job = _mc.Job;
            if (job == null) return;
            int done = _mc.JobLinesDone, total = job.Lines.Count;
            double frac = total == 0 ? 0 : (double)done / total;
            file.Text = job.Name;
            line.Text = $"Line {done:N0} of {total:N0}";
            bar.Value = frac;
            var elapsed = _mc.JobElapsed;
            var remaining = frac > 0.02 ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - frac) / frac) : job.Estimate;
            time.Text = $"Elapsed {Span(elapsed)}   Remaining about {Span(remaining)}";
            ((TextBlock)pause.Content!).Text = _mc.State == MachineState.Paused ? "RESUME" : "PAUSE";
            pause.Background = _mc.State == MachineState.Paused ? Good : Warn;
        };
        return new StackPanel { Spacing = 10, Children = { Heading("Run Job"), file, line, bar, time, main, feedRow } };
    }

    // ---- jog ----------------------------------------------------------------------

    Control JogPane()
    {
        Button Jog(string label, int axis, int dir)
        {
            var b = Btn(label, () => { }, minWidth: 90, height: 90);
            ((TextBlock)b.Content!).FontSize = 22;
            b.AddHandler(PointerPressedEvent, (_, e) =>
            {
                double fast = axis == 2 ? 1000 : 4000;
                if (_jogStep == 0) _mc.StartJog(axis, dir, fast);
                else _mc.JogStep(axis, dir, _jogStep, _jogStep >= 1 ? fast : 500);
            }, RoutingStrategies.Tunnel);
            void Stop() { if (_jogStep == 0) _mc.StopJog(); }
            b.AddHandler(PointerReleasedEvent, (_, _) => Stop(), RoutingStrategies.Tunnel);
            b.PointerCaptureLost += (_, _) => Stop();
            return b;
        }

        var pad = new Grid { ColumnDefinitions = new("Auto,Auto,Auto,30,Auto"), RowDefinitions = new("Auto,Auto,Auto") };
        void Put(Control c, int col, int row) { Grid.SetColumn(c, col); Grid.SetRow(c, row); c.Margin = new Thickness(4); pad.Children.Add(c); }
        Put(Jog("Y+", 1, 1), 1, 0);
        Put(Jog("X-", 0, -1), 0, 1);
        Put(Jog("X+", 0, 1), 2, 1);
        Put(Jog("Y-", 1, -1), 1, 2);
        Put(Jog("Z+", 2, 1), 4, 0);
        Put(Jog("Z-", 2, -1), 4, 2);

        var steps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 14, 0, 0) };
        var stepButtons = new List<(double, Button)>();
        var inch = _settings.Inches;
        foreach (var (label, mm) in inch
            ? new[] { ("0.001 in", 0.0254), ("0.010 in", 0.254), ("0.100 in", 2.54), ("1 in", 25.4), ("FAST", 0.0) }
            : new[] { ("0.025", 0.025), ("0.25", 0.25), ("1", 1.0), ("10", 10.0), ("FAST", 0.0) })
        {
            var b = Btn(label, () => _jogStep = mm, minWidth: 84, height: 48);
            stepButtons.Add((mm, b));
            steps.Children.Add(b);
        }
        if (!stepButtons.Any(s => s.Item1 == _jogStep)) _jogStep = stepButtons[2].Item1;

        var right = new StackPanel { Spacing = 10, Width = 280 };
        right.Children.Add(Btn("SET ZERO", ShowZero, Accent));
        right.Children.Add(Btn("RAPID POSITION", ShowRapid));
        right.Children.Add(AsyncBtn("PROBE Z", async () =>
        {
            int r = await PromptAsync(new Prompt("Probe Workpiece", $"Position the cutter above the touch plate and attach the ground clip.\n\nPlate thickness: {Fmt(_settings.ProbeThickness)} {(_settings.Inches ? "in" : "mm")}", "Begin Probe", "Cancel"));
            if (r == 0) await _mc.ProbeZAsync();
        }));
        right.Children.Add(AsyncBtn("LOAD NEW TOOL", () => _mc.LoadToolAsync()));
        var hint = new TextBlock { Foreground = Dim, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        right.Children.Add(hint);

        _refreshPane = () =>
        {
            foreach (var (mm, b) in stepButtons)
            {
                if (mm == _jogStep) b.Background = Accent; else b.ClearValue(BackgroundProperty);
            }
            bool canFast = _mc.Homed && _mc.Travel.X > 0;
            hint.Text = _mc.State != MachineState.Idle ? "Jogging is available when the machine is idle."
                : !canFast ? "Initialize the machine to enable FAST jogging and travel limits." : _jogStep == 0 ? "Hold a button to move; release to stop." : "";
            pad.IsEnabled = _mc.State == MachineState.Idle;
        };

        var leftCol = new StackPanel { Children = { Heading("Jog / Position"), pad, new TextBlock { Text = "Increment", Foreground = Dim, Margin = new Thickness(0, 14, 0, 0) }, steps } };
        var grid = new Grid { ColumnDefinitions = new("*,Auto") };
        grid.Children.Add(leftCol);
        grid.Children.Add(At(right, 1));
        return grid;
    }

    void ShowZero()
    {
        var p = new StackPanel { Spacing = 10, MinWidth = 420 };
        void Add(string label, bool x, bool y, bool z, IBrush? color = null) =>
            p.Children.Add(Btn(label, () => { ClosePanel(); _ = Guard(() => _mc.SetZeroAsync(x, y, z)); }, color));
        Add("ZERO X", true, false, false);
        Add("ZERO Y", false, true, false);
        Add("ZERO Z", false, false, true);
        Add("ZERO ALL", true, true, true, Accent);
        p.Children.Add(Btn("CLEAR ALL OFFSETS", () => { ClosePanel(); _ = Guard(() => _mc.ClearOffsetsAsync()); }));
        if (_settings.BitSetterEnabled)
            p.Children.Add(new TextBlock { Text = "NOTE: Setting the Z zero with an unmeasured tool will immediately trigger a tool measurement.", TextWrapping = TextWrapping.Wrap, Foreground = Dim });
        ShowPanel("Set Current Position", p);
    }

    void ShowRapid()
    {
        var g = new Grid { ColumnDefinitions = new("*,*,*"), RowDefinitions = new("Auto,Auto,Auto,Auto"), MinWidth = 480 };
        string[] names = ["NW", "N", "NE", "W", "CENTER", "E", "SW", "S", "SE"];
        for (int i = 0; i < 9; i++)
        {
            var where = (RapidPosition)i;
            var b = Btn(names[i], () => { ClosePanel(); _ = Guard(() => _mc.RapidToAsync(where)); }, minWidth: 120, height: 70);
            b.Margin = new Thickness(4);
            Grid.SetColumn(b, i % 3);
            Grid.SetRow(b, i / 3);
            g.Children.Add(b);
        }
        var xy = Btn("RAPID TO CURRENT XY ZERO", () => { ClosePanel(); _ = Guard(() => _mc.RapidToWorkXYAsync()); });
        xy.Margin = new Thickness(4, 12, 4, 0);
        Grid.SetRow(xy, 3);
        Grid.SetColumnSpan(xy, 3);
        g.Children.Add(xy);
        ShowPanel("Rapid Position", g);
    }

    // ---- MDI / log ----------------------------------------------------------------

    Control MdiPane()
    {
        var box = new TextBox { Watermark = "G-code", MinHeight = 48, FontFamily = Mono, FontSize = 18 };
        var log = LogList();
        async Task Send()
        {
            var text = box.Text ?? "";
            box.Text = "";
            if (text.Trim() == "") return;
            await _mc.MdiAsync(text);
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = Guard(Send); };
        var send = AsyncBtn("SEND", Send, Accent, minWidth: 120, height: 48);
        var row = new Grid { ColumnDefinitions = new("*,Auto") };
        row.Children.Add(box);
        send.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(At(send, 1));
        var dock = new DockPanel();
        dock.Children.Add(Dock(Heading("MDI"), Avalonia.Controls.Dock.Top));
        dock.Children.Add(Dock(row, Avalonia.Controls.Dock.Top));
        if (!_settings.EnableMdi)
        {
            row.IsEnabled = false;
            dock.Children.Add(Dock(new TextBlock { Text = "MDI is disabled in Settings.", Foreground = Dim }, Avalonia.Controls.Dock.Top));
        }
        log.Margin = new Thickness(0, 12, 0, 0);
        dock.Children.Add(log);
        return dock;
    }

    Control LogPane()
    {
        var dock = new DockPanel();
        var copy = AsyncBtn("COPY ALL", async () =>
        {
            string text;
            lock (_logLock) text = string.Join(Environment.NewLine, _log);
            if (Clipboard != null) await Clipboard.SetTextAsync(text);
        }, minWidth: 140, height: 44);
        copy.HorizontalAlignment = HorizontalAlignment.Left;
        dock.Children.Add(Dock(Heading("Log"), Avalonia.Controls.Dock.Top));
        dock.Children.Add(Dock(copy, Avalonia.Controls.Dock.Bottom));
        var list = LogList();
        list.Margin = new Thickness(0, 0, 0, 12);
        dock.Children.Add(list);
        return dock;
    }

    Control LogList()
    {
        var text = new TextBox { IsReadOnly = true, FontFamily = Mono, FontSize = 13, AcceptsReturn = true, Background = Panel, BorderThickness = new Thickness(0) };
        int shown = -1;
        var prev = _refreshPane;
        _refreshPane = () =>
        {
            prev?.Invoke();
            lock (_logLock)
            {
                if (_log.Count == shown) return;
                shown = _log.Count;
                text.Text = string.Join("\n", _log.Skip(Math.Max(0, _log.Count - 300)));
            }
            text.CaretIndex = text.Text?.Length ?? 0;
        };
        return text;
    }

    // ---- settings -----------------------------------------------------------------

    Control SettingsPane()
    {
        var p = new StackPanel { Spacing = 10, MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };

        Control Row(string label, Control c) => new Grid
        {
            ColumnDefinitions = new("230,*"),
            Children = { new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold }, At(c, 1) },
        };
        ToggleSwitch Toggle(bool value, Action<bool> set)
        {
            var t = new ToggleSwitch { IsChecked = value, OnContent = "On", OffContent = "Off" };
            t.IsCheckedChanged += (_, _) => { set(t.IsChecked == true); _settings.Save(); _shownKey = ""; };
            return t;
        }
        TextBox Number(double value, Action<double> set)
        {
            var t = new TextBox { Text = value.ToString("0.###", CultureInfo.InvariantCulture), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
            t.LostFocus += (_, _) =>
            {
                if (double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) { set(v); _settings.Save(); }
                else t.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
            };
            return t;
        }

        p.Children.Add(Heading("Machine"));
        var model = new ComboBox { ItemsSource = MachineModel.All.Select(m => m.Name).ToList(), SelectedItem = _settings.IsConfigured ? _settings.Model.Name : null, MinWidth = 240, PlaceholderText = "Choose machine" };
        model.SelectionChanged += (_, _) =>
        {
            var m = MachineModel.All.First(x => x.Name == (string)model.SelectedItem!);
            if (m.Id == _settings.ModelId) return;
            _settings.ModelId = m.Id;
            _settings.SizeName = m.Sizes.LastOrDefault()?.Name ?? "";
            if (m.ZTypes.Length > 0 && !m.ZTypes.Contains(_settings.ZType)) _settings.ZType = m.ZTypes[0];
            _settings.ConfigSent = false;
            _settings.Save();
            _shownKey = "";
        };
        p.Children.Add(Row("Type", model));
        var mdl = _settings.Model;
        if (_settings.IsConfigured && mdl.Sizes.Length > 0)
        {
            var size = new ComboBox { ItemsSource = mdl.Sizes.Select(s => s.Name).ToList(), SelectedItem = _settings.Size?.Name, MinWidth = 240 };
            size.SelectionChanged += (_, _) => { _settings.SizeName = (string)size.SelectedItem!; _settings.ConfigSent = false; _settings.Save(); };
            p.Children.Add(Row("Size", size));
        }
        if (_settings.IsConfigured && mdl.HasZChoice)
        {
            string ZName(ZAxisType z) => z switch { ZAxisType.BeltDrive => "Belt-drive Z", ZAxisType.ZPlus => "Z-Plus", _ => "HDZ" };
            var z = new ComboBox { ItemsSource = mdl.ZTypes.Select(ZName).ToList(), SelectedItem = ZName(_settings.ZType), MinWidth = 240 };
            z.SelectionChanged += (_, _) => { _settings.ZType = mdl.ZTypes[z.SelectedIndex]; _settings.ConfigSent = false; _settings.Save(); };
            p.Children.Add(Row("Z axis", z));
        }
        if (_settings.IsConfigured && mdl.IsShapeoko)
        {
            var send = AsyncBtn("SEND CONFIGURATION TO MACHINE", async () =>
            {
                int r = await PromptAsync(new Prompt("Send Configuration", $"This writes the GRBL settings for a {mdl.Name} {_settings.Size?.Name} to the controller, replacing what is stored there.\n\nThe machine will need to be initialized afterwards.", "Send", "Cancel"));
                if (r == 0) await _mc.SendConfigAsync();
            }, Accent);
            send.IsEnabled = _mc.State is MachineState.Idle or MachineState.NotInitialized or MachineState.Alarm;
            p.Children.Add(send);
            p.Children.Add(new TextBlock { Text = _settings.ConfigSent ? "Configuration has been sent." : "Configuration has not been sent from this app. If the machine already works with other software it does not need to be.", Foreground = Dim, TextWrapping = TextWrapping.Wrap });
        }
        p.Children.Add(new TextBlock { Text = _mc.State == MachineState.Disconnected ? "Not connected" : $"Port {_mc.PortName}   GRBL {_mc.GrblVersion}   Travel {_mc.Travel}", Foreground = Dim });

        p.Children.Add(Heading("User Interface"));
        p.Children.Add(Row("Inches", Toggle(_settings.Inches, v => _settings.Inches = v)));
        p.Children.Add(Row("Enable MDI", Toggle(_settings.EnableMdi, v => _settings.EnableMdi = v)));
        p.Children.Add(Row("Full Screen Mode", Toggle(WindowState == WindowState.FullScreen, v =>
        {
            _settings.FullScreen = v;
            WindowState = v ? WindowState.FullScreen : WindowState.Normal;
        })));

        p.Children.Add(Heading("Tooling"));
        p.Children.Add(Row("Enable BitSetter", Toggle(_settings.BitSetterEnabled, v => _settings.BitSetterEnabled = v)));
        if (_settings.BitSetterEnabled)
        {
            p.Children.Add(Row("BitSetter X (machine mm)", Number(_settings.BitSetterX, v => _settings.BitSetterX = v)));
            p.Children.Add(Row("BitSetter Y (machine mm)", Number(_settings.BitSetterY, v => _settings.BitSetterY = v)));
            var here = Btn("USE CURRENT POSITION AS BITSETTER LOCATION", () => { _mc.SetBitSetterHere(); _shownKey = ""; });
            here.IsEnabled = _mc.Homed && _mc.State == MachineState.Idle;
            p.Children.Add(here);
        }
        p.Children.Add(Row("Touch plate thickness (mm)", Number(_settings.ProbeThickness, v => _settings.ProbeThickness = v)));
        p.Children.Add(Row("Manual spindle (router)", Toggle(_settings.ManualSpindle, v => _settings.ManualSpindle = v)));

        p.Children.Add(Heading("Network"));
        p.Children.Add(Row("Accept GRBL senders (MillMage)", Toggle(_settings.GrblTcpEnabled, v => { _settings.GrblTcpEnabled = v; StartServers(); })));
        p.Children.Add(Row("GRBL listener port", Number(_settings.GrblTcpPort, v => { _settings.GrblTcpPort = (int)v; StartServers(); })));
        p.Children.Add(Row("Allow Remote Access (6280)", Toggle(_settings.AllowRemoteAccess, v => { _settings.AllowRemoteAccess = v; StartServers(); })));
        p.Children.Add(new TextBlock { Text = string.Join("\n", _servers.Select(s => $"{s.GetType().Name.Replace("Server", "")} listening on port {s.Port}").DefaultIfEmpty("No network listeners are running.")), Foreground = Dim });

        p.Children.Add(Heading("Maintenance"));
        p.Children.Add(new TextBlock
        {
            Text = string.Create(CultureInfo.InvariantCulture, $"Running time {_settings.RunMinutes / 60:0.0} hours\nX travel {_settings.TravelXmm / 1e6:0.000} km   Y travel {_settings.TravelYmm / 1e6:0.000} km   Z travel {_settings.TravelZmm / 1e6:0.000} km\nSince {_settings.CountersSince.ToLocalTime():yyyy-MM-dd}"),
            Foreground = Dim,
        });
        p.Children.Add(Btn("CLEAR COUNTERS", () =>
        {
            _settings.RunMinutes = _settings.TravelXmm = _settings.TravelYmm = _settings.TravelZmm = 0;
            _settings.CountersSince = DateTime.UtcNow;
            _settings.Save();
            _shownKey = "";
        }));
        if (_mc.State != MachineState.Disconnected) p.Children.Add(Btn("DISCONNECT", _mc.Disconnect));
        p.Children.Add(new TextBlock { Text = "Millway " + typeof(MainWindow).Assembly.GetName().Version?.ToString(3), Foreground = Dim, Margin = new Thickness(0, 8, 0, 0) });
        return new ScrollViewer { Content = p };
    }

    void StartServers()
    {
        foreach (var s in _servers) s.Dispose();
        _servers.Clear();
        Allowlist allow;
        try { allow = Allowlist.Parse(_settings.AllowedSenders); }
        catch (FormatException e) { AddLog("ERROR: " + e.Message + "; only private addresses are allowed."); allow = Allowlist.Parse("private"); }

        void Try(TcpJobServer server, params int[] ports)
        {
            server.Log += AddLog;
            foreach (int port in ports)
            {
                try
                {
                    server.Start(port);
                    _servers.Add(server);
                    AddLog($"Listening for jobs on TCP port {port}");
                    return;
                }
                catch (System.Net.Sockets.SocketException e)
                {
                    AddLog($"Cannot listen on TCP port {port}: {e.Message}");
                }
            }
            server.Dispose();
        }
        if (_settings.GrblTcpEnabled)
            // Ports below 1024 need extra privileges on Linux, so fall back to a high port.
            Try(new GrblTcpServer(_inbox, allow) { Travel = () => _mc.Travel.X > 0 ? _mc.Travel : _settings.Model.IsShapeoko ? _settings.Model.Travel(_settings.ZType, _settings.Size) : new Vec3(838.2, 444.5, 95) },
                new[] { _settings.GrblTcpPort, 2323 }.Distinct().ToArray());
        if (_settings.AllowRemoteAccess) Try(new CarbideRemoteServer(_inbox, allow), 6280);
    }
}

/// <summary>Top view of a program's toolpath, scaled to fit.</summary>
public sealed class ToolpathView : Control
{
    static readonly Pen Cut = new(new SolidColorBrush(Color.Parse("#58A6FF")), 1);
    static readonly Pen Rapid = new(new SolidColorBrush(Color.FromArgb(110, 139, 148, 158)), 1);
    static readonly Pen Origin = new(new SolidColorBrush(Color.Parse("#DA3633")), 2);

    GcodeProgram? _program;
    public GcodeProgram? Program
    {
        get => _program;
        set { _program = value; InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        var job = _program;
        if (job is not { HasMotion: true } || job.Path.Count < 2) return;
        double w = Bounds.Width - 24, h = Bounds.Height - 24;
        var size = job.Size;
        if (w <= 0 || h <= 0) return;
        double scale = Math.Min(w / Math.Max(size.X, 1e-6), h / Math.Max(size.Y, 1e-6));
        double ox = 12 + (w - size.X * scale) / 2, oy = 12 + (h - size.Y * scale) / 2;
        Point Map(Vec3 p) => new(ox + (p.X - job.Min.X) * scale, oy + (job.Max.Y - p.Y) * scale);

        // Skip points so even very large files draw quickly.
        int stride = Math.Max(1, job.Path.Count / 40000);
        var cut = new StreamGeometry();
        var rapid = new StreamGeometry();
        using (var c = cut.Open())
        using (var r = rapid.Open())
        {
            var prev = Map(job.Path[0].P);
            for (int i = stride; i < job.Path.Count; i += stride)
            {
                var (p, isRapid) = job.Path[i];
                var pt = Map(p);
                var g = isRapid ? r : c;
                g.BeginFigure(prev, false);
                g.LineTo(pt);
                g.EndFigure(false);
                prev = pt;
            }
        }
        ctx.DrawGeometry(null, Rapid, rapid);
        ctx.DrawGeometry(null, Cut, cut);
        var o = Map(Vec3.Zero);
        ctx.DrawLine(Origin, o + new Vector(-8, 0), o + new Vector(8, 0));
        ctx.DrawLine(Origin, o + new Vector(0, -8), o + new Vector(0, 8));
    }
}
