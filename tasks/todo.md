# Integrate YUZEYI addin'den tamamen kaldirildi, 2026-07-25 (Developed)

Kullanici sirasiyla: (1) "Integrate tabina gerek yok, karta tasi", (2) kart yanlis
konumlanmisti - duzeltildi, (3) "General tabindaki Integrate kartini da kaldir".
Sonuc: addin'de artik HICBIR ortam-promosyon yuzeyi yok.

- [x] `#region Integrate card` (318 satir) silindi: `_integrateCard`, `IsIntegrateEnabled`,
      `RefreshIntegrateCard`, `RemoveIntegrateCard`, `LayoutGeneralFooter`,
      `BuildIntegrateCard`, `ResizeIntegrateCard`, `BuildIntegrateMessage`,
      `OnIntegrateClicked` + tema sabitleri.
- [x] General tab layout'u HEAD'deki haline geri donduruldu (cardX/cardW local const,
      footer sabit Y, copyright/Close-erwin Bottom anchor, AutoScroll yok).
      `git diff` bu bolgede SIFIR fark gosteriyor.
- [x] Connect'teki `RemoveIntegrateCard()` / `RefreshIntegrateCard()` cagrilari kalkti.

## KORUNAN (silinmedi - onaya gonderim buna bagimli!)
`IntegrationPlanner.ResolveApproverCatalogPath` + `ResolveCurrentEnvironment` +
`IntegrationEnvironmentService.GetEnvironments/GetRelations` HALA KULLANILIYOR:
approver zincirinin ortamsiz catalog path'ini bunlar cozuyor
(`ShowDdlForApproval` ve `PromotionFlow.BuildSendContext`). Silinirse Integrate
modellerinde onay yonlendirmesi tekrar bozulur.

## Artik sahipsiz kalan (committed kod - SILINMEDI, kullaniciya soruldu)
- `MartMartAutomation.PromoteViaMartMerge` - merge seam'i; zaten hicbir zaman
  implemente edilmemisti (sadece log atip `false` donuyor, "NO destructive action").
- `IntegrationPlanner.BuildTargets` + `PromotionTarget` - production'da cagiran yok
  (testleri duruyor).
- `Forms/EnvironmentPipelineDiagram` - Panel olarak artik cizilmiyor, ama
  `TryParseHex` yardimcisi `DdlApprovalDialog` tarafindan hala kullaniliyor.
- `INTEGRATE_ENABLED` addin'de artik hic okunmuyor.

---

# (ARSIV) Integrate tab -> General tab karti, 2026-07-25

Kullanici: "Integrate tab'ina gerek yok tamamen kaldir, grafiksel gosterim General
tabindaki Integrate kartina tasinsin; kart sadece INTEGRATE_ENABLED modellerde gorunsun."

- [x] `tabIntegrate` TAMAMEN kaldirildi (Designer'dan alan + kurulum + declaration).
- [x] `SetIntegrateTabVisible` / `RefreshIntegrateTab` / `RebuildIntegrateTab` ->
      `RemoveIntegrateCard` / `RefreshIntegrateCard` / `BuildIntegrateCard`.
- [x] Kart General tabinda Glossary kartinin altinda, `CreateSectionCard("Integrate")`
      ile; icinde ayni `EnvironmentPipelineDiagram` + legend + hint.
- [x] Kart yuksekligi topolojiye gore; `IntegrateCardMaxBodyH`(300) asilirsa kart
      buyumek yerine GOVDE scroll eder (footer ekran disina itilmesin).
- [x] Footer (copyright + Close erwin) artik sabit Y degil: `LayoutGeneralFooter()`
      kart varsa altina alir. Iki kontrolun Anchor'i Bottom -> Top yapildi, cunku
      AutoScroll'lu panelde Bottom anchor kontrolu viewport'a cakiyor, akmiyor.
- [x] Connect basinda `RemoveIntegrateCard()` - onceki modelin topolojisi kalmasin.
- [x] FIX (kullanici ekran goruntusu): kart HIC konumlandirilmiyordu, `y=0`'da kalip
      sayfa basligi + Repository/Model kartlarinin uzerine biniyordu. `BuildIntegrateCard`
      karti sadece OLCUYOR (yukseklik topoloji cizildikten sonra belli oluyor);
      `RefreshIntegrateCard` artik `_generalCardsBottom + 16`'ya yerlestiriyor -
      kolonun EN ALTI, Glossary'nin altinda (kullanici istegi). Log satiri eklendi:
      kartin y/height'i ve footer'in yeni y'si yaziliyor.
- [x] 3 flavor 0 warning/0 error, 939 test yesil.
- [ ] LIVE test (kullanici): INTEGRATE_ENABLED acik modelde kart gorunuyor ve
      diyagram/Promote calisiyor; kapali modelde kart hic yok ve footer yerinde.

---

# Onay yonlendirmesi: bayrak yerine MODEL'in approver zinciri, 2026-07-25 (Developed)

Kullanici: "Integrate icin ayarli modelde yine ApprovedBySystem yapti, oysa
degerlendirmeci tanimladim." Logdan (20:16-20:17 kosusu) iki AYRI sebep cikti.

## Sebep A - kod: karar hala bayraga bakiyordu
`status = _approvalEnabled ? "Pending" : "ApprovedBySystem"` ve `_approvalEnabled` =
`USE_APPROVEMENT_MECHANISM` (= False). Approver tanimlansa bile standart DDL yolu
ApprovedBySystem yaziyordu; approver listesine hic bakilmiyordu.

- [x] `ShowDdlForApproval` artik `USE_APPROVEMENT_MECHANISM` OKUMUYOR. Karar:
      `PromotionService.GetModelPromotionApprovers(configId, martPath)` bos mu?
      Zincir varsa Pending, yoksa ApprovedBySystem.
- [x] Promotion modunda zincir zaten `PromotionSendContext.Approvers` icinde yuklu -
      yeniden okumak yerine o kullaniliyor (tek dogruluk kaynagi; buton metni ile
      yazilan status'un ayrisma ihtimali yok).
- [x] Okuma HATASINDA "kapi var" varsayiliyor (Pending) ve kullaniciya modal ile
      soyleniyor: iki tahminden kurtarilabilir olan bu. "Kapi yok" tahmini hem
      oto-onaylar hem REST callback'i atesler - geri donusu yok.
- [x] `USE_APPROVEMENT_MECHANISM` addin'de HICBIR yerde okunmuyor artik (kalan 3
      referans sadece "bu bayrak emekli" diyen yorumlar).

## Sebep B - KOD (ilk teshiste "veri" sanmistim, DEGIL): anahtar uyusmazligi
`MODEL_PROMOTION_APPROVER` (MetaRepoTmp):
    1012 | Kursat/MetaRepo                  | kursat
    1012 | Kursat/Integrate Test/MetaRepo   | kursat
Acik modelin path'i ise `Kursat/Integrate Test/1_DEV/MetaRepo` (ORTAM KLASORU var).

Ilk bakista "kullanici yanlis path'e tanimlamis" gibi gorundu; admin kaynagini
okuyunca DOGRUSUNUN admin oldugu cikti:
- `IntegratePopup.tsx:97`: `catalogPath = ${data.baseDirectory}/${model}`
- `INTEGRATE_BASE` config 1012 icin `Kursat/Integrate Test` tutuyor
- `IntegrateFlowEndpoints.cs`: ortam klasorleri TURETILMIS (`{base}/{ENV.NAME}`),
  "integrate stores no per-model state by design"; merge lineage
  `{base}/{srcEnv}/{model} -> {base}/{tgtEnv}/{model}`
Yani admin bilerek ORTAMSIZ anahtar kullaniyor: bir pipeline'daki mantiksal model
icin TEK approver zinciri, tum ortamlari yonetiyor. Hata add-in'de: ortama OZEL
path ile ariyordu, hicbir zaman eslesemezdi.

- [x] `IntegrationPlanner.ResolveApproverCatalogPath(martPath, environments)` - saf:
      parent klasor config'in bir ENVIRONMENT'i ise o TEK segmenti dusurur, degilse
      path'i aynen dondurur (yonetilen ortamda olmayan model eskisi gibi calisir).
      Mevcut `ResolveCurrentEnvironment` yeniden kullaniliyor, 4. bir eslestirme yok.
- [x] Iki cagri yeri de bu cozucuden geciyor: `ShowDdlForApproval` (standart yol) ve
      `PromotionFlow.BuildSendContext` (promotion yolu) - ikisi ayni anahtari kullansin.
- [x] Log satiri artik hem model path'ini hem catalog path'ini yaziyor.
- [x] 10 yeni test (3 ortam ayni catalog'a collapse, case-insensitive, backslash,
      yonetilmeyen model degismez, null/blank/ortamsiz-config).

---

# Onaya-gonderim engelleme kurallari (ENFORCE_APPROVAL_BLOCKING_RULES), 2026-07-25 (Developed)

Admin sozlesmesi ayni gun ~18:01'de DEGISTI ve addin YENI sozlesmeye tasindi:
- `BLOCKS_DDL_GENERATION` -> **`BLOCKS_DDL_APPROVEMENT`**
- `ENFORCE_DDL_BLOCKING_RULES` -> **`ENFORCE_APPROVAL_BLOCKING_RULES`** (Approval Workflow ekrani)
- Kapi artik **DDL URETIMINDE DEGIL, ONAYA GONDERIMDE**. Ihlalli model DDL uretip
  inceleyebilir; yapamayacagi sey governance kuyruguna girmek.

## Tasima
- [x] `DdlBlockingRuleGate` -> `ApprovalBlockingRuleGate` (+ Issue/Kind/Result/Verdict/RuleSet
      tipleri), `DdlBlockingRulesDialog` -> `ApprovalBlockingRulesDialog`, log prefix
      `[DDL-GATE]` -> `[APPROVAL-GATE]`, dosyalar yeniden adlandirildi.
- [x] `NamingStandardRule.BlocksDdlGeneration` -> `BlocksDdlApprovement`; izole sorgu
      kolonu `BLOCKS_DDL_APPROVEMENT` (3 lehce); `LoadDdlBlockingRules` ->
      `LoadApprovalBlockingRules`, `GetDdlBlockingRules` -> `GetApprovalBlockingRules`.
- [x] Kapi `BtnAlterWizardProd_Click`'ten KALDIRILDI (yerine neden orada olmadigini
      anlatan yorum). Yeni cagri noktalari:
      - `DdlApprovalDialog.BtnSend_Click` **Step 0** - confirm dialogundan ONCE, cunku
        once versiyon aciklamasi isteyip sonra reddetmek kullanicinin emegini cope atar.
        Hem duz approval-queue insert'ini hem `REQUEST_TYPE='PROMOTION'` send'ini kapsar.
      - `ModelConfigForm.OnIntegrateClicked` - Integrate kartindaki Promote butonu.
- [x] `USE_APPROVEMENT_MECHANISM` HIC dikkate alinmiyor (kaldirilacak bayrak): gonderim
      Pending de olsa, oto-onayli da olsa, promotion da olsa kontrol ediliyor.
- [x] Rapor dialogu reddedilen AKSIYONU basliga yaziyor ("Send to Approve blocked",
      "Integrate to 2_TEST blocked") - bloklanan promote, bloklanan save sanilmasin.
- [x] DDL queue worker'da kapi YOK ve gerekmiyor: worker onaya gondermiyor, DDL'i dogrudan
      kendi kuyruk satirina yaziyor ve review dialogu `_ddlQueueActive` iken aciliyor bile
      degil. Dolayisiyla eski "worker kilitlenir" riski bu noktada mevcut degil.
- [x] 3 flavor 0 warning/0 error, 939 test yesil. `docs/ARCHITECTURE.md` yeniden yazildi.
- [ ] LIVE test (kullanici): ihlalli modelde Generate DDL CALISMALI; "Send to Approve" /
      "Send to Approval" / Integrate Promote ise rapor gosterip HICBIR SEY kaydetmemeli.

## Not
Asagidaki eski plan/inceleme kaydi motorun kendisi icin hala gecerli (kural yukleme,
preflight, model walk, degerlendirme, no-silent-pass kararlari); yalnizca iki isim ve
tetikleme noktasi degisti.

---

# (ESKI SOZLESME - kayit icin) DDL-blocking naming rules, 2026-07-25 - PLAN

Kaynak: `erwin-admin/docs/erwin-addin-ddl-blocking-rules-prompt.md`. Admin tarafi bitti
(MC_NAMING_STANDARD.BLOCKS_DDL_GENERATION bit + config-level ENFORCE_DDL_BLOCKING_RULES
iki seviyeli bool). Addin ENFORCEMENT yapacak: DDL uretmeden once blocking kurallari
modele karsi dogrula, ihlal varsa HIC DDL uretme.

## Recon bulgulari (6 paralel ajan + kritik, hepsi file:line dogrulandi)
- Tek choke point: `ModelConfigForm.BtnAlterWizardProd_Click` (ModelConfigForm.cs:5136).
  4 cagirani var: yesil buton, dev debug butonu, dev spike, DDL queue worker
  (DdlWorker.cs:704). 6 mevcut guard'in hepsi `btnAlterWizardProd.Enabled = false`
  (:5233) ve `HideFormForAutomation` (:5245) ONCESINDE return ediyor -> 5231/5233
  arasi tek dogru ekleme noktasi.
- KRITIK: `_ddlQueueActive == true` iken erken return edilirse worker SONSUZA KADAR
  kilitleniyor (`DdlWorkerTimer_Tick` ilk satiri `if (_ddlQueueActive) return;`,
  Running state'inin timeout'u YOK) ve queue satiri sonsuza dek 'RUNNING' kaliyor
  (reaper yok). Yeni guard blokladiginda `FailAndResetCurrentJob(...)` cagirmak ZORUNDA.
- KRITIK: worker (DDLGENERATOR flavor) connect'te naming standard YUKLEMIYOR
  (ModelConfigForm.cs:1968-1970 "[DDL-ONLY] ... skipping ... naming standards"), yani
  `NamingStandardService.Instance.IsLoaded == false`. Ustelik worker her job'da FARKLI
  config'li model aciyor -> servis config-scope'lu degil (glossary'deki stale-config
  bug'inin ayni sinifi).
- KRITIK (blast radius): `BLOCKS_DDL_GENERATION` kolonunu ana `LoadStandards()`
  SELECT'ine eklemek, kolonu olmayan repoda (migration MSSQL-only) TUM naming
  yuklemesini patlatir -> `IsLoaded=false` -> `ValidateObjectName` her nesne icin BOS
  liste -> tum naming enforcement sessizce kapanir. Bu yuzden kolon ANA sorguya
  EKLENMEYECEK; ayri/izole bir sorguyla sadece gate calisirken okunacak.
- `NamingValidationEngine`'de her "degerlendiremedim" yolu sessizce false/"" donuyor
  (:602, :636, :820, :867, :1023). Gate bunlara guvenemez, kendi preflight'ini yapmali.
- Template kural bir GENERATOR; `EvaluateRule` onu no-op ediyor (:1030) -> blocking
  isaretli bir Template kurali sonsuza dek sessiz PASS olurdu.
- Object-existence kurallari (PropertyCode == "") `ValidateObjectName`'e gorunmez
  (`GetByObjectTypeAndProperty` bos propertyCode'da kisa devre, :673).
- View'da `Physical_Name` YOK (r10) -> `view.Name`. `Physical_Name` bazen `%macro`
  doner -> mevcut okuyucular gibi `.Name`'e dus.
- SCAPI STA/UI-thread'e bagli. Walk senkron olacak (timer'lar preempt edemez), walk
  ICINDE DoEvents/modal YOK.
- Build/test: `dotnet build ErwinAddIn.csproj -c Release` +
  `dotnet test tests\ErwinAddIn.Tests\ErwinAddIn.Tests.csproj -c Release` (880 test
  yesil). `dotnet test erwin-addin.sln` SIFIR test kosar - kullanma.

## Karar noktalari (onay bekleyen 1 tanesi isaretli)
- **[ONAY GEREKIYOR] APPLY_ON x BLOCKS_DDL_GENERATION**: DDL uretimi model geneli bir
  denetim; "yeni nesne" kavrami yok. Onerim: gate APPLY_ON'u YOK SAYAR (Create/Update/
  Both farketmez, hepsi degerlendirilir). Gerekce: (a) prompt "load ALL rules where
  BLOCKS_DDL_GENERATION=1" diyor, lifecycle filtresi yok; (b) `isNew=false` gecersem
  `ApplyOn=Create` + blocking kural HICBIR ZAMAN bloklayamaz = tam olarak yasaklanan
  "sessiz pass"; (c) mevcut model-seviyesi precedent ayni seyi yapiyor
  (`CheckRequiredObjectTypesExist`, TableTypeMonitorService.cs:483-488). Bedeli:
  `ApplyOn=Create` blocking kural, kural yazilmadan once olusmus eski nesneler icin de
  DDL'i bloklar ("kurallar sadece YENI nesnelere" proje kuraliyla gerilim). Iki kez
  opt-in gerektigi icin (kural bayragi + config toggle) kabul edilebilir buluyorum.
  ALTERNATIF: `isNew=false` gecip Create-scoped kurallari atla (o zaman admin'de
  Template gibi bir uyari sart).
- Toggle okuma hatasi (`GetEffectiveBool` throw) = HARD BLOCK (fail-closed), sessiz
  "false" degil. `[DDL-GATES]`'in fail-open davranisini KOPYALAMIYORUM: orada risk
  sadece radio gorunurlugu, burada admin'in acikca istedigi kapiyi atlamak olurdu.
