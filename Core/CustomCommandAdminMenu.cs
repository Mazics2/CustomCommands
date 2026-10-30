using System.Net;
using System.Text;
using CustomCommands.Config;
using CustomCommands.Models;
using CustomCommands.Utils;
using Microsoft.Extensions.Logging;
using SwiftlyS2.Shared;
using SwiftlyS2.Shared.Events;
using SwiftlyS2.Shared.Players;
using SwiftlyS2.Shared.SchemaDefinitions;

namespace CustomCommands.Core;

/// <summary>
/// Adminler icin oyun ici gorsel (CenterHTML) ozel komut yonetim menusu (!komutmenu / !ccmenu).
/// W/S: Yukari/Asagi | A/D: Deger degistir | E: Sec | R: Geri/Kapat
/// Metin girisi gerektiginde bir sonraki chat mesajini yakalar.
/// </summary>
public sealed class CustomCommandAdminMenu
{
    private const int RowsPerPage = 6;
    private const int HtmlDurationMs = 2000;
    private const float ResendSeconds = 0.25f;

    private static readonly string[] TargetValues = { "caller", "all", "team" };
    private static readonly string[] TargetLabels = { "Sadece Yazan", "Tüm Sunucu", "Sadece Takımı" };

    private static readonly string[] AliveValues = { "any", "alive", "dead" };
    private static readonly string[] AliveLabels = { "Farketmez", "Sadece Yaşayan", "Sadece Ölü" };

    private static readonly string[] PermissionPresets = { "", "admin", "vip", "!vip", "root" };
    private static readonly string[] PermissionPresetLabels = { "Herkes", "admin", "vip", "!vip (VIP Olmayan)", "root" };

    private static readonly string[] ActionPresets =
    {
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
    };

    private static readonly string[] ActionPresetLabels =
    {
        "Yok",
        "İntihar Et (suicide)",
        "İzleyiciye Geç (spec)",
        "T Takımına Geç (t)",
        "CT Takımına Geç (ct)",
        "Yeniden Doğur (respawn)",
        "Silahları Temizle (strip)",
        "Dondur (freeze)",
        "Çöz (unfreeze)",
        "Noclip Aç/Kapat (noclip)",
        "Can 100 Yap (hp:100)",
        "Zırh 100 Yap (armor:100)"
    };

    private static readonly string[][] TeamPresets =
    {
        Array.Empty<string>(),
        new[] { "T" },
        new[] { "CT" },
        new[] { "SPEC" }
    };
    private static readonly string[] TeamPresetLabels = { "Herkes", "Sadece T", "Sadece CT", "Sadece İzleyici" };

    private readonly ISwiftlyCore _core;
    private readonly ConfigLoader _loader;
    private readonly CustomCommandService _service;
    private readonly Action _onCommandsChanged;
    private readonly Action<IPlayer> _onReloadRequested;
    private PluginConfig _config;

    private readonly Dictionary<int, Session> _sessions = new();
    private readonly Dictionary<int, (string Prompt, Action<IPlayer, string> Callback)> _pendingInput = new();
    private readonly Dictionary<int, (string Html, float Time)> _lastHtml = new();
    private readonly HashSet<int> _frozenSlots = new();

    private enum RowStyle { Normal, Accent, On, Off, Danger, Muted }

    private sealed record Row(
        string Label,
        string? Value = null,
        bool Arrows = false,
        RowStyle Style = RowStyle.Normal,
        Action<IPlayer>? OnSelect = null,
        Action<IPlayer, int>? OnAdjust = null);

    private sealed class Page
    {
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public List<Row> Rows { get; init; } = new();
    }

    private delegate Page PageBuilder(IPlayer admin);

    private sealed class Session(PageBuilder root)
    {
        private readonly Stack<(PageBuilder Builder, int Cursor)> _history = new();
        public PageBuilder Current { get; private set; } = root;
        public int Cursor { get; set; }
        public GameButtonFlags LastButtons { get; set; }
        public float LastKeyTime { get; set; }

        public void Push(PageBuilder next)
        {
            _history.Push((Current, Cursor));
            Current = next;
            Cursor = 0;
        }

        public bool Back()
        {
            if (_history.Count == 0) return false;
            var (b, c) = _history.Pop();
            Current = b;
            Cursor = c;
            return true;
        }
    }

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

    public bool IsMenuOpen(int slot) => _sessions.ContainsKey(slot);

    public void Toggle(IPlayer admin)
    {
        if (IsMenuOpen(admin.Slot)) Close(admin);
        else Open(admin);
    }

