# Lessons Learned

A running log of corrections and non-obvious findings that future sessions
should not have to rediscover. Each entry is a short rule, the reason, and
how to apply it.

## 2026-05-14: Tab-switch matching - use Mart locator stem, never pu.Name

**Rule:** when picking which PU the user just tabbed to among multiple open
PersistenceUnits, NEVER compare `pu.Name`. erwin r10.10 returns
`Name = "Model_1"` (the auto-name) for BOTH the Mart-bound PU and the
side-by-side local-unsaved PU created via File > New. Name-based
disambiguation degenerates to "no PU different" and the fallback
re-binds to PU[0], which is exactly the failure case the matcher is
trying to fix. Use `PuLocatorReader.Read(pu, allowWindowTitleFallback: false)`
to read each PU's locator, normalise to the `Mart://Mart/<path>` stem
(strip optional `erwin://` prefix + any query string) and compare the
stem against the parsed window-title locator. Empty title stem + empty
PU locator -> local PU match. Helper lives at
[ModelConfigForm.FindPuIndexMatchingTitleLocator](../ModelConfigForm.cs)
together with `ExtractMartStem`.

**Why:** verified 2026-05-14 with [TabSwitch] pre-reconnect ground-truth
dumps on a Mart + side-by-side local repro:
```
PU[0] name='Model_1' locator='erwin://Mart://Mart/Kursat/MetaRepo?...'
PU[1] name='Model_1' locator=''
boundName='Model_1' parsedTitleLoc=''  (user is on local PU[1])
-> previous code: "no name-differing PU found" -> ConnectToModel(0)
-> reconnect lands on Mart again, addin stays bound to wrong PU,
   user never sees config refresh
```
After the locator-stem matcher landed, the same scenario produced
`TabSwitch: matched PU[0] by Mart stem 'Mart://Mart/Kursat/MetaRepo'`
on Mart return and `TabSwitch: matched local-unsaved PU[1] (both stems
empty)` on the way back. Round-trip ~140-190 ms.

**How to apply:** any future "which PU is the user looking at right now"
question needs locator comparison, not name comparison. The per-PU
locator path (`pu.PropertyBag().Value("Locator")`) works on r10.10
even though the direct `pu.Locator` accessor throws RuntimeBinderException -
PuLocatorReader's fallback chain handles that transparently. The window
title's bracket content (`erwin DM - [Mart://... : vN : Model]` vs
`erwin DM - [Model1 : <diagram> * ]`) gives the active tab's identity
when parsed through `ReadFromWindowTitle`.

## 2026-05-08: Generate DDL fast-path uses WM_COMMAND Next-loop, not direct InvokePreview

**Rule:** for the Generate DDL same-version "dirty vs last saved" pipeline
in `CallInvokePreviewOnCaptured`, drive the hidden wizard with
`WM_COMMAND CMD_FE_WIZARD_NEXT (1766)` posts. The GA detour fires when
MFC initializes the Preview page and writes `g_lastCapturedDdl`. Do NOT
re-introduce a direct `g_directInvokePreview(self)` call.

**Why:** the direct call to
`FEWPageOptions::InvokePreviewStringOnlyCommand` AV'd at
`mfc140.dll + 0xDBB9` for two days, masked by the WS_EX_LAYERED
compositor flush. The MSVC x64 ABI for the function's CString return
could not be matched without symbol info: the standard sret guess
(`retBuf RCX, this RDX`) produced a different AV at
`EM_EOU.dll + 0x262105` with RDX=0 and broke the GA detour entirely
(no DDL captured). The WM_COMMAND Next-loop sidesteps the ABI question:
it does not call into the C++ method directly - MFC's own page-init
code does, and it has the right `this` because MFC dispatched through
its CPropertySheet message map.

**How to apply:** see
[IPS-CALL CString return ABI sidestepped via WM_COMMAND](../../../.claude/projects/c--Users-Kursat-Repos-erwin-addin/memory/project_ips_call_cstring_abi_pending.md)
for the full code snippet and verification log signals. If a future
attempt to reintroduce direct calls becomes attractive (perf, finer
control), the prerequisite is dia2dump output proving the actual ABI -
no more guessing.

## 2026-05-08: Don't guess MSVC x64 ABI - read the symbol or sidestep the call

