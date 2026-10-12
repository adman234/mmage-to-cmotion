using Millway.Core.Grbl;

namespace Millway.Core.Machine;

public enum ZAxisType { BeltDrive, ZPlus, Hdz }

public sealed record SizeOption(string Name, double X, double Y, double Z);

/// <summary>
/// A supported machine family: how to recognise it, which sizes it comes in and the
/// GRBL configuration it should carry. Values follow the vendor's own configuration
/// for each model (see docs/spec/machine-definitions.md).
/// </summary>
public sealed record MachineModel(string Id, string Name, bool HasZChoice, ZAxisType[] ZTypes, SizeOption[] Sizes)
{
    public static readonly MachineModel Shapeoko3 = new("shapeoko3", "Shapeoko 3", true,
        [ZAxisType.BeltDrive, ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 420, 430, 95), new("XL", 830, 430, 95), new("XXL", 830, 850, 95)]);

    public static readonly MachineModel Shapeoko4 = new("shapeoko4", "Shapeoko 4", true,
        [ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 444.5, 444.5, 101.6), new("XL", 838.2, 444.5, 101.6), new("XXL", 838.2, 838.2, 101.6)]);

    public static readonly MachineModel ShapeokoPro = new("shapeokopro", "Shapeoko Pro", true,
        [ZAxisType.ZPlus, ZAxisType.Hdz],
        [new("Standard", 444.5, 444.5, 101.6), new("XL", 838.2, 444.5, 101.6), new("XXL", 838.2, 838.2, 101.6)]);

    public static readonly MachineModel Shapeoko5Pro = new("shapeoko5pro", "Shapeoko 5 Pro", false, [],
        [new("2x2", 623, 623, 155), new("4x2", 1237, 623, 155), new("4x4", 1237, 1237, 155)]);

    public static readonly MachineModel Generic = new("generic", "Generic GRBL", false, [], []);

    public static readonly MachineModel[] All = [Shapeoko4, ShapeokoPro, Shapeoko3, Shapeoko5Pro, Generic];

    public static MachineModel ById(string? id) => All.FirstOrDefault(m => m.Id == id) ?? Generic;

    public bool IsShapeoko => this != Generic;

    /// <summary>
    /// The complete GRBL settings list for this model. Returns an empty list for
    /// Generic, whose controller settings are left exactly as found.
    /// </summary>
    public IReadOnlyList<string> BuildConfig(ZAxisType z, SizeOption? size)
    {
        if (this == Generic) return [];
        var s = new List<string>();
        if (this == Shapeoko5Pro)
        {
            s.AddRange(["$0=10", "$1=255", "$2=6", "$3=0", "$4=0", "$5=1", "$6=0", "$10=255",
                "$11=0.020", "$12=0.002", "$13=0", "$20=0", "$21=0", "$22=1", "$23=0",
                "$24=500.000", "$25=2000.000", "$26=25", "$27=1.50", "$30=24000", "$31=0", "$32=0",
                "$100=80.000", "$101=80.000", "$102=160.000",
                "$110=5000.000", "$111=5000.000", "$112=3000.000",
                "$120=500.000", "$121=500.000", "$122=500.000"]);
        }
        else
        {
            s.AddRange(["$0=10", "$1=255", "$2=0"]);
            // Direction invert and the Z drive train differ per Z axis.
            s.AddRange(z switch
            {
                ZAxisType.Hdz => ["$3=2", "$102=320", "$112=1300", "$122=100"],
                ZAxisType.ZPlus => ["$3=2", "$102=200", "$112=1000", "$122=270"],
                _ => ["$3=6", "$102=40", "$112=5000", "$122=400"],
            });
            s.AddRange(["$4=0", "$5=0", "$6=0", "$10=255", "$11=.02", "$12=.01", "$13=0",
                "$20=0", "$21=0", "$22=1", "$23=0", "$24=100", "$25=2000", "$26=25", "$27=3",
                "$30=24000", "$31=0", "$32=0", "$100=40", "$101=40",
                "$110=10000", "$111=10000", "$120=500", "$121=500"]);
        }
        var t = Travel(z, size);
        s.Add("$130=" + GrblText.F(t.X));
        s.Add("$131=" + GrblText.F(t.Y));
        s.Add("$132=" + GrblText.F(t.Z));
        return s;
    }

    /// <summary>Axis travel in mm. X and Y come from the bed size, Z from the Z axis fitted.</summary>
    public Vec3 Travel(ZAxisType z, SizeOption? size)
    {
        var sz = size ?? Sizes[^1];
        double zt = this == Shapeoko5Pro ? sz.Z : z switch { ZAxisType.Hdz => 150, ZAxisType.ZPlus => 95, _ => 100 };
        return new Vec3(sz.X, sz.Y, zt);
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
