using System.Globalization;
using System.Text.RegularExpressions;

namespace Millway.Core.Grbl;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public double this[int axis] => axis switch { 0 => X, 1 => Y, _ => Z };
    public Vec3 With(int axis, double v) => axis switch { 0 => this with { X = v }, 1 => this with { Y = v }, _ => this with { Z = v } };
    public override string ToString() => FormattableString.Invariant($"{X:0.000},{Y:0.000},{Z:0.000}");
}

public enum GrblState { Unknown, Idle, Run, Hold, Jog, Alarm, Door, Check, Home, Sleep }

/// <summary>One parsed realtime status report, e.g. &lt;Idle|MPos:0,0,0|FS:0,0|Pn:P|WCO:1,2,3&gt;.</summary>
public sealed record GrblStatus
{
    public GrblState State { get; init; }
    public int SubState { get; init; }
    public Vec3 MachinePos { get; init; }
    public Vec3 Wco { get; init; }
    public double Feed { get; init; }
    public double Spindle { get; init; }
    public string Pins { get; init; } = "";
    public int FeedOverride { get; init; } = 100;
    public int RapidOverride { get; init; } = 100;
    public int SpindleOverride { get; init; } = 100;
    public int PlannerFree { get; init; } = -1;
    public int LineNumber { get; init; } = -1;

    public Vec3 WorkPos => MachinePos - Wco;
    public bool Probe => Pins.Contains('P');
    public bool Door => Pins.Contains('D');
    public bool AnyLimit => Pins.IndexOfAny(['X', 'Y', 'Z']) >= 0;

    /// <summary>Parses a status line. Fields the report omits (WCO, Ov) carry over from <paramref name="previous"/>.</summary>
    public static GrblStatus? Parse(string line, GrblStatus? previous)
    {
        if (line.Length < 3 || line[0] != '<' || line[^1] != '>') return null;
        var parts = line[1..^1].Split('|');
        var state = parts[0];
        int sub = 0;
        int colon = state.IndexOf(':');
        if (colon >= 0)
        {
            int.TryParse(state[(colon + 1)..], out sub);
            state = state[..colon];
        }
        if (!Enum.TryParse(state, true, out GrblState gs)) gs = GrblState.Unknown;

        var s = (previous ?? new GrblStatus()) with { State = gs, SubState = sub, Pins = "", LineNumber = -1 };
        Vec3? wpos = null;
        foreach (var part in parts.Skip(1))
        {
            int c = part.IndexOf(':');
            if (c < 0) continue;
            var key = part[..c];
            var val = part[(c + 1)..];
            switch (key)
            {
                case "MPos": if (TryVec(val, out var m)) s = s with { MachinePos = m }; break;
                case "WPos": if (TryVec(val, out var w)) wpos = w; break;
                case "WCO": if (TryVec(val, out var o)) s = s with { Wco = o }; break;
                case "Pn": s = s with { Pins = val }; break;
                case "Ln": if (int.TryParse(val, out var ln)) s = s with { LineNumber = ln }; break;
                case "F": s = s with { Feed = Num(val) }; break;
                case "FS":
                    {
                        var f = val.Split(',');
                        s = s with { Feed = Num(f[0]), Spindle = f.Length > 1 ? Num(f[1]) : 0 };
                        break;
                    }
                case "Ov":
                    {
                        var f = val.Split(',');
                        if (f.Length == 3)
                            s = s with { FeedOverride = (int)Num(f[0]), RapidOverride = (int)Num(f[1]), SpindleOverride = (int)Num(f[2]) };
                        break;
                    }
                case "Bf":
                    {
                        var f = val.Split(',');
                        s = s with { PlannerFree = (int)Num(f[0]) };
                        break;
                    }
            }
        }
        if (wpos is { } wp) s = s with { MachinePos = wp + s.Wco };
        return s;
    }

