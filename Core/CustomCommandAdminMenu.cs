using System.Text.RegularExpressions;
using CustomCommands.Config;
using CustomCommands.Models;
using CustomCommands.Utils;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Menu;
using SwiftlyS2.Shared.Menu.Components;
using SwiftlyS2.Shared.Players;

namespace CustomCommands.Core;

/// <summary>
/// Adminler ve oyuncular icin SwiftlyS2 Core.Menu tabanli CustomCommands arayuzu (!komutmenu / !komutlar).
/// CustomHUD, CenterHTML ve Chat rendererlariyla tam uyumludur.
/// </summary>
public sealed class CustomCommandAdminMenu
{
    private const string MenuTag = "customcommands.menu";

    private static readonly Regex HtmlTagRegex = new("<[^>]*>", RegexOptions.Compiled);

    private static readonly string[] TargetValues = ["caller", "all", "team"];
    private static readonly string[] AliveValues = ["any", "alive", "dead"];
    private static readonly string[] PermissionPresets = ["", "admin", "vip", "!vip", "root"];

    private static readonly string[] ActionPresets =
    [
        "",
        "suicide",
        "spec",
        "t",
        "ct",
        "respawn",
        "strip",
        "freeze",
        "unfreeze",
        "noclip",
        "hp:100",
        "armor:100"
    ];

    private sealed class DangerSubmenuComponent : SubmenuComponent
    {
        public DangerSubmenuComponent(string text, Func<IMenuSession, IMenu?> factory, string? id = null)
            : base(text, factory, id)
        {
        }

        public override MenuNode Render(IMenuComponentRenderContext context)
        {
            var node = base.Render(context);
            var raw = new MenuRawNode("customhud.panorama", "danger");
            return node is MenuLineNode line
                ? line with { Children = [.. line.Children, raw] }
                : MenuLineNode.Of(node, raw);
        }
    }

    private sealed class PageState
    {
        public int? Focus { get; set; }
        public object? FocusKey { get; set; }
        public int RowsAt { get; set; }
        public List<IMenuComponent> Rows { get; } = [];
    }

    private readonly ISwiftlyCore _core;
    private readonly ConfigLoader _loader;
    private readonly CustomCommandService _service;
    private readonly Action _onCommandsChanged;
    private readonly Action<IPlayer> _onReloadRequested;
    private PluginConfig _config;

    private readonly Dictionary<int, List<IMenu>> _ownedMenus = new();
    private readonly Dictionary<int, long> _lastToggleTick = new();

    public CustomCommandAdminMenu(
        ISwiftlyCore core,
        ConfigLoader loader,
        PluginConfig config,
        CustomCommandService service,
        Action onCommandsChanged,
        Action<IPlayer> onReloadRequested)
    {
        _core = core;
        _loader = loader;
        _config = config;
        _service = service;
        _onCommandsChanged = onCommandsChanged;
        _onReloadRequested = onReloadRequested;
    }

    public void UpdateConfig(PluginConfig config) => _config = config;

    public bool IsMenuOpen(int slot)
    {
        var player = _core.PlayerManager.GetPlayer(slot);
        if (!PlayerHelper.IsUsable(player)) return false;

        return _core.Menu.GetSession(player!) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag;
    }

