# Integrate flow (Integrate tab -> Mart Merge -> next environment)

Kullanici istegi 2026-07-25/26. Bu dosya OTOMASYONUN KESIF KAYDI: asagidaki id'lerin
tamami 2026-07-26 12:04-12:08 arasi CANLI bir manuel kosudan alindi
(`Ctrl+Alt+C` komut yakalama + 8 adet `Ctrl+Alt+D` pencere agaci dokumu,
`%TEMP%\2\erwin-native-bridge.log`). Tahmin YOK.

## Tetikleyici (2026-07-26 REVIZE)
Merge zinciri **`Integrate` tabindan** baslar, DDL uretiminden DEGIL.

- Tab yalnizca `INTEGRATE_ENABLED` konfigli, mart-hosted modellerde gorunur.
- Icerik: salt-okunur pipeline diyagrami + `1_DEV -> 2_TEST` rotasi + TEK buton.
- **Dirty kapisi YOK** (kullanici karari 2026-07-26). Onceki surum kirli modeli
  reddedip kullaniciyi Generate DDL'e yolluyordu; artik kaydedilmemis degisiklikler
  merge edilenin bir parcasi - zaten compare ekraninda kullaniciya gosteriliyor.
  erwin, calisilan model kirliyse kapanis zincirinde kendi save/description
  dialoglarini aciyor ve bunlar diger dialoglar gibi cevaplaniyor.
- Butona basinca: tekil-kosu kontrolu -> approval-blocking kurallari -> TEK versiyon
  yorumu -> `RunIntegrateMergeAsync`.

Approver zinciri artik bu tabi ETKILEMIYOR. DDL Review ekrani promote ile
ayni davraniyor:

| Durum | Metin |
|---|---|
| promotion modu (VERSION_PROMOTION_ENABLED) | `Send to Approval` |
| approver zinciri var | `Send to Approve` |
| diger | `Save and Close` |

ONCEKI TASARIM (artik gecersiz): DDL Review butonunun `Integrate` olmasi ve
merge'in DDL uretim zincirinden tetiklenmesi. `IntegrateFlow.ShouldOfferIntegrate`
ve `DdlApprovalDialog.IntegrateMode` bu yuzden kaldirildi.

## EN ONEMLI BULGU
`Mart > Merge` = **WM_COMMAND 1402 (0x57A)** erwin ana frame'ine, VE bu komut
dogrudan **`ShowERwinCCWiz`**'i cagiriyor:

    [12:04:38.396] [RECON-CMD] WM_COMMAND cmd=1402 (0x57A) notify=0
    [12:04:38.396] [CC-PIPE] ShowERwinCCWiz ENTER ms1=... ms2=0 b1=0 b2=0
    [12:06:53.018] [CC-PIPE] ShowERwinCCWiz EXIT rv=2

Yani Merge AYRI bir motor degil, add-in'in native bridge'inin ZATEN hook'ladigi
Complete Compare sihirbazinin merge modu (`CC-PIPE` hook'lari: ShowERwinCCWiz,
ShowConflictResolutionUI, VSDBInteractiveMerge, VSDBSilentUpdate). Alter-DDL /
Compare islerinden bilinen tum davranislar (wizard gate, siyahlik/reentrancy
kurallari, id=1083 yarisi) burada da GECERLI.

## Diyalog kontrol id'leri (canli dokumden)

### ek2 - "Right Model Selection" (cls `#32770`) = CC wizard
| id | cls | metin |
|---|---|---|
| 1079 | Button | File |
| 1080 | Button | Database / Script |
| **1081** | Button | **Mart** (radio - bunu sec) |
| **1082** | Button | **Load...** |
| 1083 | SysListView32 | Open Models in Memory |
| 1049 | Button | Set selected model as read only |
| **12325** | Button | **Compare** |
| **2** | Button | **Close** |
| 12323 / 12324 / 9 | Button | < Back / Next > / Help |
| 1027 / 1028 | Button | Load Session... / Save Session... |
| nav statics | Static | 1037 Overview, 1038 Left Model, 1039 Right Model, 1040 Type Selection, 1041 Left Object Sel., 1043 Right Object Sel., 1044 Advanced Options |

### Mart model secme - "Open" (cls `#32770`)
| id | cls | not |
|---|---|---|
| **2054** | SysTreeView32 | katalog agaci - sonraki env klasorune buradan gidilir |
| **30270** | SysListView32 | klasordeki modeller |
| **30251** | Edit | &Model Name |
| **2059** | Button | &Open |
| 2060 | Button | Cancel |
| 2062 | ComboBox | Lock Type (= 'Unlocked') |
| 2111 | ComboBox | Open Version (disabled) |
| 2053 / 2055 | ToolbarWindow32 | cmd 35751-35757, 35764 |

### ek4 "Close Model" / ek8 "Save Models" / ek6+ek9 "Mart Offline" (ucu de ayni sablon)
| id | cls | not |
|---|---|---|
| **1** | Button | OK |
| 2 | Button | Cancel |
| **2050** | XTPReport | satir grid'i - checkbox'lar SATIR ICINDE, ayri pencere DEGIL |
| 2070 | Button | "Close models, don't show this dialog in future" |
| 59392 | ToolbarWindow32 | 14 buton: 20040-20050, 20043-20045, 57670 |