    public void Open(IPlayer admin)
    {
        _sessions[admin.Slot] = new Session(BuildMainPage)
        {
            LastButtons = admin.PressedButtons,
            LastKeyTime = PlayerHelper.SafeCurrentTime(_core)
        };
        SetFrozen(admin, true);
        _service.Reply(admin, "Özel Komut yönetim menüsü açıldı! [lime]W/S[default]: Gezin | [lime]A/D[default]: Değiştir | [lime]E[default]: Seç | [lime]R[default]: Geri/Kapat");
    }

    public void Close(IPlayer admin)
    {
        _pendingInput.Remove(admin.Slot);
        if (_sessions.Remove(admin.Slot))
        {
            SetFrozen(admin, false);
            if (_lastHtml.Remove(admin.Slot))
            {
                try { admin.SendCenterHTML(" ", 1); } catch { /* yoksay */ }
            }
        }
    }

    public void OnPlayerDisconnected(int slot)
    {
        _sessions.Remove(slot);
        _pendingInput.Remove(slot);
        _lastHtml.Remove(slot);
        _frozenSlots.Remove(slot);
    }

    public void CloseAll()
    {
        try
        {
            foreach (var p in _core.PlayerManager.GetAllPlayers().Where(PlayerHelper.IsUsable))
            {
                if (_sessions.ContainsKey(p!.Slot)) Close(p);
            }
        }
        catch { /* yoksay */ }
        _sessions.Clear();
        _pendingInput.Clear();
        _lastHtml.Clear();
        _frozenSlots.Clear();
    }

    public bool TryConsumeChatInput(int playerId, string text)
    {
        if (!_pendingInput.TryGetValue(playerId, out var pending)) return false;

        var admin = _core.PlayerManager.GetPlayer(playerId);
        if (!PlayerHelper.IsUsable(admin))
        {
            _pendingInput.Remove(playerId);
            return false;
        }

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1].Trim();

        _pendingInput.Remove(playerId);

