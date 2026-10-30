using System.Globalization;
using System.Text.RegularExpressions;

namespace CustomCommands.Utils;

/// <summary>
/// CS2 chat ve Center renk yardimcisi.
/// Hem [red], [gold], [default], [#FFD700] etiketlerini
/// hem de CSS-CreateCustomCommands stilindeki {RED}, {GREEN}, {BLUE}, {DEFAULT}, {LIGHTOLIVE} etiketlerini
/// SwiftlyS2'nin Helper.Colored() fonksiyonunun tanidigi kucuk harfli [renk] formatina cevirir.
/// </summary>
public static class ColorHelper
{
    private static readonly (string Name, int R, int G, int B, string Hex)[] Palette =
    {
        ("white",       255, 255, 255, "#FFFFFF"),
        ("darkred",     255,   0,   0, "#EF4444"),
        ("green",        64, 255,  64, "#4ADE80"),
        ("lightyellow", 255, 255, 153, "#FEF08A"),
        ("lightblue",   153, 204, 255, "#38BDF8"),
        ("olive",       190, 255, 144, "#A3E635"),
        ("lime",        162, 255,  71, "#84CC16"),
        ("red",         255,  64,  64, "#F87171"),
        ("lightpurple", 186, 129, 240, "#C084FC"),
        ("purple",      136,  71, 255, "#A855F7"),
        ("grey",        197, 202, 208, "#94A3B8"),
        ("yellow",      237, 228, 122, "#FACC15"),
        ("gold",        228, 174,  57, "#F59E0B"),
        ("silver",      176, 195, 217, "#CBD5E1"),
        ("blue",         94, 152, 217, "#60A5FA"),
        ("darkblue",     75, 105, 255, "#3B82F6"),
        ("bluegrey",    100, 120, 140, "#64748B"),
        ("magenta",     211,  44, 230, "#E879F9"),
        ("lightred",    235,  75,  75, "#FB7185"),
        ("orange",      255, 156,   0, "#FB923C"),
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        { "default", "default" },
        { "/", "default" },
        { "reset", "default" },
        { "gray", "grey" },
        { "cyan", "lightblue" },
        { "pink", "magenta" },
        { "lightgreen", "lime" },
        { "lightolive", "gold" },
        { "grayblue", "bluegrey" },
        { "teamcolor", "teamcolor" },
        { "team", "teamcolor" },
    };

    private static readonly HashSet<string> KnownTags = new(
        Palette.Select(p => p.Name).Concat(new[] { "default", "/" }), StringComparer.OrdinalIgnoreCase);

    private static readonly Regex BracketTagRegex = new(@"\[(?<n>[a-zA-Z/]+|#[0-9A-Fa-f]{3,6})\]", RegexOptions.Compiled);
    private static readonly Regex BraceColorRegex = new(@"\{(?<n>[A-Z]+)\}", RegexOptions.Compiled);
    private static readonly Regex HexRegex = new(@"^#?(?<h>[0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$", RegexOptions.Compiled);

    public static IReadOnlyList<string> AvailableColors => Palette.Select(p => p.Name).Prepend("default").ToList();

    public static string ToChatTag(string? color, string? team = null)
    {
        if (string.IsNullOrWhiteSpace(color)) return string.Empty;
        var c = color.Trim();

        if ((c.StartsWith('[') && c.EndsWith(']')) || (c.StartsWith('{') && c.EndsWith('}')))
            if (c.Length > 2) c = c[1..^1];

        if (Aliases.TryGetValue(c, out var alias))
            return alias == "teamcolor" ? TeamColorTag(team) : $"[{alias}]";

        if (KnownTags.Contains(c)) return $"[{c.ToLowerInvariant()}]";

        var hex = HexRegex.Match(c);
        if (hex.Success) return $"[{NearestColor(hex.Groups["h"].Value)}]";

        return string.Empty;
    }

    public static string TeamColorTag(string? team) => team switch
    {
        "T" => "[gold]",
        "CT" => "[lightblue]",
        "SPEC" => "[grey]",
        _ => "[default]"
    };

    public static string NormalizeTags(string? text, string? team = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var step1 = BraceColorRegex.Replace(text, m =>
        {
            var n = m.Groups["n"].Value;
            if (Aliases.TryGetValue(n, out var alias))
                return alias == "teamcolor" ? TeamColorTag(team) : $"[{alias}]";
            if (KnownTags.Contains(n))
                return $"[{n.ToLowerInvariant()}]";
            return m.Value;
        });

        return BracketTagRegex.Replace(step1, m =>
        {
            var n = m.Groups["n"].Value;
            if (n.StartsWith('#'))
                return $"[{NearestColor(n[1..])}]";
            if (Aliases.TryGetValue(n, out var alias))
                return alias == "teamcolor" ? TeamColorTag(team) : $"[{alias}]";
            return KnownTags.Contains(n) ? $"[{n.ToLowerInvariant()}]" : m.Value;
        });
    }

    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var noCtrl = new string(text.Where(ch => ch >= 0x20 || ch == '\t').ToArray());
        string prev;
        do
        {
            prev = noCtrl;
            noCtrl = BracketTagRegex.Replace(noCtrl, m =>
            {
                var n = m.Groups["n"].Value;
                return n.StartsWith('#') || KnownTags.Contains(n) || Aliases.ContainsKey(n) ? string.Empty : m.Value;
            });
            noCtrl = BraceColorRegex.Replace(noCtrl, m =>
            {
                var n = m.Groups["n"].Value;
                return KnownTags.Contains(n) || Aliases.ContainsKey(n) ? string.Empty : m.Value;
            });
        } while (prev != noCtrl);
        return noCtrl;
    }

    public static string StripAll(string? text) => Sanitize(text);

    private static string NearestColor(string hex)
    {
        if (hex.Length == 3) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        int r = int.Parse(hex[..2], NumberStyles.HexNumber);
        int g = int.Parse(hex[2..4], NumberStyles.HexNumber);
        int b = int.Parse(hex[4..6], NumberStyles.HexNumber);

        string best = "white";
        double bestDist = double.MaxValue;
        foreach (var p in Palette)
        {
            double rm = (r + p.R) / 2.0;
            double dr = r - p.R, dg = g - p.G, db = b - p.B;
            double dist = (2 + rm / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rm) / 256) * db * db;
            if (dist < bestDist) { bestDist = dist; best = p.Name; }
        }
        return best;
    }
}

