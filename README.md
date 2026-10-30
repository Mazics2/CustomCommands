# ⚡ CustomCommands (SwiftlyS2 Özel Komut ve Çıktı Sistemi)

Counter-Strike 2 (CS2) ve **SwiftlyS2 (.NET 10 / v1.5.0+)** altyapısı için geliştirilmiş, yüksek performanslı, TOML tabanlı (`Tomlyn`), yetki ve koşul filtreli gelişmiş özel komut yönetim eklentisidir.

---

## ✨ Özellikler

- **📝 Kod Yazmadan Özel Komut Tanımlama:**
  - `customcommands.toml` veya `commands/*.toml` dosyaları üzerinden sınırsız komut oluşturabilme.
  - Dinamik komut kaydı: Sohbet tetikleyicileri (`!dc`, `/dc`), konsol komutları (`dc`) ve Swiftly komutları (`sw_dc`).
- **🎛️ Oyun İçi Görsel Admin Menüsü (`!komutmenu` / `!ccmenu`):**
  - Panorama CenterHTML ve `W / S / A / D / E / R` tuş navigasyonuyla çalışan etkileşimli yönetim menüsü.
  - Oyun içinden anında yeni komut ekleme, şablon seçme, düzenleme ve silme.
- **🛡️ Gelişmiş Koşul ve Yetki Filtreleri:**
  - Tekli veya çoklu yetki denetimi (`Permission`, `Permissions`, `RequiresAllPermissions`).
  - Takım filtresi (`teams = ["t", "ct", "spec"]`).
  - Yaşam durumu filtresi (`alive_state = "alive"`, `"dead"`, `"all"`).
  - Harita filtresi (`maps = ["de_dust2", "surf_*"]`).
  - Komut bazlı ve global bekleme süresi (cooldown).
- **🚀 Çok Kanallı Zengin Çıktılar:**
  - Sohbet mesajı (`chat` / `chat_lines`), Ekran Ortası (`center`), Panorama HTML kartı (`html`), CS2 Uyarısı (`alert`), Sunucu Konsolu (`console`).
- **⚙️ Komut ve Oyuncu Aksiyonu Tetikleme:**
  - Oyuncu adına komut çalıştırma (`client_commands`).
  - Sunucu konsolu adına komut çalıştırma (`server_commands`).
  - Dahili oyuncu aksiyonları (`player_action`): `suicide`, `spec`, `t`, `ct`, `respawn`, `strip`, `freeze`, `unfreeze`, `noclip`, `hp:100`, `armor:100`, `speed:1.5`, `gravity:0.5`.
- **🎨 Zengin Değişken ve Renk Desteği:**
  - `{NAME}`, `{STEAMID2}`, `{STEAMID64}`, `{IP}`, `{ARGS}`, `{ARG1}`..`{ARG9}`, `{ARGS_FROM_2}`, `{TIME}`, `{MAP}`, `{PLAYERS}`, `{MAXPLAYERS}`.
  - Komut enjeksiyonu korumalı dinamik argüman işleme.

---

## 🕹️ Komutlar

| Komut | Kısayollar | Yetki | Açıklama |
| :--- | :--- | :--- | :--- |
| `!komutmenu` | `!ccmenu`, `!customcommands` | `customcommands.admin` / `admin` | Görsel `W/S/A/D/E/R` özel komut yönetim menüsünü açar. |
| `!komutlar` | `!commands`, `!yardim` | Herkes | Oyuncunun yetkisine uygun aktif özel komutları listeler. |
| `!komutekle <komut> <tip> "<değer>" [yetki]` | `!ccadd`, `sw_komutekle` | `customcommands.admin` / `admin` | Hızlıca yeni özel komut ekler. |
| `!komutsil <id_veya_komut>` | `!ccdel`, `sw_komutsil` | `customcommands.admin` / `admin` | Belirtilen özel komutu siler. |
| `!komutcalistir <id_veya_komut>` | `!ccrun`, `!cctest` | `customcommands.admin` / `admin` | Belirtilen özel komutu test amaçlı çalıştırır. |
| `!komutreload` | `!ccreload` | `customcommands.admin` / `admin` | Yapılandırma dosyalarını canlı yeniden yükler. |

---

## 📥 Kurulum

1. `CustomCommands.dll` dosyasını derleyin veya hazır paketi indirin.
2. CS2 sunucunuzda eklenti klasörünü oluşturun:
   ```text
   addons/swiftlys2/plugins/CustomCommands/
   ```
3. Aşağıdaki dosyaları bu klasöre kopyalayın:
   - `CustomCommands.dll`
   - `CustomCommands.deps.json`
   - `customcommands.example.toml` -> `customcommands.toml` olarak adlandırın.
4. Sunucu konsolundan `sw_plugins reload CustomCommands` yazın.

---

## 📄 Lisans

Bu proje **Mazics2** bünyesinde geliştirilmiştir.
