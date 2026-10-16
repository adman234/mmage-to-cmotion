using Millway.Core.Grbl;

namespace Millway.Core.Machine;

public enum ZAxisType { BeltDrive, ZPlus, Hdz }

/// <summary>A bed size choice. X, Y and Z are the axis travel the app works within (mm).</summary>
public sealed record SizeOption(string Name, double X, double Y, double Z);

/// <summary>
/// A supported machine family: its bed sizes, the GRBL configuration Carbide Motion
/// writes for it, and the positions Carbide Motion derives from its travel. All
/// values are the vendor's own (see docs/spec/machine-definitions.md); none are
/// Millway inventions.
/// </summary>
public sealed record MachineModel(string Id, string Name, ZAxisType[] ZTypes, SizeOption[] Sizes)
{
    public static readonly MachineModel Shapeoko3 = new("shapeoko3", "Shapeoko 3",
        [ZAxisType.BeltDrive, ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 420, 430, 95), new("XL", 830, 430, 95), new("XXL", 830, 850, 95)]);

    public static readonly MachineModel Shapeoko4 = new("shapeoko4", "Shapeoko 4",
        [ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 444.5, 444.5, 101.6), new("XL", 838.2, 444.5, 101.6), new("XXL", 838.2, 838.2, 101.6)]);

    public static readonly MachineModel ShapeokoPro = new("shapeokopro", "Shapeoko Pro",
        [ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 444.5, 444.5, 101.6), new("XL", 838.2, 444.5, 101.6), new("XXL", 838.2, 838.2, 101.6)]);

    public static readonly MachineModel Shapeoko5Pro = new("shapeoko5pro", "Shapeoko 5 Pro", [],
        [new("2x2", 623, 623, 155), new("4x2", 1237, 623, 155), new("4x4", 1237, 1237, 155)]);

    public static readonly MachineModel Generic = new("generic", "Generic GRBL", [], []);

    public static readonly MachineModel[] All = [Shapeoko4, ShapeokoPro, Shapeoko3, Shapeoko5Pro, Generic];

    public static MachineModel ById(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Generic;

    public bool IsShapeoko => this != Generic;
    public bool HasZChoice => ZTypes.Length > 0;

    /// <summary>
    /// The GRBL settings Carbide Motion writes for this model, in its order. Empty for
    /// Generic, whose controller settings are left exactly as found.
    /// </summary>
    public IReadOnlyList<string> BuildConfig(ZAxisType z)
    {
        if (this == Shapeoko5Pro)
            return ["$0=10", "$1=255", "$2=6", "$3=0", "$4=0", "$5=15", "$6=0", "$10=255",
                "$11=0.020", "$12=0.002", "$13=0", "$20=0", "$21=0", "$22=1", "$23=0",
                "$24=500.000", "$25=2000.000", "$26=25", "$27=1.50", "$30=24000", "$31=0", "$32=0",
                "$100=80.000", "$101=80.000", "$102=160.000", "$110=5000.000", "$111=5000.000", "$112=3000.000",
                "$120=500.000", "$121=500.000", "$122=500.000", "$130=1250.000", "$131=1250.000", "$132=155.000"];

        if (this == Shapeoko4 || this == ShapeokoPro)
        {
            // The Shapeoko 4 and Pro have their own table: direction invert 5 for either Z axis.
            var s = new List<string> { "$0=10", "$1=255", "$2=0", "$3=5" };
            s.AddRange(z == ZAxisType.Hdz
                ? ["$102=320", "$112=1300", "$122=100", "$132=150"]
                : ["$102=200", "$112=1000", "$122=270", "$132=95"]);
            s.AddRange(CommonTail);
            return s;
        }

        if (this == Shapeoko3)
        {
            var s = new List<string> { "$0=10", "$1=255", "$2=0" };
            s.AddRange(z switch
            {
                ZAxisType.Hdz => ["$3=2", "$102=320", "$112=1300", "$122=100", "$132=150"],
                ZAxisType.ZPlus => ["$3=2", "$102=200", "$112=1000", "$122=270", "$132=95"],
                _ => ["$3=6", "$102=40", "$112=5000", "$122=400", "$132=100"],
            });
            s.AddRange(CommonTail);
            return s;
        }
        return [];
    }

    static readonly string[] CommonTail =
    [
        "$4=0", "$5=0", "$6=0", "$10=255", "$11=.02", "$12=.01", "$13=0",
        "$20=0", "$21=0", "$22=1", "$23=0", "$24=100", "$25=2000", "$26=25", "$27=3",
        "$30=24000", "$31=0", "$32=0", "$100=40", "$101=40",
        "$110=10000", "$111=10000", "$120=500", "$121=500", "$130=845", "$131=850",
    ];

    /// <summary>
    /// Axis travel in mm. Like Carbide Motion, this comes from the bed size, not from the
    /// controller's $130-$132, which the vendor configuration sets to the largest bed.
    /// </summary>
    public Vec3 Travel(SizeOption? size)
    {
        var sz = size ?? Sizes.LastOrDefault();
        return sz == null ? Vec3.Zero : new Vec3(sz.X, sz.Y, sz.Z);
    }

    // Positions below follow Carbide Motion's machine definition. Machine coordinates run
    // from -travel (left, front, bottom) to -1 (one millimetre short of the homing switches).

    /// <summary>Where the BitSetter is expected before the operator fine-tunes it: 100 mm in from the right, 30 mm in from the front.</summary>
    public (double X, double Y) DefaultBitSetter(Vec3 travel) => (-100, -travel.Y + 30);

    /// <summary>Where the spindle goes for a tool change when there is no BitSetter: front right.</summary>
    public (double X, double Y) ToolChangePosition(Vec3 travel) => (this == Shapeoko5Pro ? -1 : -5, -travel.Y + 20);

    /// <summary>
    /// A rapid position on a 3x3 grid. Column -1/0/1 is left/centre/right, row 1/0/-1 is
    /// back/centre/front. The front and centre rows sit 70 mm back from the front of
    /// travel, clear of the front plate and BitSetter.
    /// </summary>
    public static (double X, double Y) RapidPosition(Vec3 travel, int column, int row)
    {
        double minX = -travel.X, minY = -travel.Y, max = -1;
        double x = column < 0 ? minX + 10 : column == 0 ? (minX + max) / 2 : max - 10;
        double y = row > 0 ? max - 10 : row == 0 ? (minY + max) / 2 + 70 : minY + 70;
        return (x, y);
    }

    /// <summary>Maps a Carbide 3D USB product ID (vendor 16D0) to a likely model.</summary>
    public static MachineModel? FromUsb(int vid, int pid)
    {
        if (vid != 0x16D0) return null;
        return pid switch
        {
            0x0AD8 => Shapeoko4,   // Shapeoko 3 / 4 / Pro share this controller
            0x0FA7 => Shapeoko5Pro,
            _ => null,
        };
    }

    public static readonly int[] CarbideProductIds = [0x0AD8, 0x0931, 0x0ADA, 0x0FA6, 0x0FA7, 0x12DB, 0x12DC];
}

/// <summary>BitSetter cycle constants from Carbide Motion's machine definition.</summary>
public static class BitSetterCycle
{
    public const double SafeZ = -5;          // traverse height
    public const double StartZ = -15;        // lowered to before probing
    public const double ProbeDistance = 150; // how far the first touch may travel
    public const double FastFeed = 800;
    public const double SlowFeed = 200;
    public const double Retract = 5;         // above the first touch before the second
    public const double SlowOvershoot = 2;   // how far past the first touch the second may go
    public static readonly TimeSpan SpinDown = TimeSpan.FromMilliseconds(4000);
}