    public void Toggle(IPlayer admin)
    {
        if (!PlayerHelper.IsUsable(admin))
            return;

        long now = Environment.TickCount64;
        if (_lastToggleTick.TryGetValue(admin.Slot, out long prev) && now - prev < 150)
            return;
        _lastToggleTick[admin.Slot] = now;

        if (_core.Menu.GetSession(admin) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
        {
            Close(admin);
            return;
        }

        Open(admin);
    }

    public void Open(IPlayer admin)
    {
        if (!PlayerHelper.IsUsable(admin))
            return;

        Close(admin);
        var menu = BuildMainMenu(admin.Slot);
        menu.Open(admin);
    }

    public void OpenPlayerCommandMenu(IPlayer player, IReadOnlyList<CustomCommandEntry> visibleCommands)
    {
        if (!PlayerHelper.IsUsable(player))
            return;

        Close(player);

        int ownerSlot = player.Slot;
        var builder = _core.Menu.CreateMenu("customcommands.player")
            .WithTitle("ÖZEL KOMUTLAR")
            .AddHeader(new TextComponent($"Kullanılabilir {visibleCommands.Count} Komut • Çalıştırmak için seçin"));

        foreach (var entry in visibleCommands)
        {
            var cmd = entry;
            var rawDesc = !string.IsNullOrWhiteSpace(cmd.Description) ? cmd.Description : cmd.Label;
            var desc = SafePreview(rawDesc, 40);
            string text = $"{SafePreview(cmd.TriggersDisplay, 24)}\t{desc}";

            builder.Add(new ButtonComponent(text, ctx =>
            {
                _service.Execute(ctx.Player, cmd, cmd.PrimaryTrigger, Array.Empty<string>(), ctx: null);
                return ValueTask.CompletedTask;
            })
            {
                CloseOnSelect = true
            });
        }

        var menu = Track(ownerSlot, builder.Build());
        menu.Open(player);
    }

    public void Close(IPlayer admin)
    {
        if (!PlayerHelper.IsUsable(admin))
            return;

        if (_core.Menu.GetSession(admin) is { IsOpen: true, Menu.Tag: string tag } && tag == MenuTag)
        {
            _core.Menu.CloseFor(admin);
        }

        DisposeSlotMenus(admin.Slot);
    }

    public void OnPlayerDisconnected(int slot)
    {
        _lastToggleTick.Remove(slot);
        DisposeSlotMenus(slot);
    }

    public void CloseAll()
    {
        try { _core.Menu.CloseAll(); } catch { }

        foreach (int slot in _ownedMenus.Keys.ToList())
        {
            DisposeSlotMenus(slot);
        }
        _ownedMenus.Clear();
    }

    private IMenu Track(int slot, IMenu menu)
    {
        menu.Tag = MenuTag;
        if (!_ownedMenus.TryGetValue(slot, out var list))
        {
            list = new List<IMenu>();
            _ownedMenus[slot] = list;
        }
        list.Add(menu);
        return menu;
    }

    private void DisposeSlotMenus(int slot)
    {
        if (!_ownedMenus.TryGetValue(slot, out var list))
            return;

        foreach (var menu in list)
        {
            try { menu.Dispose(); } catch { }
        }
        list.Clear();
        _ownedMenus.Remove(slot);
    }

    // ============================== 1. ANA MENÜ ==============================

    private IMenu BuildMainMenu(int ownerSlot)
    {
        IMenu? mainMenu = null;
        var listMenu = BuildCommandsListMenu(ownerSlot);
        var templatesMenu = BuildTemplatesMenu(ownerSlot);
        var settingsMenu = BuildSettingsMenu(ownerSlot);

        var createInput = CreateNewCommandInput(ownerSlot, () =>
        {
            listMenu.Parent ??= mainMenu;
            return listMenu;
        });

        mainMenu = _core.Menu.CreateMenu("customcommands.admin")
            .WithTitle("CUSTOMCOMMANDS YÖNETİM")
            .AddHeader(new TextComponent
            {
                TextProvider = _ =>
                {
                    int activeCount = _config.Commands.Count(c => c.Enabled);
                    string state = _config.Settings.Enabled ? "AÇIK" : "KAPALI";
                    return $"{activeCount}/{_config.Commands.Count} aktif komut • Sistem: {state}";
                }
            })
            .Add(new SubmenuComponent("Özel Komutları Yönet", listMenu, "list")
            {
                TextProvider = _ => $"Özel Komutları Yönet\t{_config.Commands.Count} komut"
            })
            .Add(createInput)
            .Add(new SubmenuComponent("Hazır Şablonlardan Komut Ekle", templatesMenu, "templates"))
            .Add(new SubmenuComponent("Genel Ayarlar", settingsMenu, "settings")
            {
                TextProvider = _ => $"Genel Ayarlar\t{(_config.Settings.Enabled ? "AÇIK" : "KAPALI")}"
            })
            .Add(new ButtonComponent("Config Dosyasını Yeniden Yükle", ctx =>
            {
                _onReloadRequested(ctx.Player);
                ctx.Session.Invalidate();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, mainMenu);
    }

    // ============================== 2. KOMUT LİSTESİ ==============================

    private IMenu BuildCommandsListMenu(int ownerSlot)
    {
        var page = new PageState();
        IMenu? listMenu = null;

        var createInput = CreateNewCommandInput(ownerSlot, () => listMenu);

        var builder = _core.Menu.CreateMenu("customcommands.list")
            .WithTitle("ÖZEL KOMUT LİSTESİ")
            .AddHeader(new TextComponent("Düzenlemek veya test etmek istediğiniz komutu seçin"))
            .Add(createInput);

        page.RowsAt = 1;
        listMenu = Track(ownerSlot, builder.Build());

        void Populate()
        {
            var rows = new List<IMenuComponent>();
            foreach (var cmd in _config.Commands.ToList())
            {
                var entry = cmd;
                var permBadge = string.IsNullOrWhiteSpace(entry.Permission) && entry.Permissions.Count == 0
                    ? "Herkes"
                    : entry.Permission ?? string.Join(",", entry.Permissions);
                string statusText = $"{(entry.Enabled ? "AKTİF" : "KAPALI")} • {SafePreview(permBadge, 16)}";

                rows.Add(new SubmenuComponent(
                    $"{SafePreview(entry.TriggersDisplay, 22)}\t{statusText}",
                    _ => BuildCommandEditMenu(ownerSlot, entry, listMenu))
                {
                    Tag = entry.Id
                });
            }

            if (rows.Count == 0)
            {
                rows.Add(new TextComponent("Kayıtlı özel komut bulunmuyor"));
            }

            ReplaceRows(listMenu, page, rows);
        }

        listMenu.Opened += session =>
        {
            Populate();
            RestoreFocus(session, page);
        };

        listMenu.FocusChanged += session =>
        {
            page.Focus = session.FocusedIndex;
            page.FocusKey = (session.FocusedComponent as MenuComponentBase)?.Tag;
        };

        return listMenu;
    }

    private InputComponent CreateNewCommandInput(int ownerSlot, Func<IMenu?> listMenuProvider)
    {
        bool resetting = false;
        InputComponent? input = null;

        input = new InputComponent(
            "Yeni Özel Komut Oluştur",
            defaultValue: "",
            validator: raw =>
            {
                var parsed = ParseTriggers(raw);
                return parsed.Count > 0;
            },
            onChanged: change =>
            {
                if (resetting) return;

                var triggers = ParseTriggers(change.NewValue);
                if (triggers.Count == 0)
                    return;

                resetting = true;
                input?.SetValue(change.Session, "");
                resetting = false;

                var entry = CreateAndSaveCommand(change.Player, triggers);
                var player = change.Player;

                _core.Scheduler.NextWorldUpdate(() =>
                {
                    if (!PlayerHelper.IsUsable(player)) return;
                    var listMenu = listMenuProvider();
                    var editMenu = BuildCommandEditMenu(ownerSlot, entry, listMenu);
                    editMenu.Parent = listMenu ?? change.Session.Menu;
                    editMenu.Open(player);
                });
            },
            id: "create_new_cmd")
        {
            MaxLength = 128,
            EmptyText = "Sohbete Yaz",
            HintMessage = "[gold]Yeni komut tetikleyicisini sohbete yazın[default] (örn: [lime]dc[default] veya [lime]dc,discord[default])",
            InvalidText = "Geçersiz tetikleyici",
            AcceptedText = "Oluşturuldu"
        };

        return input;
    }

    private CustomCommandEntry CreateAndSaveCommand(IPlayer admin, List<string> triggers)
    {
        var id = NextUniqueId(triggers[0]);
        var entry = new CustomCommandEntry
        {
            Id = id,
            Title = $"!{triggers[0]} Komutu",
            Description = $"!{triggers[0]} özel komutu",
            Enabled = true,
            Triggers = triggers,
            Target = "caller",
            CooldownSeconds = 2.0f,
            Chat = $"[lime]!{triggers[0]}[default] komutu çalıştırıldı!"
        };

        _config.Commands.Add(entry);
        SaveAndApply(reRegisterCommands: true);
        _service.Reply(admin, $"[lime]{entry.TriggersDisplay}[default] komutu oluşturuldu! Şimdi çıktı ve aksiyonlarını düzenleyebilirsiniz.");
        return entry;
    }

    // ============================== 3. HAZIR ŞABLONLAR ==============================

    private IMenu BuildTemplatesMenu(int ownerSlot)
    {
        IMenu? templatesMenu = null;

        IMenu OpenTemplate(CustomCommandEntry template, IPlayer admin)
        {
            _config.Commands.Add(template);
            SaveAndApply(reRegisterCommands: true);
            _service.Reply(admin, $"[lime]{template.TriggersDisplay}[default] şablon komutu eklendi ve kaydedildi!");
            return BuildCommandEditMenu(ownerSlot, template, templatesMenu);
        }

        var builder = _core.Menu.CreateMenu("customcommands.templates")
            .WithTitle("HAZIR KOMUT ŞABLONLARI")
            .AddHeader(new TextComponent("Tek seçimle sunucuya hazır komut ekleyin"))
            .Add(new SubmenuComponent("!dc / !discord\tDiscord Davet", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("discord"),
                Title = "Discord Sunucusu",
                Description = "Discord davet adresini gösterir.",
                Triggers = ["dc", "discord"],
                Target = "caller",
                CooldownSeconds = 3.0f,
                Chat = "💬 [lime]Discord Sunucumuz:[default] [lightblue]{DISCORD}[default]",
                Html = "<font class='fontSize-m' color='#38BDF8'><b>💬 DISCORD</b></font><br><font class='fontSize-sm' color='#4ADE80'><b>{DISCORD}</b></font>"
            }, ctx.Player)))
            .Add(new SubmenuComponent("!kill / !intihar\tAnında İntihar", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("intihar"),
                Title = "İntihar Et (!kill)",
                Description = "Hayattayken intihar etmenizi sağlar.",
                Triggers = ["kill", "intihar", "slayme"],
                AliveState = "alive",
                CooldownSeconds = 5.0f,
                HideChat = true,
                Target = "caller",
                PlayerAction = "suicide",
                Chat = "💀 [lightred]İntihar ettiniz![default]"
            }, ctx.Player)))
            .Add(new SubmenuComponent("!gelherkes\tsw_bring @all", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("gel_herkes"),
                Title = "Herkesi Yanına Çek (!gelherkes)",
                Description = "Tüm oyuncuları komutu yazan yetkilinin yanına çeker.",
                Triggers = ["gelherkes", "herkesigetir", "bringall"],
                Permission = "admin",
                AliveState = "alive",
                CooldownSeconds = 2.0f,
                HideChat = true,
                Target = "all",
                ClientCommands = ["sw_bring @all"],
                Chat = "⚡ Yetkili [lime]{NAME}[default] tüm oyuncuları yanına çekti!"
            }, ctx.Player)))
            .Add(new SubmenuComponent("!spec / !afk\tİzleyiciye Geç", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("izleyici_gec"),
                Title = "İzleyiciye Geç (!spec)",
                Description = "İzleyici takımına geçirir.",
                Triggers = ["spec", "afk", "izleyici"],
                CooldownSeconds = 3.0f,
                HideChat = true,
                Target = "caller",
                PlayerAction = "spec",
                Chat = "👁️ [lightblue]İzleyici takımına geçtiniz.[default]"
            }, ctx.Player)))
            .Add(new SubmenuComponent("!rr / !restart\tmp_restartgame 1", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("raunt_yenile"),
                Title = "Maçı Yeniden Başlat (!rr)",
                Description = "Maçı 1 saniye içinde yeniden başlatır.",
                Triggers = ["rr", "restart"],
                Permission = "admin",
                HideChat = true,
                Target = "all",
                ServerCommands = ["mp_restartgame 1"],
                Chat = "🔄 Yetkili [lime]{NAME}[default] maçı yeniden başlattı!"
            }, ctx.Player)))
            .Add(new SubmenuComponent("!hp / !heal\tVIP Can 100", ctx => OpenTemplate(new CustomCommandEntry
            {
                Id = NextUniqueId("vip_can"),
                Title = "VIP Can Doldur (!hp)",
                Description = "VIP oyuncuların canını 100 yapar.",
                Triggers = ["hp", "heal", "can"],
                Permission = "vip",
                AliveState = "alive",
                CooldownSeconds = 30.0f,
                HideChat = true,
                Target = "caller",
                PlayerAction = "hp:100",
                Chat = "🩺 [lime]Canınız 100 olarak yenilendi![default]"
            }, ctx.Player)));

        templatesMenu = Track(ownerSlot, builder.Build());
        return templatesMenu;
    }

    // ============================== 4. KOMUT DÜZENLEME ==============================

    private IMenu BuildCommandEditMenu(int ownerSlot, CustomCommandEntry cmd, IMenu? parentForDelete)
    {
        int targetIdx = Array.FindIndex(TargetValues, t => t.Equals(cmd.Target, StringComparison.OrdinalIgnoreCase));
        if (targetIdx < 0) targetIdx = 0;

        int aliveIdx = Array.FindIndex(AliveValues, a => a.Equals(cmd.AliveState, StringComparison.OrdinalIgnoreCase));
        if (aliveIdx < 0) aliveIdx = 0;

        int permIdx = Array.FindIndex(PermissionPresets, p => p.Equals(cmd.Permission ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (permIdx < 0) permIdx = 0;

        int actIdx = Array.FindIndex(ActionPresets, a => a.Equals(cmd.PlayerAction ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        if (actIdx < 0) actIdx = 0;

        int teamPresetIdx = GetTeamPresetIndex(cmd.Teams);

        int hideChatIdx = cmd.HideChat switch
        {
            true => 1,
            false => 2,
            _ => 0
        };

        bool syncingPerm = false;
        bool syncingAction = false;
        bool syncingHtml = false;
        bool syncingChat = false;
        bool syncingCenter = false;
        bool syncingClient = false;
        bool syncingServer = false;

        SelectorComponent<string>? permSelector = null;
        InputComponent? permInput = null;
        SelectorComponent<string>? actionSelector = null;
        InputComponent? actionInput = null;
        InputComponent? chatInput = null;
        InputComponent? htmlInput = null;
        InputComponent? centerInput = null;
        InputComponent? clientCmdInput = null;
        InputComponent? serverCmdInput = null;

        permSelector = new SelectorComponent<string>(
            "Yetki Ön Ayarı",
            PermissionPresets,
            permIdx,
            change =>
            {
                if (syncingPerm) return;
                syncingPerm = true;
                cmd.Permission = string.IsNullOrEmpty(change.NewValue) ? null : change.NewValue;
                permInput?.SetValue(change.Session, cmd.Permission ?? "");
                syncingPerm = false;
                SaveAndApply();
            })
        {
            DefaultValueProvider = _ =>
            {
                int i = Array.FindIndex(PermissionPresets, p => p.Equals(cmd.Permission ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : 0;
            },
            Formatter = v => v switch
            {
                "" => "Herkes",
                "!vip" => "!vip (VIP Hariç)",
                _ => v
            }
        };

        permInput = new InputComponent(
            "Özel Yetki",
            defaultValue: cmd.Permission ?? "",
            onChanged: change =>
            {
                if (syncingPerm) return;
                syncingPerm = true;
                var trimmed = change.NewValue.Trim();
                cmd.Permission = trimmed == "-" ? null : trimmed;
                permInput?.SetValue(change.Session, SafePreview(cmd.Permission, 32));
                int matchIdx = Array.FindIndex(PermissionPresets, p => p.Equals(cmd.Permission ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                permSelector?.SetValue(change.Session, matchIdx >= 0 ? matchIdx : 0);
                syncingPerm = false;
                SaveAndApply();
                _service.Reply(change.Player, $"Yetki güncellendi: [lime]{cmd.Permission ?? "Herkes"}[default]");
            })
        {
            MaxLength = 64,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(cmd.Permission, 32),
            HintMessage = "[gold]Gereken yetkiyi yazın[default] (örn: [lime]admin[default], [lime]vip[default], [lime]!vip[default] veya kaldırmak için [lightred]-[default])"
        };

        actionSelector = new SelectorComponent<string>(
            "Dahili Aksiyon Ön Ayarı",
            ActionPresets,
            actIdx,
            change =>
            {
                if (syncingAction) return;
                syncingAction = true;
                cmd.PlayerAction = string.IsNullOrEmpty(change.NewValue) ? null : change.NewValue;
                actionInput?.SetValue(change.Session, cmd.PlayerAction ?? "");
                syncingAction = false;
                SaveAndApply();
            })
        {
            DefaultValueProvider = _ =>
            {
                int i = Array.FindIndex(ActionPresets, a => a.Equals(cmd.PlayerAction ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                return i >= 0 ? i : 0;
            },
            Formatter = v => v switch
            {
                "" => "Yok",
                "suicide" => "İntihar (suicide)",
                "spec" => "İzleyici (spec)",
                "t" => "T Takımı (t)",
                "ct" => "CT Takımı (ct)",
                "respawn" => "Doğur (respawn)",
                "strip" => "Silah Sil (strip)",
                "freeze" => "Dondur (freeze)",
                "unfreeze" => "Çöz (unfreeze)",
                "noclip" => "Noclip Aç/Kapat",
                "hp:100" => "Can 100 (hp:100)",
                "armor:100" => "Zırh 100 (armor:100)",
                _ => v
            }
        };

        actionInput = new InputComponent(
            "Özel Oyuncu Aksiyonu",
            defaultValue: cmd.PlayerAction ?? "",
            onChanged: change =>
            {
                if (syncingAction) return;
                syncingAction = true;
                var trimmed = change.NewValue.Trim();
                cmd.PlayerAction = trimmed == "-" ? null : trimmed;
                actionInput?.SetValue(change.Session, SafePreview(cmd.PlayerAction, 32));
                int matchIdx = Array.FindIndex(ActionPresets, a => a.Equals(cmd.PlayerAction ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                actionSelector?.SetValue(change.Session, matchIdx >= 0 ? matchIdx : 0);
                syncingAction = false;
                SaveAndApply();
                _service.Reply(change.Player, $"Oyuncu aksiyonu güncellendi: [lime]{cmd.PlayerAction ?? "Yok"}[default]");
            })
        {
            MaxLength = 64,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(cmd.PlayerAction, 32),
            HintMessage = "[gold]Oyuncu aksiyonunu yazın[default] (örn: [lime]suicide, spec, hp:100, speed:1.5[default] veya kaldırmak için [lightred]-[default])"
        };

        chatInput = new InputComponent(
            "Chat Mesajı",
            defaultValue: "",
            onChanged: change =>
            {
                if (syncingChat) return;
                syncingChat = true;
                var val = change.NewValue.Trim();
                cmd.ChatLines.Clear();
                cmd.Chat = val == "-" ? null : change.NewValue;
                chatInput?.SetValue(change.Session, SafePreview(cmd.Chat));
                syncingChat = false;
                SaveAndApply();
                _service.Reply(change.Player, "Chat mesajı güncellendi.");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(cmd.Chat ?? cmd.ChatLines.FirstOrDefault()),
            HintMessage = "[gold]Chat mesajını yazın[default] (kaldırmak için [lightred]-[default] yazın)"
        };

        htmlInput = new InputComponent(
            "CenterHTML Mesajı",
            defaultValue: "",
            onChanged: change =>
            {
                if (syncingHtml) return;
                syncingHtml = true;
                var val = change.NewValue.Trim();
                cmd.Html = val == "-" ? null : change.NewValue;
                htmlInput?.SetValue(change.Session, SafePreview(cmd.Html));
                syncingHtml = false;
                SaveAndApply();
                _service.Reply(change.Player, "CenterHTML mesajı güncellendi.");
            })
        {
            MaxLength = 512,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(cmd.Html),
            HintMessage = "[gold]CenterHTML mesajını yazın[default] (kaldırmak için [lightred]-[default] yazın)"
        };

        centerInput = new InputComponent(
            "Center Mesajı",
            defaultValue: "",
            onChanged: change =>
            {
                if (syncingCenter) return;
                syncingCenter = true;
                var val = change.NewValue.Trim();
                cmd.Center = val == "-" ? null : change.NewValue;
                centerInput?.SetValue(change.Session, SafePreview(cmd.Center));
                syncingCenter = false;
                SaveAndApply();
                _service.Reply(change.Player, "Center mesajı güncellendi.");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(cmd.Center),
            HintMessage = "[gold]Center mesajını yazın[default] (kaldırmak için [lightred]-[default] yazın)"
        };

        clientCmdInput = new InputComponent(
            "Oyuncu Komutu (client_commands)",
            defaultValue: "",
            onChanged: change =>
            {
                if (syncingClient) return;
                syncingClient = true;
                var val = change.NewValue.Trim();
                cmd.ClientCommands = val == "-"
                    ? []
                    : change.NewValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                clientCmdInput?.SetValue(change.Session, SafePreview(string.Join(" ; ", cmd.ClientCommands)));
                syncingClient = false;
                SaveAndApply();
                _service.Reply(change.Player, "Oyuncu komutu (client_commands) güncellendi.");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(string.Join(" ; ", cmd.ClientCommands)),
            HintMessage = "[gold]Oyuncuya çalıştırılacak komutu yazın[default] ([lime];[default] ile birden fazla, silmek için [lightred]-[default])"
        };

        serverCmdInput = new InputComponent(
            "Sunucu Komutu (server_commands)",
            defaultValue: "",
            onChanged: change =>
            {
                if (syncingServer) return;
                syncingServer = true;
                var val = change.NewValue.Trim();
                cmd.ServerCommands = val == "-"
                    ? []
                    : change.NewValue.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                serverCmdInput?.SetValue(change.Session, SafePreview(string.Join(" ; ", cmd.ServerCommands)));
                syncingServer = false;
                SaveAndApply();
                _service.Reply(change.Player, "Sunucu komutu (server_commands) güncellendi.");
            })
        {
            MaxLength = 256,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(string.Join(" ; ", cmd.ServerCommands)),
            HintMessage = "[gold]Sunucu konsolunda çalıştırılacak komutu yazın[default] ([lime];[default] ile birden fazla, silmek için [lightred]-[default])"
        };

        var editMenu = _core.Menu.CreateMenu("customcommands.edit")
            .WithTitle($"KOMUT: {SafePreview(cmd.TriggersDisplay, 24)}")
            .AddHeader(new TextComponent
            {
                TextProvider = _ => $"{SafePreview(cmd.Label, 24)} • {cmd.SummaryTag()}"
            })
            .Add(new ToggleComponent("Durum (Aktif)", cmd.Enabled, change =>
            {
                cmd.Enabled = change.NewValue;
                SaveAndApply(reRegisterCommands: true);
            })
            {
                DefaultValueProvider = _ => cmd.Enabled
            })
            .Add(new ButtonComponent("Şimdi Test Et / Çalıştır", ctx =>
            {
                CloseTree(ctx.Session);
                _service.Execute(ctx.Player, cmd, cmd.PrimaryTrigger, Array.Empty<string>(), bypassChecks: true);
                return ValueTask.CompletedTask;
            }))
            .Add(new InputComponent(
                "Tetikleyiciler",
                defaultValue: string.Join(", ", cmd.Triggers),
                validator: raw => ParseTriggers(raw).Count > 0,
                onChanged: change =>
                {
                    var list = ParseTriggers(change.NewValue);
                    if (list.Count > 0)
                    {
                        cmd.Triggers = list;
                        SaveAndApply(reRegisterCommands: true);
                        _service.Reply(change.Player, $"Tetikleyiciler güncellendi: [lime]{cmd.TriggersDisplay}[default]");
                    }
                })
            {
                MaxLength = 128,
                DefaultValueProvider = _ => string.Join(", ", cmd.Triggers),
                HintMessage = "[gold]Komut tetikleyicilerini virgülle yazın[default] (örn: [lime]dc, discord[default])"
            })
            .Add(permSelector)
            .Add(permInput)
            .Add(new SelectorComponent<string>("Çıktı Hedefi (Target)", TargetValues, targetIdx, change =>
            {
                cmd.Target = change.NewValue;
                cmd.PrintTo = null;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ =>
                {
                    int i = Array.FindIndex(TargetValues, t => t.Equals(cmd.Target, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : 0;
                },
                Formatter = v => v switch
                {
                    "all" => "Tüm Sunucu",
                    "team" => "Sadece Takımı",
                    _ => "Sadece Yazan"
                }
            })
            .Add(chatInput)
            .Add(htmlInput)
            .Add(centerInput)
            .Add(clientCmdInput)
            .Add(serverCmdInput)
            .Add(actionSelector)
            .Add(actionInput)
            .Add(new SelectorComponent<string>("Hayatta Olma Şartı", AliveValues, aliveIdx, change =>
            {
                cmd.AliveState = change.NewValue;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ =>
                {
                    int i = Array.FindIndex(AliveValues, a => a.Equals(cmd.AliveState, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : 0;
                },
                Formatter = v => v switch
                {
                    "alive" => "Sadece Yaşayan",
                    "dead" => "Sadece Ölü",
                    _ => "Farketmez"
                }
            })
            .Add(new SelectorComponent<int>("Takım Filtresi", [0, 1, 2, 3], teamPresetIdx, change =>
            {
                cmd.Teams = change.NewValue switch
                {
                    1 => ["T"],
                    2 => ["CT"],
                    3 => ["SPEC"],
                    _ => []
                };
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => GetTeamPresetIndex(cmd.Teams),
                Formatter = idx => idx switch
                {
                    1 => "Sadece T",
                    2 => "Sadece CT",
                    3 => "Sadece İzleyici",
                    _ => "Herkes"
                }
            })
            .Add(new SliderComponent("Bekleme Süresi (Cooldown)", 0f, 120f, cmd.CooldownSeconds, 1f, change =>
            {
                cmd.CooldownSeconds = MathF.Round(change.NewValue, 1);
                SaveAndApply();
            })
            {
                TotalBars = 10,
                ValueFormat = "0 sn",
                DefaultValueProvider = _ => Math.Clamp(cmd.CooldownSeconds, 0f, 120f)
            })
            .Add(new SelectorComponent<int>("Sohbette Gizle (hide_chat)", [0, 1, 2], hideChatIdx, change =>
            {
                cmd.HideChat = change.NewValue switch
                {
                    1 => true,
                    2 => false,
                    _ => null
                };
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => cmd.HideChat switch { true => 1, false => 2, _ => 0 },
                Formatter = idx => idx switch
                {
                    1 => "EVET (Gizle)",
                    2 => "HAYIR (Göster)",
                    _ => "Genel Ayar"
                }
            })
            .Add(new InputComponent("Başlık (Title)", defaultValue: SafePreview(cmd.Label, 32), onChanged: change =>
            {
                cmd.Title = change.NewValue.Trim();
                SaveAndApply();
                _service.Reply(change.Player, $"Başlık [lime]{cmd.Title}[default] olarak güncellendi.");
            })
            {
                MaxLength = 64,
                DefaultValueProvider = _ => SafePreview(cmd.Label, 32),
                HintMessage = "[gold]Yeni komut başlığını sohbete yazın[default]"
            })
            .Add(new DangerSubmenuComponent("Bu Komutu Sil", _ => BuildConfirmDeleteMenu(ownerSlot, cmd, parentForDelete), "delete"))
            .Build();

        return Track(ownerSlot, editMenu);
    }

    private IMenu BuildConfirmDeleteMenu(int ownerSlot, CustomCommandEntry cmd, IMenu? parentToJumpBack)
    {
        IMenu? confirmMenu = null;

        confirmMenu = _core.Menu.CreateMenu("customcommands.confirm_delete")
            .WithTitle($"SİL: {SafePreview(cmd.TriggersDisplay, 24)}")
            .AddHeader(new TextComponent($"{SafePreview(cmd.Label, 28)} komutu kalıcı olarak silinecek."))
            .Add(new ButtonComponent("Evet, Kalıcı Olarak Sil", ctx =>
            {
                _config.Commands.Remove(cmd);
                SaveAndApply(reRegisterCommands: true);
                _service.Reply(ctx.Player, $"[lightred]'{cmd.TriggersDisplay}' komutu silindi.[default]");
                if (confirmMenu != null && parentToJumpBack != null)
                {
                    confirmMenu.Parent = parentToJumpBack;
                }
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            })
            {
                FocusedColor = "#F87171"
            })
            .Add(new ButtonComponent("Vazgeç", ctx =>
            {
                ctx.Session.Close();
                return ValueTask.CompletedTask;
            }))
            .Build();

        return Track(ownerSlot, confirmMenu);
    }

    // ============================== 5. GENEL AYARLAR ==============================

    private IMenu BuildSettingsMenu(int ownerSlot)
    {
        var s = _config.Settings;
        bool syncingPrefix = false;
        InputComponent? prefixInput = null;

        prefixInput = new InputComponent("Ön Ek Metni", defaultValue: SafePreview(s.Prefix, 32), onChanged: change =>
        {
            if (syncingPrefix) return;
            syncingPrefix = true;
            s.Prefix = change.NewValue.Trim();
            prefixInput?.SetValue(change.Session, SafePreview(s.Prefix, 32));
            syncingPrefix = false;
            SaveAndApply();
            _service.Reply(change.Player, $"Ön ek güncellendi: {s.Prefix}");
        })
        {
            MaxLength = 64,
            EmptyText = "Yok",
            DefaultValueProvider = _ => SafePreview(s.Prefix, 32),
            HintMessage = "[gold]Yeni ön eki sohbete yazın[default] (örn: [gold][Sunucu][default])"
        };

        var menu = _core.Menu.CreateMenu("customcommands.settings")
            .WithTitle("GENEL AYARLAR")
            .AddHeader(new TextComponent("Değişiklikler anında kaydedilir ve uygulanır"))
            .Add(new ToggleComponent("Özel Komut Sistemi", s.Enabled, change =>
            {
                s.Enabled = change.NewValue;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => s.Enabled
            })
            .Add(new ToggleComponent("Sohbet Ön Eki (enable_prefix)", s.EnablePrefix, change =>
            {
                s.EnablePrefix = change.NewValue;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => s.EnablePrefix
            })
            .Add(prefixInput)
            .Add(new ToggleComponent("Komutları Sohbette Gizle", s.HideChatTriggers, change =>
            {
                s.HideChatTriggers = change.NewValue;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => s.HideChatTriggers
            })
            .Add(new SliderComponent("Varsayılan Cooldown", 0f, 60f, s.DefaultCooldownSeconds, 0.5f, change =>
            {
                s.DefaultCooldownSeconds = MathF.Round(change.NewValue, 1);
                SaveAndApply();
            })
            {
                TotalBars = 10,
                ValueFormat = "0.0 sn",
                DefaultValueProvider = _ => Math.Clamp(s.DefaultCooldownSeconds, 0f, 60f)
            })
            .Add(new ToggleComponent("Admin/Root Yetkiden Muaf", s.RootBypassesPermissions, change =>
            {
                s.RootBypassesPermissions = change.NewValue;
                SaveAndApply();
            })
            {
                DefaultValueProvider = _ => s.RootBypassesPermissions
            })
            .Add(new InputComponent("Discord Adresi ({DISCORD})", defaultValue: SafePreview(s.DiscordInvite, 32), onChanged: change =>
            {
                s.DiscordInvite = change.NewValue.Trim();
                SaveAndApply();
                _service.Reply(change.Player, $"Discord adresi güncellendi: [lightblue]{s.DiscordInvite}[default]");
            })
            {
                MaxLength = 128,
                DefaultValueProvider = _ => SafePreview(s.DiscordInvite, 32),
                HintMessage = "[gold]Yeni Discord davet adresini sohbete yazın[default]"
            })
            .Add(new InputComponent("Web Sitesi ({WEBSITE})", defaultValue: SafePreview(s.WebsiteUrl, 32), onChanged: change =>
            {
                s.WebsiteUrl = change.NewValue.Trim();
                SaveAndApply();
                _service.Reply(change.Player, $"Web sitesi güncellendi: [lime]{s.WebsiteUrl}[default]");
            })
            {
                MaxLength = 128,
                DefaultValueProvider = _ => SafePreview(s.WebsiteUrl, 32),
                HintMessage = "[gold]Yeni Web sitesi adresini sohbete yazın[default]"
            })
            .Add(new InputComponent("Sunucu Domain ({DOMAIN})", defaultValue: SafePreview(s.ServerDomain, 32), onChanged: change =>
            {
                s.ServerDomain = change.NewValue.Trim();
                SaveAndApply();
                _service.Reply(change.Player, $"Domain güncellendi: [gold]{s.ServerDomain}[default]");
            })
            {
                MaxLength = 128,
                DefaultValueProvider = _ => SafePreview(s.ServerDomain, 32),
                HintMessage = "[gold]Yeni sunucu domain/IP adresini sohbete yazın[default]"
            })
            .Build();

        return Track(ownerSlot, menu);
    }

    // ============================== YARDIMCILAR ==============================

    private void SaveAndApply(bool reRegisterCommands = false)
    {
        try { _loader.Save(_config); }
        catch (Exception ex) { _core.Logger.LogError(ex, "[CustomCommands] Config kaydedilemedi."); }

        _service.UpdateConfig(_config);
        if (reRegisterCommands)
            _onCommandsChanged();
    }

    private string NextUniqueId(string baseId)
    {
        var id = baseId;
        int n = 2;
        while (_config.Commands.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            id = $"{baseId}_{n++}";
        return id;
    }

    private static List<string> ParseTriggers(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ConfigLoader.NormalizeTrigger)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetTeamPresetIndex(List<string> teams)
    {
        if (teams.Count == 0) return 0;
        if (teams.Count == 1)
        {
            if (teams[0].Equals("T", StringComparison.OrdinalIgnoreCase)) return 1;
            if (teams[0].Equals("CT", StringComparison.OrdinalIgnoreCase)) return 2;
            if (teams[0].Equals("SPEC", StringComparison.OrdinalIgnoreCase)) return 3;
        }
        return 0;
    }

    private static string SafePreview(string? text, int maxLen = 28)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var clean = ColorHelper.StripAll(text);
        clean = HtmlTagRegex.Replace(clean, " ");
        clean = clean.Replace("<", "").Replace(">", "").Replace("\r", " ").Replace("\n", " ").Trim();
        while (clean.Contains("  ", StringComparison.Ordinal))
            clean = clean.Replace("  ", " ", StringComparison.Ordinal);

        if (clean.Length == 0) return "(HTML)";
        return clean.Length > maxLen ? clean[..maxLen] + "..." : clean;
    }

    private static void CloseTree(IMenuSession session)
    {
        var menu = session.Menu;
        var parent = menu.Parent;
        menu.Parent = null;
        session.Close();
        menu.Parent = parent;
    }

    private static void ReplaceRows(IMenu menu, PageState page, IReadOnlyList<IMenuComponent> rows)
    {
        foreach (var row in page.Rows)
            menu.Remove(row);
        page.Rows.Clear();

        for (int i = 0; i < rows.Count; i++)
        {
            menu.Insert(MenuRegion.Body, page.RowsAt + i, rows[i]);
            page.Rows.Add(rows[i]);
        }
    }

    private static void RestoreFocus(IMenuSession session, PageState page)
    {
        if (page.FocusKey is { } key && FocusWhere(session, c => c is MenuComponentBase { Tag: { } tag } && tag.Equals(key)))
            return;

        if (page.Focus is { } focus)
        {
            for (int i = focus; i > 0; i--)
            {
                if (session.FocusedIndex == i || session.SetFocus(i))
                    return;
            }
        }
    }

    private static bool FocusWhere(IMenuSession session, Func<IMenuComponent, bool> match)
    {
        int index = 0;
        foreach (var component in session.Menu.GetComponents(MenuRegion.Body))
        {
            if (!component.IsFocusable || !component.IsVisible(session))
                continue;

            if (match(component))
            {
                if (session.FocusedIndex != index)
                    session.SetFocus(index);
                return true;
            }

            index++;
        }

        return false;
    }
}
