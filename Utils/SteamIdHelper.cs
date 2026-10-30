using System.Text.RegularExpressions;

namespace CustomCommands.Utils;

/// <summary>SteamID2 (STEAM_0:1:...), SteamID3 ([U:1:...]) ve SteamID64 donusum yardimcisi.</summary>
public static class SteamIdHelper
{
    private const ulong Base = 76561197960265728UL;

    private static readonly Regex Steam2Regex =
        new(@"^STEAM_[0-5]:(?<y>[0-1]):(?<z>\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Steam3Regex =
        new(@"^\[?U:1:(?<acct>\d+)\]?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ulong Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var s = raw.Trim();

        if (ulong.TryParse(s, out var sid64) && sid64 > Base)
            return sid64;

        var m2 = Steam2Regex.Match(s);
        if (m2.Success &&
            ulong.TryParse(m2.Groups["y"].Value, out var y) &&
            ulong.TryParse(m2.Groups["z"].Value, out var z))
        {
            return Base + z * 2 + y;
        }

        var m3 = Steam3Regex.Match(s);
        if (m3.Success && ulong.TryParse(m3.Groups["acct"].Value, out var acct))
        {
            return Base + acct;
        }

        return 0;
    }

    public static string ToSteam2(ulong sid64)
    {
        if (sid64 <= Base) return sid64.ToString();
        ulong acct = sid64 - Base;
        ulong y = acct & 1UL;
        ulong z = acct >> 1;
        return $"STEAM_0:{y}:{z}";
    }

    public static string ToSteam3(ulong sid64)
    {
        if (sid64 <= Base) return sid64.ToString();
        ulong acct = sid64 - Base;
        return $"[U:1:{acct}]";
    }
}

