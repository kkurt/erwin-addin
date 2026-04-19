# Alter Script: UI Automation'dan Tam Programmatic'e Geçiş

## Amaç

`BtnAlterWizardProd_Click` mevcutta `WizardAutomationService` (1254 satır WM_COMMAND + UIA) ile erwin'in CC + RD + Alter Script wizard'larını sürüyor. Bu fragile. Native detour spike (ee49220) erwin'in kendi `FEProcessor::GenerateAlterScript` export'unu çağırabildiğimizi kanıtladı. Bu planla tüm akışı **sıfır UI** yapıyoruz — erwin'in C++ internal fonksiyonlarını doğrudan C++ köprüsüyle çağırıyoruz.

## Ana Prensipler (DOKUNMA)

- [x] Alter DDL'i biz üretmiyoruz — erwin'in `FEProcessor::GenerateAlterScript` üretiyor
- [x] Diff mantığı biz yazmıyoruz — erwin'in `MCXInvokeCompleteCompare` + `MCXMartModelUtilities` yapıyor
- [x] C++ köprüsü SADECE native fonksiyonları sarıyor, iş mantığı eklemiyor
- [x] Erwin sürüm değişikliklerinde güvenlik için dynamic GetProcAddress + symbol fingerprint guard

## Kritik Native Fonksiyonlar (doğrulanmış, dumpbin teyit)

```
EM_FEP.dll:
  ?CreateObject@FEProcessor@@SAPEAVCObject@@XZ               (static factory)
  ?GenerateAlterScript@FEProcessor@@QEAA...                  (the alter engine)
  ?GenerateFEScript@FEProcessor@@QEAA?AW4eFEPResult@@PEAVGDMModelSetI@@PEAVCWnd@@@Z
  ?GetScript@FEProcessor@@QEAA...                            (returns vector<CString>&)

EM_MCX.dll:
  ??0MCXInvokeCompleteCompare@@QEAA@PEAVGDMModelSetI@@0PEAVGDMActionSummary@@1@Z   (ctor)
  ?Execute@MCXInvokeCompleteCompare@@UEAA_NPEAVGDMModelSetI@@@Z
  ??1MCXInvokeCompleteCompare@@UEAA@XZ                       (dtor)
  ?PrepareServerModelSet@MCXMartModelUtilities@@SA...        (baseline from Mart)
  ?InitializeClientActionSummary@MCXMartModelUtilities@@SA...
  ?GetMartVersionId@MCXMartModelUtilities@@SA...
  ?DoesModelHaveUnsavedChanges@MCXMartModelUtilities@@SA...
```

## Tek Blocker: `GDMModelSetI*` Kaynağı

SCAPI `ISCPersistenceUnit` → `GDMModelSetI*` çevirisi YOK (EAL.dll opaque). Çözüm: `GenerateFEScript`'e detour + biz bir kez `FEModel_DDL` çağırdığımızda pointer'ı yakala → cache.

## Faz 1 — Pointer Capture Altyapısı (1 gün)

### 1a. native-bridge.cpp (v5)
- [ ] `GenerateFEScript` export'una inline detour ekle (v3-tarzı 14-byte trampoline — prologue zaten güvenli gibi, doğrula)
- [ ] Detour içinde `modelSet` pointer'ını thread-safe cache'e yaz (`g_lastCapturedModelSet` atomic)
- [ ] Yeni export: `GetLastCapturedModelSet()` → IntPtr döndürür
- [ ] Yeni export: `ResetCapturedModelSet()` (debug/reset için)

### 1b. NativeBridgeService.cs
- [ ] `IntPtr GetLastCapturedModelSet()` P/Invoke
- [ ] `void ResetCapturedModelSet()` P/Invoke
- [ ] `EnsureActiveModelSetCaptured(dynamic currentPU)` yardımcı — cache boşsa bir kere `currentPU.FEModel_DDL(tempPath, "")` tetikle, detour yakalayacak, cache dolu olacak

### 1c. Doğrulama
- [ ] Erwin, addin açık, manuel test: `_currentModel.FEModel_DDL(...)` çağır, log'da `[FE] captured modelSet=0x...` satırını gör
- [ ] NativeBridgeService.GetLastCapturedModelSet() non-zero IntPtr dönüyor

## Faz 2 — MCX + FEProcessor Pipeline (2 gün)