### ek5 / ek9-oncesi "Description for 'MetaRepo' Version N"
| id | cls | not |
|---|---|---|
| **1081** | Edit | aciklama metni |
| **1** | Button | Save |
| 2 | Button | Skip |
| 1743 | Button | Don't show this again |
| 1023 | ToolbarWindow32 | cmd 30001-30010 |

Bu diyalog add-in'e ZATEN tanidik: `MartSaveAutomation.cs:71` ayni RECON satirini
(`id=1081 cls='Edit'`) kaynak gostererek yaziyor.

## ZATEN VAR OLAN (yeniden yazma!)
- `MartMartAutomation.HandleCloseModelDialogChain` - "Close Model" + "Mart Offline"
  zinciri, Save-As picker'lari dahil, 15sn deadline'li residual sweep.
  **DIKKAT: polarite TERS.** Mevcut kod satiri UNCHECK ediyor (degisiklikleri at);
  Integrate akisi ise hem save hem close tikini ISTIYOR. Varyant gerekiyor.
- Description'i native bridge ile yazma (`MCXGDMPersister_Mart::SetDescription`).
- CC wizard gate/teardown kurallari (`AlterWizardGate`, siyahlik onlemleri).

## Akis (kullanicinin tarifi + kayitla dogrulanmis sira)
1. `Integrate and Close` -> WM_COMMAND **1402**
2. "Right Model Selection" bekle -> **1081** (Mart) -> **1082** (Load...)
3. "Open": agactan `{base}/{sonrakiEnv}` klasoru -> model -> **2059** (Open)
4. Wizard'a donunce -> **12325** (Compare)
5. "Resolve Differences" -> **KULLANICIYA BIRAK** (Finish'i o basar)
6. Wizard'a donunce -> **2** (Close)
7. "Close Model" -> satirda hem save hem close tikle -> **1** (OK)
8. "Description ... Version N" gelirse -> **1081**'e metin -> **1** (Save)
9. "Mart Offline" -> Save-to=Close -> **1** (OK)   [sonraki-env modeli kapandi]
10. Calisilan model de kapatilir: "Save Models" -> save tikli -> **1** (OK)
11. 2. "Description" -> metin -> **1** (Save)
12. "Mart Offline" -> Close -> **1** (OK)

## Comment: TEK (2026-07-26 son karar)
Onceki iki-yorum tasarimi (Base Model Comment + Integrate Model Comment) KALDIRILDI.
Gerekce (kullanici): Integrate tabi zaten kirli modeli reddediyor, yani calisilan
model merge'den etkilenmiyor; anlatacak yeni bir seyi yok. Tek yorum toplaniyor ve
erwin hangi aciklama diyalogunu acarsa ona yaziliyor - pratikte sadece HEDEF model
(`2_TEST`) icin bir tane aciliyor.

Bu ayni zamanda 17:23 kosusundaki TAKILMANIN kok nedeniydi: dongu tamamlanma sarti
olarak IKI description bekliyordu, ama calisilan model temiz oldugu icin erwin
ikincisini hic acmadi. Tamamlanma sarti artik dialog sayisi degil DUNYA durumu:

    modelsBefore = merge oncesi acik model penceresi sayisi
    tamam        = en az 1 description damgalandi VE modelsNow <= modelsBefore - 1

(Hedef model merge sirasinda kendi MDI child'i oluyor, yani sayi once artiyor.
`CountOpenModelWindows()` erwin okunamazsa -1 donuyor; -1 asla "tamam" saymiyor.)

## Implementasyon (2026-07-26, LIVE TEST BEKLIYOR)
| Parca | Yer |
|---|---|
| Saf karar: hedef env + hedef mart path | `Services/IntegrateFlow.ResolveTarget` (11 test) |
| Tek yorum alani + `Integrate and Close` | `Forms/ConfirmSubmitDialog.cs` (integrateMode) |
| Tab govdesi + tek buton + dirty kapisi | `ModelConfigForm` `#region Integrate tab` |
| Win32 otomasyon (single-flight) | `MartMartAutomation.RunIntegrateMerge` + `DriveIntegrateSaveCloseChain` |
| Baglanti | `ModelConfigForm.OnIntegrateClicked` -> `RunIntegrateMergeAsync` |

Yeniden kullanilanlar: `DriveCCToResolveDifferencesVisible` kalibi (Back->Overview,
Next x2, 1081/1082, `SelectMartVersionInPicker`, 12325), `DismissMartOfflineDialogWin32`
(SAF WIN32 - bu diyalogda UIA erwin'in finalizer'ini cokertiyor), `SendMessageSetText`.

Otomasyon UI thread'inde DEGIL (`Task.Run`): tamamen Win32, SCAPI yok, ve ortasinda
kullanici Resolve Differences'ta istedigi kadar calisiyor (1 saat give-up siniri).
Add-in formu GIZLENMIYOR: bu akis sentetik mouse girdisi uretmiyor (PostMessage +
saf Win32) ve hala acik olan modal'in sahibini gizlemek kendi basina bir tehlike.

## 1. CANLI KOSU (2026-07-26 14:08) - BASARISIZ, 3 duzeltme yapildi
Log: picker'a `version -1` gonderilmisti, eslesmedi:
`version v-1 not found for model 'MetaRepo' (the picker listed 18 version(s))`.
Ayrica kosudan sonra add-in'in KENDI penceresi bozuk boyandi (sekmeler kaybolmus,
kontroller dagilmis) ve bir mesaj kutusu govdesiz cikti.

