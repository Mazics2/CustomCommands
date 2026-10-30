using CustomCommands.Models;

namespace CustomCommands.Config;

public class PluginConfig
{
    public GeneralSettings Settings { get; set; } = new();
    public AdminCommandSettings AdminCommands { get; set; } = new();
    public List<CustomCommandEntry> Commands { get; set; } = new();

    public static List<CustomCommandEntry> CreateDefaultCommands() => new()
    {
        new CustomCommandEntry
        {
            Id = "discord",
            Title = "Discord Sunucusu",
            Description = "Discord davet adresimizi gösterir.",
            Enabled = true,
            Triggers = new List<string> { "dc", "discord" },
            Target = "caller",
            CooldownSeconds = 3.0f,
            ChatLines = new List<string>
            {
                "💬 [lime]Resmi Discord Sunucumuz:[default] [lightblue]{DISCORD}[default]",
                "🌐 [gold]Web Sitemiz:[default] [lime]{WEBSITE}[default] | [gold]IP:[default] {DOMAIN}"
            },
            Html = "<font class='fontSize-m' color='#38BDF8'><b>💬 DISCORD TOPLULUĞUMUZ</b></font><br><font class='fontSize-sm' color='#F8FAFC'>Davet Linki: <font color='#4ADE80'><b>{DISCORD}</b></font></font><br><font class='fontSize-xs' color='#FACC15'>Web: {WEBSITE}</font>",
            HtmlDurationMs = 5000
        },
        new CustomCommandEntry
        {
            Id = "website",
            Title = "Web Sitesi & Market",
            Description = "Web sitemizi ve VIP/Market adresini gösterir.",
            Enabled = true,
            Triggers = new List<string> { "site", "web", "market", "store", "vip" },
            Target = "caller",
            CooldownSeconds = 3.0f,
            Chat = "🛒 [gold]VIP & Market Adresimiz:[default] [lime]{WEBSITE}[default] | Destek için: [lightblue]{DISCORD}[default]",
            Html = "<font class='fontSize-m' color='#FACC15'><b>★ WEB SİTESİ & MARKET ★</b></font><br><font class='fontSize-sm' color='#F8FAFC'>Adres: <font color='#38BDF8'><b>{WEBSITE}</b></font></font>",
            HtmlDurationMs = 5000
        },
        new CustomCommandEntry
        {
            Id = "sunucu_ip",
            Title = "Sunucu Bilgisi & IP",
            Description = "Sunucu IP adresini ve aktif oyuncu durumunu gösterir.",
            Enabled = true,
            Triggers = new List<string> { "ip", "sunucu", "server" },
            Target = "caller",
            CooldownSeconds = 3.0f,
            Chat = "🌐 [lime]{SERVERNAME}[default] | IP: [gold]{DOMAIN}[default] ({SERVERIP}) | Harita: [lightblue]{MAP}[default] | Oyuncular: [lime]{PLAYERS}/{MAXPLAYERS}[default]"
        },
        new CustomCommandEntry
        {
            Id = "kurallar",
            Title = "Sunucu Kuralları",
            Description = "Temel sunucu kurallarını listeler.",
            Enabled = true,
            Triggers = new List<string> { "kurallar", "rules", "kural" },
            Target = "caller",
            CooldownSeconds = 5.0f,
            ChatLines = new List<string>
            {
                "[gold]══════════════ SUNUCU KURALLARI ══════════════[default]",
                "[lime]1.[default] Hile, makro ve 3. parti yazılım kullanımı kalıcı ban sebebidir.",
                "[lime]2.[default] Ailevi/dini değerlere küfür ve ağır hakaret yasaktır.",
                "[lime]3.[default] Mikrofon ve sohbet spamı yapmak yasaktır.",
                "[gold]══════════════════════════════════════════════[default]"
            }
        },
        new CustomCommandEntry
        {
            Id = "intihar",
            Title = "İntihar Et (!kill)",
            Description = "Hayattayken anında intihar etmenizi sağlar.",
            Enabled = true,
            Triggers = new List<string> { "kill", "intihar", "slayme", "suicide" },
            AliveState = "alive",
            CooldownSeconds = 5.0f,
            HideChat = true,
            Target = "caller",
            PlayerAction = "suicide",
            Chat = "💀 [lightred]İntihar ettiniz![default]"
        },
        new CustomCommandEntry
        {
            Id = "izleyici_gec",
            Title = "İzleyiciye Geç (!spec)",
            Description = "Sizi hızlıca İzleyici (Spectator) takımına geçirir.",
            Enabled = true,
            Triggers = new List<string> { "spec", "afk", "izleyici" },
            CooldownSeconds = 3.0f,
            HideChat = true,
            Target = "caller",
            PlayerAction = "spec",
            Chat = "👁️ [lightblue]İzleyici (Spectator) takımına geçtiniz.[default]"
        },
        new CustomCommandEntry
        {
            Id = "gel_herkes",
            Title = "Herkesi Yanına Çek (!gelherkes)",
            Description = "Tüm oyuncuları komutu yazan yetkilinin yanına ışınlar (sw_bring @all).",
            Enabled = true,
            Triggers = new List<string> { "gelherkes", "herkesigetir", "bringall" },
            Permission = "admin",
            AliveState = "alive",
            CooldownSeconds = 2.0f,
            HideChat = true,
            Target = "all",
            ClientCommands = new List<string> { "sw_bring @all" },
            Chat = "⚡ Yetkili [lime]{NAME}[default] tüm oyuncuları yanına çekti!"
        },
        new CustomCommandEntry
        {
            Id = "oyuncu_getir",
            Title = "Oyuncuyu Yanına Çek (!getir)",
            Description = "Belirtilen oyuncuyu yanınıza çeker (sw_bring <hedef>).",
            Enabled = true,
            Triggers = new List<string> { "getir", "cek" },
            Permission = "admin",
            MinArgs = 1,
            Usage = "!getir <oyuncu_adı / @t / @ct / @all>",
            HideChat = true,
            Target = "caller",
            ClientCommands = new List<string> { "sw_bring {ARG1}" },
            Chat = "⚡ [gold]{ARG1}[default] yanınıza çekildi."
        },
        new CustomCommandEntry
        {
            Id = "oyuncuya_git",
            Title = "Oyuncunun Yanına Git (!git)",
            Description = "Belirtilen oyuncunun yanına ışınlanır (sw_goto <hedef>).",
            Enabled = true,
            Triggers = new List<string> { "git", "isinlan" },
            Permission = "admin",
            MinArgs = 1,
            Usage = "!git <oyuncu_adı>",
            HideChat = true,
            Target = "caller",
            ClientCommands = new List<string> { "sw_goto {ARG1}" },
            Chat = "⚡ [gold]{ARG1}[default] oyuncusunun yanına ışınlandınız."
        },
        new CustomCommandEntry
        {
            Id = "raunt_yenile",
            Title = "Maçı Yeniden Başlat (!rr)",
            Description = "Oyunu 1 saniye içinde yeniden başlatır (mp_restartgame 1).",
            Enabled = true,
            Triggers = new List<string> { "rr", "restart" },
            Permission = "admin",
            CooldownSeconds = 3.0f,
            HideChat = true,
            Target = "all",
            ServerCommands = new List<string> { "mp_restartgame 1" },
            Chat = "🔄 Yetkili [lime]{NAME}[default] maçı yeniden başlattı!",
            Center = "🔄 Maç Yeniden Başlatılıyor!"
        },
        new CustomCommandEntry
        {
            Id = "isinma_bitir",
            Title = "Isınmayı Bitir (!warmupbitir)",
            Description = "Isınma (Warmup) süresini anında sonlandırır.",
            Enabled = true,
            Triggers = new List<string> { "warmupbitir", "isinmabitir", "endwarmup" },
            Permission = "admin",
            HideChat = true,
            Target = "all",
            ServerCommands = new List<string> { "mp_warmup_end" },
            Chat = "🔥 Yetkili [lime]{NAME}[default] ısınma süresini sonlandırdı!"
        }
    };
}