**Rule:** when patching a raw-function-pointer call to fix an AV, do NOT
swap the calling-convention shape on a hypothesis without first
confirming the actual ABI. Either run `dia2dump` against the owning
module's PDB, dump the function prologue under a debugger, or pick a
sidestep path (post WM_COMMAND, drive via UIA) that avoids guessing.

**Why:** 2026-05-08 attempted to fix the long-standing
`mfc140.dll + 0xDBB9` AV in `CallInvokePreviewOnCaptured` by switching
from single-arg `InvokeFn(self)` to the classic MSVC x64 sret pattern
`g_directInvokePreview(retBuf, self)` (RCX=retBuf, RDX=this). The
hypothesis was reasonable - `CStringT<char>` is conceptually non-POD -
but the actual lowering on this MFC build returns the CString as an
8-byte pointer in RAX, not via sret. The patched call put retBuf in
RCX where the function expected `this`, the function then dereferenced
RDX (now 0) as `this`, and produced a NEW AV at `EM_EOU.dll + 0x262105`
on entry instead of the old one in unwind. Worse: the GA-detour path
that captures DDL fired BEFORE the original AV but AFTER the new one,
so the patched build returned no DDL at all. User saw "DDL stopped
working" within minutes of installing.

The patch was reverted same session. The single-arg call with
`__try/__except` swallowing the post-return SEH is back. DDL works,
black-rectangle compositor leak works on big-model + click. Net regression
prevented; lesson recorded so the next attempt does not repeat the same
sret guess.

**How to apply:**
- For native ABI work, the sequence is: dump the symbol, read the
  prologue, write the typedef. Skipping straight to step 3 wastes a
  build/install/test cycle and risks shipping a worse failure mode.
- For the IPS-CALL bug specifically, the recommended next attempt is
  WM_COMMAND-driven Preview click (memory
  `reference_alter_script_wizard_automation`) which sidesteps the
  ABI question entirely.
- Memory record: see
  [IPS-CALL CString return ABI fix pending - sret hypothesis FALSIFIED](../../../.claude/projects/c--Users-Kursat-Repos-erwin-addin/memory/project_ips_call_cstring_abi_pending.md)
  for the full ABI evidence dump.



## 2026-05-05: PU.Locator is unreliable on r10.10 Mart-bound PUs

**Rule:** never read `pu.Locator?.ToString()` directly when the model lives
on a Mart server. Always go through `Services/PuLocatorReader.cs`.

**Why:** verified against `Mart://Mart/Kursat/MetaRepo` on r10.10 - the
direct property returned `""` even though the model was loaded and the title
bar showed the full mart path. Downstream services that scope on
`MART_PATH` (ConfigContext, Glossary, NamingStandard, PredefinedColumn,
UdpDefinition) silently misclassify the model as a local-file model and
the add-in surfaces "Active model is not on a Mart server" before any of
them can run.

**How to apply:** the helper performs four cascading reads (direct,
`PropertyBag()`, `PropertyBag(null,true)`, main-window title regex). It logs
the failed layers, so if the user reports an empty locator we can see which
fallback finally caught it. `VersionCompareService.ReadActiveLocator` is the
reference DRY consumer.

## 2026-05-05: Form.Shown can fire on a disposed form

**Rule:** every `Form.Shown` / `Form.Load` / `Form.Activated` handler that
runs as a side effect of `Show()` must guard with `if (!form.IsDisposed)`
before touching the form.

**Why:** when the form's synchronous init path (constructor, Load, or a
connect handler driven from `Show()`) hits a failure and calls `Close()` /
`ForceClose()`, the form is disposed before `Show()` returns. The Shown
event still fires post-dispose, raising `ObjectDisposedException` from
inside our handler. The exception bubbles to `ErwinAddIn.Execute()`'s catch
and is reported as "Add-In Error: Cannot access a disposed object", which
masks the real failure that triggered the dispose in the first place.

