# Spike: external invocation of the Elite Soft Erwin Add-In

## Why

Symantec Endpoint Protection on the FIBA prod machine quarantines
`ErwinInjector.exe` with `SONAR.ProcHijack!g47` because the injector
performs the canonical DLL-injection API chain
(`OpenProcess` -> `VirtualAllocEx` -> `WriteProcessMemory` ->
`CreateRemoteThread(LoadLibrary)`). We have no code-signing cert (in
procurement) and cannot get an IT exception in time.

Bypassing the SONAR signature without disguise is not feasible. The
only sustainable answer is to **eliminate the suspicious behavior**:
stop injecting and instead trigger the add-in through erwin's own
menu, the same way a manual `Tools > Elite Soft Erwin Addin` click
would. From outside the process, UI Automation (the accessibility API)
or `PostMessage(WM_COMMAND)` does this with zero AV-flagged calls.

This spike validates that approach against a live erwin instance.

## Files

| Script | Purpose |
|---|---|
| `dump-erwin-uia.ps1` | Read-only UIA tree dump under `c:\tmp\erwin-uia-tree.txt`. Shows where the add-in menu item lives so we know what to target. |
| `invoke-addin-uia.ps1` | Tries three invocation strategies in order (direct UIA, expand-then-invoke, Alt+T keyboard accelerator). Verifies success via file-mtime diff under `%LOCALAPPDATA%\EliteSoft`. |

## How to run

1. Open erwin DM r10 manually.
2. Load any model (the add-in menu item only appears once a model is
   open; this mirrors the current watcher's behaviour).
3. Open a normal PowerShell prompt (NOT elevated; same user that the
   add-in is installed for).
4. Run the dump script first:

   ```powershell
   pwsh -NoProfile -File .\dump-erwin-uia.ps1
   ```

   Inspect `c:\tmp\erwin-uia-tree.txt` and grep for `Elite Soft`. Note
   the `ControlType` and which parent owns it.

5. If the dump does NOT show the add-in entry, open the Tools menu in
   erwin by hand and re-run the dump while the menu stays open. Lazy
   menus only materialize while visible.

6. Run the invoke spike:

   ```powershell
   pwsh -NoProfile -File .\invoke-addin-uia.ps1
   ```

7. Report back which strategy succeeded (or which one got closest)
   and the final stdout.

## Success criteria

- `invoke-addin-uia.ps1` exits 0 AND reports at least one observed file
  change under `%LOCALAPPDATA%\EliteSoft`, AND
- the add-in's normal UI (ribbon/toolbar contribution, popup, etc.)
  appears in erwin within a few seconds.

If only one of those is true, the spike still gives us the data we need
to decide whether to ship UIA-based invocation or fall back to
`PostMessage(WM_COMMAND)` with a discovered command ID.

## What this spike intentionally does NOT do

- It does NOT modify any registry key, scheduled task, or installed
  binary.
- It does NOT load anything into the erwin process.
- It does NOT mutate erwin state beyond opening (and re-collapsing) a
  menu node.
- It is safe to run repeatedly while debugging.
