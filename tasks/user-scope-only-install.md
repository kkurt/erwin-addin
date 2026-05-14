# User-Scope-Only Install + HKLM-First Bootstrap Read

**Status:** awaiting user approval before implementation
**Created:** 2026-05-12

## Goal

1. Remove `-Scope` parameter from install pipeline entirely; install is User-only (LOCALAPPDATA, UAC-free).
2. Step 4 (bootstrap config) becomes: HKLM probe -> skip if present; else seed file -> HKCU; else interactive prompt -> HKCU.
3. Add-in load path becomes: HKLM probe -> read; else HKCU read. DPAPI scope auto-derived from the hive the value came from.
4. Detect legacy Machine install (Program Files binary or HKLM COM CLSID) and abort with actionable "manual uninstall first" message.

## Decisions (locked by user)

| # | Decision |
|---|----------|
| 1 | `-Scope` fully removed: param, ValidateSet, seed file `Scope` field, `registry.scope` marker, package.ps1 `-Scope`, package-for-dev.ps1 `-Scope User`, all elevation logic |
| 2 | HKLM "has bootstrap" = key exists AND `DBHost` non-empty AND `DBName` non-empty. Legacy names (Host/Database/Username/Password) NOT honored |
| 3 | HKLM absent + seed absent + HKCU present: keep current "overwrite existing? [y/N]" prompt |
| 4 | `-ReCreateBootstrapRegistry` switch removed (no longer meaningful) |
| 5 | Legacy Machine install detected: abort with "Please manually uninstall the existing Machine-scope install first" |
| 6 | DPAPI scope auto-selected: HKLM source -> LocalMachine, HKCU source -> CurrentUser |

## Files to change

### erwin-addin repo

- **`installer\install.ps1`** -- remove Scope/elevation/ReCreate, hard-code User paths, add legacy-install detector, rewrite Step 4 with HKLM-first
- **`installer\package.ps1`** -- remove `-Scope`, stop writing `Scope` into seed
- **`package-for-dev.ps1`** -- drop `-Scope User`
- **`Services\CorporateContextService.cs`** (line 98) -- error message no longer references `RegistrySettingsService.CurrentScope` (single-hive); becomes `"HKLM\Software\...\Bootstrap or HKCU\Software\...\Bootstrap"`

### erwin-admin repo (cross-repo)

- **`MetaShared\Services\RegistrySettingsService.cs`** -- replace single-hive lazy `DetectScope()` with per-call HKLM-first reads + HKCU-only writes
  - `Read` / `ReadEncrypted`: probe HKLM, fall through to HKCU
  - `ReadEncrypted` uses LocalMachine DPAPI when value came from HKLM, CurrentUser when from HKCU
  - `Write` / `WriteEncrypted` / `DeleteSubKey`: HKCU only
  - `SubKeyExists`: true if either hive
  - Add `SubKeyExistsHKLM` / `SubKeyExistsHKCU` for callers that need to distinguish (e.g. error messages, install.ps1 detection)
  - `CurrentScope` static property: remove (or return constant "HKLM-first")
- **`MetaShared\Services\RegistryBootstrapService.cs`** -- `GetConfigFilePath()` returns the hive the cached config actually came from; `WriteToRegistry` / `DeleteConfig` go through new HKCU-only Write/Delete

## Step 4 (install.ps1) rewrite -- pseudo

```powershell
$hklmBoot = "HKLM:\Software\EliteSoft\MetaRepo\Bootstrap"
$hkcuBoot = "HKCU:\Software\EliteSoft\MetaRepo\Bootstrap"

# 4a: HKLM probe
if (Test-Path $hklmBoot) {
    $h = Get-ItemProperty -LiteralPath $hklmBoot -ErrorAction SilentlyContinue
    if ($h -and $h.DBHost -and $h.DBName) {
        Write-Host "[4] HKLM bootstrap detected; skipping HKCU bootstrap write." -ForegroundColor Green
        Write-Host "    DBType=$($h.DBType) DBHost=$($h.DBHost) DBName=$($h.DBName)" -ForegroundColor Gray
        Write-Host "    Add-in will read DB config from HKLM at load time." -ForegroundColor Gray
        return
    }
}

# 4b: seed file
$seed = Join-Path $sourceDir "bootstrap.seed.json"
if (Test-Path -LiteralPath $seed) {
    $s = Get-Content -LiteralPath $seed -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($s.DBHost -and $s.DBName) {
        Write-Bootstrap-To-HKCU $s   # DPAPI = CurrentUser
        Remove-Item -LiteralPath $seed -Force
        return
    }
}

# 4c: interactive prompt (existing flow, simplified)
#   - if HKCU already exists -> keep "overwrite? [y/N]" prompt
#   - else -> Read-Host for each field, write HKCU, DPAPI = CurrentUser
```

