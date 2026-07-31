# {Current} token - Template naming kurali (add-in runtime isteri)

Tarih: 2026-07-30
Ilgili: [prefix-suffix-template.md](prefix-suffix-template.md) (ayni token motoru)
Admin tarafi: BITTI (tanim + validasyon + UI). Bu dosya SADECE add-in'in yapacagi runtime isini tarif eder.

## Neden

Canli kural 1175 (MetaRepoTmp, config 2015): OBJECT_TYPE=COLUMN, hedef property=Physical_Name,
VALUE_TEMPLATE=`{Table.Physical_Name}_{Physical_Name}`. Add-in bunu dogru sekilde reddetti:

```
[TEMPLATE-SKIP] ... references its own target 'Physical_Name' (self-referential - would loop); skipping
```

Reddin gerekcesi dogru: yazma islemi applier'i yeniden tetikler, deger sinirsiz buyur
(`abc` -> `Table2_abc` -> `Table2_Table2_abc`). 2026-06-29'da ayni sinifta canli bir olay yasandi:
PRIMARY KEY uzerinde `PK_PK_PK_..._%KeyName`, yaklasik her 400 ms'de bir transaction.

**Add-in'in statik guard'i KALIYOR.** Sorun redde degil, ifade edilemezlige aitti: kural yazarinin
niyeti ("kolonun fiziksel adi = TabloAdi_KolonAdi") mesru, ama Prefix sadece sabit metin tasiyordu.

`{Current}` bu boslugu kapatir.

## Tanim

`{Current}` = kuralin HEDEFININ (PROPERTY_DEF_ID ile secilen property veya TARGET_UDP_ID ile secilen
UDP) MEVCUT DEGERI.

`{PropertyCode}`'dan bilincli olarak farkli adlandirildi, cunku ikisini karistirmak bu bug'i uretti:

- `{Physical_Name}` = "bu nesnenin Physical_Name property'si"
- `{Current}` = "yazilmakta olan deger"

Kural 1175'in dogru yazimi: `{Table.Physical_Name}_{Current}`

## Runtime semantigi (add-in bunu implemente edecek)