**How to apply:** existing audit point is
[ErwinAddIn.cs:157](../ErwinAddIn.cs#L157) where the TopMost-reset handler
already has the guard. Any new lifecycle-event subscription on a long-lived
form follows the same pattern.

## 2026-05-07: Debug Log tab retired; use the file log

**Rule:** there is no in-form Debug Log tab anymore. All log output goes to
`%TEMP%\erwin-addin-debug.log` (path is also exposed as
`AddinLogger.FilePath` and as a clickable "Log file" link on the General
tab). New diagnostic surfaces must follow the same shape: never stream
log lines into a WinForms control.

**Why:** the live-streaming TextBox was the source of the 17:26:32 host
crash (every `AppendText` raised a UIA TextChanged event that NULL-derefed
erwin's UIA proxy, see the rule below). Replacing the streaming with a
"Reload from file" button removed the timer-hot-path AppendText calls
but the tab still hosted ten dev-only spike buttons (DumpCC State,
Normal Alter DDL, Mart-Mart via OnFE, EDR stack-trace toggle, From-DB
probe, REScript probe, REScript cross-version probe, FE alter probe,
Dialog Monitor, Generate DDL via Invoke). Those were never meant to ship
and crowded the layout. The full tab + handlers + Designer entries +
service-layer no-longer-called helpers were removed; the underlying
NativeBridge entry points stay so any future production button can wire
straight into them again.

**How to apply:** new troubleshooting features go into `AddinLogger.Log`
(production-visible) or `AddinLogger.LogDebug` ([Conditional("DEBUG")],
DEBUG-only). Scope timing uses `AddinLogger.BeginScope` which is a no-op
under PACKAGED so the shipped log is event-only, not trace-by-trace.

## 2026-05-07: Never write to a WinForms TextBox from the timer hot path on r10.10

**Rule:** the add-in's `Log()` family must NEVER call `TextBoxBase.AppendText`,
`TextBox.Text = ...`, or any other write that ends up raising a WinForms
UIA event - especially not from anything reachable on
`ValidationCoordinatorService.WindowMonitorTimer_Tick`. File is the only
canonical sink. The Debug Log tab now reads from the on-disk log via an
explicit "Reload" button (`BtnReloadLog_Click` -> `ReloadDebugLogFromFile`).

**Why:** verified crash at 2026-05-07 17:26:32, on a 31-table model (so
the 280-entity diagram threshold is NOT the trigger). The .NET Runtime
1026 stack pinned the cause:
```
UiaRaiseAutomationEvent
AccessibleObject.RaiseAutomationEvent
TextBoxBase.AppendText(string)
ModelConfigForm.Log(string)
ValidationCoordinatorService.Log
ValidationCoordinatorService.WindowMonitorTimer_Tick
```
Every `Log(...)` call appended one line to `txtDebugLog`; that
`AppendText` raised a UIA `TextChanged` event; the broadcast crossed into
erwin r10.10's broken EM_PSF/OLEACC UIA proxy and NULL-derefed at
`coreclr.dll + 0x36852a`. The host process was killed mid-tick. The
`ValidationCoordinatorService` timer fires multiple times per second on
mouse hover paths, so the trigger was not a specific user action - just
"any tick happened to log something".

**`AppContext.SetSwitch` does NOT help.** The legacy-accessibility
switches (`Switch.UseLegacyAccessibility`,
`Switch.System.Windows.Forms.AccessibilityImprovements.UseLegacyAccessibilityFeatures`,
plus `.2` and `.3` variants) were tried 2026-05-07 inside
`ErwinAddIn.Execute` and are still wired there as defense-in-depth, but
the same crash reproduced after they were set. .NET 10 WinForms ignores
them for `UiaRaiseAutomationEvent` calls. Don't trust those switches as
a sole mitigation.

**How to apply:** `Log()` writes to `_addinLogPath` (file) only and
keeps an in-memory `_fullLogText`. There is no streaming path into the
TextBox. `BtnReloadLog_Click` rebuilds the TextBox content with a single
`Text = ...` assignment (one UIA event, user-initiated, while no timer
is competing with it - acceptable risk). Any future "live tail" features
for the Debug Log tab must follow the same pattern: never write per-Log
call, only on user demand. Equivalent rule applies to RichTextBox,
ListBox, ListView, and DataGridView - if you'd be tempted to update them
from a timer, route the data through a file/buffer instead and let the
user explicitly refresh.

## 2026-05-07: Block heavy add-in actions while a modal erwin dialog is open

**Rule:** any add-in action that drives the host process via `NativeBridge`,
synthetic keystrokes (`Ctrl+Alt+T`), `SCAPI` mutations, or anything that
spans more than a few seconds on the UI thread MUST short-circuit when
erwin's main window is disabled by a modal dialog. Use
`Services.Win32Helper.IsErwinMainWindowBlockedByModal()` and surface a
"close the dialog first" warning instead of proceeding.

**Why:** verified crash on 2026-05-07 17:05:29. User had Mart Save open,
switched to add-in, clicked Generate DDL. Sequence:
1. `BtnAlterWizardProd_Click` set `btnAlterWizardProd.Enabled = false`,
2. `NativeBridge.AutoOpenAlterScriptWizard` posted `Ctrl+Alt+T` which the
   modal dialog absorbed (it had focus, not erwin main),
3. `NativeBridge` polled for the wizard for 15 seconds and returned false,
4. Click handler resumed and set `btnAlterWizardProd.Enabled = true`,
5. WinForms 10 raised `UiaRaiseAutomationPropertyChangedEvent` for the
   Enabled-property change,
6. erwin's broken EM_PSF/OLEACC UIA proxy (active diagram ~280 entities,
   memory `reference_em_psf_uia_av_big_model.md`) NULL-derefed inside
   `coreclr.dll` at offset `0x36852a` — `0xC0000005`, process killed.

Three crashes in the same session at the same offset confirmed UIA + the
host's vendor bug, not a CLR or add-in bug. The trigger was concurrent
modal + synthetic keystroke; the underlying NULL deref is the vendor's.

**How to apply:** `Services/Win32Helper.cs:IsErwinMainWindowBlockedByModal`
returns `!IsWindowEnabled(GetErwinMainWindow())`. Both
`BtnAlterWizardProd_Click` and `BtnMartReview_Click` now check it as the
very first line and bail with a Turkish warning. Apply the same guard to
any future button that drives synchronous host work; cheap UI reads (combo
box updates, validation list refresh) do not need it.

## 2026-05-07: Suppress WinForms UIA event raise on add-in load

**Rule:** the very first thing `ErwinAddIn.Execute` does is call
`AppContext.SetSwitch` to flip WinForms accessibility into legacy mode.
This must happen before any `Control` is constructed.

**Why:** even with the modal-dialog guard above, any unrelated UIA event
from add-in form controls (Button click, TextBox focus,
PropertyChanged...) is a crash trigger when the host has a broken UIA
proxy on a 280-entity diagram. The legacy-accessibility switches tell
WinForms to skip the `UiaRaise*` calls entirely, so the broadcast never
reaches erwin's broken proxy. NVDA/JAWS support for the add-in's own
controls regresses slightly, but the alternative is the host process
dying and the user losing unsaved work. Defense-in-depth alongside the
modal guard.

**How to apply:** `ErwinAddIn.Execute` line ~108. Five switches set inside
a try/catch (the add-in must never fail to load because of an
accessibility-switch problem). `Services.AddinLogger` records any failure
but proceeds. Idempotent; safe to re-execute on every Execute call.

## 2026-05-07: Service-load failures must surface to the user, not just the log

**Rule:** when a startup-path data service (`NamingStandardService`,
`GlossaryService`, `PredefinedColumnService`, `DomainDefService`, ...) returns
`IsLoaded=false`, the failure reason must reach a visible UI surface, not only
the debug log. Plumb it through `ModelConfigForm.AddConnectWarning` so it
renders on the General tab Warnings row.

**Why:** `MC_NAMING_STANDARD.OBJECT_TYPE` was renamed to `OBJECT_TYPE_ID` in
admin's 2026-05-04 refactor. The addin's `LoadStandards` query still asked for
the old column and threw `Invalid column name 'OBJECT_TYPE'`. The service
caught the exception, set `_lastError`, returned `false`, and `Log()`'d a
single line. The form's `LoadNamingStandards` re-logged the same message and
moved on. Result: silent regression, no popup, no status, naming validation
silently dead for a week. Detected only when the user happened to grep the
debug log. The DB-shape contract changes more often than any other surface
since admin and addin schemas evolve together; this UI contract must be
load-bearing.

**How to apply:** all four loaders in `InitializeValidationService`
(`LoadGlossary`/`LoadPredefinedColumns`/`LoadDomainDefs`/`LoadNamingStandards`)
now call `AddConnectWarning($"<service>: <reason>")` on `IsLoaded=false` and
on caught exceptions. `_connectWarnings.Clear()` resets at the start of every
connect cycle. Future startup-path services follow the same pattern; the
Warnings row is the canonical spot for "thing X silently failed to load".

## 2026-05-07: Sync init failures must degrade, never ForceClose

**Rule:** when a step on the synchronous startup path (Form.Load handlers, COM
session init, ConfigContext resolution) hits a non-fatal failure, surface the
warning and return cleanly. Do not call `ForceClose()` / `Close()` from inside
that path.

**Why:** `ModelConfigForm` is shown via `_activeForm.Show()` from
`ErwinAddIn.Execute()`. `Show()` pumps `Load -> LoadOpenModels ->
ConnectToModel -> InitializeValidationService` synchronously. Calling
`ForceClose()` mid-pump disposes the form before `Show()` returns; the
post-Load processing then raises `ObjectDisposedException`, which `Execute()`
re-reports as "Add-In Error: Cannot access a disposed object" - the real
reason (e.g. local-file model, no MODEL_CONFIG_MAPPING row) is lost. Verified
against a PowerDesigner-imported local `.erwin` file on 2026-05-07.

**How to apply:** in `ModelConfigForm.InitializeValidationService` the
ConfigContext-failed branch now returns after showing a warning, sets
`UpdateStatus("Connected (no config - validation disabled)")` and lets
`UpdateGeneralTab` render the reason inline. Validation services stay
uninitialized; non-validation tabs (DDL compare, debug log, version compare)
remain usable. Same pattern applies to any future startup-path failure: log,
inline status, no Dispose.

## 2026-05-05: MART_PATH stem must match admin's parser exactly

**Rule:** when extracting the mart path from a locator, use the same regex
as `VersionCompareService.BuildMartLocatorForTarget`:
`Mart://Mart/(?<path>[^?&]+?)(?:[?&]|$)` with `Trim('/')`. The shared
implementation lives in `ConfigContextService.ParseMartPath`.

**Why:** admin's `ModelMappingService.GetByMartPath` does an exact-match
string compare against the value built by `BrowserPanel.BuildPath`
(e.g. `Kursat/MetaRepo`, no leading slash, no trailing slash). The first
draft of `ParseMartPath` only stopped at `?` and only `TrimEnd`'d the
trailing slash, missing two edge cases observed in the wild: locators that
use `&version=N` instead of `?VNO=N`, and a leading slash from
`Mart://Mart//<lib>/<model>`.

**How to apply:** unit test coverage in
[tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs](../tests/ErwinAddIn.Tests/ConfigContextServiceTests.cs)
codifies the seven accepted shapes plus six rejection cases. Add a new
inline data row before changing the regex.

## 2026-05-06: Validation pipeline must be reactive, not periodic

**Rule:** never build the validation pipeline around a periodic full-model
scan. Tie validation work to actual user actions (editor open, model
change events) instead.

**Why:** the original ValidationCoordinator walked all 280 entities * 30
attrs = 8400 attribute properties every cycle, in 5-entity 500ms tick
batches. On the SQL_BUYUKMODEL big model this saturated the STA thread:
each tick was ~450ms of COM work in a 500ms slot, leaving ~10% breathing
room. The user's complaint that "tabloları select edemiyorum" (I cannot
select tables) was the periodic walk. Worst-case popup latency equals
total cycle time (~19s on a 30-entity batch) - no tick-interval tweak
fixes that, only structural change.

