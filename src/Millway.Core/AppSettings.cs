using System.Text.Json;
using System.Text.Json.Serialization;
using Millway.Core.Machine;

namespace Millway.Core;

public sealed class QuickAction
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Code { get; set; } = [];
}

/// <summary>User configuration, stored as JSON in the per-user config directory.</summary>
public sealed class AppSettings
{
    public string ModelId { get; set; } = "";
    public string SizeName { get; set; } = "";
    public ZAxisType ZType { get; set; } = ZAxisType.ZPlus;
    public bool ConfigSent { get; set; }

    public bool Inches { get; set; }
    public bool EnableMdi { get; set; } = true;
    public bool FullScreen { get; set; }
    public string LastPort { get; set; } = "";

    public bool BitSetterEnabled { get; set; }
    /// <summary>BitSetter location in machine coordinates (mm).</summary>
    public double BitSetterX { get; set; }
    public double BitSetterY { get; set; }
    /// <summary>Machine Z at which the tool that the current Z zero belongs to touched the BitSetter.</summary>
    public double? ReferenceToolZ { get; set; }
    /// <summary>Height of the touch plate above the stock surface when probing Z (mm).</summary>
    public double ProbeThickness { get; set; }
    /// <summary>True for a trim router the operator switches by hand; false when GRBL controls the spindle.</summary>
    public bool ManualSpindle { get; set; } = true;

    /// <summary>Accept jobs from Carbide Create style senders on TCP 6280.</summary>
    public bool AllowRemoteAccess { get; set; }
    /// <summary>Accept jobs from GRBL-over-TCP senders such as MillMage.</summary>
    public bool GrblTcpEnabled { get; set; } = true;
    public int GrblTcpPort { get; set; } = 23;
    public string AllowedSenders { get; set; } = "private";

    public double TravelXmm { get; set; }
    public double TravelYmm { get; set; }
    public double TravelZmm { get; set; }
    public double RunMinutes { get; set; }
    public DateTime CountersSince { get; set; } = DateTime.UtcNow;

    public List<QuickAction> UserActions { get; set; } = [];

    [JsonIgnore] public string? Path { get; private set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultPath()
    {
        var dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        return System.IO.Path.Combine(dir, "Millway", "settings.json");
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath();
        AppSettings s;
        try
        {
            s = File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new();
        }
        catch (JsonException)
        {
            // Keep the unreadable file for inspection rather than overwriting it silently.
            File.Copy(path, path + ".bad", true);
            s = new();
        }
        s.Path = path;
        return s;
    }

    public void Save()
    {
        if (Path == null) return;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, Path, true);
    }

    [JsonIgnore] public MachineModel Model => MachineModel.ById(ModelId);
    [JsonIgnore] public SizeOption? Size => Model.Sizes.FirstOrDefault(s => s.Name == SizeName) ?? Model.Sizes.LastOrDefault();
    [JsonIgnore] public bool IsConfigured => ModelId != "";

    public static readonly QuickAction[] BuiltInActions =
    [
        new() { Name = "Move cutter above current XY zero", Description = "Retract cutter and move to the current XY zero", Code = ["G21", "G53 G0 Z-3.0", "G0 X0Y0"] },
        new() { Name = "Plunge to 1 inch above current Z zero", Description = "Move cutter to 1 inch above current Z zero to confirm cutting depths", Code = ["G21", "G1 Z25.4F300"] },
        new() { Name = "Load material", Description = "Retract cutter and move it to the back of machine travel", Code = ["G21", "G53 G0 Z-3.0", "G53 G0 Y-3.0"] },
        new() { Name = "Re-initialize machine", Description = "Run a homing cycle to reset the position of the machine", Code = ["$H"] },
    ];
}