Sablonu `L {Current} R` olarak dusun (L = {Current}'tan onceki kisim, R = sonraki kisim).

1. L ve R normal sekilde render edilir (tokenlari cozulur, fonksiyon zincirleri uygulanir).
2. Mevcut deger L ile BASLIYOR **ve** R ile BITIYORSA -> seed = ortadaki kisim.
   Aksi halde -> seed = mevcut degerin tamami.
3. Sonuc = L + seed + R.

Bu bir **sabit nokta**tir: ilk yazmadan sonra deger zaten L ile baslar ve R ile biter, dolayisiyla
ikinci degerlendirme mevcut degere esit cikar ve yazma durur. **Tam olarak bir kez yazar.**

Ornek (`{Table.Physical_Name}_{Current}`, tablo = ORDER, kolon = ID):
```
1. degerlendirme: L="ORDER_", R="", mevcut="ID"        -> "ID" L ile baslamiyor -> seed="ID"        -> "ORDER_ID"  (YAZ)
2. degerlendirme: L="ORDER_", R="", mevcut="ORDER_ID"  -> L ile basliyor        -> seed="ID"        -> "ORDER_ID"  (degisiklik yok, DUR)
```

2026-06-29 olayindaki `PK_{Physical_Name}` bu semantik altinda dogru calisirdi: `PK_{Current}`
olarak yazildiginda `PK_ORDER` uretir ve orada durur.

### Sinir durumlari

- **Bos mevcut deger**: `{Current}` bos cozulur. Projenin NO-FALLBACK sozlesmesi geregi bu bir
  **hard error**dir, sessiz atlama DEGIL. (Admin bu yuzden `{Current}` + `TEMPLATE_FILL_MODE`
  = `OnlyIfEmpty` kombinasyonunu kayitta reddeder; ama add-in yine de kendi kapisinda hata vermeli.)
- **Fonksiyon zinciri**: `{Current|upper}` gecerlidir; zincir seed'e degil, `{Current}`'in cozdugu
  degere uygulanir (diger tum kaynaklarla ayni kural).
- **Sablonda birden fazla `{Current}`**: admin kayitta reddeder. Add-in yine de savunmaci davranmali.

## Add-in'in ARTIK GORMEYECEGI durumlar (admin kayitta engelliyor)

Bunlar hala runtime'da gelirse (eski satirlar, dogrudan DB yazimi), mevcut guard'lar korunmali:

| Durum | Admin mesaji |
|---|---|
| Sablon kendi hedefini okuyor | "The template reads its own target (X), which loops forever ... write {Current} instead" |
| Birden fazla `{Current}` | "A template can contain at most one {Current}; this one has N." |
| `{Current}` + `OnlyIfEmpty` | "{Current} transforms an EXISTING value ... Set Fill mode = Always." |
| Prefix/Suffix icinde `{Current}` | "{Current} is only available on a Template rule ..." |
| Parantez DISINDA fonksiyon zinciri (`{Prop}\|left:3`) | "function 'left' is written outside the token braces ..." |

## Canlidaki bozuk satirlar (OLDUKLARI GIBI BIRAKILDI)

Admin'in yeni validasyonu bunlari ARTIK KAYDETTIRMEZ, ama DB'de duran satirlara DOKUNULMADI:
kullanici bunlarin oldugu gibi kalmasina karar verdi. Yani **add-in bu sablonlarla runtime'da
karsilasmaya devam edecek** - asagidaki guard'lar bu yuzden zorunlu, gecici degil.

Onerilen sutun sadece dogru yazimin ne oldugunu gosterir; bir temizlik gorevi DEGILDIR.

| DB | ID | VALUE_TEMPLATE | Sorun | Onerilen |
|---|---|---|---|---|
| MetaRepoTmp | 1175 | `{Table.Physical_Name}_{Physical_Name}` | self-referential | `{Table.Physical_Name}_{Current}` |
| MetaRepoTarik | 1196 | `{Physical_Name}\|replace:TPL:TMP` | zincir disarida + self-referential | `{Current\|replace:TPL:TMP}` |
| MetaRepoDamla | 1173 | `{Definition}\|lower` | zincir disarida + self-referential | `{Current\|lower}` |
| MetaRepoTarik | 1194 | `{Udp:TABLE_CLASS}\|upper` | zincir disarida | `{Udp:TABLE_CLASS\|upper}` |
| MetaRepoTarik | 1195 | `{Physical_Name}\|left:3` | zincir disarida (hedef = UDP TPL_TEST_OUTPUT, self-ref DEGIL) | `{Physical_Name\|left:3}` |

Zincir disarida yazilan satirlar sessizce bozuk: `}` sonrasi duz metin oldugu icin motor
`abc|left:3` render eder ve add-in bunu hatasiz yazar.

## Ek is: `split` fonksiyonu (2026-07-30, ayni motor)

Admin'in fonksiyon listesine `split` eklendi (Rule Management > Insert function). Grameri:

```
split:<ayirac>:<index>
```

- `<ayirac>`: serbest metin, VERBATIM kullanilir (trim EDILMEZ) - tipki `replace`'in argumanlari gibi.
  Tek bosluk mesru bir ayiractir: `{Name|split: :0}` = ilk kelime.
  BOS olamaz; admin bunu kayitta reddeder (`replace`'in bos arama metni de artik reddediliyor).
- `<index>`: **0 tabanli**, negatif olamaz. 0 tabanli secildi cunku `substr:start:len` zaten 0 tabanli;
  ikinci bir konvansiyon uretmemek icin ona uyduruldu.
- Diger tum argumanlar gibi `:` ve `|` iceremez (bunlar ayirac karakterleri).

Beklenen runtime davranisi (`ApplyFunction` icinde yeni bir `case "split"`):

```
"ORDER_ITEM_ID" | split:_:0  -> "ORDER"
"ORDER_ITEM_ID" | split:_:2  -> "ID"
"ORDER_ITEM_ID" | split:_:9  -> ""      (aralik disi -> bos; substr'in "start >= length" davranisiyla ayni)
"ORDER ITEM"    | split: :1  -> "ITEM"
```

Aralik disi index icin BOS donmek, `substr`'in mevcut davranisiyla tutarlidir (bir fallback degil,
islemin dogal sonucu). Bos ayirac ise `replace`'in bos arama metni gibi HARD ERROR olmali.

**SIRALAMA UYARISI:** `ApplyFunction`'in `default` dali bilinmeyen fonksiyonda `TemplateResolutionException`
firlatir. Admin artik `split` iceren kurali KAYDEDIYOR; add-in bu case'i eklemeden once boyle bir kural
yazilirsa runtime'da hata verir. Yani bu madde admin'in yayina alinmasiyla es zamanli gitmeli.

### Durum (add-in): YAPILDI - 2026-07-30

`NamingTemplateEngine.ApplyFunction` icine `case "split"` eklendi. Yukaridaki 4 ornegin dordu de
testle sabitlendi. Uygulama notlari:

- Ayirac BOSSA hata: "split needs a non-empty separator" (`replace`'in bos arama metniyle ayni sinif).
  Ayirac kontrolu index parse'indan ONCE yapiliyor, boylece `split::x` gibi iki hatali argumanda
  daima ayni (ve daha temel olan) mesaj cikar.
- Ayirac VERBATIM: `funcSegment.Split(':')` sonrasi yalnizca `parts[0]` (fonksiyon ADI) trim edilir,
  argumanlar edilmez. `{ Name | split: :1 }` boylece hala tek bosluk ayiracini gorur.
- Bolme ORDINAL (`string.Split(string, StringSplitOptions.None)`): `split:x:1` "X"i ayirac saymaz.
  Fonksiyon adi case-insensitive, ayirac degil.
- Aralik disi index -> `string.Empty` -> mevcut "chain produced an empty value" kontrolu render'i
  komple iptal eder. Ayni sey BOS PARCA uretilen durumlarda da olur ve bu dogru: `_LEADING|split:_:0`
  ile parca 0 bostur, yarim bir isim modele yazilamaz.
- `{Current|split:...}` bedava calisiyor: mevcut cift-render yakinsaklik kontrolu, `split:_:0` gibi
  sabit-nokta zincirlerine izin verip `split:_:1` gibi salinan zincirleri ("A_B" -> "B" -> "") reddediyor.
- Diger 7 fonksiyonun davranisi, sema ve MetaShared sozlesmesi DEGISMEDI.

## Sema / sozlesme degisikligi

**YOK.** `VALUE_TEMPLATE` zaten `nvarchar(2000)`; `{Current}` sadece metin. MetaShared sozlesmesinde
yeni alan yok - add-in bu token icin admin'den ek bir alan okumaz.
