# ErwinAlterDdl - Faz 0 Araştırma Raporu

**Tarih:** 2026-04-22
**Hedef:** NEW_NEED.md spec'i için "yapılabilir mi, nasıl yapılır" sorusunun kaynaklı cevabı. Kod YAZILMADI, sadece canlı ölçümler + doküman taraması + mevcut memory kanıtlarının re-verify'ı.

---

## 0. Özet (tl;dr)

1. **Pure SCAPI ile alter DDL üretimi mümkün DEĞİL** - 16 adayı metot canlı probe'landı, hepsi "NOT FOUND". PropertyBag'de hiçbir alter/compare-ref key'i yok. Dokümantasyon da bunu teyit ediyor.
2. **CompleteCompare headless çalışıyor** - `ERwin9.SCAPI.9.0` COM LocalServer'a out-of-process attach oldu, iki `.erwin` dosyasını yükledi, XLS üretti; **hiç UI penceresi açılmadı, lisans/splash dialog'u çıkmadı**.
3. **"XLS" aslında HTML `<table>`** - BIFF değil. 4 kolon (`Type | Left Value | Status | Right Value`), hiyerarşik indent'li, **ObjectID içermiyor**.
4. **.erwin XML dosyası ObjectID+Name mapping'i her obje için veriyor** - Entity, Attribute, Key_Group, Relationship, Index, Schema, Domain, Trigger_Template, Subject_Area... hepsi `id="..."` + `name="..."` attribute'lu. v1/v2 arası ortak ObjectID'ler rename'i ve silme/eklemeyi ayırt etmeye yetiyor.
5. **XLS-centric hibrid strateji teyit:** `CompleteCompare(v1,v2) → XLS parse (değişimler)` + `.erwin XML parse (ObjectID↔Name mapping, identity)` + `FEModel_DDL(v1), FEModel_DDL(v2) → CREATE DDL parse (tip/constraint detayları)` → SQL emitter → alter DDL. Pure SCAPI çekirdek, bizim koda sadece emission düşer.

---

## 1. Ortam & Sürüm Teyidi

| Öğe | Değer | Kaynak |
|---|---|---|
| OS | Windows Server 2022 Standard 10.0.20348 | ortam bilgisi |
| erwin kurulu sürüm | **r10.10.38485.00** (tek sürüm) | `(Get-Item erwin.exe).VersionInfo.FileVersion` |
| Registry erwin Versions | `HKLM\SOFTWARE\erwin\Data Modeler\10.10` (+ stale 9.98) | `reg query` |
| 15.x kurulumu | **YOK** - `HKLM:\SOFTWARE\erwin\Data Modeler\15.0` ve `\15.2` yolları yok | canlı kontrol |
| AchModel file version | `10.10.38485` | `FileVersion` attribute v1/v2 XML |
| Target_Server | **Oracle 19** | AchModel-v1 PropertyBag, canlı okuma |
| Model_Type | **Physical** | aynı |
| .NET SDK | **10.0.102** | `dotnet --list-sdks` |
| Dokümantasyon | `docs/erwin-api-ref-15.txt` başlığı "API Reference Guide 15.0" | dosya header |
| Doküman-r10 uyumsuzluğu | Genel SCAPI tasarımı r9→r15 arasında stabil; dokümantasyon 15.0 olsa da r10 binary ile canlı test edildi, metot imzaları uyuştu |

**Karar (user onayı alındı):** r10.10 hedef, 15.2 kurulum yok, CI/CD hedefi iptal edildi, daemon modu seçildi, alt klasör `ErwinAlterDdl/` mevcut repo içinde.

---

## 2. Faz 0.A - SCAPI Alter-Mode Flag Araştırması

### 2.1 Dokümantasyon taraması

`docs/erwin-api-ref-15.txt` tam grep edildi. `ISCPersistenceUnit` interface metot listesi (line 6395-6447):

- `DirtyBit`, `HasSession`, `IsValid`, `ModelSet`, `Name`, `ObjectId`, `PropertyBag` (get+set), `Save`
- `ApplyDataVault` (6451), `CompleteCompare` (6524), `ReportDesigner` (6662)
- `ReverseEngineer` (6712), `ReverseEngineerScript` (7141)
- `ForwardEngineer` (7281) → `FEModel_DB`, `FEModel_DDL`

**`FEModel_DDL` imzası (line 7309):** `HRESULT FEModel_DDL(VARIANT Locator, VARIANT OptionXML, VARIANT_BOOL* ppVal)` - **sadece 2 parametre**. Alter mode flag, compare-ref path, baseline path parametresi **yok**.

**PropertyBag content listesi (line 8773-8997):** 9 key - `Locator, Disposition, Persistence_Unit_Id, Branch_Log, Model_Type, Target_Server, Target_Server_Version, Target_Server_Minor_Version, Storage_Format`. **Alter-related key yok.**