    static double Num(string v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    internal static bool TryVec(string v, out Vec3 vec)
    {
        var f = v.Split(',');
        vec = default;
        if (f.Length < 3) return false;
        vec = new Vec3(Num(f[0]), Num(f[1]), Num(f[2]));
        return true;
    }
}

public readonly record struct GrblResponse(bool Ok, int ErrorCode)
{
    public static readonly GrblResponse Success = new(true, 0);
}

public sealed class GrblException(string message) : Exception(message);

public static partial class GrblText
{
    public static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"^\[PRB:([^:\]]+):(\d)\]$")]
    public static partial Regex ProbeResult();

    [GeneratedRegex(@"^\[(G5[4-9]|G28|G30|G92):([^\]]+)\]$")]
    public static partial Regex Offset();

    [GeneratedRegex(@"^\$(\d+)=([-\d.]+)")]
    public static partial Regex Setting();

    [GeneratedRegex(@"^Grbl (\d+\.\d+)(\w*)")]
    public static partial Regex Welcome();

    [GeneratedRegex(@"^ALARM:\s?(\d+)$")]
    public static partial Regex Alarm();

    [GeneratedRegex(@"^error:\s?(\d+)$")]
    public static partial Regex Error();

    public static string ErrorText(int code) => code switch
    {
        1 => "G-code words consist of a letter and a value. Letter was not found.",
        2 => "Numeric value format is not valid or missing an expected value.",
        3 => "Grbl '$' system command was not recognized or supported.",
        4 => "Negative value received for an expected positive value.",
        5 => "Homing cycle is not enabled via settings.",
        6 => "Minimum step pulse time must be greater than 3usec.",
        7 => "EEPROM read failed. Reset and restored to default values.",
        8 => "Grbl '$' command cannot be used unless Grbl is IDLE.",
        9 => "G-code locked out during alarm or jog state.",
        10 => "Soft limits cannot be enabled without homing also enabled.",
        11 => "Max characters per line exceeded. Line was not processed and executed.",
        12 => "Grbl '$' setting value exceeds the maximum step rate supported.",
        13 => "Safety door detected as opened and door state initiated.",
        14 => "Build info or startup line exceeded EEPROM line length limit.",
        15 => "Jog target exceeds machine travel. Command ignored.",
        16 => "Jog command with no '=' or contains prohibited g-code.",
        17 => "Laser mode requires PWM output.",
        20 => "Unsupported or invalid g-code command found in block.",
        21 => "More than one g-code command from same modal group found in block.",
        22 => "Feed rate has not yet been set or is undefined.",
        23 => "G-code command in block requires an integer value.",
        24 => "Two G-code commands that both require the use of the XYZ axis words were detected in the block.",
        25 => "A G-code word was repeated in the block.",
        26 => "A G-code command requires XYZ axis words in the block, but none were detected.",
        27 => "N line number value is not within the valid range of 1 - 9,999,999.",
        28 => "A G-code command was sent, but is missing some required P or L value words in the line.",
        29 => "Grbl supports six work coordinate systems G54-G59. G59.1, G59.2, and G59.3 are not supported.",
        30 => "The G53 G-code command requires either a G0 seek or G1 feed motion mode to be active.",
        31 => "There are unused axis words in the block and G80 motion mode cancel is active.",
        32 => "A G2 or G3 arc was commanded but there are no XYZ axis words in the selected plane to trace the arc.",
        33 => "The motion command has an invalid target.",
        34 => "A G2 or G3 arc, traced with the radius definition, had a mathematical error when computing the arc geometry.",
        35 => "A G2 or G3 arc, traced with the offset definition, is missing the IJK offset word in the selected plane.",
        36 => "There are unused, leftover G-code words that aren't used by any command in the block.",
        37 => "The G43.1 dynamic tool length offset command cannot apply an offset to an axis other than its configured axis.",
        38 => "Tool number greater than max supported value.",
        _ => "Unknown error.",
    };

    public static string AlarmText(int code) => code switch
    {
        1 => "Hard limit has been triggered. Machine position is likely lost; re-initialize the machine.",
        2 => "G-code motion target exceeds machine travel.",
        3 => "Reset while in motion. Machine position is likely lost; re-initialize the machine.",
        4 => "Probe fail. The probe is not in the expected initial state before starting probe cycle.",
        5 => "Probe fail. Probe did not contact the workpiece within the programmed travel.",
        6 => "Homing fail. Reset during homing cycle.",
        7 => "Homing fail. Safety door was opened during homing cycle.",
        8 => "Homing fail. Cycle failed to clear limit switch when pulling off.",
        9 => "Homing fail. Could not find limit switch within search distances.",
        10 => "Homing fail. Second dual axis limit switch failed to trigger after the first.",
        _ => "Unknown alarm.",
    };
}
