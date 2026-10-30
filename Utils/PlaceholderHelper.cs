using System.Text.RegularExpressions;
using CustomCommands.Config;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Players;

namespace CustomCommands.Utils;

/// <summary>
/// Tek gecisli (single-pass) {PLACEHOLDER} degistirici.
/// Sunucu, oyuncu ve komut arguman degiskenlerini ({NAME}, {USERID}, {SLOT}, {STEAMID64}, {DISCORD}, {ARGS}, {ARG1}...)
/// guvenle doldurur. Konsol komutlarinda komut enjeksiyonunu onlemek icin SanitizeCommandToken kullanir.
/// </summary>
public static class PlaceholderHelper
{
    private static readonly Regex TokenRegex = new(@"\{(?<k>[A-Z0-9_]+)\}", RegexOptions.Compiled);

    public static Dictionary<string, string> BuildValues(
        ISwiftlyCore core,
        PluginConfig config,
        IPlayer? caller,
        string? usedCommand = null,
        string[]? args = null)
    {
        List<IPlayer> all;
        try
        {
            all = core.PlayerManager.GetAllPlayers().Where(PlayerHelper.IsUsable).ToList();
        }
        catch
        {
            all = new List<IPlayer>();
        }

        int humanCount = all.Count(p => !p!.IsFakeClient);
        int botCount = all.Count(p => p!.IsFakeClient);
        int tCount = all.Count(p => !p!.IsFakeClient && PlayerHelper.GetTeam(p) == "T");
        int ctCount = all.Count(p => !p!.IsFakeClient && PlayerHelper.GetTeam(p) == "CT");
        int specCount = all.Count(p => !p!.IsFakeClient && PlayerHelper.GetTeam(p) == "SPEC");

        string mapName = "unknown";
        int maxPlayers = 32;
        string serverIp = config.Settings.ServerDomain;

        try
        {
            var rawMap = PlayerHelper.SafeMapName(core);
            if (!string.IsNullOrWhiteSpace(rawMap)) mapName = rawMap;
            var mc = core.Engine.GlobalVars.MaxClients;
            if (mc > 0) maxPlayers = mc;
            if (!string.IsNullOrWhiteSpace(core.Engine.ServerIP))
                serverIp = core.Engine.ServerIP!;
        }
        catch { /* fallback */ }

        string serverName = config.Settings.ServerNameOverride;
        if (string.IsNullOrWhiteSpace(serverName))
        {
            try
            {
                serverName = core.ConVar.FindAsString("hostname")?.ValueAsString ?? "Counter-Strike 2";
            }
            catch
            {
                serverName = "Counter-Strike 2";
            }
        }

        var now = DateTime.Now;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TIME"] = now.ToString("HH:mm:ss"),
            ["SHORTTIME"] = now.ToString("HH:mm"),
            ["DATE"] = now.ToString("dd.MM.yyyy"),
            ["DATETIME"] = now.ToString("dd.MM.yyyy HH:mm"),
            ["MAP"] = mapName,
            ["SERVERNAME"] = ColorHelper.Sanitize(serverName),
            ["SERVERIP"] = serverIp,
            ["DOMAIN"] = config.Settings.ServerDomain,
            ["DISCORD"] = config.Settings.DiscordInvite,
            ["WEBSITE"] = config.Settings.WebsiteUrl,
            ["PLAYERS"] = humanCount.ToString(),
            ["BOTS"] = botCount.ToString(),
            ["TOTALPLAYERS"] = (humanCount + botCount).ToString(),
            ["MAXPLAYERS"] = maxPlayers.ToString(),
            ["T_COUNT"] = tCount.ToString(),
            ["CT_COUNT"] = ctCount.ToString(),
            ["SPEC_COUNT"] = specCount.ToString(),
            ["PREFIX"] = config.Settings.Prefix,
            ["COMMAND"] = usedCommand ?? string.Empty,
        };

