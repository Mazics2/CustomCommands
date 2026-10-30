using System.Globalization;
using System.Text;
using CustomCommands.Models;
using CustomCommands.Utils;
using Tomlyn;
using Tomlyn.Model;

namespace CustomCommands.Config;

/// <summary>
/// customcommands.toml dosyasini (ve varsa commands/*.toml ek dosyalarini) okur ve diske kaydeder.
/// </summary>
public class ConfigLoader
{
    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string ExtraCommandsDir { get; }
    public List<string> Warnings { get; } = new();

    public ConfigLoader(string configDirectory)
    {
        ConfigDirectory = configDirectory;
        Directory.CreateDirectory(configDirectory);
        ConfigPath = Path.Combine(configDirectory, "customcommands.toml");
        ExtraCommandsDir = Path.Combine(configDirectory, "commands");
    }

    public PluginConfig Load()
    {
        Warnings.Clear();

        if (!File.Exists(ConfigPath))
        {
            File.WriteAllText(ConfigPath, DefaultConfigTemplate.Content, new UTF8Encoding(false));
        }

        var content = File.ReadAllText(ConfigPath, Encoding.UTF8);
        var config = Parse(content, ConfigPath);

        // CSS-CreateCustomCommands'daki Commands/ klasor destegi gibi ek .toml dosyalarini da yukle
        if (config.Settings.LoadExtraCommandFiles && Directory.Exists(ExtraCommandsDir))
        {
            var seenIds = new HashSet<string>(config.Commands.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(ExtraCommandsDir, "*.toml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var extraToml = File.ReadAllText(file, Encoding.UTF8);
                    var extraSyntax = Toml.Parse(extraToml, file);
                    if (extraSyntax.HasErrors)
                    {
                        Warnings.Add($"Ek dosya '{Path.GetFileName(file)}' sözdizimi hatası içeriyor, atlandı.");
                        continue;
                    }
                    var extraModel = extraSyntax.ToModel();
                    ParseCommandsArray(extraModel, config, seenIds, Path.GetFileName(file));
                }
                catch (Exception ex)
                {
                    Warnings.Add($"Ek dosya '{Path.GetFileName(file)}' okunamadı: {ex.Message}");
                }
            }
        }

        return config;
    }

    public void Save(PluginConfig config)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ==============================================================================");
        sb.AppendLine("#  CustomCommands - Özel Komut, Çıktı ve Sunucu/Oyuncu Komut Sistemi (SwiftlyS2)");
        sb.AppendLine("#  Görsel Admin Menüsü : !komutmenu (veya !ccmenu)");
        sb.AppendLine("#  Oyuncu Komut Listesi: !komutlar  (veya !commands, !yardim)");
        sb.AppendLine("#  Hızlı Komutlar      : !komutekle, !komutsil, !komutcalistir, !komutreload");
        sb.AppendLine("# ==============================================================================");
        sb.AppendLine();