1. **Versiyon secimi.** `SelectMartVersionInPicker` versiyonu NUMARAYLA esliyordu.
   Artik ACIK bir `useLatestVersion` parametresi var (sentinel DEGIL - `version<=0`
   sentinel'i, LEFT_VERSION=0 tasiyan bir DDL-worker kuyruk satirini sessizce
   "en yeniyi ac"a cevirirdi; bugun o satir yuksek sesle hata veriyor, oyle kalmali).
   Latest = "(Current Version)" isaretli oge; yoksa en buyuk N. Canli logda
   `combo[0] = '(Current Version) Version 18 ...'` -> index 0 secilecek.
2. **Eksik gate (asil bozulma sebebi).** `AlterWizardGate` SADECE
   "Forward Engineer Alter Script..." basligini prob ediyordu. Merge/CC sihirbazinin
   basligi farkli ("Right Model Selection" / "Model Selection"), o yuzden gate hic
   kalkmadi ve tum add-in timer'lari erwin'in modal pump'i icinde kosmaya devam etti -
   belgelenmis GDI bozulma yolu. Iki duzeltme: (a) gate artik baslik SETI prob ediyor
   (Mart > Review'i de kapsar - kullanici butonuyla acilan ayni sihirbaz), (b) ayrica
   `AlterWizardGate.Enter(reason)` ref-sayacli acik scope eklendi ve integrate kosusu
   bunu aliyor.
3. **Form artik gizlenmiyor.** Bu akis mouse-sim yapmiyor (sadece PostMessage +
   saf Win32), ve acik modal review dialogunun SAHIBINI gizlemek kendi basina risk.

## 2. CANLI KOSU (2026-07-26 14:48) - onceki 2 duzeltme TUTTU, yeni takilma
Calisan: `[WIZARD-GATE] Integrate (Mart Merge) - ticks paused`,
`[PICK] latest wanted - '(Current Version)' marker at index 0`, `Open posted`.
Takilan: `[INTEGRATE] Resolve Differences did not open.` - **Compare ekrani hic acilmadi**.

Sebep (bridge logu ile kesin): Open 14:48:20.539'da gonderildi, erwin yuklenen model
setini ancak **14:48:23.606**'da kaydetti (`[EDR-REG] Register ms=...`). Benim kodum
Open'dan sonra sadece `Sleep(600)` bekleyip Compare'i basiyordu - yani model HALA
YUKLENIRKEN, ~2.5sn erken. Buton o anda disabled oldugu icin WM_COMMAND yok sayildi ve
sihirbaz Right Model sayfasinda oylece kaldi. (Manuel kosuda kullanici modeli gorunce
tikliyor, o yuzden sorun cikmamisti.)

- [x] Artik sabit sleep YOK: Compare butonunun (id 12325) ENABLED olmasi bekleniyor
      (60sn siniri) - bu erwin'in kendi "iki taraf da hazir" sinyali.
- [x] Compare `lParam = buton HWND` ile gonderiliyor; bu XTP diyaloglari `lParam=0` olan
      WM_COMMAND'i yok sayiyor (picker'in Open gonderimi de zaten bu formu kullaniyor).
- [x] Buton hic enable olmazsa yuksek sesle hata: sihirbaz acik birakiliyor, hicbir sey
      kaydedilmiyor/kapatilmiyor.

## 3. CANLI KOSU (2026-07-26 15:00) - Compare YINE acilmadi + SIYAHLIK notu

### Kok varsayim hatasi (benim)
`DriveCCToResolveDifferencesVisible`'i "kanitlanmis CC surucusu" sanip kalibini
kopyalamistim. **O metodun SIFIR cagirani var** - hizmet ettigi cross-version yol
2026-05-28'de kapatilmis (`LegacyCrossVersionEnabled = false`). Yani adim
zamanlamalari canli bir sihirbaza karsi HIC kosmamis. Iki basarisiz kosunun sebebi
tam olarak bu. Ondan kopyalanan her adim DOGRULANMAMIS sayilmali.

### Compare neden acilmadi
- 14:48 kosusu: Open'dan 600ms sonra Compare basildi, model 3sn sonra yuklendi -> yok sayildi.
- 15:00 kosusu: "Compare butonu ENABLED olsun" beklemesi **1 milisaniyede** gecti, cunku
  buton BASTAN enabled. Sihirbazin kendi Overview metni soyluyor:
  *"You can click on the Compare/Finish button at any time"*. Yani buton hicbir
  hazir-olma bilgisi TASIMIYOR. Compare yine erken gitti (`[EDR-REG]` 3.35sn sonra).

- [x] Artik butona degil, **erwin'in kendi durumuna** bakiliyor: Right Model sayfasindaki
      "Open Models in Memory" listesinin (id=1083) satir sayisi Load ONCESI aliniyor,
      sonra artmasi bekleniyor (120sn siniri) + 1.5sn bind payi. Liste bulunamazsa
      6sn sabit settle'a dusuluyor ve bu loga YAZILIYOR.