        ulong sid = PlayerHelper.GetSteamId64(caller);
        string rawName = PlayerHelper.IsUsable(caller) ? PlayerHelper.GetName(caller) : "Konsol";
        string ip = PlayerHelper.GetIpAddress(caller);

        dict["NAME"] = ColorHelper.Sanitize(rawName);
        dict["PLAYERNAME"] = ColorHelper.Sanitize(rawName);
        dict["STEAMID64"] = sid == 0 ? "-" : sid.ToString();
        dict["STEAMID"] = sid == 0 ? "-" : sid.ToString();
        dict["STEAMID2"] = sid == 0 ? "-" : SteamIdHelper.ToSteam2(sid);
        dict["STEAMID3"] = sid == 0 ? "-" : SteamIdHelper.ToSteam3(sid);
        dict["IP"] = string.IsNullOrWhiteSpace(ip) ? "-" : ip;

        if (PlayerHelper.IsUsable(caller))
        {
            dict["SLOT"] = caller!.Slot.ToString();
            dict["PLAYERID"] = caller.PlayerID.ToString();
            dict["USERID"] = caller.UserID.ToString();
            dict["TEAM"] = PlayerHelper.GetTeam(caller);
            dict["HP"] = PlayerHelper.GetHealth(caller).ToString();
            dict["HEALTH"] = PlayerHelper.GetHealth(caller).ToString();
            dict["ARMOR"] = PlayerHelper.GetArmor(caller).ToString();
            dict["PING"] = PlayerHelper.GetPing(caller).ToString();
            dict["SCORE"] = PlayerHelper.GetScore(caller).ToString();
            dict["KILLS"] = PlayerHelper.GetKills(caller).ToString();
            dict["DEATHS"] = PlayerHelper.GetDeaths(caller).ToString();
            dict["MVPS"] = PlayerHelper.GetMvps(caller).ToString();
        }
        else
        {
            dict["SLOT"] = "0";
            dict["PLAYERID"] = "0";
            dict["USERID"] = "0";
            dict["TEAM"] = "NONE";
            dict["HP"] = "0";
            dict["HEALTH"] = "0";
            dict["ARMOR"] = "0";
            dict["PING"] = "0";
            dict["SCORE"] = "0";
            dict["KILLS"] = "0";
            dict["DEATHS"] = "0";
            dict["MVPS"] = "0";
        }

        var safeArgs = args ?? Array.Empty<string>();
        dict["ARGS"] = string.Join(" ", safeArgs);
        dict["ARGS_COUNT"] = safeArgs.Length.ToString();
        dict["ARGS_FROM_2"] = safeArgs.Length >= 2 ? string.Join(" ", safeArgs.Skip(1)) : string.Empty;
        dict["ARGS_FROM_3"] = safeArgs.Length >= 3 ? string.Join(" ", safeArgs.Skip(2)) : string.Empty;

        for (int i = 1; i <= 9; i++)
        {
            dict[$"ARG{i}"] = i <= safeArgs.Length ? safeArgs[i - 1] : string.Empty;
        }

        return dict;
    }

    public static string Apply(string? input, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        return TokenRegex.Replace(input, m => values.TryGetValue(m.Groups["k"].Value, out var v) ? v : m.Value);
    }

    /// <summary>
    /// Konsol/Sunucu komutu olustururken oyuncu kaynakli degiskenlerdeki (NAME, ARGS, ARG1..)
    /// ';' ve yeni satir karakterlerini temizleyerek komut enjeksiyonunu onler.
    /// </summary>
    public static string ApplyForCommand(string? commandTemplate, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(commandTemplate)) return string.Empty;
        return TokenRegex.Replace(commandTemplate, m =>
        {
            var key = m.Groups["k"].Value;
            if (!values.TryGetValue(key, out var val)) return m.Value;
            return SanitizeCommandArg(val);
        });
    }

    public static string SanitizeCommandArg(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value
            .Replace(";", "")
            .Replace("\r", "")
            .Replace("\n", "")
            .Replace("\"", "'")
            .Trim();
    }
}

