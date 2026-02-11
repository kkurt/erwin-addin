# Elite Soft erwin Data Modeler Add-In

## Genel Bilgi

Bu add-in, erwin Data Modeler için geliştirilmiş bir COM bileşenidir. Model yapılandırma, glossary validasyonu, domain validasyonu, TABLE_TYPE yönetimi ve tablo kopyalama işlevleri sağlar.

## Sistem Gereksinimleri

- erwin Data Modeler 15.0 veya üzeri (64-bit)
- .NET Framework 4.8
- Windows 10/11 veya Windows Server

## Dosya Yapısı

```
erwin-addin/
├── ErwinAddIn.cs                    # COM Add-In giriş noktası
├── ModelConfigForm.cs               # Ana UI formu
├── ModelConfigForm.Designer.cs      # Form tasarımcı kodu
├── ErwinUtilities.cs                # Yardımcı fonksiyonlar
├── DDLGenerator.cs                  # DDL oluşturma
├── ErwinAddIn.csproj                # Proje dosyası
├── erwin-addin.sln                  # Solution dosyası
├── build-and-register.ps1           # Build ve COM kayıt scripti
├── References/
│   └── EAL.dll                      # erwin API Library (SCAPI)
├── Services/
│   ├── ValidationCoordinatorService.cs  # Merkezi validasyon koordinatörü
│   ├── ColumnValidationService.cs       # Glossary validasyon servisi
│   ├── TableTypeMonitorService.cs       # TABLE_TYPE izleme servisi
│   ├── DatabaseService.cs               # Multi-database bağlantı yönetimi
│   ├── GlossaryService.cs               # Glossary veritabanı servisi
│   ├── GlossaryConnectionService.cs     # Glossary bağlantı yönetimi
│   ├── DomainDefService.cs              # Domain tanımları servisi
│   ├── TableTypeService.cs              # TABLE_TYPE veritabanı servisi
│   └── PredefinedColumnService.cs       # Önceden tanımlı kolon servisi
├── scripts/
│   └── insert_test_glossary.sql         # Test glossary verisi seed scripti
└── README.md
```

## Özellikler

### 1. Model Yapılandırma
- Açık modeller arasında seçim yapma (dropdown)
- Database Name ve Schema Name giriş alanları
- Otomatik Name oluşturma: `DatabaseName.SchemaName`
- Otomatik Code oluşturma: `DatabaseName_SchemaName`
- Subject_Area > Definition alanına yapılandırma kaydetme

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
- TABLE_TYPE UDP otomatik oluşturma (metamodel seviyesinde)
- Tablo tipi değişikliklerinde PREDEFINED_COLUMN ekleme
- LOG, PARAMETER, TRANSACTION, HISTORY tipleri desteği

### 5. Tablo Kopyalama (Table Processes)
- Archive tablo oluşturma (_ARCHIVE suffix)
- Isolated tablo oluşturma (_ISOLATED suffix)
- Tüm kolonları ve özellikleri kopyalama

### 6. Konsolide Validasyon Popup
- Glossary ve Domain hatalarını tek pencerede gösterme
- Tek timer ile senkronize kontrol (1.5 saniye interval)

## Build ve Kayıt

### Otomatik (Önerilen)

PowerShell'den çalıştırın (script otomatik olarak Administrator yetkisi ister):

```powershell
cd c:\Users\Kursat\Repos\erwin-addin
.\build-and-register.ps1
```

Script şunları yapar:
1. Administrator yetkisi yoksa otomatik yükseltme yapar
2. erwin çalışıyorsa kapatma seçeneği sunar
3. Projeyi Release modunda build eder
4. Eski COM kaydını siler (varsa)
5. Yeni COM bileşenini Type Library ile kaydeder

### Manuel Build

```powershell
dotnet build erwin-addin.sln -c Release
```

### Manuel COM Kayıt

```powershell
# Administrator CMD/PowerShell gerekli
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe bin\Release\net48\EliteSoft.Erwin.AddIn.dll /codebase /tlb:bin\Release\net48\EliteSoft.Erwin.AddIn.tlb
```

