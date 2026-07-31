# Prefix / Suffix'te Value Template motoru - erwin-addin Gorevi

> Admin tarafi (erwin-admin) 2026-07-30'da BITTI: web editoru, API validasyonu, EF modeli, 01-schema
> snapshot ve TUM canli MetaRepo* DB'leri (9 adet) migrate edildi. Bu dosya add-in tarafinin kendi
> basina yeterli isteridir.
> Kaynak plan: erwin-admin/tasks/todo-rule-management.md ("Prefix/Suffix'te Value Template motoru")

## Ozet

`MC_NAMING_STANDARD.PREFIX` ve `SUFFIX` artik duz metin OLMAK ZORUNDA DEGIL: `VALUE_TEMPLATE` ile
AYNI token gramerini tasiyabilir.

```
{ SOURCE ( "|" FUNC ( ":" ARG )* )* }
SOURCE : {PropertyCode} | {Alias.PropertyCode} | {Udp:Name}
FUNC   : trim | upper | lower | left:n | right:n | substr:start:len | replace:a:b | split:sep:index
```

> `split:sep:index` 2026-07-30'da eklendi (8. fonksiyon, bkz.
> [template-current-token.md](template-current-token.md#ek-is-split-fonksiyonu-2026-07-30-ayni-motor)).
> Ayni motor oldugu icin affix yolu da onu bedavaya alir.

Ornek: `Prefix = "{Udp:Domain|upper|left:3}_"` -> FIN domainli bir kolon icin `FIN_`.

Token icermeyen bir affix (bugunku tum kayitlar) DEGISMEDEN calisir: sabit metin, sifir davranis
farki. Token iceren affix "DINAMIK" affix'tir ve nesne basina RENDER edilir.

## DB sozlesmesi (canli DB'lerde MEVCUT)

| Kolon | Onceki | Simdi |
|---|---|---|
| PREFIX | nvarchar(100) | nvarchar(2000) |
| SUFFIX | nvarchar(100) | nvarchar(2000) |

- Migration dosyalari: `erwin-admin/installer/sql/2026-07-naming-affix-template-{mssql,oracle,pgsql}.sql`
  (idempotent). 9 canli MSSQL DB'ye uygulandi.
- Add-in tarafinda SEMA ISI YOK: `NamingStandardService.cs:461-462` kolonlari tipsiz string olarak
  okuyor, 3 lehcenin SELECT'i (818 / 836 / 855) zaten kolonu aliyor. Uzunluk artisi seffaf.
- Dogrulandi: 9 canli DB'de PREFIX/SUFFIX icinde tek bir `{` veya `}` yok, en uzun deger 14 karakter.
  Yani sahada su an DINAMIK affix YOKTUR; bu ozellik geriye donuk kirilma uretmez.

## Admin'in save aninda GARANTI ettikleri

`MetaWeb.Api/Governance/NamingRulesEndpoints.cs` -> `ValidateAffix`:

1. Affix gramer olarak GECERLI (bilinmeyen fonksiyon, kapanmamis parantez, bozuk arguman -> 400).
2. Affix'te SATIR SONU YOK (`\r` / `\n` reddedilir). Editor gorsel olarak multiline, cunku uzun
   token zinciri tek satira sigmaz; ama yazilan deger tek satirdir.
3. AUTO_APPLY acik + affix token iceriyor => APPLY_ON zorunlu olarak `Create`. Sebep asagida.
4. Token'in COZULEBILIRLIGI garanti DEGILDIR (admin sadece uyari gosterir). Cozum runtime'da,
   yani add-in'de olur; cozulemeyen token bir HATA'dir, sessiz bos deger degildir.

## Neden "auto-apply + dinamik affix" sadece Create?

`NamingValidationEngine.ApplyNamingStandards` (satir 252) affix'i **strip-then-reapply** ile
uygular: once bu kuralin affix'ini isimden soyar (`StartsWith`/`EndsWith`, Ordinal - satir 319-363),
sonra tek sefer geri ekler (368-384). Bu algoritma affix'in SABIT olmasina dayanir.

Dinamik affix'te kaynak deger degisirse eski render'i tanimak imkansizdir: `Udp:Domain` FIN -> RIS
olunca isimdeki `FIN_` artik kuralin bugunku affix'i (`RIS_`) ile eslesmez, soyulamaz, ve ustune
`RIS_` eklenir -> `RIS_FIN_Musteri`. Sinirsiz bir birikim degil ama bozuk bir isim.

Cozum: dinamik + auto-apply olan kural sadece Create'te calisir, yani nesnenin adi bir kez uretilir
ve bir daha bu kuralla dokunulmaz. `MatchesApplyOn(r, isNew)` (272) bunu ZATEN sagliyor; ek kod
gerekmiyor. Admin de bu kombinasyonu kaydettirmiyor.

## Yapilacaklar

### 1. Affix render seam'i - `Services/NamingValidationEngine.cs`

`ApplyNamingStandards` icinde, `applicable` sozlugu doldurulduktan HEMEN SONRA (satir ~285) her kural
icin affix'i BIR KEZ render et ve hem strip hem re-apply pass'lerinde bu render edilmis degeri
kullan. `rule.Prefix` / `rule.Suffix` dogrudan kullanilan tum yerler (342-344, 352-354, 372-374,
378-380) bu cozulmus degere gecmeli.

```csharp
// affix[rule] = bu nesne icin gecerli affix metni. Token'siz affix kendisine render olur.
var affix = new Dictionary<NamingStandardRule, string>();
```

Kurallar:
- **Hizli yol**: affix'te `{` yoksa motoru HIC cagirma, degeri aynen kullan. Bugunku tum kayitlar bu
  yoldan gecer, davranis birebir korunur.
- Render icin mevcut `NamingTemplateEngine.Render` (satir 91) kullanilir, YENI motor yazilmaz.
- Tek cagri icinde render edilmis deger SABITTIR, dolayisiyla strip + re-apply hala idempotenttir.
- Render sonucu bos/whitespace ise veya satir sonu iceriyorsa: bu bir HATA'dir (bkz. madde 4).

### 2. Related token (`{Alias.Prop}`) icin resolver - IMZA DEGISIKLIGI GEREKIYOR

`ApplyNamingStandards(objectType, objectName, scapiObject, ...)` sadece nesnenin KENDISINI aliyor;
`{Table.Physical_Name}` gibi bir token'i cozecek parent nesne elinde yok. `{Prop}` ve `{Udp:Name}`
ise `scapiObject` ile cozulebilir (`ReadScapiProperty`, `NamingValidationEngine.ReadUdpValueForRule`).

Onerilen: metoda opsiyonel bir `Func<string, string, string?> relatedReader = null` parametresi ekle
ve elinde parent olan cagricilardan gecir. Ornek olarak Template kuralinin kolon yolu zaten boyle
yapiyor: `ValidationCoordinatorService.cs:5599-5603`
(`(alias, code) => ResolveColumnRelatedProperty(entity, alias, code)`).

Cagrici envanteri:

| Cagrici | Related resolver var mi |
|---|---|
| ValidationCoordinatorService.cs:8504, 8558 (Column) | EVET - `ResolveColumnRelatedProperty(entity, ...)` |
| ValidationCoordinatorService.cs:6023 (PRIMARY KEY) | EVET - `ResolvePrimaryKeyRelatedProperty(entity, ...)` |
| ValidationCoordinatorService.cs:9360 (Index) | ARASTIR |
| TableTypeMonitorService.cs:2693, 2844 (Table) | ARASTIR |
| ApprovalBlockingRuleGate.cs:1279 (canonical form) | ARASTIR |

**Resolver'i olmayan baglamda `{Alias.Prop}` iceren bir affix HARD ERROR olmali** (madde 4), sessizce
atlanmamali. FALLBACK YOK.

### 3. Validasyon yolu - `ValidateRule` Prefix/Suffix dallari (satir 944-971)

Bugun `objectName.StartsWith(rule.Prefix, OrdinalIgnoreCase)` yapiyor. Dinamik affix'te once ayni
render yapilip SONUC ile karsilastirilmali. Validasyon salt-okunur oldugu icin APPLY_ON'dan bagimsiz
her zaman calisir; dinamik affix de dogrulanabilir.

Karsilastirma case-INsensitive kalsin (validasyon), apply tarafi Ordinal kalsin (satir 303-311'deki
`UpdateDate` -> `UpDate` regresyonunun sebebi budur, dokunma).

Ayrica satir 116-132'deki "kanonik formda ise pozisyonel Prefix/Suffix ihlallerini dus" mantigi
`ApplyNamingStandards` ciktisina dayandigi icin render'dan sonra kendiliginden dogru calisir; ayri
degisiklik gerekmez, ama testle dogrula.

### 4. NO-FALLBACK sozlesmesi

`NamingTemplateEngine.Render` cozulemeyen token'da `TemplateResolutionException` firlatir (motorun
sozlesmesi bu, Template kural tipiyle ayni). Affix yolunda da AYNI davranis:

