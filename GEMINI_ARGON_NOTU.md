# CustomCommands - Geliştirme ve Mimari Notları

Bu belge, `CSS-CreateCustomCommands` eklentisinin **SwiftlyS2 v1.5.0 (.NET 10.0)** mimarisiyle sıfırdan, çok daha gelişmiş özelliklerle (`TagSystem` ve `BroadcastSystem` standartlarında) yazılmış sürümü olan **`CustomCommands`** eklentisinin yapısını ve kullanımını özetler.

---

## 1. Proje Özeti ve Mimari

`CustomCommands`, sunucu yöneticilerinin kod yazmadan **TOML yapılandırmasıyla (`customcommands.toml`)** veya **oyun içi görsel menüden (`!komutmenu`)** yetki ve koşul bazlı özel komutlar oluşturmasını, bu komutlara zengin mesaj çıktıları (`Chat`, `CenterHTML`, `Center`, `Alert`, `Console`) bağlamasını, **komutu yazan oyuncu adına (`client_commands`, örn: `sw_bring @all`)** veya **sunucu konsolu adına (`server_commands`, örn: `mp_restartgame 1`)** komut çalıştırmasını ve **dahili oyuncu aksiyonları (`player_action`, örn: `!kill` ile intihar, `!spec` ile izleyiciye geçme)** tetiklemesini sağlar.