### 2a. native-bridge.cpp wrappers
- [ ] `PrepareServerModelSet` export'unu resolve et
- [ ] `InitializeClientActionSummary` export'unu resolve et
- [ ] `MCXInvokeCompleteCompare` ctor/Execute/dtor export'larını resolve et
- [ ] `FEProcessor::CreateObject` + dtor resolve et
- [ ] Yeni C++ fonksiyon: `RunSilentAlterDdl(GDMModelSetI* client) → char*` (UTF-8 alter DDL veya null)
  - `PrepareServerModelSet(client, &as1)` → serverMs
  - `InitializeClientActionSummary(client, &as2)`
  - 1KB buffer allocate → MCXInvokeCompleteCompare ctor
  - `cc.Execute(client)` → populates as2
  - `FEProcessor::CreateObject()` → fep instance
  - `fep->GenerateAlterScript(client, as2, nullptr, false)`
  - `vector<CString>& ddl = fep->GetScript()` → concat to single UTF-8 string
  - Free server/summary resources, return malloc'd string
- [ ] Yeni export: `GenerateAlterDdlForActiveModel(IntPtr modelSet) → LPSTR` (caller frees via `FreeDdlBuffer`)
- [ ] Yeni export: `FreeDdlBuffer(LPSTR)`

### 2b. NativeBridgeService.cs
- [ ] `string GenerateAlterDdl(dynamic currentPU)` high-level API
  - `EnsureActiveModelSetCaptured(currentPU)`
  - `IntPtr ms = GetLastCapturedModelSet()`
  - P/Invoke `GenerateAlterDdlForActiveModel(ms)`
  - UTF-8 → managed string; native buffer'ı free et
  - Null/boş döndüyse "no differences" tut

### 2c. Doğrulama
- [ ] Dirty V3 açık, basit test formu: `NativeBridgeService.GenerateAlterDdl(_currentModel)` çağır
- [ ] Dönen DDL, wizard UI'nın ürettiği DDL ile birebir aynı
- [ ] "No differences" senaryosu (model temizse) temiz string/null döner

## Faz 3 — UI Entegrasyonu + Eski Kod Silme (1 gün)

### 3a. ModelConfigForm.cs
- [ ] `BtnAlterWizardProd_Click` yeniden yaz:
  - `WizardAutomationService` kullanma
  - Wait dialog aç
  - Background thread: `NativeBridgeService.GenerateAlterDdl(_currentModel)`
  - DDL rtbDDLOutput'a
  - Status güncelle
- [ ] `cmbRightModel`'den version number okunup `NativeBridgeService`'e iletilmesi
  - Not: MCX kendisi baseline'ı seçiyor mu, yoksa version biz söylüyor muyuz? Faz 2'de netleştir

### 3b. Silinecekler
- [ ] `Services/WizardAutomationService.cs` komple sil (~1254 satır)
- [ ] `ModelConfigForm.cs` içindeki wizard-related state (`rePUToCleanup` gibi) temizle
- [ ] `ErwinAddIn.csproj`'da WizardAutomationService referansı varsa kaldır
- [ ] Memory note `reference_alter_script_wizard_automation.md` "deprecated" olarak işaretle (ama silme — referans için dursun)

### 3c. Doğrulama
- [ ] `dotnet build` temiz
- [ ] Happy path: "From Mart" Alter DDL üretimi çalışıyor, UI görünmüyor
- [ ] Erwin UI donmuyor (background thread temiz)

## Faz 4 — Edge Cases + Hata Yönetimi (1 gün)

- [ ] `DoesModelHaveUnsavedChanges`'ı ön-kontrol olarak ekle — temiz modelde ne olur?
- [ ] Dirty transaction durumu: native tarafa göndermeden önce commit gerekiyor mu?
- [ ] `PrepareServerModelSet` null dönerse (Mart erişimi yok) → kullanıcıya anlamlı hata
- [ ] Bridge DLL yüklenmemişse → kullanıcıya "native bridge unavailable" mesajı + eski akışa düş (feature flag)
- [ ] Symbol resolution fail ederse (erwin sürümü farklı) → log + graceful degradation
- [ ] Thread safety: aynı anda 2 alter-ddl request gelmez ama yine de mutex koy

## Doğrulama (Genel)

- [ ] Happy path: dirty V3 vs baseline, Preview ekranındaki DDL ile biri bir aynı
- [ ] Hızlı: erwin UI otomasyonu ~10s idi, hedef <1s
- [ ] Erwin yeniden başlatılırsa bridge yeniden inject oluyor, sorunsuz çalışıyor
- [ ] Eski wizard-automation kod yolu çalışmıyor (feature flag ile kapalı) — wizard açılmıyor

## Sonrası

- UI: `lblDDLStatus` yeni akışa uygun mesajlar
- Memory/documentation: `reference_silent_re_pattern.md` gibi bir `reference_silent_alter_script_pattern.md` yaz
- Production: regular DLL build → CI, release
