# Installation and Load Logic Reference

This document describes how Elite Soft Erwin Add-In is installed on a user's
machine, where files and registry entries land, and how the running add-in
discovers its database bootstrap configuration. It is the source of truth for
`installer/install-impl.ps1`, `installer/package.ps1`, and the runtime
`DatabaseService` / `HkcuBootstrapReader` C# classes.

## At a glance

- **Single install mode: per-user.** No `-Scope` parameter. Binaries land in
  `%LOCALAPPDATA%\EliteSoft\ErwinAddIn`. COM is registered under
  `HKCU\Software\Classes`. The installer never elevates and never prompts for
  UAC.
- **End user double-clicks `install.bat`** (or `uninstall.bat`); both forward
  to `install-impl.ps1` with `-NoProfile -ExecutionPolicy Bypass` (per-process,
  immune to GPO policy override). No need to open PowerShell manually.
- **HKCU-only bootstrap at runtime AND at install time (as of 2026-07-02).** The
  add-in reads `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` only (DPAPI scope:
  CurrentUser), via `HkcuBootstrapReader`, and `install-impl.ps1` writes that
  same key only. HKLM is never read and never written for bootstrap. HKLM
  support (the old "corporate IT seeds HKLM, the add-in reads it first") was
  removed: a single hive removes the "stale HKLM shadows the current HKCU
  config" class of bugs and the per-hive DPAPI-scope ambiguity.

## Install paths (all User scope)

| What | Path |
|------|------|
| Binaries | `%LOCALAPPDATA%\EliteSoft\ErwinAddIn` |
| COM registration | `HKCU\Software\Classes\CLSID\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` |
| ProgId | `HKCU\Software\Classes\EliteSoft.Erwin.AddIn` |
| Add-In Manager entry | `HKCU\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\EliteSoft.Erwin.AddIn` |
| Bootstrap config (read + write target) | `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` |
| Auto-start Scheduled Task | per-user task triggered on logon |
| Uninstall detection | presence of `%LOCALAPPDATA%\EliteSoft\ErwinAddIn` |

CLSID is hard-coded in `install-impl.ps1` and must mirror the `[Guid(...)]` attribute
on the `ErwinAddIn` class. Add-In Manager registry is HKCU regardless of who
installed, because erwin DM r10 only reads Add-In entries from HKCU
(empirically verified -- HKLM entries are invisible in the Tools menu).

## install-impl.ps1 step sequence

```
[1/4] Copy files                  -> %LOCALAPPDATA%\EliteSoft\ErwinAddIn
[2/4] Register COM                -> HKCU\Software\Classes (no regsvr32, no UAC)
[3/4] Register in Add-In Manager  -> HKCU\SOFTWARE\erwin\Data Modeler\10.10
[4]   Configure MetaRepo bootstrap (HKCU-only; see below)
```

### Step 4 (bootstrap) decision tree

HKCU-only. HKLM is never consulted or written.

```
Does bootstrap.seed.json sit next to install-impl.ps1
  (DBHost + DBName non-empty in the file)?

  YES -> Write straight to HKCU silently (DPAPI = CurrentUser).
         Delete the seed file on success.

  NO  -> Does HKCU bootstrap already exist?

         YES -> Show current-vs-new summary;
                ask "Overwrite existing? [y/N]";
                if y, prompt for missing fields, write HKCU;
                if n, leave HKCU as-is.

         NO  -> Prompt for every field interactively;
                write HKCU (DPAPI = CurrentUser).
```

### Legacy Machine-install detection (early abort)

Before Step 1 runs, `install-impl.ps1` checks for leftover Machine-scope artifacts
from any prior install. If any of these are present, it aborts with an
actionable message:

- `%ProgramFiles%\EliteSoft\ErwinAddIn\` directory exists
- `HKLM\Software\Classes\CLSID\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` exists

Abort message:

```
Detected an existing Machine-scope install:
  - <listed hits>