        if (trimmed.Equals("iptal", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("!iptal", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("cancel", StringComparison.OrdinalIgnoreCase))
        {
            _service.Reply(admin, "[lightred]Giriş iptal edildi.[default]");
            return true;
        }

        pending.Callback(admin!, trimmed);
        return true;
    }

    private void PromptInput(IPlayer admin, string prompt, Action<IPlayer, string> callback)
    {
        _pendingInput[admin.Slot] = (prompt, callback);
        _service.Reply(admin, $"[gold]{prompt}[default] (İptal için sohbete [lightred]iptal[default] yazın)");
    }

    // ============================== TICK & INPUT ==============================

    public void OnTick()
    {
        if (_sessions.Count == 0) return;

        foreach (var kv in _sessions.ToList())
        {
            try
            {
                var admin = _core.PlayerManager.GetPlayer(kv.Key);
                if (!PlayerHelper.IsUsable(admin))
                {
                    OnPlayerDisconnected(kv.Key);
                    continue;
                }

                PollButtons(admin!, kv.Value);
                Render(admin!, kv.Value);
            }
            catch { /* yoksay */ }
        }
    }

    private void PollButtons(IPlayer admin, Session session)
    {
        if (_config.Settings.FreezeWhileMenuOpen)
            SetFrozen(admin, true);

        var buttons = admin.PressedButtons;
        var rising = buttons & ~session.LastButtons;
        session.LastButtons = buttons;
        if (rising == 0) return;

        if ((rising & GameButtonFlags.W) != 0) OnKeyPressed(admin, KeyKind.W);
        else if ((rising & GameButtonFlags.S) != 0) OnKeyPressed(admin, KeyKind.S);
        else if ((rising & GameButtonFlags.A) != 0) OnKeyPressed(admin, KeyKind.A);
        else if ((rising & GameButtonFlags.D) != 0) OnKeyPressed(admin, KeyKind.D);
        else if ((rising & GameButtonFlags.E) != 0) OnKeyPressed(admin, KeyKind.E);
        else if ((rising & GameButtonFlags.R) != 0) OnKeyPressed(admin, KeyKind.R);
    }

    public void OnKeyPressed(IPlayer admin, KeyKind key)
    {
        if (!_sessions.TryGetValue(admin.Slot, out var session)) return;

        var now = PlayerHelper.SafeCurrentTime(_core);
        if (MathF.Abs(now - session.LastKeyTime) < 0.12f) return;
        session.LastKeyTime = now;

        var page = session.Current(admin);
        var count = page.Rows.Count;
        if (count == 0) return;
        session.Cursor = Math.Clamp(session.Cursor, 0, count - 1);

        switch (key)
        {
            case KeyKind.W:
                session.Cursor = (session.Cursor - 1 + count) % count;
                break;
            case KeyKind.S:
                session.Cursor = (session.Cursor + 1) % count;
                break;
            case KeyKind.A:
                page.Rows[session.Cursor].OnAdjust?.Invoke(admin, -1);
                break;
            case KeyKind.D:
                page.Rows[session.Cursor].OnAdjust?.Invoke(admin, +1);
                break;
            case KeyKind.E:
                var r = page.Rows[session.Cursor];
                if (r.OnSelect != null) r.OnSelect(admin);
                else r.OnAdjust?.Invoke(admin, +1);
                break;
            case KeyKind.R:
            case KeyKind.Esc:
                _pendingInput.Remove(admin.Slot);
                if (!session.Back()) Close(admin);
                break;
        }
    }

    private void Render(IPlayer admin, Session session)
    {
        var page = session.Current(admin);
        if (page.Rows.Count > 0)
            session.Cursor = Math.Clamp(session.Cursor, 0, page.Rows.Count - 1);

        int pageIdx = session.Cursor / RowsPerPage;
        int pageCount = Math.Max(1, (page.Rows.Count + RowsPerPage - 1) / RowsPerPage);
        var slice = page.Rows.Skip(pageIdx * RowsPerPage).Take(RowsPerPage).ToList();
        int selectedInSlice = session.Cursor - pageIdx * RowsPerPage;

        var sb = new StringBuilder();
        sb.Append($"<font class='fontSize-m' color='#38BDF8'><b>✦ {E(page.Title)} ✦</b></font>");
        if (pageCount > 1)
            sb.Append($" <font class='fontSize-s' color='#94A3B8'>[{pageIdx + 1}/{pageCount}]</font>");
        sb.Append("<br>");

        if (!string.IsNullOrEmpty(page.Subtitle))
            sb.Append($"<font class='fontSize-xs' color='#CBD5E1'>{E(page.Subtitle)}</font><br>");

        for (int i = 0; i < slice.Count; i++)
        {
            var row = slice[i];
            bool sel = i == selectedInSlice;
            var labelColor = sel ? "#FACC15" : row.Style switch
            {
                RowStyle.Danger => "#F87171",
                RowStyle.Accent => "#38BDF8",
                RowStyle.Muted => "#64748B",
                _ => "#F8FAFC"
            };

            sb.Append("<font class='fontSize-sm'>");
            sb.Append(sel ? "<font color='#FACC15'><b>➤ </b></font>" : "<font color='#475569'>• </font>");
            sb.Append(sel
                ? $"<font color='{labelColor}'><b>{E(row.Label)}</b></font>"
                : $"<font color='{labelColor}'>{E(row.Label)}</font>");

            if (!string.IsNullOrEmpty(row.Value))
            {
                var valColor = row.Style switch
                {
                    RowStyle.On => "#4ADE80",
                    RowStyle.Off => "#F87171",
                    _ => "#E2E8F0"
                };
                var v = row.Arrows ? $"◀ {E(row.Value)} ▶" : $"[{E(row.Value)}]";
                sb.Append($" <font color='{valColor}'><b>{v}</b></font>");
            }
            sb.Append("</font><br>");
        }

        if (_pendingInput.TryGetValue(admin.Slot, out var pending))
            sb.Append($"<font class='fontSize-s' color='#FBBF24'><b>💬 Sohbete yazın: {E(pending.Prompt)}</b></font><br>");

        sb.Append("<font class='fontSize-xs' color='#94A3B8'>[W/S] Gezin • [A/D] Değiştir • [E] Seç • [R] Geri/Kapat</font>");

        var html = sb.ToString();
        var now = PlayerHelper.SafeCurrentTime(_core);
        if (_lastHtml.TryGetValue(admin.Slot, out var last) && last.Html == html && MathF.Abs(now - last.Time) < ResendSeconds)
            return;

        _lastHtml[admin.Slot] = (html, now);
        try { admin.SendCenterHTML(html, HtmlDurationMs); } catch { /* yoksay */ }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    private void SetFrozen(IPlayer player, bool freeze)
    {
        if (freeze && !_config.Settings.FreezeWhileMenuOpen) return;
        if (!freeze && !_frozenSlots.Remove(player.Slot)) return;

        try
        {
            var pawn = player.PlayerPawn;
            if (pawn is not { IsValid: true }) return;
            var target = freeze ? MoveType_t.MOVETYPE_NONE : MoveType_t.MOVETYPE_WALK;
            if (pawn.MoveType != target)
            {
                pawn.MoveType = target;
                pawn.ActualMoveType = target;
                pawn.MoveTypeUpdated();
            }
            if (freeze)
            {
                _frozenSlots.Add(player.Slot);
                pawn.AbsVelocity.X = 0f;
                pawn.AbsVelocity.Y = 0f;
                pawn.AbsVelocity.Z = 0f;
            }
        }
        catch { /* yoksay */ }
    }

    private void SaveAndApply(bool reRegisterCommands = false)
    {
        try { _loader.Save(_config); }
        catch (Exception ex) { _core.Logger.LogError(ex, "[CustomCommands] Config kaydedilemedi."); }

        _service.UpdateConfig(_config);
        if (reRegisterCommands)
            _onCommandsChanged();
    }

    // ============================== MENU PAGES ==============================

    private Page BuildMainPage(IPlayer admin)
    {
        int activeCount = _config.Commands.Count(c => c.Enabled);
        return new Page
        {
            Title = "CustomCommands Yönetim",
            Subtitle = $"{activeCount}/{_config.Commands.Count} aktif özel komut • Durum: {(_config.Settings.Enabled ? "AÇIK" : "KAPALI")}",
            Rows =
            {
                new Row("📋 Özel Komutları Yönet", $"{_config.Commands.Count} komut", Style: RowStyle.Accent,
                    OnSelect: p => _sessions[p.Slot].Push(BuildCommandsListPage)),

                new Row("➕ Yeni Özel Komut Oluştur", Style: RowStyle.Accent,
                    OnSelect: StartCreateCommandFlow),

                new Row("⚡ Hazır Şablonlardan Komut Ekle", Style: RowStyle.Accent,
                    OnSelect: p => _sessions[p.Slot].Push(BuildTemplatesPage)),

                new Row("🔧 Genel Ayarlar", _config.Settings.Enabled ? "AÇIK" : "KAPALI",
                    Style: _config.Settings.Enabled ? RowStyle.On : RowStyle.Off,
                    OnSelect: p => _sessions[p.Slot].Push(BuildSettingsPage)),

                new Row("🔄 Config Dosyasını Yeniden Yükle (Reload)", Style: RowStyle.Accent,
                    OnSelect: p => _onReloadRequested(p)),

                new Row("❌ Menüyü Kapat", Style: RowStyle.Danger, OnSelect: Close)
            }
        };
    }

    private Page BuildCommandsListPage(IPlayer admin)
    {
        var rows = new List<Row>
        {
            new("➕ Yeni Özel Komut Oluştur", Style: RowStyle.Accent, OnSelect: StartCreateCommandFlow)
        };

        foreach (var cmd in _config.Commands.ToList())
        {
            var entry = cmd;
            var permBadge = string.IsNullOrWhiteSpace(entry.Permission) && entry.Permissions.Count == 0
                ? "Herkes"
                : entry.Permission ?? string.Join(",", entry.Permissions);

            rows.Add(new Row(
                Label: $"{entry.TriggersDisplay} ({entry.Label})",
                Value: $"{(entry.Enabled ? "AKTİF" : "KAPALI")} • {permBadge} • {entry.SummaryTag()}",
                Style: entry.Enabled ? RowStyle.On : RowStyle.Off,
                OnSelect: a => _sessions[a.Slot].Push(_ => BuildCommandEditPage(entry))));
        }

        if (_config.Commands.Count == 0)
            rows.Add(new Row("Kayıtlı özel komut bulunmuyor", Style: RowStyle.Muted));

        return new Page
        {
            Title = "Özel Komut Listesi",
            Subtitle = "Düzenlemek veya test etmek istediğiniz komutu seçin",
            Rows = rows
        };
    }

    private void StartCreateCommandFlow(IPlayer admin)
    {
        PromptInput(admin, "Yeni komutun tetikleyicisini yazın (örn: dc veya gelherkes veya dc,discord):", (adm, rawTriggers) =>
        {
            var triggers = rawTriggers
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(ConfigLoader.NormalizeTrigger)
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (triggers.Count == 0)
            {
                _service.Reply(adm, "[lightred]Geçerli bir komut adı girmediniz.[default]");
                return;
            }

            var baseId = triggers[0];
            var id = baseId;
            int suffix = 2;
            while (_config.Commands.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            {
                id = $"{baseId}_{suffix++}";
            }

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
            _service.Reply(adm, $"[lime]{entry.TriggersDisplay}[default] komutu oluşturuldu! Şimdi çıktı ve aksiyonlarını düzenleyebilirsiniz.");

            if (_sessions.TryGetValue(adm.Slot, out var session))
                session.Push(_ => BuildCommandEditPage(entry));
        });
    }

    private Page BuildTemplatesPage(IPlayer admin)
    {
        return new Page
        {
            Title = "Hazır Komut Şablonları",
            Subtitle = "Tek tuşla sunucunuza hazır özel komut ekleyin",
            Rows =
            {
                new Row("💬 !dc / !discord (Discord Davet)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("discord"),
                        Title = "Discord Sunucusu",
                        Description = "Discord davet adresini gösterir.",
                        Triggers = new List<string> { "dc", "discord" },
                        Target = "caller",
                        CooldownSeconds = 3.0f,
                        Chat = "💬 [lime]Discord Sunucumuz:[default] [lightblue]{DISCORD}[default]",
                        Html = "<font class='fontSize-m' color='#38BDF8'><b>💬 DISCORD</b></font><br><font class='fontSize-sm' color='#4ADE80'><b>{DISCORD}</b></font>"
                    })),

                new Row("💀 !kill / !intihar (Anında İntihar)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("intihar"),
                        Title = "İntihar Et (!kill)",
                        Description = "Hayattayken intihar etmenizi sağlar.",
                        Triggers = new List<string> { "kill", "intihar", "slayme" },
                        AliveState = "alive",
                        CooldownSeconds = 5.0f,
                        HideChat = true,
                        Target = "caller",
                        PlayerAction = "suicide",
                        Chat = "💀 [lightred]İntihar ettiniz![default]"
                    })),

                new Row("⚡ !gelherkes (Admin: sw_bring @all)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("gel_herkes"),
                        Title = "Herkesi Yanına Çek (!gelherkes)",
                        Description = "Tüm oyuncuları komutu yazan yetkilinin yanına çeker.",
                        Triggers = new List<string> { "gelherkes", "herkesigetir", "bringall" },
                        Permission = "admin",
                        AliveState = "alive",
                        CooldownSeconds = 2.0f,
                        HideChat = true,
                        Target = "all",
                        ClientCommands = new List<string> { "sw_bring @all" },
                        Chat = "⚡ Yetkili [lime]{NAME}[default] tüm oyuncuları yanına çekti!"
                    })),