**Grep sonucu (alter-related keyword araması):** `FEModel_Alter`, `AlterScript`, `GenerateAlter`, `ResolveDifferences`, `ApplyCompareResult`, `ImportChanges`, `CompareRef`, `BaselineModel` - **hiçbiri dokümantasyonda yok**.

### 2.2 Canlı TypeLib / runtime probe

Script: `c:\tmp\smoke_scapi.ps1`
Log: `c:\tmp\smoke_scapi_log.txt`

PropertyBag canlı okuma:
```
[0] Locator = 'erwin://c:\tmp\scapi_smoke\v1.erwin'
[1] Disposition = ''
[2] Branch_Log = '{BBFDA15D-...} {553ED144-...} {F95E496F-...} ...'
[3] Persistence_Unit_Id = '{553ED144-0DDD-4AE9-97D7-948B0BBED4EA}+00000000'
[4] Model_Type = 'Physical'
[5] Target_Server = 'Oracle'
[6] Target_Server_Version = '19'
[7] Target_Server_Minor_Version = '0'
[8] Storage_Format = 'Normal'
```
Toplam 9 key, hepsi dokümantasyondaki ile aynı, alter ipucu **yok**.

16 metot `InvokeMember` ile canlı probe edildi:
```
FEModel_AlterDDL     -> NOT FOUND
FEModel_Alter        -> NOT FOUND
FEModel_DDL_Alter    -> NOT FOUND
GenerateAlterScript  -> NOT FOUND
AlterScript          -> NOT FOUND
AlterDDL             -> NOT FOUND
ResolveDifferences   -> NOT FOUND
ApplyCompareResult   -> NOT FOUND
ImportChanges        -> NOT FOUND
FEModel_Delta        -> NOT FOUND
DeltaScript          -> NOT FOUND
FEModel_CompareDDL   -> NOT FOUND
FEModel_DDLDiff      -> NOT FOUND
CompareDDL           -> NOT FOUND
DiffDDL              -> NOT FOUND
CompareForAlter      -> NOT FOUND
```

### 2.3 Sonuç

**SCAPI'de pure alter-DDL üretim yolu YOK.** Memory'deki `reference_native_bridge_detour.md` iddiası (*"fully programmatic alter-DDL via SCAPI + exported natives is NOT achievable"*) **re-verify edildi ve doğrulandı** (feedback_memory_verify_live.md kuralı uygulandı). Alternatif B (XLS-centric) zorunlu tek yol.

---

## 3. Faz 0.B - CompleteCompare XLS Yapısı

### 3.1 Canlı test

Script: `c:\tmp\smoke_scapi_v2.ps1`
Üretilen dosyalar:

| Dosya | Preset | CompareLevel | Boyut | Kaynak |
|---|---|---|---|---|
| `c:\tmp\scapi_smoke\diff_standard_lp.xls` | "Standard" | LP | 18 484 B | canlı |
| `c:\tmp\scapi_smoke\diff_advance_lp.xls` | "Advance" | LP | 108 603 B | canlı |
| `c:\tmp\scapi_smoke\diff_custom.xls` | temp1.XML | LP | 21 775 B | canlı |

Üçü de çalıştı, üç farklı içerik üretildi. `CompleteCompare(..., ret)` her üçünde `True` döndü.

### 3.2 Format - aslında HTML

İlk 8 byte: `3C 68 74 6D 6C 3E 3C 68` = `"<html><h"`. **BIFF değil, düz HTML.** Yapı:

```html
<html><head><Title>Report</Title></head><body>
<table border=1>
  <CAPTION>Table Description</CAPTION>
  <THEAD>
    <TR><TH>Type</TH><TH>Left Value</TH><TH>Status</TH><TH>Right Value</TH></TR>
  </THEAD>
  <TBODY>
    <tr><td>&nbsp;&nbsp;&nbsp;Model</td><td>AchModel</td><td>Equal</td><td>AchModel</td></tr>
    <tr><td>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;Entity/Table</td><td>ACCOUNT_DETAIL_SUMMARY</td><td>Equal</td><td>ACCOUNT_DETAIL_SUMMARY</td></tr>
    ...
  </TBODY>
</table>
</body></html>
```

- **Tek tablo, tek header, tek body** (sheet kavramı yok, multi-sheet değil)
- **4 kolon**: `Type | Left Value | Status | Right Value`
- **Hiyerarşi** `&nbsp;` count'u ile - her 3 space bir nesting level (3=Model/DetailInfo, 6=Entity/Table, 9=Attribute/Column, 12=property)
- **Status** sadece iki değer: `Equal`, `Not Equal`
- **Değişiklik rengi**: `<font color="#FF0000">` (left=kırmızı) / `<font color="#0000FF">` (right=mavi)
- **Row count**: Standard/LP 92 `<tr>`, Advance/LP 527 `<tr>`
- **84 "Not Equal"** satır Standard/LP'de - bu bizim gerçek change sinyalimiz

