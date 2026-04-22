# Claude Code Görevi: erwin DM İki Model Arası ALTER DDL Üretici

## 1. Bağlam ve Hedef

Ben bir data architect'im ve erwin Data Modeler ile yoğun çalışıyorum. İki farklı `.erwin` model dosyası arasındaki farkı programatik olarak tespit edip, sol (eski/baseline) modelden sağ (yeni/hedef) modele geçiş için gereken **ALTER DDL scriptini üretecek bir komut satırı aracı** geliştirmek istiyorum.

Kritik gereksinim: erwin DM UI'ı açılmadan, tamamen COM API çağrıları üzerinden çalışacak. Tam otomasyon istiyorum — CI/CD pipeline'a sokabilmeliyim.

Strateji: CompleteCompare + metamodel API + Forward Engineer zincirini tamamen erwin'in kendi API'si üzerinden kuracağız. Harici schema-diff araçları, database round-trip, ya da başka dolaylı yollar bu işte yok — tek yol saf erwin API.

## 2. Ortam ve Kısıtlar

**Sabit:**
- erwin Data Modeler **15.2** (en güncel API yüzeyi — bu versiyonun dokümantasyonunu kullan)
- Windows 10/11 workstation, erwin kurulu ve lisanslı
- **C# / .NET 10** — tek dil, alternatif yok
- Target DBMS: modelden okunan Target_Server değerini kullan, sabitleme

**Örnek dosyalar — test için bunları kullan, kendin sentetik dosya yaratma:**
```
c:\work\FromPowerDesignerRepoX\
  ├── AchModel_v1.erwin        ← base/sol model
  ├── AchModel_v2.erwin        ← target/sağ model
  ├── AchModel_v1.xml          ← aynı modelin XML export'u
  └── AchModel_v2.xml          ← aynı modelin XML export'u
```
XML sürümleri Faz 0 araştırmasında metamodel yapısını metin editöründe gözle inceleyebilmen için faydalı olacak (tag isimleri, property isimleri, object hierarchy). Çalışma zamanında `.erwin` dosyalarını kullan.

Gerçek dosya isimlerini ilk incelemende `dir` komutu ile teyit et — yukarıdaki isimlendirme tahmindir, "v1/v2" suffix farklı olabilir. Gerçek isimleri bulup kullan.

**Davranışsal kısıtlar:**
- erwin UI hiçbir şartta görünmeyecek (sessiz/headless mod)
- Türkçe karakter içeren tablo/kolon isimleri kayıpsız işlenmeli (encoding: UTF-8 I/O — model içi Windows-1254 olabilir, araştır)
- Idempotent: aynı iki model için iki ayrı çalıştırma bit-identical alter DDL üretmeli (timestamp, random ID, GUID suffix yok)

## 3. Hata Yönetimi Felsefesi (MUTLAK)

**Hatalar yutulmayacak. Nokta.**

- Boş `catch` bloğu yasak — analyzer ile derleme hatası olarak enforce et
- `catch (Exception) { /* ignore */ }` = suç
- "Log-and-continue" mantığı yasak; hata varsa propagate et, process exit code'u da hatayı yansıtsın
- COM HRESULT hataları `Marshal.GetExceptionForHR` ile anlamlı .NET exception'a çevrilmeli, ham HRESULT'ı fırlatma
- Meşru bir recovery mekanizması varsa (örn. retry with backoff), her adım loglanmalı ve max attempt aşılırsa exception fırlatılmalı
- Transaction rollback'te bile, rollback nedeni olan orijinal exception kaybolmamalı (inner exception olarak sakla veya `AggregateException` kullan)
- Her exception'ın stack trace'i log'a düşmeli — `ex.Message` tek başına yetmez
- `#pragma warning disable` ile warning bastırma — gerçek problemi çöz
- Top-level `Main` içinde sadece final logging + exit code mapping için genel catch olabilir; orada bile exception kaybolmadan loglanmalı ve stack trace yazılmalı

Sebebi: erwin COM katmanında sessiz hata = saatlerce debug. Bir tablo eksik DDL'de, sebep aslında 3 saat önce yutulmuş bir `E_FAIL` HRESULT'ı olmasın.

## 4. Teyit Edilmiş API Gerçekleri (Bunları Tekrar Araştırma)