## Legacy-Machine-install detector (install.ps1, early)

```powershell
$legacyHits = @()
if (Test-Path "$env:ProgramFiles\EliteSoft\ErwinAddIn") {
    $legacyHits += "  - Binary folder: $env:ProgramFiles\EliteSoft\ErwinAddIn"
}
$ourClsid = Get-OurComClsid   # already computed in install.ps1
if (Test-Path "HKLM:\Software\Classes\CLSID\$ourClsid") {
    $legacyHits += "  - HKLM COM registration: HKLM\Software\Classes\CLSID\$ourClsid"
}
if ($legacyHits.Count -gt 0) {
    Write-Host "Detected an existing Machine-scope install:" -ForegroundColor Red
    $legacyHits | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    Write-Host ""
    Write-Host "User-scope install would leave dangling Program Files binaries and HKLM COM" -ForegroundColor Yellow
    Write-Host "registration. Please run the old install.ps1 -Uninstall as Administrator on this" -ForegroundColor Yellow
    Write-Host "machine first, then re-run this script." -ForegroundColor Yellow
    exit 1
}
```

**HKLM bootstrap key alone is NOT a legacy-install trigger** -- admins legitimately seed HKLM from external tooling. Only `Program Files\EliteSoft\ErwinAddIn` directory OR `HKLM\Software\Classes\CLSID\<our-clsid>` aborts.

## RegistrySettingsService API change

Current (single-hive at startup):

```csharp
public static string CurrentScope => RootKey == Registry.LocalMachine ? "HKLM" : "HKCU";
public static string Read(string subKey, string name, string defaultValue = "") { ... single RootKey ... }
public static string ReadEncrypted(string subKey, string name) { ... CurrentScope check ... }
```

New (HKLM-first reads, HKCU-only writes):

```csharp
public static string Read(string subKey, string name, string defaultValue = "") {
    var v = ReadFromHive(Registry.LocalMachine, subKey, name);
    if (v != null) return v;
    v = ReadFromHive(Registry.CurrentUser, subKey, name);
    return v ?? defaultValue;
}

public static (string Value, string SourceHive) ReadWithSource(string subKey, string name, string defaultValue = "") {
    var v = ReadFromHive(Registry.LocalMachine, subKey, name);
    if (v != null) return (v, "HKLM");
    v = ReadFromHive(Registry.CurrentUser, subKey, name);
    return (v ?? defaultValue, v != null ? "HKCU" : "");
}

public static string ReadEncrypted(string subKey, string name) {
    var (encrypted, hive) = ReadWithSource(subKey, name);
    if (string.IsNullOrEmpty(encrypted)) return "";
    return hive == "HKLM"
        ? PasswordEncryptionService.DecryptMachine(encrypted) ?? ""
        : PasswordEncryptionService.Decrypt(encrypted) ?? "";
}

public static void Write(string subKey, string name, string value) {
    using (var key = Registry.CurrentUser.CreateSubKey($@"{BaseKey}\{subKey}"))
        key.SetValue(name, value ?? "", RegistryValueKind.String);
}

public static void WriteEncrypted(string subKey, string name, string plainText) {
    // HKCU only, CurrentUser DPAPI only -- admin tools that need HKLM use WriteEncryptedMachine
    var encrypted = PasswordEncryptionService.Encrypt(plainText);
    Write(subKey, name, encrypted ?? "");
}

public static bool SubKeyExists(string subKey) {
    return SubKeyExistsHKLM(subKey) || SubKeyExistsHKCU(subKey);
}
public static bool SubKeyExistsHKLM(string subKey) { ... }
public static bool SubKeyExistsHKCU(string subKey) { ... }

// CurrentScope removed (per-call only, no global concept)
```

