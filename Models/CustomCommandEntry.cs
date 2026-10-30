namespace CustomCommands.Models;

/// <summary>
/// Tek bir ozel komut tanimi ([[commands]]).
/// Hem mesaj ciktilarini (Chat, ChatLines, Center, CenterHTML, Alert, Console),
/// hem sunucu/istemci komut calistirmalarini (ServerCommands, ClientCommands),
/// hem de dahili oyuncu aksiyonlarini (PlayerAction: suicide, spec, t, ct, respawn, strip, freeze...) barindirir.
/// </summary>
public class CustomCommandEntry
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool ShowInHelp { get; set; } = true;

    /// <summary>Komutu tetikleyen isimler (orn: ["dc", "discord"]). Basinda ! veya / olmadan saklanir.</summary>
    public List<string> Triggers { get; set; } = new();

    /// <summary>Tekil yetki anahtari (orn: "admin", "vip", "!vip").</summary>
    public string? Permission { get; set; }

    /// <summary>Coklu yetki listesi (CSS-CreateCustomCommands PermissionList uyumu).</summary>
    public List<string> Permissions { get; set; } = new();

    /// <summary>true ise Permissions listesindeki TUM yetkiler gerekir; false ise herhangi biri yeterlidir.</summary>
    public bool RequiresAllPermissions { get; set; } = false;

    /// <summary>Yetki yoksa gosterilecek ozel mesaj (bos ise genel ayardaki no_permission_message kullanilir).</summary>
    public string? NoPermissionMessage { get; set; }

    /// <summary>Oyuncu basina bekleme suresi (saniye).</summary>
    public float CooldownSeconds { get; set; } = 0f;

    /// <summary>Sunucu geneli bekleme suresi (saniye).</summary>
    public float GlobalCooldownSeconds { get; set; } = 0f;

    /// <summary>Komutu kullanabilecek takimlar ("T", "CT", "SPEC"). Bos ise herkes kullanabilir.</summary>
    public List<string> Teams { get; set; } = new();

    /// <summary>"any" (varsayilan), "alive" (sadece yasayanlar), "dead" (sadece oluler).</summary>
    public string AliveState { get; set; } = "any";

    /// <summary>Komutun aktif oldugu haritalar (orn: ["de_dust2", "surf_"]). Bos ise tum haritalarda calisir.</summary>
    public List<string> Maps { get; set; } = new();

    /// <summary>Komutun calismasi icin gereken minimum arguman sayisi (orn: !getir <oyuncu> icin 1).</summary>
    public int MinArgs { get; set; } = 0;

    /// <summary>Eksik arguman girildiginde gosterilecek kullanim bilgisi (orn: "!getir <oyuncu>").</summary>
    public string? Usage { get; set; }

    /// <summary>Komut sohbetten yazildiginda mesaj herkesten gizlensin mi? (null = genel ayari kullanir).</summary>
    public bool? HideChat { get; set; }

    /// <summary>Sohbet ciktisinin basina prefix eklensin mi? (null = genel ayari kullanir).</summary>
    public bool? UsePrefix { get; set; }

    /// <summary>Ciktiyi kimin gorecegi: "caller" (sadece yazan), "all" (tum sunucu), "team" (yazanin takimi).</summary>
    public string Target { get; set; } = "caller";

    /// <summary>
    /// CSS-CreateCustomCommands PrintTo (0..7) uyumlulugu.
    /// null degilse Target hedefini bu degere gore belirler.
    /// </summary>
    public int? PrintTo { get; set; }

    // --- Mesaj Çıktıları ---
    public string? Chat { get; set; }
    public List<string> ChatLines { get; set; } = new();
    public string? Center { get; set; }
    public string? Html { get; set; }
    public int? HtmlDurationMs { get; set; }
    public string? Alert { get; set; }
    public string? Console { get; set; }
    public List<string> ConsoleLines { get; set; } = new();

    // --- Çalıştırılacak Komutlar ve Aksiyonlar ---

    /// <summary>
    /// Komutu yazan OYUNCU adina calistirilacak komutlar (caller.ExecuteCommand).
    /// Orn: ["sw_bring @all"] veya ["sw_bring {ARG1}"].
    /// </summary>
    public List<string> ClientCommands { get; set; } = new();

    /// <summary>
    /// SUNUCU KONSOLUNDA calistirilacak komutlar (Core.Engine.ExecuteCommand).
    /// Orn: ["mp_restartgame 1"] veya ["sw_slay #{USERID}"].
    /// </summary>
    public List<string> ServerCommands { get; set; } = new();

    /// <summary>
    /// Dahili oyuncu aksiyonu:
    /// "none" / "", "suicide" ("kill"), "spec", "t", "ct", "respawn", "strip", "freeze", "unfreeze",
    /// "hp:100", "armor:100", "speed:1.5", "gravity:0.5"
    /// </summary>
    public string? PlayerAction { get; set; }

    /// <summary>Komut/aksiyonlarin calismasi icin gecikme suresi (saniye, 0 = aninda).</summary>
    public float DelaySeconds { get; set; } = 0f;

    public string Label => !string.IsNullOrWhiteSpace(Title) ? Title : PrimaryTrigger;

    public string PrimaryTrigger => Triggers.FirstOrDefault() ?? Id;

    public string TriggersDisplay => Triggers.Count > 0
        ? string.Join(", ", Triggers.Select(t => "!" + t))
        : "!" + Id;

    public bool HasAnyActionOrOutput() =>
        !string.IsNullOrWhiteSpace(Chat) ||
        ChatLines.Count > 0 ||
        !string.IsNullOrWhiteSpace(Center) ||
        !string.IsNullOrWhiteSpace(Html) ||
        !string.IsNullOrWhiteSpace(Alert) ||
        !string.IsNullOrWhiteSpace(Console) ||
        ConsoleLines.Count > 0 ||
        ClientCommands.Count > 0 ||
        ServerCommands.Count > 0 ||
        (!string.IsNullOrWhiteSpace(PlayerAction) && !PlayerAction.Equals("none", StringComparison.OrdinalIgnoreCase));

    public List<string> GetAllPermissions()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(Permission))
            list.Add(Permission.Trim());
        foreach (var p in Permissions)
        {
            if (!string.IsNullOrWhiteSpace(p) && !list.Contains(p.Trim(), StringComparer.OrdinalIgnoreCase))
                list.Add(p.Trim());
        }
        return list;
    }

    public bool MatchesTeam(string playerTeam) =>
        Teams.Count == 0 || Teams.Contains(playerTeam, StringComparer.OrdinalIgnoreCase);

    public bool MatchesMap(string currentMap)
    {
        if (Maps.Count == 0 || string.IsNullOrWhiteSpace(currentMap)) return true;
        foreach (var m in Maps)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            var pattern = m.Trim();
            if (pattern.EndsWith('_') || pattern.EndsWith('*'))
            {
                var prefix = pattern.TrimEnd('*');
                if (currentMap.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (currentMap.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                     currentMap.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public string SummaryTag()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Chat) || ChatLines.Count > 0) parts.Add("Chat");
        if (!string.IsNullOrWhiteSpace(Html)) parts.Add("HTML");
        if (!string.IsNullOrWhiteSpace(Center)) parts.Add("Center");
        if (!string.IsNullOrWhiteSpace(Alert)) parts.Add("Alert");
        if (ClientCommands.Count > 0) parts.Add($"OyuncuKomut({ClientCommands.Count})");
        if (ServerCommands.Count > 0) parts.Add($"SunucuKomut({ServerCommands.Count})");
        if (!string.IsNullOrWhiteSpace(PlayerAction) && !PlayerAction.Equals("none", StringComparison.OrdinalIgnoreCase))
            parts.Add($"Aksiyon:{PlayerAction}");
        return parts.Count > 0 ? string.Join("+", parts) : "Boş";
    }
}

