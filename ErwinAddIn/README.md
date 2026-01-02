# erwin Data Modeler Add-In

## Genel Bilgi

Bu add-in, erwin Data Modeler icin gelistirilmis bir COM bilesenidir. Model yapilandirma (Database Name, Schema Name, Name, Code) islevleri saglar ve degerleri Model Properties > Definition alanina kaydeder.

## Sistem Gereksinimleri

- erwin Data Modeler 15.0 veya uzeri (64-bit)
- .NET Framework 4.8
- Windows 10/11 veya Windows Server

## Dosya Yapisi

```
ErwinAddIn/
├── TableCreatorAddIn.cs         # COM Add-In ana sinifi
├── TableCreatorForm.cs          # Ana form (UI)
├── TableCreatorForm.Designer.cs # Form designer
├── ErwinAddIn.csproj            # Proje dosyasi
├── build-and-register.ps1       # Build ve kayit scripti
└── README.md                    # Bu dosya
```

## Ozellikler

### Model Yapilandirma
- Acik modeller arasinda secim yapma (dropdown)
- Database Name ve Schema Name giris alanlari
- Otomatik Name olusturma: `DatabaseName.SchemaName`
- Otomatik Code olusturma: `DatabaseName_SchemaName`

### Veri Depolama
- Subject_Area > Definition alanina yapilandirma kaydetme
- `key=value;` formatinda veri saklama
- Otomatik yukleme: Daha once kaydedilen degerler otomatik yuklenir

## Build ve Kayit

### Otomatik (Onerilen)

PowerShell'i **Administrator** olarak acin:

```powershell
cd d:\Projects\erwin-addon\ErwinAddIn
.\build-and-register.ps1
```

Script sunlari yapar:
1. erwin calisiyorsa kapatma secenegi sunar
2. Projeyi Release modunda build eder
3. Eski COM kaydini siler (varsa)
4. Yeni COM bilesenini Type Library ile kaydeder

### Manuel Build

```powershell
dotnet build -c Release
```

### Manuel COM Kayit

```powershell
# Administrator CMD/PowerShell gerekli
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe bin\Release\net48\ErwinAddIn.dll /codebase /tlb:bin\Release\net48\ErwinAddIn.tlb
```

### COM Kaydini Silme

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe bin\Release\net48\ErwinAddIn.dll /unregister
```

## erwin'e Ekleme

1. erwin Data Modeler'i acin
2. **Tools > Add-Ins > Add-In Manager** gidin
3. Yeni item ekleyin (+ ikonu)
4. **Menu Type:** Command > Com Object
5. **Name:** Model Configurator (veya istediginiz isim)
6. **COM ProgID:** `ErwinAddIn.TableCreator`
7. **Function Name:** `Execute`
8. **Save** tiklayin

## Kullanim

1. erwin'de bir model acin
2. **Tools > Add-Ins > [Add-In Adi]** tiklayin
3. Form acilacak ve acik modelleri listeleyecek
4. Model secin
5. Database Name ve Schema Name girin
6. Name ve Code otomatik olusturulacak
7. **Apply** tiklayin

### Kayit Edilen Veriler

Degerler Subject_Area nesnesinin Definition alanina kaydedilir:

```
DatabaseName=<deger>;SchemaName=<deger>;FullName=<deger>;Code=<deger>
```

Bu degerleri gormek icin:
1. erwin'de model acacin
2. Subject Area'ya sag tiklayin > Properties
3. Definition sekmesine gidin

## Teknik Detaylar

### SCAPI Baglantisi

Add-in, erwin SCAPI (Script Client API) kullanir:

```csharp
// SCAPI baglantisi
Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
dynamic scapi = Activator.CreateInstance(scapiType);

// Aktif modelleri al
dynamic persistenceUnits = scapi.PersistenceUnits;

// Session olustur
dynamic session = scapi.Sessions.Add();
session.Open(model);

// Transaction baslat
int transId = session.BeginNamedTransaction("TransactionName");

// Islem yap...

// Commit
session.CommitTransaction(transId);

// Session kapat
session.Close();
```

### Subject_Area Nesnesine Erisim

```csharp
dynamic modelObjects = session.ModelObjects;
dynamic saCollection = modelObjects.Collect("Subject_Area");
if (saCollection.Count > 0)
{
    dynamic subjectArea = saCollection.Item(0);

    // Definition alanina yaz
    subjectArea.Properties("Definition").Value = "key=value;...";
}
```

## UDP Hakkinda Onemli Not

erwin SCAPI, late binding (dynamic) ile erisimde UDP (User Defined Properties) olusturma ve deger atama islevlerini **desteklememektedir**. Asagidaki yontemler test edilmis ve calismamistir:

- `scapi.CreateUserProperty()` - Metod bulunamadi
- `scapi.SetUserPropertyValue()` - Metod bulunamadi
- `session.CreateUserProperty()` - Metod bulunamadi
- `scapi.GetPropertyBag()` - Metod bulunamadi
- `Properties("UDP::Name")` - Calismadi
- M1 Level Property_Type olusturma - Tanimlar olusturuldu ama degerler atanamadi

Bu nedenle yapilandirma degerleri **Definition** alanina kaydedilmektedir. Definition alani guvenilir ve her zaman calisan bir cozumdur.

UDP islevselligi icin ABORAPI veya typed wrapper API'ler kullanilmasi gerekmektedir, ancak bu API'ler erwin kurulumunda ayri olarak gelmektedir.

## Onemli Notlar

1. **erwin Kapali Build:** DLL erwin tarafindan kilitlenebilir, build oncesi erwin'i kapatin
2. **Administrator Gerekli:** COM kaydi icin Administrator yetkisi gerekli
3. **64-bit:** erwin 64-bit oldugu icin add-in de x64 olarak derlenmeli
4. **ProgID:** erwin 15.0 icin bile `erwin9.SCAPI` ProgID'si kullanilir

## Hata Ayiklama

### "Failed to invoke Execute Method"
- Function Name dogru girilmemis olabilir
- COM kaydi yapilmamis olabilir

### "Could not find erwin SCAPI"
- erwin kurulu degil veya farkli versiyon
- ProgID farkli olabilir (erwin9.SCAPI, erwin15.SCAPI vb. deneyin)

### Build sirasinda DLL kilitli hatasi
- erwin'i kapatin
- Task Manager'dan erwin process'ini kontrol edin

## API Referansi

erwin SCAPI dokumantasyonu:
https://bookshelf.erwin.com/bookshelf/public_html/Content/PDFs/API%20Reference.pdf

## Versiyon Gecmisi

- **v1.0** - Ilk surum: Entity olusturma, model yapilandirma
- **v1.1** - Model Properties > Definition tab desteyi eklendi
- **v1.2** - UDP (User Defined Properties) desteyi eklendi (denendi)
- **v1.3** - Sadece Definition alani kullanilarak sadelelestirildi
  - UDP yaklasimlari SCAPI late binding ile calismadigi icin kaldirildi
  - Definition alani guvenilir depolama olarak kullaniliyor
  - Kod temizlendi ve sadelelestirildi