### Klasör ve Dosya Yapısı
* **[`CustomCommands.csproj`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/CustomCommands.csproj)**: `net10.0`, `SwiftlyS2.CS2` (1.5.0) ve `Tomlyn` (0.17.0) referansları. Build sonrası yerel sunucu klasörü mevcutsa otomatik kopyalama (`DeployToLocalServer`) hedefi içerir.
* **[`Plugin.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Plugin.cs)**: Ana eklenti giriş noktası (`BasePlugin`). Dinamik komut kaydı (`sw_<komut>`, konsol `<komut>` ve sohbet `!<komut>` / `/<komut>`), çift çalışma koruması (`IsDuplicateExecution`), `hide_chat` desteği ve yönetim komutları.
* **Config/**
  * **[`PluginConfig.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Config/PluginConfig.cs)**: `Settings`, `AdminCommands` ve `Commands` (`[[commands]]`) bölümlerini barındıran yapılandırma modeli.
  * **[`ConfigLoader.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Config/ConfigLoader.cs)**: `customcommands.toml` (ve `commands/*.toml` alt klasör dosyaları) okuma (`Load`) ve atomik kaydetme (`Save`) sınıfı.
  * **[`DefaultConfigTemplate.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Config/DefaultConfigTemplate.cs)**: İlk çalıştırmada oluşturulan kapsamlı Türkçe açıklamalı varsayılan `customcommands.toml` şablonu.
* **Models/**
  * **[`CustomCommandEntry.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Models/CustomCommandEntry.cs)**: Özel komut veri modeli (`Id`, `Title`, `Description`, `Triggers`, `Permission`, `Permissions`, `RequiresAllPermissions`, `CooldownSeconds`, `GlobalCooldownSeconds`, `Teams`, `AliveState`, `Maps`, `MinArgs`, `Usage`, `HideChat`, `Target`, `PrintTo`, `Chat`, `ChatLines`, `Center`, `Html`, `Alert`, `Console`, `ClientCommands`, `ServerCommands`, `PlayerAction`, `DelaySeconds`).
* **Core/**
  * **[`CustomCommandService.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Core/CustomCommandService.cs)**: Yetki/koşul/bekleme süresi (cooldown) denetimi, çok kanallı çıktı gönderimi, `caller.ExecuteCommand` (`ClientCommands`), `Core.Engine.ExecuteCommand` (`ServerCommands`) ve dahili `PlayerAction` (`suicide`, `spec`, `t`, `ct`, `respawn`, `strip`, `freeze`, `unfreeze`, `noclip`, `hp:100`, `armor:100`, `speed:1.5`, `gravity:0.5`) yürütücüsü.
  * **[`CustomCommandAdminMenu.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Core/CustomCommandAdminMenu.cs)**: `CenterHTML` ve `W/S/A/D/E/R` tuş navigasyonuyla çalışan oyun içi görsel özel komut yönetim ve şablon ekleme menüsü (`!komutmenu` / `!ccmenu`).
* **Utils/**
  * **[`ColorHelper.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Utils/ColorHelper.cs)**: Hem `[red]`, `[gold]`, `[#RRGGBB]` etiketlerini hem de `CSS-CreateCustomCommands` stilindeki `{RED}`, `{GREEN}`, `{DEFAULT}` etiketlerini destekleyen renk dönüştürücü.
  * **[`PlaceholderHelper.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Utils/PlaceholderHelper.cs)**: Tek geçişli sunucu, oyuncu ve komut argümanı (`{ARGS}`, `{ARG1}`..`{ARG9}`, `{ARGS_FROM_2}`) değiştirici ve komut enjeksiyonu koruması (`ApplyForCommand`).
  * **[`PlayerHelper.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Utils/PlayerHelper.cs)** & **[`SteamIdHelper.cs`](file:///c:/Users/Devrim/Documents/Varsayılan%20Proje/CustomCommands/Utils/SteamIdHelper.cs)**: Güvenli oyuncu verisi ve SteamID2/3/64 dönüştürücüler.

---

## 2. Yönetim ve Yardım Komutları

| Komut | Alternatifler | Yetki | Açıklama |
| :--- | :--- | :--- | :--- |
| `!komutmenu` | `!ccmenu`, `!customcommands` | `customcommands.admin` / `admin` | Görsel `W/S/A/D/E/R` özel komut yönetim menüsünü açar. |
| `!komutlar` | `!commands`, `!yardim`, `!help` | Herkes | Oyuncunun yetkisine uygun aktif özel komutları listeler. |
| `!komutekle <komut> <tip> "<değer>" [yetki]` | `!ccadd`, `sw_komutekle` | `customcommands.admin` / `admin` | Sohbetten veya konsoldan hızlıca yeni özel komut ekler ve kaydeder. |
| `!komutsil <id_veya_komut>` | `!ccdel`, `sw_komutsil` | `customcommands.admin` / `admin` | Belirtilen özel komutu siler ve kaydeder. |
| `!komutcalistir <id_veya_komut>` | `!ccrun`, `!cctest` | `customcommands.admin` / `admin` | Belirtilen özel komutu test amaçlı anında çalıştırır. |
| `!komutreload` | `!ccreload` | `customcommands.admin` / `admin` | `customcommands.toml` ve `commands/*.toml` dosyalarını canlı yeniden yükler. |

---

## 3. Varsayılan Hazır Özel Komutlar (`customcommands.toml`)

1. **`!dc` / `!discord`**: Discord davet adresini sohbette ve ekran ortası HTML kartında gösterir.
2. **`!site` / `!web` / `!market` / `!vip`**: Web sitesi ve mağaza adresini gösterir.
3. **`!ip` / `!sunucu`**: Sunucu IP, harita ve aktif oyuncu sayısını gösterir.
4. **`!kurallar` / `!rules`**: Çok satırlı sunucu kurallarını gösterir.
5. **`!kill` / `!intihar` / `!slayme`**: Hayattaki oyuncuyu `sv_cheats` gerektirmeden anında öldürür (`player_action = "suicide"`).
6. **`!spec` / `!afk` / `!izleyici`**: Oyuncuyu İzleyici (Spectator) takımına geçirir (`player_action = "spec"`).
7. **`!gelherkes` / `!bringall`**: (`permission = "admin"`) Komutu yazan yetkili adına `sw_bring @all` çalıştırır (`client_commands = ["sw_bring @all"]`).
8. **`!getir <hedef>`**: (`permission = "admin"`) Komutu yazan yetkili adına `sw_bring {ARG1}` çalıştırır.
9. **`!git <hedef>`**: (`permission = "admin"`) Komutu yazan yetkili adına `sw_goto {ARG1}` çalıştırır.
10. **`!rr` / `!restart`**: (`permission = "admin"`) Sunucu konsolunda `mp_restartgame 1` çalıştırır (`server_commands = ["mp_restartgame 1"]`).
11. **`!warmupbitir`**: (`permission = "admin"`) Sunucu konsolunda `mp_warmup_end` çalıştırır.

