using CustomCommands.Config;
using CustomCommands.Core;
using CustomCommands.Models;
using CustomCommands.Utils;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Commands;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.Plugins;

namespace CustomCommands;

[PluginMetadata(
    Id = "customcommands",
    Version = "2.1.1",
    Name = "CustomCommands",
    Author = "surfevo",
    Description = "Yetki ve kosul bazli ozel sohbet/konsol komutlari, zengin mesaj ciktilari, oyuncu adina (client_commands) ve sunucu adina (server_commands) komut calistirma sistemi."
)]
public class Plugin(ISwiftlyCore core) : BasePlugin(core)
{
    private static readonly HashSet<string> EngineReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "kill", "explode", "say", "say_team", "quit", "exit", "disconnect",
        "retry", "status", "ping", "map", "changelevel", "exec", "bind", "unbind",
        "ip", "port", "hostport", "hostname", "server", "restart", "mp_restartgame",
        "sv_cheats", "help", "find", "cvarlist", "cmdlist", "alias", "echo", "wait",
        "noclip", "god", "buddha", "give", "drop", "use", "lastinv", "invnext", "invprev",
        "slot1", "slot2", "slot3", "slot4", "slot5", "team", "jointeam", "spectate",
        "buy", "autobuy", "rebuy", "radio", "ent_fire", "ent_create", "kick", "banid",
        "addip", "writeid", "writeip", "rcon", "password", "sv_password", "meta", "sw", "css"
    };

    private PluginConfig _config = new();
    private ConfigLoader? _configLoader;
    private CustomCommandService? _commandService;
    private CustomCommandAdminMenu? _adminMenu;

    private readonly List<Guid> _commandGuids = new();
    private Guid _chatHookGuid;
    private readonly Dictionary<int, float> _lastAdminCmdTime = new();
    private readonly Dictionary<(int Slot, string Key), float> _recentExecutions = new();
    private bool _reRegisterQueued;
    private bool _isUnloading;

    public override void Load(bool hotReload)
    {
        _isUnloading = false;
        var configDir = ResolveConfigDirectory();
        _configLoader = new ConfigLoader(configDir);

        try
        {
            _config = _configLoader.Load();
            LogConfigWarnings();
        }
        catch (Exception ex)
        {
            Core.Logger.LogError(ex, "[CustomCommands] Config yüklenemedi, varsayılan ayarlar kullanılıyor.");
            _config = new PluginConfig { Commands = PluginConfig.CreateDefaultCommands() };
        }

        _commandService = new CustomCommandService(Core, _config);
        _adminMenu = new CustomCommandAdminMenu(
            Core,
            _configLoader,
            _config,
            _commandService,
            ScheduleRegisterAllCommands,
            p => ExecuteReloadCommand(p, null));
        _commandService.SetMenuOpenPredicate(slot => _adminMenu.IsMenuOpen(slot));

        RegisterAllCommands();
        RegisterEvents();

        int activeCount = _config.Commands.Count(c => c.Enabled);
        Core.Logger.LogInformation(
            "[CustomCommands] v2.1.1 yüklendi ({Active}/{Total} aktif özel komut). Menü: !{MenuCmd} | Liste: !{ListCmd}",
            activeCount,
            _config.Commands.Count,
            _config.AdminCommands.MenuCommand,
            _config.AdminCommands.ListCommand);
    }

    public override void Unload()
    {
        _isUnloading = true;
        _adminMenu?.CloseAll();
        _commandService?.CancelPendingTimers();
        _commandService?.ClearCooldowns();
        UnregisterEvents();
        UnregisterAllCommands();

        Core.Logger.LogInformation("[CustomCommands] Eklenti kapatıldı.");
    }

    private string ResolveConfigDirectory()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(Core.Configuration.BasePath))
                return Core.Configuration.BasePath;
        }
        catch { /* fallback */ }

        return Path.Combine(Core.PluginPath, "CustomCommands");
    }

    private void LogConfigWarnings()
    {
        if (_configLoader == null) return;
        foreach (var w in _configLoader.Warnings)
            Core.Logger.LogWarning("[CustomCommands] Config uyarısı: {Warning}", w);
    }

    private void SaveAndRefreshAll()
    {
        if (_configLoader == null || _commandService == null) return;
        try { _configLoader.Save(_config); }
        catch (Exception ex) { Core.Logger.LogError(ex, "[CustomCommands] Config kaydedilemedi."); }

        _commandService.UpdateConfig(_config);
        _adminMenu?.UpdateConfig(_config);
        ScheduleRegisterAllCommands();
    }

    /// <summary>
    /// Komut callback'i calisirken senkron olarak UnregisterCommand/RegisterCommand cagirmak
    /// SwiftlyS2 / Source 2 ICvar tablosunu bozabildiginden yeniden kayit islemini bir sonraki world update'e erteler.
    /// </summary>
    private void ScheduleRegisterAllCommands()
    {
        if (_isUnloading || _reRegisterQueued) return;
        _reRegisterQueued = true;

        try
        {
            Core.Scheduler.NextWorldUpdate(() =>
            {
                _reRegisterQueued = false;
                if (_isUnloading) return;
                RegisterAllCommands();
            });
        }
        catch
        {
            _reRegisterQueued = false;
            if (!_isUnloading)
                RegisterAllCommands();
        }
    }

    // ============================== EVENTS ==============================

    private void RegisterEvents()
    {
        _chatHookGuid = Core.Command.HookClientChat(OnClientChat);

        Core.Event.OnTick += OnTick;
        Core.Event.OnClientKeyStateChanged += OnClientKeyStateChanged;
        Core.Event.OnClientDisconnected += OnClientDisconnected;
        Core.Event.OnMapLoad += OnMapLoad;
    }

    private void UnregisterEvents()
    {
        if (_chatHookGuid != Guid.Empty)
        {
            Core.Command.UnhookClientChat(_chatHookGuid);
            _chatHookGuid = Guid.Empty;
        }

        Core.Event.OnTick -= OnTick;
        Core.Event.OnClientKeyStateChanged -= OnClientKeyStateChanged;
        Core.Event.OnClientDisconnected -= OnClientDisconnected;
        Core.Event.OnMapLoad -= OnMapLoad;
    }

    private void OnTick() => _adminMenu?.OnTick();

    private void OnClientKeyStateChanged(IOnClientKeyStateChangedEvent e)
    {
        if (!e.Pressed || _adminMenu == null || !_adminMenu.IsMenuOpen(e.PlayerId)) return;
        var player = Core.PlayerManager.GetPlayer(e.PlayerId);
        if (PlayerHelper.IsUsable(player))
            _adminMenu.OnKeyPressed(player!, e.Key);
    }

    private HookResult OnClientChat(int playerId, string text, bool teamOnly)
    {
        if (_adminMenu != null && _adminMenu.TryConsumeChatInput(playerId, text))
            return HookResult.Stop;

        return TryHandleChatCommand(playerId, text);
    }

    private void OnClientDisconnected(IOnClientDisconnectedEvent e)
    {
        _lastAdminCmdTime.Remove(e.PlayerId);
        _adminMenu?.OnPlayerDisconnected(e.PlayerId);
        _commandService?.OnPlayerDisconnected(e.PlayerId);

        var keys = _recentExecutions.Keys.Where(k => k.Slot == e.PlayerId).ToList();
        foreach (var k in keys)
            _recentExecutions.Remove(k);
    }

    private void OnMapLoad(IOnMapLoadEvent e)
    {
        _lastAdminCmdTime.Clear();
        _recentExecutions.Clear();
        _adminMenu?.CloseAll();
        _commandService?.ClearCooldowns();
    }

    // ========================== COMMAND REGISTRATION ==========================

    private void RegisterAllCommands()
    {
        UnregisterAllCommands();
        var ac = _config.AdminCommands;

        var registeredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Yonetim ve Yardim komutlari
        RegisterDual(ac.MenuCommand, OnMenuCommand, "CustomCommands görsel admin menüsünü açar.", registeredNames);
        RegisterDual("ccmenu", OnMenuCommand, "CustomCommands görsel admin menüsünü açar.", registeredNames);

        RegisterDual(ac.ListCommand, OnListCommand, "Kullanılabilir özel komutları listeler.", registeredNames);
        RegisterDual("commands", OnListCommand, "Kullanılabilir özel komutları listeler.", registeredNames);

        RegisterDual(ac.AddCommand, OnAddCommand, "Hızlı özel komut ekler: !komutekle <komut> <tip:chat|client|server|action> \"<değer>\" [yetki]", registeredNames);
        RegisterDual(ac.RemoveCommand, OnRemoveCommand, "Özel komutu siler: !komutsil <id_veya_komut>", registeredNames);
        RegisterDual(ac.RunCommand, OnRunCommand, "Özel komutu test eder/çalıştırır: !komutcalistir <id_veya_komut>", registeredNames);
        RegisterDual(ac.ReloadCommand, OnReloadCommand, "CustomCommands yapılandırmasını yeniden yükler.", registeredNames);
        RegisterDual("ccreload", OnReloadCommand, "CustomCommands yapılandırmasını yeniden yükler.", registeredNames);

        // 2) Config icindeki dinamik [[commands]] tetikleyicileri
        foreach (var entry in _config.Commands.Where(c => c.Enabled))
        {
            var capturedEntry = entry;
            foreach (var trigger in entry.Triggers)
            {
                var norm = ConfigLoader.NormalizeTrigger(trigger);
                if (string.IsNullOrWhiteSpace(norm) || !registeredNames.Add(norm))
                    continue;

                var capturedTrigger = norm;
                var help = !string.IsNullOrWhiteSpace(capturedEntry.Description)
                    ? capturedEntry.Description
                    : capturedEntry.Label;

                RegisterDynamicTrigger(capturedTrigger, ctx =>
                {
                    OnDynamicCommandTriggered(ctx, capturedEntry, capturedTrigger);
                }, help);
            }
        }
    }

    private void RegisterDual(
        string? name,
        ICommandService.CommandListener handler,
        string help,
        HashSet<string> registeredNames)
    {
        var norm = ConfigLoader.NormalizeTrigger(name);
        if (string.IsNullOrWhiteSpace(norm) || !registeredNames.Add(norm)) return;

        try
        {
            // sw_<komut> olarak kaydet (registerRaw: false)
            if (!Core.Command.IsCommandRegistered("sw_" + norm))
            {
                _commandGuids.Add(Core.Command.RegisterCommand(norm, handler, registerRaw: false, helpText: help));
            }
        }
        catch (Exception ex)
        {
            Core.Logger.LogWarning(ex, "[CustomCommands] Komut kaydedilemedi: {Name}", norm);
        }
    }

    private void RegisterDynamicTrigger(string triggerName, ICommandService.CommandListener handler, string help)
    {
        try
        {
            // ONEMLI: Source 2 motorunun dahili ConVar/ConCommand tablosunu (orn: "ip", "restart", "server", "kill")
            // bozmamak ve diger eklentilerle cakismamak icin dinamik tetikleyicileri registerRaw: false (sw_<komut>) olarak kaydet.
            // Oyun ici "!<komut>" ve "/<komut>" tetiklemeleri zaten HookClientChat + SwiftlyS2 uzerinden calisir.
            if (!EngineReservedNames.Contains(triggerName) &&
                !Core.Command.IsCommandRegistered("sw_" + triggerName) &&
                !Core.Command.IsCommandRegistered(triggerName))
            {
                _commandGuids.Add(Core.Command.RegisterCommand(triggerName, handler, registerRaw: false, helpText: help));
            }
        }
        catch (Exception ex)
        {
            if (_config.Settings.Debug)
                Core.Logger.LogWarning(ex, "[CustomCommands] Dinamik komut kaydedilemedi: {Trigger}", triggerName);
        }
    }

    private void UnregisterAllCommands()
    {
        foreach (var g in _commandGuids)
        {
            try { Core.Command.UnregisterCommand(g); } catch { /* yoksay */ }
        }
        _commandGuids.Clear();
    }

    // ========================== CHAT & CONSOLE DISPATCH ==========================

    private void OnDynamicCommandTriggered(ICommandContext ctx, CustomCommandEntry entry, string trigger)
    {
        if (_commandService == null) return;

        var sender = ctx.Sender;
        int slot = PlayerHelper.IsUsable(sender) ? sender!.Slot : -1;
        if (IsDuplicateExecution(slot, entry.Id))
            return;

        var args = SplitArguments(string.Join(" ", ctx.Args)).ToArray();
        _commandService.Execute(sender, entry, trigger, args, ctx);
    }

    private HookResult TryHandleChatCommand(int playerId, string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Trim();

        if (trimmed.Length < 2 || (trimmed[0] != '!' && trimmed[0] != '/'))
            return HookResult.Continue;

        var args = SplitArguments(trimmed[1..]);
        if (args.Count == 0)
            return HookResult.Continue;

        var rawCmd = args[0];
        var cmd = ConfigLoader.NormalizeTrigger(rawCmd);
        var rest = args.Skip(1).ToArray();

        var player = Core.PlayerManager.GetPlayer(playerId);
        if (!PlayerHelper.IsUsable(player))
            return HookResult.Continue;

        var ac = _config.AdminCommands;

        // 1) Yonetim ve Yardim komutlari
        if (Matches(cmd, ac.MenuCommand, "ccmenu", "customcommands", "komutlarimenu"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__menu__"))
                ExecuteMenuCommand(player);
            return HookResult.Stop;
        }
        if (Matches(cmd, ac.ListCommand, "commands", "yardim", "help"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__list__"))
                ExecuteListCommand(player, null);
            return HookResult.Stop;
        }
        if (Matches(cmd, ac.AddCommand, "ccadd"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__add__"))
                ExecuteAddCommand(player, null, rest);
            return HookResult.Stop;
        }
        if (Matches(cmd, ac.RemoveCommand, "ccdel", "ccremove"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__remove__"))
                ExecuteRemoveCommand(player, null, rest);
            return HookResult.Stop;
        }
        if (Matches(cmd, ac.RunCommand, "ccrun", "cctest"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__run__"))
                ExecuteRunCommand(player, null, rest);
            return HookResult.Stop;
        }
        if (Matches(cmd, ac.ReloadCommand, "ccreload", "customcommands_reload"))
        {
            if (!IsDuplicateExecution(player!.Slot, "__reload__"))
                ExecuteReloadCommand(player, null);
            return HookResult.Stop;
        }

        // 2) Dinamik Ozel Komutlar ([[commands]])
        if (_commandService != null)
        {
            var entry = _commandService.FindByTrigger(cmd);
            if (entry != null)
            {
                if (!IsDuplicateExecution(player!.Slot, entry.Id))
                {
                    _commandService.Execute(player, entry, cmd, rest, ctx: null);
                }

                // "/" ile yazildiysa veya hide_chat aktifse mesaji sohbette gizle
                bool startedWithSlash = trimmed[0] == '/';
                bool hideChat = startedWithSlash || (entry.HideChat ?? _config.Settings.HideChatTriggers);
                return hideChat ? HookResult.Stop : HookResult.Continue;
            }
        }

        return HookResult.Continue;
    }

    private bool IsDuplicateExecution(int slot, string key)
    {
        var now = PlayerHelper.SafeCurrentTime(Core);
        var tuple = (slot, key);
        if (_recentExecutions.TryGetValue(tuple, out float last) && MathF.Abs(now - last) < 0.15f)
            return true;
        _recentExecutions[tuple] = now;
        return false;
    }

    private static bool Matches(string input, params string?[] names) =>
        names.Any(n => !string.IsNullOrWhiteSpace(n) && ConfigLoader.NormalizeTrigger(n).Equals(input, StringComparison.OrdinalIgnoreCase));

    private static List<string> SplitArguments(string commandLine)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return list;

        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;

        foreach (char ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (cur.Length > 0) { list.Add(cur.ToString()); cur.Clear(); }
            }
            else
            {
                cur.Append(ch);
            }
        }
        if (cur.Length > 0) list.Add(cur.ToString());
        return list;
    }

    private bool CheckAdminPermission(IPlayer? caller)
    {
        if (!PlayerHelper.IsUsable(caller)) return true; // Sunucu konsolu
        if (IsAdminThrottled(caller!)) return false;
        if (_commandService == null || !_commandService.HasAdminPermission(caller))
        {
            _commandService?.Reply(caller, _config.Settings.NoPermissionMessage);
            return false;
        }
        return true;
    }

    private bool IsAdminThrottled(IPlayer player)
    {
        var now = PlayerHelper.SafeCurrentTime(Core);
        if (_lastAdminCmdTime.TryGetValue(player.Slot, out var last) && MathF.Abs(now - last) < 0.2f)
            return true;
        _lastAdminCmdTime[player.Slot] = now;
        return false;
    }

    // ========================== BUILT-IN ADMIN & HELP COMMANDS ==========================

    // 1) !komutmenu / !ccmenu
    private void OnMenuCommand(ICommandContext ctx)
    {
        if (!PlayerHelper.IsUsable(ctx.Sender))
        {
            ctx.Reply("[CustomCommands] Görsel menü yalnızca oyun içinden (!komutmenu) açılabilir. Konsol komutları: sw_komutlar, sw_komutekle, sw_komutsil, sw_komutcalistir, sw_komutreload");
            return;
        }
        if (IsDuplicateExecution(ctx.Sender!.Slot, "__menu__")) return;
        ExecuteMenuCommand(ctx.Sender!);
    }

    private void ExecuteMenuCommand(IPlayer admin)
    {
        if (!CheckAdminPermission(admin)) return;
        _adminMenu?.Toggle(admin);
    }

    // 2) !komutlar / !commands (Tüm oyuncular kullanabilir)
    private void OnListCommand(ICommandContext ctx)
    {
        if (PlayerHelper.IsUsable(ctx.Sender) && IsDuplicateExecution(ctx.Sender!.Slot, "__list__"))
            return;
        ExecuteListCommand(ctx.Sender, ctx);
    }

    private void ExecuteListCommand(IPlayer? caller, ICommandContext? ctx)
    {
        if (_commandService == null) return;

        var visibleCommands = _config.Commands
            .Where(c => c.Enabled && c.ShowInHelp && _commandService.HasCommandPermission(caller, c))
            .ToList();

        if (visibleCommands.Count == 0)
        {
            _commandService.SendDirectFeedback(caller, ctx, "[grey]Kullanabileceğiniz aktif bir özel komut bulunmuyor.[default]");
            return;
        }

        _commandService.SendDirectFeedback(
            caller,
            ctx,
            $"[gold]══════════ KULLANILABİLİR KOMUTLAR ({visibleCommands.Count}) ══════════[default]");

        foreach (var c in visibleCommands)
        {
            var desc = !string.IsNullOrWhiteSpace(c.Description) ? c.Description : c.Label;
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"• [lime]{c.TriggersDisplay}[default] : {desc}");
        }
    }

    // 3) !komutekle <komut_adi> <tip:chat|client|server|action> "<değer>" [yetki]
    private void OnAddCommand(ICommandContext ctx)
    {
        if (PlayerHelper.IsUsable(ctx.Sender) && IsDuplicateExecution(ctx.Sender!.Slot, "__add__"))
            return;
        ExecuteAddCommand(ctx.Sender, ctx, SplitArguments(string.Join(" ", ctx.Args)).ToArray());
    }

    private void ExecuteAddCommand(IPlayer? caller, ICommandContext? ctx, string[] args)
    {
        if (!CheckAdminPermission(caller) || _commandService == null) return;

        if (args.Length < 3)
        {
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Kullanım: [lime]!{_config.AdminCommands.AddCommand} <komut> <chat|client|server|action> \"<değer>\" [yetki][default]");
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Örnek 1: [gold]!{_config.AdminCommands.AddCommand} dc chat \"Discord: {{DISCORD}}\"[default]");
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Örnek 2: [gold]!{_config.AdminCommands.AddCommand} gelherkes client \"sw_bring @all\" admin[default]");
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Örnek 3: [gold]!{_config.AdminCommands.AddCommand} kill action suicide[default]");
            return;
        }

        var triggers = args[0]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ConfigLoader.NormalizeTrigger)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (triggers.Count == 0)
        {
            _commandService.SendDirectFeedback(caller, ctx, "[lightred]Geçerli bir komut adı girmediniz.[default]");
            return;
        }

        var type = args[1].Trim().ToLowerInvariant();
        var value = args[2].Trim();
        var perm = args.Length >= 4 && args[3] != "-" ? args[3].Trim() : null;

        var primary = triggers[0];
        var existing = _config.Commands.FirstOrDefault(c =>
            c.Id.Equals(primary, StringComparison.OrdinalIgnoreCase) ||
            c.Triggers.Contains(primary, StringComparer.OrdinalIgnoreCase));

        bool isNew = existing == null;
        if (existing == null)
        {
            existing = new CustomCommandEntry
            {
                Id = primary,
                Title = $"!{primary} Komutu",
                Description = $"!{primary} özel komutu",
                Enabled = true,
                Triggers = triggers,
                Target = "caller",
                CooldownSeconds = 2.0f
            };
            _config.Commands.Add(existing);
        }
        else
        {
            foreach (var t in triggers)
            {
                if (!existing.Triggers.Contains(t, StringComparer.OrdinalIgnoreCase))
                    existing.Triggers.Add(t);
            }
        }

        if (perm != null)
            existing.Permission = perm;

        switch (type)
        {
            case "chat":
            case "mesaj":
            case "text":
                existing.Chat = value;
                break;

            case "html":
                existing.Html = value;
                break;

            case "center":
                existing.Center = value;
                break;

            case "client":
            case "player":
            case "oyuncu":
                existing.ClientCommands = value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                existing.HideChat = true;
                break;

            case "server":
            case "sunucu":
            case "rcon":
                existing.ServerCommands = value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                existing.HideChat = true;
                break;

            case "action":
            case "aksiyon":
                existing.PlayerAction = value;
                existing.HideChat = true;
                if (value.Equals("suicide", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("kill", StringComparison.OrdinalIgnoreCase))
                {
                    existing.AliveState = "alive";
                }
                break;

            default:
                _commandService.SendDirectFeedback(
                    caller,
                    ctx,
                    "[lightred]Geçersiz tip! Kullanılabilir tipler: [gold]chat, html, center, client, server, action[default]");
                if (isNew) _config.Commands.Remove(existing);
                return;
        }

        SaveAndRefreshAll();
        _commandService.SendDirectFeedback(
            caller,
            ctx,
            $"[lime]{existing.TriggersDisplay}[default] komutu ({existing.SummaryTag()}, yetki: [gold]{existing.Permission ?? "Herkes"}[default]) {(isNew ? "oluşturuldu" : "güncellendi")} ve kaydedildi!");
    }

    // 4) !komutsil <id_veya_komut>
    private void OnRemoveCommand(ICommandContext ctx)
    {
        if (PlayerHelper.IsUsable(ctx.Sender) && IsDuplicateExecution(ctx.Sender!.Slot, "__remove__"))
            return;
        ExecuteRemoveCommand(ctx.Sender, ctx, SplitArguments(string.Join(" ", ctx.Args)).ToArray());
    }

    private void ExecuteRemoveCommand(IPlayer? caller, ICommandContext? ctx, string[] args)
    {
        if (!CheckAdminPermission(caller) || _commandService == null) return;

        if (args.Length == 0)
        {
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Kullanım: [lime]!{_config.AdminCommands.RemoveCommand} <id_veya_komut>[default]");
            return;
        }

        var target = _commandService.FindByIdOrTrigger(args[0]);
        if (target == null)
        {
            _commandService.SendDirectFeedback(caller, ctx, $"[lightred]'{args[0]}' adında bir özel komut bulunamadı.[default]");
            return;
        }

        _config.Commands.Remove(target);
        SaveAndRefreshAll();
        _commandService.SendDirectFeedback(
            caller,
            ctx,
            $"[lime]{target.TriggersDisplay}[default] ({target.Id}) özel komutu silindi ve kaydedildi.");
    }

    // 5) !komutcalistir <id_veya_komut> [argümanlar...]
    private void OnRunCommand(ICommandContext ctx)
    {
        if (PlayerHelper.IsUsable(ctx.Sender) && IsDuplicateExecution(ctx.Sender!.Slot, "__run__"))
            return;
        ExecuteRunCommand(ctx.Sender, ctx, SplitArguments(string.Join(" ", ctx.Args)).ToArray());
    }

    private void ExecuteRunCommand(IPlayer? caller, ICommandContext? ctx, string[] args)
    {
        if (!CheckAdminPermission(caller) || _commandService == null) return;

        if (args.Length == 0)
        {
            _commandService.SendDirectFeedback(
                caller,
                ctx,
                $"Kullanım: [lime]!{_config.AdminCommands.RunCommand} <id_veya_komut> [argümanlar][default]");
            return;
        }

        var entry = _commandService.FindByIdOrTrigger(args[0]);
        if (entry == null)
        {
            _commandService.SendDirectFeedback(caller, ctx, $"[lightred]'{args[0]}' adında bir özel komut bulunamadı.[default]");
            return;
        }

        var passArgs = args.Skip(1).ToArray();
        _commandService.Execute(caller, entry, entry.PrimaryTrigger, passArgs, ctx, bypassChecks: true);
    }

    // 6) !komutreload / !ccreload
    private void OnReloadCommand(ICommandContext ctx)
    {
        if (PlayerHelper.IsUsable(ctx.Sender) && IsDuplicateExecution(ctx.Sender!.Slot, "__reload__"))
            return;
        ExecuteReloadCommand(ctx.Sender, ctx);
    }

    private void ExecuteReloadCommand(IPlayer? caller, ICommandContext? ctx)
    {
        if (!CheckAdminPermission(caller)) return;
        if (_configLoader == null || _commandService == null) return;

        try
        {
            var newConfig = _configLoader.Load();
            _config = newConfig;

            _commandService.UpdateConfig(_config);
            _commandService.ClearCooldowns();
            _adminMenu?.UpdateConfig(_config);

            ScheduleRegisterAllCommands();
            LogConfigWarnings();

            int activeCount = _config.Commands.Count(c => c.Enabled);
            var msg = $"Config yeniden yüklendi ({activeCount}/{_config.Commands.Count} aktif komut, {_configLoader.Warnings.Count} uyarı).";
            _commandService.SendDirectFeedback(caller, ctx, $"[lime]{msg}[default]");
            Core.Logger.LogInformation("[CustomCommands] {Message}", msg);
        }
        catch (Exception ex)
        {
            var err = $"Config yüklenemedi (eski ayarlar korundu): {ex.Message}";
            _commandService.SendDirectFeedback(caller, ctx, $"[lightred]{err}[default]");
            Core.Logger.LogError(ex, "[CustomCommands] Config reload hatası.");
        }
    }
}

