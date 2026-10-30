using System.Globalization;
using CustomCommands.Config;
using CustomCommands.Models;
using CustomCommands.Utils;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CustomCommands.Core;

/// <summary>
/// Ozel komutlarin yetki, bekleme suresi (cooldown), takim/hayatta olma kosullarini denetler;
/// Chat/HTML/Center/Alert/Console ciktilarini gonderir;
/// Oyuncu adina (ClientCommands -> caller.ExecuteCommand) ve Sunucu adina (ServerCommands -> Engine.ExecuteCommand)
/// komutlari ve dahili oyuncu aksiyonlarini (PlayerAction: suicide, spec, t, ct, respawn, freeze...) calistirir.
/// </summary>
public class CustomCommandService
{
    private const int MaxExecutionDepth = 4;

    private readonly ISwiftlyCore _core;
    private PluginConfig _config;
    private Func<int, bool>? _isMenuOpenPredicate;

    // Cooldown takibi: (CommandId, PlayerSlot) -> Son kullanim zamani (CurrentTime)
    private readonly Dictionary<(string CmdId, int Slot), float> _playerCooldowns = new();
    private readonly Dictionary<string, float> _globalCooldowns = new(StringComparer.OrdinalIgnoreCase);

    // Sonsuz döngü (self-trigger / recursion) koruması ve gecikmeli zamanlayıcı takibi
    private readonly HashSet<string> _executingCommandIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CancellationTokenSource> _pendingTimers = new();
    private int _executionDepth;

    public CustomCommandService(ISwiftlyCore core, PluginConfig config)
    {
        _core = core;
        _config = config;
    }

    public void SetMenuOpenPredicate(Func<int, bool> predicate) => _isMenuOpenPredicate = predicate;

    public void UpdateConfig(PluginConfig config) => _config = config;

    public void ClearCooldowns()
    {
        _playerCooldowns.Clear();
        _globalCooldowns.Clear();
        CancelPendingTimers();
    }

    public void CancelPendingTimers()
    {
        foreach (var cts in _pendingTimers)
        {
            try { cts.Cancel(); cts.Dispose(); } catch { /* yoksay */ }
        }
        _pendingTimers.Clear();
        _executingCommandIds.Clear();
        _executionDepth = 0;
    }

    public void OnPlayerDisconnected(int slot)
    {
        var keys = _playerCooldowns.Keys.Where(k => k.Slot == slot).ToList();
        foreach (var k in keys)
            _playerCooldowns.Remove(k);
    }

    // ========================== LOOKUP ==========================

    public CustomCommandEntry? FindByTrigger(string? rawTrigger)
    {
        var norm = ConfigLoader.NormalizeTrigger(rawTrigger);
        if (string.IsNullOrEmpty(norm)) return null;

        return _config.Commands.FirstOrDefault(c =>
            c.Enabled &&
            c.Triggers.Any(t => t.Equals(norm, StringComparison.OrdinalIgnoreCase)));
    }

    public CustomCommandEntry? FindByIdOrTrigger(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.Trim();
        var norm = ConfigLoader.NormalizeTrigger(q);

        return _config.Commands.FirstOrDefault(c => c.Id.Equals(q, StringComparison.OrdinalIgnoreCase))
            ?? _config.Commands.FirstOrDefault(c => c.Triggers.Any(t => t.Equals(norm, StringComparison.OrdinalIgnoreCase)));
    }

    // ========================== PERMISSIONS ==========================

    public bool HasAdminPermission(IPlayer? player)
    {
        if (!PlayerHelper.IsUsable(player)) return true; // Sunucu konsolu
        var perm = _config.AdminCommands.AdminPermission;
        if (string.IsNullOrWhiteSpace(perm)) return true;

        var sid = PlayerHelper.GetSteamId64(player);
        if (sid == 0) return false;

        try
        {
            return _core.Permission.PlayerHasPermission(sid, perm) ||
                   _core.Permission.PlayerHasPermission(sid, "admin") ||
                   _core.Permission.PlayerHasPermission(sid, "root");
        }
        catch
        {
            return false;
        }
    }