- "Degerlendirilemez" = HARD FAIL sayilacak durumlar (yapisal, SCAPI'siz preflight):
  Template tipli blocking kural / derlenmeyen veya bos REGEXP / bos PREFIX-SUFFIX /
  eksik veya taninmayan LENGTH_OPERATOR-LENGTH_VALUE / SCAPI sinifina eslenmeyen
  OBJECT_TYPE / Required disi bos PropertyCode. Ayrica: kural yuklenemedi, blocking-id
  sorgusu patladi, `Collect` patladi.
- Hedef property okuma hatasi = MEVCUT semantik (bos kabul et, `EvaluateRule` karar
  versin). Boylece yeni tabloda henuz olmayan `Name_Qualifier` yanlislikla bloklamaz.
- Prefix/Suffix icin `ValidateObjectName`'deki canonical-affix bastirmasi AYNEN
  uygulanacak (yigilmis prefix'lerde yanlis blok olmasin).

## Plan
- [x] `NamingStandardRule.BlocksDdlGeneration` property (yalniz gate yolu doldurur).
- [x] `NamingStandardService`: `_loadedConfigId` + `LoadedConfigId`, `SeedForTesting`
      opsiyonel configId parametresi, `EnsureLoadedForConfig(int)`, IZOLE
      `LoadDdlBlockingRules(int configId)` (3 lehce ID sorgusu, hata FIRLATIR) ve
      `GetDdlBlockingRules()`.
- [x] `TableTypeMonitorService.ScapiCollectTypeForExistence` private -> internal
      (reflection testi NonPublic kullandigi icin bozulmaz), 4. bir harita eklemedik.
- [x] `Services/DdlBlockingRuleGate.cs`: saf/testlenebilir parca
      (`DescribeRule`, `GetUnevaluatableReason`, `EvaluateRuleAgainstObject`, `Dedupe`)
      + SCAPI walk (`Evaluate(session, log)` -> `DdlBlockingGateResult`).
- [x] `Forms/DdlBlockingRulesDialog.cs`: UdpSyncDialog kalibi (ListView Details,
      accent strip, `ShowFor` static giris, Copy butonu). Chrome INGILIZCE, satirdaki
      ERROR_MESSAGE admin DB'den AYNEN.
- [x] `BtnAlterWizardProd_Click` icine 7. guard: senkron, modal yalniz
      `!_ddlQueueActive` iken, worker'da `FailAndResetCurrentJob(...)`.
- [x] 30 unit test (xUnit + FluentAssertions, `[Collection("NamingStandardSingleton")]`).
- [x] 3 flavor da (default / PackagedBuild / DdlGenerator) 0 warning 0 error;
      939 test yesil (oncesi 933). `docs/ARCHITECTURE.md` guncellendi.
- [ ] LIVE test (kullanici): toggle KAPALI iken davranis degismiyor; toggle ACIK +
      ihlalli model -> DDL uretilmiyor, rapor aciliyor; ihlal duzeltilince uretiliyor;
      DDL worker'da bloklanan job FAILED yaziyor ve worker bir sonraki job'a geciyor.

## Review (adversarial, 5 boyut x bagimsiz curutucu)
27 bulgu kaldirildi, 22'si curutuldu, 5'i DOGRULANDI ve duzeltildi. Hepsi ayni kok
nedendi: **motor kendi okuma hatalarini yutuyor, gate bunu "eslesmedi"den ayirt edemiyor.**

1. **(critical) Affix bastirmasi APPLY_ON'u geri sokuyordu.** `ApplyNamingStandards`
   affix kume'sini `MatchesApplyOn(rule, isNew)` ile suzuyor; uyusmayan cerceve altinda
   kural apply'dan dusuyor, canonical == deger cikiyor ve ihlal yutulyordu. Sonuc:
   `Both` olmayan HER Prefix/Suffix blocking kurali sessizce bloklamayi birakiyordu -
   tam olarak "ApplyOn'u yok say" kararinin engellemek icin var oldugu sey.
2. **(critical) Ayni bastirma pkMembership'i tasimiyordu.** Gate PK uyeligini Key_Group
   grafiginden cozup veriyor, `ApplyNamingStandards` ise 3-arg overload ile yeniden
   cozuyor (PK'yi okuyamiyor) -> kural dusuyor -> PK kosullu affix kurali hic bloklamiyor.
   -> Tek kavramla ikisi de cozuldu: **bastirma ancak bu kuralin GERCEKTEN dahil oldugu
   bir canonical hesap uzerinden kanit sayilir** (`MatchesApplyOn` + 3-arg
   `IsRuleApplicable` on-kontrolu). 3 regresyon testi eklendi.
3. **(high) PK cozumleme hatalari yutuluyordu** (`ReadPrimaryKeyMemberIds`,
   `ReadObjectId`, `IsPrimaryKeyGroup`). Bos uye kumesi -> her kolon "PK degil" ->
   PK kapsamli kural hicbir seye uygulanmiyor -> PASS. Artik hepsi FIRLATIYOR, mevcut
   walk-level catch bunu Unevaluatable'a ceviriyor.
4. **(high) Hedef property TUM nesnelerde okunamazsa** (admin'in PROPERTY_CODE'u o sinif
   icin gecersiz) her kontrol bos degerde kisa devre yapip "temiz" raporluyordu. Nesne
   basina musamaha korundu, ama "hepsinde basarisiz" artik kural basina tek Unevaluatable.
5. **(critical, curutucudan ek bulgu) DDL-worker flavor'inda `ModelRootProvider` hic
   atanmiyor** (`ValidationCoordinatorService.StartMonitoring` orada calismiyor), bu
   yuzden MODEL-scoped UDP kosullu bir blocking kural SADECE gozetimsiz build'de
   sessizce hic uygulanmiyordu. Gate artik bos ise kendi saglayicisini veriyor ve
   finally'de geri aliyor.
   Ayrica: dangling kosul FK'si (admin'de silinmis UDP/property) artik preflight'ta
   hard-fail; bilinmeyen kural tipi icin default arm; kural basina "applicable" sayaci
   loglaniyor.

Curutulen dikkat cekici iddialar: "gate iyi cache'i bozabilir" (EnsureLoadedForConfig
yalniz cache bos/yanlis config iken yukler), "IsActive filtresi eksik" (izole sorgunun
WHERE'inde var), "Collect null = sessiz pass" (koddaki her walker null'i bos sayiyor;
zorla bloklamak yanlis-blok uretirdi).

Kucuk kalite duzeltmeleri: dedup anahtari ayirici karakterle (farkli ihlaller
carpisip rapordan dusuyordu), overflow artik Debug Log'a TAM yaziliyor (dialog oraya
yonlendiriyor), clipboard export'ta tab/newline normalizasyonu, Reason sutunu gercek
client genisliginden, hucre uzerinde HitTest tooltip, sayac Font'u tek instance,
footer tab sirasi gorsel sira ile, walk sirasinda `UseWaitCursor`, ve durum etiketi
artik "kural ihlali" ile "kural kontrol edilemedi"yi ayiriyor.

## Riskler / admin tarafina bildirilecek
- Migration MSSQL-only (`migrations/20260725_naming_rule_blocks_ddl.sql`); PG/Oracle
  scripti YOK. Gate'in izole sorgusu bu yuzden sadece toggle ACIKKEN calisir ve
  patlarsa yalniz DDL'i bloklar, naming enforcement'i degil.
- `ENFORCE_DDL_BLOCKING_RULES` API'de kayitli sabit degil, sadece DdlGeneration.tsx:41
  icinde string literal; yazim hatasi sessizce "kapali" demek olur.
- Admin tarafi henuz COMMIT EDILMEMIS (erwin-admin working tree).

---

# Bug #332: naming rule "Required=Hayir" force yerine warn olmali, 2026-07-24 (Developed)

OpenProject #332 (Furkan). Rule Management'ta bir kuralin Required alani "Hayir"
olsa bile ihlalde kullanici force ediliyordu; beklenen: sadece uyari + devam.

## Root cause (DB ile dogrulandi)
- Force-vs-warn karari PROPERTY seviyesindeydi (2026-05-24 "Required-property-
  promotion"): bir property'de tek bir required kural varsa, o property'deki TUM
  kurallar (Required=Hayir Regexp/Prefix/Suffix dahil) force ediliyordu.
- MC_NAMING_STANDARD sorgusu: Model.Name'de birden fazla Required-tip kural var
  (1064/1084/1130/1131), o yuzden "Name" hep required sayilip tum Regexp'ler force
  ediliyordu. Zeynep DB'sinde rule#1082 IS_REQUIRED=0 iken bile force = kanit.
- Admin persist DOGRU calisiyor (Zeynep=0, Fiba/MetaRepo/Damla=1). Yani addin bug'i.

## Fix (per-rule, Kursat onayli)
- NamingValidationEngine.RuleForcesInput (tek kaynak): force ancak kuralin KENDISI
  required ise (IS_REQUIRED || RuleType==Required || RuleType==Length). Length tip
  geregi hep force (admin flag'i otomatik true yazacak; addin legacy satirda da tipe
  gore davranir). Regexp/Prefix/Suffix + Required=Hayir = warn-only.
- 3 karar noktasi property-level yerine per-rule: Model + Column (Validation
  CoordinatorService), Table/entity (TableTypeMonitorService).
- Re-prompt donguleri + RevalidatePropertyAfterRevert: yalniz FORCING ihlallerde
  yeniden sorar; forcing bitince kalan warn-only ihlaller settled degere gore
  yeniden dogrulanip consolidated warning'e birakilir (stale mesaj yok).
- 8 yeni unit test (RuleForcesInput). Tam paket: 879 passed / 0 failed. Ana proje
  0 warning / 0 error. WP #332 -> "Developed".
- [ ] LIVE test (user): Required=Hayir ihlalinde warn + devam; Required=Evet ve
  Length hala force.

---

# Version Promotion Phase 2 - single-target picker simplification, 2026-07-24

User: linear pipelines only ever expose ONE reachable target (the next env), so the
"Promote to" dropdown in the DDL Review dialog is pointless there. Approach = Variant 1
(conditional): read-only destination when single target, keep the dropdown when a
config genuinely offers several targets.

## Plan
- [x] Detect single target via PromotionPlanner.TargetsOf(routes).Count == 1.
- [x] Single target: hide the combo, show a read-only destination label instead.
- [x] Keep the combo created + selected so the send path is untouched.
- [x] Multi-target: keep the real dropdown (defensive; user's pipelines are linear).
- [x] Build clean.

## Result
- [x] DdlApprovalDialog.BuildUi: when TargetsOf(routes).Count == 1, hide the target
      combo and render a read-only destination label (owner-painted COLOR_HEX dot +
      target name, mirroring CmbPromoteTarget_DrawItem). The combo stays created +
      SelectedIndex=0 so SelectedPromotionRoute() and the whole send/re-derivation
      pipeline are UNCHANGED (single source of truth for the picked route). The
      "From: <source> (current)" + auto-approve/approval-required indicator stay
      visible so the user still confirms the destination before Send.
- [x] Multi-target configs (a Test->Prod second hop offered alongside a Dev->Test
      re-promote) still get the real dropdown - kept as a defensive path even
      though the user's pipelines are linear.
- [x] Build: 0 warnings / 0 errors (TreatWarningsAsErrors).
- [ ] LIVE test (user): confirm the read-only destination renders and Send targets
      the correct env.

---

# DDL Review blocking-rule report pane - IN PROGRESS 2026-07-28

Requirement (user): when `ENFORCE_APPROVAL_BLOCKING_RULES` is on, the DDL Review window splits
in two - the existing DDL text on the left, a report of which blocking rules passed and which
did not on the right - and the green submit button is disabled while any rule is violated.

## Decisions (settled with the user 2026-07-27)

| | |
|---|---|
| Pane shows | whenever at least ONE blocking rule exists; with no violations it says "N rules checked, no violations" |
| Button | disabled in ALL THREE states; the promotion caption "Send to Approval" becomes "Send to Approve" |
| Unevaluatable rule | its own "Not checked" state, and it STILL disables the button (admin data problem, fail-closed) |
| Scope | whole model, and the pane says so; sorted so DDL-relevant objects surface first |
| Rule label | admin `NAME` when present, generated fallback otherwise |
| Issue cap | 200 -> **20** |
| Existing modal | REMOVED; a blocked Integrate gets a short message pointing at Generate DDL |
| Re-check button | none - the left pane's DDL would be stale, so it is Cancel, fix, regenerate |
| Evaluation | at dialog OPEN (forced by the "always show the pane" decision), on the owning STA, with a non-pumping progress indicator |

Open-time evaluation is only affordable because of the 221x perf work below: 12.5 s on a
8,401-column model instead of 46 minutes.

## Layer 1 - DONE, tested (no COM, no DB)

- [x] `MC_NAMING_STANDARD.NAME` / `DESCRIPTION` now loaded into `NamingStandardRule.Name` /
      `.Description`, in all three dialects, aliased `RULE_NAME` / `RULE_DESCRIPTION` to avoid
      colliding with `ot.NAME AS OBJECT_TYPE`. The MSSQL form was run against the live
      `MetaRepoTmp` before committing: valid, 20 rules for config 1012. The columns exist in
      every admin repo but are unpopulated in most, so the fallback label is the normal path.
- [x] `ApprovalRuleStatus` (Passed / Failed / NotApplicable / NotChecked) and
      `ApprovalRuleReportRow`, plus `RuleReport`, `GateIssues` and `HasReport` on
      `ApprovalBlockingGateResult`. `NotApplicable` is a first-class state: "the condition
      matched nothing" previously existed only in a Debug Log line, and showing it as "Passed"
      would tell a reviewer the rule verified their model when it inspected nothing.
- [x] The walk accumulates one `RuleOutcome` per rule - passing ones included - including the
      rules it never reached (preflight-rejected, DB-flagged but not loaded, aborted walk).
      Status and counts are computed PRE-CAP; only the detail rows come from the capped slice.
- [x] `MaxReportedIssues` 200 -> 20.
- [x] `ApprovalBlockingIssue`'s string parameters are now `string?`, which is their actual
      contract (the ctor normalises them). They were non-nullable, so feeding them from the
      null-oblivious `NamingStandardRule` made the flow analysis warn at safe call sites.
- [x] `tests/ErwinAddIn.Tests/ApprovalRuleReportTests.cs` - 14 tests. The load-bearing one:
      25 violations with an EMPTY reported slice must still render Failed with count 25.
- [x] Build 0 warnings / 0 errors. Tests 1001/1001 green.

## Layer 2 - CODE-DONE (awaiting live test)

- [x] `Forms/ApprovalReportPane.cs` - the right-hand pane. Count chips (violated / not checked /
      not applicable / passed) in a WRAPPING flow panel because the splitter makes the width
      user-controlled; rule list ordered failures-first; per-rule detail in an in-pane read-only
      wrapping TextBox rather than a nested dialog (stacking a modal over an already-modal review
      window is hostile, and long Turkish admin ERROR_MESSAGE text would clip in a cell). A
      `Panel` subclass, matching `EnvironmentPipelineDiagram` - this codebase has no UserControl.
      The subtitle states the verdict is model-wide, because the DDL beside it is an ALTER script
      over a different object set and letting a reviewer assume otherwise makes a violation on an
      untouched legacy table look like a bug.
- [x] `DdlApprovalDialog` takes the precomputed verdict and swaps its Fill control for a
      `SplitContainer` in ONE conditional line at the existing composition point. `FixedPanel =
      Panel2` so the DDL keeps the space on resize. Widens by 440 px, clamped to the working area
      (a CenterParent window that grew past the screen would push its own right-docked button
      strip off-view on a 1366 px laptop or a 1024 px RDP session).
- [x] Submit button disabled in ALL THREE caption states, and `ReenableForRetry` now respects the
      latch - it used to re-enable unconditionally, which would have handed back a green button
      the click-time check refuses. The disabled button EXPLAINS; the check inside
      `BtnSend_Click` is untouched and remains the authority.
- [x] Promotion caption "Send to Approval" unified to "Send to Approve".
- [x] Open-time evaluation via `ModelConfigForm.EvaluateApprovalBlockingRules`, which
      `CheckApprovalBlockingRules` now also uses, so both paths share one marshalled walk. A gate
      that throws returns a BLOCKING result (`ApprovalBlockingRuleGate.Failed`) rather than
      rethrowing, so neither caller can end up silently passing.
- [x] Non-pumping progress: `WalkProgressOverlay` is created INSIDE the marshalled delegate (it
      runs on the UI thread, where controls may be created) and repaints with `Invalidate()` +
      `Update()`. No `DoEvents`, no animation - pumping here is what cost 46 minutes.
      `Evaluate` gained an optional `progress` sink, called between rule groups and every
      50 tables / 2000 objects.
- [x] `Forms/ApprovalBlockingRulesDialog.cs` DELETED. A blocked Integrate now gets a short
      message naming the counts and pointing at Generate DDL, where the pane can actually show
      them. Reproducing a rule report inside a message box for the one path with no DDL would
      have been the worse half of both options.
- [x] Build 0 warnings / 0 errors. Tests 1005/1005 green.

### Cost of open-time evaluation, stated plainly

A successful submit now walks the model TWICE - once for the pane, once for the authoritative
check - so roughly 25 s on the 8,401-column model. Deliberate: erwin's frame is disabled while
the add-in is on screen, but the user is explicitly allowed to minimise the add-in and edit, so
the open-time snapshot cannot be assumed still true at click time. Cancelling costs one walk.

### Live test 1 - PASS state confirmed (2026-07-28 11:34)

First attempt crashed erwin on a SplitContainer property order; fixed in a separate commit and
recorded in `tasks/lessons.md`. Second attempt:

```
11:34:48  collected 8401 COLUMN object(s) in 5378 ms
11:34:59  rule#1081 ... 8401 applicable, 0 unreadable -> 0 violation(s) in 10737 ms
11:34:59  [WALK-GATE] done in 16223 ms
```

Window split, pane rendered, no violations, submit button enabled. 16.2 s at open time on the
8,401-column model.

- [ ] **LIVE test 2 - BLOCKED state:** add an `nvarchar(5000)` column to a table in
      `SQL_BUYUKMODEL`, then Generate DDL. Expect rule #1081 as "Violated" with a red chip, the
      submit button DISABLED, and the detail box showing the offending column plus the admin's
      Turkish ERROR_MESSAGE verbatim.
- [ ] Live-check the fallback path too (it has never run): it is the one thing standing between
      a future pane bug and another dead erwin.

### KNOWN-INCORRECT INTERIM STATE - do not ship (2026-07-28)

`0c31f0f` scopes the blocking-rule check to the tables named by the generated DDL when
"Only Selected Objects" is ticked. **That is narrower than the selection and therefore
under-checks.** A table can be SELECTED but UNCHANGED, in which case it never appears in the
alter script and its rules are never evaluated - a silent governance pass, which is the exact
failure class this gate exists to prevent. Found by live test: 3 tables selected, 1 changed,
only the changed one was checked.

Left in place deliberately (user decision 2026-07-28) while the research below runs, rather
than reverting to the whole-model walk. **Must be either fixed by a real selection mechanism or
reverted before this ships.**

Reverting is `git revert 0c31f0f`; that restores the whole-model walk, which is correct but
broad and costs ~16 s per DDL Review open.

### RESEARCH IN FLIGHT: how to enumerate the diagram selection

The DDL is the wrong source; we need the SELECTION itself. What is already ruled out:

- `Win32Helper.GetDiagramSelectedEntities` / `ParseSelectedEntityFromOverviewText` read erwin's
  Overview-pane Static text, which is `"MODEL (ENTITY)"` for ONE selection but a COUNT
  ("2 objects") for multi-select - they return null/empty for 2+, by design and by comment.
- The generated alter DDL, for the reason above.

Being investigated in parallel: the SCAPI object model (display/placement classes and any
Selected-shaped property), the live metamodel's ~1,500 property types, erwin's own UI surfaces
readable via Win32 (Model Explorer tree multi-selection, Properties pane, status bar), and side
channels (the Object Filter wizard page after "Yes", the native bridge, temp/registry artefacts).

Acceptance bar for any candidate: it must return ALL selected entities, not just the focused
one - that single-item failure mode is what already sank the Overview-pane approach - and it
must not require a wizard to be open.

### RESULT (2026-07-28): erwin exposes no readable selection. Pure-COM route CLOSED.

- **SCAPI interfaces: nothing.** Full enumeration of the type library in `EAL.dll` (71
  typeinfos) - no Selection member anywhere.
- **Metamodel: 2471 property pages**, the only selection-shaped names being
  `Selection_Grips_Color` (a theme colour) and SQL `Select_Top_*` clauses.
- **`Current_Selection` exists as a property CLASS but is dormant.** Probed live on BOTH the
  Model root and every ER_Diagram: *"Property Current_Selection class cannot be assigned to
  object of X class"*, and the same for its metamodel neighbours `Current_Tool` /
  `Current_Zoom_Level`. **Controls proved the probe sound**: `Current_ER_Diagram_Ref` returned a
  real GUID on Model and `Selection_Grips_Color` returned 16750848 on ER_Diagram, so we were
  addressing the right objects and the failure is the property, not the target.
- Worth remembering for any future property probe: *"cannot be assigned to object of X class"*
  means the class exists but is not carried here, whereas *"not a valid class id, class name or
  object property"* (what `Selection` and `Selected_Objects` returned) means the name does not
  exist at all.

Remaining route, verified by binary export grep but NOT tried: `EM_ERD.dll`
`?GetSelectedModelObjectIds@ERD@@` + `?IsDiagramSelection@ERD@@` with
`?CurrentModelSet@ECX@@` - statically imported by erwin.exe, returns `std::set<unsigned int>`
so N-safe, and the bridge already has the identical CSV machinery. Unsolved: those are 32-bit
GDMIds while SCAPI speaks GUID ObjectIds.

- [ ] **DECISION NEEDED:** native EM_ERD route, or the add-in's own table picker. Recommended:
      the picker - 100% reliable, no undocumented export, no mangled name to break on the next
      erwin build, and the checked set becomes explicit and auditable, which is what a
      governance gate wants. Adding a reverse-engineered native call into a governance path is a
      poor risk trade in a process this add-in has already killed twice this week.

### WON'T-DO: scoping the gate walk to "Only Selected Objects" (SUPERSEDED 2026-07-28)

The reasoning below stands on the facts but NOT on the conclusion: the user has since asked
twice for the scoping and accepted the consequence, so the question is now "how", not "whether".
Kept for the API constraints it records.

Asked for, investigated, dropped. How the checkbox actually works:

- **From-Mart** (the path the review window uses): erwin's own Alter Script Wizard has an
  Object Filter page that raises "Use current diagram selections? You have N entities
  selected."; the bridge answers Yes via `NativeBridgeService.SetUseDiagramSelection`, and
  **erwin scopes the script natively**. The add-in never learns which tables. When the box is
  ticked the automation must WALK the wizard pages instead of jumping to Preview
  (`requireObjectFilterPass`), or the Object Filter page is skipped and the filter silently
  drops (regression verified 2026-05-30).
- **From-DB**: that popup never appears, so the add-in post-filters the captured DDL TEXT by
  regex against the `Physical_Name` of the entities read off the diagram.

Why the gate is NOT scoped to it:

1. **The selection cannot be read for more than one object.** SCAPI exposes no selection API,
   so `Win32Helper.GetDiagramSelectedEntities` parses erwin's Overview pane Static text
   (`"MODEL (ENTITY)"`). With several entities selected erwin shows a COUNT ("N objects") and
   the method returns an EMPTY list. Scoping a walk to that list would check ZERO objects and
   report "no violations" - precisely the silent pass this feature exists to prevent.
2. **It would be a governance bypass.** The gate answers "may this MODEL enter the approval
   queue". Scoped to the diagram selection, a modeller passes it by selecting clean tables.
3. The performance motive is gone: 16 s, not 46 minutes, and the remaining optimisations
   (single model-wide `Collect(root,"Attribute")`) do not weaken the gate at all.

Consequence, by design: with the box ticked the DDL shows only the selected tables while the
pane can still report - and block on - a violation in an untouched one. The pane's subtitle
states the verdict is model-wide for exactly this reason.

---

# Approval gate walk took 46 MINUTES - timer reentrancy - CODE-DONE 2026-07-27

## The measurement

First live run of the approval blocking-rule gate with a rule actually flagged
(`MetaRepoTmp`, `CORPORATE_PROPERTY.ENFORCE_APPROVAL_BLOCKING_RULES=True` at corp=4, rule
#1081 `COLUMN.Physical_Data_Type` Regexp rejecting `nvarchar(>4000)`), model
`Mart://TestRoot/Kursat/SQL_BUYUKMODEL`:

```
18:04:44.065  [DDL-GATE] blocking rules for config 1012: 1 flagged, 1 resolved (#1081)
18:50:40.840  [APPROVAL-GATE] rule#1081 ... checked 8401 object(s), 8401 applicable,
                              0 unreadable -> 0 violation(s).
```

| | |
|---|---|
| Elapsed | 2,756,771 ms = **45 m 56 s** |
| Objects | 8,401 columns / 286 entities |
| Per column | **328 ms** |
| Rules | 1 |
| Violations | 0 |

All 66 previously logged gate runs had early-returned at "no evaluable blocking rule is
defined", so this walk had never actually run before.

## Root cause

The walk is synchronous on the UI thread and sets **no suspension flag**, unlike every other
whole-model walk in this codebase. A synchronous walk does NOT stop the message loop: the
outbound SCAPI/COM calls pump it themselves, so `WM_TIMER` keeps being dispatched and the
seven periodic ticks each run their own SCAPI work in the middle of the walk.

Proof, verified directly in the log: between the walk's first and last line exactly 139 lines
were written, and they are **46 glossary refresh cycles at exactly 60 s apart** - nothing
else. `_glossaryRefreshTimer` is a `System.Windows.Forms.Timer` (ModelConfigForm.cs:224, file
has `using System.Windows.Forms;` and no alias), whose `Tick` can only be delivered by a
pumping loop. A blocked thread would have coalesced all 46 into one burst at the end.

Calibration from the SAME session and model, 41 minutes earlier: `BaselineDiagramHeartbeat`
walked ~10,000 SCAPI properties in **734 ms**, and `UdpSyncEngine` walked 1,507 UDPs in
958 ms. The gate's own work is ~51,800 IDispatch ops, i.e. seconds. So ~99% of the wall time
was not the gate's work.

The comment at ModelConfigForm.cs:5612-5617 asserted the opposite ("ShowBusyOverlay is
deliberately NOT used because it calls DoEvents, which would let the validation timers
re-enter SCAPI mid-walk"). Not calling `DoEvents` does not stop the pump. Comment corrected.

This is the THIRD occurrence of this defect class: black-rectangle wizard reentrancy ->
`AlterWizardGate`; Mart Save 15 s freeze -> `MartSaveGate`; now the approval gate.

**Not yet proven:** that the reentrant tick work accounts for the missing ~2,750 s. The
100/250/500/2000 ms ticks do not log on a stable tick, so they are invisible in the log. One
of three adversarial review lenses refuted exactly this attribution. The skipped-tick census
below is what settles it.

## What was implemented

- [x] `Services/ModelWalkGate.cs` - ref-counted gate (the `MartSaveGate` pattern) raised
      around a whole-model walk. Carries a per-site **skipped-tick census** and a stopwatch;
      on the outermost dispose it logs
      `[WALK-GATE] <reason> done in <N> ms - add-in timer ticks resumed; <T> tick(s)
      suspended [site=count, ...]`. That single line is the measurement that either confirms
      or refutes the attribution above.
- [x] `Services/AddinTickGate.cs` - the condition now lives in ONE place. All seven
      UI-thread ticks previously carried their own copy of
      `if (AlterWizardGate.IsOpen || MartSaveGate.IsActive) return;`, which is exactly why a
      third source could be (and was) missed. Sites: `ModelConfigForm.Reconnect`,
      `ModelConfigForm.GlossaryRefresh`, `ModelConfigForm.PuWatcher`,
      `ColumnValidation.Monitor`, `ColumnValidation.WindowMonitor`, `Validation.Monitor`,
      `Validation.WindowMonitor`.
- [x] `ErwinInputBlock.cs:143` deliberately NOT routed through the new gate: it consumes
      `AlterWizardGate.IsOpen` / `MartSaveGate.IsActive` as inputs to erwin's input-block
      policy, so feeding a model walk in would re-enable erwin's frame mid-walk.
- [x] `ModelConfigForm.CheckApprovalBlockingRules` wraps `Evaluate` in
      `using (ModelWalkGate.Enter(...))`, inside the existing `UseWaitCursor` try/finally.
- [x] Gate instrumentation: collect phase timed and logged
      (`collected N COLUMN object(s) in X ms`), per-rule summary line now carries elapsed,
      and coarse progress lines during collect (every 50 tables / 2000 objects) so a slow
      walk can never again be indistinguishable from a hang. Interval is coarse on purpose -
      `AddinLogger` opens/appends/closes the file per line under a global lock.
- [x] `tests/ErwinAddIn.Tests/ModelWalkGateTests.cs` - 8 tests: ref counting, nested scopes,
      double dispose, concurrent census, and `AddinTickGate` skip behaviour.
- [x] Build 0 warnings / 0 errors (TreatWarningsAsErrors). Tests 987/987 green.

- [ ] **LIVE retest (user):** open `SQL_BUYUKMODEL` from `MetaRepoTmp`, Generate DDL, press
      the green button, then read the `[WALK-GATE]` line. Expected: seconds instead of
      46 minutes, and a large suspended-tick count. If the time does NOT drop, the census
      tells us where it actually goes instead.

## Retest 1 (2026-07-27 20:30) - gate works, but it was NOT the dominant cost

New binary loaded (`[WALK-GATE]` and collect-progress lines present). Same model.

```
20:30:11.880  [WALK-GATE] Approval blocking gate (Send to Approval) - timer ticks paused
20:31:12.631  collecting COLUMN objects -  50 table(s), 1470 column(s)   (+60.7 s)
20:32:20.908  collecting COLUMN objects - 100 table(s), 2970 column(s)   (+68.3 s)
20:33:28.329  collecting COLUMN objects - 150 table(s), 4470 column(s)   (+67.4 s)
20:34:41.690  collecting COLUMN objects - 200 table(s), 5970 column(s)   (+73.4 s)
20:35:51.401  collecting COLUMN objects - 250 table(s), 7470 column(s)   (+69.7 s)
```

Run was killed by the user before it finished, so there is no evaluate-phase number yet.

- **The gate works.** Zero glossary ticks landed inside the 5.5 minutes of walk; the previous
  run took 46 of them. Timer reentrancy is closed.
- **The attribution was wrong in magnitude.** 328 ms/column before, ~45 ms/column for the
  COLLECT PHASE ALONE now. Roughly 7x, not the 100x-750x the diagnosis claimed. One of the
  three adversarial review lenses refuted exactly this attribution and was right.
- **Dead linear** at ~68 s per 50 tables / 1500 columns. Linear per-object cost, not a
  pathological loop.
- The collect phase's ONLY per-column work was two NAMED property reads
  (`.Name` and `Properties("Physical_Name")`), both purely for the report label. That is
  ~22 ms per named read. The 0.07 ms/op calibration came from `BaselineDiagramHeartbeat`,
  which reads `ObjectId` (a direct member) and `Collect` - NOT named-property lookups. That
  is why it failed to predict this.

## Fix applied after retest 1: the report label is resolved on demand

`GateObject.Display` is now a memoised `Func<string>` instead of an eagerly built string
(ApprovalBlockingRuleGate.cs, `#region Model walk`). The table half of a column label is a
`Lazy<string>` shared by all columns of that entity, so it is read at most once per table and
only if one of its columns reaches the report. At most `MaxReportedIssues` labels can ever be
shown; the walk used to build 8,401 of them. All three construction sites (MODEL root, COLUMN,
generic) are lazy. A resolve failure degrades to `<name unavailable>` rather than an empty
string, so it cannot masquerade as a real object name.

After this change the collect phase does only the enumerator step per column, which makes the
next run a clean split:

| Log line | What it now measures |
|---|---|
| `collected N COLUMN object(s) in X ms` | pure enumeration + one Collect per entity |
| `rule#1081 ... in Y ms` | exactly 8,401 named `Physical_Data_Type` reads |
| `[WALK-GATE] ... T tick(s) suspended` | how many ticks the gate stopped |

If Y is still ~22 ms per column, a named SCAPI property read is inherently that expensive on
this model and no amount of tidying the walk will help - the feature then needs a different
shape, not a faster loop. If Y is small, the cost was specific to `ReadDisplayName`.

- [x] Build 0 warnings / 0 errors. Tests 987/987 green.
- [ ] **LIVE retest 2 (user):** same steps; read the three lines above.

## Retest 2 (2026-07-27 20:54) - full run, 5.9x faster, and a hard contradiction

```
20:54:34.588  [WALK-GATE] paused
20:57:34.231  collected 8401 COLUMN object(s) in 179403 ms
21:02:21.901  rule#1081 ... 8401 applicable, 0 unreadable -> 0 violation(s) in 287667 ms
21:02:21.920  [WALK-GATE] done in 467325 ms; 7008 tick(s) suspended
              [Validation.WindowMonitor=4207, Validation.Monitor=1865,
               ModelConfigForm.Reconnect=935, ModelConfigForm.GlossaryRefresh=1]
```

| | before | after |
|---|---|---|
| Total | 2,756,771 ms (45 m 57 s) | **467,325 ms (7 m 47 s)** = 5.9x |
| Collect | - | 179,403 ms = 21.4 ms/column |
| Evaluate | - | 287,667 ms = 34.2 ms/column |

- Both fixes are real. 7,008 ticks suspended; `Validation.WindowMonitor` alone would have
  fired ~4,673 times at 100 ms over 467 s and 4,207 were caught, so the ticks really were
  being delivered at ~90% of nominal rate straight through the old walk.
- The lazy label halved the collect phase (45.3 -> 21.4 ms/column), which prices one named
  `Properties(name)` read at ~12 ms.
- Per-object cost is FLAT across the run (+28.0, +34.4, +31.9, +32.5, +32.2 s per 50 tables),
  so nothing degrades as retained RCWs accumulate. That argues against the RCW-cache theory.

**The contradiction that must be resolved before any further change.**
`ValidationCoordinatorService.BaselineDiagramHeartbeat` (:1440-1502) walks the SAME model with
the SAME shape - `Collect(root,"Entity")`, then `Collect(entity,"Attribute")` per entity
(:1482), then a property read on all 8,400 attributes (:1488) - and finishes in **734 ms**.
The gate's collect phase now does STRICTLY LESS COM work than that (no property read at all,
just enumeration) and takes **179,403 ms**. That is 244x slower for less work.

So the remaining cost is NOT explained by anything in the gate's own COM shape. Known
differences between the two contexts, none yet tested:

1. The heartbeat calls `ReleaseCom(entityAttrs)` in a `finally` (:1495) and retains nothing.
   The gate has ZERO `Marshal.ReleaseComObject` in 1,235 lines and retains 8,401 Attribute
   RCWs in a `List` for the whole run. (Weakened by the flat per-object cost above.)
2. The heartbeat reads `ObjectId`, a direct member. The gate reads named properties via
   `Properties(name)`, a lookup over the model's ~1,500 Property_Types.
3. **Context**: the heartbeat runs from a plain timer tick. The gate runs from
   `BtnSend_Click` INSIDE `DdlApprovalDialog.ShowDialog()`'s modal message loop, with erwin's
   main frame disabled by `ErwinInputBlock`. Every COM call pumps that modal loop.

Note: the heartbeat's 734 ms ALSO includes 286 named `Properties("Physical_Name")` reads
(ValidationCoordinatorService.cs:1509), so a named read costs at most ~2.5 ms in that context
against the ~12 ms and ~34 ms derived inside the gate. That leans towards (3), the context.

## Probe added: Services/ScapiCalibration.cs (DEV builds only)

Two wrong attributions is enough; the next step is a measurement. `ScapiCalibration.RunOnce`
times each access shape over the same sample of objects and reports ms/op:

- `Collect(root,"Entity")`, `Collect(entity,"Attribute")` per call, attribute enumeration per step
- `.ObjectId` (direct member) vs `Properties("Physical_Data_Type")` vs `Properties("Physical_Name")`
  (named lookups), each with a THROW COUNT - "every read threw" would itself be the answer,
  and that is the cost the walk's silent catches were hiding

Bounded: 150 objects, 4 s ceiling per shape, once per process per context, never throws.

Wired at two call sites so one session yields the comparison:

| Call site | Context |
|---|---|
| `ValidationCoordinatorService.BaselineDiagramHeartbeat` (after its own log line) | `connect (no modal loop)` |
| `ApprovalBlockingRuleGate.Evaluate` (before the walk) | `approval gate (modal loop)` |

Reading it: same per-shape costs in both blocks means the SHAPE is the problem (named lookup
per object). The same shape orders of magnitude slower in the modal block means the CONTEXT is
the problem - every SCAPI call pumps that loop and erwin's frame is disabled while it runs.

### Calibration result (2026-07-27 22:05 / 22:06) - it is the CONTEXT, not the shape

Same 150 objects, same code, same process, zero throws in either block.

| shape | connect (no modal) | approval gate (modal) | ratio |
|---|---|---|---|
| `Collect(root,"Entity")` | 1.8 ms | 0.6 ms | - |
| `Collect(entity,"Attribute")` | 0.067 ms/call | 1.592 ms/call | 24x |
| enumerate attribute | 0.523 ms/step | 15.824 ms/step | 30x |
| read `.ObjectId` (direct member) | 0.017 ms/read | 7.268 ms/read | **427x** |
| read `Properties("Physical_Data_Type")` | 0.071 ms/read | 11.120 ms/read | 157x |
| read `Properties("Physical_Name")` | 0.037 ms/read | 12.921 ms/read | 349x |

- **The named-lookup theory is dead.** `.ObjectId` is a direct member and is the WORST
  offender at 427x. Named vs direct makes no difference; the context makes all of it.
- **Our timers are not the remaining cause.** The gate block ran INSIDE
  `ModelWalkGate.Enter`, so all seven ticks were already suspended, and it was still 400x.
- **0 threw** in both blocks, so the exception-storm theory is dead too.
- At connect the whole walk would cost 8,401 x ~0.6 ms = **~5 seconds**. The COM shape the
  gate uses is fine. It is where it runs from that is broken.

Per-call costs of 7-16 ms with no work to justify them are the signature of a call that is
not a direct in-apartment dispatch: it is being marshalled and/or delayed. Two mechanisms fit
and both are testable with one more line of output (added):

1. The walk runs on a DIFFERENT thread/apartment from the one that owns the SCAPI objects, so
   every call is marshalled to the owning STA.
2. Same apartment, but the outgoing call is subject to erwin's OLE message filter, which can
   retry-with-delay while the application is "busy" - which is exactly what a modal dialog
   makes it.

`ScapiCalibration` now logs `thread=#N apartment=STA/MTA name=... isThreadPool=...` as its
first line in both contexts. If the two differ, it is (1) and the fix is to run the walk on
erwin's UI thread. If they match, it is (2).

### ROOT CAUSE (2026-07-27 22:28) - the walk ran in the WRONG COM APARTMENT

```
[SCAPI-CAL] context=connect (no modal loop)     thread=#2 apartment=STA name='(unnamed)' isThreadPool=False
[SCAPI-CAL] context=approval gate (modal loop)  thread=#8 apartment=MTA name='.NET TP...' isThreadPool=True
```

The DDL review dialog is shown and pumped on a **thread-pool MTA thread**, so `BtnSend_Click`
and everything it calls runs there. Every SCAPI call from an MTA thread to erwin's STA-owned
objects is marshalled through a proxy and serviced by the owning apartment - a direct dispatch
becomes a cross-apartment round trip. `.ObjectId` does no real work and shows the worst ratio
(over 400x), which is what proves it is apartment cost and not the properties being read.

Note `BtnSend_Click` calls the gate as its FIRST statement, before any `await`
(DdlApprovalDialog.cs:790-798), so the thread switch is NOT an async continuation - the whole
dialog lives on that thread.

## Fix: marshal the walk onto the owning STA

`ModelConfigForm.CheckApprovalBlockingRules` now runs `Evaluate` through
`Invoke` when `InvokeRequired`. Only the WALK is marshalled; the report dialog stays on the
calling thread because its owner window lives there. `ModelWalkGate.Enter` now logs
`thread #N APARTMENT` permanently, so a walk that lands in the wrong apartment again is one
line away from being noticed instead of invisible.

Expected: 467 s -> ~5 s (8,401 objects at the measured STA rate).

### CONFIRMED (2026-07-27 22:53) - 45 m 57 s -> 12.5 s

```
collected 8401 COLUMN object(s) in 4339 ms
rule#1081 ... 8401 applicable, 0 unreadable -> 0 violation(s) in 7617 ms
[WALK-GATE] done in 12475 ms - timer ticks resumed; 4 tick(s) suspended
```

| | first measurement | now | gain |
|---|---|---|---|
| Total | 2,756,771 ms | **12,475 ms** | **221x** |
| Collect | 179,403 ms | 4,339 ms | 41x |
| Evaluate | 287,667 ms | 7,617 ms | 38x |
| Per column | 328 ms | 1.49 ms | 220x |

In-walk calibration now reports STA rates: `.ObjectId` 7.268 -> **0.003** ms,
`Properties(...)` 11.120 -> **0.040** ms, enumerate 15.824 -> **0.536** ms/step.

The cost model closes exactly: 8,401 steps x 0.536 ms = 4,503 ms predicted collect vs 4,339 ms
measured. Nothing unexplained is left.

The `ModelWalkGate.Enter` scope was then moved INSIDE the marshalled delegate, because raised
around the `Invoke` it logged the caller's MTA thread while the walk ran on the STA - the exact
confusion that line exists to prevent.

- [x] Build 0 warnings / 0 errors. Tests 987/987 green.

### Remaining headroom (not needed, recorded for later)

- Evaluate is 7,617 ms for 8,401 named reads that calibrate at 0.040 ms (336 ms expected), so
  ~0.87 ms/object goes to `EvaluateRuleAgainstObject` and the engine, not to COM.
- Collect is now purely the per-attribute enumerator (fully accounted for). A single
  `Collect(root,"Attribute")` might enumerate cheaper than 286 per-entity collections.
- The label reads, the missing `ReleaseComObject` and the uncapped issue log are all still
  outstanding from the list below and are now worth far less than they were.

### Open, wider than this diff

- [ ] The whole review dialog is shown and pumped on a thread-pool **MTA** thread, so every
      OTHER SCAPI call it makes (Mart save, promotion save, version capture) pays the same
      cross-apartment cost this walk was paying. Audit those paths.
- [ ] Decide whether `ScapiCalibration` stays. Now that every shape is sub-millisecond the
      probe costs ~0.2 s at connect instead of ~15 s, so keeping it is nearly free and it is
      the only tool that can attribute a future slow walk. USER DECISION.

## Known remaining waste in the walk (verified by code read, NOT yet fixed)

Deliberately left out of this diff to keep it reviewable; all are rounding errors next to the
gate above, and should be revisited after the live retest confirms the new baseline.

- 2 of the 3 per-column property reads exist only to build the report label
  (`ReadDisplayName` at collect time, ApprovalBlockingRuleGate.cs:1012), although at most
  `MaxReportedIssues` labels can ever be shown. ~57% of the walk's intrinsic COM traffic.
- 287 `Collect` round trips (one per entity) where one `Collect(root, "Attribute")` would do -
  the fast path `ValidationCoordinatorService.cs:1450` already uses. Needs the per-entity path
  kept for PK-membership rules.
- Zero `Marshal.ReleaseComObject` in the whole file: 8,401 Attribute RCWs retained for the
  run, 16,802 transient Property RCWs dropped. Every sibling walk releases.
- `Finish` logs the full deduped issue set one `File.AppendAllText` per line (:1227), uncapped.
  Today's run had 0 violations; on a violating model this is the next multi-minute wall.
- REJECTED: moving the walk to a background thread. SCAPI is STA-bound; calls would marshal
  back to the owning STA, which must pump to service them, reopening the identical window and
  adding proxy/stub cost.

---

# WP 310 - datatype dialog appeared TWICE per new column - FIXED 2026-07-27

Report (WP 310 comment 2026-07-24, status "Test failed"): in Model Explorer a new column is
fine, but from the Column Properties / Column Editor screen the datatype picker comes back a
second time after the user already picked one.

Root cause, proved by the attached `erwin-addin-debug.log` (column MSL):
```
17:04:09  [DT-ENFORCE] DIM_DML.MSL: attempted='CHAR(18)' isNew=True allowed=False
17:04:09  AllowedDatatype: ... 'CHAR(18)' not in whitelist - forced to 'CLOB'
17:04:13  AllowedDatatype: ... user picked 'VARCHAR2(55 CHAR)'          <- picker #1 (enforcement)
17:04:14  Column Editor closed - final validation pass
17:04:15  [DT-ENFORCE] DIM_DML.MSL: attempted='VARCHAR2(55 CHAR)' isNew=True allowed=True
17:04:16  AllowedDatatype always-ask: ... kept 'VARCHAR2(55 CHAR)'      <- picker #2 (ALWAYS_ASK)
```
The `_alwaysAskPrompted` latch (WP 317 bug 2) was populated ONLY by
`PromptAlwaysAskDatatype` itself, so the NOT-ALLOWED enforcement picker never marked the
column. The editor-close pass then saw the freshly picked type as allowed AND still
`isNew=True`, and the ALWAYS_ASK confirm fired on top of the picker the user had just used.
(The memory claim that the post-`ProcessNewAttribute` baseline makes later passes
`isNew=false` does NOT hold on the Column Editor path - the heartbeat re-detects the new attr
and re-validates it as new.)

Fix: the latch now means "this column's datatype has ALREADY been put to the user this
session", renamed `_datatypePromptShown`, and is recorded by ALL THREE surfaces - the
ALWAYS_ASK confirm, the not-allowed enforcement picker (recorded right after Show, whether the
user picked or cancelled), and the term-mapping "datatype is fixed" warning (following that
message with a "choose the datatype" picker contradicted the message still on screen).
Verified there are exactly two `AllowedDatatypePickerForm.Show` sites and one
`PromptAlwaysAskDatatype` call site, so the set is complete. Both paths key on the identical
string (`attr.ObjectId.ToString()`; `CreateSnapshot` fills the snapshot's `ObjectId` from the
same expression, and it is the same key the already-working double-combo-commit dedupe uses).
Net behaviour: exactly ONE datatype dialog per new column, on every path. 979/979 tests green,
0 warnings. No unit test added - the latch lives inside the COM/SCAPI-driven enforcement
method with no seam; verification is the log trace above plus the call-site enumeration.

- [ ] LIVE retest (user): new column in Column Properties with a NOT-allowed default type -
      expect ONE picker; and in Model Explorer with an allowed type - expect ONE confirm.

---

# Bug batch WP 334 / 331 / 324 / 329 - CODE-DONE (awaiting live test), 2026-07-25

- [x] **WP 334** locked predefined column was deletable via "Discard New Column" on the
      Required Properties dialog. Root cause: the restore path
      (`RestoreDeletedLockedColumns`) needs the heartbeat's consecutive-snapshot diff, but a
      column created and discarded in the same breath was never in a prior snapshot AND
      `TryDeleteNewAttribute` drops its snapshot, so the locked-column protection was fully
      bypassed. Fix at the single delete primitive: `TryWarnLockedColumnDiscard` keeps the
      column and shows the SAME LockedColumnDialog the Column-tab delete shows. Covers every
      discard-of-new-column site; no delete-then-recreate (which would re-open the very
      Required prompt the user just cancelled). Conditional locks whose condition no longer
      holds still allow the discard (lock released by design).
- [x] **WP 331** DDL Generation tab kept the closed model's Source label / Target version /
      "Alter DDL: N lines" status after every model was closed. `ResetDdlTab()` added and
      called from `HandleSessionLost` (where General already reset); a later connect
      repopulates via `PopulateVersionCombos`.
- [x] **WP 324** the DDL Review ("Save Model") window got `ShowInTaskbar = true`. It is a
      modal on erwin's UI thread, so with no taskbar button it was reachable only via Alt+Tab
      and erwin looked frozen. A taskbar button also makes Windows auto-flash it when the
      foreground lock stops it surfacing. Deliberately NOT persistent TopMost: that could
      cover erwin's own "Save Models" dialog which the background teardown dismisses by
      mouse-sim.
- [x] **WP 329** add-in window now blocks erwin input while it is on screen (user
      clarification 2026-07-25: minimize the add-in to work in erwin).
      `Services/ErwinInputBlock.cs` = ref-counted block over
      `EnableWindow(erwinMainFrame, false)` - the same primitive WinForms already applies to
      erwin when the add-in opens a modal. Wiring: `SyncErwinInputBlock()` from
      Shown/VisibleChanged/Resize/Activated + `ReconnectTimer_Tick` (self-heal, runs in every
      state), release on FormClosing/FormClosed/HandleDestroyed.
      Two conflicts that HAD to be solved, not just the EnableWindow call:
        1. `Win32Helper.IsErwinMainWindowBlockedByModal()` is `!IsWindowEnabled(main)` and
           guards 6 tick/pipeline paths - our own block would have read as "erwin is modal"
           forever. It now subtracts `ErwinInputBlock.IsApplied`.
        2. The pipelines synthesize mouse input onto erwin windows BEHIND the add-in form
           (the `ToggleBusyOverlay` WS_EX_TRANSPARENT pass-through), and a disabled frame
           swallows synthetic input. `ShowBusyOverlay` therefore takes a `Suspend()` scope
           released on the overlay's `Disposed` - one choke point covering DDL, compare,
           From-DB, UDP sync and config reload. DebugMode + the wizard/Mart-Save gates also
           force release.
      Fail-safe direction is always "erwin usable"; `Sync` reconciles against the frame's
      REAL enabled state, which also repairs WinForms re-enabling it after an add-in modal.
- [x] FORCED promotion-approver refactor (was blocking the whole build; user chose "I adapt
      it", 2026-07-25). erwin-admin 1795ce3 deleted `EnvironmentRelationModelApprover`.
      Server semantics derived from the admin code, not guessed: PROMOTION and DDL rows share
      ONE per-model catalog `ModelPromotionApprover` keyed (CONFIG_ID, MART_PATH) with SEQ
      order and up to 3 backups per slot (slot satisfied by primary OR any backup), and
      `DdlApprovalService` treats an empty catalog as a HARD ERROR - "they are hard errors
      rather than a silent fall back to some other list" - so the per-transition list is gone
      from the promotion path (`ENVIRONMENT_RELATION_APPROVER` now serves FLOW='INTEGRATE'
      only). Add-in: `GetRelationApprovers(relationId, martPath)` ->
      `GetModelPromotionApprovers(configId, martPath)` DELEGATING to the server's own reader
      `ApprovalConfigService.GetModelPromotionApprovers` (in MetaShared, already referenced -
      so the add-in cannot drift from the web); `PromotionSendContext.ApproversByRelationId`
      dictionary -> single `Approvers` list (one read per context instead of one per route, and
      a send-time route re-derivation can no longer miss its entry);
      `LookupPromotionApprovers()` loses its relationId. Empty catalog still means
      auto-approve add-in side, which is exactly what keeps a Pending row from being inserted
      for a model the server would refuse to resolve a quorum for.
      `PromotionPlanner.ResolveEffectiveApprovers` is now unused but LEFT IN PLACE (project
      rule: ask before deleting working code); its tests still pass. DECISION NEEDED: remove it?
- [x] Build green, 0 warnings; **880/880 tests pass**.
- [x] Adversarial review (19 agents, 3 dimensions, 2 skeptics per critical/major, both must
      agree): 7 confirmed (#1 and #7 are the same defect found twice), 1 refuted, 1 unverified
      minor. SIX fixed, one deferred:
      * **CRITICAL - ErwinInputBlock adopted a disable it did not perform.** `Sync` set
        `_blockedHwnd` even when the frame was ALREADY disabled, which happens routinely: the
        user minimizes the add-in (the sanctioned way to use erwin), opens Mart Save / Properties
        / Print by hand, then clicks back to the add-in. We then claimed erwin's OWN modal
        disable, so `IsErwinMainWindowBlockedByModal()` reported "no modal" while one was up -
        blinding all six guards, including the two the repo treats as invariants (the verified
        2026-05-29 STA deadlock on `_scapi.PersistenceUnits` and the 2026-05-07 0xC0000005 in
        coreclr.dll) - and a later release re-enabled the frame UNDER a live MFC modal loop.
        Self-inflicting even with no user gesture, because the tick's Sync runs before the tick's
        own probe. FIX: ownership is claimed ONLY on a transition we performed; the probe now
        detects real modals with `GW_ENABLEDPOPUP` (an enabled owned popup, immune to our own
        EnableWindow); both release paths refuse to re-enable while such a popup exists.
      * **MAJOR - block target was not pinned to our own process.** `GetErwinMainWindow` is
        process-agnostic and Z-order dependent; with two erwin instances (the add-in is HKCU, so
        every instance loads it) we could disable ANOTHER instance's frame and then orphan it
        disabled forever when the resolved HWND changed. FIX: `ResolveOwnErwinFrame` pins to our
        own PID, and a changed/vanished HWND is re-enabled before the new one is claimed.
      * **MAJOR - a disabled frame can still be RAISED** (taskbar / Alt+Tab activation is not
        input), burying the only window that can release the block, with no dialog for Windows to
        flash: indistinguishable from the freeze this repo has burned days on. FIX: `OnDeactivate`
        brings the add-in back to front when the blocked frame takes the foreground (gated on
        `IsApplied`, so the pipelines are never fought).
      * **CRITICAL - the promotion GATE rule was stale.** The same admin commit that moved the
        approver source ALSO dropped per-transition `REQUIRES_APPROVAL` from the promotion path:
        server gates on the catalog alone (`PromotionEndpoints`: "per-transition RequiresApproval
        is no longer used for promotion", `approvalRequired = approverCount > 0`;
        `DdlApprovalService`: `requiresVote = slots.Count > 0`; the flag survives only for
        INTEGRATE - all verified first-hand). The add-in still ANDed the flag, so a model WITH
        alice+bob would AUTO-APPROVE a production promotion whenever the transition's flag was
        off - silent bypass, and the stale test asserted exactly that. FIXED + test rewritten.
      * **MAJOR - MODEL_ENVIRONMENT_VERSION write drifted.** Server's single writer advances the
        TARGET's own counter (`state.Version += 1`, "monotonic per-environment counter, decoupled
        from the source's version"); the add-in wrote the source Mart version, which 409s the web
        promote of the next hop and breaks the add-in's own rule-1 source match. FIXED.
      * MINOR - `ResetDdlTab` left the Target area fully blank when the closed model had a single
        version (`ApplyRightTargetSingleChoiceDisplay` had hidden the combo). FIXED.
      * REFUTED (both skeptics): "busy-overlay Disposed re-applies the block mid-pipeline".
- [ ] **DEFERRED, needs your call (finding #6, MAJOR, pre-existing - not caused by this batch):**
      the add-in's auto-approve branch never continues the promotion chain, while EVERY server
      finalisation pairs `UpsertModelEnvironmentVersion` with `ContinuePromotionChain`
      (DdlApprovalService :173+182 vote, :321+326 two-stage, PromotionEndpoints :272+285 web).
      So an add-in auto-approve on a Dev->Test->Prod topology lands only the first row: Prod never
      advances and, with TWO_STAGE_PROMOTION_ENABLED, the terminal DDL-only step is never
      enqueued, stalling the queue chain with no visible reason. The identical submission DOES
      advance when it is vote-gated (the server's decision path continues it). Fixing needs an
      architecture decision: `ContinuePromotionChain` is `MetaCore`, which ErwinAddIn does NOT
      reference - either add the reference, move the writer next to `ApprovalConfigService` in
      MetaShared, or have the add-in stop auto-approving locally and let the server finalise.
- [ ] LIVE test (user): 334 discard on a locked column; 331 close all models; 324 switch to
      Chrome during Generate DDL; 329 click erwin while the add-in is up, minimize, then run
      a full Generate DDL to confirm the block suspends and re-applies; promotion send on a
      model WITH an approver chain and one WITHOUT (auto-approve).

---

# WP 323: STRUCTURED Parametrization (add-in side) - CODE-DONE (awaiting live test), 2026-07-23

Spec: tasks/wp323-structured-parametrization.md (admin side + DB migration already live).

## Result
- [x] AllowedDatatypeService: `Structured` enum value + `StructuredPartMode` enum,
      7 new entry properties + GetSuffixValueList, SELECT lists (3 dialects),
      DBNull-safe reader (mode NULL -> NONE; out-of-int-range bound neutralized
      ROW-LOCALLY to null + log, never fail-open the whole whitelist),
      ParseParametrization STRUCTURED, DescribeEntry + public DescribeStructuredRules
      (also used by the ModelConfigForm load log).
- [x] New Services/StructuredParamParser.cs: paren-content grammar `p[,s][ suffix]`,
      Int32 overflow = parse failure. NOT the DataTypeParser paren-OUTSIDE suffix.
- [x] ValidateAgainstEntry STRUCTURED branch (single source, used by model
      validation AND the picker): bare via AllowNonParametrized; unparseable =
      generated message (REGEX_ERROR never used); p/scale bounds with one-sided
      texts; suffix mode + OrdinalIgnoreCase list match; every Invalid tagged with
      StructuredParamPart (Length/Scale/Suffix) for picker focus routing;
      OPTIONAL/REQUIRED suffix with EMPTY SUFFIX_VALUES = unenforceable rule ->
      accept + log on BOTH present- and absent-suffix paths.
- [x] GetFallbackDatatype synthesis: p=PARAM_MIN??1, ",SCALE_MIN??0" when scale
      REQUIRED, " first-suffix" when suffix REQUIRED; null seeds clamp to the
      opposite bound (SCALE_MAX=-1 -> seed -1) so the token always round-trips.
- [x] Picker: structured surface (Length/Scale boxes + Semantics combo) in one
      _paramRow container; OPTIONAL combo = empty first item (default), REQUIRED
      preselects only a single unambiguous value; lossless carry-over (raw text
      stashed on entering structured mode, restored verbatim unless the user
      edited the fields - "max"/"200 CHAR" survive browsing); deliberate OPTIONAL
      suffix composes alone so validation explains the incomplete combination;
      Esc/Enter guarded while a dropdown is open (fixes whole-dialog cancel, also
      pre-existing on the type combo); scale '-' filter is replacement-aware;
      error focus lands on the offending field via StructuredParamPart. Term-lock
      keeps the pinned single box. Compose unchanged.
- [x] Tests 851 -> 855, all green (parser grammar matrix, bounds/scale/suffix
      matrix, defensive empty-SUFFIX_VALUES, fallback round-trips incl. clamp,
      part tagging, picker composition).
- [x] Adversarial review workflow: 34 agents, 10 confirmed findings (1 major:
      dropdown Esc; 9 minor incl. 3 duplicates of the empty-SUFFIX_VALUES dead
      end), ALL fixed + re-verified by the new tests. Spec-compliance auditor: 0
      deviations.
- [ ] LIVE test (user): structured entry in the picker on a real model; DB read
      of the 7 new columns against a migrated MetaRepo.

---

# Version Promotion (Release Management) - Phase 2 CODE-DONE (awaiting live test), 2026-07-22

## Phase 2 result (2026-07-22, commits 1525ede + this one)
- [x] MartSaveAutomation.SaveWithDescriptionCaptureAsync: MartSaveOutcome(success,
      dialogSeen, capturedVersion); version read off the description dialog title.
- [x] PromotionPlanner: ParseVersionFromSaveDialogTitle (end-anchored),
      CandidatePlanningVersion (positive title-clean -> plan on CURRENT version,
      else fresh; SCAPI dirty probes are inert on r10.10).
- [x] PromotionFlow: IsPromotionEnabled gate, ResolveEffectiveLockType (default
      EXCLUSIVE), BuildSendContext (envs + relations + routes + preloaded
      per-relation approvers). PromotionSaveOutcome contract.
- [x] ModelConfigForm.SavePromotionModelWithDescription: clean model promotes the
      open version WITHOUT a save (second-hop scenario); dirty saves + captures
      the minted version (dialog title, then post-save window-title poll);
      unknown version = hard block, never guessed.
- [x] DdlApprovalDialog promotion mode (user-approved sketch): "Promote to" combo
      with COLOR_HEX dots above the Note row, derived "From" label (combo when
      rule-3 multi-candidate), "Approval required"/"Auto-approve" indicator,
      "Send to Approval" button; RunPromotionSendAsync = lock gate BEFORE save ->
      version-capturing save -> send-time route re-derivation -> approval
      decision -> single-transaction PROMOTION insert (+MEV upsert on auto) ->
      lock release on failed insert -> model close -> outcome modal.
- [x] ShowDdlForApproval wiring: enabled-but-unconfigured = warn + standard flow;
      context-build error = error + ask (continue standard / abort). Classic DDL
      branch untouched when promotion off.
- [x] Adversarial review round 2 (7 findings, 2 confirmed, both fixed):
      (1) clean-to-clean version drift behind the dialog now also triggers route
      re-derivation (stale rule-1 source was possible); (2) promotion combos
      freeze during the async send (mid-send selection could diverge from the
      submitted route).
- [x] 791/791 tests green; both flavors 0 warn / 0 err.
- [x] LIVE E2E round 1 (user, 2026-07-22, config 2012 CORE BANKING @ MetaRepoTmp):
      auto-approve happy path worked earlier on config 1012 (queue ID 7, log). Two
      findings reported; verified against live DB:
      - "Auto-approve shown despite approver" = NOT a code bug. config 2012 has
        ZERO approvers anywhere (ENVIRONMENT_RELATION_APPROVER empty in all 9
        MetaRepo DBs; APPROVAL_APPROVER only has rows for 1010/1012). Per spec an
        empty approver list auto-approves even with REQUIRES_APPROVAL=1. FIX:
        label now reads "Auto-approve (no approvers set)" when the flag is on but
        no approvers exist, so it does not read as a bug. To test the approver
        path, add a per-transition approver to config 2012.
      - "Unset lock type still errors with EXCLUSIVE" = real UX bug. PROMOTION_
        LOCK_TYPE unset -> built-in default EXCLUSIVE -> blocked every send. FIX:
        PromotionFlow.DecideLock - unset resolves to the strictest lock the build
        can ACTUALLY apply (UNLOCKED today, auto-upgrades to EXCLUSIVE when a
        lock-capable service ships); only an EXPLICIT non-UNLOCKED value blocks.
- [ ] LIVE E2E round 2 (user): re-test CORE BANKING (unset lock now proceeds
      UNLOCKED, auto-approve), then add an approver to a transition and confirm
      the approver path shows "Approval required" and inserts Pending.

Phase 3 (status watcher + missed-unlock recovery) and Phase 4 (General tab env x
version card) await user approval after the live test.

---

# Version Promotion (Release Management) - Phase 1 DONE (2026-07-22)

OpenProject WP: 322 (In progress). Decisions (user, 2026-07-22):
- D1: lock later ("tamam sonra") -> Phase 1 ships UnlockedOnly service; non-UNLOCKED
  effective PROMOTION_LOCK_TYPE throws before insert (no silent fallback).
- D2: Marts are Windows-auth -> Environment.UserName IS the Mart login (SUBMITTED_BY).
- D3: single row per send: REQUEST_TYPE='PROMOTION' replaces the DDL row for that send.
- D4: watcher cadence 30 s proposed (explained to user; Phase 3 topic, no objection yet).
- D5: pre-existing working-tree fixes committed (f144735, e5a70f9, 8c8423c).
- D6: no WP existed; Feature WP 322 created + started.

## Phase 1 result (2026-07-22)
- [x] Services/PromotionPlanner.cs: BuildRoutes (3-step source rule; first env NEVER a
      target), TargetsOf/RoutesForTarget, RequiresApprovalVote, SelectMissedUnlocks.
- [x] Services/MartVersionLockService.cs: PromotionLockType enum + exact codes +
      IMartVersionLockService + UnlockedOnlyMartVersionLockService (non-UNLOCKED throws).
- [x] Services/PromotionService.cs: EF via RepoDbContext; SubmitPromotion single
      transaction (insert + auto-approve MEV upsert), readers (approvers, env versions,
      pending promotions, own lock rows, reject reason), ComposeNote, ResolveSubmitter.
- [x] tests/PromotionPlannerTests.cs: 25 tests. Suite 776/776 green; both flavors 0/0.
- [x] Adversarial review (3 lenses + verifiers): 9 findings, 3 confirmed, both fixed:
      (1) first env offered as target -> forbidden MEV row (BuildRoutes now skips);
      (2) GetPendingPromotions equality on MODEL_LOCATOR = NCLOB on Oracle (ORA-00932)
      -> SQL filters bounded cols (CONFIG_ID/STATUS/REQUEST_TYPE), path match in memory.

---

# Version Promotion (Release Management) - PLAN (2026-07-22)

Authoritative spec: C:\Users\Kursat\Repos\erwin-admin\docs\erwin-addin-release-management-prompt.md
(admin web + live DB schema done 2026-07-21; schema/contracts are FIXED, do not change).
Companions: erwin-admin tasks/specs/release-management.md (6 signed-off decisions),
migrations/20260721_release_management_promotion.sql (exact DDL).

## Recon conclusions (2026-07-22, 9 parallel explore agents + critic)

- Queue write today: DdlApprovalService.Submit (Services/DdlApprovalService.cs:43), raw ADO,
  3 dialect INSERTs, exactly 10 columns, NO REQUEST_TYPE, no transaction, identity read-back OK.
  SUBMITTED_BY = Environment.UserName (Windows), NOT the Mart login the spec requires.
- MetaShared (project-referenced, erwin-addin.csproj:82) already ships everything schema-side:
  DdlApprovalQueue entity with REQUEST_TYPE/SOURCE_ENVIRONMENT_ID/TARGET_ENVIRONMENT_ID/
  MODEL_VERSION/LOCK_TYPE + RequestTypes.Promotion + Statuses constants; ModelEnvironmentVersion;
  EnvironmentDef; EnvironmentRelation; EnvironmentRelationApprover; RepoDbContext has DbSets for
  ALL of them (RepoDbContext.cs:30-89). EF route gives the auto-approve transaction for free.
- Canonical path: ConfigContextService.MartPath (ParseMartPath, ConfigContextService.cs:206) is
  byte-identical to MODEL_CONFIG_MAPPING.MART_PATH. Promotion MODEL_LOCATOR must use THIS,
  NOT _lastConnectedLocator (which is a mart:// locator; that stays for DDL rows only).
- Settings: GetEffectiveBool / GetEffectiveEnum (ConfigContextService.cs:411) already implement
  CONFIG_PROPERTY > CORPORATE_PROPERTY > builtin default. Keys + lock codes exist as constants in
  MetaCore/Constants.cs (VERSION_PROMOTION_ENABLED :37, PROMOTION_LOCK_TYPE :46,
  PromotionLockTypes :194-200).
- Env readers: IntegrationEnvironmentService.GetEnvironments/GetRelations exist (dialect ADO).
  ENVIRONMENT_RELATION_APPROVER and MODEL_ENVIRONMENT_VERSION have ZERO add-in references today.
- Version number: no C_Version symbol anywhere; version is parsed from the PU locator
  ([?&](VNO|version)=N, ExtractLocatorVersion ModelConfigForm.cs:909). The version being created
  by the Mart save is visible in the Description dialog title "Description for '<model>'
  Version <N>" which MartSaveAutomation already hooks (title prefix const :59). Capture it there.
- Poller template: SessionTrackingService (System.Timers.Timer, threadpool, DB-only,
  Interlocked tick guard, best-effort catch). Off-UI-thread, so no AlterWizardGate needed.
  Any UI-thread tick MUST start with `if (AlterWizardGate.IsOpen) return;` (black-rect rule,
  5 existing sites, commits 2bfb80d/e363f15).
- No startup reconciliation pattern exists anywhere; the missed-unlock recovery pass is net-new.
- Tests: xUnit + FluentAssertions, pure-extraction convention. Templates:
  IntegrationPlannerTests (derivation/approval), DdlWorkerConfigTests (startup decision),
  UdpSyncEngineDiffTests (flag outcomes).
- BLOCKER FINDING: SCAPI r10.10 has NO lock API. Proven twice: live ISCPersistenceUnit
  IDispatch dump (29 methods, none lock-related) + API-ref Disposition token list (no lock
  token; only open-time "read-only / ignore locks" requests). Applying Existence/Shared/
  Update/Exclusive locks needs either a UI-automation spike (Mart catalog lock UI RECON,
  fresh cmd-id capture) or deferral. Direct Mart-repo-DB lock writes: no schema documented,
  no connection exists, server holds lock state; treat as NOT an option.
- Interactive add-in has NO current-Mart-user reader (DdlWorkerConfig.UserName is the headless
  worker service account; DDL_GENERATION_CONF MART_USER is a shared credential).
- Working tree has uncommitted, live-verified changes (black-rect gate, ORA-01745 binds,
  ghost-PU gate, DDL tab Designer re-flow). New DDL-tab UI must build on the NEW coordinates.

## Open decisions - ASK USER, do not implement before answers

- [ ] D1 Mart lock mechanism. No SCAPI path exists. Options:
      (a) core feature first, lock as separate later phase; until then a non-UNLOCKED effective
          PROMOTION_LOCK_TYPE hard-blocks Send with a clear error (no silent fallback),
          deployments set PROMOTION_LOCK_TYPE=UNLOCKED to go live;
      (b) UI-automation spike FIRST (live RECON of erwin Mart catalog lock commands), then build;
      (c) direct Mart repo DB writes - recommend AGAINST (unproven, server-side lock state).
      Recommendation: (a) then (b) as its own phase.
- [ ] D2 SUBMITTED_BY = Mart login. Where does the interactive add-in get it?
      If the Marts are Windows-auth, Environment.UserName is correct as-is. If Server-auth,
      options: capture from the Mart Connect automation, or live RECON for a SCAPI/UI source.
      Which auth do the target deployments use?
- [ ] D3 UI anchor for "Send to Approval". Two candidates (lesson 2026-07-19: name both):
      the review dialog DdlApprovalDialog (recommended: add target-environment picker there,
      shown only when VERSION_PROMOTION_ENABLED) or a separate button on the Generate DDL tab.
      Also confirm: when promotion is enabled, does the send write ONE row
      (REQUEST_TYPE='PROMOTION', replacing the normal DDL row for that send) - my reading of
      the spec - or a PROMOTION row IN ADDITION to the normal DDL row?
- [ ] D4 Poll cadence. Spec says "same cadence as existing polling infra" but the two precedents
      differ (heartbeat 5 min vs DDL worker 2 s). Recommendation: 30 s threadpool DB-only
      watcher (SessionTrackingService clone), active only while this user has Pending
      promotion rows; terminal handling marshalled to UI thread via BeginInvoke.
- [ ] D5 Commit the current uncommitted working-tree changes first (they are live-verified)?
      Recommended yes, promotion work then starts from a clean tree.
- [ ] D6 OpenProject WP id for this feature (start_work_package needs it). Is there one?

## Phases (wait for approval between phases; each phase = small commits)

### Phase 1 - Data layer + pure decision logic + tests (no UI, no erwin)
- [ ] PromotionPlanner (new, pure static, Services/PromotionPlanner.cs):
      - ReachableTargets(envs, relations, envVersions, promotedVersion): targets that have a
        defined transition from at least one candidate source; no transition = not offered.
      - DeriveSource(envs, relations, envVersions, promotedVersion, targetId): spec 3-step rule
        (1 env holding VERSION with transition to target; 2 else first env by SORT_ORDER,
        first env never has a DB row; 3 multiple candidates -> return all, UI asks user).
      - DecideApproval(relation, approvers): RequiresApproval && approvers.Count > 0 -> Pending
        else AutoApprove. NO fallback to config APPROVAL_APPROVER list (deliberate).
      - SelectMissedUnlocks(rows, currentUser): SUBMITTED_BY == user (match rule per D2),
        REQUEST_TYPE == PROMOTION, LOCK_TYPE not UNLOCKED/null, STATUS terminal.
- [ ] PromotionService (new, Services/PromotionService.cs), EF via RepoDbContext:
      - Readers: relation approvers by relation ids; MODEL_ENVIRONMENT_VERSION by MART_PATH;
        pending PROMOTION rows by MART_PATH; reject reason from DDL_APPROVAL_VOTE by QUEUE_ID.
      - SubmitPromotion(...): one transaction. Insert DdlApprovalQueue row: REQUEST_TYPE =
        RequestTypes.Promotion, MODEL_LOCATOR = canonical MartPath, MODEL_NAME/DBMS_TYPE/
        DDL_TEXT/NOTE/CONFIG_ID/SOURCE_MODE filled exactly like DDL push, SOURCE/TARGET env ids,
        MODEL_VERSION, LOCK_TYPE code, STATUS + NOTE per approval decision
        ('Auto-approved (no approval required for this transition)' verbatim on auto path),
        SUBMITTED_AT = UtcNow. Auto path additionally upserts ModelEnvironmentVersion
        (target env, MART_PATH, VERSION, QUEUE_ID, PROMOTED_BY = SUBMITTED_BY, UtcNow) in the
        SAME transaction. Approver path NEVER touches MODEL_ENVIRONMENT_VERSION.
      - Existing DdlApprovalService raw-ADO path stays UNTOUCHED (spec: do not touch DDL push).
- [ ] IMartVersionLockService abstraction + UnlockedLockService implementation (v1):
      resolves effective PROMOTION_LOCK_TYPE; UNLOCKED -> no-op + LOCK_TYPE='UNLOCKED';
      any other code -> hard error dialog before insert (until D1 phase lands).
- [ ] Unit tests (templates per recon): ReachableTargets/DeriveSource matrix incl. multi-candidate,
      DecideApproval branches (REQUIRES_APPROVAL=1+empty approvers -> auto), SelectMissedUnlocks
      boundary cases, lock-code resolve/parse, canonical-path invariants.

### Phase 2 - Send to Approval flow (Generate DDL side)
- [ ] MartSaveAutomation: capture "Version <N>" from the Description dialog title during the
      existing save hook; surface it to the caller. Send hard-blocks when version unknown.
- [ ] Target picker UI per D3 (env names + COLOR_HEX swatch; source auto-derived, picker only
      when multiple candidates), gated by VERSION_PROMOTION_ENABLED + IsMartModel +
      ActiveConfigId > 0 (IsIntegrateEnabled pattern). AddinMessageDialog only; UI English.
- [ ] Wire submit: preconditions (spec Akis 1), lock step (v1 = UnlockedLockService),
      SubmitPromotion, user feedback incl. auto-approve outcome.
- [ ] DDLGENERATOR dedicated build: promotion UI + watcher fully excluded.

### Phase 3 - Status watcher + missed-unlock recovery
- [ ] PromotionStatusWatcher (SessionTrackingService clone: System.Timers.Timer, singleton,
      Interlocked tick guard, best-effort): polls own Pending PROMOTION rows; on Approved ->
      unlock (v1 no-op), notify, refresh General tab; on Rejected -> unlock, notify with
      DDL_APPROVAL_VOTE reason when available. Version row is written by the SERVER on the
      approver path; add-in never writes it there.
- [ ] Startup recovery: once per connect (InitializeModelServices seam), SelectMissedUnlocks
      over own rows -> release leftover locks (v1: log-only since UNLOCKED). Errors surface.
- [ ] Any UI-thread touch goes through BeginInvoke and respects AlterWizardGate rule.

### Phase 4 - General tab: environment x version card
- [ ] New "Environments" section card after Glossary card (CreateSectionCard/AddCardRow chrome,
      ListView Details idiom like listValidationResults; COLOR_HEX swatch via TryParseHex).
      Columns: Environment / Version / Promoted By / Promoted At. First env = "Current v<N>"
      (open model's latest saved version); others from MODEL_ENVIRONMENT_VERSION by MART_PATH,
      '-' when absent; "Pending v<N>" badge for in-flight promotion.
- [ ] footerY + form/tab height re-derived (Designer.cs:120-129 regression note!). Refresh from
      connect seam (:2153-2161), HandleModelChanged, after submit and on watcher terminal.
- [ ] Hidden entirely when VERSION_PROMOTION_ENABLED is off.

### Phase 5 - Mart lock (pending D1; separate phase, own RECON)
- [ ] Live RECON of erwin Mart lock surface (catalog/Open-from-Mart lock commands, cmd ids,
      dialog map) with the established WmCommandLogger/WinEvent patterns.
- [ ] Real IMartVersionLockService implementation + LOCK_TYPE wiring + unlock on terminal +
      startup recovery doing real unlocks. Re-run Phase 1 recovery tests against real codes.

### Phase 6 - Verification + docs
- [ ] Live E2E on MetaRepo test DB: auto-approve path (version row + note), approver path
      (Pending -> web approve -> watcher unlock + server-written version row), reject path
      (reason shown), restart-during-Pending (missed-unlock recovery), transition-less target
      blocked, VERSION_PROMOTION_ENABLED off -> zero UI.
- [ ] Both build flavors 0 warn / 0 err; full test suite green; docs/ARCHITECTURE.md + README
      updated; OpenProject WP comment (Turkish, short).

## Review
(to be filled after implementation)

---

# Value Template v2: UDP target + {Udp:...} source + pipe functions (2026-07-19) - DONE + LIVE-VERIFIED

## Live verification result (2026-07-19, MetaRepoTmp+Zeynep, config 1012)
- [x] Kural A (UDP target + funcs + related source): column 'Xyz' ->
      [TEMPLATE-APPLY] Attribute.Physical.TemplateTargetTest='TABL_xyz'.
- [x] Kural B ({Udp:Application|upper|left:3}): with Application model UDP set
      -> 'UYG'; with it empty -> [TEMPLATE-SKIP] (never-write-empty contract).
- [x] Applied at NAME-COMMIT moment (editor still open) - value visible without
      closing the Column Editor (user requirement).
- [x] CRASH FIX: writing at editor-CLOSE raced GDM teardown -> fatal AV in
      EM_GDM!GDMActionSummary::GraftPostState (dump erwin.exe.51960.dmp). Moved
      the apply into the pending-name drain (proven-safe editor-open window, same
      as required-UDP prompt); editor-close heartbeat stays as idempotent catch-up.
      Added [TEMPLATE-WRITE] pre-write marker so a future native death is traceable.
- [x] Seed rules + test UDP defs REMOVED from both DBs after the test.


## Request (user, 2026-07-19)
Admin side done, migration 9 live (verified on ALL 9 MetaRepo* DBs: TARGET_UDP_ID
column + CK_MC_NAMING_TARGET_XOR + MC_UDP_DEFINITION present). Extend the add-in
Template resolver:
1. New token source {Udp:Name} - read a UDP of the SAME object (name may contain ':').
2. Per-token pipe function chain, left to right: trim, upper, lower, left:n,
   right:n, substr:start:len, replace:a:b.
3. If rule has TARGET_UDP_ID -> write rendered value into that UDP instead of a
   property. XOR with PROPERTY_DEF_ID (DB CK enforces).
4. PRESERVE contract: FillMode (OnlyIfEmpty/Always), ApplyOn (Create/Update/Both),
   AND/OR condition gating, "error out, never write empty". Same pipeline, no
   separate "UDP formula" path.

## Current state (explored 2026-07-19)
- Grammar lives in pure NamingTemplateEngine.Render (Services/NamingTemplateEngine.cs:69).
  Sources today: {Prop} (no dot) + {Alias.Prop} (first dot). NO pipe, NO Udp:.
- Apply sites: ApplyColumnTemplateRules (ValidationCoordinatorService.cs:5427) and
  ApplyPrimaryKeyRules (:5679). Write = obj.Properties(rule.PropertyCode).Value.
  TABLE-object template rules have NO apply site today (unchanged by this work).
- Loader GetQuery/LoadStandards (NamingStandardService.cs:565/322) does NOT read
  TARGET_UDP_ID; rule model has no TargetUdp fields.
- UDP read for conditions: NamingValidationEngine.ReadUdpValue (:742) - owner class
  from objectType + "{Owner}.Physical.{name}" + Model.Physical fallback (private).
- UDP value write canonical: UdpRuntimeService.TrySetUdpProperty (:681) - set, on
  reject Properties.Add then retry. Currently private instance (uses no state).
- Live DB: MC_UDP_DEFINITION.OBJECT_TYPE in {MODEL, TABLE, COLUMN}. Rule 1167 =
  the only live Template (PK_{Table.Physical_Name}, PK, property target).

## Status (2026-07-19)
- [x] Steps 1-7 done: engine v2 + loader + apply sites + 38 new tests.
      736/736 green; both flavors 0 warn / 0 err. Loader SQL verbatim-verified
      on MetaRepoZeynep (rule 1176 resolves TARGET_UDP_NAME/OBJECT_TYPE; 1167
      untouched).
- [ ] Step 8 in-erwin part: seeded UDP 2040 'TemplateTargetTest' (COLUMN) +
      rule 1176 '{Udp:Application|upper|left:3}_{Physical_Name|lower}' ->
      TARGET_UDP_ID 2040, APPLY_ON=Create, Always, AUTO_APPLY=1, config 1012.
      erwin was RUNNING at install time - awaiting user OK to restart, then:
      new column in a 1012 model (e.g. SQL/1_DEV/EK_KART) -> expect
      [TEMPLATE-APPLY] ... Attribute.Physical.TemplateTargetTest='APP_<col>'.
      Cleanup after test: DELETE the two seeded rows (script in scratchpad).

## Plan
- [ ] 1. NamingTemplateEngine grammar v2 (pure, all unit-testable):
      token = SOURCE ("|" FUNC(":"ARG)*)*. Split inner token on '|': seg0=SOURCE,
      rest=funcs. SOURCE dispatch ORDER: "Udp:" prefix (OrdinalIgnoreCase) FIRST
      (rest = UDP name, may contain ':' and '.'), else first-dot Alias.Prop, else
      own Prop. New optional 4th delegate udpReader; {Udp:X} with null reader =
      TemplateResolutionException (no silent skip).
- [ ] 2. Function chain evaluator in the engine: 7 funcs, left to right.
      Malformed = TemplateResolutionException (unknown name, wrong arg count,
      non-int/negative n). After the full chain the FINAL value must be non-empty,
      else throw (extends the never-write-empty contract).
- [ ] 3. Self-ref guards pipe-aware: ReferencesOwnProperty must compare the SOURCE
      (strip |chain) - {Physical_Name|upper} targeting Physical_Name IS self-ref.
      New ReferencesOwnUdp(template, udpName) for UDP-target rules (runaway guard,
      same rationale as PK_ '+Always' runaway).
- [ ] 4. Rule model + loader: NamingStandardRule += TargetUdpId(int?),
      TargetUdpName, TargetUdpObjectType. All 3 SQL dialects: select
      ns.TARGET_UDP_ID + LEFT JOIN MC_UDP_DEFINITION tudp -> NAME/OBJECT_TYPE.
      Reader maps; both-set rows (CK-violating) skip+log like condition XOR skip.
      GetTemplateRules filter: ValueTemplate + (PropertyCode OR TargetUdpName).
- [ ] 5. Apply sites (Column + PK), shared flow unchanged (ApplyOn -> conditions ->
      self-ref -> Render -> FillMode -> idempotent -> AutoApply prompt -> write):
      if TargetUdpId set -> target path "{OwnerClass}.Physical.{TargetUdpName}",
      current-value read sparse-safe, write via TrySetUdpProperty (make it
      internal static in UdpRuntimeService - it uses no instance state - and
      reuse, no duplicate). Guard: TargetUdpObjectType must equal the rule's
      object type (COLUMN rule -> COLUMN UDP), mismatch = skip + [TEMPLATE-SKIP]
      log, never silent. udpReader delegate = public wrapper over
      NamingValidationEngine.ReadUdpValue (keeps Model.Physical fallback so
      {Udp:ApplicationCode} on a column reads the MODEL UDP).
- [ ] 6. Tests (NamingTemplateEngineTests + new): each func, chaining order,
      malformed funcs, empty-after-chain, {Udp:name-with-colon-and-dot}, no
      udpReader, back-compat (pipeless templates byte-identical), pipe-aware
      self-ref, loader filter. Full suite green.
- [ ] 7. Build both flavors 0 warn / 0 err.
- [ ] 8. LIVE verification on real model (build-and-run): (a) rule 1167 PK
      property template unchanged; (b) new COLUMN rule with TARGET_UDP_ID +
      {Udp:...} source + function chain writes expected UDP value; seed test rule
      in MetaRepoZeynep config 1012, then remove it.

## Assumptions (flagged for user)
- A1 upper/lower = ToUpperInvariant/ToLowerInvariant (NOT tr-TR; DB-identifier
  context, consistent with glossary CASE_INSENSITIVE=OrdinalIgnoreCase decision).
- A2 Func names case-insensitive; replace args used verbatim (no trim), 'a'
  non-empty, args cannot contain ':' or '|' (grammar separators); numeric args
  int >= 0.
- A3 left:n / right:n with n >= length -> whole string; substr start beyond end ->
  empty (then the final-empty error applies if nothing else remains).
- A4 UDP-target only meaningful for COLUMN/TABLE-object rules today (PK has no
  matching UDP OBJECT_TYPE); mismatch guarded+logged. TABLE template rules still
  have no apply site (pre-existing, out of scope).


# DDLGENERATOR build flavor: dedicated DDL-generation add-in (2026-07-11) - PLAN APPROVED 2026-07-12

## Phase 7 - Unattended robustness (2026-07-13, live-test findings)
- [x] Self-healing restart: Mart server enforces a ~4h ABSOLUTE session
      timeout (keep-alive ping can't extend it; an in-place drop -> "Access
      Denied" modal -> stalled worker -> erwin crash). DDL-generator now
      restarts erwin for a fresh session: PROACTIVE (session age >=
      MartSessionMaxAgeMinutes=210, idle only) + REACTIVE (keep-alive detects
      drop). MartMartAutomation.RequestErwinRestart: dismiss blocking modal ->
      WM_CLOSE main -> popup dismisser -> 20s force-kill fallback. Watcher
      relaunches. _martLoginTimeUtc stamped at login.
- [x] Startup popup auto-dismiss (blocks add-in load, must be handled BEFORE
      add-in loads -> WATCHER): license-expiry warning + Welcome/Start Page
      DISABLE erwin's main frame. Watcher.DismissBlockingStartupDialog
      (GetWindow(main,GW_ENABLEDPOPUP) -> WM_COMMAND IDOK) called each
      Wait-ForModel iteration. Add-in DismissBlockingStartupDialog is the
      post-load backstop.
- [x] Configuration Warning suppressed in DDLGENERATOR: a config-less model
      (the bootstrap) used to pop "Add-in loaded with controls disabled" modal
      (nobody clicks OK on the worker VM). `#if DDLGENERATOR` -> log + degrade
      silently, no modal.
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] OPEN: MartSessionMaxAgeMinutes hardcoded 210 - move to DDL_GENERATION_CONF
      if the server timeout differs per site. Confirm ~4h is the real timeout
      (single observation 2026-07-13: 18:26 login -> 22:26 drop).
- [ ] LIVE TEST: leave running >3.5h -> proactive restart before the 4h drop;
      + license popup path when license nears expiry.



## Requirements (user, 2026-07-11)
1. Compile-time flavor: built with a "DDLGenerator" flag the worker mode is
   ALWAYS on - the checkbox is removed.
2. The watcher for this flavor loads the add-in as soon as erwin runs (no
   model-open wait).
3. Auto Mart login on load: Mart tab > Connect; if Authentication shows
   "Server Authentication" fill User Name + Password (from DB), Windows auth
   fills nothing; click Connect; dismiss the optional "Mart Connected
   Successfully" OK box. Keep-alive: every N minutes (N from DB, default 5)
   Mart > Open then Cancel; last-activity timestamp also reset by a DDL job
   START; keep-alive must NEVER run while a DDL generation is active.
   Auth type + credentials + timeout interval all come from the admin DB.
4. UI: only the General tab visible, a "DDL Generation MODE ON!" banner, no
   other buttons (dev controls still visible in DEV builds).

## Phase 0 - Spikes (must close before coding; ~half day, on the worker VM)
- [x] S1 RESOLVED 2026-07-12 (live tests on the dev machine):
      (a) WM_COMMAND(1181) on a model-less erwin = NO-OP, even with the
          Welcome dialog closed (MFC UPDATE_COMMAND_UI disable confirmed).
      (b) NO startup-autoload registry value exists (Add-Ins\<name> has only
          Menu Identifier / ProgID / Invoke Method / Invoke EXE).
      (c) OPTION F PROVEN: start erwin WITH a bootstrap .erwin argument
          (copy of BlankTemplate.erwin) -> title 'erwin DM - ddlgen-bootstrap'
          -> post 1181 -> add-in LOADED (dev DB picker appeared = Execute ran).
      DECISION: DdlGeneratorMode watcher launches erwin with a bundled
      bootstrap model (installer ships it); existing wait-for-model + post
      flow stays unchanged; the DDLGENERATOR add-in closes the bootstrap
      model (discard) right after load, then logs into Mart. (Add-in
      surviving model-less is already proven in production logs.)
- [x] S2 RESOLVED 2026-07-12 (user RECON capture): Mart > Connect =
      WM_COMMAND 1059. New const CMD_MART_CONNECT = 1059.
- [x] S3 RESOLVED 2026-07-12 (Ctrl+Alt+D dump of 'Connect to Mart' #32770):
      Server Name  = Edit    id=1005
      Port         = Edit    id=1007
      Use SSL      = Button  id=35797 (checkbox)
      App Name     = Edit    id=1020 (disabled)
      Authentication = ComboBox id=1011 (text e.g. 'Server Authentication')
      User Name    = Edit    id=1012
      Password     = Edit    id=1013
      Recent Conns = SysListView32 id=1017
      Connect      = Button  id=1002   Cancel=2  Help=9
      Phase-4 automation drives these BY ID (GetDlgItem), not by text.
      Bonus finding: the bootstrap model opened READ-ONLY (title suffix
      '(Read-Only)') - ship the bootstrap .erwin with the read-only file
      attribute so its close can never raise a save prompt.

## Phase 0 status: COMPLETE (S1+S2+S3). Next: Phase 1 after user approval.

## Phase 1 - Build flavor - DONE 2026-07-12
- [x] csproj: `-p:DdlGenerator=true` adds `DDLGENERATOR` to DefineConstants
      (mirrors the PackagedBuild=true -> PACKAGED pattern; combinable with
      both PACKAGED and DEV).
- [x] IsDdlDedicatedInstance: compile-time (`#if DDLGENERATOR` true, else
      false). chkDdlWorker checkbox REMOVED everywhere (creation, reveal
      gesture, Designer field, CheckedChanged incl. the live-toggle re-init);
      HKCU DdlWorker\Enabled flag code deleted. Worker auto-starts from
      ModelConfigForm_Load via InitializeDdlWorker() (#if DDLGENERATOR only).
      Normal builds cannot ever start the worker (no caller of
      StartDdlWorker outside the flavor).
- [x] build-and-run.ps1 + package.ps1: -DdlGenerator switch -> passes
      -p:DdlGenerator=true (package keeps PackagedBuild=true too).
- [x] Single-worker mutex: Local\EliteSoft.ErwinAddIn.DdlWorker acquired in
      InitializeDdlWorker; not acquired -> LOUD log + red status, worker NOT
      started. AbandonedMutexException treated as acquired (prior owner died).
- [x] Verified: both flavors build 0 warn / 0 err; 605/605 tests; raw-byte
      string check proves the flavor-only code exists ONLY in the
      -p:DdlGenerator=true DLL.

## Phase 2 - UI restriction (DDLGENERATOR only) - DONE 2026-07-12
- [x] ApplyDdlGeneratorUiRestrictions (DdlWorker partial, ctor after
      InitializeGeneralTab): tabValidation/tabTableProcesses/tabDdlGeneration
      REMOVED from the TabControl (not disposed - the worker pipeline drives
      the DDL tab's controls programmatically; tabIntegrate never appears in
      DDL-only mode, Debug Log tab was already retired).
- [x] Red banner "DDL Generation MODE ON!" top-right of the General header
      (x=360, clear of title and cards); subtitle text swapped to "Dedicated
      DDL generation instance..."; form title suffix " - DDL Generator".
- [x] Buttons hidden in flavor: General-tab "Close erwin" + the bottom
      status-bar Close (either would kill the worker with one click).
      `#if DEV` controls (Change DB / Reload Config, RECON hotkeys) untouched.
- [x] build-and-run-ddlgenerator.ps1 wrapper added (user request): calls
      build-and-run.ps1 -DdlGenerator (same-CLSID replace warning in header).
- [x] Both flavors 0 warn / 0 err; 605/605 tests.
      NOTE: banner placement needs one live visual check on the next
      -DdlGenerator dev install (absolute coords; expected clear, unverified).

## Phase 3 - Worker config table + service - DONE 2026-07-12 (CORRECTED to real schema)
- CORRECTION: DDL_GENERATION_CONF is an EXISTING admin-system table, not one we
  create. My initial CREATE-TABLE script was wrong (USERNAME/PASSWORD/IS_ACTIVE)
  and was DELETED. Real schema (live DB MetaRepoZeynep): ID, CORPORATE_ID,
  API_KEY_HASH, MART_USER, MART_PASSWORD (encrypted), UPDATED_AT, MART_SERVER,
  MART_PORT, MART_USE_SSL (bit), MART_AUTH_TYPE (default 'SERVER'),
  + KEEPALIVE_MINUTES (int NULL) - admin added this column 2026-07-12.
- Decisions: row selection = the single row (zero->disabled, 2+->ambiguous
  refuse); keep-alive minutes = the new KEEPALIVE_MINUTES column.
- [x] DdlWorkerConfigService.ReadActiveConfig: real columns; single-row contract
      (reads first row, then detects a second -> null+loud log); decrypts
      MART_USER/MART_PASSWORD (Server auth) via DecryptConnectionSecret, decrypt
      failure/echo -> null (no silent fallback). Windows auth skips creds.
      Reads MART_USE_SSL + CORPORATE_ID (logged).
- [x] DdlWorkerConfig POCO: + UseSsl, + CorporateId. ParseAuthType,
      NormalizeKeepAliveMinutes, IsKeepAliveDue unchanged.
- [x] 24 unit tests (DdlWorkerConfigTests, pure logic) - 629/629 green; both
      flavors 0 warn / 0 err. Live DB column verified via sqlcmd.

## Phase 4 - Mart auto-login automation - CODE DONE 2026-07-12 (needs live test)
- [x] MartMartAutomation.ConnectToMart(cfg, log) - all pure Win32:
      1. Post Mart>Connect (WM_COMMAND 1059) to XTPMainFrame; wait for the
         "Connect to Mart" #32770 dialog (10s). No dialog -> ProbeMartConnected
         (Mart>Open: picker => AlreadyConnected, "Connect to Mart" => not).
      2. EnsureAuthCombo: align combo 1011 to cfg.AuthType (CB_SELECTSTRING +
         CBN_SELCHANGE) so credential fields enable/disable right. Optional
         server/port -> ids 1005/1007.
      3. SERVER -> WM_SETTEXT user (1012) + pass (1013); WINDOWS -> nothing.
         Click Connect (1002; text match + id fallback).
      4. WaitForLoginOutcome (25s): Connect dialog closes = LoggedIn; success
         "erwin Data Modeler" box OK'd (OK only, never the checkbox); error box
         while dialog open = Failed; timeout = Failed + Cancel the dialog.
- [x] Worker gating: DdlWorkerTryStartNextJob has a `#if DDLGENERATOR` login
      gate - no job claim until _martLoginVerified. EnsureMartLogin (non-
      blocking): reads DDL_GENERATION_CONF once (60s backoff on
      missing/undecryptable), runs ConnectToMart on a background Task (25s+
      dialog waits must not freeze erwin UI), marshals result to
      OnMartLoginComplete (verified+stamp _lastMartActivityUtc | 60s retry).
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] LIVE TEST (next): assumptions to confirm on the worker erwin -
      (a) Mart>Connect (1059) works model-less;
      (b) the success box title is "erwin Data Modeler" / text contains
          "Connected"/"Successfully" (WaitForLoginOutcome keys on that);
      (c) the auth combo items start with "Server"/"Windows".
      All are logged verbatim ([MART-LOGIN] ...) so one run captures any drift.

## Phase 5 - Keep-alive ping - CODE DONE 2026-07-12 (needs live test)
- [x] _lastMartActivityUtc stamped on: login success, ping success, JOB
      COMPLETION (both OnDdlWorkerCloseComplete paths, `#if DDLGENERATOR`).
- [x] MartMartAutomation.PingMartSession = ProbeMartConnected (Mart>Open ->
      picker=alive+IDCANCEL / "Connect to Mart"=dropped+IDCANCEL), shared with
      the login probe via a `tag` param ([MART-KEEPALIVE] prefix).
- [x] Worker tick gate (after login-verified, before claim):
      MaybeStartKeepAlivePing - IsKeepAliveDue(_lastMartActivityUtc, now,
      _keepAliveMinutes, busy, pingActive); busy = _ddlQueueActive ||
      _martMartPipelineActive || _currentDdlJob!=null (defensive; tick already
      returned on the first two). Ping runs on a background Task (dialog waits
      must not freeze UI); returns true so no claim while pinging.
- [x] Live-refresh: the ping task re-reads DDL_GENERATION_CONF for the current
      KEEPALIVE_MINUTES (admin edit takes effect within one interval); login
      also seeds it. OnKeepAlivePingComplete: alive -> stamp; dropped ->
      _martLoginVerified=false + immediate re-login (login gate drives
      Mart>Connect again).
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] LIVE TEST: with KEEPALIVE_MINUTES=1, leave the worker idle > 1 min and
      confirm [MART-KEEPALIVE] due -> ping OK cycle; then confirm a job resets
      the clock (no ping right after a job).

## Phase 6 - Watcher + bootstrap auto-load - DEV DONE 2026-07-12 (prod installer TODO)
- [x] installer/assets/ddlgen-bootstrap.erwin (copy of BlankTemplate, git-tracked).
- [x] autostart-watcher.ps1: reads HKCU DdlGeneratorMode/BootstrapModelPath/
      ErwinExePath; in DDL-gen mode, when erwin is NOT running it LAUNCHES
      erwin itself with the bootstrap (Start-ErwinWithBootstrap, Resolve-ErwinExe
      known-paths fallback), then the existing Wait-ForModel + post flow runs
      untouched. Non-DDL builds unaffected (mode flag = 0).
- [x] Add-in (DDLGENERATOR): MartMartAutomation.CloseBootstrapModelIfActive
      (title marker "ddlgen-bootstrap" -> WM_CLOSE active MDI child, read-only
      so no save prompt). Worker tick bootstrap gate runs BEFORE the login
      gate; one-shot (_bootstrapHandled). Never touches a non-bootstrap model.
- [x] build-and-run.ps1 -DdlGenerator: copies bootstrap to installDir (read-
      only), writes the 3 HKCU values; normal build clears DdlGeneratorMode=0.
- [x] Both flavors 0 warn / 0 err; watcher + build-and-run parse clean.
- [ ] PROD installer (install-impl.ps1 / package.ps1 -DdlGenerator): copy
      bootstrap + write HKCU flags (same as build-and-run). NOT done yet -
      dev flow (build-and-run-ddlgenerator.ps1) covers testing first.
- [ ] LIVE TEST: run build-and-run-ddlgenerator.ps1, CLOSE erwin, watch the
      watcher launch erwin+bootstrap -> add-in loads -> [DDL-BOOTSTRAP] closes
      it -> [MART-LOGIN] -> job. autostart.log + erwin-addin-debug.log.

## Phase 7 - End-to-end verification + docs
- [ ] Fresh logon -> watcher -> erwin (no model) -> add-in loads -> auto
      login -> job -> no-diff job -> 5-min keep-alive observed -> second job.
      Log markers: [DDL-ONLY], [MART-LOGIN], [MART-KEEPALIVE], [FORM].
- [ ] README + docs/ARCHITECTURE.md + memory update.

## Decisions (user, 2026-07-12)
1. Credentials: stored in DDL_GENERATION_CONF encrypted the same way the
   glossary CONNECTION_DEF credentials are (DecryptConnectionSecret,
   erwin-admin writes them).
2. Normal interactive builds lose the worker entirely (checkbox removed;
   worker exists only in the DDLGENERATOR flavor). CONFIRMED.
3. Keep-alive stamp at job END. CONFIRMED.
4. Spikes (Phase 0) are the first implementation step. CONFIRMED.
   S1 outcome still unknown (model-less load) - riskiest item; plan assumes
   one of the two paths (WM_COMMAND post OR erwin startup-autoload) works.

---

# DDL-dedicated instance mode + form hide during automation (2026-07-11 round 3) - DONE

## Findings (job-6 retest + manual CC hang, log 20:17-20:41)
- Job 6 SUCCEEDED end-to-end: empty-RD no-diff detected, row DONE (ddlLen=80 note),
  quiesced close worked first try (Mart Offline dismissed, HandleSessionLost reset).
- User then MANUALLY opened v2+v1 and launched Complete Compare: the add-in had
  adopted BOTH models (full validation init) and UDP-synced BOTH (creates=6,
  updates=2 each - both dirtied), holding a live session on v1. The manual
  compare stuck at "Comparing / Processing Left Model"; during the hang the
  add-in was idle (timers modal-guarded; only DB-only glossary refresh ran).
  Add-in's background interference (dirty writes + open session + walks) is the
  only delta vs vanilla erwin.

## Plan
- [x] A: DDL-only mode: when chkDdlWorker is ON the instance is DDL-dedicated:
      InitializeValidationService skips glossary/naming/predefined/dependency/
      UDP sync/UDP runtime/monitors/validate-tab; keeps ConfigContext + DBMS
      mismatch guard + General tab + PopulateVersionCombos (DDL gates + combo).
      IsDdlDedicatedInstance predicate lives in the DdlWorker partial.
- [x] B: glossary auto-refresh tick no-ops in DDL-only mode.
- [x] C: checkbox live-toggle re-runs InitializeValidationService(closeConfigLess
      MartModel:false) so the mode applies without restart.
- [x] D: HideFormForAutomation/RestoreFormAfterAutomation: manual pipeline hides
      at start, restores at tail (only when _ddlWorkerState==Idle); worker jobs
      hide at claim and restore in OnDdlWorkerCloseComplete (success + give-up),
      so the Save-Models checkbox mouse-sim can never land on add-in UI.
- [x] E: build 0 warn/0 err + 605 tests green.

## Review
- Manual-CC-hang root: add-in was IDLE during the hang (timers modal-guarded);
  delta vs vanilla erwin = UDP-sync dirty writes on BOTH manually opened models
  + live session + walks. DDL-only mode removes all of it on the worker
  instance. If the hang reproduces with the add-in quiet, it is native erwin
  behavior (test with worker checkbox OFF + fresh erwin to isolate).
- Worker still needs: ConfigContext (job gates), DBMS-mismatch guard,
  PopulateVersionCombos (right-version combo), reconnect tick (adoption).

---

# Auto-DDL worker: no-diff compare freezes erwin (job 4 incident, 2026-07-11) - DONE

## Root cause (from %TEMP%\erwin-addin-debug.log 17:34-17:43)
1. Job 4 (v2 vs v1) had NO differences. After CC_COMPARE the pipeline only waits
   1.5s for a popup then 10s for Resolve Differences / Type Resolution. The no-diff
   outcome (erwin info box arriving AFTER the compare finishes, or RD simply never
   opening) is not watched, so the run dies with the generic "did not reach Resolve
   Differences" FAILED.
2. Teardown posts IDCANCEL to the CC wizard and NEVER verifies it closed. It did not
   close: POST-CLOSE diag still shows the ;Duplicate=YES PU; user screenshot shows the
   wizard alive on the Right Model page.
3. Worker cleanup: dirty v2 model cannot close while the CC wizard holds the
   ;Duplicate PU, so CloseActiveMartModelDiscardingChanges returns false and
   OnDdlWorkerCloseComplete retries FOREVER every ~15s (WM_CLOSE + Save-Models sweep +
   ForceForeground each pass): erwin unusable = the reported freeze.
   Side finding: a SECOND erwin instance (PID 65360) ran the worker simultaneously
   (claimed job 2 mid-flight) - HKCU flag is per-user, both processes saw enabled=True.

## Plan
- [x] A: CCSession.CompareNoDifferences flag + IsNoDifferenceInfoText (pure, testable)
      + combined post-Compare watcher (RD | Type Resolution | info box) in
      DriveCompareToResolveDifferences only.
- [x] B: CloseCcWizardVerified escalation (IDCANCEL, verify, dismiss blocking
      child dialog by OK, IDCANCEL again, CC_CLOSE, verify; loud logs). Used on the
      no-diff exit AND in CloseReviewSession instead of the blind IDCANCEL+Sleep(800).
- [x] C: ModelConfigForm cross-version branch: CompareNoDifferences means script=""
      (NOT an error): interactive shows info status, queue writes DONE with the
      explicit note "-- No differences detected between the compared versions; no
      alter DDL required." (upgraded from the silent empty-DDL contract).
- [x] D: DdlWorker cleanup retry CAP (4 attempts): then loud log + Idle +
      DdlWorkerActiveUnattended=false; worker stops hammering, resumes when the
      operator closes the model (Idle guard already waits for model-less).
- [x] E: unit tests for IsNoDifferenceInfoText (19 cases).
- [x] F: build clean (main project 0 warnings / 0 errors) + full test run 605/605.

## Round 2 (2026-07-11 evening, job-5 retest findings)
erwin does NOT show an info box for identical versions: it OPENS Resolve
Differences with an EMPTY diff grid (job-5 log: no "listview ready (items=N)"
line = count stayed 0 for the whole poll; the arrow click on blank canvas can
never fire an EDR tx -> old error "Apply-to-Right did not register (no EDR tx)").
Also the worker's model close aborted after every Save-Models discard (Mart
Offline never raised) because the close ran with the reconnect tick + validation
walks resumed and the add-in's SCAPI session still open on the job model; the
pipeline's v1 child (no session, monitoring suspended) closed clean seconds
earlier.
- [x] ApplyToRightOutcome enum (Applied | NoDifferences | Failed) in the shared
      ApplyToRightArrowAndWaitForRas: empty grid confirmed with a +3.5s
      count-only watch -> NoDifferences (both Review and From-DB pipelines).
- [x] Review caller: NoDifferences -> script="" + precise status; queue row goes
      DONE with EMPTY RESULT_DDL (no placeholder note - misleading; user
      2026-07-11). From-DB caller: returns (empty, null) -> informational dbMode
      status.
- [x] Defensive tail branch narrowed to script==null so the explicit "" reaches
      the informational no-diff status label.
- [x] Worker cleanup QUIESCE before WM_CLOSE: StopReconnectTimer +
      SuspendValidation + StopMonitoring x2 + CloseCurrentSession; on success
      AND give-up -> HandleSessionLost() (canonical disconnected reset; without
      it the tick's count==0 early-return + suspended monitoring would leave
      _isConnected latched and the worker stuck).
- [x] Build 0 warn / 0 err; tests 605/605.

## Review (2026-07-11)
- New file Services/CcCompareOutcome.cs (public static, pure text classifier,
  #nullable enable) + tests/CcCompareOutcomeTests.cs.
- MartMartAutomation: watcher only ACTS on "erwin Data Modeler"-titled message
  boxes (same family the old 1.5s popup guard targeted); any other new dialog
  (e.g. compare progress meter) is logged once and left alone - zero new risk to
  healthy compares. Unknown erwin boxes keep the old No/Cancel dismiss + abort.
- CloseCcWizardVerified deliberately has NO ForceDestroy (CC engine corruption,
  see reference_cross_version_orphan_unsolved) - worst case it reports loudly and
  the worker's bounded cleanup keeps erwin usable.
- Freeze is eliminated in EVERY branch: even if erwin's no-diff wording is not in
  the classifier, the box is logged verbatim (ground truth for extending the
  list), the run fails explicitly, the wizard close is verified, and the cleanup
  loop is capped at 4 attempts (~1 min) instead of forever.
- NOT changed: dual-erwin-instance worker guard (see Deferred).

## Deferred (noted for user)
- Single-worker mutex per logon session (two erwin processes both ran the worker).
- Exact no-diff info-box title/text ground truth: watcher logs FULL title+text of any
  unexpected dialog so the next live run captures it even if classification misses.

---

# Manual-rename revalidation + Properties-pane dropdown coverage (2026-07-10) - DONE

## From live test of the A-F fixes
- test 1 (picker idle -> uniquify -> rule fires): OK
- test 2 (dialogs show live name): OK
- test 3 (Model Explorer rename existing column to a digit -> rule should fire): FAILED -> fixed
- limitation 1a (Properties-pane dropdown datatype edit unobserved): user chose selection-scoped
  fingerprint -> implemented

## Fixes
- [x] Bug-3 (columns): SPLIT `treatAsNew` into `revalidateAsNew` (validation scope: apply=Create
      rules fire on ANY real rename) vs `treatAsNew` (identity: Cancel deletes-vs-reverts). Manual
      rename now fires rule#1127 but Cancel REVERTS (does not delete the pre-existing column).
      Trigger = NamingValidationEngine.RenameRequiresRevalidation (pure, tested).
- [x] Table + View: same split in the shared TableTypeMonitorService.ValidateNamingStandard
      (revalidateAsNew param + internal RenameRequiresRevalidation detection); threaded a revalidate
      bool through the table heartbeat; view rename site passes it directly. Cancel still reverts,
      never deletes the table/view. (User earlier: "diger objelerde de (Tablo,View) vardir".)
- [x] Task (a): SelectionScopedAttributeCheck - Overview-pane selected entity fingerprinted each
      heartbeat (editor-closed), handle-cached + backoff so no per-second full child enum. Catches
      Properties-pane dropdown datatype/name edits on existing columns.
- [x] Task (a) HARDENING (user: "bazen kaciyor" - edit not caught first time, caught on re-select):
      Overview tracks DIAGRAM selection but a Model-Explorer TREE column selection does not sync, so
      the edited entity was never fingerprinted until re-selected. Fix = fingerprint selected entity
      PLUS a bounded round-robin slice (RollingRescanPerHeartbeat=3) of the baselined working set, so
      the edited table is re-checked within a few seconds regardless of the Overview, no re-select
      needed. Bounded (touched entities only), no spurious popup (stable entity short-circuits).
- [x] Tests: RenameRevalidationTests, OverviewSelectionParseTests. DEV 0/0, packaged 0/0, 571 green.

## Verification (user live)
- [ ] test 3 re-run: rename existing column to a digit name -> rule#1127 fires with LIVE name; Cancel reverts (does NOT delete the column)
- [ ] table/view manual rename to a digit -> naming rule fires; Cancel reverts (does NOT delete)
- [ ] Properties-pane datatype dropdown change on existing column -> caught in ~1-2s ([SEL-SCOPE] + rule)
- [ ] confirm the manual-rename prefix/suffix RE-APPLY side effect is acceptable (else narrow revalidateAsNew to validation-only)

## Still open (declined / caveat)
- Definition/Comment-only Properties-pane edits: no observer (user declined).
- [SEL-SCOPE] relies on Overview reflecting the selection; verify with the log line.

---

# Model Explorer / modal-race validation gaps (2026-07-09) - DONE (A-F approved + implemented)

## Bugs (user report + log/code verified)
- bug-1: erwin auto-uniquify rename (Pre_Abc -> Pre_Abc__1070/__1073) committed WHILE a modal
  was pumping is never validated: rule#1127 (Regexp) bypass. Log never contains the new name.
- bug-2: dialogs print stale names: picker said 'TEST.Pre_Abc' while live was Pre_Abc__1070;
  "Naming standard applied" said 'Abc' -> 'Pre_Abc' while live was Pre_Abc__1073.
- structural: right-dock Properties pane edits on EXISTING objects have no watcher at all.

## Root causes
1. `_datatypePickerShowing` gates BOTH timers for the whole picker modal (57 s in the repro);
   the uniquify commit lands mid-modal, no detector sees it.
2. Post-modal only Physical_Data_Type is live re-read (VCS ~:7134); curr.PhysicalName stays
   stale; the isNew replay validates the STALE name; IsAutoUniquifyRename compares stale-vs-stale
   (baseline snapshot and state are the SAME object/value) so it can never fire here.
3. After the gesture (pending-new consumed), no detector ever rescans that attribute:
   heartbeat is count-only (rename = no delta), ScanForRenamesEventDriven walks ENTITIES only.
   The snapshot-vs-live diff sits unread forever.
4. Dialog texts are built from pre-captured state: picker msg (~:7070), "Naming standard
   applied" (~:7528), required-field fieldLabel built once outside the re-prompt loop (~:7671).

## Fix plan
- [x] A. Helper ReadLivePhysicalName(attr, fallback) + RefreshNameAfterModal(attr, state, ctx):
      live re-read + sync (placeholder-safe), mirroring the live datatype re-read discipline.
- [x] B. Post-modal rename catch in EnforceAllowedDatatypeWhitelist: entry refresh + refresh after
      picker/warn-only/term dialogs; replay condition isNew || renameCaught (Core's
      IsAutoUniquifyRename baseline bridge decides Create-vs-Update for !isNew).
- [x] C. _attrRecheckQueue + ScheduleAttributeRecheck + DrainAttributeRecheckQueue (MonitorTimer):
      targeted live-vs-snapshot re-diff per attr ObjectId, routed through ProcessAttributeChanges.
      Scheduled at Enforce exits, Core Step1/2 name writes, snapshot-advance sites, inline-edit close.
- [x] D. Dialogs resolve LIVE name: picker path via entry refresh; "Naming standard applied"
      re-reads before AND after the modal (Steps 2/3 continue with the live '__NNNN' name);
      required-field label rebuilt per pass; Naming/Domain queue entries carry Attribute/ObjectId,
      ShowConsolidatedPopup prints LiveColumnNameFor.
- [x] E. Gate consistency: _datatypePickerShowing -> _validationModalShowing (+ShowValidationModal
      wrapping warn-only + term dialogs); WindowMonitorTimer bails on _isProcessingChange/_isCheckingForChanges.
- [x] F. Properties-pane / Model Explorer F2 coverage: Win32Helper.GetFocusedInlineEditText reads
      the in-place editor's initial text on the OPEN edge; SelectInlineEditCandidates (pure, cap 8,
      names before types, overflow logged) matches snapshots; close edge schedules rechecks.
      KNOWN GAP: pane datatype edits via dropdown-only and Definition/Comment-only pane edits.

## Verification
- [x] Unit tests: InlineEditCandidateTests (5) - 549/549 total green
- [x] Builds: DEV 0/0, PackagedBuild 0/0
- [ ] Live repro (user): idle >30 s in picker until uniquify lands, confirm rule#1127 fires post-pick
- [ ] Live repro (user): dialog texts show the live (uniquified) name
- [ ] Live repro (user): Properties pane rename of existing column with digits triggers rules

---

# "Integrate" tab - environment promotion front-end (2026-06-22)

Read-only runtime consumer of the admin-side Integrate feature. The user, with a
Mart model open, sees which deployment environment the model is in (derived from
the Mart folder) and can promote it forward/back per the admin definitions. This
iteration: tab visibility + current-env detection + targets UI + a Merge SEAM
(placeholder, no destructive run).

## ADIM 0 findings (confirmed)
- UI: ModelConfigForm is a floating WinForms Form with a `tabControl` (4 TabPages,
  declared in ModelConfigForm.Designer.cs). New tab = Designer shell + runtime fill
  in ConnectToModel. Theme colors/fonts in Designer InitializeComponent; CreateInfoCard
  + AddinMessageDialog for styling; pnlValidationToolbar for a horizontal row.
- Open model Mart path: ConfigContextService.Instance.MartPath (e.g.
  "Kursat/MetaRepo/Dev/SalesModel"). Current env = parent folder = second-to-last segment.
- Mart access: 100% SCAPI / in-process + Win32 WM_COMMAND. No REST. CONFIRMED.
- Merge: no existing infra; native commands dispatched via PostMessage WM_COMMAND.
  Merge cmd id unknown (later WmCommandLogger discovery). This iteration = SEAM + log only.
- Repo DB: INTEGRATE_ENABLED via ConfigContextService.GetEffectiveBool (already does the
  CONFIG_PROPERTY -> CORPORATE_PROPERTY -> false cascade, admin-identical). ENVIRONMENT /
  ENVIRONMENT_RELATION have NO EF entity (RepoDbContext is admin's, out of repo) -> read via
  raw ADO.NET dialect-aware (DatabaseService.CreateConnection/CreateCommand + SqlDialect.Param),
  mirroring LookupConfigId. No admin changes, no EF-version risk.

## User decisions (2026-06-22)
1. Tab hidden entirely when INTEGRATE_ENABLED off or model has no config (TabPage removed).
2. Not-in-managed-environment: single-line text "This model is not in a managed environment."
3. SCAPI / in-process + Win32 confirmed (no REST).

## Design (SOLID separation)
- Services/IntegrationEnvironmentService.cs: DTOs + DB reads (raw ADO, dialect-aware).
  - record IntegrationEnvironment(Id, ConfigId, Name, SortOrder, Description, ColorHex)
  - record IntegrationRelation(Id, ConfigId, FromEnvironmentId, ToEnvironmentId, RequiresApproval)
  - GetEnvironments(configId)            ORDER BY SORT_ORDER
  - GetRelationsFrom(configId, fromId)   WHERE CONFIG_ID=.. AND FROM_ENVIRONMENT_ID=..
  - No error swallowing: exceptions propagate, UI boundary shows them.
- Services/IntegrationPlanner.cs: PURE logic (DB-free, unit-tested).
  - ParseParentFolder(martPath) -> parent segment or null
  - ResolveCurrentEnvironment(martPath, environments) -> env or null (NAME match, OrdinalIgnoreCase)
  - BuildTargets(currentEnvId, relations, environments) -> ordered PromotionTarget list
    (target env + RequiresApproval), ordered by target SORT_ORDER
  - record PromotionTarget(IntegrationEnvironment Target, bool RequiresApproval)
- Services/MartMartAutomation.cs: PromoteViaMartMerge(sourceEnv, targetEnv, log) SEAM ->
  placeholder log "Merge will run here (steps pending)", no destructive action.
- ModelConfigForm.Designer.cs: tabIntegrate shell TabPage (NOT added to tabControl by
  default, so default = hidden). Inner content built at runtime.
- ModelConfigForm.cs:
  - SetIntegrateTabVisible(bool): add/remove tabIntegrate from tabControl.TabPages (append
    at end keeps order).
  - RebuildIntegrateTab(): states - not-in-env line / "No promotions from {env}" / 1 target
    static / N targets combo / approval info / Integrate button / DB-error red text.
  - combo SelectedIndexChanged -> refresh action area (button vs approval info).
  - Integrate button click -> PromoteViaMartMerge seam (placeholder).
  - Wire into ConnectToModel success path (after mismatch/config-less guards):
    enabled = IsInitialized && ActiveConfigId>0 && GetEffectiveBool("INTEGRATE_ENABLED", false)
    SetIntegrateTabVisible(enabled); if (enabled) RebuildIntegrateTab();

## Visual (single clean row)
  [Current env badge]  --->  [Target badge | v Combo]   [ Integrate ] | "Requires approval..."
  - 0 targets: "No promotions available from {CurrentEnv}." (no button)
  - 1 target: static target text
  - N targets: ComboBox of targets; action area updates on change
  - selected target RequiresApproval: info text, NO button (no run)
  - COLOR_HEX -> env badge background (optional)

## Plan (checkable)
- [ ] 1. IntegrationEnvironmentService.cs (DTOs + dialect-aware raw-ADO reads, no swallow).
- [ ] 2. IntegrationPlanner.cs (pure logic + PromotionTarget DTO).
- [ ] 3. IntegrationPlannerTests.cs (parent-folder, current-env resolve, build-targets ordering/approval).
- [ ] 4. MartMartAutomation.PromoteViaMartMerge seam (placeholder log).
- [ ] 5. Designer: tabIntegrate shell.
- [ ] 6. ModelConfigForm: SetIntegrateTabVisible + RebuildIntegrateTab + handlers + ConnectToModel wiring.
- [ ] 7. Build 0/0 + dotnet test green. Self-verify states by reasoning through each branch.

## NOT in scope (this iteration)
- No real Merge execution (seam + placeholder only; no WM_COMMAND posted).
- No writes to ENVIRONMENT / ENVIRONMENT_RELATION (read-only).
- No approval mechanism (ENVIRONMENT_RELATION_APPROVER untouched).
- No Mart catalog browsing (open model's own path is enough for current-env).
- No admin-project changes.

## Review (2026-06-22 - DONE, not yet committed)
All 7 plan items done. New: Services/IntegrationEnvironmentService.cs,
Services/IntegrationPlanner.cs, tests/ErwinAddIn.Tests/IntegrationPlannerTests.cs.
Edited: Services/MartMartAutomation.cs (PromoteViaMartMerge seam),
ModelConfigForm.Designer.cs (tabIntegrate shell), ModelConfigForm.cs (region + 2 wiring edits).

Verification:
- Build 0 errors / 0 new warnings (new files use #nullable enable; 3 pre-existing xUnit1012
  warnings are not mine).
- Tests 360/360 green; IntegrationPlannerTests 18/18.
- No em-dash in any new/edited line (ripgrep \x{2014} - only pre-existing comments match).
- Linchpin verified by hand (ModelConfigForm.cs:1548-1561): a genuine model switch clears
  _globalDataLoaded -> full path -> ConfigContext re-resolves, so RefreshIntegrateTab inside
  InitializeModelServices never reads a stale config (fast path only runs for same-model reconnect).

Adversarial review (Workflow, 11 agents, 6 dimensions): 5 raw findings, 3 confirmed.
- HIGH: Integrate gate missing IsMartModel guard -> a local .erwin (config-initialized since
  2026-06-13, MartPath = file path) could falsely resolve a "current environment" from its
  folder name. FIXED: added !ctx.IsMartModel to IsIntegrateEnabled (mirrors every other Mart
  feature). Lesson captured in lessons.md (2026-06-22).
- LOW: gate-read failure hides tab + logs (does not surface on screen). KEPT: matches the
  codebase's PropertyApplicator.IsPropertyEnabled gate-read convention (logged, not swallowed);
  the data reads the user actually looks at DO surface errors in red.
- LOW: duplicate ENVIRONMENT.NAME resolves to lowest SORT_ORDER. Added a Log so the admin data
  anomaly is not silent (planner stays pure).

Pending: real Mart Merge execution (seam placeholder only); approval mechanism; commit when asked.

## Graph redesign (2026-06-22 - DONE, not yet committed)
User asked to replace the single-row promotion UI with a graphical topology like the admin
Integrate screen, with an Integrate action ON the allowed arrows. Confirmed via sketches:
full topology (admin parity) + round play-icon button on allowed arrows.

Reuse check (Explore on erwin-admin, read-only): admin draws it in
EnvironmentRelationsSection.RelationDiagram (pure System.Drawing) but it is private/sealed and
lives in MetaAdmin.dll (the app, NOT the shared DLL the add-in references) + coupled to
ServiceLocator/AppTheme. NOT reusable. So I reimplemented the SAME visual language in the add-in
with zero new dependencies.

Changes:
- Services/IntegrationEnvironmentService.cs: GetRelationsFrom -> GetRelations(configId) (full topology).
- Forms/EnvironmentPipelineDiagram.cs (NEW): Panel that paints rounded env nodes (ColorHex border,
  current highlighted), directed Bezier arrows (forward arc up / backward down, AdjustableArrowCap,
  approval = orange + badge), and overlays a round play-button on each allowed (non-approval)
  transition out of current; tooltip per button; IntegrateRequested event -> OnIntegrateClicked seam.
- ModelConfigForm.cs RebuildIntegrateTab: builds the diagram in an AutoScroll surface + legend
  (play / approval) + hint ("No promotions..." / "All ... require approval"); removed the old
  FlowLayout row builders + 2 unused color fields + 2 unused helpers.

Verification: build 0/0, tests 360/360. Adversarial review (code-analyzer): no critical/high/medium;
one low (Button Font/Region not deterministically disposed) FIXED (shared static glyph font +
b.Region dispose in cleanup). No em-dash in new code; English strings; no swallow; no dead code.

---

# "Template" naming rule type - runtime applier (2026-06-23)

New `RULE_TYPE='Template'` (admin Rule Management). Generates a target property
value from a template (tokens `{PropertyCode}` own / `{Alias.PropertyCode}`
related via MC_OBJECT_RELATION) and writes it via SCAPI on the per-column
lifecycle hook. v1 = COLUMN.Definition (example 1). Approved decisions: per-object
lifecycle (no full walk); COLUMN first, TABLE.PrimaryKey deferred; AUTO_APPLY=false
= Yes/No confirm.

## Done
- [x] `Services/NamingTemplateEngine.cs` - pure renderer + `ShouldWrite` + `TemplateResolutionException` (no-fallback). 25 unit tests.
- [x] `Services/ObjectRelationCatalog.cs` - cached raw-ADO MC_OBJECT_RELATION loader, `ResolveAlias(fromType, alias)`.
- [x] `NamingStandardService` - `Template` enum value; `ValueTemplate`/`TemplateFillMode` fields; 3-dialect query columns; AutoApply mask includes Template; `GetTemplateRules(objectType)`.
- [x] `NamingValidationEngine` - `IsRuleApplicable` made public (reuse); `case Template: break;` no-op in `EvaluateRule`.
- [x] `ValidationCoordinatorService` - `ApplyColumnTemplateRules` + `ReadScapiProperty` + `ResolveColumnRelatedProperty`; wired into `CheckEntityForChanges` (create-commit + update, placeholder-guarded, reuse treatAsNew).
- [x] `AddInPropertyMetadataService.GetRelations(int fromObjectTypeId)` - EF impl of the new MetaShared interface member (build was broken by the admin-side contract growth; not our regression).
- [x] `ModelConfigForm` - diagnostic dump Template branch + catalog reload next to naming load (only when a Template rule exists).

## Verification
- Build 0 warning / 0 error (TreatWarningsAsErrors).
- `dotnet test` 385/385 (25 new NamingTemplateEngineTests; flaky NamingStandardEngineTests passed this run).
- NOT live-verified yet (user will test): open a Mart model whose config has the COLUMN.Definition Template rule, add a column -> Definition rendered from parent table name; AUTO_APPLY true silent / false Yes-No; OnlyIfEmpty respected; token failure -> ERROR_MESSAGE logged + NO write.
- NOT committed (waiting for explicit request).

## Deferred / next stage
- TABLE.PrimaryKey (example 2): write PK Key_Group name. Blocked on a live SCAPI
  probe to identify WHICH Key_Group of an entity is the PK (`kg.Properties("Name")`
  write is proven; PK discrimination is not). Engine + catalog are generic, so this
  is a "PK target writer" adapter + table lifecycle wiring.
- TABLE/VIEW direct-property Template rules (no example yet): same engine, more wiring.

---

# Bug: Model name rename in Model Explorer fired no naming checks (2026-06-24)

The model validator `ValidateModelOnEditorClose` (MODEL.Name regex/prefix/required +
MODEL.Definition required) was only triggered on the "Model 'X' Editor" dialog close.
Renaming the MODEL node via Model Explorer inline edit never opened that dialog -> no
check fired. Log evidence: MODEL.* rules ARE loaded (rule#1102 Regexp MODEL.Name,
#1104 Prefix, #1131 Required, #1103 Required MODEL.Definition, all apply=Both) but
`NamingValidate:` fires only for Column/Table/View, never Model. Model-level analog of
the column-add-via-Model-Explorer bug.

## Fix (Services/ValidationCoordinatorService.cs)
- [x] `_modelNameSnapshot` instance field (fresh per connect -> no cross-model staleness).
- [x] Baseline it in `StartMonitoring` from `root.Name` (before timers start).
- [x] `ScanForModelRenameEventDriven(source)` - reuses the existing `ValidateModelOnEditorClose`; re-entrancy + session guards; advances baseline before validating, refreshes after.
- [x] Wired into the inline-edit-close edge (same edge as entity/column/view renames; does NOT fire on tab switch).
- [x] Fixed a stale comment that claimed the model validator is "warn-only / no write-back" (it DOES write Required fields).

## Verification
- Build 0/0; `dotnet test` 385/385.
- Adversarial code-analyzer review: NO real bugs. NITs addressed (guard parity with sibling; stale comment). Open UX note: a name-only rename also re-checks Definition (rare popup).
- NOT live-verified (user will test): rename model in Model Explorer -> MODEL.Name regex/prefix/required fire (RequiredFieldDialog + regex re-prompt).
- NOT committed.

## Update 2026-06-24: model rename "Revert Change" did not restore the old name
- User test: rename fired the warning (fix works), but "Revert Change" kept the typed invalid name. Root cause: `ValidateModelOnEditorClose` cancel branch logged "left as-is" and broke, never reverting (it had no prior value).
- Fix: `ValidateModelOnEditorClose(nameRevertValue, nameOnly)`. Cancel on the name now writes `nameRevertValue` back in a transaction (symmetric with the forward write). `nameOnly:true` so a name-only rename does not also prompt for Definition. `ScanForModelRenameEventDriven` passes the pre-rename name + nameOnly.
- Build 0/0; tests 385/385. Adversarial review: no real bugs. Editor-dialog-close path unchanged (still "left as-is" on cancel). NOTE log file c:\work\erwin-addin-debug.log was stale (last write 2026-06-23 15:25) so this was diagnosed from code; user must redeploy + retest.
- Pre-existing NIT (out of scope): Turkish write-failure popup at ValidationCoordinatorService.cs ~3386 violates the English-strings rule.

## Update 2026-06-24 (2): "Revert Change" now also works in the Model Editor DIALOG
- User confirmed inline-rename revert works; asked for the same in the Model Editor dialog.
- Fix (WindowMonitorTimer_Tick model-editor block): capture `_modelEditorOpenName = Root.Name` on the editor OPEN transition (pre-edit name); on CLOSE call `ValidateModelOnEditorClose(nameRevertValue: _modelEditorOpenName, nameOnly: false)` so "Revert Change" restores the name; refresh `_modelNameSnapshot` after; null the captured name. nameOnly stays false (the editor can also edit Definition).
- Adversarial review: core change sound + fail-safe on COM timing. Risk "double-fire with the inline scan" is empirically unreachable (if the editor triggered the inline edge, last turn's inline revert would already have worked in the editor - it did not) AND backstopped by the same-tick snapshot refresh; documented rather than guarded with new state. Fixed stale `nameRevertValue` XML doc.
- Build 0/0; tests 385/385 (1 flaky NamingStandardService singleton race; green on re-run). NOT committed.
- LOG NOTE: the live log is %TEMP%\erwin-addin-debug.log; the c:\work\erwin-addin-debug.log copy the user shares was frozen at 2026-06-23 15:25 (latest tests not captured) - diagnosed from code. User must re-copy the fresh %TEMP% log to c:\work to share runtime evidence.

## Update 2026-06-24 (3): "Revert Change" on the first model dialog must stop the whole chain
- User: reverting the first warning (Name) still popped the next dialog (Definition). Revert should abort the entire chain.
- Root cause: in ValidateModelOnEditorClose the per-property `foreach` validates Name then Definition; the cancel/revert branch only `break`ed the inner `while`, so the foreach advanced to the next property and opened another dialog.
- Fix: cancel/revert branch now `return`s (exits the whole method) after the revert write, so no further model property is validated this round. OK/fill path still continues to the next property. Also corrected a stale inline comment.
- Build 0/0; tests 385/385. Traced all paths (editor nameOnly:false multi-prop, inline nameOnly:true single-prop, OK-fill, write-failure unaffected). NOT committed.
- LOG STILL STALE: c:\work\erwin-addin-debug.log mtime unchanged (2026-06-23 15:25). Live log is %TEMP%\erwin-addin-debug.log in the erwin process; the c:\work copy was not refreshed, so diagnosed from code + the user's exact symptom.

## Update 2026-06-24 (4): apply the 2026-05-24 "force valid Required on revert" rule to COLUMN + MODEL
- User: "this logic should be in all rules". On investigation, the cross-property chain-stop already held for TABLE/VIEW/COLUMN (only MODEL was fixed earlier). The real asymmetry was the 2026-05-24 force-fix (re-prompt if the reverted value is still invalid): TABLE/VIEW had it, COLUMN/MODEL let the user escape.
- IMPORTANT: the user's first lean (revert -> stop+leave) contradicted their own 2026-05-24 rule; surfaced it + asked; user chose KEEP-2026-05-24 + apply everywhere.
- Fix (ValidationCoordinatorService.cs): MODEL cancel branch re-validates -> re-prompt (continue) if invalid, return if valid. COLUMN: wrapped the first dialog in a `while` (SITE 1) + made the OK-path re-prompt `while` (SITE 2) re-validate-then-reprompt. Both: existing-object revert-to-invalid loops; new-object discards; valid revert dismisses + stops chain. Added try/catch (fault -> treat valid, no trap) + session dismissal parity.
- Adversarial review: 1 real bug (SITE 2 missing dismissal on revert-to-valid) FIXED; 1 NIT (exception guard) FIXED; 1 NIT (Schema_Ref non-dismissable-by-revert) = intended, consistent with TABLE.
- Build 0/0; tests 385/385. NOT committed.

## PRIMARY KEY object type runtime support (2026-06-26)
- Admin added "PRIMARY KEY" governance type = Key_Group (Key_Group_Type="PK"); admin can author naming rules (target usually the PK constraint name) incl. Template (PK_{Table.Name}).
- Done: `ApplyPrimaryKeyRules` (Template applier, parallel to ApplyColumnTemplateRules) + `ResolvePrimaryKeyRelatedProperty` ("Table" alias) + call site after CheckEntityKeyGroups + `_pkTemplateSeen`/`_pkTemplateWriteFailed` (cleared in all 3 rebaseline paths) + ReadUdpValue "primary key"=>"Key_Group".
- Adversarial review: 2 bugs FIXED - (1) PK sets not cleared on rebaseline; (2) write-failure log-spam guard. PK-detection/string-consistency/idempotency/exception-safety CONFIRMED-OK.
- Build 0/0; tests 385/385. NOT committed.
- OPEN (CHALLENGE - needs live verify): codebase evidence says a Key_Group has NO `Physical_Name` (all existing writes use `Name`; KeyGroupCandidates omits it; Views-have-no-Physical_Name precedent). Applier is generic (writes rule.PropertyCode); if the admin PK rule targets Physical_Name and it throws, the log shows `[PK-TEMPLATE-ERROR] writing 'Physical_Name' failed` and the write is suppressed. Live-verify the correct PK constraint-name property (likely `Name`).
- DEFERRED: non-template PK rules (Prefix/Suffix/Length/Regexp/Required validate+prompt) and PRIMARY KEY object-existence rule mapping (needs Type=="PK" filter).

## Update 2026-06-26: PRIMARY KEY deferred items done (non-template + filtered existence)
- (1) Non-template PK rules: `ApplyPrimaryKeyRules` now also runs a non-template pass mirroring the Index flow (CheckEntityKeyGroups): baseline-on-first-sight then auto-apply prefix/suffix + validate-warn on a value change, snapshot-gated via `_pkPropertySnapshots` (cleared in all 3 rebaseline paths), warn-only (no required-field force-fill). Generic over PropertyCode. Early-out now fires only when there are NO PK rules at all (template OR non-template).
- (2) Filtered existence: `ScapiCollectTypeForExistence` maps PRIMARY_KEY -> Key_Group; `CheckRequiredObjectTypesExist` filters members by Key_Group_Type=="PK" and caches under a distinct "Key_Group:PK" key so a PRIMARY KEY existence rule never shares INDEX's any-Key_Group result.
- Adversarial review: NO real bugs (2 cosmetic NITs left). Snapshot gating = exactly-once-per-change (no _pendingResults spam); template/non-template don't oscillate; missing Physical_Name degrades to inert; existence cacheKey isolates PK from INDEX; exception-safe. no-full-walk preserved (PK pass is scoped to the processed entity).
- Build 0/0; tests 385/385. NOT committed. Physical_Name-on-Key_Group still pending live verify (generic code handles either way).

## Update 2026-06-29: PRIMARY KEY Template LIVE-VERIFIED + Physical_Name uncertainty RESOLVED
- Live log proof: template 'PK_{Table.Physical_Name}' -> [PK-TEMPLATE-APPLY] table='TEST' Physical_Name='PK_TEST' (once, no error, no runaway). User confirmed this is the intended behavior ("PK name'i bu olmalı"), feature stays.
- RESOLVED: Key_Group.Physical_Name IS writable via SCAPI (the PK constraint name) - unlike Views which have no Physical_Name. The earlier ship-blocker uncertainty is cleared.
- Self-referential guard LIVE-VERIFIED: with the old 'PK_{Physical_Name}' it logged [PK-TEMPLATE-SKIP] once, 0 APPLY, flicker gone.
- Status: all PK work + self-ref guard done. Build 0/0, tests 395/395. Generic over PropertyCode (admin can target Name instead of Physical_Name if a visible-name rename is ever wanted - no code change). NOT committed.

# WP#280 review (2026-07-18) - Predefined columns: single "When UDP" -> ordered AND/OR list

Done (add-in side; admin backend/web/migration already shipped):
- Shared evaluator: extracted `NamingValidationEngine.AreConditionsSatisfied(list, objectType, obj, pk?)` from `IsRuleApplicable` (which now delegates). Both naming rules and predefined columns fold through it - one engine, no duplicated logic.
- `PredefinedColumn`: dropped `DependsOnUdp*`; added `List<NamingRuleCondition> Conditions` (reuses the naming row type); `IsUnconditional => Conditions.Count == 0`.
- Loader: main `PREDEFINED_COLUMN` query no longer selects the dropped columns/UDP join; new `LoadColumnConditions` reads `MC_PREDEFINED_COLUMN_CONDITION` - a faithful clone of `NamingStandardService.LoadRuleConditions` (XOR-skip, ORDER_INDEX sort, fails the load on error).
- Applicability: `GetApplicableNames` + `FindApplicableLockedRule` go through the shared fold; removed `GetByUdpCondition`/`GetByUdpName`/`AddPredefinedColumnsForUdp`.
- Reactive: `ReevaluateConditionalPredefinedColumns` re-evaluates every conditional column's full list; the two per-UDP call sites (required-UDP prompt after WriteUdpValues, and the UDP-change `anyChanged` block) now call it once each.
- Hardening (from adversarial review): a term whose source FK is set but resolved name is empty (dangling UDP - gating UDP deleted) previously hit the evaluator's vacuous-true fallback -> single-term column applied to EVERY table. Now returns false (gate cannot hold). Restores old predefined fail-safe AND hardens naming (shared path).

Verified:
- Build 0/0. Tests 687/687 (rewrote `PredefinedColumnApplicabilityTests` for unconditional / single-migrated / AND / OR / left-to-right-fold / dangling-FK; fakes MUST be `public` for cross-assembly dynamic dispatch).
- Live schema (MetaRepoZeynep): child-table columns match the loader exactly; flat `DEPENDS_ON_UDP_*` gone from PREDEFINED_COLUMN; the exact loader JOIN query runs and resolves UDP_NAME.
- Live data across all 9 MetaRepo* DBs: 146 condition terms, ALL at ORDER_INDEX=0 (regression guarantee: migrated single conditions fold identically), 0 comma-bearing values (CSV-split concern inert), 0 dangling UDP FKs.
- REMAINING: in-erwin UI runtime test (create Log/Parametre tables, watch the columns land) - not driven here to avoid disrupting the live erwin session. NOT committed.

# Version Promotion (WP 322) - Live-test round 2 finding: per-model approvers ignored (2026-07-23)

User report: "onayci tanimli ama Auto-approve yaziyor" on CORE BANKING (config 2012, DB=MetaRepoTmp). My earlier close ("data state, no approvers, spec-correct") was WRONG - the user was right.

Root cause (REAL add-in bug):
- Log's DevDatabaseSelector line proves the test ran on MetaRepoTmp (not the DBs I checked). MetaRepoTmp config 2012 has approver 'Emre' in ENVIRONMENT_RELATION_MODEL_APPROVER for RELATION_ID 3 (TEST->UAT) and 4 (UAT->PROD), keyed by MART_PATH='Kursat/CORE BANKING ACCOUNTING_ACCOUNTING'. The transition-wide ENVIRONMENT_RELATION_APPROVER is empty.
- Approver resolution is TWO-TIER (per the LIVE server, MetaWeb.Api/Governance/PromotionEndpoints.cs, added 2026-07-23 via migration 20260722_approver_catalog_model_approvers.sql - AFTER the 2026-07-21 spec freeze): per-model override (RELATION_ID+MART_PATH) REPLACES the transition default; the default itself is FLOW='PROMOTION' filtered. Add-in's GetRelationApprovers read only the transition table, FLOW-blind and martPath-blind -> the override never surfaced -> wrong Auto-approve on BOTH the label AND the send-time insert decision.

Fix:
- PromotionPlanner.ResolveEffectiveApprovers(modelOverride, transitionDefault) - pure precedence (override wins when non-empty; else default; never merge; no APPROVAL_APPROVER fallback). Mirrors the server.
- PromotionService.GetRelationApprovers(relationId, martPath) - reads MODEL_APPROVER (RELATION_ID+MART_PATH) + transition default (RELATION_ID+FLOW='PROMOTION'), folds through the planner. MART_PATH is nvarchar(500) (not a LOB) so equality is safe on all three dialects.
- Call sites updated to pass martPath: PromotionFlow.BuildSendContext (context preload), DdlApprovalDialog.LookupPromotionApprovers (send-time live read fallback).

Verified: build 0/0 both flavors; tests 871/871 (+5 ResolveEffectiveApprovers: override-replaces-default, override-wins-over-non-empty-default, empty-override-falls-back, both-empty, end-to-end feeds RequiresApprovalVote). NOT committed (awaiting user OK). Awaiting live re-test round 2 on CORE BANKING: expect "Approval required" now, Pending row on send.

# Integrate redesign (2026-07-26) - merge moves from DDL Review to its own tab

User change of design: the merge chain must NOT hang off Generate DDL. DDL Review
goes back to behaving exactly like promote; Integrate gets its own tab with one button.

Done:
- `Integrate` tab restored (`ModelConfigForm.Designer.cs` + `#region Integrate tab`).
  Shown only for mart-hosted models whose config has `INTEGRATE_ENABLED`; body is
  rebuilt on every connect so stale topology cannot linger.
- Tab body: READ-ONLY pipeline diagram (`EnvironmentPipelineDiagram.SetData(...,
  showPromoteButtons: false)` - new parameter), the `1_DEV -> 2_TEST` route, and ONE
  `Integrate into <env>` button. Per-arrow buttons were wrong here: the merge always
  targets the next environment, so a choice was being offered that does not exist.
- Button flow: dirty gate -> approval-blocking rules -> two version comments ->
  `RunIntegrateMergeAsync`. The dirty gate only lets a POSITIVE clean reading through;
  `null` (title unreadable) refuses, because a merge stamps versions on BOTH models and
  unsaved edits would ride into the next environment with DDL nobody reviewed.
- DDL Review reverted to promote-like: `IntegrateMode`, `_integrateTarget`,
  `_integrateCallback`, `RunIntegrateSendAsync` and the `BtnSend_Click` integrate branch
  removed; the no-approver button now reads `Save and Close` (was `Save Model`); the
  confirm dialog is back to a single comment there.
- Dead code removed with the path that used it: `IntegrateFlow.ShouldOfferIntegrate`
  (approver-based button switching) + its 2 tests. `ConfirmSubmitDialog`'s integrate
  mode stays - the Integrate tab still needs both comments, because erwin raises two
  description dialogs during the merge chain.

Verified: build 0 warnings / 0 errors on all three flavors (default, PackagedBuild,
DdlGenerator); tests 960/960. NOT committed.

Open / carried over:
- Black rectangles after the right model loads - user deferred ("sonra bakalim"),
  candidate causes noted in `tasks/integrate-flow.md`.
- "Close Model" second checkbox column offset (+24px) is still a measured guess; the
  toolbar dump added earlier should reveal a proper check-all command id on the next run.
- `BLOCKS_DDL_APPROVEMENT` migration exists for SQL Server only (no PG/Oracle script).

# Integrate: tek yorum + dogru tamamlanma sarti (2026-07-26, ikinci tur)

Kullanici: "integrate'de 2 tane comment aliniyor hala, gerek yok - ilk modelde
degisiklik olmayacagi icin sadece integrate edilen 2. model icin comment yeterli."

Yapilanlar:
- `ConfirmSubmitDialog` integrate modu artik TEK kutu: "Version comment for <env>:".
  Ikinci TextBox, `IntegrateDescription` property'si ve 470px ozel yukseklik gitti.
- `RunIntegrateMerge` / `DriveIntegrateSaveCloseChain` tek `versionComment` aliyor;
  erwin hangi description dialogunu acarsa ona yaziliyor.
- **Bunu incelerken 17:23 kosusundaki takilmanin kok nedeni cikti**: dongu bitis
  sarti "IKI description gorulsun"du. Integrate tabi kirli modeli reddettigi icin
  calisilan model temiz; erwin onun icin aciklama sormuyor. Yani kosu, hic gelmeyecek
  bir dialogu 85 saniye bekledi ve o sirada bir sonraki kosunun dialoglarini kapti.
- Tamamlanma sarti artik dunya durumu: `CountOpenModelWindows()` merge oncesi
  baseline'in ALTINA duserse (>=1 damga sarti ile) tamam. Baseline farki kullanildi,
  mutlak sifir degil - kullanicinin baska modelleri acik olabilir. Okunamayan erwin
  -1 donuyor ve asla "tamam" saymiyor.

Verified: 3 flavor build 0/0, testler 963/963. NOT committed.
CANLI TEST BEKLIYOR - tamamlanma sartinin gercek kosuda dogru tetiklendigi
gorulmeli (log satiri: "cascade complete (... model windows N -> M)").

# Integrate 18:10 kosusu - 2 duzeltme (2026-07-26)

1. **Description doldurulmadi**: `Answered()` bloke beklemesi, erwin'in `Close Model`
   USTUNE actigi `Description for ...` modalini gormemize engel oluyordu. Bloke bekleme
   kaldirildi; yerine cevaplanmis-dialog defteri (HWND+caption) geldi - dongu canli
   kalirken ayni dialog iki kez tiklanmiyor.
2. **Sonuc mesaj kutusu erwin'i kilitledi**: mesaj kutusunun ic ice mesaj dongusune
   WM_TIMER dagitiliyor, gate dusmustu, reconnect tick'i modal pump icinde calisti.
   `AlterWizardGate` scope'u artik sonuc dialogunu de kapsiyor.

Verified: 3 flavor build 0/0, testler 963/963. NOT committed.
BLOKE: `Close Model` grid'inin satir sayisi ve kutucuk varsayilani bilinmiyor;
manuel kesif kosusu gerekiyor (bkz. tasks/integrate-flow.md "HALA BILINMEYEN").

# Integrate - kullanici ekran goruntuleriyle gelen 2 duzeltme (2026-07-26)

1. **Kutucuklar artik okunuyor, kor tiklanmiyor.** Durum yapiskan (erwin son
   birakilan hali hatirliyor), yani toggle guvenli degildi. `ReadCheckbox` ekran
   pikselinden okuyor (XTPReport Win32'ye kapali, UIA yasak), gerekirse tikliyor,
   sonra DOGRULUYOR. Dogrulanamazsa `SetAllRowCheckboxes` false donuyor ve cagiran
   OK'a basmiyor - merge'i kaybetmektense dialog kullaniciya birakiliyor.
   Kalibrasyon icin her cagride `[checkbox] gutter` izi loglaniyor.
2. **Calisilan model artik kapatiliyor.** "Close Model" sadece hedefi listeliyor
   (tek satir, ekran goruntusuyle kanitli); calisilan model icin erwin hic bir sey
   sormuyor. Merge oncesi aktif MDI child yakalanip, ekranda hicbir cascade dialogu
   kalmayinca WM_CLOSE gonderiliyor.

Verified: 3 flavor build 0/0, testler 963/963. NOT committed.
CANLI TEST BEKLIYOR.

# Integrate 19:15 kosusu - 2 duzeltme (2026-07-26)

Piksel okuma+dogrulama CALISTI: Close Model'de +12 zaten dogruydu (tiklanmadi),
+36 tiklanip dogrulandi, aciklama otomatik yazildi, calisilan modele WM_CLOSE gitti.

1. **Kolon sayisi diyaloga gore degisiyor** (Close Model 2, Save Models 1).
   Sabit iki-kolon varsayimi Save Models'ta abort etti. Kolonlar artik gutter
   taramasindan KESFEDILIYOR. Ayirici mantik saf fonksiyona cikarildi
   (`LocateCheckboxCentres`) ve loglardan alinan IKI GERCEK iz ile test edildi
   (`CheckboxColumnLocatorTests`, 6 test).
2. **Basarisizlik mesaji erwin'i kilitliyordu**: erwin'in kendi modali acikken
   WinForms modal = ayni UI thread'de iki modal dongu -> deadlock
   (`Save Models (Not Responding)`, 0 CPU). Artik native `ShowTopMostMessage`.

Verified: 3 flavor build 0/0, testler 971/971. NOT committed.

# Integrate 21:20 kosusu - zincir tamam, zamanlama duzeltildi (2026-07-26)

Zincir bastan sona calisti: kolon kesfi (Close Model 2 / Save Models 1), kutucuk
oku-tikla-dogrula, aciklama otomatik, calisilan model kapandi, cascade complete.

Kalan sorun ZAMANLAMA idi: modeller MDI'dan cikinca `model windows 1 -> 0` sarti
saglaniyor, ama erwin son versiyonu Mart'a hala YAZIYOR (yuzde penceresi). Cascade o
bosluga "complete" dedi ve popup mesgul erwin'in ustune dustu -> deadlock.

1. `WaitForErwinDialogsToClear` - complete demeden once erwin'in gorunur `#32770`
   dialoglari temizlenene kadar bekliyor (120sn siniri, bekledigi baslik loglaniyor).
   Sinif bazli arama; ilerleme penceresinin basligi hardcode edilmedi.
2. BASARI popup'i da native `ShowTopMostMessage`. Onceki turda sadece basarisizlik
   yolunu cevirmistim; basari yolu daha guvenli degil.

Verified: 3 flavor build 0/0, testler 971/971. NOT committed.

# Integrate - AKIS TAMAMLANDI + input block politikasi (2026-07-26)

21:33 kosusu bastan sona basarili: merge, kutucuk oku-dogrula, aciklama otomatik,
her iki model kapandi, `cascade complete`, native mesaj kutusu ile deadlock YOK.

Kalan tek sorun input block'tu ve teshis ONCE OLCULEREK yapildi:
`Responding=True` + `XTPMainFrame enabled=False` -> donma DEGIL, devre disi birakilma.

Duzeltme: `ErwinInputBlock.ShouldBlock` saf politika fonksiyonu + `hasOpenModel` sarti.
Model yokken bloklamak koruyacak bir sey olmadigi gibi kullaniciyi erwin'in
menulerinden de ediyordu (yeni model acamiyor, kapatamiyor).
`hasOpenModel` = `Win32Helper.GetActiveMdiChild(main) != Zero`, timeout'lu,
"bilemiyorum" -> bloklama.

Verified: 3 flavor build 0/0, testler 979/979 (+8 `ErwinInputBlockPolicyTests`).
Kullanicinin acik erwin'i (pid 32588) `EnableWindow` ile kurtarildi, oldurulmedi.
NOT committed.

# "Mart save failed" - GetErwinMainWindow yaris kosulu (2026-07-26)

Belirti: Generate DDL -> DDL Review -> comment -> gonder -> "Mart save failed.
Approval not submitted". Ayni islem 26 dakika once calismisti.

Log:
    22:42:36.508 SaveCurrentModelWithDescription: dirty before save = True
    22:42:36.617 SaveCurrentModelWithDescription: erwin XTPMainFrame HWND not resolvable - aborting.
    22:42:36.709 DdlApprovalDialog: Mart save callback returned false; aborting queue insert.

Kok neden: `SaveCurrentModelWithDescription` `Task.Run` icinde, yani ARKA PLAN
thread'inde calisiyor. `GetErwinMainWindow` ana pencereyi BASLIKTAN buluyordu ve
baslik okuma cross-thread `WM_GETTEXT` + 100ms `SMTO_ABORTIFHUNG`. erwin'in UI
thread'i mesgulse yaris kaybediliyor, fonksiyon Zero donuyor, save iptal oluyor.
Durum degil kura - bu yuzden "bazen oluyor".

Duzeltme: pencere kimligi artik PENCERE SINIFINDAN (`XTPMainFrame`) kuruluyor.
`GetClassName` / `IsWindowVisible` / `GetWindowThreadProcessId` window manager'dan
mesajsiz okunur, thread durumuna bagimli degildir. Baslik yalnizca adaylar arasinda
ayrim icin; hic `XTPMainFrame` yoksa eski baslik taramasi ikinci gecis olarak duruyor
(davranis supersetı, daralma yok). Ayrica sadece frame adaylari baslik icin
sorgulaniyor, yani takilmis bir `#32770` artik enum'a hic maliyet cikarmiyor.

Verified: 3 flavor build 0/0, testler 979/979. NOT committed.

# Integrate: dirty kapisi kaldirildi (2026-07-26, kullanici karari)

`OnIntegrateClicked` artik modelin kirli olup olmadigina bakmiyor. Kaldirilan blok
dirty (veya durumu okunamayan) modelde uyari verip zinciri kesiyordu.

Etkisi: kaydedilmemis degisiklikler de merge'e dahil oluyor (compare ekraninda
kullaniciya zaten gosteriliyor) ve erwin kapanis zincirinde calisilan model icin de
kendi save/description dialoglarini aciyor. Cascade her description dialoguna AYNI
tek yorumu yazdigi icin bu ek dialog kendiliginden isleniyor - kod degisikligi
gerekmedi.

`MartMartAutomation.IsActiveMdiChildDirtyByTitle` duruyor: promotion akisi ve DDL
review yollari (3 cagiran) hala kullaniyor.

Verified: 3 flavor build 0/0, testler 979/979. NOT committed.

# Integrate sonuc popup'i: add-in dialogu geri, guvenlik korundu (2026-07-26)

Kullanici native Windows popup yerine add-in'in kendi (daha guzel) dialogunu istedi.
Native'e gecmemin sebebi gercek bir deadlock'ti (ayni gun IKI kez oturum kaybi), o
yuzden kosulsuz geri almak yerine `ShowIntegrateResult` eklendi:

- erwin'de gorunur bir `#32770` YOKSA -> `Forms.AddinMessageDialog` (guzel olan).
- VARSA -> native `ShowTopMostMessage`.
- Prob'un kendisi patlarsa "erwin mesgul" sayiliyor (guvenli yon).

Cascade zaten `WaitForErwinDialogsToClear` ile erwin susana kadar bekliyor, yani
normal akista kullanici hep add-in dialogunu gorecek. Bu kontrol istisnanin oturuma
mal olmasini engelleyen kisim.

`MartMartAutomation.FirstVisibleErwinDialogTitle` private -> internal.

Verified: 3 flavor build 0/0, testler 979/979. NOT committed.

# Domain Like Glossary: kolon picker popup'i (2026-07-28)

Admin tarafi (erwin-admin) bitti; bu is paketi add-in yarisidir.
Runtime kontrati: `erwin-admin/docs/specs/domain-like-glossary.md`.

Bir config `USE_DOMAIN_LIKE_GLOSSARY` ile bu moda alindiginda, kolon adiyla eslestirme
YOKTUR: kullanici satiri bir popup'tan secer (Domain -> o domainin kolonlari), secilen
satirin maplenmis degerleri kolona uygulanir.

Kullanici kararlari:
- Popup HER yeni kolonda ve HER kolon edit'inde acilir (UX maliyetini sordum, teyit edildi).
- Cancel `GLOSSARY_REQUIRED_OPTION`'a uyar (silent / warn / required).
- Uygulanan: maplenen UDP + erwin property'ler VE kolonun fiziksel adi.
- Naming standard degeri regex tasiyan bir standardi isaret eder; ad ona gore dogrulanir.

## Yapilanlar

- `Services/DomainGlossaryService.cs` (YENI). `GlossaryService`'in KARDESI, icine gomulmedi:
  o servis `_MATCH_` etrafinda kurulu (satir yoksa hard failure) ve cache'i match degerine
  gore anahtarli. Iki mod zaten karsilikli dislayici, yani ayni anda en fazla biri yuklu.
  Sadece DG_DATA_SOURCE (named-SQL) yolu desteklenir; admin bu mapping icin TABLE_NAME
  yazmiyor, DATA_SOURCE_ID yoksa sessizce sorgu uydurmak yerine yuksek sesle hata verir.
- `Forms/DomainGlossaryPickerForm.cs` (YENI). Iki typeahead combo + uygulanacak degerlerin
  onizlemesi. Repoda tek bir `AutoCompleteMode` kullanimi bile yoktu, o yuzden liste
  daraltma TextChanged uzerinden elle yazildi; filtre `Filter` saf static olarak ayrildi.
- `ValidateGlossary` icine erken dal: mod aciksa picker akisi calisir ve `return` eder.
  Modal, `PromptAlwaysAskDatatype` desenini birebir izler (`_validationModalShowing`
  try/finally + sonrasinda `RefreshNameAfterModal`).
- `ApplyGlossaryUdpValues` iki opsiyonel resolver parametresi aldi. Sebep: bu modda
  `GlossaryService` HIC yuklenmiyor, dolayisiyla `GetTargetType` null donup her mapping
  `default:` koluna (UDP muamelesi) dusecek ve ERWIN_PROPERTY hedefleri kaybolacakti.
- `ModelConfigForm`: `DOMAIN_GLOSSARY_LOAD_INTERVAL` icin ayri refresh timer.

## Iki tuzak ve cozumleri

1. **Sonsuz popup dongusu.** Popup her edit'te aciliyor, ama picker'in kendi rename +
   UDP yazmalari da bir edit. Onlem: `_domainGlossaryApplied` (objectId -> uygulanan ad)
   ve saf `ShouldSkipDomainGlossaryPrompt`. Kolon hala tam olarak bizim yazdigimizi
   tasidigi surece prompt atlanir; sonraki her rename tekrar sorar.
2. **Kolon basina DB sorgusu.** Mod gate'i her kolonda calisiyor, ama
   `ConfigContextService.GetEffective` HER cagrida bir `RepoDbContext` acip iki sorgu
   atiyor. Onlem: `IsModeArmed()` bayragi config basina cache'ler; `Reload()` cache'i
   dusurur, boylece admin'deki toggle bir sonraki refresh'te yakalanir.

## Kapsam disi

- `Parent_Domain_Ref` yazimi (kolonu gercek erwin Domain nesnesine baglamak). Repoda bu
  property SADECE okunuyor, hicbir yerde yazilmiyor; SCAPI'de yazilabilirligi kanitsiz.
  Istenirse kendi spike'ini gerektirir.
- Prefix/suffix/template naming kurallarindan ad URETMEK. Sadece Regexp turu uygulanir.

Verified: build 0 warn / 0 err (TreatWarningsAsErrors acik), testler 1037/1037.
Canli erwin testi HENUZ YAPILMADI. NOT committed.
