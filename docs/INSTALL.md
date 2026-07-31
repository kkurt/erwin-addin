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
| Auto-start fallback | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\EliteSoftErwinAddInWatcher` (only when the task cannot be registered) |
| Uninstall credential backup | `%LOCALAPPDATA%\EliteSoft\ErwinAddIn-Backup\Bootstrap-<timestamp>.reg` |
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

### "Key not valid for use in specified state." on model open

This popup (`Add-In Error: Key not valid for use in specified state.`) is a DPAPI
`CryptographicException` from `HkcuBootstrapReader`, raised inside the
`ModelConfigForm` constructor and reported by `ErwinAddIn.Execute`'s catch. It
means the Bootstrap key **exists** (a missing key gives the "No configuration
found" warning instead) but its `DBUserName` / `DBPassword` blobs cannot be
decrypted by the account running erwin. Causes, most common first:

1. The HKCU key was copied between profiles or machines (`.reg` export/import,
   profile copy). DPAPI `CurrentUser` blobs are bound to the user **and** the
   machine.
2. `install.bat` ran under a different Windows account than the one running
   erwin, and the key was then copied across.
3. The account's Windows password was reset by an administrator. On a local
   account that permanently invalidates the DPAPI master key.
4. The profile did not load properly (temporary profile, or a roaming profile
   mid-load over RDP). This variant is intermittent.

The add-in's message names the failing value, the registry path, the account and
the fix. Recovery: run `install.bat` **as the user that runs erwin, on that
machine**, supplying the DB values (`-DBHost/-DBName/-DBUserName/-DBPassword`, or
a `bootstrap.seed.json` next to `install.bat`) so they are re-encrypted with that
account's key.

Since 2026-07-30 `Test-BootstrapConfigured` also probes decryptability, so an
undecryptable key no longer counts as "configured": a plain re-run re-seeds it
instead of silently preserving the broken blob. Before that fix the only recovery
was deleting `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` by hand.

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

Packaging also stamps the build identity into the packaged `install-impl.ps1`
(see [Which build is this machine running?](#which-build-is-this-machine-running)).
The repo copy is never modified, and packaging **fails** if the
`@@BUILD_STAMP@@` placeholder is missing, so a renamed variable cannot quietly
produce packages that all claim to be working-tree builds.

The seed can be **full** or **partial**. Passing any connection field
(`-DBHost`, `-DBName`, `-DBUserName`, `-DBPassword`) requires **both** `-DBHost`
and `-DBName`, or packaging aborts: a half-filled connection seed would silently
drop the baked password at install time (the interactive password prompt has no
default). Passing only `-DBType` and/or `-DBPort` writes a **partial** seed with
empty host/name; at install time those values become the pre-filled defaults for
the interactive prompts (e.g. `-DBType Oracle` for a POC where the target DB
coordinates are entered on the install machine). When `-DBPort` is omitted the
seed carries no port, and `install-impl.ps1` derives the prompt/HKCU default
from the DBType (Oracle 1521, PostgreSQL 5432, MSSQL 1433) rather than assuming
MSSQL's 1433; `HkcuBootstrapReader` applies the same DBType-derived fallback at
runtime for any blank stored port. `HkcuBootstrapReader` treats an empty
host/name seed as "not configured", so a partial seed never yields a broken
runtime config.

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

## Auto-start mechanism, and what happens when it is refused

The watcher does not depend on Task Scheduler in any way. The task's action is
nothing more than
`powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File <watcher>`,
and `autostart-watcher.ps1` never references Task Scheduler. The task is a
launcher, not a dependency, which is what makes a fallback possible.

```
Register-ScheduledTask "EliteSoft Erwin AddIn AutoStart - <user>"  (-Force)
  |
  +-- OK   -> remove any stale HKCU Run entry from an earlier fallback
  |           (each watcher force-kills every other watcher at startup, so two
  |            mechanisms produce a kill race at every logon, not a harmless
  |            duplicate; see scripts/autostart-watcher.ps1:596-608),
  |           then Start-ScheduledTask and verify the process came up.
  |
  +-- FAIL -> print the collected cleanup diagnostics and the recovery steps,
              then re-query the task:
                |
                +-- an enabled task of that name SURVIVED (this is the usual
                |   reason registration failed: it is owned by another
                |   principal). It already launches the refreshed watcher, so
                |   NO Run entry is written and any stale one is removed.
                |   Auto-start keeps running the OLD task definition.
                |
                +-- nothing survived -> write
                    HKCU\...\Run\EliteSoftErwinAddInWatcher (read back to
                    confirm).
              Either way, start the watcher directly for the CURRENT session
              and record the outcome as a run failure.