    public bool HasCommandPermission(IPlayer? player, CustomCommandEntry cmd)
    {
        if (!PlayerHelper.IsUsable(player)) return true; // Sunucu konsolu

        var perms = cmd.GetAllPermissions();
        if (perms.Count == 0) return true;

        ulong sid = PlayerHelper.GetSteamId64(player);
        if (sid == 0) return false;

        bool isRootOrAdmin = false;
        if (_config.Settings.RootBypassesPermissions)
        {
            try
            {
                isRootOrAdmin = _core.Permission.PlayerHasPermission(sid, "root") ||
                                _core.Permission.PlayerHasPermission(sid, "admin") ||
                                (!string.IsNullOrWhiteSpace(_config.AdminCommands.AdminPermission) &&
                                 _core.Permission.PlayerHasPermission(sid, _config.AdminCommands.AdminPermission));
            }
            catch { isRootOrAdmin = false; }
        }

        bool CheckSingle(string rawFilter)
        {
            var filter = rawFilter.Trim();
            bool negate = filter.StartsWith('!');
            if (negate) filter = filter[1..].Trim();
            if (string.IsNullOrEmpty(filter)) return true;

            // Pozitif yetki kontrolunde admin/root her zaman gecsin (negatif "!vip" gibi filtreler haric)
            if (!negate && isRootOrAdmin) return true;

            bool has = CheckPlayerPermissionRaw(sid, filter);
            return negate ? !has : has;
        }

        return cmd.RequiresAllPermissions
            ? perms.All(CheckSingle)
            : perms.Any(CheckSingle);
    }

    private bool CheckPlayerPermissionRaw(ulong sid, string filter)
    {
        try
        {
            if (_core.Permission.PlayerHasPermission(sid, filter))
                return true;

            // CSS-CreateCustomCommands uyumu: "@css/vip" veya "@custom/vip" yazildiysa "vip" olarak da dene
            if (filter.StartsWith('@'))
            {
                var trimmed = filter.TrimStart('@');
                int slash = trimmed.IndexOf('/');
                var shortPerm = slash >= 0 && slash + 1 < trimmed.Length ? trimmed[(slash + 1)..] : trimmed;
                if (!string.IsNullOrWhiteSpace(shortPerm) && _core.Permission.PlayerHasPermission(sid, shortPerm))
                    return true;
            }
        }
        catch { /* yoksay */ }

        return false;
    }

    // ========================== EXECUTION ==========================