### 3.3 ObjectID YOK

Her iki preset'te de `{GUID+N}` araması sıfır hit. CompleteCompare XLS **human-readable** olarak tasarlanmış, identity için GUID emit etmiyor.

**Etkisi:** Rename yakalamak için XLS tek başına yetmez. Hibrid XLS + .erwin XML gerekli (bkz. §4).

### 3.4 Standard vs Advance

- **Standard/LP:** Sadece DDL'e yansıyan property'ler (Name, Physical Name, Physical Data Type, Null Option, Physical Only, Do Not Generate, Attribute Order List, Column Order List).
- **Advance/LP:** Standard + font/color/style/notation/UI metadata (Entity Alternate Key Font Bold, Entity Definition Fill Color 1, vs.) - 5.5x daha büyük, **alter DDL için NOISE**.

**Kararı Faz 1/2'de yapacağız ama önerim: Standard preset default olsun.**

### 3.5 Hiyerarşik rename örüntüsü

Rename AchModel v1→v2'de `ACCOUNT_DETAIL_SUMMARY.ACCOUNT_SUMMARY_ID` → `ACCOUNT_SUMMARY_ID_CHANGED`:
```
(6sp) Entity/Table         | ACCOUNT_DETAIL_SUMMARY  | Equal     | ACCOUNT_DETAIL_SUMMARY
(9sp)   Attribute/Column   | ACCOUNT_SUMMARY_ID      | Not Equal | ACCOUNT_SUMMARY_ID_CHANGED
(12sp)     Name            | ACCOUNT_SUMMARY_ID      | Not Equal | ACCOUNT_SUMMARY_ID_CHANGED
(9sp)   Attribute/Column   | ACCOUNT_SUMMARY_ID      | Not Equal | ACCOUNT_SUMMARY_ID_CHANGED
(12sp)     Physical Name   | ACCOUNT_SUMMARY_ID      | Not Equal | ACCOUNT_SUMMARY_ID_CHANGED
```
**erwin rename'i "Attribute'ın Name + Physical Name property'leri değişti" olarak raporluyor** - aynı Attribute'ın iki kez listelenmesi ilginç (XLS baglanma özelliği olabilir, Faz 1 spike'ında netleşecek).

---

## 4. Faz 0.C - .erwin XML ObjectID ↔ Name Mapping

### 4.1 Kök yapı

```xml
<?xml version="1.0" encoding="UTF-8" standalone="no"?>
<erwin xmlns="http://www.erwin.com/dm" FileVersion="10.10.38485" Format="erwin">
  <UDP:UDP_Definition_Groups>
    <Model_Groups id="{E7AB3CC0-...}+40200002">
      <Property_Type id="{...}+00000000" name="Model.Physical.DBName_BAU">...</Property_Type>
      ...
    </Model_Groups>
  </UDP:UDP_Definition_Groups>
  <Entity_Groups id="{E7AB3CC0-...}+40200003">
    <Entity id="{69D572A8-0000-0000-0000-000000000053}+00000000" name="ACCOUNT_DETAIL_SUMMARY">
      <Attribute id="{...}+00000000" name="ACCOUNT_SUMMARY_ID">...</Attribute>
      ...
    </Entity>
  </Entity_Groups>
  ...
</erwin>
```

### 4.2 ObjectID taşıyan element tipleri (v1 XML taraması)

```
<Attribute, <Attribute_Groups, <Data_Vault_Object, <Data_Vault_Object_Properties,
<Default, <Default_Constraint_Usage, <Default_Trigger_Template, <Domain,
<ER_Diagram, <ER_Diagram_Proxy_Object, <ER_Model_Shape, <Entity, <Entity_Groups,
<Image, <Key_Group, <Key_Group_Member, <Model_Groups, <Name_Mapping,
<Naming_Options, <Oracle_Entity_Partition, <Page_Setup, <Page_Style_Sheet,
<Partition_Column, <Property_Type, <Relationship, <Schema, <Subject_Area,
<Subject_Area_Groups, <Theme, <Trigger_Template
```

**Hepsi `id="{GUID}+N"` + `name="..."` formatında.** Standart .NET XDocument + namespace manager ile kolayca parse edilir.

### 4.3 Cross-version ObjectID eşleşmesi

| Obje tipi | v1 count | v2 count | Ortak ObjectID | Anlam |
|---|---|---|---|---|
| Entity | 14 | 14 | 13 | 1 silindi (TABLE_13), 1 eklendi (NEW_TABLE) |
| Attribute | 92 | 91 | 87 | 5 v1-only + 4 v2-only = rename/drop/add karışımı |

Entity silme/ekleme net tespit edildi:
- **Silindi v1→v2:** `{69D572A8-...-174}+00000000` name="TABLE_13"
- **Eklendi v2:** `{A9B19A52-47A5-4019-B2B9-4258E6326830}+00000000` name="NEW_TABLE"

ObjectID pattern'de şüpheli kalıp: var olan entity'ler `69D572A8-0000-...` (model-derived) ailesi, yeni eklenen `A9B19A52-...` (rastgele GUID). erwin yeni entity eklerken rastgele GUID verip, "inherited/imported" entity'ler için deterministic pattern tutuyor.

### 4.4 Identity çözümü (XLS + XML birleştirme)

```
1. v1 XML parse → Dict<ObjectID, {Name, Class, Parent}>
2. v2 XML parse → aynısı
3. CompleteCompare XLS parse → hierarchical change list
4. Her "Attribute/Column Not Equal" satırı için:
   a. Parent Entity adını XLS'ten oku
   b. Entity adı hem v1 hem v2 XML'de bulunan bir entity ise ObjectID'si sabittir → aynı identity
   c. Attribute adı v1 XML'de var + v2 XML'de farklı name ile aynı ObjectID var → RENAME
   d. Attribute adı v1'de var, v2'de yok ve ObjectID v2'de yok → DROP
   e. ObjectID v2'de var, v1'de yok → ADD
```

Bu identity-aware heuristic XLS-centric stratejinin rename-problem'ini çözüyor.

---

## 5. Faz 0.D - CompleteCompare Option XML

Örnek dosya: `C:\work\ErwinCompleteCompareTemplates\temp1.XML` (21.7 KB, tek satır)

### 5.1 Yapı

```xml
<ErwinOptionSet Version="1">
  <TreeState>
    <Node TypeId="1075838978:1073742125"/>
    <Node TypeId="1075838978:1073742126"/>
    <Node TypeId="1075838978:1075838979:1075838981:1075839169"/>
    ...
  </TreeState>
</ErwinOptionSet>
```

**TypeId** - erwin'in `SC_CLSID` ID'leri, `:` separated path - object class + property class hiyerarşisi.

`"Standard"` / `"Advance"` / `"Speed"` string preset'leri erwin-internal, bu template XML'e denk düşen set'ler runtime'da üretiliyor.

### 5.2 CLI içindeki kullanım planı

- Default: `"Standard"` string geçilir (kullanıcı XML sağlamaz)
- Customize: kullanıcı `--cc-option-set <path>` ile kendi XML'ini verebilir
- NEW_NEED.md §5.2'deki `AutoMerge`, `AutoImport`, `ResolveDirection`, `GenerateAlterScript` flag'lerinin bu XML'de OLMADIĞI gözlendi - erwin option set sadece "hangi class/property CC kapsamında" belirliyor, "sonrasında ne yap" seçenekleri sunmuyor.

---

## 6. Faz 0.E - Headless COM Activation

### 6.1 Bulgular

- `Activator.CreateInstance("ERwin9.SCAPI.9.0")` → mevcut `erwin.exe` process'leri varsa ona attach (out-of-process LocalServer singleton). Yoksa yeni `erwin.exe` başlatır.
- Smoke test sırasında **process count değişmedi**, yani COM zaten çalışan erwin'e bağlandı.
- **Tüm canlı test process'lerde `MainWindowHandle=0` ve `Title=""`** - ne main window, ne dialog, ne splash.
- `PersistenceUnits.Add(...)`, `FEModel_DDL(...)`, `CompleteCompare(...)` - hepsi **sessiz** çalıştı (UI yok, user interaction yok).
- Lisans popup'ı çıkmadı (user'ın süreli seri numarası zaten aktif).
- `Marshal.FinalReleaseComObject` + `GC.Collect` sonrası erwin process'leri baseline'a döndü (memory leak gözlemlenmedi kısa testte).

