using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Millway.Core.Grbl;

namespace Millway.Core.Gcode;

public enum LineKind { Motion, ToolChange, Pause, ProgramEnd, SpindleOn, SpindleOff, Other }

/// <summary>One executable line of a program, already stripped of comments and whitespace.</summary>
public sealed record GcodeLine(int SourceLine, string Text, LineKind Kind, int Tool, double Rpm, string Comment);

public sealed record ToolUse(int Number, string Description);

/// <summary>
/// A loaded G-code file: the cleaned lines to stream, plus what a dry interpretation
/// of the motion says about its extents, tools and running time.
/// </summary>
public sealed partial class GcodeProgram
{
    public string Name { get; private init; } = "";
    public DateTime Loaded { get; } = DateTime.Now;
    public IReadOnlyList<GcodeLine> Lines { get; private init; } = [];
    public IReadOnlyList<ToolUse> Tools { get; private init; } = [];
    public IReadOnlyList<string> Warnings { get; private init; } = [];
    /// <summary>Extents of all motion in work coordinates (mm). Invalid when <see cref="HasMotion"/> is false.</summary>
    public Vec3 Min { get; private init; }
    public Vec3 Max { get; private init; }
    public bool HasMotion { get; private init; }
    public TimeSpan Estimate { get; private init; }
    /// <summary>Toolpath vertices for preview: (point, isRapid). Capped to keep huge files cheap.</summary>
    public IReadOnlyList<(Vec3 P, bool Rapid)> Path { get; private init; } = [];

    public Vec3 Size => HasMotion ? Max - Min : Vec3.Zero;

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex Paren();

    [GeneratedRegex(@"([A-Z])\s*([-+]?(?:\d+\.?\d*|\.\d+))")]
    private static partial Regex Word();