    public bool Execute(
        IPlayer? caller,
        CustomCommandEntry cmd,
        string usedTrigger,
        string[] args,
        ICommandContext? ctx = null,
        bool bypassChecks = false)
    {
        if (!_config.Settings.Enabled && !bypassChecks)
            return false;

        if (!cmd.Enabled && !bypassChecks)
            return false;

        // Sonsuz döngü (self-trigger / recursion) koruması
        if (_executionDepth >= MaxExecutionDepth || _executingCommandIds.Contains(cmd.Id))
        {
            if (_config.Settings.Debug)
            {
                _core.Logger.LogWarning(
                    "[CustomCommands] Özyinelemeli (recursive) komut tetiklemesi engellendi: {Id} (depth: {Depth})",
                    cmd.Id, _executionDepth);
            }
            return false;
        }

        bool isPlayer = PlayerHelper.IsUsable(caller);

        if (!bypassChecks)
        {
            // 1) Harita kontrolu
            string currentMap = PlayerHelper.SafeMapName(_core);
            if (!cmd.MatchesMap(currentMap))
            {
                SendDirectFeedback(caller, ctx, $"[lightred]Bu komut şu anki haritada ({currentMap}) kullanılamaz.[default]");
                return false;
            }

            // 2) Yetki kontrolu
            if (!HasCommandPermission(caller, cmd))
            {
                if (_config.Settings.ShowNoPermissionMessage)
                {
                    var noPermMsg = !string.IsNullOrWhiteSpace(cmd.NoPermissionMessage)
                        ? cmd.NoPermissionMessage!
                        : _config.Settings.NoPermissionMessage;
                    SendDirectFeedback(caller, ctx, noPermMsg);
                }
                return false;
            }

            if (isPlayer)
            {
                // 3) Takim kontrolu
                var team = PlayerHelper.GetTeam(caller);
                if (!cmd.MatchesTeam(team))
                {
                    SendDirectFeedback(caller, ctx, _config.Settings.WrongTeamMessage);
                    return false;
                }

                // 4) Hayatta olma / Olu olma kontrolu
                bool alive = PlayerHelper.IsAlive(caller);
                if (cmd.AliveState.Equals("alive", StringComparison.OrdinalIgnoreCase) && !alive)
                {
                    SendDirectFeedback(caller, ctx, _config.Settings.OnlyAliveMessage);
                    return false;
                }
                if (cmd.AliveState.Equals("dead", StringComparison.OrdinalIgnoreCase) && alive)
                {
                    SendDirectFeedback(caller, ctx, _config.Settings.OnlyDeadMessage);
                    return false;
                }

                // 5) Cooldown (Bekleme suresi) kontrolu
                if (TryCheckAndSetCooldown(caller!, cmd, out float remainingSeconds))
                {
                    if (_config.Settings.ShowCooldownMessage && !string.IsNullOrWhiteSpace(_config.Settings.CooldownMessage))
                    {
                        var cdMsg = _config.Settings.CooldownMessage.Replace(
                            "{COOLDOWN}",
                            remainingSeconds.ToString("0.0", CultureInfo.InvariantCulture));
                        SendDirectFeedback(caller, ctx, cdMsg);
                    }
                    return false;
                }
            }

            // 6) Minimum arguman kontrolu
            if (cmd.MinArgs > 0 && args.Length < cmd.MinArgs)
            {
                var usageText = !string.IsNullOrWhiteSpace(cmd.Usage)
                    ? cmd.Usage!
                    : $"!{usedTrigger} " + string.Join(" ", Enumerable.Range(1, cmd.MinArgs).Select(i => $"<arg{i}>"));
                SendDirectFeedback(caller, ctx, $"Kullanım: [lime]{usageText}[default]");
                return false;
            }
        }

        var values = PlaceholderHelper.BuildValues(_core, _config, caller, usedTrigger, args);

        // 7) Mesaj Ciktilarini Gonder (Chat, ChatLines, Center, HTML, Alert, Console)
        DeliverOutputs(caller, ctx, cmd, values);

        // 8) Aksiyon ve Komutlari Calistir:
        // HookClientChat / CommandListener icerisinde senkron entity mutasyonu (CommitSuicide, ChangeTeam, Respawn)
        // veya ExecuteCommand cagirmak CS2 sunucusunu cokertebildiginden her zaman NextWorldUpdate / DelayBySeconds ile calistir.
        if (HasExecutableWork(cmd))
        {
            int callerSlot = isPlayer ? caller!.Slot : -1;
            if (cmd.DelaySeconds > 0.05f)
            {
                CancellationTokenSource? cts = null;
                cts = _core.Scheduler.DelayBySeconds(cmd.DelaySeconds, () =>
                {
                    if (cts != null) _pendingTimers.Remove(cts);
                    var delayedPlayer = callerSlot >= 0 ? _core.PlayerManager.GetPlayer(callerSlot) : null;
                    RunActionsAndCommandsGuarded(delayedPlayer, cmd, values);
                });
                if (cts != null) _pendingTimers.Add(cts);
            }
            else
            {
                _core.Scheduler.NextWorldUpdate(() =>
                {
                    var nextTickPlayer = callerSlot >= 0 ? _core.PlayerManager.GetPlayer(callerSlot) : null;
                    RunActionsAndCommandsGuarded(nextTickPlayer, cmd, values);
                });
            }
        }

        if (_config.Settings.LogToConsole)
        {
            var who = isPlayer ? $"{PlayerHelper.GetName(caller)} ({PlayerHelper.GetSteamId64(caller)})" : "Konsol";
            _core.Logger.LogInformation(
                "[CustomCommands] '{Who}' -> !{Trigger} {Args} (id: {Id})",
                who, usedTrigger, string.Join(" ", args), cmd.Id);
        }

        return true;
    }

    private static bool HasExecutableWork(CustomCommandEntry cmd) =>
        (!string.IsNullOrWhiteSpace(cmd.PlayerAction) && !cmd.PlayerAction.Equals("none", StringComparison.OrdinalIgnoreCase)) ||
        cmd.ClientCommands.Count > 0 ||
        cmd.ServerCommands.Count > 0;