**How to apply:** the final design (Phase-2D) is purely reactive. Per-table
silent populate fires when the user opens a Column Editor for that table.
The MonitorTimer scoped path validates only that one entity. Editor
closed = MonitorTimer is idle. Any future "validate everything" feature
must run on user demand (button), not on a timer.

## 2026-05-06: Editor-close popup runs DURING WindowMonitor tick

**Rule:** when a `MessageBox.Show` modal is up, other timers on the UI
thread continue to fire (the modal pumps messages internally). Code that
runs on close-transition cannot assume the popup's outcome has already
been applied.

**Why:** Phase-2C's `DeletePleaseChangeItColumns` ran from
`WindowMonitorTimer.Tick` on close detection. With a popup still showing
the validation FAILED message, the WindowMonitor tick fires, walks the
table for PLEASE CHANGE IT placeholders - finds zero (the rename happens
later when user clicks Yes on popup) - exits cleanly. The renamed
placeholder then survives forever.

**How to apply:** dispatch on observable state at the action site, not on
a follow-up timer. Phase-2D's fix routes the rename/delete decision
inside `ShowConsolidatedPopup` itself, using `_activeColumnEditorTable`
(captured at popup-OK time) to choose: editor-still-open -> rename to
PLEASE CHANGE IT placeholder, editor-closed -> delete directly. No race.

