# Probe: `pu.Save(file, OVF=Yes)` Live Mart PU Üzerinde Yıkıcı

**Tarih:** 2026-04-25
**Ortam:** erwin DM r10.10.38485.00, Mart Server localhost:18170, Model `Kursat/MetaRepo` v3
**Probe kodu:** [Services/PuSaveProbe.cs](../Services/PuSaveProbe.cs), tetikleyici: Alter Compare tab'ındaki turuncu `[DEV] Probe PU.Save behavior` butonu

## Soru

Aktif (Mart-backed) PU'yu güvenli bir şekilde diske yazıp CC için tekrar
kullanabilir miyiz? SCAPI doc `Save`'in "yer-değiştirme" yaptığını söylüyor
ama daha önce bunu `.xml` formatında test etmiştik (farklı export path).
`.erwin` formatında live Mart PU üzerinde nasıl davrandığı bilinmiyordu.

## Yöntem

1. Aktif Mart-backed PU'nun PropertyBag snapshot'ı alındı
   (`Locator`, `Disposition`, `Persistence_Unit_Id`, `Name`, `Model_Type`,
   `Target_Server`, `Active_Model`, `Hidden_Model`).
2. `activePU.Save("temp.erwin", "OVF=Yes")` çağrıldı.
3. Aynı 8 alan tekrar okundu.
4. `activePU.FEModel_DDL("temp.sql", "")` benign post-check çağrıldı.

## Bulgular

### 1. Save'in kendisi başarılı

```
PuSaveProbe: calling activePU.Save(tempPath, "OVF=Yes") ...
PuSaveProbe: Save returned True
PuSaveProbe: temp file written, size=505,911 bytes
```

Dosya geçerli (505KB, sonra erwin'in kendisi tarafından açılabildi).

### 2. Live PU object **anında invalidate oluyor**

Save dönüşünden milisaniyeler sonra erwin'in kendi session-monitor'u tetiklendi:

```
[21:26:13.061] PuSaveProbe: Save returned True
[21:26:13.096] Session lost - model was closed.
[21:26:13.097] Model closed - session lost. Cleaning up services.
```

PropertyBag okumalarının hepsi COMException atıyor:

```
PuSaveProbe[AFTER]: Locator = (error: COMException/COMException)
PuSaveProbe[AFTER]: Disposition = (error: COMException/COMException)
... (8 alan da aynı)
```

FE_DDL post-check'inin tam exception mesajı:

```
System.Runtime.InteropServices.COMException:
  Persistence Unit Component ! Persistence Unit <unknown> is not available.
  It was either destroyed or corrupted. If it was a Model Mart model, it is
  also possible that the model was removed as a part of the mart connection
  termination.
```

### 3. erwin yan etkisi: yeni dosyayı otomatik yüklüyor

Live PU öldükten sonra erwin'in kendi reconnect mekanizması temp dosyayı
**ayrı, lokal bir model** olarak yeni session'a aldı:

```
[21:26:13.614] Model detected (1 open). Reconnecting...
[21:26:16.402] DDL: Model='Model_1', Version=3,
              Locator='erwin://C:\Users\Kursat\AppData\Local\Temp\3\pu-save-probe-20260425-212613.erwin'
[21:26:16.405] DDL: Current version from locator = v1
```

- Locator **Mart URL'sinden lokal dosya path'ine değişti**
- VNO bilgisi kayboldu (lokal dosyada Mart version metadata'sı yok →  `v1`'e düşüyor)
- Mart bağlantısı tamamen koptu

## Sonuç (Verdict)

`activePU.Save(localFile, "OVF=Yes")` r10.10'da **fully destructive ve geri
alınamaz**:

1. Live PU object COM-invalidate oluyor → bu PU üzerinden hiçbir SCAPI
   çağrısı yapılamıyor (PropertyBag, FEModel_DDL, vb. hepsi atıyor)
2. Mart binding sonlanıyor → PU artık Mart'a kaydedilemez
3. erwin GUI yeni dosyayı normal lokal model olarak yüklüyor → kullanıcı
   "açık olan Mart modelini" kaybediyor

`Locator` PropertyBag'de **read-only** olduğu için (API doc table) ne save
sonrası restore mümkün, ne de save öncesi farklı bir locator'a yönlendirme.

## Karar: Live add-in'de Save kullanmak yok

Mart-Mart compare için **Worker-process pipeline**'ını korumalıyız:

| Yol | Karar |
|-----|-------|
| `activePU.Save("...erwin", "OVF=Yes")` (in-process) | ❌ YIKICI - kullanılamaz |
| Save sonrası locator restore | ❌ Locator read-only + PU zaten ölü |
| In-process MCXInvokeCompleteCompare | ❌ MCXMartModelUtilities::PrepareServerModelSet null döndü (önceki F2 testi) |
| Worker child process | ✅ Tek güvenli yol — active PU asla dokunulmaz |

Worker'daki yavaşlık SCAPI'nin per-process ~10s startup'ından geliyor.
Çözüm: tek Worker process'inde tüm CC pipeline'ını çalıştıran combined
subcommand. (Sıradaki adım.)

## Probe Artifact'ları

- Probe kodu: [Services/PuSaveProbe.cs](../Services/PuSaveProbe.cs)
- Buton + handler: [ModelConfigForm.Designer.cs](../ModelConfigForm.Designer.cs)
  ve [ModelConfigForm.cs](../ModelConfigForm.cs) `btnProbePuSave_Click`

Buton şimdilik UI'da kalıyor; aynı bulguyu farklı bir Mart server / sürüm
üzerinde teyit etmek isteyen olursa tekrar koşturulabilir. İleride
production temizliği yapıldığında kaldırılabilir.