### NOT (kullanici istegi - SONRA bakilacak, simdi degil)
Right model yuklendikten SONRA **siyahliklar** olustu. Kullanici "oncekinde olmamisti,
simdi ne degistiysen oluyor" diyor. Ilk gozlem: gate bu sirada ZATEN kalkik
(`[WIZARD-GATE] Integrate (Mart Merge) - ticks paused`), yani add-in timer'lari
kosmuyor - klasik timer-reentrancy aciklamasi BURADA GECERLI DEGIL. Diger adaylar:
(a) CC sihirbazina IKINCI bir modelin yuklenmesi bu akista ilk kez gerceklesiyor
    (onceki kosular versiyon secmede patliyordu, o yuzden "onceden olmuyordu"),
(b) gate kalkinca `ErwinInputBlock` blogu birakiyor (`ErwinInputBlock.cs:94` gate'leri
    OR'luyor) -> merge sirasinda erwin frame'i enable/disable degisiyor.
Arastirilmadi; kullanici once akisin calismasini istiyor.

## 4. CANLI KOSU (2026-07-26 15:11) - akis Compare'i GECTI, sonda coktu

Calisan: `right model loaded ('Open Models in Memory' 1 -> 2 rows)` -> `[4] Compare` ->
`[5] Resolve Differences handed to the USER` -> kullanici Finish -> `[6] wizard Close`.

### CRASH - sebep BENIM UIA kullanimim
Event log:
    15:12:07  erwin.exe  faulting module **OLEACC.dll**  0xc0000005
    15:12:12  erwin.exe  faulting module coreclr.dll     0xc0000005 (FailFast)
`SetAllRowCheckboxes` 15:11:21'de "Close Model" uzerinde `AutomationElement.FromHandle`
cagirdi. Iki kere birden battik:
1. XTPReport UIA'ya DataItem/ListItem gostermiyor -> **`checklist has 0 row(s)`** -> hicbir
   kutu isaretlenmedi -> model NE kaydedildi NE kapandi (kullanicinin gordugu sey).
2. Sahipsiz kalan IAccessible RCW ~45sn sonra GC'de finalize olunca erwin'i oldurdu -
   bu dosyanin Mart Offline icin ZATEN belgeledigi crash sinifi.

Adversarial review bunu HIGH olarak isaretlemisti; ben "UI thread'ine marshal et" gibi
ZAYIF duzeltmeyi secmistim. Marshal etmek RCW'yi yok etmiyor. Dogrusu UIA'yi TAMAMEN
cikarmakti.
- [x] `SetAllRowCheckboxes` artik **saf Win32**: `CloseSaveModelsWin32`'in kalibre ettigi
      geometri (checkbox col0 = grid.left+12, ilk satir = grid.top+31) + ikinci checkbox
      sutunu icin +24px. Her tiklama noktasi loga yaziliyor, tiklamadan once
      `WindowFromPoint` ile hedefin hala bu dialog oldugu dogrulaniyor.
- [x] Dialog toolbar'i (id=59392) artik loga dokuluyor - "Close Model"in enabled+visible
      butonlari (20042/20043/20045) icinde muhtemelen bir "check all" var; bir sonraki
      kosu manuel recon'a gerek kalmadan id'yi gosterecek.

### ACIK RISK
Ikinci checkbox sutununun +24px offset'i TAHMIN. Log tiklama koordinatlarini yaziyor;
tutmazsa toolbar dokumundeki check-all komutuna gecilecek.

## Add-in'in merge hedefini BENIMSEMESI (kullanici sorusu) - acikti, kapatildi
Soru: "yeni bir klasorden model acildigi icin addin kendini refresh etmemeli, UDP Sync
cikmamali. Buna dikkat ediliyor mu?"
Cevap: KISMEN. Kosu SIRASINDA wizard gate tum tick'leri durduruyor. AMA kosu biterse
(veya bu kosudaki gibi BASARISIZ olup hedefi acik birakirsa) gate dusuyor ve ilk reconnect
tick'i hedefi benimsiyor -> TAM connect akisi: UDP sync, naming standards, validation -
kullanicinin hic acmadigi bir modeli kirletir ve versiyonlar.

Mevcut `_pipelineOwnedLocators` korumasi bunu KAPSAMIYOR: o set stem+VERSION ile ayni
modelin kopyasini esliyor; integrate hedefi FARKLI klasorde FARKLI bir model.
- [x] `_integrateOwnedStems` (stem-anahtarli) eklendi; uc benimseme kapisi da
      (`ConnectToModel`, reconnect known-locator seed, `TabSwitch`) artik onu da soruyor.
- [x] Guard merge BASLAMADAN once kuruluyor, ve `_pipelineOwnedLocators` ile ayni omur
      kuralina tabi: ancak model GERCEKTEN kapandiginda dusuyor (reconnect tick'inde
      prune). Yani basarisiz bir merge hedefi acik birakirsa guard armed kaliyor.

## Ayni hata sinifi - review'da bulunup duzeltilenler
- `ColumnValidationService`'in IKI timer'i (2sn CheckForChanges + 500ms window monitor)
  6 gate noktasinin HICBIRINDE degildi. `CheckForChanges` kolon SILEBILIYOR, yani
  buradaki reentrancy sadece boyama artefakti degil. Gate eklendi.
- `DdlApprovalDialog`'daki iki `CloseActiveModelFast` cagrisi ("Save Models" +
  "Mart Offline" kaskadi) gate'siz kosuyordu. Ikisi de `AlterWizardGate.Enter` icine alindi.
