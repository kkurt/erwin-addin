# Elite Soft erwin Data Modeler Add-In

## Proje Genel Bilgileri

| Özellik | Değer |
|---------|-------|
| **Proje Adı** | EliteSoft.Erwin.AddIn |
| **Platform** | .NET Framework 4.8 (x64) |
| **Tip** | COM Visible DLL (erwin Add-In) |
| **COM ProgID** | `EliteSoft.Erwin.AddIn` |
| **Namespace** | EliteSoft.Erwin.AddIn |

## Dosya Bazlı Satır Sayıları

| Dosya | Satır | Açıklama |
|-------|-------|----------|
| ModelConfigForm.cs | 1,564 | Ana UI formu |
| ModelConfigForm.Designer.cs | 811 | Form tasarımcı kodu |
| ColumnValidationService.cs | 696 | Glossary validasyon servisi |
| ValidationCoordinatorService.cs | 680 | Merkezi validasyon koordinatörü |
| TableTypeMonitorService.cs | 617 | TABLE_TYPE izleme servisi |
| ErwinUtilities.cs | 445 | Yardımcı fonksiyonlar |
| DDLGenerator.cs | 349 | DDL oluşturma |
| TableTypeService.cs | 318 | TABLE_TYPE veritabanı servisi |
| DomainDefService.cs | 280 | DOMAIN_DEF veritabanı servisi |
| GlossaryService.cs | 216 | GLOSSARY veritabanı servisi |
| GlossaryConnectionService.cs | 190 | Glossary bağlantı yönetimi |
| PredefinedColumnService.cs | 174 | PREDEFINED_COLUMN servisi |
| DatabaseService.cs | 165 | Multi-database bağlantı yönetimi |
| ErwinAddIn.cs | 79 | Add-In giriş noktası |

## Bağımlılıklar

### NuGet Paketleri
- **Npgsql** v4.1.13 - PostgreSQL bağlantısı
- **Oracle.ManagedDataAccess** v21.15.0 - Oracle bağlantısı

### Referanslar
- **EAL.dll** - erwin API Library (SCAPI)
- **System.Windows.Forms** - WinForms UI

## Desteklenen Veritabanları
- Microsoft SQL Server (MSSQL)
- PostgreSQL
- Oracle

## Mimari

### Servis Katmanı (Services/)

```
Services/
├── ValidationCoordinatorService.cs  # Merkezi validasyon (tek timer)
├── ColumnValidationService.cs       # Glossary validasyonu
├── TableTypeMonitorService.cs       # TABLE_TYPE izleme
├── DatabaseService.cs               # Veritabanı bağlantı yönetimi
├── GlossaryService.cs               # Glossary verileri
├── GlossaryConnectionService.cs     # Glossary bağlantı bilgileri
├── DomainDefService.cs              # Domain tanımları
├── TableTypeService.cs              # Tablo tipleri
└── PredefinedColumnService.cs       # Önceden tanımlı kolonlar
```

### Ana Sınıflar

| Sınıf | Sorumluluk |
|-------|------------|
| `ErwinAddIn` | COM Add-In giriş noktası (ProgID: `EliteSoft.Erwin.AddIn`) |
| `ModelConfigForm` | Ana kullanıcı arayüzü |
| `ValidationCoordinatorService` | Glossary + Domain validasyonu (tek timer) |
| `TableTypeMonitorService` | TABLE_TYPE UDP değişiklik izleme |
| `DDLGenerator` | DDL script oluşturma |
| `ErwinUtilities` | Model işlemleri yardımcı fonksiyonları |

## Özellikler

### 1. Model Yapılandırma
- Database ve Schema adı ayarlama
- Model adı güncelleme

### 2. Glossary Validasyonu
- Kolon adlarını GLOSSARY tablosuna karşı doğrulama
- Yeni/değişen kolon adları için otomatik kontrol
- Bulunamayan kolonlar için uyarı
- Glossary'den Physical_Data_Type otomatik atama

### 3. Domain Validasyonu
- Parent Domain seçildiğinde kolon adı pattern kontrolü
- DOMAIN_DEF tablosundaki REGEXP ile eşleşme kontrolü
- Otomatik Description ve Data Type atama

### 4. TABLE_TYPE Yönetimi
- Tablo tipi UDP otomatik oluşturma (metamodel seviyesinde)
- Tablo tipi değişikliklerinde PREDEFINED_COLUMN ekleme
- LOG, PARAMETER, TRANSACTION, HISTORY tipleri desteği

### 5. Tablo Kopyalama (Table Processes)
- Archive tablo oluşturma (_ARCHIVE suffix)
- Isolated tablo oluşturma (_ISOLATED suffix)
- Tüm kolonları ve özellikleri kopyalama

### 6. Konsolide Validasyon Popup
- Glossary ve Domain hatalarını tek pencerede gösterme
- Tek timer ile senkronize kontrol

## Teknik Notlar

### erwin SCAPI Kullanımı
- Session yönetimi: `_scapi.Sessions.Add()` ve `session.Open(model)`
- Transaction: `BeginNamedTransaction`, `CommitTransaction`, `RollbackTransaction`
- Model nesneleri: `modelObjects.Collect(parent, "ObjectType")`
- Attribute oluşturma: `modelObjects.Collect(entity).Add("Attribute")`
- **SCAPI ProgID:** erwin 15.x için bile `erwin9.SCAPI` kullanılır

### UDP Yönetimi
- TABLE_TYPE UDP metamodel seviyesinde oluşturulur
- ModelConfigForm add-in başlatılırken metamodel session açarak UDP'leri oluşturur
- Validasyon servisleri bu UDP'leri kullanarak kolon ve tablo özelliklerini yönetir

### Validasyon Zamanlama
- Tek merkezi timer: 1.5 saniye interval
- Değişiklik tespiti: Snapshot karşılaştırma
- Batch operasyonlarda suspend/resume mekanizması

---
*Son güncelleme: Şubat 2026*