    private bool TryCheckAndSetCooldown(IPlayer player, CustomCommandEntry cmd, out float remaining)
    {
        remaining = 0f;
        float now = PlayerHelper.SafeCurrentTime(_core);

        if (cmd.GlobalCooldownSeconds > 0f)
        {
            if (_globalCooldowns.TryGetValue(cmd.Id, out float lastGlobal))
            {
                float diff = now - lastGlobal;
                if (diff >= 0f && diff < cmd.GlobalCooldownSeconds)
                {
                    remaining = cmd.GlobalCooldownSeconds - diff;
                    return true;
                }
            }
        }

        float playerCd = cmd.CooldownSeconds > 0f ? cmd.CooldownSeconds : _config.Settings.DefaultCooldownSeconds;
        if (playerCd > 0f)
        {
            var key = (cmd.Id, player.Slot);
            if (_playerCooldowns.TryGetValue(key, out float lastPlayer))
            {
                float diff = now - lastPlayer;
                if (diff >= 0f && diff < playerCd)
                {
                    remaining = playerCd - diff;
                    return true;
                }
            }
            _playerCooldowns[key] = now;
        }

        if (cmd.GlobalCooldownSeconds > 0f)
            _globalCooldowns[cmd.Id] = now;

        return false;
    }

    // ========================== OUTPUT DELIVERY ==========================

    private void DeliverOutputs(
        IPlayer? caller,
        ICommandContext? ctx,
        CustomCommandEntry cmd,
        IReadOnlyDictionary<string, string> baseValues)
    {
        bool usePrefix = cmd.UsePrefix ?? _config.Settings.EnablePrefix;
        int htmlDuration = cmd.HtmlDurationMs ?? _config.Settings.DefaultHtmlDurationMs;

        // Hedef kitlenin belirlenmesi (PrintTo 0..7 varsa CSS-CreateCustomCommands uyumlu, yoksa Target: caller/all/team)
        var (chatTargets, centerTargets) = ResolveTargets(caller, cmd);

        // Eger konsoldan cagrildiysa ve chat/console ciktisi varsa konsola da yazdir
        if (!PlayerHelper.IsUsable(caller) && ctx != null)
        {
            foreach (var rawLine in CollectChatLines(cmd))
            {
                var filled = PlaceholderHelper.Apply(rawLine, baseValues);
                ctx.Reply("[CustomCommands] " + ColorHelper.StripAll(filled));
            }
        }

        // 1) Chat & Console mesajlari
        var chatLines = CollectChatLines(cmd);
        var consoleLines = CollectConsoleLines(cmd);

        if (chatLines.Count > 0 || consoleLines.Count > 0)
        {
            foreach (var target in chatTargets)
            {
                if (!PlayerHelper.IsUsable(target)) continue;
                var team = PlayerHelper.GetTeam(target);

                try
                {
                    foreach (var rawLine in chatLines)
                    {
                        var filled = PlaceholderHelper.Apply(rawLine, baseValues);
                        var withPrefix = PrependPrefixIfEnabled(filled, usePrefix);
                        var colored = Helper.Colored(ColorHelper.NormalizeTags(withPrefix, team));
                        target.SendChat(colored);
                    }

                    foreach (var rawCon in consoleLines)
                    {
                        var filled = ColorHelper.StripAll(PlaceholderHelper.Apply(rawCon, baseValues));
                        target.SendConsole(filled);
                    }
                }
                catch { /* yoksay */ }
            }
        }

        // 2) Center, CenterHTML ve Alert mesajlari
        bool hasCenter = !string.IsNullOrWhiteSpace(cmd.Center);
        bool hasHtml = !string.IsNullOrWhiteSpace(cmd.Html);
        bool hasAlert = !string.IsNullOrWhiteSpace(cmd.Alert);

        if (hasCenter || hasHtml || hasAlert)
        {
            foreach (var target in centerTargets)
            {
                if (!PlayerHelper.IsUsable(target)) continue;

                try
                {
                    if (hasCenter)
                    {
                        var centerText = ColorHelper.StripAll(PlaceholderHelper.Apply(cmd.Center, baseValues));
                        if (!string.IsNullOrWhiteSpace(centerText))
                            target.SendCenter(centerText);
                    }

                    if (hasHtml && !IsMenuOpen(target.Slot))
                    {
                        var htmlText = PlaceholderHelper.Apply(cmd.Html, baseValues);
                        if (!string.IsNullOrWhiteSpace(htmlText))
                            target.SendCenterHTML(htmlText, htmlDuration);
                    }

                    if (hasAlert)
                    {
                        var alertText = ColorHelper.StripAll(PlaceholderHelper.Apply(cmd.Alert, baseValues));
                        if (!string.IsNullOrWhiteSpace(alertText))
                            target.SendAlert(alertText);
                    }
                }
                catch { /* yoksay */ }
            }
        }
    }