public class GeneralSettings
{
    public bool Enabled { get; set; } = true;
    public bool EnablePrefix { get; set; } = true;
    public string Prefix { get; set; } = "[gold][Sunucu][default]";
    public bool HideChatTriggers { get; set; } = false;
    public int DefaultHtmlDurationMs { get; set; } = 5000;
    public float DefaultCooldownSeconds { get; set; } = 1.0f;
    public bool RootBypassesPermissions { get; set; } = true;

    public bool ShowNoPermissionMessage { get; set; } = true;
    public string NoPermissionMessage { get; set; } = "[lightred]Bu komutu kullanmak için yetkiniz yok![default]";

    public bool ShowCooldownMessage { get; set; } = true;
    public string CooldownMessage { get; set; } = "[lightred]Bu komutu tekrar kullanmak için [gold]{COOLDOWN} sn[lightred] beklemelisiniz.[default]";

    public string OnlyAliveMessage { get; set; } = "[lightred]Bu komutu sadece hayattayken kullanabilirsiniz![default]";
    public string OnlyDeadMessage { get; set; } = "[lightred]Bu komutu sadece ölüyken kullanabilirsiniz![default]";
    public string WrongTeamMessage { get; set; } = "[lightred]Bu komutu mevcut takımınızda kullanamazsınız![default]";

    public string ServerNameOverride { get; set; } = string.Empty;
    public string ServerDomain { get; set; } = "oyun.sunucunuz.com";
    public string DiscordInvite { get; set; } = "discord.gg/sunucunuz";
    public string WebsiteUrl { get; set; } = "www.sunucunuz.com";

    public bool LoadExtraCommandFiles { get; set; } = true;
    public bool FreezeWhileMenuOpen { get; set; } = true;
    public bool LogToConsole { get; set; } = false;
    public bool Debug { get; set; } = false;
}

public class AdminCommandSettings
{
    public string MenuCommand { get; set; } = "komutmenu";
    public string ListCommand { get; set; } = "komutlar";
    public string AddCommand { get; set; } = "komutekle";
    public string RemoveCommand { get; set; } = "komutsil";
    public string RunCommand { get; set; } = "komutcalistir";
    public string ReloadCommand { get; set; } = "komutreload";
    public string AdminPermission { get; set; } = "customcommands.admin";
}