                new Row("👁️ !spec / !afk (İzleyiciye Geç)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("izleyici_gec"),
                        Title = "İzleyiciye Geç (!spec)",
                        Description = "İzleyici takımına geçirir.",
                        Triggers = new List<string> { "spec", "afk", "izleyici" },
                        CooldownSeconds = 3.0f,
                        HideChat = true,
                        Target = "caller",
                        PlayerAction = "spec",
                        Chat = "👁️ [lightblue]İzleyici takımına geçtiniz.[default]"
                    })),

                new Row("🔄 !rr / !restart (Admin: mp_restartgame 1)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("raunt_yenile"),
                        Title = "Maçı Yeniden Başlat (!rr)",
                        Description = "Maçı 1 saniye içinde yeniden başlatır.",
                        Triggers = new List<string> { "rr", "restart" },
                        Permission = "admin",
                        HideChat = true,
                        Target = "all",
                        ServerCommands = new List<string> { "mp_restartgame 1" },
                        Chat = "🔄 Yetkili [lime]{NAME}[default] maçı yeniden başlattı!"
                    })),

                new Row("🩺 !hp / !heal (VIP: Can 100 Yap)", Style: RowStyle.Accent,
                    OnSelect: a => AddTemplateCommand(a, new CustomCommandEntry
                    {
                        Id = NextUniqueId("vip_can"),
                        Title = "VIP Can Doldur (!hp)",
                        Description = "VIP oyuncuların canını 100 yapar.",
                        Triggers = new List<string> { "hp", "heal", "can" },
                        Permission = "vip",
                        AliveState = "alive",
                        CooldownSeconds = 30.0f,
                        HideChat = true,
                        Target = "caller",
                        PlayerAction = "hp:100",
                        Chat = "🩺 [lime]Canınız 100 olarak yenilendi![default]"
                    }))
            }
        };
    }

    private string NextUniqueId(string baseId)
    {
        var id = baseId;
        int n = 2;
        while (_config.Commands.Any(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            id = $"{baseId}_{n++}";
        return id;
    }

    private void AddTemplateCommand(IPlayer admin, CustomCommandEntry template)
    {
        _config.Commands.Add(template);
        SaveAndApply(reRegisterCommands: true);
        _service.Reply(admin, $"[lime]{template.TriggersDisplay}[default] şablon komutu eklendi ve kaydedildi!");
        if (_sessions.TryGetValue(admin.Slot, out var session))
            session.Push(_ => BuildCommandEditPage(template));
    }

    private Page BuildCommandEditPage(CustomCommandEntry cmd)
    {
        int targetIdx = Array.FindIndex(TargetValues, t => t.Equals(cmd.Target, StringComparison.OrdinalIgnoreCase));
        if (targetIdx < 0) targetIdx = 0;

        int aliveIdx = Array.FindIndex(AliveValues, a => a.Equals(cmd.AliveState, StringComparison.OrdinalIgnoreCase));
        if (aliveIdx < 0) aliveIdx = 0;

        int teamIdx = GetTeamPresetIndex(cmd.Teams);

        int permIdx = Array.FindIndex(PermissionPresets, p => p.Equals(cmd.Permission ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        string permDisplay = string.IsNullOrWhiteSpace(cmd.Permission) ? "Herkes" : cmd.Permission!;

        int actIdx = Array.FindIndex(ActionPresets, a => a.Equals(cmd.PlayerAction ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        string actDisplay = actIdx >= 0
            ? ActionPresetLabels[actIdx]
            : (string.IsNullOrWhiteSpace(cmd.PlayerAction) ? "Yok" : cmd.PlayerAction!);

        string hideChatLabel = cmd.HideChat switch
        {
            null => "Genel Ayar",
            true => "EVET (Gizle)",
            false => "HAYIR (Göster)"
        };

        var rows = new List<Row>
        {
            new("Durum", cmd.Enabled ? "AKTİF" : "KAPALI", Arrows: true,
                Style: cmd.Enabled ? RowStyle.On : RowStyle.Off,
                OnAdjust: (_, _) => { cmd.Enabled = !cmd.Enabled; SaveAndApply(reRegisterCommands: true); }),

            new("🚀 Şimdi Test Et / Çalıştır", Style: RowStyle.Accent,
                OnSelect: a =>
                {
                    Close(a);
                    _service.Execute(a, cmd, cmd.PrimaryTrigger, Array.Empty<string>(), bypassChecks: true);
                }),

            new("⌨️ Tetikleyiciler (E ile Yaz)", cmd.TriggersDisplay, Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Komut tetikleyicilerini virgülle yazın (örn: dc, discord):", (adm, val) =>
                {
                    var list = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(ConfigLoader.NormalizeTrigger)
                        .Where(x => x.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    if (list.Count > 0)
                    {
                        cmd.Triggers = list;
                        SaveAndApply(reRegisterCommands: true);
                        _service.Reply(adm, $"Tetikleyiciler güncellendi: [lime]{cmd.TriggersDisplay}[default]");
                    }
                })),

            new("🔑 Gereken Yetki (A/D veya E ile Yaz)", permDisplay, Arrows: true, Style: RowStyle.Accent,
                OnAdjust: (_, dir) =>
                {
                    int baseIdx = permIdx >= 0 ? permIdx : 0;
                    int next = ((baseIdx + dir) % PermissionPresets.Length + PermissionPresets.Length) % PermissionPresets.Length;
                    cmd.Permission = string.IsNullOrEmpty(PermissionPresets[next]) ? null : PermissionPresets[next];
                    SaveAndApply();
                },
                OnSelect: a => PromptInput(a, "Gereken yetkiyi yazın (örn: admin, vip, !vip veya herkes için -):", (adm, val) =>
                {
                    cmd.Permission = val == "-" ? null : val.Trim();
                    SaveAndApply();
                    _service.Reply(adm, $"Yetki güncellendi: [lime]{cmd.Permission ?? "Herkes"}[default]");
                })),

            new("📡 Çıktı Hedefi (Target)", TargetLabels[targetIdx], Arrows: true, Style: RowStyle.Accent,
                OnAdjust: (_, dir) =>
                {
                    int next = ((targetIdx + dir) % TargetValues.Length + TargetValues.Length) % TargetValues.Length;
                    cmd.Target = TargetValues[next];
                    cmd.PrintTo = null;
                    SaveAndApply();
                }),

            new("💬 Chat Mesajı (E ile Yaz)", ShortPreview(cmd.Chat ?? cmd.ChatLines.FirstOrDefault()), Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Chat mesajını yazın (kaldırmak için - yazın):", (adm, val) =>
                {
                    cmd.ChatLines.Clear();
                    cmd.Chat = val == "-" ? null : val;
                    SaveAndApply();
                    _service.Reply(adm, "Chat mesajı güncellendi.");
                })),

            new("🖥️ CenterHTML Mesajı (E ile Yaz)", ShortPreview(cmd.Html), Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "CenterHTML mesajını yazın (kaldırmak için - yazın):", (adm, val) =>
                {
                    cmd.Html = val == "-" ? null : val;
                    SaveAndApply();
                    _service.Reply(adm, "CenterHTML mesajı güncellendi.");
                })),

            new("🎯 Center Mesajı (E ile Yaz)", ShortPreview(cmd.Center), Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Center mesajını yazın (kaldırmak için - yazın):", (adm, val) =>
                {
                    cmd.Center = val == "-" ? null : val;
                    SaveAndApply();
                    _service.Reply(adm, "Center mesajı güncellendi.");
                })),

            new("👤 Oyuncu Komutu (client_commands)", cmd.ClientCommands.Count > 0 ? string.Join(" ; ", cmd.ClientCommands) : "Yok", Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Oyuncuya çalıştırılacak komutu yazın (örn: sw_bring @all | silmek için -):", (adm, val) =>
                {
                    cmd.ClientCommands = val == "-"
                        ? new List<string>()
                        : val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    SaveAndApply();
                    _service.Reply(adm, "Oyuncu komutu (client_commands) güncellendi.");
                })),

            new("🖧 Sunucu Komutu (server_commands)", cmd.ServerCommands.Count > 0 ? string.Join(" ; ", cmd.ServerCommands) : "Yok", Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Sunucu konsolunda çalıştırılacak komutu yazın (örn: mp_restartgame 1 | silmek için -):", (adm, val) =>
                {
                    cmd.ServerCommands = val == "-"
                        ? new List<string>()
                        : val.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    SaveAndApply();
                    _service.Reply(adm, "Sunucu komutu (server_commands) güncellendi.");
                })),

            new("⚡ Dahili Oyuncu Aksiyonu (A/D veya E)", actDisplay, Arrows: true, Style: RowStyle.Accent,
                OnAdjust: (_, dir) =>
                {
                    int baseIdx = actIdx >= 0 ? actIdx : 0;
                    int next = ((baseIdx + dir) % ActionPresets.Length + ActionPresets.Length) % ActionPresets.Length;
                    cmd.PlayerAction = string.IsNullOrEmpty(ActionPresets[next]) ? null : ActionPresets[next];
                    SaveAndApply();
                },
                OnSelect: a => PromptInput(a, "Oyuncu aksiyonunu yazın (suicide, spec, t, ct, respawn, strip, freeze, hp:100 veya -):", (adm, val) =>
                {
                    cmd.PlayerAction = val == "-" ? null : val.Trim();
                    SaveAndApply();
                    _service.Reply(adm, $"Oyuncu aksiyonu güncellendi: [lime]{cmd.PlayerAction ?? "Yok"}[default]");
                })),

            new("❤️ Hayatta Olma Şartı", AliveLabels[aliveIdx], Arrows: true,
                OnAdjust: (_, dir) =>
                {
                    int next = ((aliveIdx + dir) % AliveValues.Length + AliveValues.Length) % AliveValues.Length;
                    cmd.AliveState = AliveValues[next];
                    SaveAndApply();
                }),

            new("🚩 Takım Filtresi", TeamPresetLabels[teamIdx], Arrows: true,
                OnAdjust: (_, dir) =>
                {
                    int next = ((teamIdx + dir) % TeamPresets.Length + TeamPresets.Length) % TeamPresets.Length;
                    cmd.Teams = TeamPresets[next].ToList();
                    SaveAndApply();
                }),

            new("⏱️ Bekleme Süresi (Cooldown)", $"{cmd.CooldownSeconds:0.0} sn", Arrows: true,
                OnAdjust: (_, dir) =>
                {
                    cmd.CooldownSeconds = Math.Clamp(cmd.CooldownSeconds + dir * 1.0f, 0f, 300f);
                    SaveAndApply();
                }),

            new("🙈 Sohbette Gizle (hide_chat)", hideChatLabel, Arrows: true,
                OnAdjust: (_, _) =>
                {
                    cmd.HideChat = cmd.HideChat switch
                    {
                        null => true,
                        true => false,
                        false => null
                    };
                    SaveAndApply();
                }),

            new("✏️ Başlık (Title)", cmd.Label, Style: RowStyle.Accent,
                OnSelect: a => PromptInput(a, "Yeni komut başlığını yazın:", (adm, val) =>
                {
                    cmd.Title = val;
                    SaveAndApply();
                    _service.Reply(adm, $"Başlık [lime]{val}[default] olarak güncellendi.");
                })),

            new("🗑️ Bu Komutu Sil", Style: RowStyle.Danger,
                OnSelect: a =>
                {
                    _config.Commands.Remove(cmd);
                    SaveAndApply(reRegisterCommands: true);
                    _service.Reply(a, $"[lightred]'{cmd.TriggersDisplay}' komutu silindi.[default]");
                    if (_sessions.TryGetValue(a.Slot, out var s)) s.Back();
                })
        };

        return new Page
        {
            Title = $"Komut: {cmd.TriggersDisplay}",
            Subtitle = $"{cmd.Label} • {cmd.SummaryTag()}",
            Rows = rows
        };
    }

    private Page BuildSettingsPage(IPlayer admin)
    {
        var s = _config.Settings;

        return new Page
        {
            Title = "Genel Ayarlar",
            Subtitle = "Değişiklikler anında kaydedilir ve uygulanır",
            Rows =
            {
                new Row("Özel Komut Sistemi", s.Enabled ? "AÇIK" : "KAPALI", Arrows: true,
                    Style: s.Enabled ? RowStyle.On : RowStyle.Off,
                    OnAdjust: (_, _) => { s.Enabled = !s.Enabled; SaveAndApply(); }),

                new Row("Sohbet Ön Eki (enable_prefix)", s.EnablePrefix ? "AÇIK" : "KAPALI", Arrows: true,
                    Style: s.EnablePrefix ? RowStyle.On : RowStyle.Off,
                    OnAdjust: (_, _) => { s.EnablePrefix = !s.EnablePrefix; SaveAndApply(); }),

                new Row("✏️ Ön Ek Metnini Düzenle (E)", s.Prefix, Style: RowStyle.Accent,
                    OnSelect: a => PromptInput(a, "Yeni ön eki yazın (örn: [gold][Sunucu][default]):", (adm, val) =>
                    {
                        s.Prefix = val;
                        SaveAndApply();
                        _service.Reply(adm, $"Ön ek güncellendi: {s.Prefix}");
                    })),

                new Row("Komut Mesajlarını Sohbette Gizle", s.HideChatTriggers ? "AÇIK" : "KAPALI", Arrows: true,
                    Style: s.HideChatTriggers ? RowStyle.On : RowStyle.Off,
                    OnAdjust: (_, _) => { s.HideChatTriggers = !s.HideChatTriggers; SaveAndApply(); }),

                new Row("Varsayılan Cooldown", $"{s.DefaultCooldownSeconds:0.0} sn", Arrows: true,
                    OnAdjust: (_, dir) =>
                    {
                        s.DefaultCooldownSeconds = Math.Clamp(s.DefaultCooldownSeconds + dir * 0.5f, 0f, 60f);
                        SaveAndApply();
                    }),

                new Row("Admin/Root Tüm Yetkilerden Geçsin", s.RootBypassesPermissions ? "EVET" : "HAYIR", Arrows: true,
                    Style: s.RootBypassesPermissions ? RowStyle.On : RowStyle.Off,
                    OnAdjust: (_, _) => { s.RootBypassesPermissions = !s.RootBypassesPermissions; SaveAndApply(); }),

                new Row("✏️ Discord Adresi ({DISCORD})", s.DiscordInvite, Style: RowStyle.Accent,
                    OnSelect: a => PromptInput(a, "Yeni Discord davet adresini yazın:", (adm, val) =>
                    {
                        s.DiscordInvite = val;
                        SaveAndApply();
                        _service.Reply(adm, $"Discord adresi güncellendi: [lightblue]{val}[default]");
                    })),

                new Row("✏️ Web Sitesi ({WEBSITE})", s.WebsiteUrl, Style: RowStyle.Accent,
                    OnSelect: a => PromptInput(a, "Yeni Web sitesi adresini yazın:", (adm, val) =>
                    {
                        s.WebsiteUrl = val;
                        SaveAndApply();
                        _service.Reply(adm, $"Web sitesi güncellendi: [lime]{val}[default]");
                    })),

                new Row("✏️ Sunucu Domain ({DOMAIN})", s.ServerDomain, Style: RowStyle.Accent,
                    OnSelect: a => PromptInput(a, "Yeni sunucu domain/IP adresini yazın:", (adm, val) =>
                    {
                        s.ServerDomain = val;
                        SaveAndApply();
                        _service.Reply(adm, $"Domain güncellendi: [gold]{val}[default]");
                    })),

                new Row("Menü Açıkken Oyuncuyu Dondur", s.FreezeWhileMenuOpen ? "AÇIK" : "KAPALI", Arrows: true,
                    Style: s.FreezeWhileMenuOpen ? RowStyle.On : RowStyle.Off,
                    OnAdjust: (adm, _) =>
                    {
                        s.FreezeWhileMenuOpen = !s.FreezeWhileMenuOpen;
                        SetFrozen(adm, s.FreezeWhileMenuOpen);
                        SaveAndApply();
                    })
            }
        };
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

    private static string ShortPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Yok";
        var clean = ColorHelper.StripAll(text).Replace("\n", " ").Trim();
        return clean.Length > 24 ? clean[..24] + "..." : clean;
    }
}