### 4.1 CompleteCompare
```
HRESULT ISCPersistenceUnit::CompleteCompare(
    VARIANT CCLeftModelPath,    // sol .erwin dosya yolu
    VARIANT CCRightModelPath,   // sağ .erwin dosya yolu
    VARIANT CCOutputPath,       // çıktı .xls dosya yolu
    VARIANT CCOptionSet,        // "Speed" | "Standard" | "Advance" | XML path
    VARIANT CCCompareLevel,     // "LP" | "L" | "P" | "DB"
    VARIANT EventID,            // "" (dahili kullanım)
    VARIANT_BOOL* ppVal
);
```
- Sadece diske kaydedilmiş modellerde çalışır
- Çıktı format: XLS (DDL değil — fark raporu)

### 4.2 Forward Engineer
```
HRESULT ISCPersistenceUnit::FEModel_DDL(
    VARIANT Locator,    // çıktı .sql/.ddl yolu
    VARIANT OptionXML,  // FE option set XML yolu
    VARIANT_BOOL* ppVal
);
```
- Tek bir modelden tam DDL üretir (alter değil — alter üretimi için araştırma gerekli, bkz. Faz 0)

### 4.3 ProgID'ler ve Hiyerarşi
- Application: `ERwin9.SCAPI.9.0` (isimde "9" olması yanıltıcı — tüm modern versiyonlarda geçerli, 15.2 dahil)
- PropertyBag: `ERwin9.SCAPI.PropertyBag.9.0`
- Tier'lar: Application → PersistenceUnits → ModelSets → Sessions → ModelObjects → Properties

### 4.4 Resmi Dokümantasyon
- 15.2 için API Reference PDF'ini indir. Önce bu URL'i dene: `https://bookshelf.erwin.com/bookshelf/public_html/15.2/Content/PDFs/API%20Reference.pdf`. 404 dönerse `15.0` veya `2021R1` sürüm dokümanına düş. Hangi sürümü kullandığını raporda belirt.

## 5. FAZ 0 — Araştırma (Bu aşamada hiç kod YAZMA)