### COM Kaydını Silme

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe bin\Release\net48\EliteSoft.Erwin.AddIn.dll /unregister
```

## erwin'e Ekleme

1. erwin Data Modeler'ı açın
2. **Tools > Add-Ins > Add-In Manager** bölümüne gidin
3. Yeni item ekleyin (+ ikonu)
4. **Menu Type:** Command > Com Object
5. **Name:** Elite Soft Model Configurator (veya istediğiniz isim)
6. **COM ProgID:** `EliteSoft.Erwin.AddIn`
7. **Function Name:** `Execute`
8. **Save** tıklayın

## Kullanım

1. erwin'de bir model açın
2. **Tools > Add-Ins > [Add-In Adı]** tıklayın
3. Form açılacak ve açık modelleri listeleyecek
4. Model seçin
5. Database Name ve Schema Name girin
6. Name ve Code otomatik oluşturulacak
7. **Apply** tıklayın

### Kayıt Edilen Veriler

Değerler Subject_Area nesnesinin Definition alanına kaydedilir:

```
DatabaseName=<değer>;SchemaName=<değer>;FullName=<değer>;Code=<değer>
```

## Teknik Detaylar

### COM Bileşen Bilgileri

| Özellik | Değer |
|---------|-------|
| **ProgID** | `EliteSoft.Erwin.AddIn` |
| **GUID** | `A1B2C3D4-E5F6-7890-ABCD-EF1234567890` |
| **Namespace** | `EliteSoft.Erwin.AddIn` |
| **Assembly** | `EliteSoft.Erwin.AddIn.dll` |
| **Platform** | x64 |
| **Framework** | .NET Framework 4.8 |

### SCAPI Bağlantısı

Add-in, erwin SCAPI (Script Client API) kullanır:

```csharp
// SCAPI bağlantısı
Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
dynamic scapi = Activator.CreateInstance(scapiType);

// Aktif modelleri al
dynamic persistenceUnits = scapi.PersistenceUnits;

// Session oluştur
dynamic session = scapi.Sessions.Add();
session.Open(model);

// Transaction başlat
int transId = session.BeginNamedTransaction("TransactionName");

// İşlem yap...

// Commit
session.CommitTransaction(transId);

// Session kapat
session.Close();
```

### UDP Yönetimi

TABLE_TYPE gibi User Defined Property'ler metamodel seviyesinde oluşturulur. ModelConfigForm, add-in başlatılırken metamodel session açarak gerekli UDP'leri oluşturur. Validasyon servisleri bu UDP'leri kullanarak kolon ve tablo özelliklerini yönetir.

### Desteklenen Veritabanları

Glossary ve yapılandırma verileri için:
- Microsoft SQL Server (MSSQL)
- PostgreSQL
- Oracle

## Bağımlılıklar

### NuGet Paketleri
- **Npgsql** v4.1.13 - PostgreSQL bağlantısı
- **Oracle.ManagedDataAccess** v21.15.0 - Oracle bağlantısı

### Referanslar
- **EAL.dll** - erwin API Library (SCAPI)
- **System.Windows.Forms** - WinForms UI

## Önemli Notlar

1. **erwin Kapalı Build:** DLL erwin tarafından kilitlenebilir, build öncesi erwin'i kapatın
2. **Administrator Gerekli:** COM kaydı için Administrator yetkisi gerekli (script otomatik ister)
3. **64-bit:** erwin 64-bit olduğu için add-in de x64 olarak derlenmeli
4. **SCAPI ProgID:** erwin 15.x için bile `erwin9.SCAPI` ProgID'si kullanılır

## Hata Ayıklama

### "Cannot find ErwinAddIn.TableCreator Component"
- ProgID yanlış girilmiş. Doğru ProgID: `EliteSoft.Erwin.AddIn`
- COM kaydı yapılmamış olabilir, `build-and-register.ps1` çalıştırın

### "Failed to invoke Execute Method"
- Function Name doğru girilmemiş olabilir (`Execute` olmalı)
- COM kaydı yapılmamış olabilir

### "Could not find erwin SCAPI"
- erwin kurulu değil veya farklı versiyon
- ProgID farklı olabilir (erwin9.SCAPI, erwin15.SCAPI vb. deneyin)

### Build sırasında DLL kilitli hatası
- erwin'i kapatın
- Task Manager'dan erwin process'ini kontrol edin

## API Referansı

erwin SCAPI dokümantasyonu:
https://bookshelf.erwin.com/bookshelf/public_html/Content/PDFs/API%20Reference.pdf