    [GeneratedRegex(@"TOOL\s+#?(\d+)\s*:\s*(.*)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolComment();

    public static GcodeProgram Load(string path) =>
        Parse(System.IO.Path.GetFileName(path), File.ReadAllLines(path));

    public static GcodeProgram Parse(string name, IEnumerable<string> source, double rapidRate = 5000)
    {
        var lines = new List<GcodeLine>();
        var tools = new List<ToolUse>();
        var toolNotes = new Dictionary<int, string>();
        var warnings = new List<string>();
        var path = new List<(Vec3, bool)>();

        // Modal state for the dry run. Positions are in mm, work coordinates.
        var pos = Vec3.Zero;
        bool absolute = true, inches = false, haveMotion = false;
        int motion = 0, pendingTool = 0;
        double feed = 0, minutes = 0;
        int plane = 17;
        Vec3 min = new(double.MaxValue, double.MaxValue, double.MaxValue), max = new(double.MinValue, double.MinValue, double.MinValue);
        const int PathCap = 200_000;

        void Extend(Vec3 p)
        {
            min = new(Math.Min(min.X, p.X), Math.Min(min.Y, p.Y), Math.Min(min.Z, p.Z));
            max = new(Math.Max(max.X, p.X), Math.Max(max.Y, p.Y), Math.Max(max.Z, p.Z));
        }

        int n = 0;
        foreach (var raw in source)
        {
            n++;
            var comment = new StringBuilder();
            var text = Paren().Replace(raw, m => { comment.Append(m.Value.Trim('(', ')')).Append(' '); return ""; });
            int semi = text.IndexOf(';');
            if (semi >= 0) { comment.Append(text[(semi + 1)..]); text = text[..semi]; }
            var note = comment.ToString().Trim();
            var tm = ToolComment().Match(note);
            if (tm.Success) toolNotes[int.Parse(tm.Groups[1].Value)] = tm.Groups[2].Value.Trim();

            text = text.Trim().ToUpperInvariant();
            if (text.Length == 0 || text == "%") continue;
            if (text[0] == '/') continue; // block delete
            if (text[0] == '$') { warnings.Add($"Line {n}: GRBL system command \"{text}\" ignored."); continue; }

            var words = Word().Matches(text).Select(m => (L: m.Groups[1].Value[0], V: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))).ToList();
            if (words.Count == 0) { warnings.Add($"Line {n}: could not be read and was skipped."); continue; }

            var kind = LineKind.Other;
            double rpm = 0;
            double? x = null, y = null, z = null, i = null, j = null, k = null, r = null;
            bool g53 = false, hasG43 = false;
            int lineMotion = -1;
            foreach (var (l, v) in words)
            {
                switch (l)
                {
                    case 'G':
                        switch (v)
                        {
                            case 0: case 1: case 2: case 3: motion = lineMotion = (int)v; break;
                            case 17: case 18: case 19: plane = (int)v; break;
                            case 20: inches = true; break;
                            case 21: inches = false; break;
                            case 90: absolute = true; break;
                            case 91: absolute = false; break;
                            case 53: g53 = true; break;
                            case 43: hasG43 = true; break;
                            case 38.2: case 38.3: case 38.4: case 38.5: case 80: motion = -1; break;
                        }
                        break;
                    case 'M':
                        switch ((int)v)
                        {
                            case 0: case 1: kind = LineKind.Pause; break;
                            case 2: case 30: kind = LineKind.ProgramEnd; break;
                            case 3: case 4: kind = LineKind.SpindleOn; break;
                            case 5: kind = LineKind.SpindleOff; break;
                            case 6: kind = LineKind.ToolChange; break;
                        }
                        break;
                    case 'T': pendingTool = (int)v; break;
                    case 'S': rpm = v; break;
                    case 'F': feed = inches ? v * 25.4 : v; break;
                    case 'X': x = v; break;
                    case 'Y': y = v; break;
                    case 'Z': z = v; break;
                    case 'I': i = v; break;
                    case 'J': j = v; break;
                    case 'K': k = v; break;
                    case 'R': r = v; break;
                }
            }

            bool moves = (x ?? y ?? z) != null && motion >= 0 && !g53;
            if (moves)
            {
                double u = inches ? 25.4 : 1;
                var target = new Vec3(
                    x is { } xv ? (absolute ? xv * u : pos.X + xv * u) : pos.X,
                    y is { } yv ? (absolute ? yv * u : pos.Y + yv * u) : pos.Y,
                    z is { } zv ? (absolute ? zv * u : pos.Z + zv * u) : pos.Z);
                double dist;
                if (motion >= 2 && (i != null || j != null || k != null || r != null))
                {
                    dist = PlaneArc(plane, pos, target, (i ?? 0) * u, (j ?? 0) * u, (k ?? 0) * u, r * u, motion == 2, Extend, path.Count < PathCap ? path : null);
                }
                else
                {
                    var d = target - pos;
                    dist = Math.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
                    if (path.Count < PathCap) path.Add((target, motion == 0));
                }
                Extend(target);
                if (!haveMotion) { haveMotion = true; }
                double rate = motion == 0 ? rapidRate : feed;
                if (rate > 0) minutes += dist / rate;
                pos = target;
                if (kind == LineKind.Other) kind = LineKind.Motion;
            }

            if (kind == LineKind.ToolChange && !tools.Any(t => t.Number == pendingTool))
                tools.Add(new ToolUse(pendingTool, ""));

            if (hasG43)
            {
                // GRBL has no tool table; tool length is handled by measuring the tool instead.
                text = Regex.Replace(text, @"G43(\.\d)?\s*|H\s*\d+\s*", "").Trim();
                if (text.Length == 0) continue;
            }
            if (text.Length > 100) warnings.Add($"Line {n}: longer than GRBL accepts and may be rejected.");
            lines.Add(new GcodeLine(n, text, kind, pendingTool, rpm, note));
        }

        if (lines.Count == 0) throw new InvalidDataException("File contains no G-code");

        return new GcodeProgram
        {
            Name = name,
            Lines = lines,
            Tools = tools.Select(t => t with { Description = toolNotes.GetValueOrDefault(t.Number, "") }).ToList(),
            Warnings = warnings,
            HasMotion = haveMotion,
            Min = haveMotion ? min : Vec3.Zero,
            Max = haveMotion ? max : Vec3.Zero,
            Estimate = TimeSpan.FromMinutes(minutes),
            Path = path,
        };
    }

    /// <summary>
    /// Runs an arc in any of the three planes through the XY arc walker by renaming axes,
    /// the way GRBL selects them: G18 uses Z and X, G19 uses Y and Z.
    /// </summary>
    static double PlaneArc(int plane, Vec3 from, Vec3 to, double i, double j, double k, double? r, bool clockwise,
        Action<Vec3> extend, List<(Vec3, bool)>? path)
    {
        if (plane == 17) return ArcExtents(from, to, i, j, r, clockwise, extend, path);
        Func<Vec3, Vec3> toPlane, back;
        double oa, ob;
        if (plane == 18)
        {
            toPlane = v => new Vec3(v.Z, v.X, v.Y);
            back = v => new Vec3(v.Y, v.Z, v.X);
            (oa, ob) = (k, i);
        }
        else
        {
            toPlane = v => new Vec3(v.Y, v.Z, v.X);
            back = v => new Vec3(v.Z, v.X, v.Y);
            (oa, ob) = (j, k);
        }
        var local = path == null ? null : new List<(Vec3, bool)>();
        double len = ArcExtents(toPlane(from), toPlane(to), oa, ob, r, clockwise, v => extend(back(v)), local);
        if (local != null) path!.AddRange(local.Select(e => (back(e.Item1), e.Item2)));
        return len;
    }

    /// <summary>Walks an XY-plane arc, reporting its extents, and returns its length.</summary>
    static double ArcExtents(Vec3 from, Vec3 to, double i, double j, double? r, bool clockwise, Action<Vec3> extend, List<(Vec3, bool)>? path)
    {
        double cx, cy;
        if (r is { } radius && i == 0 && j == 0)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d == 0 || Math.Abs(radius) < d / 2) { extend(to); path?.Add((to, false)); return d; }
            double h = Math.Sqrt(radius * radius - d * d / 4);
            // The centre lies to the right of the chord for a clockwise minor arc.
            double sign = (clockwise ? -1 : 1) * (radius < 0 ? -1 : 1);
            cx = (from.X + to.X) / 2 - sign * h * dy / d;
            cy = (from.Y + to.Y) / 2 + sign * h * dx / d;
        }
        else
        {
            cx = from.X + i;
            cy = from.Y + j;
        }
        double rad = Math.Sqrt((from.X - cx) * (from.X - cx) + (from.Y - cy) * (from.Y - cy));
        double a0 = Math.Atan2(from.Y - cy, from.X - cx), a1 = Math.Atan2(to.Y - cy, to.X - cx);
        double sweep = a1 - a0;
        if (clockwise) { if (sweep >= -1e-9) sweep -= 2 * Math.PI; }
        else if (sweep <= 1e-9) sweep += 2 * Math.PI;
        // The extremes of an arc lie on the axes through its centre; add those exactly.
        for (int q = -4; q <= 4; q++)
        {
            double a = q * Math.PI / 2, rel = clockwise ? a0 - a : a - a0;
            while (rel < 0) rel += 2 * Math.PI;
            if (rel <= Math.Abs(sweep))
                extend(new Vec3(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a), to.Z));
        }
        int steps = Math.Clamp((int)(Math.Abs(sweep) * rad / 1.0), 8, 720);
        for (int s = 1; s <= steps; s++)
        {
            double t = (double)s / steps, a = a0 + sweep * t;
            var p = new Vec3(cx + rad * Math.Cos(a), cy + rad * Math.Sin(a), from.Z + (to.Z - from.Z) * t);
            extend(p);
            path?.Add((p, false));
        }
        double dz = to.Z - from.Z;
        return Math.Sqrt(Math.Pow(Math.Abs(sweep) * rad, 2) + dz * dz);
    }
}