Aşağıdaki soruları sırayla cevapla. Her birinde kaynak göster (dokümantasyon URL'i, dosya path'i, COM interface inspector output'u). Bulguları tek bir `docs/research_findings.md` dosyasında topla.

### 5.1 Metot Enumerasyonu
- erwin 15.2 install dizinindeki Samples klasörünü listele (tipik: `C:\Program Files\erwin\Data Modeler r15.2\Samples\` veya Program Files (x86) altı). `erwinSpy_Addin.NET`, `Sample Client` projelerini bul.
- `OleView.exe` veya `tlbimp.exe` ile `ERwin9.SCAPI.9.0` type library'sini dump et. `ISCPersistenceUnit` interface'inin **tüm** metotlarını listele.
- Özellikle şu metotları ara ve varsa imzasını çıkar: `FEModel_AlterDDL`, `FEModel_Alter`, `GenerateAlterScript`, `ApplyCompareResult`, `ResolveDifferences`, `ImportChanges`.
- Bulunan her alter-related metot için PropertyBag parametresindeki geçerli key'leri listele.

### 5.2 CompleteCompare Option Set XML Yapısı
- GUI'de Complete Compare dialog'undan bir option set'i XML olarak export et (`Save Option Set As XML`). Örnek dosyayı incele.
- XML içinde `AutoMerge`, `AutoImport`, `ResolveDirection`, `GenerateAlterScript` benzeri flag'ler var mı?
- "Advance" preset'in XML eşdeğeri nedir? "Standard"dan farkı? Hangi object class'lar ve property'ler kapsam dışında?

### 5.3 XLS Diff Raporunun Yapısı
- CompleteCompare'i `AchModel` v1 ve v2 üzerinde çalıştır (gerçek dosya isimlerini `dir c:\work\FromPowerDesignerRepoX\` ile tespit et), üretilen XLS'i `ClosedXML` (NuGet: `ClosedXML`) ile aç.
- Raporla:
  - Sheet isimleri
  - Kolon yapısı (Object ID, Class, Property, Left Value, Right Value, Change Type?)
  - Değişiklik türleri kodlanmış mı, string mi? (örn. "ADD"/"DELETE"/"MODIFY")
  - Parent-child ilişkisi (tablo → kolon) nasıl ifade edilmiş?
  - ObjectID formatı ve bu ID'lerin metamodel'deki karşılıkları

### 5.4 Alter Script Üretim Mekanizması
- erwin GUI'de: Complete Compare → Resolve Differences → Import → Forward Engineer | Alter Script iş akışını adım adım gözle, screenshot al.
- Alter Script dialog'unda üretilen SQL'in kaynağı nedir? Model'in "dirty state" takibi nasıl yapılıyor?
- `AchModel` v1 XML dosyasını aç, içinde `LastKnownState`, `Baseline`, `CompareRefModel` benzeri bir bölüm var mı? Dirty state/diff tracking için hangi XML tag'leri kullanılıyor?
- API tarafında `FEModel_DDL` dışında alter-mode'a geçişi sağlayan bir PropertyBag flag'i var mı? (örn. `GenerationMode=Alter`, `AlterScriptMode=True`)

### 5.5 Metamodel Mutation API
- `ISCSession::BeginNamedTransaction` → `ISCModelObjectCollection::Add` → `Commit` akışı nasıl çalışıyor?
- Şu işlemler için gereken ObjectType ID'leri (SC_CLSID) ve property ID'lerini bul:
  - Yeni Entity (tablo) ekleme
  - Entity silme
  - Attribute (kolon) ekleme, silme, tip değiştirme, null/not null değiştirme
  - Primary Key değişikliği
  - Relationship (FK) ekleme, silme
  - Index ekleme, silme, unique flag değiştirme
- erwin Spy'ı `AchModel` v1 üzerinde çalıştırıp yukarıdaki her işlemin property değişikliklerini gözlemle.

### 5.6 Türkçe Karakter
- `AchModel` v1 .erwin ve XML export'unda Türkçe karakter var mı? Varsa nasıl encode edilmiş?
- CompleteCompare XLS çıktısı ve FEModel_DDL SQL çıktısı encoding'i? (.sql BOM'lu UTF-8 mi, ANSI mi, UTF-16 LE mi?)
- SCAPI COM arayüzü BSTR (UTF-16) kullanıyor — C# string → BSTR marshalling otomatik çalışır, ama dosya I/O tarafında encoding manipülasyonu gerekebilir. Test et ve raporla.

### 5.7 Lisans ve Headless Çalışma
- erwin DM 15.2 lisansı GUI açmadan API üzerinden nasıl aktive ediliyor? Lisans kontrol mekanizması?
- Windows Service account altında çalıştırıldığında COM activation başarılı mı? (CI/CD senaryosu için)
- İlk COM activation'da splash screen veya diyalog popup'ı çıkıyor mu? Çıkıyorsa nasıl bastırılır?

**Faz 0 çıktısı:** `docs/research_findings.md` — yukarıdaki 7 sorunun cevabı + kaynak referansları + strateji güncellemesi önerin. Raporu verdikten sonra onayımı bekle, Faz 1'e geçme.

## 6. FAZ 1 — Spike / PoC (Tek Proje, ~150 Satır)

Faz 0 onayımdan sonra:

- Tek bir .NET 10 console project: `ErwinAlterDdl.Spike`
- Input path'leri: `c:\work\FromPowerDesignerRepoX\` altındaki gerçek AchModel v1 ve v2 dosyaları (isimleri Faz 0'da teyit ettin)
- İşlemler:
  1. COM activation (`ERwin9.SCAPI.9.0`) — dynamic binding ile (type library import gerekmez)
  2. CompleteCompare çağır → `c:\work\FromPowerDesignerRepoX\out\ach_diff.xls` üret
  3. Çıkan XLS'i `ClosedXML` ile aç, toplam diff satır sayısını stdout'a yaz
  4. Her iki model için FEModel_DDL çağır → `ach_v1_full.sql` + `ach_v2_full.sql`
  5. Clean exit (COM release, `Marshal.FinalReleaseComObject`)
- Verbose logging (Serilog console sink), her COM call öncesi ve sonrası log satırı
- **Hata yakalama yok** — bu Faz'da unhandled exception direkt çıksın, stack trace'i görmek istiyorum
- Exit code: 0 başarılı, unhandled exception'da .NET default

**Faz 1 çıktısı:** çalışan spike project + 3 çıktı dosyası (1 XLS + 2 SQL). Ben manuel inceleyeceğim. Onayımı bekle.

## 7. FAZ 2 — Mimari Taslağı

Faz 1 başarı kanıtından sonra:

```
ErwinAlterDdl/
├── src/
│   ├── ErwinAlterDdl.Cli/                # Entry point, arg parsing
│   ├── ErwinAlterDdl.Core/
│   │   ├── Abstractions/
│   │   │   ├── IErwinComGateway.cs       # CompleteCompare, FEModel_DDL, session mgmt
│   │   │   ├── IDiffReportParser.cs      # XLS → IReadOnlyList<Change>
│   │   │   ├── IModelMutator.cs          # Model A'ya Change[] uygula
│   │   │   └── IAlterDdlGenerator.cs     # Post-mutation alter DDL üretimi
│   │   ├── Models/                       # Change, ChangeType, ObjectRef, PropertyDiff
│   │   ├── Services/                     # Implementasyonlar
│   │   └── Exceptions/                   # Custom exception hiyerarşisi
│   └── ErwinAlterDdl.ComInterop/         # COM gateway + marshalling helpers
└── tests/
    ├── ErwinAlterDdl.Core.Tests/         # xUnit unit tests
    └── ErwinAlterDdl.Integration.Tests/  # AchModel v1/v2 ile e2e
```

**Gereksinimler:**
- `net10.0` hedef framework
- DI: `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection`
- Logging: `Serilog.AspNetCore` + `Serilog.Sinks.Console` + `Serilog.Sinks.File` (JSON format)
- CLI: `System.CommandLine` (2.0+ preview)
- Config: `appsettings.json` (erwin install path, log path, default option set path)
- Test: `xUnit` + `FluentAssertions` + `NSubstitute`
- Static analysis: `Microsoft.CodeAnalysis.NetAnalyzers` + `StyleCop.Analyzers` + `TreatWarningsAsErrors=true`
- Formatting: `dotnet format` pre-commit hook

**Custom exception hiyerarşisi** (hata yutma yasağı gereği):
```
ErwinAlterDdlException : Exception           // root
├─ ErwinComException                          // COM HRESULT hataları
├─ ErwinLicenseException                      // lisans sorunu
├─ CompleteCompareFailedException             // CC başarısız
├─ DiffReportParsingException                 // XLS yapısı bozuk
├─ ModelMutationException                     // metamodel write hatası
└─ AlterDdlGenerationException                // FEModel_DDL hatası
```
Her exception `Exception innerException` parametresini destekleyecek — chain hiç koparılmayacak.

**Faz 2 çıktısı:** solution skeleton (boş metot stub'ları, DI wire-up yapılmış), mimari diyagramı (Mermaid Chart MCP ile üret), `docs/ARCHITECTURE.md`, `README.md` taslağı. Onayımı bekle.

## 8. FAZ 3 — Core İmplementasyon

Her katmanı izole edilmiş şekilde ve unit test'lerle birlikte yaz. **Sıra önemli:**

1. **Models/** — POCO'lar, immutable records, hiç bağımlılık yok
2. **Exceptions/** — tüm custom exception class'ları
3. **IDiffReportParser + implementation** — XLS parser, mock XLS dosyası ile unit test
4. **IAlterDdlGenerator + implementation** — FEModel_DDL wrapper + (araştırma sonucuna göre) alter-mode aktivasyonu
5. **IModelMutator + implementation** — Metamodel API ile Model A'yı modifiye et
6. **IErwinComGateway + implementation** — en son, çünkü diğerleri bunu mock'layarak test edilebilir
7. **Cli** — arg parsing, DI container wire-up, top-level exception handler (stack trace + anlamlı exit code)

### CLI Spesifikasyonu
```
erwin-ddl-diff
  --left <path>                   # REQUIRED: sol/baseline .erwin
  --right <path>                  # REQUIRED: sağ/hedef .erwin
  --out <path>                    # REQUIRED: çıktı .sql
  [--compare-level LP|L|P|DB]     # default: LP
  [--cc-option-set <xml-path>]    # default: "Standard"
  [--fe-options <xml-path>]       # FE option set
  [--keep-artifacts <dir>]        # ara XLS/DDL'leri sakla (debug)
  [--verbose]                     # tüm COM call'larını logla
  [--dry-run]                     # sadece diff raporu ver, DDL üretme
```

### Exit Codes
- 0: başarılı
- 1: COM activation / erwin bulunamadı
- 2: lisans hatası
- 3: input validation hatası (file yok, invalid path, yanlış extension)
- 4: CompleteCompare başarısız
- 5: diff parsing başarısız
- 6: model mutation başarısız (transaction rollback)
- 7: alter DDL generation başarısız
- 99: unhandled exception (bu duruma düşmemeli — düşerse bug)

### COM Resource Yönetimi (KRİTİK)
- Her COM objesi için explicit `Marshal.FinalReleaseComObject`
- PersistenceUnit → Session → ModelObjectCollection hiyerarşisi LIFO sırasıyla release
- `IDisposable` wrapper class'ları yaz (`ComHandle<T> : IDisposable`)
- `using` statement zorunlu, manuel release yasak
- Her transaction `BeginNamedTransaction` ile başlamalı; exception'da `RollbackTransaction` çağrılmalı **ama orijinal exception kaybolmayacak**:

```csharp
try {
    session.BeginNamedTransaction(txName);
    // işlemler
    session.CommitTransaction(txName);
} catch (Exception ex) {
    try { session.RollbackTransaction(txName); }
    catch (Exception rollbackEx) {
        throw new AggregateException(
            "Transaction failed AND rollback failed",
            ex, rollbackEx);
    }
    throw; // orijinal exception propagate
}
```

**Faz 3 çıktısı:** tüm core katmanlar + %80+ unit test coverage + tüm analyzer uyarıları temiz. Onayımı bekle.

## 9. FAZ 4 — End-to-End Integration Test

- Mevcut örnek dosyaları kullan: `c:\work\FromPowerDesignerRepoX\` altındaki AchModel v1 ve v2.
- **Sentetik model üretme.** Var olan AchModel dosyalarını kullan; yetersiz bulursan (örn. belirli bir edge case kapsanmıyor) bana söyle, başka örnek çıkarırım — sen yaratma.
- `tests/ErwinAlterDdl.Integration.Tests` projesinde:
  - Bu iki dosya üzerinde CLI'ı subprocess olarak çalıştır
  - Üretilen alter SQL'ini yapısal olarak doğrula (syntactic parse, semantic assertion'lar)
  - Golden master yaklaşımı: ilk başarılı çalıştırmanın çıktısını `tests/golden/AchModel_v1_to_v2.sql` olarak kaydet, sonraki çalıştırmalar bu dosyaya göre diff almalı
- İlk integration test:
  - CLI'ı çağır, exit code == 0 doğrula
  - Alter SQL dosyasının var olduğunu ve boş olmadığını doğrula
  - SQL içinde en az bir ALTER statement olduğunu doğrula (regex veya AST)
  - Golden master karşılaştırması

Golden master bir diff ile farklılaşırsa, test fail etsin ve diff'i stdout'a bassın — developer gözle inceleyip golden'ı update edebilir.

**Faz 4 çıktısı:** integration test projesi + `docs/testing.md` (nasıl çalıştırılır, golden master nasıl güncellenir). Onayımı bekle.

## 10. Kod Kalitesi Gereksinimleri

- **Statik analiz:** `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `<AnalysisLevel>latest</AnalysisLevel>` + `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- **Nullable:** `<Nullable>enable</Nullable>` her projede
- **Format:** `.editorconfig` + `dotnet format` pre-commit hook
- **Commit'ler:** Conventional Commits (feat:, fix:, docs:, refactor:, test:, chore:)
- **Dökümantasyon:** her public metot XML doc comment; `README.md` başlangıç kılavuzu; `docs/ARCHITECTURE.md` Mermaid diyagramıyla
- **Yorumlar:** "ne" değil "neden"; erwin COM idiosyncrasies için bol yorum (garip davranışlar, workaround'lar, dokümante edilmemiş davranışlar)
- **Hiç `#pragma warning disable` yok** — warning varsa düzelt, bastırma
- **Hiç `TODO` / `FIXME` kod içinde yok** — varsa GitHub issue aç, link commit mesajına koy

## 11. Senin Çalışma Tarzın

- **Önce sor:** belirsiz noktada bana sor, tahminle ilerleme
- **Aşama sırasını koru:** Faz X bitmeden Faz X+1'e geçme, onayımı bekle
- **Küçük commit'ler:** her mantıksal adım ayrı commit
- **Assumption'ları loga yaz:** "X'in Y şekilde çalıştığını varsayıyorum çünkü dokümanda..." — erken hata yakalamam için
- **Bilmediğin zaman de ki bilmiyorum:** araştırma gerekiyorsa yap, ama sonucu halüsinasyona uğratma; kaynak URL'i paylaş
- **Dil:** dokümanlar + commit mesajları + XML doc comment'ler İngilizce (açık kaynak standardı), benimle sohbet Türkçe, kod içi yorumlar ihtiyaca göre (kritik "neden"ler Türkçe OK)
- **Hata yutma yasağına uy:** bir exception'ı görmezden geldiğini yakalarsam Faz baştan başlar

## 12. Başlangıç

Faz 0'dan başla. İlk iş olarak `c:\work\FromPowerDesignerRepoX\` dizinini listeleyip gerçek AchModel dosya isimlerini teyit et, sonra araştırmaya geç. `docs/research_findings.md` raporunu bana verdiğinde değerlendireceğim.

Hadi başla.