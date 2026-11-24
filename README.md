# Özel Tarayıcı (Windows Native Edition)

**Özel Tarayıcı**, Electron altyapısından tamamen çıkarılarak **C# (.NET 8 WPF)** ve **Microsoft Edge WebView2** motoruyla sıfırdan geliştirilmiş, hafif, hızlı ve gizlilik odaklı geleneksel bir Windows masaüstü web tarayıcısıdır.

---

## Öne Çıkan Özellikler

### Sekme, Çalışma Grubu & Gezinme
* **Chrome Tarzı Çalışma Grupları:** 12 farklı görsel renk paletiyle grup oluşturma, grup adı/rengi düzenleme, grup katlama/açma, sekmeleri sürükle-bırak ile gruplara taşıma ve sekme sayısı rozeti.
* **Akıllı Sekme Çubuğu:** Dinamik genişleyen/daralan sekmeler, site ikonu (Favicon), tam başlık tooltip'i, orta tıkla sekme kapatma, **📌 Sekme Sabitleme (Pin)** ve **💤 Sekme Uyutma (RAM Tasarrufu)**.
* **Canlı Medya Ekolayzırı:** Video/ses çalan sekmelerde animasyonlu 3 çubuklu ekolayzır (üzerine tıklayarak videoyu durdurup başlatma).
* **Bölünmüş Ekran & Mini Video (PiP):** Yan yana sürüklenebilir ayırıcılı (`GridSplitter`) çift sekme görünümü ve videoyu her zaman üstte kalan mini pencereye alma.
* **Hızlı Komut Paleti (`Ctrl+K`):** Açık sekmeler, yer imleri, geçmiş ve tarayıcı komutları arasında tek kutudan anında geçiş.

### Sol Kayar Yan Uygulama Barı
* Sabitlenebilir veya sol kenara yaklaşınca kayarak açılan yan bar.
* **WhatsApp**, **Telegram**, **Spotify** ve kullanıcının kendi ekleyebileceği siteler (otomatik yüksek çözünürlüklü site ikonlarıyla) ana sekmelerden bağımsız yan panelde çalışır.

### Gizlilik, Güvenlik & Şifre Kasası
* **Gerçek Gizli Mod (`InPrivate`):** Mor/antrasit özel tema, bağımsız sekme şeridi, `IsInPrivateModeEnabled` motoru ve kapanışta geçici klasör temizliği.
* **Reklam & Parmak İzi Koruması:** Reklam/tracker ağ engelleme + kozmetik sayfa filtresi, Canvas/WebGL maskeleme ve `navigator.webdriver` gizleme.
* **🔐 Şifre Kasası:** Sayfa formlarından otomatik şifre yakalama, tek tıkla doldurma, şifre görüntüleme/düzenleme, 16 haneli güçlü şifre üretici ve **Windows DPAPI** yerel şifreleme.
* **Üst Düzey Şifre Aktarımı:** **PBKDF2 + AES-256-GCM** ana parolalı şifreli yedek (`.ozelpwd`), Chrome/Edge uyumlu `.csv` ve `.json` içe/dışa aktarma.

### Ana Sayfa Dashboard, Yapay Zeka & Araçlar
* **Canlı Gösterge Paneli:** LED dijital saat, `open-meteo` canlı hava durumu (il/ilçe seçimi + otomatik konum), Diyanet uyumlu (`aladhan.com`) ezan vakitleri, geri sayım ve vakit girdiğinde sesli bildirim.
* **Verimlilik Araçları:** Hızlı yer imleri, tarih/saat seçicili **Zamanlı Hatırlatıcılar** (sağ altta sesli *Ertele / Tamam* popup'ı) ve renk seçilebilen, düzenlenebilen, 3D sürükle-bırak **Yapışkan Notlar**.
* **✨ Gemini Asistan & Özetle:** Sağda açılan gömülü Gemini paneli ve tek tıkla aktif sayfayı özetleme.
* **🌐 Sağ Tık Çeviri & Araçlar:** Seçili metni anında Türkçeye/İngilizceye çevirme, Google'da arama, Gemini'ye sorma, sayfa QR kodu üretme (`QRCoder`), bölgesel ekran kırpma ve `Ctrl+P` yazdırma.
* **⚡ Sistem & Bellek Optimizasyonu:** Tek tıkla arka plan sekmelerini uyutma, `EmptyWorkingSet` bellek sıkıştırma ve zaman aralıklı (`1 saat / 1 gün / 1 hafta / komple`) geçmiş temizleme.

---

## Kurulum ve Derleme

### Gereksinimler
* Windows 10 / 11 (x64)
* [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (yalnızca kaynak koddan derlemek için)
* Microsoft Edge WebView2 Runtime (Windows 10/11'de yerleşik gelir)

### Kaynak Koddan Derleme
```powershell
dotnet publish OzelTarayici.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o publish
```

### Masaüstü ve Başlat Menüsü Kurulumu
Derleme sonrası `OzelTarayici_Kurulum.ps1` betiğini çalıştırarak uygulamayı `%LocalAppData%\Programs\OzelTarayici` altına kurabilir ve Masaüstü / Başlat Menüsü kısayollarını oluşturabilirsiniz.
