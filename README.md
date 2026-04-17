# Elite Soft erwin Data Modeler Add-In

## Genel Bilgi

Bu add-in, erwin Data Modeler için geliştirilmiş bir COM bileşenidir. Model yapılandırma, glossary validasyonu, domain validasyonu, TABLE_TYPE yönetimi ve tablo kopyalama işlevleri sağlar.

## Sistem Gereksinimleri

- erwin Data Modeler 15.0 veya üzeri (64-bit)
- .NET 10 Runtime (Windows Desktop)
- .NET 10 SDK (sadece build için)
- Windows 10/11 veya Windows Server

## Dosya Yapısı

```
erwin-addin/
├── ErwinAddIn.cs                    # COM Add-In giriş noktası
├── ModelConfigForm.cs               # Ana UI formu
├── ModelConfigForm.Designer.cs      # Form tasarımcı kodu
├── ErwinUtilities.cs                # Yardımcı fonksiyonlar
├── DDLGenerator.cs                  # DDL oluşturma
├── ErwinAddIn.csproj                # Proje dosyası (net10.0-windows, EnableComHosting)
├── erwin-addin.sln                  # Solution dosyası
├── build-and-run.ps1                # Build + install + COM register (dev workflow)
├── package.ps1                      # Dağıtım için ZIP/EXE paketleme
├── References/
│   └── EAL.dll                      # erwin API Library (SCAPI)
├── Forms/
│   ├── DbConnectionForm.cs              # DB bağlantı (From DB modu)
│   └── QuestionWizardForm.cs            # Soru sihirbazı
├── Services/
│   ├── ValidationCoordinatorService.cs  # Merkezi validasyon koordinatörü
│   ├── ColumnValidationService.cs       # Glossary validasyon servisi
│   ├── TableTypeMonitorService.cs       # TABLE_TYPE izleme servisi
│   ├── DatabaseService.cs               # Multi-database bağlantı yönetimi
│   ├── GlossaryService.cs               # Glossary veritabanı servisi
│   ├── DomainDefService.cs              # Domain tanımları servisi
│   ├── PredefinedColumnService.cs       # Önceden tanımlı kolon servisi
│   ├── DdlGenerationService.cs          # DDL üretim ve karşılaştırma
│   ├── DependencySetRuntimeService.cs   # Dependency set runtime
│   ├── PropertyApplicatorService.cs     # Property uygulayıcı
│   ├── UdpDefinitionService.cs / UdpRuntimeService.cs / UdpDependencyService.cs / UdpValidationEngine.cs
│   ├── NamingStandardService.cs / NamingValidationEngine.cs
│   ├── CorporateContextService.cs / RegistryBootstrapService.cs / PasswordEncryptionService.cs
│   ├── AddInPropertyMetadataService.cs
│   └── Win32Helper.cs                   # Win32/IAccessible yardımcıları
├── tools/
│   └── DdlHelper/                       # SCAPI'yi ayrı process'te çalıştıran yardımcı
├── scripts/
│   ├── insert_test_glossary.sql         # Test glossary verisi seed scripti
│   ├── install.ps1                      # Son kullanıcı kurulum scripti
│   ├── erwin-launcher.ps1               # erwin launcher
│   ├── autostart-watcher.ps1            # Otomatik başlatma watcher
│   └── erwin-injector/                  # DLL injection auto-load (NativeAOT)
├── installer/
│   └── install.ps1                      # Paketlenmiş installer scripti
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
.\build-and-run.ps1
```

Script şunları yapar:
1. Administrator yetkisi yoksa otomatik yükseltme yapar
2. erwin (ve takılı kalmış DdlHelper) process'lerini kapatır
3. Projeyi Release modunda build eder + DdlHelper publish eder
4. `%LOCALAPPDATA%\EliteSoft\ErwinAddIn` altına kopyalar
5. Eski COM host kaydını siler ve yeni `*.comhost.dll` dosyasını `regsvr32` ile kaydeder

### Manuel Build

```powershell
dotnet build erwin-addin.sln -c Release
```

Çıktı: `bin\Release\net10.0-windows\EliteSoft.Erwin.AddIn.dll` ve `EliteSoft.Erwin.AddIn.comhost.dll`.

### Manuel COM Kayıt

.NET 10 COM hosting kullanıldığı için kayıt `regsvr32` ile `comhost.dll` üzerinden yapılır (regasm DEĞİL):

```powershell
# Administrator CMD/PowerShell gerekli
regsvr32.exe "%LOCALAPPDATA%\EliteSoft\ErwinAddIn\EliteSoft.Erwin.AddIn.comhost.dll"
```

### COM Kaydını Silme

```powershell
regsvr32.exe /u "%LOCALAPPDATA%\EliteSoft\ErwinAddIn\EliteSoft.Erwin.AddIn.comhost.dll"
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
| **Assembly** | `EliteSoft.Erwin.AddIn.dll` (+ `EliteSoft.Erwin.AddIn.comhost.dll`) |
| **Platform** | x64 |
| **Framework** | .NET 10 (windows desktop, COM hosting) |

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

### NuGet Paketleri (bkz. ErwinAddIn.csproj)
- **Microsoft.Data.SqlClient** v6.1.4 - SQL Server bağlantısı
- **Npgsql** v10.0.2 - PostgreSQL bağlantısı
- **Oracle.ManagedDataAccess.Core** v23.26.100 - Oracle bağlantısı
- **System.Data.Odbc** v10.0.6 - ODBC bağlantısı
- **ExcelDataReader** v3.7.0 + **ExcelDataReader.DataSet** v3.7.0 - Excel okuma

### Project References
- `..\erwin-admin\MetaShared\MetaShared.csproj`
- `..\x-hw-licensing\xLicense\xLicense.csproj` - lisans doğrulama

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
- COM kaydı yapılmamış olabilir, `build-and-run.ps1` çalıştırın

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
