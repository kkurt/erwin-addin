# Installation and Load Logic Reference

This document describes how Elite Soft Erwin Add-In is installed on a user's
machine, where files and registry entries land, and how the running add-in
discovers its database bootstrap configuration. It is the source of truth for
`installer/install-impl.ps1`, `installer/package.ps1`, and the runtime
`DatabaseService` / `HklmFirstBootstrapReader` C# classes.

## At a glance

- **Single install mode: per-user.** No `-Scope` parameter. Binaries land in
  `%LOCALAPPDATA%\EliteSoft\ErwinAddIn`. COM is registered under
  `HKCU\Software\Classes`. The installer never elevates and never prompts for
  UAC.
- **End user double-clicks `install.bat`** (or `uninstall.bat`); both forward
  to `install-impl.ps1` with `-NoProfile -ExecutionPolicy Bypass` (per-process,
  immune to GPO policy override). No need to open PowerShell manually.
- **HKLM-first bootstrap read at runtime.** The add-in probes
  `HKLM\Software\EliteSoft\MetaRepo\Bootstrap` first; if `DBHost` and `DBName`
  are both present there it uses HKLM (DPAPI scope: LocalMachine). Otherwise it
  falls back to `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` (DPAPI scope:
  CurrentUser).
- **HKCU-only write at install time.** `install-impl.ps1` never writes HKLM. HKLM
  is the property of corporate IT (seeded by GPO, MSI, MetaAdmin, etc.). The
  installer cooperates with whatever IT already put there.

## Install paths (all User scope)

| What | Path |
|------|------|
| Binaries | `%LOCALAPPDATA%\EliteSoft\ErwinAddIn` |
| COM registration | `HKCU\Software\Classes\CLSID\{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}` |
| ProgId | `HKCU\Software\Classes\EliteSoft.Erwin.AddIn` |
| Add-In Manager entry | `HKCU\SOFTWARE\erwin\Data Modeler\10.10\Add-Ins\EliteSoft.Erwin.AddIn` |
| Bootstrap config (write target) | `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` |
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
[4]   Configure MetaRepo bootstrap (HKLM-first; see below)
```

### Step 4 (bootstrap) decision tree

```
Is HKLM\Software\EliteSoft\MetaRepo\Bootstrap populated
  (key exists AND DBHost non-empty AND DBName non-empty)?

  YES  -> Log "HKLM bootstrap detected; skipping HKCU bootstrap write."
          Do NOT touch HKCU. Do NOT prompt.
          The add-in will read from HKLM at runtime.

  NO   -> Does bootstrap.seed.json sit next to install-impl.ps1
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

`HKLM\Software\EliteSoft\MetaRepo\Bootstrap` ALONE is not a legacy-install
signal. Corporate IT often seeds that key separately (without ever running
the old `-Scope Machine` install), and the new installer is designed to
cooperate with that seed.

## DPAPI scope rules

| Source hive | Scope used to Encrypt | Scope used to Decrypt |
|-------------|-----------------------|-----------------------|
| HKLM | `DataProtectionScope.LocalMachine` | `DataProtectionScope.LocalMachine` |
| HKCU | `DataProtectionScope.CurrentUser` | `DataProtectionScope.CurrentUser` |

The install script writes HKCU only, so it always encrypts with
`CurrentUser`. IT or admin tooling that seeds HKLM must encrypt with
`LocalMachine` -- otherwise the add-in's HKLM read decrypts garbage and falls
through to HKCU silently (because DPAPI Unprotect throws and the wrapper
returns the ciphertext as-is).

## Add-in load-time read path

`Services/DatabaseService.cs` instantiates `HklmFirstBootstrapReader` on first
access. The reader implements `IBootstrapService` and resolves the
`BootstrapConfig` like this:

1. Try to open `HKEY_LOCAL_MACHINE\Software\EliteSoft\MetaRepo\Bootstrap`.
2. If the subkey exists AND `DBHost` and `DBName` are both non-empty, read all
   six values (`DBType`, `DBHost`, `DBPort`, `DBName`, `DBUserName`,
   `DBPassword`) from HKLM and DPAPI-Unprotect the encrypted ones with
   `LocalMachine` scope.
3. Otherwise try the same under `HKEY_CURRENT_USER`. DPAPI scope is
   `CurrentUser`.