- `SetAllRowCheckboxes` UIA'yi MTA threadpool thread'inden cagiriyordu; artik UI
  thread'ine marshal ediliyor (marshaller yoksa inline - checkbox'lar erwin'in kaydedip
  kaydetmeyecegini belirliyor, sessizce atlamak en kotu secenek).

## LIVE TESTTE BAKILACAKLAR (en riskli 3 nokta)
1. **"Close Model" iki checkbox sutunu.** `SetAllRowCheckboxes` UIA ile satirdaki TUM
   TogglePattern'leri ON yapiyor (hangi sutun hangisi diye tahmin etmiyor). Kayitta
   checkbox'lar XTPReport ICINDE, ayri pencere degil - iki sutunun ikisinin de
   isaretlendigi dogrulanmali.
2. **Wizard sayfa navigasyonu.** Back->Overview + Next x2'nin Merge modunda da Right
   Model sayfasina getirdigi dogrulanmali (CC modunda kanitli).
3. **Iki description diyalogunun SIRASI.** Kod, birinciyi hedef (`2_TEST`) ikinciyi
   calisilan (`1_DEV`) model kabul ediyor - kayittaki sira bu. Versiyon numaralarindan
   teyit et (kayitta once "Version 18" = hedef, sonra "Version 19" = calisilan).

## Kayittan pratik notlar
- Kosuda `1_DEV` v18 idi; akis sonunda `2_TEST` "Version 18", `1_DEV` "Version 19"
  aciklama diyalogu gosterdi.
- "Close Model" ile "Mart Offline" ayni id setine sahip; ayrim PENCERE BASLIGI ile
  yapilmali (mevcut sweep zaten boyle yapiyor).

## 2026-07-26 17:25 - erwin DEADLOCK: iki es zamanli integrate kosusu

Canli kanit (`erwin-addin-debug.log` + `erwin-native-bridge.log` + donmus process'in
pencere dokumu). Zincir:

1. **Kosu A** 17:23:10'da basladi. 17:24:09'da `description #1` yazdi ve **takildi**:
   erwin ikinci dialogu hic acmadi, ama dongu 120sn'lik deadline'i dolana kadar
   (~17:25:34) taramaya devam etti.
2. Kullanici bitti sanip 17:24:31'de **Reload Config**'e bastı. Bu
   `RefreshIntegrateTab` -> `RebuildIntegrateTab` cagirdi; eski (disabled) buton
   dispose edildi, yerine **yeni ve ENABLED** bir buton kuruldu.
   `btn.Enabled=false` bir re-entrancy korumasi DEGILDI.