    private (List<IPlayer> ChatTargets, List<IPlayer> CenterTargets) ResolveTargets(IPlayer? caller, CustomCommandEntry cmd)
    {
        var allHumans = GetEligibleHumanPlayers();
        var callerList = PlayerHelper.IsUsable(caller) ? new List<IPlayer> { caller! } : new List<IPlayer>();

        if (cmd.PrintTo.HasValue)
        {
            return cmd.PrintTo.Value switch
            {
                0 => (callerList, new List<IPlayer>()),
                1 => (allHumans, new List<IPlayer>()),
                2 => (new List<IPlayer>(), callerList),
                3 => (new List<IPlayer>(), allHumans),
                4 => (callerList, callerList),
                5 => (callerList, allHumans),
                6 => (allHumans, callerList),
                7 => (allHumans, allHumans),
                _ => (callerList, callerList)
            };
        }

        var normTarget = (cmd.Target ?? "caller").Trim().ToLowerInvariant();
        if (normTarget == "all")
            return (allHumans, allHumans);

        if (normTarget == "team" && PlayerHelper.IsUsable(caller))
        {
            var callerTeam = PlayerHelper.GetTeam(caller);
            var teamMates = allHumans.Where(p => PlayerHelper.GetTeam(p) == callerTeam).ToList();
            return (teamMates, teamMates);
        }

        return (callerList, callerList);
    }