        var st = config.Settings;
        sb.AppendLine("[settings]");
        sb.AppendLine($"enabled = {B(st.Enabled)}");
        sb.AppendLine($"enable_prefix = {B(st.EnablePrefix)}");
        sb.AppendLine($"prefix = {Q(st.Prefix)}");
        sb.AppendLine($"hide_chat_triggers = {B(st.HideChatTriggers)}");
        sb.AppendLine($"default_html_duration_ms = {st.DefaultHtmlDurationMs}");
        sb.AppendLine($"default_cooldown_seconds = {st.DefaultCooldownSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"root_bypasses_permissions = {B(st.RootBypassesPermissions)}");
        sb.AppendLine($"show_no_permission_message = {B(st.ShowNoPermissionMessage)}");
        sb.AppendLine($"no_permission_message = {Q(st.NoPermissionMessage)}");
        sb.AppendLine($"show_cooldown_message = {B(st.ShowCooldownMessage)}");
        sb.AppendLine($"cooldown_message = {Q(st.CooldownMessage)}");
        sb.AppendLine($"only_alive_message = {Q(st.OnlyAliveMessage)}");
        sb.AppendLine($"only_dead_message = {Q(st.OnlyDeadMessage)}");
        sb.AppendLine($"wrong_team_message = {Q(st.WrongTeamMessage)}");
        sb.AppendLine($"server_name_override = {Q(st.ServerNameOverride)}");
        sb.AppendLine($"server_domain = {Q(st.ServerDomain)}");
        sb.AppendLine($"discord_invite = {Q(st.DiscordInvite)}");
        sb.AppendLine($"website_url = {Q(st.WebsiteUrl)}");
        sb.AppendLine($"load_extra_command_files = {B(st.LoadExtraCommandFiles)}");
        sb.AppendLine($"freeze_while_menu_open = {B(st.FreezeWhileMenuOpen)}");
        sb.AppendLine($"log_to_console = {B(st.LogToConsole)}");
        sb.AppendLine($"debug = {B(st.Debug)}");
        sb.AppendLine();

        var ac = config.AdminCommands;
        sb.AppendLine("[admin_commands]");
        sb.AppendLine($"menu_command = {Q(ac.MenuCommand)}");
        sb.AppendLine($"list_command = {Q(ac.ListCommand)}");
        sb.AppendLine($"add_command = {Q(ac.AddCommand)}");
        sb.AppendLine($"remove_command = {Q(ac.RemoveCommand)}");
        sb.AppendLine($"run_command = {Q(ac.RunCommand)}");
        sb.AppendLine($"reload_command = {Q(ac.ReloadCommand)}");
        sb.AppendLine($"admin_permission = {Q(ac.AdminPermission)}");
        sb.AppendLine();

        if (config.Commands.Count > 0)
        {
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("#  ÖZEL KOMUTLAR ([[commands]])");
            sb.AppendLine("# ==============================================================================");
            foreach (var cmd in config.Commands)
            {
                sb.AppendLine("[[commands]]");
                sb.AppendLine($"id = {Q(cmd.Id)}");
                if (!string.IsNullOrWhiteSpace(cmd.Title)) sb.AppendLine($"title = {Q(cmd.Title)}");
                if (!string.IsNullOrWhiteSpace(cmd.Description)) sb.AppendLine($"description = {Q(cmd.Description)}");
                sb.AppendLine($"enabled = {B(cmd.Enabled)}");
                if (!cmd.ShowInHelp) sb.AppendLine($"show_in_help = {B(cmd.ShowInHelp)}");
                sb.AppendLine($"triggers = [{string.Join(", ", cmd.Triggers.Select(Q))}]");

                if (!string.IsNullOrWhiteSpace(cmd.Permission))
                    sb.AppendLine($"permission = {Q(cmd.Permission)}");
                if (cmd.Permissions.Count > 0)
                    sb.AppendLine($"permissions = [{string.Join(", ", cmd.Permissions.Select(Q))}]");
                if (cmd.RequiresAllPermissions)
                    sb.AppendLine($"requires_all_permissions = {B(cmd.RequiresAllPermissions)}");
                if (!string.IsNullOrWhiteSpace(cmd.NoPermissionMessage))
                    sb.AppendLine($"no_permission_message = {Q(cmd.NoPermissionMessage)}");

                if (cmd.CooldownSeconds > 0f)
                    sb.AppendLine($"cooldown_seconds = {cmd.CooldownSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");
                if (cmd.GlobalCooldownSeconds > 0f)
                    sb.AppendLine($"global_cooldown_seconds = {cmd.GlobalCooldownSeconds.ToString("0.0", CultureInfo.InvariantCulture)}");

                if (cmd.Teams.Count > 0)
                    sb.AppendLine($"teams = [{string.Join(", ", cmd.Teams.Select(Q))}]");
                if (!string.IsNullOrWhiteSpace(cmd.AliveState) && !cmd.AliveState.Equals("any", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"alive_state = {Q(cmd.AliveState)}");
                if (cmd.Maps.Count > 0)
                    sb.AppendLine($"maps = [{string.Join(", ", cmd.Maps.Select(Q))}]");
                if (cmd.MinArgs > 0)
                    sb.AppendLine($"min_args = {cmd.MinArgs}");
                if (!string.IsNullOrWhiteSpace(cmd.Usage))
                    sb.AppendLine($"usage = {Q(cmd.Usage)}");

                if (cmd.HideChat.HasValue)
                    sb.AppendLine($"hide_chat = {B(cmd.HideChat.Value)}");
                if (cmd.UsePrefix.HasValue)
                    sb.AppendLine($"use_prefix = {B(cmd.UsePrefix.Value)}");

                sb.AppendLine($"target = {Q(cmd.Target)}");
                if (cmd.PrintTo.HasValue)
                    sb.AppendLine($"print_to = {cmd.PrintTo.Value}");

                if (!string.IsNullOrWhiteSpace(cmd.Chat))
                    sb.AppendLine($"chat = {Q(cmd.Chat)}");
                if (cmd.ChatLines.Count > 0)
                {
                    sb.AppendLine("chat_lines = [");
                    foreach (var line in cmd.ChatLines)
                        sb.AppendLine($"    {Q(line)},");
                    sb.AppendLine("]");
                }

                if (!string.IsNullOrWhiteSpace(cmd.Center))
                    sb.AppendLine($"center = {Q(cmd.Center)}");
                if (!string.IsNullOrWhiteSpace(cmd.Html))
                    sb.AppendLine($"html = {Q(cmd.Html)}");
                if (cmd.HtmlDurationMs.HasValue)
                    sb.AppendLine($"html_duration_ms = {cmd.HtmlDurationMs.Value}");
                if (!string.IsNullOrWhiteSpace(cmd.Alert))
                    sb.AppendLine($"alert = {Q(cmd.Alert)}");
                if (!string.IsNullOrWhiteSpace(cmd.Console))
                    sb.AppendLine($"console = {Q(cmd.Console)}");
                if (cmd.ConsoleLines.Count > 0)
                    sb.AppendLine($"console_lines = [{string.Join(", ", cmd.ConsoleLines.Select(Q))}]");

                if (cmd.ClientCommands.Count > 0)
                    sb.AppendLine($"client_commands = [{string.Join(", ", cmd.ClientCommands.Select(Q))}]");
                if (cmd.ServerCommands.Count > 0)
                    sb.AppendLine($"server_commands = [{string.Join(", ", cmd.ServerCommands.Select(Q))}]");
                if (!string.IsNullOrWhiteSpace(cmd.PlayerAction) && !cmd.PlayerAction.Equals("none", StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"player_action = {Q(cmd.PlayerAction)}");
                if (cmd.DelaySeconds > 0f)
                    sb.AppendLine($"delay_seconds = {cmd.DelaySeconds.ToString("0.0", CultureInfo.InvariantCulture)}");

                sb.AppendLine();
            }
        }

        var tmp = ConfigPath + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, ConfigPath, overwrite: true);
    }

    private PluginConfig Parse(string toml, string sourcePath)
    {
        var syntax = Toml.Parse(toml, sourcePath);
        if (syntax.HasErrors)
        {
            var errors = string.Join("; ", syntax.Diagnostics.Select(d => d.ToString()));
            throw new InvalidDataException("TOML sözdizimi hatası: " + errors);
        }

        var model = syntax.ToModel();
        var config = new PluginConfig();

        if (Table(model, "settings") is { } s)
        {
            var st = config.Settings;
            st.Enabled = Bool(s, "enabled", st.Enabled);
            st.EnablePrefix = Bool(s, "enable_prefix", st.EnablePrefix);
            st.Prefix = Str(s, "prefix") ?? st.Prefix;
            st.HideChatTriggers = Bool(s, "hide_chat_triggers", st.HideChatTriggers);
            st.DefaultHtmlDurationMs = Math.Clamp(Int(s, "default_html_duration_ms", st.DefaultHtmlDurationMs), 1000, 30000);
            st.DefaultCooldownSeconds = Math.Clamp((float)Double(s, "default_cooldown_seconds", st.DefaultCooldownSeconds), 0f, 3600f);
            st.RootBypassesPermissions = Bool(s, "root_bypasses_permissions", st.RootBypassesPermissions);
            st.ShowNoPermissionMessage = Bool(s, "show_no_permission_message", st.ShowNoPermissionMessage);
            st.NoPermissionMessage = Str(s, "no_permission_message") ?? st.NoPermissionMessage;
            st.ShowCooldownMessage = Bool(s, "show_cooldown_message", st.ShowCooldownMessage);
            st.CooldownMessage = Str(s, "cooldown_message") ?? st.CooldownMessage;
            st.OnlyAliveMessage = Str(s, "only_alive_message") ?? st.OnlyAliveMessage;
            st.OnlyDeadMessage = Str(s, "only_dead_message") ?? st.OnlyDeadMessage;
            st.WrongTeamMessage = Str(s, "wrong_team_message") ?? st.WrongTeamMessage;
            st.ServerNameOverride = Str(s, "server_name_override") ?? st.ServerNameOverride;
            st.ServerDomain = Str(s, "server_domain") ?? st.ServerDomain;
            st.DiscordInvite = Str(s, "discord_invite") ?? st.DiscordInvite;
            st.WebsiteUrl = Str(s, "website_url") ?? st.WebsiteUrl;
            st.LoadExtraCommandFiles = Bool(s, "load_extra_command_files", st.LoadExtraCommandFiles);
            st.FreezeWhileMenuOpen = Bool(s, "freeze_while_menu_open", st.FreezeWhileMenuOpen);
            st.LogToConsole = Bool(s, "log_to_console", st.LogToConsole);
            st.Debug = Bool(s, "debug", st.Debug);
        }

        var adminTable = Table(model, "admin_commands") ?? Table(model, "commands_settings");
        if (adminTable is { } ac)
        {
            var cm = config.AdminCommands;
            cm.MenuCommand = Str(ac, "menu_command") ?? cm.MenuCommand;
            cm.ListCommand = Str(ac, "list_command") ?? cm.ListCommand;
            cm.AddCommand = Str(ac, "add_command") ?? cm.AddCommand;
            cm.RemoveCommand = Str(ac, "remove_command") ?? cm.RemoveCommand;
            cm.RunCommand = Str(ac, "run_command") ?? cm.RunCommand;
            cm.ReloadCommand = Str(ac, "reload_command") ?? cm.ReloadCommand;
            cm.AdminPermission = Str(ac, "admin_permission") ?? cm.AdminPermission;
        }

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ParseCommandsArray(model, config, seenIds, Path.GetFileName(sourcePath));

        return config;
    }

    private void ParseCommandsArray(TomlTable model, PluginConfig config, HashSet<string> seenIds, string fileLabel)
    {
        if (!model.TryGetValue("commands", out var cmdsObj) || cmdsObj is not TomlTableArray cmdsArr)
            return;

        int idx = 0;
        foreach (var ct in cmdsArr)
        {
            idx++;
            var entry = new CustomCommandEntry();

            // Triggers: hem triggers = ["dc", "discord"] hem de command = "dc,discord" desteklenir
            var triggers = new List<string>();
            if (StrList(ct, "triggers") is { } tList)
                triggers.AddRange(tList);
            if (Str(ct, "command") is { Length: > 0 } cmdStr)
                triggers.AddRange(cmdStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            if (StrList(ct, "aliases") is { } aliasList)
                triggers.AddRange(aliasList);

            entry.Triggers = triggers
                .Select(NormalizeTrigger)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rawId = Str(ct, "id");
            if (string.IsNullOrWhiteSpace(rawId))
                rawId = entry.Triggers.FirstOrDefault() ?? $"cmd_{idx}";

            if (!seenIds.Add(rawId))
            {
                Warnings.Add($"[{fileLabel}] [[commands]] '{rawId}' birden fazla kez tanımlanmış, sonraki atlandı.");
                continue;
            }

            entry.Id = rawId;
            if (entry.Triggers.Count == 0)
                entry.Triggers.Add(NormalizeTrigger(rawId));

            entry.Title = Str(ct, "title") ?? Str(ct, "name") ?? entry.PrimaryTrigger;
            entry.Description = Str(ct, "description") ?? string.Empty;
            entry.Enabled = Bool(ct, "enabled", true);
            entry.ShowInHelp = Bool(ct, "show_in_help", true);

            entry.Permission = Str(ct, "permission");
            if (StrList(ct, "permissions") is { } perms)
                entry.Permissions = perms.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();
            else if (StrList(ct, "permission_list") is { } permList)
                entry.Permissions = permList.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).ToList();

            entry.RequiresAllPermissions = Bool(ct, "requires_all_permissions", false);
            entry.NoPermissionMessage = Str(ct, "no_permission_message");

            entry.CooldownSeconds = Math.Max(0f, (float)Double(ct, "cooldown_seconds", Double(ct, "cooldown", 0.0)));
            entry.GlobalCooldownSeconds = Math.Max(0f, (float)Double(ct, "global_cooldown_seconds", 0.0));

            if (StrList(ct, "teams") is { } teams)
                entry.Teams = teams.Select(NormalizeTeam).Where(x => x.Length > 0).ToList();

            entry.AliveState = NormalizeAliveState(Str(ct, "alive_state"));

            if (StrList(ct, "maps") is { } maps)
                entry.Maps = maps.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList();

            entry.MinArgs = Math.Max(0, Int(ct, "min_args", 0));
            entry.Usage = Str(ct, "usage");

            if (ct.ContainsKey("hide_chat"))
                entry.HideChat = Bool(ct, "hide_chat", true);
            if (ct.ContainsKey("use_prefix"))
                entry.UsePrefix = Bool(ct, "use_prefix", true);

            entry.Target = NormalizeTarget(Str(ct, "target") ?? Str(ct, "receiver"));
            if (ct.ContainsKey("print_to"))
                entry.PrintTo = Int(ct, "print_to", 0);

            entry.Chat = Str(ct, "chat");
            if (StrList(ct, "chat_lines") is { } chatLines)
                entry.ChatLines = chatLines;
            else if (StrList(ct, "message") is { } msgLines)
                entry.ChatLines = msgLines;

            entry.Center = Str(ct, "center");
            entry.Html = Str(ct, "html") ?? Str(ct, "center_html") ?? Str(ct, "center_message");
            if (ct.ContainsKey("html_duration_ms"))
                entry.HtmlDurationMs = Math.Clamp(Int(ct, "html_duration_ms", 5000), 500, 30000);
            else if (ct.ContainsKey("html_time_seconds"))
                entry.HtmlDurationMs = Math.Clamp(Int(ct, "html_time_seconds", 5) * 1000, 500, 30000);

            entry.Alert = Str(ct, "alert");
            entry.Console = Str(ct, "console");
            if (StrList(ct, "console_lines") is { } conLines)
                entry.ConsoleLines = conLines;

            // ClientCommands (Oyuncu adina calistirilacak komutlar: sw_bring @all vb.)
            var clientCmds = new List<string>();
            if (Str(ct, "client_command") is { Length: > 0 } singleClientCmd)
                clientCmds.Add(singleClientCmd);
            if (StrList(ct, "client_commands") is { } multiClientCmds)
                clientCmds.AddRange(multiClientCmds.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (StrList(ct, "player_commands") is { } multiPlayerCmds)
                clientCmds.AddRange(multiPlayerCmds.Where(x => !string.IsNullOrWhiteSpace(x)));
            entry.ClientCommands = clientCmds;

            // ServerCommands (Sunucu konsolunda calistirilacak komutlar: mp_restartgame 1 vb.)
            var serverCmds = new List<string>();
            if (Str(ct, "server_command") is { Length: > 0 } singleServerCmd)
                serverCmds.Add(singleServerCmd);
            if (StrList(ct, "server_commands") is { } multiServerCmds)
                serverCmds.AddRange(multiServerCmds.Where(x => !string.IsNullOrWhiteSpace(x)));
            entry.ServerCommands = serverCmds;

            entry.PlayerAction = Str(ct, "player_action") ?? Str(ct, "action");
            entry.DelaySeconds = Math.Clamp((float)Double(ct, "delay_seconds", 0.0), 0f, 300f);

            if (!entry.HasAnyActionOrOutput())
                Warnings.Add($"[{fileLabel}] [[commands]] '{entry.Id}': hiçbir çıktı, komut veya aksiyon tanımlanmamış.");

            config.Commands.Add(entry);
        }
    }

    public static string NormalizeTrigger(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim().TrimStart('!', '/', '.').Trim().ToLowerInvariant();
        if (s.StartsWith("sw_") && s.Length > 3)
            s = s[3..];
        if (s.StartsWith("css_") && s.Length > 4)
            s = s[4..];
        return s;
    }

    private static string NormalizeTeam(string t) => t.Trim().ToUpperInvariant() switch
    {
        "T" or "TERRORIST" or "TR" => "T",
        "CT" or "COUNTERTERRORIST" or "COUNTER-TERRORIST" => "CT",
        "SPEC" or "SPECTATOR" or "SPECTATORS" => "SPEC",
        _ => string.Empty
    };

    private static string NormalizeAliveState(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "alive" or "yasayan" or "canli" or "true" => "alive",
        "dead" or "olu" or "false" => "dead",
        _ => "any"
    };

    private static string NormalizeTarget(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "all" or "everyone" or "herkes" or "public" or "server" => "all",
        "team" or "takim" => "team",
        _ => "caller"
    };

    private static string B(bool v) => v ? "true" : "false";
    private static string Q(string? s) => $"\"{(s ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")}\"";

    private static TomlTable? Table(TomlTable model, string key) =>
        model.TryGetValue(key, out var o) ? o as TomlTable : null;

    private static string? Str(TomlTable t, string key) =>
        t.TryGetValue(key, out var o) && o != null ? Convert.ToString(o, CultureInfo.InvariantCulture) : null;

    private static bool Bool(TomlTable t, string key, bool fallback)
    {
        if (!t.TryGetValue(key, out var o) || o == null) return fallback;
        if (o is bool b) return b;
        return bool.TryParse(o.ToString(), out var r) ? r : fallback;
    }

    private static int Int(TomlTable t, string key, int fallback)
    {
        if (!t.TryGetValue(key, out var o) || o == null) return fallback;
        try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static double Double(TomlTable t, string key, double fallback)
    {
        if (!t.TryGetValue(key, out var o) || o == null) return fallback;
        try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return fallback; }
    }

    private static List<string>? StrList(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var o) || o == null) return null;
        if (o is TomlArray arr)
            return arr.Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToList();
        if (o is string s) return new List<string> { s };
        return null;
    }
}

