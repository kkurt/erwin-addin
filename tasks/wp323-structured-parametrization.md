# WP #323: STRUCTURED Parametrization - erwin-addin Gorevi

> Admin tarafi (erwin-admin) 2026-07-23'te BITTI: web ekrani, API, EF modeli ve TUM canli MetaRepo* DB'leri
> (9 adet) migrate edildi. Bu dosya add-in tarafinin kendi basina yeterli isteridir.
> Kaynak plan: erwin-admin/tasks/datatype-structured-parametrization.md

## Ozet

DATATYPE_LIBRARY'de yeni PARAMETRIZATION_TYPE degeri var: `STRUCTURED`. Regex yerine yapilandirilmis
tanim: zorunlu Length/Precision + tanim bazli Scale kurali + tanim bazli suffix (length semantics) listesi.
Kompozisyon: `BASE(p[,s][ suffix])` - ornekler: `NUMBER(22,5)`, `VARCHAR2(15 CHAR)`, `NUMBER(22,5 XYZ)`.

## DB sozlesmesi (kolonlar canli DB'lerde MEVCUT, hepsi NULL'ablanir)

| Kolon | Tip (MSSQL) | Anlam |
|---|---|---|
| PARAM_MIN | int NULL | p alt siniri (NULL = serbest; admin >= 1 garantiler) |
| PARAM_MAX | int NULL | p ust siniri |
| SCALE_MODE | nvarchar(10) NULL | NONE / OPTIONAL / REQUIRED; NULL'u NONE oku (savunmaci) |
| SCALE_MIN | int NULL | s alt siniri (negatif olabilir, orn. Oracle -84) |
| SCALE_MAX | int NULL | s ust siniri |
| SUFFIX_MODE | nvarchar(10) NULL | NONE / OPTIONAL / REQUIRED; NULL'u NONE oku |
| SUFFIX_VALUES | nvarchar(400) NULL | Izinli suffix listesi CSV ("BYTE,CHAR") |

- Kolonlar sadece STRUCTURED satirlarda dolu; diger tiplerde server null'lar.
- SUFFIX_VALUES token'lari: ic bosluk icerebilir, parantez icermez, salt-rakam olamaz, server
  case-insensitive dedupe eder. Eslesme OrdinalIgnoreCase yapilmali (base-name eslesmesiyle ayni,
  AllowedDatatypeService.cs:390 orengi); kompozisyonda admin'in yazdigi casing kullanilir.

## Yapilacaklar

### 1. Services/AllowedDatatypeService.cs
- `AllowedDatatypeEntry` (satir ~39-67): 7 yeni property.
- `GetQuery` (~264-287): 3 lehce SELECT listesine 7 kolon.
- `Load()` reader (~188-231): DBNull-guvenli okuma; SCALE_MODE/SUFFIX_MODE DBNull -> "NONE".
- `ParseParametrization` (~292-304): `STRUCTURED` degeri; bilinmeyen deger davranisi (Standard'a
  dusur + log) AYNEN kalsin.
- `DescribeEntry` (~306-315) ve ModelConfigForm.cs:4521 yukle logu: structured bilgiyi ekle.

### 2. YENI parantez-ici gramer parser'i
Mevcut `DataTypeParser` paren icerigini VERBATIM dondurur ve `Parts.Suffix` PARANTEZ DISI kuyruktur
("TIMESTAMP(6) WITH TIME ZONE" -> " WITH TIME ZONE"); onunla KARISTIRMA. Paren icerigi icin yeni,
saf, test edilebilir bir parser yaz (orn. `StructuredParamParser.TryParse(string content, out int p,
out int? s, out string suffix)`):
- Gramer: `p [, s] [ bosluk suffix ]`; p pozitif tamsayi, s isaretli tamsayi olabilir.
- Ham erwin degerleri normalize edilmemis gelir: "10 , 2" -> p=10 s=2 kabul et (virgul cevresi bosluk).
- Suffix: sayilardan sonraki ilk bosluktan itibaren kalan her sey (trim edilmis); ic bosluklu ve
  rakam-parcali token'lar gecerli olabilir ("22,5 XYZ 2" -> suffix "XYZ 2").