    private static List<string> CollectChatLines(CustomCommandEntry cmd)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(cmd.Chat))
        {
            var split = cmd.Chat.Replace("\\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            result.AddRange(split.Select(s => s.Trim()).Where(s => s.Length > 0));
        }
        foreach (var line in cmd.ChatLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var split = line.Replace("\\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            result.AddRange(split.Select(s => s.Trim()).Where(s => s.Length > 0));
        }
        return result;
    }

    private static List<string> CollectConsoleLines(CustomCommandEntry cmd)
    {
        var result = new List<string>();
        if (!string.IsNullOrWhiteSpace(cmd.Console))
        {
            var split = cmd.Console.Replace("\\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            result.AddRange(split);
        }
        foreach (var line in cmd.ConsoleLines)
        {
            if (!string.IsNullOrWhiteSpace(line))
                result.Add(line);
        }
        return result;
    }

    // ========================== ACTIONS & COMMANDS ==========================

    private void RunActionsAndCommandsGuarded(
        IPlayer? caller,
        CustomCommandEntry cmd,
        IReadOnlyDictionary<string, string> values)
    {
        if (_executionDepth >= MaxExecutionDepth || !_executingCommandIds.Add(cmd.Id))
            return;

        _executionDepth++;
        try
        {
            RunActionsAndCommands(caller, cmd, values);
        }
        finally
        {
            _executionDepth = Math.Max(0, _executionDepth - 1);
            _executingCommandIds.Remove(cmd.Id);
        }
    }

    private void RunActionsAndCommands(
        IPlayer? caller,
        CustomCommandEntry cmd,
        IReadOnlyDictionary<string, string> values)
    {
        // 1) Dahili Oyuncu Aksiyonu (PlayerAction: suicide, spec, t, ct, respawn, strip, freeze, hp:100...)
        if (!string.IsNullOrWhiteSpace(cmd.PlayerAction) &&
            !cmd.PlayerAction.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            PlayerHelper.IsUsable(caller))
        {
            ExecutePlayerAction(caller!, cmd.PlayerAction!);
        }

        // 2) Oyuncu Adina Komut Calistirma (ClientCommands -> caller.ExecuteCommand, orn: "sw_bring @all")
        if (cmd.ClientCommands.Count > 0)
        {
            foreach (var rawCmd in cmd.ClientCommands)
            {
                if (string.IsNullOrWhiteSpace(rawCmd)) continue;
                var expanded = PlaceholderHelper.ApplyForCommand(rawCmd, values).Trim();
                if (expanded.Length == 0) continue;

                // Eger kullanici basina ! veya / koyarak yazdiysa (orn: "!bring @all"), konsol komutu formatina ("sw_bring @all") normalize et
                expanded = NormalizeExecutableCommand(expanded);
                if (IsSelfTrigger(expanded, cmd)) continue;

                try
                {
                    if (PlayerHelper.IsUsable(caller))
                    {
                        caller!.ExecuteCommand(expanded);
                    }
                    else
                    {
                        _core.Engine.ExecuteCommand(expanded);
                    }
                }
                catch (Exception ex)
                {
                    if (_config.Settings.Debug)
                        _core.Logger.LogWarning(ex, "[CustomCommands] ClientCommand çalıştırılamadı: {Cmd}", expanded);
                }
            }
        }

        // 3) Sunucu Konsolunda Komut Calistirma (ServerCommands -> Core.Engine.ExecuteCommand)
        if (cmd.ServerCommands.Count > 0)
        {
            foreach (var rawCmd in cmd.ServerCommands)
            {
                if (string.IsNullOrWhiteSpace(rawCmd)) continue;
                var expanded = PlaceholderHelper.ApplyForCommand(rawCmd, values).Trim();
                if (expanded.Length == 0) continue;

                expanded = NormalizeExecutableCommand(expanded);
                if (IsSelfTrigger(expanded, cmd)) continue;

                try
                {
                    _core.Engine.ExecuteCommand(expanded);
                }
                catch (Exception ex)
                {
                    if (_config.Settings.Debug)
                        _core.Logger.LogWarning(ex, "[CustomCommands] ServerCommand çalıştırılamadı: {Cmd}", expanded);
                }
            }
        }
    }

    private static bool IsSelfTrigger(string expandedCommand, CustomCommandEntry cmd)
    {
        if (string.IsNullOrWhiteSpace(expandedCommand)) return false;
        int spaceIdx = expandedCommand.IndexOf(' ');
        var firstToken = spaceIdx > 0 ? expandedCommand[..spaceIdx] : expandedCommand;
        var normToken = ConfigLoader.NormalizeTrigger(firstToken);
        return cmd.Triggers.Any(t => t.Equals(normToken, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeExecutableCommand(string cmd)
    {
        if (cmd.StartsWith('!') || cmd.StartsWith('/'))
        {
            var withoutPrefix = cmd[1..].TrimStart();
            if (!withoutPrefix.StartsWith("sw_", StringComparison.OrdinalIgnoreCase) &&
                !withoutPrefix.StartsWith("css_", StringComparison.OrdinalIgnoreCase))
            {
                return "sw_" + withoutPrefix;
            }
            return withoutPrefix;
        }
        return cmd;
    }

    public void ExecutePlayerAction(IPlayer player, string rawAction)
    {
        if (!PlayerHelper.IsUsable(player)) return;
        var act = rawAction.Trim().ToLowerInvariant();

        try
        {
            switch (act)
            {
                case "suicide":
                case "kill":
                case "slay":
                case "intihar":
                    CommitPlayerSuicide(player);
                    break;

                case "spec":
                case "spectator":
                case "afk":
                case "izleyici":
                    if (PlayerHelper.GetTeam(player) != "SPEC")
                        player.ChangeTeam(Team.Spectator);
                    break;

                case "t":
                case "terrorist":
                case "tr":
                    if (PlayerHelper.GetTeam(player) != "T")
                        player.ChangeTeam(Team.T);
                    break;

                case "ct":
                case "counterterrorist":
                    if (PlayerHelper.GetTeam(player) != "CT")
                        player.ChangeTeam(Team.CT);
                    break;

                case "respawn":
                case "dogur":
                    if (PlayerHelper.GetTeam(player) is "T" or "CT")
                        player.Respawn();
                    break;

                case "strip":
                case "disarm":
                case "silahsil":
                    StripPlayerWeapons(player);
                    break;

                case "freeze":
                case "dondur":
                    SetPlayerMoveType(player, MoveType_t.MOVETYPE_NONE, zeroVelocity: true);
                    break;

                case "unfreeze":
                case "coz":
                    SetPlayerMoveType(player, MoveType_t.MOVETYPE_WALK, zeroVelocity: false);
                    break;

                case "noclip":
                    TogglePlayerNoclip(player);
                    break;

                default:
                    if ((act.StartsWith("hp:") || act.StartsWith("health:")) &&
                        int.TryParse(act[(act.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hpVal))
                    {
                        SetPlayerHealth(player, Math.Clamp(hpVal, 1, 10000));
                    }
                    else if (act.StartsWith("armor:") &&
                             int.TryParse(act[(act.IndexOf(':') + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int armorVal))
                    {
                        SetPlayerArmor(player, Math.Clamp(armorVal, 0, 1000));
                    }
                    else if (act.StartsWith("speed:") &&
                             float.TryParse(act[(act.IndexOf(':') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float speedVal))
                    {
                        SetPlayerSpeed(player, Math.Clamp(speedVal, 0.1f, 5.0f));
                    }
                    else if (act.StartsWith("gravity:") &&
                             float.TryParse(act[(act.IndexOf(':') + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float gravVal))
                    {
                        SetPlayerGravity(player, Math.Clamp(gravVal, 0.1f, 5.0f));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            if (_config.Settings.Debug)
                _core.Logger.LogWarning(ex, "[CustomCommands] PlayerAction '{Action}' uygulanırken hata oluştu.", rawAction);
        }
    }

    private static void CommitPlayerSuicide(IPlayer player)
    {
        if (!PlayerHelper.IsAlive(player)) return;

        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;

        try
        {
            pawn.TakesDamage = true;
            pawn.CommitSuicide(explode: false, force: true);
        }
        catch { /* yoksay */ }
    }

    private static void StripPlayerWeapons(IPlayer player)
    {
        if (!PlayerHelper.IsAlive(player)) return;

        var pawn = PlayerHelper.GetPlayerPawn(player);
        var itemServices = pawn?.ItemServices;
        if (itemServices is not { IsValid: true }) return;

        try
        {
            itemServices.RemoveItems();
        }
        catch { /* yoksay */ }
    }

    private static void SetPlayerMoveType(IPlayer player, MoveType_t moveType, bool zeroVelocity)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        pawn.MoveType = moveType;
        pawn.ActualMoveType = moveType;
        pawn.MoveTypeUpdated();
        if (zeroVelocity)
        {
            pawn.AbsVelocity.X = 0f;
            pawn.AbsVelocity.Y = 0f;
            pawn.AbsVelocity.Z = 0f;
        }
    }

    private static void TogglePlayerNoclip(IPlayer player)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        var next = pawn.MoveType == MoveType_t.MOVETYPE_NOCLIP
            ? MoveType_t.MOVETYPE_WALK
            : MoveType_t.MOVETYPE_NOCLIP;
        SetPlayerMoveType(player, next, zeroVelocity: false);
    }

    private static void SetPlayerHealth(IPlayer player, int hp)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        pawn.Health = hp;
        pawn.HealthUpdated();
    }

    private static void SetPlayerArmor(IPlayer player, int armor)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        pawn.ArmorValue = armor;
        pawn.ArmorValueUpdated();
    }

    private static void SetPlayerSpeed(IPlayer player, float speed)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        pawn.VelocityModifier = speed;
        pawn.VelocityModifierUpdated();
    }

    private static void SetPlayerGravity(IPlayer player, float gravity)
    {
        var pawn = PlayerHelper.GetPlayerPawn(player);
        if (pawn == null) return;
        pawn.GravityScale = gravity;
        pawn.GravityScaleUpdated();
    }

    // ========================== HELPERS ==========================

    public void Reply(IPlayer? player, string text)
    {
        if (!PlayerHelper.IsUsable(player)) return;
        try
        {
            var raw = PrependPrefixIfEnabled(text, _config.Settings.EnablePrefix);
            var line = Helper.Colored(ColorHelper.NormalizeTags(raw, PlayerHelper.GetTeam(player)));
            player!.SendChat(line);
        }
        catch { /* yoksay */ }
    }

    public void SendDirectFeedback(IPlayer? caller, ICommandContext? ctx, string message)
    {
        try
        {
            if (PlayerHelper.IsUsable(caller))
                Reply(caller, message);
            else
                ctx?.Reply("[CustomCommands] " + ColorHelper.StripAll(message));
        }
        catch { /* yoksay */ }
    }

    private string PrependPrefixIfEnabled(string message, bool usePrefix)
    {
        if (!usePrefix || string.IsNullOrWhiteSpace(_config.Settings.Prefix))
            return message;
        return $"{_config.Settings.Prefix} {message}";
    }

    private List<IPlayer> GetEligibleHumanPlayers()
    {
        try
        {
            return _core.PlayerManager.GetAllPlayers()
                .Where(p => PlayerHelper.IsUsable(p) && !p!.IsFakeClient)
                .Cast<IPlayer>()
                .ToList();
        }
        catch
        {
            return new List<IPlayer>();
        }
    }

    private bool IsMenuOpen(int slot) => _isMenuOpenPredicate?.Invoke(slot) ?? false;
}
