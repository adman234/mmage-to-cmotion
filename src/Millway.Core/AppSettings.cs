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
    /// <summary>BitSetter location in machine coordinates (mm), i.e. measured from the homing switches.</summary>
    public double BitSetterX { get; set; }
    public double BitSetterY { get; set; }
    /// <summary>True once the operator has confirmed the BitSetter location for the current machine size.</summary>
    public bool BitSetterLocated { get; set; }
    /// <summary>The machine and size the BitSetter location belongs to. A different size clears it, as in Carbide Motion.</summary>
    public string BitSetterMachine { get; set; } = "";
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
    /// <summary>Identifies the machine geometry the BitSetter location was set for.</summary>
    [JsonIgnore] public string MachineKey => $"{Model.Id}/{Size?.Name}";

    /// <summary>Folder for GRBL settings backups, next to the settings file.</summary>
    [JsonIgnore] public string BackupDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path ?? DefaultPath())!, "grbl-backups");

    /// <summary>
    /// Forgets a BitSetter location that belongs to a different machine or size and puts
    /// it back at the vendor default for this one. Returns true if anything changed.
    /// </summary>
    public bool ResetBitSetterIfMachineChanged()
    {
        if (!IsConfigured || BitSetterMachine == MachineKey) return false;
        if (BitSetterMachine == "" && (BitSetterX != 0 || BitSetterY != 0))
        {
            // A location saved by version 0.1, which did not record the machine: keep it.
            BitSetterMachine = MachineKey;
            BitSetterLocated = true;
            return true;
        }
        var (x, y) = Model.DefaultBitSetter(Model.Travel(Size));
        BitSetterX = x;
        BitSetterY = y;
        BitSetterLocated = false;
        BitSetterMachine = MachineKey;
        return true;
    }

    public static readonly QuickAction[] BuiltInActions =
    [
        new() { Name = "Move cutter above current XY zero", Description = "Retract cutter and move to the current XY zero", Code = ["G21", "G53 G0 Z-3.0", "G0 X0Y0"] },
        new() { Name = "Plunge to 1 inch above current Z zero", Description = "Move cutter to 1 inch above current Z zero to confirm cutting depths", Code = ["G21", "G1 Z25.4F300"] },
        new() { Name = "Load material", Description = "Retract cutter and move it to the back of machine travel", Code = ["G21", "G53 G0 Z-3.0", "G53 G0 Y-3.0"] },
        new() { Name = "Re-initialize machine", Description = "Run a homing cycle to reset the position of the machine", Code = ["$H"] },
    ];
}