## 2026-05-07: License/anti-tamper on background thread = false positive

**Rule:** never run `LicensingService.Initialize` on a thread-pool worker.
License check must stay on the UI thread.

**Why:** Phase-3C tried to overlap the ~700ms license check with the
SCAPI activation + form constructor by wrapping it in `Task.Run`. The
first paying-user run reported a tampering-detected status and refused
to load. `LicensingService` runs `AntiTamper.CheckGroup1_Debugger` and
`CheckGroup2_Timing` - timing fingerprints and debugger-detection logic
that misfire on a non-UI thread context inside erwin's host process.
The ~250ms saving was not worth the false-positive risk on a security
check that gates all add-in loads.

**How to apply:** keep license validation sequential at the top of
`Execute()`. If startup parallelization is needed, only background
work that is provably context-independent (file I/O, DB queries that
don't touch process state) qualifies.

## 2026-05-07: Discovery loops with COM-property dynamic dispatch are taxes

**Rule:** never write a "try every name" probe loop over COM properties
to discover something at runtime. Each failed `attr.Properties("X").Value`
costs ~50ms (COM exception marshaling). Loops of 10-20 names compound
to 700-900ms of pure tax with no signal.

**Why:** `ReadModelPath` probed 9 PersistenceUnit + 7 root + 3 session
properties looking for a model path string that always fell through to
`root.Name`. Logs showed an 849ms gap between two adjacent log lines -
that gap was the failed-property loop. Removing the loop entirely
reclaimed the time.

**How to apply:** if you genuinely don't know which property holds the
value, run the probe ONCE, log the winning property name, then hard-code
the lookup. Don't ship the discovery loop to production.

## 2026-05-07: User intuition beats premature structural optimization

**Rule:** when the user says "this should be simpler / there must be an
easier way", stop and re-examine the problem from their angle before
adding more layers of indirection.

**Why:** Phase-1A through Phase-2C built a chunked silent populate
pipeline, fingerprint pass, scoped editor scan - all atop the assumption
that the validation pipeline needed a model-wide baseline. The user
asked: "objenin durumu snapshot edilse sadece?" (snapshot only the
selected object). That single sentence was the right architecture -
per-table lazy baseline, no global walk - and was hiding inside the
existing Phase-2C scoped scan code. Took ~5 lines to wire up. The earlier
elaborate work would have been unnecessary if I had understood that
framing earlier.

**How to apply:** before deepening a complex implementation, ask:
"what is the minimum set of objects this user actually cares about
right now?" If the answer is "the one they're editing", scope to that
and skip the global state.

## 2026-05-07: Performance variability hides real wins in single-run measurements

**Rule:** never claim a perf change is a regression based on a single
startup measurement. DB cold-start, COM lazy-init, network jitter
contribute ±1-2s of run-to-run variance on this codebase.

**Why:** Phase-3 optimizations measurably eliminated ~1900ms of work
(ValidationCoord 1470->27ms, MODEL_PATH probe gone, DB pre-warm
overlap), but a single run logged 6766ms vs 6228ms baseline - looking
worse on the surface. The hidden delta was a 1358ms LoadOpenModels
gap and a 1043ms LoadTablesComboBox spike that had nothing to do with
the changes - just cold COM. Without averaging across runs the wins
were invisible.

**How to apply:** when measuring startup, take 3-5 consecutive runs and
report the median (or report the min, since variability is one-sided
upward). Per-component scopes (`AddinLogger.BeginScope`) make the
component-level wins visible even when the total moves around.