- Onerilen desen: `^\s*(\d+)\s*(?:,\s*([+-]?\d+))?(?:\s+(\S.*?))?\s*$` (test ile dogrula).

### 3. ValidateAgainstEntry STRUCTURED dali (~419-469)
- hasParam=false: `AllowNonParametrized` ise Valid, degilse "Type '{type}' requires a parameter."
  (STANDARD/REGEX ile ayni kalip).
- Parse edilemeyen icerik: Invalid, uretilmis mesaj (REGEX_ERROR STRUCTURED'da KULLANILMAZ).
- p siniri: PARAM_MIN/MAX (NULL = sinirsiz) -> "Length/precision must be between X and Y." (tek tarafli
  sinirlarda uygun metin).
- s: varsa ve SCALE_MODE=NONE -> "Type '{type}' does not take a scale."; SCALE_MODE=REQUIRED ve yoksa ->
  "Type '{type}' requires a scale."; varsa SCALE_MIN/MAX kontrolu.
- suffix: varsa ve SUFFIX_MODE=NONE -> gecersiz; REQUIRED ve yoksa -> gecersiz; varsa listede
  OrdinalIgnoreCase aranir, yoksa "Length semantics must be one of: BYTE, CHAR." benzeri mesaj.

### 4. Forms/AllowedDatatypePickerForm.cs
- STRUCTURED secilince tek parametre kutusu yerine: Length sayisal kutu + Scale sayisal kutu
  (SCALE_MODE'a gore gorunur/zorunlu) + Suffix combo (SUFFIX_VALUES'tan; OPTIONAL'da bos secim var,
  REQUIRED'da tek deger varsa onceden secili).
- Param stringini `p[,s][ suffix]` olarak birlestir, mevcut `Compose` (108-122) DEGISMEDEN kullan
  (virgul normalizasyonu ve suffix oncesi boslugu zaten koruyor).
- `SyncParamEnabled` (~386-419) etiket metinlerini mode'lara gore guncelle; `TakesParameter` (~574),
  `FormatComboLabel` (~585) gozden gecir.

### 5. GetFallbackDatatype (~336-349)
Sabit `"(1)"` yerine tam sentez: p = PARAM_MIN ?? 1; SCALE_MODE=REQUIRED ise `,{SCALE_MIN ?? 0}`;
SUFFIX_MODE=REQUIRED ise ` {ilk suffix}`. (OPTIONAL parcalar fallback'e eklenmez.)

### 6. Testler (tests/ErwinAddIn.Tests/AllowedDatatypeMatcherTests.cs)
Yeni `Structured(...)` entry builder + matris: sinir ici/disi p, scale NONE/OPTIONAL/REQUIRED
varlik/yokluk/sinir, negatif scale, suffix listesi case-insensitive eslesme, listede olmayan suffix,
bare kullanim, fallback sentezi, parser kenar durumlari ("22,5", "15 CHAR", "22,5 XYZ", "10 , 2",
"22,-5", bos icerik, "5CHAR" bitisik gecersiz).

## Rollout notlari

- Tum MetaRepo* DB'ler 2026-07-23'te migrate edildi. YINE DE dikkat: yeni add-in migrationsiz bir
  DB'ye baglanirsa Load() catch'i FAIL-OPEN calisir (whitelist bosalir, enforcement sessizce kapanir,
  sadece log satiri). Add-in'in isaret edebildigi baska DB varsa once
  erwin-admin/installer/sql/2026-07-datatype-structured-mssql.sql uygulanmali.
- Eski add-in STRUCTURED satirlari permissive STANDARD gibi gorur (bilinen gecis penceresi); add-in
  guncellemesi bu pencereyi kapatir.