```

The fallback is announced explicitly, never applied silently. It needs no
administrator rights. Differences from the task: a brief console window flashes
at logon, and the entry is visible and disableable in Task Manager > Startup.

### Why registration gets refused

`Access is denied` (0x80070005) from `Register-ScheduledTask` has three
realistic causes on a corporate machine, and they need different fixes:

1. The task store was hardened and the `Authenticated Users` write ACE removed.
   Check both descriptors, they are separate: `icacls %SystemRoot%\System32\Tasks`
   for the NTFS DACL, and the Task Scheduler service's own root folder DACL via
   `Schedule.Service` -> `GetFolder('\').GetSecurityDescriptor(4)`. A healthy
   machine shows `NT AUTHORITY\Authenticated Users:(CI)(W,Rc)` and
   `(A;CI;FW;;;AU)` respectively.
2. An endpoint-protection persistence rule. The blocked call comes from
   `powershell.exe` creating a task whose action is `powershell.exe`, not from
   `schtasks.exe`.
3. A task of that name already exists and is owned by another principal, so
   this user can neither read, delete nor replace it.
4. The logon trigger names a principal other than the caller. The installer used
   to pass the bare `$env:USERNAME` to `New-ScheduledTaskTrigger -AtLogOn -User`;
   on a domain-joined machine a bare sAMAccountName can resolve to the domain
   account while a local account of the same name exists (or the reverse), and
   setting a logon trigger for a different principal requires privileges a
   non-elevated install does not have. Since 2026-07-30 the trigger and an
   explicit `-Principal` both use `[Security.Principal.WindowsIdentity]::GetCurrent().Name`
   (`DOMAIN\user`), which is unambiguous.

The installer now separates cause 1/2 from cause 3/4 by itself: on failure it
registers a **probe task under an unused name** (no trigger, so it can never
fire) and removes it again. Probe succeeds -> the name is the problem, recovery
is a delete. Probe denied too -> the machine denies task creation to this
account, there is nothing to delete, and the HKCU Run entry is the correct
permanent mechanism. The closing summary says which of the two it was.

Three traps when diagnosing this:

- `schtasks.exe /Create ... /SC ONLOGON` **without `/RU`** means a logon trigger
  for ANY user, which Task Scheduler restricts to administrators by design. It
  returns `Access is denied` on a perfectly healthy machine, so it proves
  nothing. Test with `Register-ScheduledTask`, which is what the installer uses.
- Being a member of Administrators is not the same as running elevated. Under
  UAC the Administrators SID is present but marked deny-only, and
  `[Security.Principal.WindowsIdentity]::GetCurrent().Groups` hides deny-only
  SIDs entirely. Read it from `whoami /groups` and match the SID string
  `S-1-5-32-544`.
- **Never use `Test-Path` to check a TaskCache registry key.**
  `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache` is
  readable by administrators only, and the PowerShell registry provider resolves
  "cannot read" as **`True`**: measured as a normal user on PS 7.6.3 / Windows
  Server 2022, `Test-Path 'HKLM:\...\TaskCache\Tree\<pure garbage>'` returns
  `True` while `Test-Path 'HKLM:\SOFTWARE\<garbage>'` one level up returns
  `False`. Any diagnosis built on it fabricates a remnant on a clean machine.
  Use `Get-Item` and classify the exception instead: found /
  `ItemNotFoundException` = absent / `SecurityException` = unknowable without
  elevation. `Get-ScheduledTaskNameCollisionReport` does exactly this and prints
  "NOT CHECKABLE" rather than guessing.

### Do not run install.bat elevated

A task registered by an elevated process is owned by `BUILTIN\Administrators`.
The `CREATOR OWNER` ACE on the Tasks folder then grants Full Control to a group
that is deny-only in the same user's normal token: the user keeps an explicit
Read ACE but loses write and delete, so every later NON-elevated install fails
at re-registration with `Access is denied`. Verified 2026-07-29 on a task file
created by an elevated run (Read granted, Write denied for its own creator).
The installer warns when it detects elevation. Recovery is an elevated
`schtasks /Delete /TN "<task name>" /F` followed by a normal, non-elevated
install.bat run **as the affected user**. Elevating with a different
administrator account does not help: the install dir, the HKCU config and the
task name are all per-user, so everything lands in that admin's profile.

## Exit codes

`install-impl.ps1` exits non-zero when any step failed, and `install.bat`
forwards `%ERRORLEVEL%` verbatim, so a deployment tool can tell a
half-installed machine from a good one. The closing banner lists every failed
step instead of printing an unconditional "complete!". This covers **both**
paths: install and `-Uninstall` share one `$script:RunFailures` list and one
`Write-ClosingBanner`.

| Code | Meaning |
|---|---|
| 0 | clean |
| 1 | at least one step failed, the closing banner lists which |
| 2 | bad command line, nothing was installed, uninstalled or changed |

## Mistyped switches are rejected, not ignored

`install.bat -Slient` used to run the entire install, report success, exit 0,
and then still stop at "Press any key to exit..." with the switch silently
discarded. `install.bat /Silent` was worse: the slash form is not a parameter
name at all, so PowerShell bound it **positionally** to the first string
parameter, `-DBHost`.

Both come from `param()` being a simple block. `[CmdletBinding()]` would reject
them for free, but it also makes PowerShell intercept `-?` and print nothing,
and `-?` is the documented help switch, so the rejection is done by hand in the
sanity gate just below `param()`:

```
> uninstall.bat -Slient

  ERROR: unrecognized argument: -Slient
         Did you mean -Silent ?

  Nothing was installed, uninstalled or changed.
```

The suggestion is two cheap checks, not fuzzy scoring: an exact name match after
stripping a leading `-` or `/` catches `/Silent`, and comparing sorted letters
catches transposition typos like `Slient`. `-DBPassword` is deliberately exempt
from the leading-character check, since a password may legitimately start with
`-` or `/`, and a stray positional token always fills `-DBHost` first anyway.

## Which build is this machine running?

Every run prints one `build:` line under the opening banner, and `-?` prints it
without running anything, so it is safe to ask for over the phone on a live
machine:

```
> uninstall.bat -?

Elite Soft Erwin Add-In - Installer
===================================
  build: 2026-07-31 14:47 f2c0901+local
```

| Value | Meaning |
|---|---|
| `2026-07-31 14:47 f2c0901` | packaged on that date/time from commit `f2c0901` |
| `... f2c0901+local` | packaged from `f2c0901` **plus uncommitted working-tree changes**, so the commit alone does not describe it |
| `... no-git` | packaged outside a git checkout, only the build time is known |
| `working tree (unpackaged)` | run straight out of the repo, never packaged |

`installer/install-impl.ps1` carries a `@@BUILD_STAMP@@` placeholder that
`package.ps1` rewrites **in the packaged copy only**, so the repo copy stays
placeholder-only and git never sees a generated value. The stamp is copied into
`%LOCALAPPDATA%\EliteSoft\ErwinAddIn` with the rest of the package, so a later
uninstall still reports the build that installed the machine.

This exists because diagnosing a field report cost an extra round trip purely
because nothing in the console output identified the build: a plausible "that
package predates the fix" theory could neither be confirmed nor killed from the
screenshot the customer had already sent.

## Unattended install (-Silent)

```
install.bat -Silent
install.bat -Silent -DBHost srv01 -DBName METAREPO -DBUserName app -DBPassword ...
uninstall.bat -Silent
```

`-Silent` guarantees the run never waits for a human. Use it for mass
deployment, where a "press any key" pause stalls the rollout on every machine.
It composes with every other switch and works for `-Uninstall` too; both `.bat`
wrappers forward their arguments verbatim.

`Test-Unattended` is true when EITHER `-Silent` was passed OR stdin is
redirected. Both signals are needed: the redirection check catches wrappers and
CI shells that never learned about `-Silent`, while `-Silent` covers a
deployment agent that runs the script with a real console attached, where the
redirection check sees nothing unusual and would wait forever.
`[Environment]::UserInteractive` is deliberately not used, because it stays
true for a console process whose streams have been redirected. And note that
`RawUI.ReadKey` reads the console input buffer directly: with stdin redirected
it neither returns nor throws, so a missed ReadKey hangs rather than failing.

What an unattended run does at each point that would otherwise wait:

| Point | Unattended behaviour |
|---|---|
| Any "press any key to exit" | skipped, the exit code is returned immediately |
| "Close erwin and press any key to continue" | **stops** with exit 1, before changing anything. Skipping is not safe here: continuing would wipe a directory erwin holds open |
| DB connection prompts, and nothing supplied | skipped, recorded as a failure. The add-in installs and reports "No configuration found" until you re-run with `-DBHost`/`-DBName` or a `bootstrap.seed.json` |

An unattended run announces itself under the opening banner:

```
=== Uninstalling Elite Soft Erwin Add-In ===
  build: 2026-07-31 14:47 f2c0901
  Unattended run (-Silent): no prompts, no exit pause. The exit code carries the result.
```

Without that line the only evidence `-Silent` took effect is the *absence* of a
pause thirty lines later, so a switch that never bound and a switch that worked
look identical until the very end of the run. The notice also names which signal
triggered it, because redirected stdin turns unattended mode on without anyone
passing `-Silent`.

The DB prompts only ever appear when there is no seed file, no CLI values AND
no existing HKCU config, so a correctly packaged deployment never reaches that
row. It exists so a forgotten seed file produces a failed install with a clear
reason instead of a machine hanging on a prompt nobody can see.

## Uninstall and the DB credentials

Uninstall removes `HKCU\Software\EliteSoft\MetaRepo\Bootstrap` and `Extension`
unconditionally (question-free uninstall, owner decision 2026-07-14).
`-RemoveConnectionInfo` is a no-op kept for CLI compatibility.

That wipe used to be justified by "upgrades re-seed from bootstrap.seed.json",
which stops being true the moment the first install succeeds: the install path
deletes the seed file once HKCU has been written. So after one uninstall,
neither the registry nor the seed file held the credentials and the DPAPI
encrypted user and password were gone for good.

Uninstall therefore exports the Bootstrap key first:

```
%LOCALAPPDATA%\EliteSoft\ErwinAddIn-Backup\Bootstrap-<yyyyMMdd-HHmmss>.reg
restore with:  reg import "<that file>"
```

The export sits outside the install dir that uninstall is about to delete, and
`DBUserName` / `DBPassword` stay DPAPI CurrentUser encrypted, so it is
restorable only by the same user on the same machine. If the export fails the
key is deliberately left in place rather than destroyed.

## Never run install.bat from the install folder

`install.bat`, `uninstall.bat` and `install-impl.ps1` are copied INTO
`%LOCALAPPDATA%\EliteSoft\ErwinAddIn` so uninstall still works after the
extracted ZIP is gone. Running the **install** copy from there would make the
copy source and the install target the same folder: Step 1 empties the target
before copying, so the source would be deleted first and the install would
"succeed" having copied zero files, destroying every binary. `uninstall.bat` is
safe to run from there and remains the reason those files are copied at all.

`Test-InstallPathOverlap` catches three shapes, not just the obvious one:

| Shape | Example | Why it is fatal |
|---|---|---|
| `same` | running the copy in the install folder | source is the wipe target |
| `source-under-install` | package extracted to `...\ErwinAddIn\v2` | `Remove-Item "$installDir\*" -Recurse` takes the whole subtree |
| `install-under-source` | running from `...\EliteSoft` | `Copy-Item -Recurse "*"` copies the destination into itself |

The comparison appends a path separator before `StartsWith`, so a sibling like
`...\ErwinAddIn-Logs` or `...\ErwinAddInBackup` is not mistaken for a child.

## File / registry surface, fully spelled out

```
%LOCALAPPDATA%\EliteSoft\ErwinAddIn\
    EliteSoft.Erwin.AddIn.dll
    EliteSoft.Erwin.AddIn.comhost.dll
    EliteSoft.Erwin.AddIn.runtimeconfig.json
    ErwinNativeBridge.dll
    (other binaries)
    install-impl.ps1                       (kept for in-place uninstall via uninstall.bat)
    install.bat                       (double-click wrapper; NEVER run this copy, see the
                                       "Never run install.bat from the install folder" section above)
    uninstall.bat                     (double-click wrapper, forwards to install-impl.ps1 -Uninstall)

    NOT here: bootstrap.seed.json. Step 1 copies with -Exclude "bootstrap.seed.json"
    so the plaintext credentials never reach a permanent location; it is read from,
    and deleted in, the extracted package folder only.

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