3. Kullanici tekrar Integrate'e bastı -> **Kosu B** 17:24:37'de basladi. A hala canli.
4. 17:25:04'ten itibaren iki dongu ayni dialoglari paylasti:
   - Ayni `Close Model` 0x910832 uzerinde **4 checkbox pass** (her tik bir TOGGLE
     oldugu icin cift sayida pass = ilk pass'i geri alir),
   - iki ayri `IDOK` post,
   - ayni `Description for` dialoguna 13ms arayla iki `Save` post; A kendi
     `secondComment`'ini (6 char) yazdi, B 13ms sonra BOS metinle uzerine yazdi.
5. erwin Mart save'ine reentrant girdi ve kilitlendi.

Donmus process'in (pid 77552) o anki hali:
```
0x1010A98 #32770  'Saving in progress....'   (enabled, en ustte)
0x910832  #32770  'Close Model'              (disabled, altta)
0xEF2830  XTPMainFrame 'erwin DM - [Mart://.../2_TEST/MetaRepo : v19 : ER_Diagram_164 * ]'
```
6 saniyede 0 CPU + `Responding=False` -> gercek deadlock, yavaslik degil.

### Yan bulgu: checkbox varsayilani BOS
Loglar tutarli: 15:11 kosusu 0 kutucuk isaretledi -> **description hic gelmedi**
(save olmadi). 17:23 kosusu 1 pass (tek sayi) -> description geldi. 16:12 kosusu 3
pass (tek sayi) -> 2 description + tamamlandi. Yani **tek sayida pass = isaretli =
dogru**. Bu yuzden dialog basina TAM BIR pass sarttir; ikinci pass zarar verir.

### Alinan onlemler
- `MartMartAutomation` icinde **process-genelinde single-flight** (`Interlocked`
  claim + `finally` release). `IsIntegrateRunning` disariya advisory olarak aciliyor;
  `OnIntegrateClicked` yorum ekranini ACMADAN once reddediyor.
- Cascade artik her dialogdan sonra **kapanmasini bekliyor** (`Answered`, 60sn).
  Kapanmazsa TEKRAR TIKLAMIYOR - dongu iptal, log net. Tekrar tiklama = toggle geri
  alma = kaydedilmemis model.
- **60sn idle bound**: hic dialog gelmezse dongu birakiyor (eskiden sabit 120sn
  squat ediyordu, sonraki kosunun dialoglarini kapiyordu). Mutlak deadline 240sn'ye
  cikarildi cunku artik her adim kendi icinde sinirli.
- Bos versiyon yorumu artik logda WARNING olarak gorunuyor.

## 2026-07-26 18:10 - iki yeni bulgu (canli kosu)

### 1. Description dialogu DOLDURULAMADI (kullanici elle yazdi)
Log:
```
18:10:07.340 'Close Model' - ticking save + close
18:10:08.160 checkbox column(s) clicked
18:10:39.298 'Mart Offline' - Save-to=Close + OK      <-- 31 saniye sonra
18:11:40.980 cascade IDLE for 60s ... 0 version comment(s); model windows 1 -> 1
```
Aradaki 31 saniye, `Answered()` helper'inin `Close Model` kapansin diye BLOKE
bekledigi suredir. Ama erwin `Description for ...` modalini **hala acik olan
`Close Model`in USTUNE** aciyor. Dongu o sirada tek bir pencereyi bekledigi icin
description'i hic aramadi; kullanici elle yazmak zorunda kaldi.

16:12 kosusunda calismasinin sebebi de bu: orada dongu her tur bastan tariyordu ve
`Close Model` (description modali ustteyken DISABLED oldugu icin) bulunamiyor,
description dalina dusuluyordu.

**Duzeltme:** bloke bekleme kaldirildi, yerine **cevaplanmis dialog defteri**
(`Dictionary<HWND,caption>`) geldi. Dongu canli kaliyor, ayni dialogu iki kez
tiklamiyor. Caption da saklaniyor cunku MFC HWND geri donusturuyor; ayni handle
farkli caption tasiyorsa o BASKA bir dialogdur ve cevaplanmalidir. Yok olan
pencereler her turda defterden siliniyor.

### 2. Sonuc mesaj kutusu erwin'i kilitledi
Log'un SON satiri reconnect timer'i:
```
18:11:40.979 [WIZARD-GATE] Integrate (Mart Merge) finished - timer ticks resumed
18:11:40.980 cascade IDLE ...
18:11:41.709 Reconnect guard: pruned 1 Integrate target stem(s) ... normal handling restored
<log burada bitiyor>
```
O anda ekranda add-in'in kendi "The integrate into 2_TEST did not complete" mesaj
kutusu vardi. Mesaj kutusu **ic ice mesaj dongusu** calistirir ve WM_TIMER oraya da
dagitilir; yani gate dustugu anda reconnect tick'i modal pump'in ICINDE, merge'i
yeni bitmis bir model uzerinde calisti. erwin kilitlendi (0 CPU, Responding=False).

**Duzeltme:** `AlterWizardGate` scope'u sonuc dialogunu de kapsiyor
(`OnIntegrateClicked` icinde ref-counted ikinci `Enter`). Tick'ler kullanici OK'a
basana kadar askida.

## HALA BILINMEYEN (manuel kesif gerekiyor)
- `Close Model` grid'i KAC satir listeliyor? Kosu `model windows 1 -> 1` ile bitti,
  yani calisilan model kapanmadi. `SetAllRowCheckboxes` sadece **row 0**'i tikliyor
  (`gr.top + 31`), ikinci satir varsa isaretlenmiyor.
- Kutucuklarin VARSAYILAN durumu (log'lar "bos" diyor ama kanit dolayli).
- `Close Model` toolbar'inda ENABLED olan 20042 / 20043 / 20045 ne yapiyor?
  Biri "check all" ise tum geometri tahmini cope gider.

## 2026-07-26 18:40 - kullanici ekran goruntuleri: iki kesin bulgu

### A. "Close Model" TEK SATIR listeler ve kutucuk durumu YAPISKAN
Ekran goruntusu: grid'de tek satir - `MetaRepo*` / `MetaRepo` /
`Mart://TestRoot/Kursat/Integrate Test/2_TEST`. Yani dialog SADECE marttan acilan
HEDEF model ile ilgili. Row 0'i tiklamak dogru; eksik satir yok.

Kullanici: "Ilk acilista 'close' secili ve 'save' secimsiz gelmisti, ben save'i de
secip OK dedim" + "son diyalogda nasil birakildi ise oyle gelebilir".

**Bu, kor tiklamayi olduruyor.** Tik bir TOGGLE ve baslangic durumu YAPISKAN
(erwin son birakildigi hali hatirliyor). Iki kolona da kosulsuz tiklamak
[close=acik, save=kapali] halini [close=KAPALI, save=acik] yapar - yani model
kaydedilir ama KAPANMAZ, ya da tam tersi.

**Cozum: once OKU, sonra gerekirse tikla, sonra DOGRULA.**
XTPReport hucresi hicbir Win32 mesajina cevap vermiyor ve UIA bu dialoglarda yasak
(OLEACC AV, 15:12 canli cokme). Geriye EKRAN PIKSELI kaliyor:
`ReadCheckbox` hucre merkezinde 5x5 ornekliyor, koyu piksel sayiyor. Satir secili
oldugu icin arka plan mavi (0,120,215); koyu esigi (r,g,b < 110) bu maviyi ELER,
gevsek bir esik secili satirdaki her bos kutuyu "tikli" okurdu.

Akis: oku -> zaten istenen haldeyse TIKLAMA -> degilse tikla -> TEKRAR OKU.
Ikinci okuma istenen hali vermezse o x bir kutucuk uzerinde DEGIL demektir:
`SetAllRowCheckboxes` false doner ve cagiran **OK'a basmaz**. Merge'i cope atma
riski yerine dialog kullaniciya birakilir.

Ayrica her cagride `[checkbox] gutter` izi loglaniyor (grid solundan +71 piksele
kadar W/#/. siniflamasi) - kolon offset'i yanlissa log'dan kalibre edilir,
tahminle degil.

### B. "Description for" GERCEKTEN "Close Model"in USTUNDE aciliyor
Ikinci ekran goruntusu bunu dogruladi (ek2): `Close Model` arkada acik dururken
`Description for 'MetaRepo' Version 17` onunde. Cevaplanmis-dialog defteri
duzeltmesinin dayandigi varsayim DOGRU.

### C. "Mart Offline" ekrani da geliyor -> "Close" + OK
Kullanici teyidi + ekran goruntusu ("Save to" kolonu = Close). Bu zaten
`DismissMartOfflineDialogWin32` ile isleniyor (18:10:39 log'unda cmdId=20049
gonderildi). Degisiklik gerekmedi.

### D. Calisilan modeli KIMSE kapatmiyordu
"Close Model" tek satir = sadece hedef. Calisilan model (`1_DEV`) icin erwin hicbir
sey sormuyor, dolayisiyla dongu gelmeyecek dialoglari bekliyordu
(`model windows 1 -> 1`). Artik add-in **acikca kapatiyor**: merge oncesi aktif MDI
child handle olarak yakalaniyor, hedefle ilgili dialoglar bittikten ve ekranda
HICBIR cascade dialogu kalmadiktan sonra (+3sn settle) o pencereye `WM_CLOSE`
gonderiliyor. erwin'in bunun uzerine actigi Save Models / Description / Mart
Offline dialoglarini ayni dongu cevapliyor.

"Ekranda hicbiri yok" kontrolu, "cevaplanmamis olan yok"tan farkli olmak zorunda:
erwin `Close Model`i kaydederken yarim dakika ekranda tutuyor ve o pencere defterde
oldugu icin "cevaplanmamis" gorunmuyor.

## 2026-07-26 19:15 - piksel okuma CALISTI, iki artik sorun kaldi

Log (gercek satirlar):
```
19:15:01 'Close Model' - setting save + close on the row
19:15:03   gutter y=515: .#........#WWW###.W...............WWWWWWW.W..............
19:15:04   col+12 before: 11/25 dark -> CHECKED   -> zaten dogru, TIKLANMADI
19:15:05   col+36 before:  0/25 dark -> unchecked
19:15:06   col+36 after : 11/25 dark -> CHECKED   -> tiklandi ve DOGRULANDI
19:15:06   both checkbox columns VERIFIED CHECKED
19:15:08 description #1 filled (20 chars) on 'Description for MetaRepo Version 18'
19:15:16 target handled - closing the working model 0xF22A5E
19:15:17 'Save Models' - setting save on the row
19:15:20   gutter y=515: .#........#WWW###.W...............................WWW...W.W.....W..W
19:15:20   col+12 before: 11/25 dark -> CHECKED
19:15:22   col+36 after :  0/25 dark -> unchecked -> ABORT
```
Yani: kutucuk okuma+dogrulama tam istendigi gibi calisti, aciklama otomatik yazildi,
calisilan model kapatma istegi gonderildi. Kalan iki sorun:

### 1. Kutucuk kolon SAYISI diyaloga gore degisiyor
`Close Model` = IKI kutu (close + save). `Save Models` = TEK kutu (save).
+36 ikinci diyalogda model-adi hucresine denk geliyor. Sabit iki kolon varsayimi
bu yuzden abort etti (dogru davranis, ama akis bitmedi).

**Duzeltme:** kolonlar artik ARITMETIKLE degil KESIFLE bulunuyor.
`FindCheckboxColumns` gutter'i tariyor, `LocateCheckboxCentres` (saf fonksiyon)
W/# kosularindan kutu merkezlerini cikariyor. Secili satirda arka plan '.',
kutu ici 'W', kenar/tik '#'; model adi da beyaz yaziliyor ama dagitik, bu yuzden
kosu uzunlugu penceresi [4..14] ikisini ayiriyor.

Yukaridaki IKI GERCEK iz `CheckboxColumnLocatorTests`'te birebir test verisi:
Close Model -> [13, 37], Save Models -> [13]. Tahmin degil, kayittan.

### 2. Basarisizlik mesaj kutusu erwin'i kilitledi
Ekranda `Save Models (Not Responding)` ve onunde add-in'in WinForms uyarisi;
erwin 0 CPU. Sebep: erwin'in KENDI modal dongusu acikken ayni UI thread'e ikinci
bir WinForms modal koymak. Pencere dokumu bunu net gosteriyor
(`XTPMainFrame enabled=False` = erwin'in modali aktif).

**Duzeltme:** basarisizlik yolunda `ErwinAddIn.ShowTopMostMessage` (native
`MessageBoxW`). WinForms'un modal makinesini (owner disable, IsDialogMessage)
tasimiyor. Ayni kacis yolu UDP-sync izin hatasinda zaten kullaniliyor.
Basari yolunda erwin bosta oldugu icin WinForms dialogu kaldi.

## 2026-07-26 21:20 - zincir TAM CALISTI, donma zincirden SONRA

Log, bastan sona basarili:
```
21:20:05 'Close Model' - setting save + close
21:20:07   2 checkbox column(s) at +13, +37        <- kesif dogru (Close Model = 2 kutu)
21:20:08   col+13 before: CHECKED -> tiklanmadi
21:20:10   col+37 after : CHECKED -> tiklandi, dogrulandi
21:20:12 description #1 filled (12 chars) on 'Description for MetaRepo Version 19'
21:20:21 target handled - closing the working model
21:20:22 'Save Models' - 1 checkbox column(s) at +13   <- kesif dogru (Save Models = 1 kutu)
21:20:25   col+13 before: CHECKED -> tiklanmadi
21:20:26 'Mart Offline' - Save-to=Close + OK
21:20:28 'Mart Offline' - Save-to=Close + OK
21:20:30 cascade complete (5 dialog(s), 1 comment, model windows 1 -> 0)
<log burada bitiyor>
```
Kolon kesfi iki diyalogda da dogru sayiyi buldu, kutucuklar okunup dogrulandi,
aciklama otomatik yazildi, calisilan model kapatildi. Kalan tek sorun ZAMANLAMA.

### Kok neden: "model penceresi kalmadi" != "erwin isini bitirdi"
`Save Models` OK'ledikten sonra erwin son versiyonu Mart'a YAZIYOR ve bunu kendi
yuzde gosteren penceresinde yapiyor. Modeller MDI'dan cikmis oluyor, yani
`model windows 1 -> 0` sarti YAZMA HALA SURERKEN saglaniyor. Cascade o bosluga
"complete" dedi, add-in de sonuc popup'ini mesgul erwin'in uzerine koydu.
Kullanicinin gordugu: save %100'e ulasmadan pencere gitti, arkada
"erwin Application is not responding".

### Duzeltme 1: erwin'in dialoglari temizlenene kadar bekle
`WaitForErwinDialogsToClear` (120sn siniri) - `#32770` SINIFINA gore ariyor,
basliga gore DEGIL: ilerleme penceresinin basligini hardcode etmek yanlis olurdu,
erwin ne acarsa sayilir. Bekledigi baslik loglaniyor, timeout'ta "durumu
dogrulanmadi" diyerek devam ediyor - sonsuz bekleme yok.

### Duzeltme 2: BASARI popup'i da native
Onceki turda sadece BASARISIZLIK yolunu native yapmistim; basari yolu WinForms
kalmisti. Ayni tuzak: erwin ve add-in tek UI thread paylasiyor, erwin bir sey
calistirirken ustune WinForms modal koymak thread'i kilitliyor. Basari yolu daha
guvenli DEGIL - erwin arkada hala Mart'a yaziyor olabilir. Her iki yol da artik
`ErwinAddIn.ShowTopMostMessage`.

## 2026-07-26 21:33 - AKIS TAMAM. Kalan tek sorun input block'un politikasiydi

Log bastan sona temiz:
```
21:33:20.212 cascade complete (5 dialog(s), 1 comment, model windows 1 -> 0)
21:33:32.111 [WIZARD-GATE] Integrate (result dialog) finished - ticks resumed
21:33:32.188 Session lost - model was closed. Monitoring stopped.
21:33:32.714 [ERWIN-BLOCK] erwin input blocked - minimize the add-in to use erwin   <-- SORUN
21:34:32.257 Glossary auto-refreshed: 21 entries                                     <-- add-in saglikli
```
Native mesaj kutusu deadlock'u cozdu: bu kosuda kilitlenme YOK.

### "erwin kilitlendi" bu sefer YANLIS TESHIS OLURDU
Olcum: `Responding=True`, `XTPMainFrame enabled=False`. Yani erwin **canli ve mesaj
pompaliyor**, sadece DEVRE DISI. Donma ile devre disi birakilma bambaska seyler:
donmus pencere "(Not Responding)" gosterir, devre disi pencere normal gorunur ama
her tiklamayi yutar. Bu ayrimi olcmeden kod degistirmek yanlis yere yama olurdu.

### Kok neden: WP 329 input block'u MODEL YOKKEN de uyguluyordu
Block'un amaci "kullanici add-in'in arkasindaki MODELE tiklamasin". Integrate iki
modeli de kapattiktan sonra ortada model YOK - korunacak bir sey yok, ama frame
devre disi kaldigi icin kullanici erwin'in File/Mart menusunden yeni model de
acamiyor, X ile kapatamiyor bile.

**Duzeltme:** politika saf bir fonksiyona cikarildi
(`ErwinInputBlock.ShouldBlock`) ve `hasOpenModel` sarti eklendi:

    addinOnScreen && hasOpenModel && !suspended && !debugMode && !wizardGate && !martSaveGate

`hasOpenModel` = `Win32Helper.GetActiveMdiChild(main) != Zero`. Timeout'lu; donmus
ya da MDI olmayan frame Zero donuyor, yani "bilemiyorum" -> BLOKLAMA. Sinifin
zaten benimsedigi "supheliyse erwin kullanilabilir kalsin" yonu.

8 test: `ErwinInputBlockPolicyTests`.

### Kurtarma
Devre disi kalmis frame `EnableWindow(hwnd, true)` ile geri aciliyor - process
oldurmeye gerek yok, oturum korunuyor.
`scratchpad/reenable.ps1` bunu yapiyor (tum XTPMainFrame'leri tarayip devre disi
olanlari geri aciyor).