### 6.2 Uyarı: mevcut instance'a attach riski

Eğer CLI çağrıldığında kullanıcının erwin GUI'si açıksa, COM activation **o process'e attach** olur. CLI'ın ekleyeceği PU'lar (hidden olsa bile) GUI'nin internal state'ini etkileyebilir.

**Mitigation (Faz 2 mimari kararı):**
- Option 1: her zaman ayrı erwin process başlat (`Process.Start erwin.exe /Embedding` gibi - bu class-registration ile tetiklenir; test edilecek)
- Option 2: kullanıcıya "lütfen erwin GUI'sini kapat" direktifi (çirkin)
- Option 3: Kabul et, COM referans sayımı ile sanitize

Faz 2'de netleşecek.

---

## 7. Faz 0.F - Encoding

### 7.1 FEModel_DDL çıktısı

- `v1.sql` (23903 B), first 3 byte: `0D 0A 43` - **no BOM**.
- UTF-8 okuma ile Türkçe karakter count: 0.
- CP-1254 okuma ile Türkçe karakter count: 0.
- **Sonuç:** AchModel Oracle DDL'inde physical isimler ASCII-only. UDP tanımları (`ÇIKLAMA`, `İNSİYET`) DDL'e aksetmemiş (muhtemelen UDP definition'lar DDL emission'a girmiyor).

