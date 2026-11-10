# CircleLine — 0-RAM Pendulum-Physics Desktop Clothesline & Studio Bridge (`by thezencirs`)

**CircleLine**, ekranın üst kenarında gerçek **sarkaç fiziği (pendulum physics)** ile çalışan; **Circle Mix Record**, **3ds Max / AutoCAD / Revit** ve **Antigravity** iş akışlarını birbirine bağlayan ultra-hafif (`~85 KB`), sıfır kurulumlu, tek `.exe` masaüstü ip ve mandal (clothespin) stüdyosudur.

Geliştirici: **[thezencirs](https://github.com/thezencirs)** | Lisans: **MIT**

---

## 🌟 Öne Çıkan Özellikler

1. **🧲 Gerçek Sarkaç Fiziği & `%0` Boşta CPU/RAM Tüketimi**
   - İpe asılan her Polaroid fotoğraf, video karesi ve yapışkan not; yerçekimi, açısal hız ve komşu kart etkileşimiyle gerçek bir ip üzerindeymiş gibi sallanır.
   - Fare imlecini kartların üzerinden geçirdiğinizde hızınıza göre doğal rüzgar salınımı oluşur; kartlar durduğunda fizik motoru otomatik olarak uyku moduna geçer (`%0 CPU`).
2. **🎨 Polaroid Altında Otomatik 5'li Canlı Renk Paleti**
   - İpe asılan her görselin en baskın 5 rengi otomatik olarak Polaroid kartın altındaki beyaz şeride dizilir.
   - Herhangi bir renk kutucuğuna **sol tıklayarak `#HEX`**, **sağ tıklayarak `RGB(r, g, b)`** kodunu anında panoya kopyalayabilirsiniz.
3. **🏗️ 3ds Max & AutoCAD İçin "İpten Çekerken Dönüştür" (`Auto-Convert on Drag`)**
   - Tarayıcıdan veya WhatsApp'tan gelen `.webp`, `.avif`, `.heic`, `.bmp` görsellerini ya da panodaki ekran görüntülerini ipe asın; **ipten aşağı doğru tutup 3ds Max, AutoCAD, Revit veya Photoshop içine sürüklediğiniz anda** otomatik olarak tertemiz `.png` dosyasına dönüşerek bırakılır.
4. **🎬 Circle Mix Record Canlı Film Şeridi & İp Üstünde Anında Kurgu**
   - `Circle Mix Record` ile çektiğiniz yeni videoları ve kapak fotoğraflarını otomatik algılayıp ipe mandallar.
   - **⚡ İptekileri Birleştir (MP4):** Seçili video/görsel mandallarını editör açmadan arka planda `ffmpeg` ile tek bir `60 FPS MP4` videosuna birleştirip yeni bir mandalla ipe asar.
   - **📸 Kare Sök:** İpteki herhangi bir videodan tek tıkla kayıpsız `.png` kapak karesi çıkarır.
5. **🤖 Antigravity Tek Tıkla AI Görev Paketi (`🚀 Antigravity Paketi`)**
   - O sırada açık olan **3ds Max, AutoCAD, Revit, Blender, SketchUp** pencere ve dosya adlarını otomatik algılar; ipteki seçili görsellerin tam yollarını (`file:///...`), 5'li renk paletlerini ve mandal notlarını tek bir hazır komut metni halinde panoya kopyalar.
6. **✂️ Canlı Ekran Kesiti (`Ctrl + Shift + X`), Kırmızı Kalem (`Redline`) ve X-Ray OCR**
   - `Ctrl + Shift + X` ile ekranın istediğiniz bölgesini kesip anında ipe mandallayın.
   - Kartlara çift tıklayarak üzerine **ok, çerçeve, serbest çizim veya etiket** ekleyin (`Redline`) ya da **🔍 X-Ray** ile görseldeki yazıları/ölçüleri metne dönüştürün.

---

## ⌨️ Kısayollar

| Kısayol | İşlev |
|---|---|
| `Ctrl + Shift + L` | CircleLine İpini Göster / Gizle (Aç / Kapat) |
| `Ctrl + Shift + X` | Ekran Kesiti Al ve İpe Mandalla |
| `Ctrl + Shift + V` | Panodaki Görseli / Metni / Dosyayı İpe Mandalla |
| `Fare Tekerleği` | İp Üzerinde Makarayı Sağa / Sola Kaydır |
| `Aşağı Sürükle` | Kartı `.PNG` / `.MP4` Dosyası Olarak Başka Programa Bırak |
| `Sağa/Sola Sürükle` | Mandalın İp Üzerindeki Sırasını Değiştir |

---

## 🛠️ Derleme

Harici NuGet paketi veya SDK gerektirmez; Windows ile gelen yerleşik `.NET Framework 4` derleyicisi (`csc.exe`) ile derlenir:

```powershell
.\build.ps1
```

---

## ⚖️ Lisans ve Marka Beyanı (Trademark Disclaimer)

- **Lisans:** Bu proje **MIT Lisansı** altında açık kaynak olarak sunulmaktadır (`Copyright (c) 2026 thezencirs`). Ayrıntılar için [LICENSE](LICENSE) dosyasına bakın.
- **Marka Beyanı:** `CircleLine`, **thezencirs** tarafından sıfırdan geliştirilmiş bağımsız ve özgün bir yazılımdır. Dokümantasyonda adı geçen *Windows*, *3ds Max*, *AutoCAD*, *Revit*, *SketchUp*, *Blender*, *Tendedero* veya *ClipBoardLine* ilgili sahiplerinin ticari markalarıdır; bu projeyle herhangi bir resmi ortaklık veya bağlılıkları yoktur.