**Backward-compat for admin tools (if needed):** add `WriteHKLM` / `WriteEncryptedMachine` so admin/install tooling can target HKLM explicitly. Search erwin-admin for `RegistrySettingsService.Write(` callers first; if any of them are admin-only that target HKLM, we need this.

## CorporateContextService error message

```csharp
// before:
LastError = $"No configuration found in {RegistrySettingsService.CurrentScope}\\Software\\EliteSoft\\MetaRepo\\Bootstrap. ...";

// after:
LastError = "No configuration found in HKLM\\Software\\EliteSoft\\MetaRepo\\Bootstrap or HKCU\\Software\\EliteSoft\\MetaRepo\\Bootstrap. Please run the installer to configure the add-in.";
```

## Acceptance criteria

- [ ] Fresh User install, no HKLM bootstrap, no seed: prompts user, writes HKCU; add-in reads HKCU, CurrentUser DPAPI
- [ ] Fresh User install, HKLM bootstrap pre-seeded by admin: Step 4 logs "HKLM detected, skipping" + does NOT touch HKCU; add-in reads HKLM, LocalMachine DPAPI
- [ ] Fresh User install with `bootstrap.seed.json`: silent HKCU write, seed deleted, no prompt
- [ ] Re-install with HKCU present (no HKLM, no seed): "overwrite? [y/N]" prompt fires
- [ ] Re-install on machine with `%ProgramFiles%\EliteSoft\ErwinAddIn` present: aborts with manual-uninstall message; nothing written
- [ ] Re-install on machine with HKLM COM CLSID present: aborts with manual-uninstall message
- [ ] HKLM has DBHost but missing DBName (partial seed): treated as "no HKLM"; Step 4 falls through to seed/prompt
- [ ] `.\install.ps1 -?` help output makes no reference to Scope
- [ ] `.\install.ps1` (no args) runs cleanly; no "param X is mandatory" error
- [ ] `.\package.ps1 -Zip` (no args) produces a User-only installer; no Scope baked into seed
- [ ] PowerShell 5.1 AND PowerShell 7+ both run install.ps1 cleanly (System.Security Add-Type fix carried over)
- [ ] Both repo builds clean (erwin-admin build first, then erwin-addin)

## Risk register

| Risk | Mitigation |
|------|------------|
| Cross-repo compile failure: changing MetaShared API breaks erwin-admin callers | grep `RegistrySettingsService.` across erwin-admin BEFORE changing the API; list all call sites in this plan before editing |
| `RegistrySettingsService.CurrentScope` used outside erwin-addin | grep first; if used widely, keep as deprecated `[Obsolete]` returning "HKLM-first" |
| Admin tools that legitimately write HKLM through `RegistrySettingsService.Write` | grep first; if they exist, add explicit `WriteHKLM`/`WriteEncryptedMachine` and migrate those callers |
| Existing `bootstrap.seed.json` files with `Scope` field on customer machines | Extra JSON fields ignored on deserialize -- backward compatible |
| `RegistryBootstrapService.MigrateFromJsonIfNeeded` writes to HKCU (via new Write); was writing to whatever hive `registry.scope` said. Acceptable? | Yes -- JSON migration target is always the running user, so HKCU is correct |
| User had Machine install, manually uninstalled but left HKLM CLSID stale | abort message is actionable; user re-runs old uninstaller or `Remove-Item HKLM:\...` manually |
| Legacy `registry.scope` file inside %ProgramFiles%\EliteSoft\ErwinAddIn during transition | the abort-on-Program-Files detector fires first; never reached |

## Implementation order

1. [PRE-WORK] grep `RegistrySettingsService` across erwin-admin to see ALL callers and whether anything writes HKLM
2. Update `tasks/user-scope-only-install.md` with grep findings (any extra files surface here)
3. **erwin-admin** changes: RegistrySettingsService + RegistryBootstrapService
4. Build erwin-admin -> green
5. **erwin-addin** changes: install.ps1 + package.ps1 + package-for-dev.ps1 + CorporateContextService error string
6. Build erwin-addin -> green
7. Manual install test on a clean LOCALAPPDATA + clean HKCU
8. Manual install test with HKLM seeded by hand (admin scenario)
9. Manual install test with `bootstrap.seed.json` present
10. Manual install test on a machine with Program Files binary present (must abort cleanly)

## Open questions before implementation

None -- decisions 1-6 are locked. Awaiting user "go".