- Apply yolunda: ismi YAZMA, `rule.ErrorMessage` (yoksa uretilmis mesaj) ile logla, kullaniciya
  gorunur ihlal olarak dus. Yari render edilmis bir isim modele ASLA ulasmamali.
- Validate yolunda: `NamingValidationResult.Invalid("Prefix"/"Suffix", ...)` uret.
- Bos/whitespace render, satir sonu iceren render ve resolver'siz `{Alias.Prop}` de ayni sekilde
  hata uretir.

### 5. Savunmaci kontrol: dinamik + auto-apply + APPLY_ON != Create

Admin bu kombinasyonu artik kaydettirmiyor, sahada da mevcut kayit yok. Ama DB'ye elle yazilabilir.
Boyle bir satir gelirse add-in **ileri (forward) apply'i yapmamali** ve bir kez loglamalidir. Bu bir
fallback degil, bilinen bozuk konfigurasyonun REDDIDIR: alternatif, ismi bozmaktir.

### 6. Testler (`tests/ErwinAddIn.Tests`)

Mevcut `NamingStandardEngineTests` / `AffixStaleStripTests` desenini kullan:

- Token'siz affix: bugunku tum testler AYNEN gecmeli (regresyon kalkani).
- `{Udp:X|upper}_` prefix -> UDP degerinden render, isme bir kez eklenir.
- Ayni cagri iki kez: idempotent (`stripped` + Ordinal davranisi bozulmamis).
- Render hatasi (UDP bos): isim DEGISMEZ + ihlal uretilir.
- Resolver'siz `{Alias.Prop}`: hata, sessiz atlama yok.
- APPLY_ON=Create + isNew=false: kural HIC katilmaz (mevcut `MatchesApplyOn` davranisi).
- Validate: `FIN_Musteri` adi `{Udp:Domain|upper|left:3}_` kuralini gecer, `RIS_Musteri` gecmez.

## Kapsam disi

- `Length` ve `Regexp` kural tipleri: token gramerine DAHIL DEGIL, dokunma.
- Admin tarafinda enforcement yok; ihlali kullaniciya gosteren taraf her zaman add-in'dir.