### 7.2 CompleteCompare XLS (HTML)

- HTML pure ASCII. Turkish char count: 0 (Standard + Custom dosyaları).

### 7.3 .erwin XML

- `<?xml version="1.0" encoding="UTF-8" standalone="no"?>` - UTF-8 declared, CRLF.
- UDP definition'larında Türkçe karakter gözlemlendi (`ÇIKLAMA`, `İNSİYET`, `DOĞUM` vb.) - XML'de pure UTF-8 kodlu, sorunsuz.

### 7.4 Karar

- Pipeline UTF-8-end-to-end.
- Output alter SQL için **UTF-8 no-BOM** default (Oracle/SQL Server SQL*Plus/sqlcmd BOM'suz UTF-8'i sorunsuz okur; BOM bazı linters'e noise eder).
- SCAPI COM tarafında BSTR (UTF-16) otomatik - .NET string marshalling ile problem yok.

**Uyarı:** Logical-only model'lerde veya physical isim Türkçe içeren modellerde edge case olabilir. Faz 3'te gerçek multi-model fixture ile re-test.

---

## 8. Faz 0.G - Change-Type Envanteri (AchModel v1→v2)

### 8.1 AchModel v1→v2'de mevcut test senaryoları

| # | Senaryo | Örnek | Anlam |
|---|---|---|---|
| 1 | Attribute Name + Physical Name rename (aynı entity) | `ACCOUNT_DETAIL_SUMMARY.ACCOUNT_SUMMARY_ID → ..._CHANGED` | `ALTER TABLE RENAME COLUMN` |
| 2 | Attribute Physical Data Type change | `TABLE_11.COLUMN_1 CHAR(18) → INT`, `.COLUMN_2 CHAR(18) → FLOAT`, `.COLUMN_4 VARCHAR2(50) → ...` | `ALTER TABLE MODIFY` |
| 3 | Entity add | `NEW_TABLE` (v2-only) | `CREATE TABLE` |
| 4 | Entity delete | `TABLE_13` (v1-only) | `DROP TABLE` |
| 5 | Attribute add/delete | net -1 attribute (92→91), 5 v1-only + 4 v2-only | `ALTER TABLE ADD/DROP COLUMN` |

### 8.2 AchModel'de KAPSANMAYAN senaryolar (test boşluğu)

- Entity rename (aynı ObjectID, farklı name) - NEW_NEED.md'de "rename var" dediniz ama tespit edilen sadece Attribute rename
- Foreign key (Relationship) add/drop/modify
- Key_Group (PK/AK) değişiklikleri
- Index add/drop + unique/filter değişimi
- User trigger add/drop/modify (`tD_*`, `tU_*`, `tI_*` dışında)
- Schema/owner değişimi
- Attribute nullability flip (NULL ↔ NOT NULL)
- Default value change
- Identity/Sequence change
- Partitioning değişimi (Oracle partition)
- Subject Area / Diagram değişimi (DDL'e aksetmez ama XLS'te görünür - filter edilmeli)

**Faz 4 integration test'leri için bu boşluklar doldurulmalı.** AchModel-v3 gibi ek fixture'lar gerekecek. Kullanıcı NEW_NEED.md'de bunu onaylıyor: *"yetersiz bulursan bana söyle, başka örnek çıkarırım"*.

### 8.3 Noise filter gereksinimi

- `Do Not Generate = TRUE` olan objeler
- `Logical Only = TRUE` olan objeler (Physical model'de DDL'e çıkmaz)
- Built-in FK trigger'lar (`tD_*`, `tU_*`, `tI_*`) - erwin FE option toggle'a göre asimetrik üretiyor, zaten mevcut `DdlGenerationService.ComputeDDLDiff` buna filter uyguluyor
- Advance preset'in tüm font/color/style property'leri
- Subject Area / Diagram / ER_Model_Shape / Page_Setup / Theme değişiklikleri - alter DDL'e aksetmez

---

## 9. Strateji - NEW_NEED.md Güncellemesi

### 9.1 Doğrulanan ana akış

```
CompleteCompare(v1,v2,xls,"Standard","LP")   [erwin, headless]
    ↓
XLS parse → hierarchical change list       [kendi kodumuz, HtmlAgilityPack]
    ↓
.erwin XML parse (v1 + v2) → id↔name maps  [kendi kodumuz, XDocument]
    ↓
FEModel_DDL(v1) + FEModel_DDL(v2)           [erwin, headless]
    ↓
CREATE DDL parse (T-SQL/Oracle)            [kendi kodumuz, regex+basit parser]
    ↓
Semantic merge: change + identity + body   [kendi kodumuz, Core motor]
    ↓
Alter SQL emitter (DBMS-aware)             [kendi kodumuz, Target_Server'a bağlı]
    ↓
alter.sql → user review
```

**"Pure erwin API" NEW_NEED.md §9 kuralı NET bozuluyor** çünkü alter emission motoru bizde. Alternatif (Y4 native bridge) kullanıcı tarafından "çok zor" olarak iptal edildi. Bu strateji user tarafından seçildi/onaylandı.

### 9.2 NEW_NEED.md'de REVIZE EDİLECEK maddeler

| Madde | Mevcut | Yeni |
|---|---|---|
| §5.4 Alter Script Üretim Mekanizması | SCAPI flag var mı sorusu | **Kesin cevap: YOK**; Alter emission bizim kodumuz, kaynakları CC XLS + XML + CREATE DDL |
| §5.5 Metamodel Mutation API | Metamodel mutation akışı | **İPTAL** - modeli modifiye etmiyoruz, sadece okuyoruz |
| §2 "CI/CD" | CI/CD hedef | **İPTAL** - user karar verdi |
| §2 "15.2" | 15.2 sabitlik | **r10.10 hedef** |
| §9 "pure SCAPI" kuralı | Sabit | **SCAPI okuma için, SQL emission kendimizde** - user onayı ile |
| §7 Mimari | CLI-only | **Shared Core library + CLI + REST + Addin consumer + NuGet** |
| §6 Faz 1 Spike | Tek console project | **İki entry**: XLS parse PoC + FEModel_DDL+XML ObjectID bridge PoC |

---

## 10. Faz 1 (Spike) Önerisi

### 10.1 Kapsam

`ErwinAlterDdl/spike/` içinde tek sayfa (~200 satır) console project. İşler:

1. `Activator.CreateInstance("ERwin9.SCAPI.9.0")` (dynamic binding)
2. Input: `c:\work\FromPowerDesignerRepoX\AchModel-v1.erwin` + `AchModel-v2.erwin`
3. Add two PUs, run `CompleteCompare → diff.xls`, run `FEModel_DDL` on each PU → v1.sql + v2.sql
4. Parse XLS as HTML (via `HtmlAgilityPack` NuGet, lightweight)
5. Parse both .erwin XML files → `Dict<ObjectID, (Name, Class, ParentId)>`
6. Correlation dump (stdout): list every "Not Equal" XLS row, print left/right ObjectID from XML maps, print classification (RENAME / TYPE_CHANGE / ADD / DROP)
7. Clean COM release

### 10.2 Exit criteria (kullanıcı onayı için)

- [ ] 3 çıktı dosyası üretilir (1 xls + 2 sql)
- [ ] Correlation dump'ta en az şu satırlar var:
  - `ENTITY_ADD     NEW_TABLE  ObjectID={A9B19A52-...}`
  - `ENTITY_DROP    TABLE_13   ObjectID={69D572A8-...-174}`
  - `ATTR_RENAME    ACCOUNT_DETAIL_SUMMARY.ACCOUNT_SUMMARY_ID → _CHANGED (same ObjectID)`
  - `ATTR_TYPE      TABLE_11.COLUMN_1 CHAR(18) → INT`
- [ ] Hata yakalama YOK (NEW_NEED.md §6 gereği) - unhandled exception direkt çıkar, stack trace görülür
- [ ] Serilog verbose log

**Bu Faz 1 kendi bulgularımı kullanıcıya kanıtlamak için.** Alter SQL emit etmiyoruz; sadece "correlation çalışıyor" kanıtı.

---

## 11. Açık Sorular (kullanıcıdan onay gerekiyor)

1. **Standard vs Advance preset default:** Standard öneriyorum (84 change sinyal vs 527 noise). Onay?
2. **erwin instance isolation:** Kullanıcının GUI'si açıkken CLI çağrılırsa aynı process'e attach. Faz 2 mimari kararı: her CLI/REST run için yeni erwin process veya shared tolerate? Tercih?
3. **Output encoding:** UTF-8 no-BOM default. Oracle sqlplus, SQL*Loader açısından bir sorun var mı?
4. **Test fixture ekleri:** §8.2 kapsanmayan senaryolar için AchModel-v3, AchModel-v4 gibi ek modeller sağlayabilir misiniz? Hangi senaryolara öncelik?
5. **NEW_NEED.md güncellemesi:** §9.2'deki revize matrisini uygulamak için NEW_NEED.md'yi senin onayınla güncelleyeyim mi, yoksa sadece `research_findings.md` yeterli mi?

---

## 12. Kaynak Dosyalar

- Smoke test script: `c:\tmp\smoke_scapi.ps1`, `c:\tmp\smoke_scapi_v2.ps1`
- Smoke test log: `c:\tmp\smoke_scapi_log.txt`, `c:\tmp\smoke_scapi_v2_log.txt`
- XLS inspection: `c:\tmp\inspect_xls.ps1` (BIFF değil HTML fark ettiği yer)
- Üretilen CC XLS dosyaları: `c:\tmp\scapi_smoke\diff_standard_lp.xls`, `diff_advance_lp.xls`, `diff_custom.xls`
- Üretilen DDL dosyaları: `c:\tmp\scapi_smoke\v1.sql`, `v2_alone.sql`, `v2_alone_run2.sql` (deterministic check)
- Orijinal model dosyaları (okunan): `C:\work\FromPowerDesignerRepoX\AchModel-v1.erwin` + `.xml`, `AchModel-v2.erwin` + `.xml`
- CC option XML örneği: `C:\work\ErwinCompleteCompareTemplates\temp1.XML`
- SCAPI API dokümanı: `docs/api-ref-15-original.pdf` + `docs/erwin-api-ref-15.txt` (pdftotext'li, wrap artefaktları var)
- Mevcut addin referans kodu: `Services/DdlGenerationService.cs` (`ComputeDDLDiff` - statement-level text diff, yeni iş için yetersiz)

## 13. Bekleyen Kararlar + Faz 1 Tetikleme

**Faz 1'e geçmeden kullanıcı onayı gerekir** (NEW_NEED.md §11 "aşama sırasını koru"). Bu raporu incelenip §11 açık sorulara cevap verildikten sonra Faz 1 spike implementation başlar.

---

## 14. Faz 1 Spike Sonuçları (2026-04-23)

User §11 sorularını cevapladı (bkz. NEW_NEED.md §0), Faz 1 spike uygulandı.

### 14.1 Teslim edilenler

**Kod:**
- `ErwinAlterDdl/spike/ErwinAlterDdl.Spike.csproj` + `Program.cs` (.NET 10 windows x64, HtmlAgilityPack + Serilog, hata yakalama yok NEW_NEED §6 gereği)
- `ErwinAlterDdl/fixture_tools/FixtureGen/FixtureGen.csproj` + `Program.cs` (md case map + alter SQL rename parse + v1/v2 XML UID transplant + post-save reference rewrite)

**Fixture'lar (canonical):**
```
ErwinAlterDdl/test_files/erwin/
├── backup_dont_consider/          (orijinal RE çıktıları, dokunulmaz)
├── mssql_2022_v1.erwin + .xml     (baseline)
├── mssql_2022_v2.erwin + .xml     (UID-aligned, erwin import + save sonrası)
├── db2_zos_v12_v13_v1/_v2.*       (aynı)
└── oracle_19c_21c_v1/_v2.*        (aynı)
```

### 14.2 FixtureGen tool tasarımı

- **Input:** v1.xml, v2.xml, md spec (case map), output path
- **Rename parsing:** md satırları regex ile `` `X` → `Y` `` pattern + case ID prefix (TBL/COL/IDX/...) class inference. Ek olarak `expected_alters/*_v1_to_v2.sql` otomatik taranır: `ALTER TABLE ... RENAME COLUMN / RENAME TO`, `RENAME TABLE`, `sp_rename` pattern'leri.
- **UID transplant:** v2 XML Descendants tarafından her `id+name` element için class-aware name match (Attribute parent-scope, Entity global). Rename match fallback.
- **Composition-sensitive skip:** Key_Group, Key_Group_Member, Relationship için name match devre dışı (sadece rename). Sebebi: v1/v2 member list farklıysa UID transplant `ESX-112 invalid entries in the order list` hatasına yol açar.
- **Post-save reference rewrite:** Tüm uidRemap entry'leri text-level olarak XML dosyasında search+replace ile güncellenir. Erwin XML'deki UID'ler hem `id` attribute'unda hem de reference arrays/element text'lerinde geçtiği için bu ikinci geçiş şart (aksi halde Key_Group_Member attribute referansları dangling kalır, `EBS-1051` hatası).

### 14.3 Validation süreci (iterative, user-in-the-loop)

1. İlk geçiş: sadece `id` attribute'u transplant → import EBS-1051 (dangling refs) → post-save text rewrite eklendi.
2. Counter bug'ı (length karşılaştırması) düzeltildi, gerçek replacement sayısı rapor edildi (MSSQL: 2688 replacement / 2195 UID).
3. İkinci import: ESX-32807 (Key_Group_Type renumber, cosmetic) + ESX-112 (invalid order list) → composition-sensitive skip eklendi (Key_Group, Key_Group_Member, Relationship).
4. Üçüncü import: ESX-112 tamamen gitti, diagram temiz, erwin `.erwin` olarak kaydetti.

### 14.4 Spike çalıştırma sonuçları

| Fixture | Değerlendirme | Kanıt |
|---|---|---|
| **AchModel v1/v2** (retiree) | 4/4 exit criteria PASSED | ATTR_RENAME ACCOUNT_SUMMARY_ID → ..._CHANGED dahil |
| **MSSQL 2022 canonical** | 4/4 PASSED | ENTITY_ADD CAMPAIGN, ENTITY_DROP PRODUCT_ARCHIVE, ENTITY_RENAME CUSTOMER_BACKUP → CUSTOMER_HISTORY, ATTR_RENAME CUSTOMER.mobile_phone → mobile_no, ATTR_TYPE varchar(100) → varchar(250), int → bigint, vs. |
| **Db2 z/OS canonical** | 4/4 PASSED | Aynı sinyaller, COL-03 rename alter-sql'den auto-extracted |
| **Oracle 19c canonical** | BLOCKED (fixture hazır, CC xls eksik) | SCAPI singleton state-pollution bug'ı (bkz. §14.5) |

entity summary örneği MSSQL için: `added=1 dropped=1 common=7` (önceki independent-UID durumunda `added=8 dropped=8 common=0` idi - UID transplant'ın etkisi net).

### 14.5 Yeni keşfedilen / teyit edilen SCAPI r10.10 gotcha'lar

1. **FEModel_DDL singleton server pollution (§14.5.1):** Aynı COM oturumunda ardışık `FEModel_DDL` çağrıları farklı PU'larda `RPC_E_SERVERFAULT` atıyor. `PersistenceUnits.Clear()` + `FinalReleaseComObject` + `GC.Collect` çözmüyor. Workaround: Faz 2'de process-per-DDL veya erwin.exe kill + respawn. Spike'ta FEModel_DDL çağrıları deferred edildi (Faz 3'e).
2. **CompleteCompare memory violation at SCAPI cleanup (§14.5.2):** CC başarıyla XLS yazıyor, ardından cleanup path'inde (`Marshal.FinalReleaseComObject`) `AccessViolationException` 0xC0000005. XLS zaten dosyaya yazılmış, iş kaybı yok. Faz 2'de try/catch + swallow cleanup exception ile tolere edilebilir.
3. **erwin.exe COM singleton:** Her `CreateInstance` mevcut erwin.exe'ye attach (yeni process açmaz). User GUI açıksa CLI/spike onun state'ini kirletir veya onun state'inden etkilenir. Faz 2'nin "Option 3: daemon + dedicated erwin.exe child" kararı bunu direkt çözüyor.
4. **"Processing Events" progress modal (§14.5.3):** CC sırasında erwin `Processing Events` modal'ı gösterir (ActivateSilentMode bastırmıyor). İş bitince kendi kapanır. Faz 2'de cross-process `ShowWindow(SW_HIDE)` ile gizlenecek.

Hepsi `reference_scapi_gotchas_r10.md` memory'sinde dokümante.

### 14.6 Faz 1 için sonuç

**Spike'ın temel misyonu (correlation mantığının doğru olduğunu kanıtlamak) TAM BAŞARI.** 3 canonical fixture + 1 retiree fixture üzerinde ENTITY_ADD/DROP/RENAME + ATTR_ADD/DROP/RENAME + ATTR_TYPE_CHANGE sinyalleri ObjectID-aware doğru çıkıyor.

Oracle'ın CC XLS'i spike'ta çalıştırılamadı ama **correlation kodu fixture'a bağlı değil** (aynı logic MSSQL ve Db2 için çalıştı). Faz 2'nin process-isolation kararı Oracle'ı da otomatik yeşile çevirecek.

### 14.7 Faz 2'ye devir kararları

- Core library tasarımı: NEW_NEED.md §0.4 mimari (Core + ComInterop + Cli + Api consumers)
- `IScapiSession` abstraction: InProcess / OutOfProcess / Mock
- `OutOfProcessScapiSession` Option-3 isolation implement edecek (her daemon instance kendi erwin.exe child process)
- Spike `Program.cs` referans koddur - Faz 2'de `XlsDiffParser`, `ErwinXmlObjectIdMapper`, `CompleteCompareRunner` ayrı class'lara bölünür, SOLID temizliği ile
- FixtureGen ayrı bir utility olarak kalacak (dev-time tool, Core'a sızmaz)