User-scope install would leave dangling Program Files binaries and HKLM COM
registration. Please run the old install.ps1 -Uninstall as Administrator
on this machine first, then re-run this script.
```

This detection is about the old **COM/Program Files** Machine-scope install
only. `HKLM\Software\EliteSoft\MetaRepo\Bootstrap` is NOT part of it and is
never read: a stray HKLM bootstrap key left by old tooling is simply ignored
by both the installer and the add-in.

## DPAPI scope rules

The add-in reads, and the installer writes, HKCU only, so the DPAPI scope is
always `CurrentUser`:

| Hive | Scope used to Encrypt | Scope used to Decrypt |
|------|-----------------------|-----------------------|
| HKCU | `DataProtectionScope.CurrentUser` | `DataProtectionScope.CurrentUser` |

Because the encrypt and decrypt scopes always match, credentials seeded on one
user profile do not decrypt on another (a `bootstrap.seed.json` copied between
machines/accounts is re-encrypted at install time, so that is fine; a raw HKCU
key copied verbatim between profiles is not - DPAPI Unprotect throws, and the
add-in surfaces that error rather than returning ciphertext).

## Add-in load-time read path

`Services/DatabaseService.cs` instantiates `HkcuBootstrapReader` on first
access. The reader implements `IBootstrapService` and resolves the
`BootstrapConfig` like this:

1. Try to open `HKEY_CURRENT_USER\Software\EliteSoft\MetaRepo\Bootstrap`.
2. If the subkey exists AND `DBHost` and `DBName` are both non-empty, read all
   six values (`DBType`, `DBHost`, `DBPort`, `DBName`, `DBUserName`,
   `DBPassword`) and DPAPI-Unprotect the encrypted ones with `CurrentUser`
   scope.
3. Otherwise `IsConfigured` returns false and `ConfigContextService` surfaces a
   warning pointing at the single candidate path
   (`HKCU\Software\EliteSoft\MetaRepo\Bootstrap`).

There is no hive fallback: the add-in reads exactly one place. `DatabaseService`
caches the result; `DatabaseService.ClearCache()` (wired into the dev "Reload
Config" button) drops the cache so the next read re-reads HKCU.

## package.ps1 contract

`package.ps1` produces a `.zip` (or staged folder) that contains:

- All `bin\Release\` files from the add-in
- `installer\install-impl.ps1` (this script)
- optional `bootstrap.seed.json` if the packager passed `-DBHost`, `-DBName`,
  etc. at packaging time

There is no `-Scope` flag on `package.ps1`. Every output package is
User-scope; the receiver runs `.\install-impl.ps1` (no arguments needed) and gets
a per-user install.

The seed can be **full** or **partial**. Passing any connection field
(`-DBHost`, `-DBName`, `-DBUserName`, `-DBPassword`) requires **both** `-DBHost`
and `-DBName`, or packaging aborts: a half-filled connection seed would silently
drop the baked password at install time (the interactive password prompt has no
default). Passing only `-DBType` and/or `-DBPort` writes a **partial** seed with
empty host/name; at install time those values become the pre-filled defaults for
the interactive prompts (e.g. `-DBType Oracle` for a POC where the target DB
coordinates are entered on the install machine). `HkcuBootstrapReader` treats an
empty host/name seed as "not configured", so a partial seed never yields a
broken runtime config.

`bootstrap.seed.json` contains plaintext credentials (the receiver's machine
hasn't run DPAPI yet at package time). `install-impl.ps1` encrypts the values with
DPAPI `CurrentUser` and **deletes the seed file** as soon as the HKCU write
succeeds. If the user aborts the install before Step 4 completes, the
plaintext seed lingers in the install staging folder -- packagers should not
distribute pre-seeded packages over insecure channels.

## What changed vs. the old dual-scope install

The original installer had a `-Scope User|Machine` flag, a
`bootstrap.seed.json` `Scope` field, auto-elevation for Machine scope, a
`registry.scope` marker file next to the binaries, and a runtime
`RegistrySettingsService.DetectScope()` that read that marker to pick ONE
hive at startup. That was replaced by a per-user-only installer plus an
add-in reader (`HklmFirstBootstrapReader`) that probed HKCU then HKLM
per-call.

As of 2026-07-02, HKLM was removed from the read path entirely:

- `-Scope`, `-ReCreateBootstrapRegistry`, `Scope` seed field, `registry.scope`
  marker: all long gone.
- The add-in reader is now `HkcuBootstrapReader` (HKCU-only). The former
  `HklmFirstBootstrapReader` (HKCU-first / HKLM-fallback) was renamed and its
  HKLM branch deleted.
- `install-impl.ps1` Step 4 no longer checks HKLM before writing HKCU.
- `RegistrySettingsService` in `MetaShared` is UNCHANGED -- it still serves
  the erwin-admin tool with its own (scope-aware) single-hive semantics. The
  add-in does not use it for bootstrap reads.

## Operational scenarios

### Fresh user, no seed

1. User double-clicks `install.bat` (or runs `.\install-impl.ps1` from PowerShell).
2. Step 4 finds neither a seed file nor an existing HKCU bootstrap; prompts for
   DBType / DBHost / DBPort / DBName / DBUserName / DBPassword.
3. HKCU is written, password DPAPI-encrypted with `CurrentUser`.
4. erwin DM picks up the add-in on next start, reads HKCU, connects.

### A stray HKLM bootstrap key exists

1. User double-clicks `install.bat`.
2. Step 4 ignores HKLM entirely and follows the HKCU-only decision tree above.
3. The add-in reads HKCU only; any HKLM `...\MetaRepo\Bootstrap` key is inert.

(If you previously relied on HKLM seeding for a machine-wide config, that is no
longer supported - seed HKCU per user, or reintroduce HKLM behind a deliberate
design.)

### Packaged install with embedded seed

1. Packager runs `.\package.ps1 -DBHost X -DBName Y -DBUserName admin
   -DBPassword secret -Zip`.
2. `package.ps1` writes a `bootstrap.seed.json` (plaintext) into the staged
   folder and zips it.
3. Receiver unzips, double-clicks `install.bat`. Step 4 reads the seed file,
   encrypts with DPAPI `CurrentUser`, writes HKCU, deletes the seed file.

### Re-install on a machine with legacy Machine-scope install

1. User double-clicks `install.bat`.
2. Pre-flight detector finds `%ProgramFiles%\EliteSoft\ErwinAddIn` (or HKLM
   COM CLSID).
3. Script aborts immediately, prints the manual-uninstall instructions, exits
   with non-zero status. Nothing is written.

### Re-install on top of an existing per-user install

1. User double-clicks `install.bat` again.
2. Step 1 overwrites `%LOCALAPPDATA%\EliteSoft\ErwinAddIn`.
3. Step 2 re-registers COM (idempotent).
4. Step 3 re-writes the Add-In Manager entry (idempotent).
5. Step 4: seed file check (no), HKCU bootstrap check (yes) -> "Existing HKCU
   bootstrap found. Overwrite? [y/N]". Default `N` leaves the working config
   untouched.

## File / registry surface, fully spelled out

```
%LOCALAPPDATA%\EliteSoft\ErwinAddIn\
    EliteSoft.Erwin.AddIn.dll
    EliteSoft.Erwin.AddIn.comhost.dll
    EliteSoft.Erwin.AddIn.runtimeconfig.json
    ErwinNativeBridge.dll
    (other binaries)
    install-impl.ps1                       (kept for in-place uninstall via install.bat / uninstall.bat)
    install.bat                       (double-click wrapper, forwards to install-impl.ps1)
    uninstall.bat                     (double-click wrapper, forwards to install-impl.ps1 -Uninstall)
    bootstrap.seed.json               (deleted if Step 4 wrote HKCU)

HKCU\Software\Classes\
    CLSID\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}\
        (Default) = "EliteSoft.Erwin.AddIn"
        InProcServer32\
            (Default)       = "<install dir>\EliteSoft.Erwin.AddIn.comhost.dll"
            ThreadingModel  = "Both"
        ProgId\
            (Default)       = "EliteSoft.Erwin.AddIn"
    EliteSoft.Erwin.AddIn\
        (Default) = "EliteSoft.Erwin.AddIn"
        CLSID\
            (Default)       = "{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}"

HKCU\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\EliteSoft.Erwin.AddIn
    (per-machine UI registration; values defined by the script)

HKCU\Software\EliteSoft\MetaRepo\Bootstrap\
    DBType        = "MSSQL" | "PostgreSQL" | "Oracle"
    DBHost        = "<hostname or IP>"
    DBPort        = "<port>"
    DBName        = "<database name>"
    DBUserName    = "<DPAPI(CurrentUser) base64 ciphertext>"
    DBPassword    = "<DPAPI(CurrentUser) base64 ciphertext>"
```

The add-in reads and writes only the HKCU key above. HKLM is not part of the
bootstrap contract anymore.
