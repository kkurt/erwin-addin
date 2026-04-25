# Probe: In-process Mart Multi-PU + `.erwin_isc` Loadability

**Tarih:** 2026-04-25
**Ortam:** erwin DM r10.10.38485.00, Mart Server localhost:18170, Model `Kursat/MetaRepo`
**Probe kodu (artık silindi):** `Services/ScapiStateInspector.cs` + üç turuncu/yeşil DIAG butonu
**Recovery:** git history (commit `c211033`'ten sonraki commit'lerde mevcuttu)

## Sorular

1. erwin GUI dirty bir Mart PU açıkken aynı modelin başka bir versiyonunu nasıl açabiliyor?
2. SCAPI API'mizden bunu in-process yapabilir miyiz? Worker spawn'ını eleyebilir miyiz?
3. erwin'in `%TEMP%\<PU_Id>.erwin_isc` dosyaları doğrudan kullanılabilir mi?
4. Active PU'nun dirty buffer'ı bir yerde diske yansıyor mu?

## Yöntem

3 tetiklenebilir DIAG butonu eklendi (Alter Compare tab'ında, hep tee-log
ile dosyaya da yazıyordu):

- **Dump SCAPI state** — `_scapi.PersistenceUnits` listele + erwin-transactions
  klasörü + son 10dk içinde değişen `%TEMP%` ve `%LOCALAPPDATA%\erwin` dosyalarını listele
- **Probe `.erwin_isc` loadability** — `<temp>\<active PU_Id>.erwin_isc`
  dosyasını `.erwin` uzantısıyla kopyala, `scapi.PersistenceUnits.Add` ile yüklemeyi dene
- **Probe same-version 2nd PU** — Active PU'nun Mart locator'ını (önce naïve,
  sonra short-form, sonra canonical) farklı disposition kombinasyonlarıyla
  `Add`'e geçirip 2. PU açılıp açılmadığını test et

## Bulgular

### Bulgu 1: Mart açıkken erwin GUI multi-PU yapıyor

Snapshot dizisi:
- v3 single open: `PersistenceUnits.Count = 1`, PU_Id `+01000000`
- User Mart > Open Model > v1: `Count = 2`, yeni PU_Id `+02000000` Active=True,
  v3 PU_Id `+01000000` Active=False (hâlâ canlı, locator korunmuş)

**Sonuç:** GUI tek SCAPI Application içinde paralel PU instance'ları açıyor.
Sessions koleksiyonu paylaşılıyor (Count = 5 değişmiyor).

### Bulgu 2: Dirty buffer `.erwin_isc`'te DEĞİL

User v3'te dirty edit yaptıktan sonra (`+182 attribute`):
- `{...}+01000000.erwin_isc` (v3) — boyut/timestamp **DEĞİŞMEDİ** (125,602 byte / open zamanı)
- `erwin-transactions\GDM*.gdmtxl` — **DEĞİŞTİ** (902 → 2,498 byte gibi büyüdü)

**Sonuç:** Live PU = `.erwin_isc` (open-time saved snapshot) + replayed
`*.gdmtxl` (transaction log). Dirty buffer GDM transaction log'unda; binary,
proprietary, parse edemiyoruz.

### Bulgu 3: `.erwin_isc` `.erwin` olarak yüklenmiyor

`scapi.PersistenceUnits.Add(<isc-copy>.erwin, "")`:

```
COMException: Persistence Units Component !
Failed while reading a file 'C:\...\erwin-isc-probe-*.erwin'
```

**Sonuç:** `.erwin_isc` erwin'in iç serialization formatı, `.erwin` export
formatından farklı. Sadece uzantı değiştirmek yetmiyor.

### Bulgu 4: KRİTİK — In-process Mart Add SCAPI tarafından engellenmiş

Active PU'nun Mart locator'ını short-form (`mart://Mart/<path>?VNO=<n>`) ve
canonical-with-credentials formla, 4 farklı disposition (`OVM=Yes`,
`RDO=Yes`, `OVS=Yes;RDO=Yes`, `""`) ile 2. PU eklemeyi denedik. **Hepsi**
aynı yönetilen exception ile reddedildi:

```
COMException: Persistence Units Component !
Mart user interface is active.
Only connection established by a user via Mart user interface
is available for use via the API.
```

(Daha öncesinde GUI'nin enriched `?&version=3&modelLongId=...` form'u SCAPI
parser'ında native AV ile erwin'i çökertmişti — ondan beri canonical
form kullandık.)

**Sonuç:** GUI bir Mart bağlantısı tutuyorken, SCAPI API kendi Mart
oturumunu açamaz; sadece GUI'nin oturumunu okuyabilir. Yani:

| Yol | İn-process API'den çalışıyor mu? |
|-----|----------------------------------|
| `scapi.PersistenceUnits.Add(martLocator, ...)` | ❌ engelli |
| GUI > Mart > Open Model | ✅ (GUI privileged) |
| Worker child process'te `Add` | ✅ (yeni process, kendi Mart oturumu) |

## Pipeline kararı

- **Worker mandatory** (Mart fetch için). In-process Add yolu kapalı.
- Mevcut single-Worker `cc-pipeline` arketipi optimum yaklaşım.
- Süre kısaltma için sıradaki yatırım: **long-lived Worker** (daemon mode).
  App start'ta spawn et, idle bekle, Compare anında stdin'den komut gönder.
  ~25s → ~5-8s warm path.

## DIAG kod artikleri

`Services/ScapiStateInspector.cs` + üç buton bu probe çalışmasının çıktısıydı.
Verdict netleştikten sonra silindi. Reproduce gerekirse: bu doc'un yazıldığı
commit'ten geri alınabilir.