4. If neither hive yields a valid config, `IsConfigured` returns false and the
   `ConfigContextService` surfaces a warning that includes both candidate hive
   paths.

This split means a single binary works for three scenarios:

- **Personal install** (developer / single user): nothing in HKLM; add-in
  reads from HKCU.
- **Corporate seeded** (GPO / MSI / MetaAdmin pushes HKLM): add-in reads
  from HKLM. Per-user HKCU is ignored as long as the HKLM seed is valid.
- **Migration / override** (admin removes HKLM seed): add-in immediately
  falls back to whatever the user had in HKCU.

## package.ps1 contract

`package.ps1` produces a `.zip` (or staged folder) that contains:

- All `bin\Release\` files from the add-in
- `installer\install-impl.ps1` (this script)
- optional `bootstrap.seed.json` if the packager passed `-DBHost`, `-DBName`,
  etc. at packaging time

There is no `-Scope` flag on `package.ps1`. Every output package is
User-scope; the receiver runs `.\install-impl.ps1` (no arguments needed) and gets
a per-user install.

`bootstrap.seed.json` contains plaintext credentials (the receiver's machine
hasn't run DPAPI yet at package time). `install-impl.ps1` encrypts the values with
DPAPI `CurrentUser` and **deletes the seed file** as soon as the HKCU write
succeeds. If the user aborts the install before Step 4 completes, the
plaintext seed lingers in the install staging folder -- packagers should not
distribute pre-seeded packages over insecure channels.

## What changed vs. the old dual-scope install

The previous installer had a `-Scope User|Machine` flag, a
`bootstrap.seed.json` `Scope` field, auto-elevation for Machine scope, a
`registry.scope` marker file next to the binaries, and a runtime
`RegistrySettingsService.DetectScope()` that read that marker to pick ONE
hive at startup. This forced admins to choose at install time and made
mixed-mode deployments (corporate HKLM + per-user HKCU override) impossible.

The new flow:

- `-Scope`, `-ReCreateBootstrapRegistry`, `Scope` seed field, `registry.scope`
  marker: all removed.
- `RegistrySettingsService` in `MetaShared` is UNCHANGED -- it still serves
  the erwin-admin tool with its single-hive semantics. The add-in does not
  use it for bootstrap reads.
- The add-in uses its own `HklmFirstBootstrapReader` to implement
  HKLM-first / HKCU-fallback per-call. DPAPI scope is derived from the
  source hive each read.

## Operational scenarios

### Fresh user, no IT seeding

1. User double-clicks `install.bat` (or runs `.\install-impl.ps1` from PowerShell).
2. Step 4 finds neither HKLM nor seed nor HKCU bootstrap; prompts for
   DBType / DBHost / DBPort / DBName / DBUserName / DBPassword.
3. HKCU is written, password DPAPI-encrypted with `CurrentUser`.
4. erwin DM picks up the add-in on next start, reads HKCU, connects.

### Corporate user, IT has GPO-seeded HKLM

1. User double-clicks `install.bat`.
2. Step 4 detects HKLM with `DBHost` and `DBName`; logs the values; writes
   nothing to HKCU.
3. erwin DM picks up the add-in; the runtime reader returns the HKLM config;
   DPAPI decrypt uses `LocalMachine`.

### Packaged install with embedded seed

1. Packager runs `.\package.ps1 -DBHost X -DBName Y -DBUserName admin
   -DBPassword secret -Zip`.
2. `package.ps1` writes a `bootstrap.seed.json` (plaintext) into the staged
   folder and zips it.
3. Receiver unzips, double-clicks `install.bat`. No HKLM seed exists, so Step 4
   reads the seed file, encrypts with DPAPI `CurrentUser`, writes HKCU,
   deletes the seed file.

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
5. Step 4: HKLM check (no), seed file check (no), HKCU bootstrap check
   (yes) -> "Existing HKCU bootstrap found. Overwrite? [y/N]". Default
   `N` leaves the working config untouched.

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

HKLM\Software\EliteSoft\MetaRepo\Bootstrap\          (read-only from add-in)
    DBType / DBHost / DBPort / DBName  (plaintext strings)
    DBUserName / DBPassword            (DPAPI(LocalMachine) base64)
```

The HKLM block is shown for reference only; nothing in this repo writes it.
A corporate seeding tool produces it.
