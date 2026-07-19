# Lessons Learned

A running log of corrections and non-obvious findings that future sessions
should not have to rediscover. Each entry is a short rule, the reason, and
how to apply it.

## 2026-07-19: A WP that names "the SEND/SAVE button" can point at either dialog in a two-step flow

**Rule:** WP 319 said "the SEND or SAVE button in the modal after Generate DDL should become
'...AND CLOSE'". The Generate-DDL flow has TWO dialogs: the review dialog (DdlApprovalDialog, button
"Send to Approve" / "Save Model") AND the confirm dialog it opens (ConfirmSubmitDialog, "Submit for
Approval" / "Save the model", OK/Cancel). The button TEXT matched the review dialog, so I mapped the
change there - but the user meant the CONFIRM dialog's OK button (the actual save trigger). I also
built a clarifying question on a wrong premise (approval-off "shows no popup") when in fact the
confirm modal always appears; the question was rejected.

**How to apply:** When a WP references "the button/modal" in a multi-step UI flow, resolve WHICH
dialog before designing - name both candidates back to the user (or ask for the screenshot) instead
of picking by literal button text. The actual commit/action step (here the final confirm OK) is
usually the intended anchor, not the intent step that merely opens it.

## 2026-07-10 (PM): Lazily-populated constraint metadata = windows where the constraint is OFF

**Rule:** TermTypeCanonical was only populated by glossary-validation events (create/rename) and
carried across snapshots. A fresh silent baseline of a PRE-EXISTING column therefore had
canonical=null - and every term-lock check treats null as "no constraint". In that window the
picker opened unlocked and accepted Numeric(555) (field log). If a constraint's activation
depends on metadata, the metadata must be resolvable AT the enforcement site (lazy, cached), not
only at bless-time events. Corollary: one machine's "value is fine by MY rules" (whitelist
IsAllowed early-return) must not advance the shared baseline without asking the OTHER machine
(term policy) - that absorb silently flipped the baseline 5 -> 555 on erwin's delayed re-commit.

**How to apply:** ResolveTermCanonical(snap) lazy dict lookup at enforcement sites (cache "" only
when the glossary is actually loaded); absorb gate at the whitelist pre-read routes lock-violating
diffs through EnforceTermTypePolicy before accepting; ResolveLockedLength is glossary-first.

## 2026-07-10: A locked value needs a DURABLE source, not the last snapshot

**Rule:** the term-type "fixed length" was resolved from the snapshot baseline (prev.PhysicalDataType).
A legitimate transition through a parameterless base (picker allows BIGINT under AMORPH_DATA_TYPE,
and the picker write path bypasses EnforceTermTypePolicy because it advances the snapshot itself)
erased the length from the baseline - the next picker pinned an EMPTY parameter and the policy
reverted to a bare base. A constraint's authoritative value must come from the artifact that
DEFINES it (the glossary term mapping's PHYSICAL_DATA_TYPE), with the snapshot only as a proxy.

**How to apply:** TermTypeLocks.ResolveLockedLength(snapshot, glossary) - snapshot length first,
glossary fallback - at every site that pins/restores the locked length. Do NOT tighten Honors the
same way: the remembered parameterless pick must keep honoring, or the picker re-opens in a loop.

## 2026-07-10: Modal loop labels must be rebuilt every pass - the fix itself can rename the object

**Rule:** the Required-field re-prompt loop reused the fieldLabel string built before the FIRST
prompt. When the user's accepted fix WAS a rename (Physical_Name), the second rule's dialog cited
the old name (AbcDate__592 shown while the grid already said AbcDat). Any loop that shows a dialog
must rebuild identity strings (and sync state.PhysicalName) at the top of each pass, not capture
them once - same family as the live-name-at-display rule from the modal-race work.

## 2026-07-10: A picker's Compose must emit EXACTLY what it validated - no post-validate mutation

**Rule:** AllowedDatatypePickerForm.Compose stripped ALL internal whitespace from the parameter
(`Regex.Replace(p, @"\s+", "")`), added to normalize Standard "10 , 2" -> "10,2". But
ValidateComposition validates the RAW (Trim-only) param for Regex types, so for Oracle
"VARCHAR2(55 CHAR)" the raw "55 CHAR" PASSED the admin regex, then Compose mutated it to "55CHAR"
which FAILS the same regex (`[0-9]+\s+CHAR`, space required). The composed value that got written /
DDL'd diverged from what was validated - a warning fired on a value the user had entered correctly.

**Why:** whitespace is significant for Regex-parametrized datatypes; only whitespace AROUND the
Standard precision,scale comma is cosmetic. A compose step that mutates the parameter after
validation breaks the validate<->emit contract.

**How to apply:** normalize ONLY around the separator comma (`\s*,\s*` -> `,`); preserve every
other internal space. General principle: whatever a picker VALIDATES is what it must EMIT - never
re-normalize a value between the validate call and the write. DataTypeParser already preserves the
length verbatim (group `([^)]*)`), so the picker was the sole strip site. Tests:
AllowedDatatypeMatcherTests Compose cases (55 CHAR, 10 BYTE preserved; "10 , 2" -> "10,2" kept).

## 2026-07-10: One flag must not drive both "which rules fire" and "does Cancel delete" - split them

**Rule:** the `isNew`/`treatAsNew` flag was overloaded: it decided BOTH whether apply=Create
naming rules fire AND whether a Required-popup Cancel DELETES the object (Create) or REVERTS the
property (Update). The user's contract is that ANY real rename (manual Model Explorer F2 /
Properties pane / Column Editor, not just erwin auto-uniquify) re-runs the naming chain on the new
name. Naively setting the one flag true for renames would make Cancel DELETE a pre-existing
column/table/view. So SPLIT into `revalidateAsNew` (validation scope) and `isNew`/`treatAsNew`
(identity). Manual rename => revalidateAsNew=true (rule#1127 fires) but identity=false (Cancel
reverts). The trigger is `NamingValidationEngine.RenameRequiresRevalidation(baseline, current,
placeholderProbe)` - true only for a real, non-placeholder rename (not retroactive on an unchanged
name).

**Why:** columns go through `ValidateColumnNamingStandardCore`; tables AND views share
`TableTypeMonitorService.ValidateNamingStandard` (one method, so split there once + thread a
`revalidateAsNew` bool from the table heartbeat and view rename site). Before splitting, TRACE
every use of the flag and classify each as validation-scope vs cancel/identity - a mis-classified
site either deletes a user's object on Cancel or silently skips a rule.

**How to apply:** never widen an identity/isNew flag to make a rule fire; add a separate
validation-scope flag. Keep discard/Cancel gated on the identity flag. When one method serves
several object kinds, split inside it and inject the per-kind placeholder probe.

## 2026-07-10: A "coverage" claim needs an observer enumeration - name the input signal for EVERY edit path

**Rule:** the inline-edit candidate mechanism (F) covers renames/retype via a Win32 "Edit"
control, but a Properties-pane datatype change via DROPDOWN leaves no "Edit" focus, so it had NO
observer. Closing that needed a selection-scoped fingerprint: read erwin's Overview-pane Static for
the selected entity and fingerprint just that one entity each heartbeat. Cache the Static handle
(one WM_GETTEXT/tick) and BACK OFF when it is not found, or a full child-window enumeration runs
every second on the UI thread (the GetWindowText-hang class).

**How to apply:** for each distinct edit gesture (typed edit, dropdown pick, drag, paste) ask
"what signal fires?" If none, it is unobserved - do not claim coverage. Bound every new detector to
a candidate set (one selected entity), never a model-wide walk.

## 2026-07-09: State captured before a modal is STALE after it - re-read live, and pair every sync with a re-validation

**Rule:** any modal in the validation pipeline pumps the message loop, and erwin's
delayed commits (auto-uniquify '__NNNN' above all) can land during that pump while
both timers are gated. Therefore: (1) never display or validate a snapshot name
after a modal without a live re-read (`ReadLivePhysicalName`); (2) whenever you
sync a snapshot to the live value you MUST also guarantee a validation runs on the
refreshed value in the same flow - sync-without-validate silently absorbs the
change forever (the 'Pre_Abc__1070' rule#1127 bypass), validate-without-sync
re-fires forever.

**Why:** the detection machinery is diff-based (snapshot vs live) with NO event
source. Gating timers during modals is necessary for sequencing but creates a
blind window; the only safe pattern is re-observe-at-the-boundary plus a deferred
targeted recheck (`_attrRecheckQueue`) for commits that land even later. Also
learned: a "coverage" claim needs an observer ENUMERATION - the heartbeat was
count-only and the rename scan walked entities only, so existing-column edits from
Model Explorer F2 / Properties pane had literally zero observers until the
inline-edit-candidate mechanism was added.

**How to apply:** new enforcement dialog? Wrap it in `ShowValidationModal`, call
`RefreshNameAfterModal` after it, and make sure a naming replay covers the
renameCaught case. New write of Physical_Name? `ScheduleAttributeRecheck` after
the commit. Never add a detection path that walks the whole model - match against
in-memory snapshots instead (SelectInlineEditCandidates pattern).

## 2026-06-22: A "forget the model / re-detect" reset MUST clear the COMPLETE disconnect state, not a convenient subset - and never trust an unverified invariant claim

**Rule:** when you add a reset that pushes the form back to "disconnected" so a
reopen re-runs the connect (the DBMS-mismatch / config-less close path:
`_isConnected=false; _lastConnectedLocator=null; _knownLocators.Clear();`), you
MUST also clear **`_globalDataLoaded`**. `ConnectToModel` forks on it:
`if (_globalDataLoaded)` -> fast `ReinitializeForModelSwitch` (model-only) which
SKIPS `ConfigContext.Initialize`; else -> full `InitializeValidationService`
which re-resolves config. A config-resolved model sets `_globalDataLoaded=true`
(ModelConfigForm.cs:672). My partial reset left it true, so after a close the
next model took the switch path, never re-read its config, and the mismatch /
config-less check ran against the PREVIOUS model's STALE config - a false
"Oracle 21c mismatch" on a model that has no config row at all.

**Why:** the connect state machine is a SET of fields
(`_isConnected`, `_globalDataLoaded`, `_lastConnectedLocator`, `_knownLocators`,
`_inDegradedMode`, `_lastDegradedLocator`). The existing disconnect paths
(`HandleSessionLost`, the timer's count-drop/switch-detection at :1196/:1290/
:1346/:1439) clear `_globalDataLoaded` too; the timer's **adopt** path
(`!_isConnected`, ~:1137) does NOT. I reset a subset and assumed the adopt path
would re-resolve config - it doesn't. Worse: the adversarial review I ran
asserted "every tick path that adopts/switches first resets `_globalDataLoaded`"
and cited :1196/:1290/:1346/:1439 - but those are the switch-DETECTION lines,
NOT the adopt path the reset actually routes through. I accepted the claim
without tracing the specific path, so the review "passed" a real bug.

**How to apply:** (1) when mirroring/forcing a state transition, reset the WHOLE
state set the canonical transition resets - diff your reset against
`HandleSessionLost` / the existing disconnect path and match it field-for-field,
don't hand-pick. (2) After any "forget the connection" reset, the next connect
must do a FULL re-resolve: clear `_globalDataLoaded`. (3) When a review (sub-agent
or your own) claims an invariant holds because "path X resets Y", open path X and
confirm line Y is on THAT path, not a sibling path with the same effect elsewhere
- an invariant proof is only as good as the exact lines it cites.

## 2026-06-14: A placeholder/name classifier must test the EXACT variable the caller passes, not a name from a different log line

**Rule:** when you write a name predicate (e.g. `IsPlaceholderViewName`), confirm
the literal form of the string the call site actually feeds in - read the code
path, not a convenient log line elsewhere. erwin's `view.Name` COM accessor
renders a view's auto-name slash as an UNDERSCORE ("V/1" -> "V_1"), while
`Properties("Name").Value` returns the raw slash ("V/1"). The view scan feeds
`view.Name` ("V_1") into the placeholder test, but the `liveValue='V/1'` log
line (from a DIFFERENT `Properties("Name")` read in ValidateNamingStandard
Step-3b) shows the slash. I matched only "V/<digits>", so "V_1" was classified
as a real name and the deferral never engaged - the popup still fired
immediately on the first live test.

**Why:** entities read `Physical_Name` (raw "E/284" slash) so `IsPlaceholderEntityName`
matching "E/<digits>" works; views read `view.Name` (rendered "V_1" underscore),
a different code path with a different separator. Assuming parity without
checking the actual variable cost a full deploy+test cycle.

**How to apply:** accept BOTH separators (`name[1]=='/' || name[1]=='_'`) for
view auto-names; and in general, before shipping a string classifier, grep the
call site and log the exact input once, rather than inferring its shape from an
unrelated diagnostic line. See [[reference_view_defer_like_tables]].

## 2026-06-06: Any MODAL popup added to a per-change validation path MUST take a reentrancy guard, or it loops/stacks ad infinitum

**Rule:** before you make `ValidateColumnNamingStandard` (or any method on the
ProcessAttributeChanges / pending-name / heartbeat path) raise a MODAL dialog
(`RequiredFieldDialog`, `AddinMessageDialog`, MessageBox...), gate it with a
`_xxxInProgress` flag set in a `try/finally` wrapper, and ALSO bail on that flag
at the top of `WindowMonitorTimer_Tick` and in `MonitorTimer_Tick`'s guard list.
The Table path already does this via `_scopedCheckInProgress`; the Column path
did not (it historically only validated `Physical_Name`, which auto-applies or
passes silently, so it rarely showed a modal).

**Why:** a modal pumps the message loop while it is up. The 100 ms
`WindowMonitorTimer` fires during the pump and re-runs the SAME pending-name
rename detection - but the attribute snapshot has not advanced yet (the
`_attributeSnapshots[aid] = snapshot` at the end of the pending-name block only
runs AFTER `ProcessAttributeChanges` returns, i.e. after the modal closes). So
the reentrant tick sees `<default> -> DENEME` again, opens another modal, and so
on. 2026-06-06: the new `COLUMN.Definition` ("comment required") Step-3b made the
column path raise a modal on every inline-add/rename, which surfaced this latent
loop as endless stacked "Kolon Comment alani 0 olamaz" popups (log: the same
`PENDING-NAME ... renamed to 'DENEME' ... -> NamingValidate Column.Definition`
block repeating every ~230 ms).

**How to apply:** wrapper pattern - `if (_columnNamingCheckInProgress) return;
_columnNamingCheckInProgress = true; try { ...Core(); } finally {
_columnNamingCheckInProgress = false; }`. Note `WindowMonitorTimer_Tick` does NOT
check `_isProcessingChange` (only `MonitorTimer_Tick` does), so the window-monitor
needs its OWN bail on the new flag. Confirm the snapshot-advance line in the
pending-name handler runs after the validation returns, not before.

## 2026-06-02: A "COM-RCW lifetime" crash is not "unsolvable / needs a Worker" until you (a) read the dump's native stacks and (b) try deterministic Marshal release

**Rule:** when an in-process SCAPI pipeline crashes erwin with a fatal
`System.ExecutionEngineException` at teardown, do NOT jump to "the COM
lifetime is unfixable, rebuild it out-of-process in a Worker". First:
(1) find the real crash dump (`%LOCALAPPDATA%\CrashDumps\erwin.exe.*.dmp`)
and walk the NATIVE stacks of BOTH the finalizer thread and the faulting
STA thread; (2) check whether the pipeline ever calls
`Marshal.ReleaseComObject` / `FinalReleaseComObject` on the RCWs it
creates - if it does not, that is almost certainly the bug.

**Why:** the From-DB / Review / cross-version teardown EEE was pinned for
weeks as "orphan-PU RCW lifetime, Worker is the real fix", and an earlier
"is it apartment?" test was mis-run (it moved monitoring RESUME to the STA
via BeginInvoke, which changed the wrong knob and 'ruled out' apartment).
The dump (`erwin.exe.29840.dmp`, 2026-06-02) proved the exact mechanism:
the CLR finalizer thread (MTA) drains abandoned SCAPI RCWs via
`RCWCleanupList::CleanupAllWrappers`, cross-apartment-marshals
`IUnknown::Release` onto erwin's main STA, and that Release faults inside
`coreclr!SafeReleasePreemp` on an already-freed object (RDI=0x0BADF00D
heap poison) -> CLR escalates the AV to a FailFast EEE. The whole
in-process pipeline had ZERO COM releases (every SCAPI RCW was abandoned
to GC); the same-version path ships clean precisely because it creates no
2nd-model RCWs. The codebase's own `ValidationCoordinatorService.ReleaseCom`
already does the right thing for the active-model walk - the From-DB path
just never adopted it. So the standard cure (deterministic in-STA release
before teardown frees the natives) was never even attempted before
reaching for the much heavier Worker / separate-Windows-logon design.

**How to apply:** release every SCAPI RCW you create (sessions,
ModelObjects, Root, Collect results AND each per-item proxy in a foreach,
PropertyBags) deterministically, on the STA, BEFORE the native objects are
torn down - reuse `ReleaseComSafe` (ModelConfigForm) /
`ValidationCoordinatorService.ReleaseCom`. Release per-item proxies inside
the loop and the collection/root/modelObjects before `session.Close()`.
Avoid `GC.WaitForPendingFinalizers` on the STA (it can deadlock if a
finalizer must marshal a Release back to that blocked STA). Note
`FinalReleaseComObject(rePU)` (an RCW refcount drop) is NOT the same as
`PUs.Remove` (engine removal that invalidates the active mart root) - the
former is safe, the latter is why Remove was skipped.

## 2026-05-31: Never use System.Windows.Forms.MessageBox - use AddinMessageDialog

**Rule:** every user-facing modal popup must go through
`EliteSoft.Erwin.AddIn.Forms.AddinMessageDialog.Show(...)`, NOT
`System.Windows.Forms.MessageBox.Show(...)`. Same signature, drop-in
replacement.

**Why:** the standard Win32 `MessageBox` chrome (grey title bar,
default system fonts, classic icon glyphs) visually blends with
erwin's own warning / error dialogs. The user cannot tell at a glance
which popup came from our addin vs. from erwin's internal flow, and
the inconsistent look feels stale on Win11. `AddinMessageDialog`
exists exactly for this (introduced 2026-05-15, see its class header
comment in `Forms/AddinMessageDialog.cs`): borderless chrome, Segoe
UI palette, accent stripe matching the icon severity, TopMost
lifetime guard, multi-monitor positioning - so every addin popup has
a recognisable identity.

The rule existed informally since 2026-05-15 but was NOT recorded
here. The success-confirmation modal added 2026-05-31 shipped with a
raw `MessageBox.Show` call; user caught it on the first screenshot.
Formal entry added today to prevent regression.

**How to apply:**

1. NEW popup code: always
   `EliteSoft.Erwin.AddIn.Forms.AddinMessageDialog.Show(owner, body, title, buttons, icon)`.
   Same args as `MessageBox.Show`, same `DialogResult` return.
2. EDITING an existing `MessageBox.Show` callsite for any reason
   (translation, wording change, button set change): swap it to
   `AddinMessageDialog.Show` in the same edit.
3. Do NOT mass-rewrite all remaining `MessageBox.Show` calls in
   unrelated commits - same scope discipline as the UI English rule
   below. A dedicated cleanup PR should grep for `MessageBox\.Show\(`
   across the repo and migrate every direct caller.

## 2026-05-31: UI strings are ALWAYS English (user-facing, not logs)

**Rule:** every user-visible UI string must be in English. This covers:

- MessageBox titles and bodies
- Form / Dialog / TabPage titles (`Form.Text`)
- Button labels, label text, status strips
- ToolTip text, placeholder text
- Inline error / warning messages rendered to the user

NOT covered (free to stay in any language per project convention):

- `AddinLogger` / `Log` / `Debug.WriteLine` calls (backend diagnostics)
- Comments and XML doc (CLAUDE.md allows the author's language for these)
- SQL queries, locator strings, regex patterns, file paths
- Admin DB content shown verbatim (CONFIG names, UDP names, etc - that
  is user-authored data, not our UI copy)
- Chat with the user (continue in Turkish per CLAUDE.md)

**Why:** the addin is deployed to mixed-language teams (Turkish admins,
English compliance reviewers, support engineers reading screenshots).
Mixed-language UI strings make screenshots ambiguous in incident
reports and force translators to handle interleaved language. The
earlier success modal added today shipped Turkish briefly and was
caught in user review the same minute; sweep plus rule formalisation
on 2026-05-31 to prevent regression.

**How to apply:**

1. Before introducing any new UI string, ask: will the END USER see
   this? If yes, write it in English.
2. When editing an existing UI string, take the chance to translate it
   if you spot Turkish.
3. Do NOT mass-rewrite existing Turkish UI strings in unrelated
   commits - keep the PR scoped to the feature you are working on. The
   sweep below tracks the backlog for a dedicated cleanup pass.

> Sweep 2026-05-31 found 26 user-visible Turkish UI strings across 6
> files. Tracked for follow-up cleanup; not addressed in this commit.
> Highest-impact items: `Forms/DdlApprovalDialog.cs:569`,
> `ModelConfigForm.cs:3469`, `Services/ValidationCoordinatorService.cs:3389`.

## 2026-05-31: Mart commit from inside addin = drive ribbon WM_COMMAND, not SCAPI pu.Save

**Rule:** to commit a dirty Mart-bound PU to Mart from inside the
addin, drive erwin's own ribbon Mart > Save flow via WM_COMMAND, NOT
SCAPI pu.Save. The SCAPI path is permanently closed in-process; the
ribbon path is the only one that actually advances the Mart version.

**Why:**

| Path | What happens |
|------|--------------|
| `pu.Save()` bare | Silent LOCAL .erwin file write. Returns True, but Mart version does NOT advance. This is what `[pu.Save destructive]` was always documenting - the destination is local disk, not Mart. |
| `pu.Save(martUri, "OVM=Yes")` | `COMException: Persistence Unit Component ! Mart user interface is active. Only connection established by a user via Mart user interface is available for use via the API.` SCAPI permanently blocks in-process Mart URI save while erwin's Mart UI is up. Memory `[reference_scapi_mart_ui_active_block]`. |
| Ribbon Mart > Save (manual user click) | `MCXModelIncrementalSaveCommand::Write` runs through the description dialog and commits. Mart version advances cleanly. Verified end-to-end 2026-05-31 (v5 -> v6). |
| **WM_COMMAND 1061 to XTPMainFrame from in-process** | Same dispatch path as the manual click - SCAPI block does not apply (it only fires for SCAPI pu.Save calls, not for the native UI command pipeline). Verified working 2026-05-31. |

**How to apply:**

1. `Services/MartSaveAutomation.cs` wraps the full automation chain.
   Public API: `SaveWithDescriptionAsync(erwinMainHwnd, description, timeoutMs, log)`.
2. Cmd id discovery (one-time, captured 2026-05-31 via Ctrl+Alt+C
   recon during a manual save):
   - Ribbon Mart > Save: **1061** (0x425) - stable across erwin
     restarts, observed at 21:37 / 01:30 / 02:48 in wmcmd.log.
   - Description dialog class: **`#32770`** (standard Win32 dialog).
   - Description dialog title prefix: **`Description for `**
     (variable suffix is "'<model>' Version <N>").
   - Description Edit child control id: **1081**, class `Edit`.
   - Save button control id: **1 (IDOK)** - native dialog convention,
     no custom MFC cmd id (a big relief - no second WmCommandLogger
     bootstrap needed).
3. Automation sequence inside `MartSaveAutomation`:
   - `SetWinEventHook(EVENT_OBJECT_CREATE..NAMECHANGE, OUTOFCONTEXT)`
     on the erwin process id. Filters by class + title prefix; race
     guard via `Interlocked.Exchange` on a once-flag (CREATE +
     NAMECHANGE can both fire for the same dialog).
   - `PostMessage(erwinMain, WM_COMMAND, 1061, 0)` from this thread.
   - Hook callback (on whatever thread WinEvent dispatch picks):
     a. `ShowWindow(dialog, SW_HIDE)` BEFORE first paint (zero-flash).
     b. `GetDlgItem(dialog, 1081)` for the Edit child, fallback to
        `FindWindowEx(dialog, NULL, "Edit", NULL)` if the id changes.
     c. `SendMessage(edit, WM_SETTEXT, 0, description)`.
     d. `PostMessage(dialog, WM_COMMAND, IDOK, 0)`.
   - Wait for `EVENT_OBJECT_DESTROY` on the captured dialog HWND =
     commit chain finished. Separate hook on DESTROY so we know
     when to stop waiting.
   - Finally block: unhook BOTH WinEvents; if dialog is still alive
     re-show it with `SW_SHOW` so an interrupted run does not leave
     the user with an invisible-modal-stuck session.
4. Caller (`SaveCurrentModelWithDescription` in `ModelConfigForm.cs`)
   re-probes `pu.IsDirty` afterwards via `VersionCompareService.ProbeDirty()`
   - True post-save means the UI flow completed but the commit silently
   failed; surface that as a hard failure so the queue insert is
   aborted and the popup status strip shows the error.
5. Cmd id 1061 is hardcoded today. If erwin renumbers it in a future
   build, harvest with the existing `WmCommandLogger` pattern (subclass
   XTPMainFrame, user clicks Mart > Save once, persist captured wParam
   to `HKCU\Software\EliteSoft\ErwinAddIn\Watcher\MartSaveCmdId`).
   Same 2-click bootstrap UX the user already accepted for addin
   auto-load (memory `[reference_addin_autoload_postmessage]`).

The earlier attempts removed by this commit:
- `BridgeSetMartSaveDescription` call from `SaveCurrentModelWithDescription`
  (the bridge hook for SetDescription is still installed and useful as
  a research probe; it just is no longer on the critical save path).
- `TryDeleteStaleDuplicatePuSnapshot` helper + Generate DDL fast-path
  call to it. The artifact deletion was a real fix for the
  `+01000000.erwin_isc` collision, but ONLY for the obsolete pu.Save
  code path. With WM_COMMAND we never call pu.Save so the collision
  never occurs - the helper is dead code. If the artifact collision
  ever resurfaces on a different path, the cleanup pattern is in git
  history (commit before this one).
- 2-argument `_currentModel.Save(martUri, "OVM=Yes")` attempt - meta-sync
  pattern that works in a standalone process but is blocked by
  SCAPI's in-process Mart UI check.

## 2026-05-31: Duplicate-PU snapshot artifact blocks pu.Save (error code 42cc3e78)

**Rule:** the Generate DDL hidden Alter Script wizard (the F2-PAIR /
`RunSilentAlterDdlWithServerMs` path in `scripts/native-bridge/native-bridge.cpp`)
creates a session-less duplicate PU via
`PersistenceUnits.Create(...;Duplicate=YES, modelLongId)`, and erwin
writes an open-time `.erwin_isc` snapshot for that duplicate at
`%TEMP%\{ModelLongId}+01000000.erwin_isc`. The wizard close path does
NOT delete it - the file lingers across runs and silently collides with
the NEXT `pu.Save()` of the active model.

`pu.Save` on a Mart-bound PU needs the `+01000000` slot for its own
next-version snapshot during the Mart commit. It calls Win32 delete on
the stale path, fails, and raises a `COMException` with the message:

    Persistence Unit Component ! Failed to delete
    {<GUID>}+01000000 file due to an error with error code 42cc3e78.

This breaks the DDL Review "Send to Approve" Mart-save end-of-flow. The
file is NOT held by a real process handle - `rm` from the shell removes
it cleanly and erwin keeps running normally - it is "dangling artifact +
slot reuse collision" rather than a true lock.

**Why pu.Save destructive memory does not apply here:** the older note
`[pu.Save / SaveEx / SaveToPlatform all destructive]` referred to live
PU teardown (full addin re-init + Mart re-open). This bug is a clean
file-delete failure during the commit; the addin keeps its PU after
`pu.Save` throws, and the queue insert path runs immediately afterwards
in the same session.

**How to apply:**

1. End of every Generate DDL path that drives the hidden Alter Script
   wizard, call `TryDeleteStaleDuplicatePuSnapshot("[ROUTE] <which>")`
   (root-cause sweep - "the run that created the artifact cleans it up").
   Already wired into the same-version fast path in `ModelConfigForm.cs`
   immediately after the busy overlay close in the `finally` block.
2. Start of any code path that calls `pu.Save()` on the active model
   (today: `SaveCurrentModelWithDescription` only), also call the helper
   as belt-and-suspenders so a crash that skipped the pipeline cleanup
   does not break Send to Approve on the next attempt.
3. The cleaner root-cause fix (release the duplicate PU from inside the
   bridge's wizard-close handler) is parked. When picked up, look at the
   F2-PAIR cleanup branch and call `PUs.Remove(dupPu, false)` -
   memory `[reference_rescript_pu_removable]` confirms session-less
   duplicates accept the remove cleanly.
4. The helper uses `PuLocatorReader.Read(pu, true, log)` for the
   locator + a regex on `modelLongId=(\{[0-9A-Fa-f-]+\})`. Mart-bound
   PUs return their full enriched locator (`erwin://Mart://Mart/...
   ?&version=N&modelLongId={GUID}+00000000`) through this reader -
   plain `pu.Locator` returns empty on r10.10 (memory
   `[reference_pu_locator_empty_r10]`).

The helper is best-effort: all exceptions stay inside it. A delete
failure (genuinely locked file) logs the COM-style error and lets
`pu.Save` surface the same COMException to the user - we want the real
error in the log, not a silent mask.

## 2026-05-31: UdpSyncEngine vs DependencySetRuntime ListValues turf war

**Rule:** when computing or applying UDP diffs in `UdpSyncEngine`, treat
"admin defines a List UDP with `ListOptions.Count == 0`" as
"admin delegates list content management to runtime". Skip BOTH the
ListValues compare in `ComputeDiff` AND the `tag_Udp_Values_List` write
in `SetPropertyTypeTags` when the existing model value is non-empty.

**Why:** the addin has TWO writers competing for `tag_Udp_Values_List`:

1. `UdpSyncEngine.Apply` (admin source of truth) - writes admin's
   joined list values.
2. `UdpRuntimeService.UpdateListUdpsFromDependencySet` - writes
   DB-table-derived values whenever `DependencySetRuntimeService` has
   a `TABLE -> UDP` mapping for that UDP.

When admin defines an ASSET UDP with no static options but a dependency
set fills it from `[ASSET]` (3 rows: Asset1, Asset2, Asset3), the writer
fight produces a perpetual "Sync UDP definitions from config?" dialog:

    Diff:    admin=''(0)         vs model='Asset1,Asset2,Asset3'(20) -> "List options changed"
    Apply:   wrote='' (admin)    -> Property_Type cleared
    Runtime: writes 'Asset1,...' (dep-set re-fill, fires within ~1s)
    Save:    persists 'Asset1,...' to Mart (runtime won the race)
    Reopen:  model='Asset1,...' vs admin='' -> SAME diff -> infinite loop

Verified 2026-05-31 against ASSET / MODEL UDP in user's MetaRepo Mart;
log signature (search `UdpSyncEngine.Apply DIAG` + `list values updated
from dependency set` in `%TEMP%\erwin-addin-debug.log`):

    UDP diff DIAG [Model.Physical.ASSET] ListValues:
      admin   (0 chars): ''
      model   (20 chars): 'Asset1,Asset2,Asset3'
      admin opts (0): []
    ...
    UdpSyncEngine.Apply DIAG [ASSET] tag_Udp_Values_List:
      before  (20 chars): 'Asset1,Asset2,Asset3'
      wrote   (0 chars): ''
      readback(0 chars): ''
    ...
    UdpRuntime: Model.Physical.ASSET list values updated from
                dependency set (3 items): Asset1,Asset2,Asset3

**How to apply:**

1. Heuristic `adminUdp.ListOptions.Count == 0` is intentional - a List
   UDP with zero static options is semantically useless without a
   runtime fill source (empty dropdowns help no one). Treating this
   as "runtime-managed" needs no new admin-schema flag and no new
   dependency on `DependencySetRuntimeService` plumbed into
   `UdpSyncEngine`. The trade-off: an admin who genuinely intends a
   List UDP with zero options to STAY empty in the model will see the
   model's existing values preserved instead of cleared. That is the
   less-destructive failure mode and matches the "never delete user
   data without a positive signal" rule.
2. Type / Default / Description diff axes are unaffected - admin still
   owns those even for runtime-filled UDPs (the dropdown content is
   dynamic, but the data type and description are static admin facts).
3. Create path (when the Property_Type does not yet exist) still
   writes the empty list - the model has no value to preserve there,
   and `DependencySetRuntime` will fill it on the next tick.
4. If the codebase ever grows a true `IS_RUNTIME_FILLED` flag on
   `MC_UDP_DEFINITION`, swap the heuristic for that flag (the read
   path is in `UdpSyncEngine.FetchSnapshot`) - the call sites already
   route through `ComputeDiff` + `SetPropertyTypeTags`.

Tests: `UdpSyncEngineDiffTests` adds two guard cases -
`List_admin_with_zero_options_skips_ListValues_diff_when_model_has_values`
proves the loop is broken on the diff side;
`List_admin_with_zero_options_still_flags_Type_drift` proves the
guard does not over-suppress (Type/Default/Description still flow
through for the same UDP).

## 2026-05-23 (b): Naming-rule popups MUST serialize with PromptForMissingRequiredUdps

**Rule:** any code path that calls `RequiredFieldDialog.Show` /
`RequiredUdpForm.ShowDialog` while another popup of the family is
already up will stack two modals on the user. The Windows `ShowDialog`
method pumps the message loop, so the addin's other timers
(`WindowMonitorTimer_Tick` especially) keep firing during the modal
and can drive a second popup-opening code path. Always acquire a
single shared "naming activity in progress" gate before opening a
popup, and have the parallel triggers no-op while it is held.

**Why:** verified on 2026-05-23 against the FibaEmre_SQL config: a new
table with admin's TableClass IS_REQUIRED=true UDP plus a Table.Definition
required-length rule produced both `RequiredUdpForm` and
`RequiredFieldDialog` visible at the same time. The flow:

1. `DiagramHeartbeatTick` per-entity loop hits the new entity.
2. `FireNewEntityPipeline` -> `OnNewEntityDetected` -> `PromptForMissingRequiredUdps`
   opens `RequiredUdpForm.ShowDialog()` (modal #1).
3. While modal #1's message pump is running, `WindowMonitorTimer_Tick`
   fires and sees an inline-edit / editor close transition produced by
   the same user gesture.
4. The close transition calls `RunScopedTableNamingCheck` ->
   `ValidateNamingStandard` -> `RequiredFieldDialog.Show()` opens
   modal #2 ON TOP of modal #1.

`MonitorTimer_Tick` was already protected by `_isCheckingForChanges`;
`WindowMonitorTimer_Tick` was not, and `_scopedCheckInProgress` only
guarded the inner `RunScopedTableNamingCheck` body - not the broader
"a new-entity pipeline is showing its own popup right now" window.

**How to apply:**

1. Wrap `FireNewEntityPipeline` (the entry point that runs
   `OnNewEntityDetected` synchronously) in a set/reset of
   `_scopedCheckInProgress`. While held, the other popup-opening
   paths (`RunScopedTableNamingCheck`, `DiffWatchedPropertiesAndFire`,
   `ScanForRenamesEventDriven`) early-return.
2. The heartbeat's `entitiesToNamingCheck` drain at the end of the
   tick still fires the scoped naming check AFTER the new-entity
   pipeline's modal closes, so deferred work is not lost.
3. If you ever add a third popup type, route its open through the
   same gate. Do not invent a parallel flag - one gate per concern
   keeps the reasoning local.

## 2026-05-23: erwin's UDP setter silently collapses Turkish dotted-I

**Rule:** when comparing strings that have been round-tripped through
erwin's `tag_Udp_Values_List` / `tag_Udp_Default_Value` /
`Definition` setters (any UDP definition field, basically), pass both
sides through `UdpSyncEngine.NormalizeForErwinListCompare` before
calling `string.Equals`. Without it, a byte-Ordinal compare on a
value containing U+0130 'İ' or U+0131 'ı' will diff forever no
matter how many times the user clicks Apply.

**Why:** erwin r10.10's setter for these fields applies a Latin-only
ToUpperInvariant pass somewhere in its MFC layer. The store-side
transformation observed in the live log on 2026-05-23 against the
CLASSIFICATION UDP:

| Admin wrote | erwin stored | Codepoint |
|-------------|--------------|-----------|
| `Kurum İçi` | `Kurum Içi` | U+0130 'İ' -> U+0049 'I' |

Symmetric assumption for the lowercase form (U+0131 'ı' -> U+0069 'i'),
covered in the normaliser but not yet log-verified in a real model -
add a probe if a future bug surfaces on a different character.

The compare bug produces the exact "Sync UDP definitions from config?"
dialog re-appearing on every model open: the diff dialog shows
"List options changed", the user clicks Apply, the metamodel
transaction commits, the Mart save persists the bytes erwin chose to
store (which differ from what we wrote), and the next FetchSnapshot
+ ComputeDiff re-detects the same mismatch immediately.

**How to apply:**

1. Use `NormalizeForErwinListCompare` in any compare against a value
   that came back from a SCAPI read on a string-typed UDP definition
   field. Keep the original (un-normalised) string for display and
   for write-back; only the COMPARE goes through the normaliser.
2. Do NOT normalise on write - the user's authoritative value still
   contains the Turkish character; erwin will quietly strip it but
   that is erwin's bug, not ours to hide on the admin side.
3. When adding a new field-by-field UDP compare, look at what the
   user-reported log says erwin stored for the diagnostic case; if
   it differs from what we wrote by anything OTHER than the Turkish-I
   pair, expand the normaliser there - do not assume the closed set.

Tests: `UdpSyncEngineDiffTests` adds five guard cases - uppercase
list, lowercase list, default value, description, and a genuine
"TTT orphan" drift to prove the normaliser does not hide real
differences.

## 2026-05-17 (revised): Rename detection is EVENT-DRIVEN, not polled

**Rule:** do NOT walk all entities every heartbeat tick to diff
`Physical_Name` against a snapshot. Move rename detection to the
specific user gestures that produce a rename:

1. **Inline edit close** (`_wasInlineEditOpen && !inlineEditOpen`) -
   user double-clicked an entity / attribute name and committed via
   Enter/Tab/click-away. Catches diagram and Model Explorer renames,
   INCLUDING renames of pre-existing real-named entities that the old
   `_pendingNamedEntities` pending-set mechanism missed (it only
   tracked placeholder-named newcomers).
2. **Column Editor close transition** - catches Physical_Name typed in
   the parent-table field on the way out.
3. **Entity Editor close transition** - same for the Table Properties
   dialog.

All three trigger `ScanForRenamesEventDriven(trigger)` which walks the
model ONCE per event (not per tick), diffs current Physical_Name
against `_entityDisplayNameSnapshot`, and fires
`RunScopedTableNamingCheck` on the new name for any diff.

DiagramHeartbeatTick on stable ticks (`delta==0 && entityDelta==0 && !pending`)
now returns immediately after the count read - no per-entity walk at
all. Tick cost on a 286-entity model: ~5 ms instead of 1.5-2 s.

**Why this is safe:** every realistic erwin rename gesture goes
through one of the three boundaries above. The previous walk-every-
tick approach exists in the codebase history because the editor
close-transition diff was missing; the Phase-2G comment ("we now walk
every tick regardless of count delta") was a workaround for that
absence, not a fundamental requirement.

**How to apply when adding a new "X changed" detection:**

1. Identify the user gesture that produces X. Hook the gesture's
   close/commit event.
2. Capture the relevant state at gesture START, diff at gesture END.
3. Only fall back to a periodic walk if the gesture cannot be hooked
   (true event-less SCAPI mutations - rare, only system-driven model
   imports).

The 2026-05-05 "UDP backfill DISABLED" lesson, the earlier
"walk-every-tick drift" lesson, and this one are all the same
underlying anti-pattern: scaling work linearly with model size in a
sub-1-second polling loop. erwin's STA thread cannot absorb that on
real big models.

## 2026-05-17 (superseded): NEVER walk every entity every heartbeat tick. Use edit-session diff instead.

**Rule:** any "per-property drift detection across the model" feature must
NOT be implemented as a `foreach (entity in all)` SCAPI walk inside
`DiagramHeartbeatTick` (or any sub-1-second timer). It scales linearly
with the number of entities AND the number of watched properties; on a
realistic big model (286 entities × ~4 watched SCAPI reads per entity ×
~3 ms per read) tick interval ballooned from 1 s to 3-5 s and the user
reported "addin yavaşladı". This is the same failure mode the 2026-05-05
"UDP backfill DISABLED on big-model evidence" lesson already caught:
per-entity SCAPI reads on every tick freeze erwin's right-click menu
and starve the STA thread.

**Why:** SCAPI reads are dynamic COM property bag lookups - cheap once,
but the cost compounds linearly. There is no caching layer between the
addin and erwin's metamodel, so an N-entity model produces N reads per
property per tick. The heartbeat runs every ~1 second, so the per-tick
budget is in the order of tens of milliseconds, not seconds.

**How to apply (and the actual fix we shipped for the C3 drift case):**

1. **Edit-session diff pattern.** Capture the watched-property snapshot
   when an edit dialog OPENS (`!_columnEditorWasOpen && editorIsOpen`
   transition in `WindowMonitorTimer_Tick`), diff vs current when it
   CLOSES (existing close-transition handler). Cost: O(rules × 1 entity)
   per edit session - bounded by the rule set, independent of model size.
   See `ReadEntityWatchedProperties` + `DiffWatchedPropertiesAndFire`
   in `ValidationCoordinatorService.cs`.
2. **Diagram-only / rename path** stays in the heartbeat because it is
   already event-shaped (responds to entity-count delta and name diff)
   and only does O(1) reads per entity (Physical_Name + Attribute count).
3. **Snapshot mutating writes IMMEDIATELY refresh the snapshot's
   WatchedProperties** in-place (already in the Required-popup write
   path). Without that, a write→read race in the very next tick will
   look like fresh drift and re-fire the popup the user just dismissed
   (verified 2026-05-17 log line "Watched property changed ...
   '' -> 'dbo' - re-running naming check" 1.2 s after a successful fill).
4. **If you MUST scan periodically**: batch with a budget (the
   pre-2026-05-05 UDP backfill did this with `UdpBackfillBatchSize`) or
   throttle to every Nth tick AND profile against a 280+ entity model
   before merging.

The dead-end refactor (kept here as cautionary tale): an earlier
attempt added `EnsureEntitySnapshotSeeded` + `CheckEntityPropertyDrift`
to the per-entity walk inside `DiagramHeartbeatTick`. It "worked" on
small models but the user's 286-entity test surfaced the same
slowdown immediately. The fix was to delete that walk entirely and
move drift detection to the edit-session boundary.

## 2026-05-17 (C3 follow-up): Required forces, non-required warns, AutoApply toasts

**Rule:** the three violation classes have three distinct UX paths:

1. **Required violation** (Step 1 of `EvaluateRule`, fires when `IS_REQUIRED=true`
   and value is empty): show a modal `RequiredFieldDialog` with a TextBox
   so the user can fill the field inline. Apply writes the typed value back
   via `scapiObject.Properties(propertyCode).Value = typed` inside its own
   named transaction; Cancel leaves the violation in the batch where it
   surfaces as a warning later. This is the "user is forced" path - the
   warning is not enough, the user must engage.
2. **Pattern violation** (Step 3, `IS_REQUIRED=false` or non-empty value):
   stays in the consolidated batch popup (`_pendingResults` →
   `ShowConsolidatedPopup`). Warning only, never blocks save.
3. **AutoApply silent fix** (Step 2 wrote a prefix/suffix): emits a
   transient `ToastNotification` in the bottom-right of the addin's
   active screen. 5s lifetime, dismissible. No popup interruption but
   the user sees what happened.

**Why:** earlier model had Required violations going to the same
warning popup as Length/Regexp issues - users dismissed them and the
empty field stayed empty. The 2026-05-17 spec demanded "zorlanmalı" for
Required while keeping non-required violations as just warnings. The
toast handles the third case: AutoApply was silently fixing column
names (e.g. `TEST` → `TEST_DATE`) and the user could not tell what
happened.

**How to apply (addin side):**

1. After Step 3 produces `failures`, split off `f.RuleName == "Required"`
   into its own pass and show `RequiredFieldDialog` per violation:
   ```csharp
   var rc = RequiredFieldDialog.Show(
       title: "Required field",
       message: rf.ErrorMessage,
       fieldLabel: $"{objectType}.{rf.Rule.PropertyCode}",
       out string typed);
   if (rc == DialogResult.OK)
   {
       // Begin tx, write SCAPI, commit, remove violation.
   }
   ```
2. Snapshot writeback: when the property is `Physical_Name` update both
   `state.PhysicalName` and (for table path) the `_entitySnapshots[objectId]`
   dictionary so the next monitor tick does not detect the write as a
   rename loop.
3. The column data-type-change branch (C3 replay) must call
   `ShowConsolidatedPopup` explicitly after `ValidateColumnNamingStandard`
   because pure type changes have no inline-edit close edge.
4. `ToastNotification.Show(title, body)` from auto-apply paths only.
   Thread-safe (marshals through `addinForm.BeginInvoke`); swallows UI
   exceptions so the transactional path is not endangered by a UI
   hiccup. Stack-rendered bottom-up in the addin's active screen,
   `WS_EX_NOACTIVATE` so focus is never stolen.

Live verified 2026-05-17 against the C3 DateTime suffix rule: the user
changed `VARCHAR2(150)` -> `DATE` on a column, suffix `_DATE` was
silently applied (AutoApply=true), toast surfaced the change.

## 2026-05-17 (C3): Naming-rule condition is polymorphic across UDP + built-in property

**Rule:** a naming-standard row can condition on EITHER an admin UDP
(`DEPENDS_ON_UDP_ID` → `MC_UDP_DEFINITION`) OR an erwin built-in
property (`DEPENDS_ON_PROPERTY_DEF_ID` → `MC_PROPERTY_DEF`) - the DB
`CK_MC_NAMING_COND_XOR` constraint enforces "at most one source". The
condition values live in `DEPENDS_ON_PROPERTY_VALUES` as a
case-insensitive CSV (single value = back-compat path; empty CSV with
a source set = "any non-empty value matches").

**Why:** the old single-source UDP-only model forced admins to author
manual UDP shims (`IsDateColumn=Y/N` etc.) just to express rules like
"DateTime columns must end with `_DATE`". Conditioning directly on
`Physical_Data_Type` removes that shim layer entirely.

**How to apply (addin side):**

1. `IsRuleApplicable` dispatches on which FK is set:
   - `DependsOnUdpId` → read via `<OwnerClass>.Physical.<UdpName>` (existing UDP path)
   - `DependsOnPropertyDefId` → read via direct `scapiObject.Properties(propertyCode).Value`
     (built-in properties do NOT use the `Entity.Physical.X` wrapping)
   - Neither → rule is unconditional
2. The CSV IN-match is case-insensitive and trims each token; empty
   tokens between commas are skipped (so `", ,Date"` matches `Date`
   not `""`).
3. SCAPI rejection on the built-in read (same "Entity class does not
   use a property of X type" pattern as `Name_Qualifier` on a fresh
   entity) returns "" - the IN-match short-circuits and the rule skips,
   which is the right behaviour for "entity hasn't reached the gated
   state yet".
4. Defence-in-depth at the loader: any row with BOTH FKs populated is
   skipped + logged. Admin's CHECK constraint prevents this server-side
   but a hand-edited row would otherwise produce confusing dual reads.
5. Diagnostic dump on connect now prints the condition shape as
   `udp[X] in [csv]` / `prop[Y] in [csv]` / `(none)` so admin can read
   off which source each rule is using.

Live tests: 30+ unit tests cover Prefix/Suffix/Length/Regexp dispatch,
IS_REQUIRED gate, and CSV IN-match (single, multi, case, empty tokens,
empty-with-source, empty-with-empty-value). The polymorphic dispatch
itself (UDP vs built-in) is tested at runtime since it requires a live
SCAPI object.

## 2026-05-17: IS_REQUIRED is a per-row flag, not a rule type

**Rule:** the four `RULE_TYPE` values are now `Prefix | Suffix | Length | Regexp`.
"Required" is **no longer a rule type** - the orthogonal
`IS_REQUIRED bit NOT NULL` column gates whether an empty/whitespace
value emits a violation. Engine dispatch: Step 1 = IS_REQUIRED gate,
Step 3 = pattern check on non-empty values (Step 2 = AutoApply, lives
in `ApplyNamingStandards`).

**Why:** admin refactored the schema 2026-05-17. The previous "Required"
kind couldn't combine with a pattern check on the same row, forcing
admins to author "must not be empty AND must start with DM_" as TWO
rows that fight over the same property. Splitting required-ness into
an orthogonal flag lets one row carry "Prefix=DM_, IsRequired=true" and
share its `ERROR_MESSAGE` between the empty-case and the pattern-case
violations. DB CHECK constraint updated to drop "Required" from the
allowed set.

**How to apply:**

1. Step 1 in `EvaluateRule`: if value is null/whitespace and
   `IS_REQUIRED=true`, emit a violation tagged `RuleName="Required"`
   using the rule's own `ERROR_MESSAGE` and return without running
   the pattern check. Empty + IS_REQUIRED=false → skip the entire
   rule (no violation, no pattern check).
2. Step 3: dispatch on `rule.RuleType` switch over the four kinds.
   Never re-introduce a `NamingRuleKind.Required` case - the enum
   does not have it and the loader's `Enum.TryParse` would reject a
   "Required" RULE_TYPE row anyway.
3. Misconfigured rows (Length with NULL `LENGTH_VALUE`, Regexp with
   empty pattern, Prefix with empty `PREFIX`) are silently skipped.
   Admin's `NormalizeByRuleType` + `ValidateByRuleType` already
   filters most at save time; the addin defends against hand-edited
   rows.
4. Only Prefix/Suffix rules participate in `ApplyNamingStandards`.
   AUTO_APPLY on other kinds is forced to false at load time
   (truncating to fit Length or transforming to satisfy Regexp would
   silently corrupt user data; admin enforces this server-side too).
5. The `PLEASE_CHANGE_IT` auto-rename path only triggers when an
   AutoApply Prefix/Suffix rule on `Physical_Name` still fails after
   Step 2 - the "I will fix this for you" promise was broken, so the
   placeholder forces a manual fix.
6. UDP conditioning (`DEPENDS_ON_UDP_ID` + `DEPENDS_ON_UDP_VALUE`)
   applies uniformly to all rule kinds. Empty `DEPENDS_ON_UDP_VALUE`
   means "any non-empty UDP value matches"; a non-empty value is
   compared case-insensitively.

Live verified 2026-05-17: MetaRepo has 2 atomic rows - a Prefix `LOG_`
rule conditional on UDP `LOG=1008` and a Length `>0` rule on
`Column.Definition`. Both have `IS_REQUIRED=0` post-migration, so the
Length>0 rule is currently a no-op on empty values; admin needs to
flip `IS_REQUIRED=1` to recover the old "Required" semantic.

## 2026-05-16 (revised same day): Schema_Ref + Name_Qualifier + Schema is an OBJECT not a string

**Rule:** in erwin, a table's owner is a separate `Schema` SCAPI object,
not a string property on the Entity. The Entity references it via
`Schema_Ref` (write-side: `entity.Properties("Schema_Ref").Value = "DBO"`),
and reads it back via `Name_Qualifier` (derived from the referenced
Schema's Name). Both accessors only work AFTER the entity has been
bound to a schema. On a brand-new entity that has not yet been assigned,
both `Name_Qualifier` and `Schema_Name` throw "Entity class does not use
a property of <X> type or the property failed to satisfy a property
collection filter conditions" - because the property instance does not
exist on the entity yet (the schema reference has never been wired).

**Why:** confirmed end-to-end on 2026-05-16. Probe on EXISTING entities
showed `Name_Qualifier='dbo'` returning cleanly. Runtime on NEW entities
('E/33', 'E/34'→'DDDDDD') triggered the rejection above. Meta-sync's
internal documentation
(`/c/Users/Kursat/Repos/meta-sync/docs/MetaSync-Technical-Internal-EN.md:280-289`)
spells out the actual architecture:
- "In erwin, Owner is a separate object type called `Schema`"
- "Entities reference a Schema via the `Schema_Ref` property"
- "Without creating a Schema object, the Owner dropdown appears empty"
- "`Schema_Name` is a read-only property that returns the name of the
  referenced Schema"

So `Schema_Name` is also a real SCAPI accessor; it just only resolves
when `Schema_Ref` has been set. Probe failed to print it because the
probe only logs accessors that successfully return a value, and on a
schema-less entity neither works.

| DBMS | Target_Server | Confirmed `Name_Qualifier` on bound entity |
|------|---------------|--------------------------------------------|
| SQL Server 2012 | 172 | `'MMS'` |
| Oracle 19c | 174 | `'dbo'` |
| DB2 z/OS 12/13 | 170 | `'dbo'` |
| PostgreSQL 16 | 216 | `'dbo'` |

The misleading clue: `EMXLPropertyAssociations.data` lists
`Entity__has__SchemaName` AND `Entity__has__SQLServerSchemaName` AND
`AbstractEntity__has__NameQualifier`. Only the last gives a usable
universal accessor; the schema-name accessors all flow through
`Schema_Ref`.

The other misleading clue: meta-sync
(`/c/Users/Kursat/Repos/meta-sync/MetaSync/Services/ErwinUserService.cs:73`)
uses `Schema_Name` reads inside a `try {} catch {}` block. The catch
silently swallows the SCAPI rejection on un-bound entities - meta-sync
gets away with it because it iterates entities that have already been
imported from an ODBC DB (Schema_Ref preset). On a fresh diagram-drop
entity those silent swallows would mean every read returns "".

**How to apply:**

1. **For naming-standard rules with `Length > 0`**: treat any SCAPI
   rejection on the configured `PROPERTY_CODE` as an empty string, NOT
   a skip. A brand-new entity with no Owner assigned will reject
   `Name_Qualifier` exactly when the rule should fire. The fix in
   `TableTypeMonitorService.ValidateNamingStandard` Step 3b is:
   ```csharp
   catch (Exception ex)
   {
       propValue = "";  // unset = empty for validation purposes
       Log($"... SCAPI did not surface ... (treating as empty): {ex.Message}");
   }
   ```
2. **For reading the actual schema name in other code paths**: try
   `entity.Properties("Name_Qualifier").Value` first; if it rejects,
   the entity has no Schema_Ref - the answer is genuinely empty.
3. **For writing an Owner via SCAPI**: set
   `entity.Properties("Schema_Ref").Value = "DBO"`. erwin will look up
   or create the matching Schema object. Direct write to
   `Name_Qualifier` does NOT work (it is derived).

**Original (superseded) version of this rule** claimed
`Name_Qualifier` was universally readable. That was only true for
schema-bound entities; freshly-dropped entities show the
"...property collection filter conditions" rejection. The rule above
is the corrected version.

**Why:** verified empirically with [MetamodelPropertyProbeService](../Services/MetamodelPropertyProbeService.cs)
on 2026-05-16 against four DBMS families. Same model copied to all
four target servers; on each, `Schema_Name` was rejected and
`Name_Qualifier` returned the schema value:

| DBMS | Target_Server code | `Name_Qualifier` value |
|------|---------------------|--------------------------|
| SQL Server 2012 | 172 | `'MMS'` |
| Oracle 19c | 174 | `'dbo'` |
| DB2 z/OS 12/13 | 170 | `'dbo'` |
| PostgreSQL 16 | 216 | `'dbo'` |

The misleading clue: `EMXLPropertyAssociations.data` (shipped under
`Program Files\erwin\Data Modeler r10\`) lists
`Entity__has__SchemaName` AND `Entity__has__SQLServerSchemaName` AND
`AbstractEntity__has__NameQualifier`. Only the last one is exposed on
the SCAPI accessor surface. The association table reflects metamodel
relations; not all metamodel properties have a SCAPI accessor.

The other misleading clue: meta-sync
(`/c/Users/Kursat/Repos/meta-sync/MetaSync/Services/ErwinUserService.cs:73`)
uses `Schema_Name` reads inside a `try {} catch {}` block. The catch
silently swallows the SCAPI rejection, so meta-sync's "Schema_Name"
calls actually return `null` on most DBMS - misread as "Schema_Name
works" but it never did.

**How to apply:** before adding a Platform Property row to
`MC_PROPERTY_DEF`, verify the accessor name is one SCAPI accepts on the
target DBMS by reading the object's `Properties` collection (via a
REScript or the debugger). Confirmed universal accessors as of
2026-05-16:

- Entity / Table: `Physical_Name`, `Name`, `Definition`, `Comment`, `Name_Qualifier`
- Attribute / Column: `Physical_Name`, `Name`, `Physical_Data_Type`, `Null_Option_Type`, `Hide_In_Physical`, `Definition`, `Comment`
- Key_Group / Index: `Physical_Name`, `Name`, `Key_Group_Type`, `Is_Unique`
- Model root: `Name`, `Target_Server`, `Author`

Empty Entity / View / Sequence / Subject_Area models cannot be probed -
the test model needs at least one instance of each class for the probe
to read its accessor surface. Drop a stub table + column + index +
view before probing.

## 2026-05-16: SCAPI dynamic property reads are 0.85-3ms each - filter walks aggressively

**Rule:** when walking a `mmObjects.Collect(parent, classKey)` result, only
read per-item properties (`pt.Properties("tag_*").Value`) on items you
actually care about. Iterating a 1500-entry collection and reading 4
properties on every item costs ~18 seconds via COM dynamic dispatch. Reading
only `pt.Name` to filter, then reading details on the small matching
subset, drops the cost to ~1.3 seconds (still mostly the Name read overhead).

**Why:** verified 2026-05-16 against a 1517-entry metamodel during the
UDP sync feature. Before optimisation:
```
[01:04:35.224] UdpSyncEngine.FetchSnapshot: 5 definition(s) loaded
[01:04:53.997] UdpSyncEngine.WalkModelUdps: 1517 Property_Type entries
[01:04:54.010] <<< FetchSnapshot took 18795ms
```
After `WalkModelUdps(namesOfInterest)`:
```
[01:10:36.496] FetchSnapshot: 5 definition(s) loaded
[01:10:37.791] WalkModelUdps: 5 entries read (seen=1517, filter=5)
[01:10:37.800] <<< FetchSnapshot took 1308ms
```
14x speedup. Same Collect pass, same number of items walked, but only 5x4
property reads instead of 1517x4. The walking-only-for-Name cost is the
floor for SCAPI dynamic-dispatch + COM marshalling on this codebase.

**How to apply:** see
[UdpSyncEngine.WalkModelUdps](../Services/UdpSyncEngine.cs) and its caller
[ModelConfigForm.RunUdpSyncIfNeeded](../ModelConfigForm.cs). Any future
metamodel-spanning code that needs only a subset of Property_Types must
take a filter parameter. If two consumers need the same walk, share the
walk result through a struct return (see `ModelWalkResult`) rather than
calling Collect twice.

## 2026-05-16: erwin has no native Boolean UDP - normalize to List at the snapshot boundary

**Rule:** admin's `Boolean` UDP type does not map to any `tag_Udp_Data_Type`
value. Mapping it to Text (2) loses the dropdown UX users expect. The
convention is to surface admin Booleans as `List` with two options
`True,False`. Do the rewrite at the snapshot boundary
(`UdpSyncEngine.NormalizeBooleanToList`) so every downstream layer
(ComputeDiff, Apply, the dialog) stays Boolean-unaware.

**Why:** verified 2026-05-16 with `KVKK` and `PCIDSS` UDPs defined as
`Boolean` in admin's UI. Before the fix, the sync dialog said
"Type: Integer -> Text" and Apply wrote `tag_Udp_Data_Type=2` to the
metamodel; Column Editor showed a free TextBox instead of a dropdown.
After normalisation the dialog says "Type: Integer -> List", Apply writes
`tag_Udp_Data_Type=6` + `tag_Udp_Values_List="True,False"`, and the
Column Editor renders a dropdown.

**How to apply:** any future admin UDP type that does not map to erwin's
metamodel datatype set (1=Integer / 2=Text / 3=Date / 4=Command / 5=Real
/ 6=List) needs the same boundary rewrite. The normaliser is per-row and
runs unconditionally - just add the case.

## 2026-05-16: Cancel-no-state is fine - users get the same diff next open

**Rule:** for sync features that show a "Apply or Cancel" prompt every
model open, do NOT store last-seen state on the model side. Cancel
becomes "do nothing"; the next open recomputes the same diff and shows
the dialog again. Stateless cancel is exactly what users want for
indecisive cases ("I will look at this later") and removes an entire
class of bugs (last-seen drift, CONFIG_ID rebind, fingerprint stale).

**Why:** the original UDP sync plan had a version-counter / fingerprint
mechanism for "I have seen this version, do not bother me again". Two
review iterations with the user (2026-05-15) showed:
1. The fingerprint did not buy any work-skipping - both the admin fetch
   and the model walk happened anyway as part of other connect flow.
2. The cancel-but-pretend-I-applied UX was rejected: users wanted the
   dialog to keep reminding them until they actually decided.
3. Cancel-with-state added 5 edge cases (CONFIG_ID switch invalidates,
   fingerprint hash collisions, where to store, multi-model semantics)
   for no real benefit.

**How to apply:** when in doubt, prefer stateless. Make sure the
diff/snapshot pipeline is cheap enough that recomputing on every open
is acceptable - if it is not, fix the cost (see filtered walk above),
do not bolt on caching.

## 2026-05-16: SCAPI Property_Type.Name iteration is the only fast way to find a UDP

**Rule:** there is no `mmObjects.GetByName("Entity.Physical.OWNER")`
direct lookup on metamodel level. To find a Property_Type by its
canonical name, you walk `mmObjects.Collect(mmRoot, "Property_Type")`
and compare `pt.Name`. ~1.3 seconds for 1500 entries is the floor.

**Why:** verified 2026-05-16 looking for an index-by-name short-circuit
to drop the WalkModelUdps cost below 1.3 s. SCAPI Find / GetByName /
LookupByName methods do not exist on metamodel sessions in r10.10. The
only public access pattern is Collect + iterate.

**How to apply:** budget for the per-walk cost up front. Do not promise
"this will be milliseconds" if you have to find Property_Types by name.
The filtered walk is the practical optimisation; sub-second access
needs an architectural change (e.g. cache Property_Type ObjectIds at
first walk and reuse via the COM-level `GetById` if that exists).

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

---

## 2026-06-07 - UDP Property_Type naming: erwin stores full-path, displays leaf; match by canonical identity not Name string

**Context:** A MetaSync-imported model (Demo/SQL/dev_1/Ek_Kart v1) showed the
"Sync UDP definitions from config?" screen proposing a Create for "TableClass"
even though the UDP already existed in the model. Apply then created a duplicate
`Entity.Physical.TableClass` next to the existing one.

**Root cause (proven, not inferred):**
- erwin's canonical metamodel `Property_Type.Name` for a UDP is the three-part
  full path `<Owner>.<Physical|Logical>.<Leaf>` (erwin API Ref 15.0 sec 4495;
  verified live: a UDP created in erwin's OWN UDP editor serialises to
  `Entity.Physical.ZZPROBE_TABLE` in the .erwin file). erwin's editors DISPLAY
  only the leaf, so a full-path Name still shows cleanly as "ZZPROBE_TABLE".
- MetaSync deliberately runs `RenameCreatedUdpsToLeaf` (a "cosmetic post-pass")
  that overwrites the canonical full path with the bare leaf to mimic MIMB
  import style. erwin tolerates it: owner lives in `tag_Udp_Owner_Type` (GUID
  suffix, e.g. `+40200003`=Entity) and values link by Long_Id, so the leaf form
  fully functions.
- The add-in's `UdpSyncEngine` matched model UDPs by the EXACT Name string
  (`{Owner}.Physical.{Name}`), so a leaf-named import missed and was misread as
  a missing Create.
- The SCAPI VALUE accessor (`obj.Properties("Entity.Physical.X").Value`) is
  owner+scope+leaf and INDEPENDENT of the stored Name label (confirmed across
  4 codebases: add-in, admin, meta-sync, ScapiTest), so naming rules and value
  reads were NOT broken on imported models - only the sync diff's match.

**Fix:** match by canonical IDENTITY, not the Name string. `BuildCanonicalKey`
normalises either form to `{Owner}.{Scope}.{Leaf}`, deriving the owner from
`tag_Udp_Owner_Type` (GUID-suffix OR plain-class form) for bare leaves.
`WalkModelUdps` keys its map on it and detail-reads leaf entries matching an
admin leaf; `Apply` keys `ptByName` on it too so an Update finds a leaf-named
target instead of creating a duplicate. The add-in's CREATE stays full-path
(canonical). Mirrors what MetaSync + erwin-admin already do internally.

**Why (the trap):** I initially leaned "leaf is erwin-native (MIMB)". That was
WRONG - it conflated DISPLAY with STORAGE. erwin STORES full-path. A 6-agent
research workflow + a live experiment (create UDP in erwin's editor, inspect
the .erwin file) settled it. Lesson: when "which convention is canonical?"
decides a fix, get the live ground truth (native-tool output on disk), do not
infer from one tool's behaviour or one comment.

**How to apply:** never match erwin UDPs by the `Property_Type.Name` string
alone - it is convention-dependent (full path vs leaf). Key on
owner-from-`tag_Udp_Owner_Type` + leaf. Reading UDP VALUES is already
name-independent (reconstruct `{Owner}.Physical.{leaf}`), so that path was fine.

## 2026-06-10: Gating on a probe that does not exist on the live system

**What happened:** Phase 4 of the DDL pipeline fix gated the cross-version
compare on VersionCompareService.ProbeDirty. The adversarial review proved the
gate was INERT: none of the probed property names (Modified/IsDirty/Dirty/
HasChanges) exist on the r10.10 PU dispatch surface, so the probe ALWAYS fell
back to "assume dirty" and the gate could never block. The plan checkbox said
"done" while the shipped behavior did not match the promise.

**Why (the trap):** I reused an existing helper because the Save flow calls it,
without checking whether it ever produced a real reading in production logs
(every LogSessionPUs line printed "?" for the same probes - the evidence was
already in the log). A second layer of the same trap followed: the replacement
signal (title asterisk) was verified for one direction only - asterisk-free
means clean held, but asterisk-present did NOT mean Review would accept
(it over-reports right after open).

**How to apply:** before gating any behavior on a probe/signal, (1) grep the
live debug log for proof the probe ever returned a real value in production,
(2) verify BOTH directions of the signal against the authority (here: erwin's
own accept/refuse), and (3) design the gate to fail OPEN toward the authority
with an in-flight detection of the authoritative answer (the refusal-box
detection + Complete Compare relaunch is the pattern that survived).

## 2026-06-11: In-proc UIA on erwin-native dialogs = delayed OLEACC crash

**What happened:** the Type Resolution guard (Compare-step interceptor) clicked
the wizard's Finish via UIA (AutomationElement.FromHandle + ClickButtonByName).
The pipeline SUCCEEDED end-to-end (DDL produced, clean teardown logs), then
erwin crashed ~5s after "pipeline complete" - WER: OLEACC.dll APPCRASH followed
by coreclr 0xc0000005. The codebase already encoded this exact lesson in
ClickDialogButtonByTextWin32's doc: "oleacc IAccessible RCWs crash erwin's
finalizer at teardown" - I used the UIA path anyway because OTHER call sites
(ClickButtonByName on transient MessageBox popups) appeared to work.

**Why (the trap):** "UIA works on standard dialogs here" generalized from
transient OS message boxes to ERWIN-NATIVE wizard windows. We run INSIDE
erwin's process: a UIA client call bridges through OLEACC in-proc and leaves
IAccessible RCWs tied to erwin's windows; the crash fires minutes later at
finalization, far from the call site, so the pipeline log looks perfect.
The "it crashed" report and the guard's own success log pointed at opposite
conclusions - only the WER faulting module (OLEACC) connected them.

**How to apply:** (1) NEVER touch erwin-owned windows (wizards, CC cascade,
Mart dialogs) with UIA from the addin - use the existing pure-Win32 helpers
(ClickDialogButtonByTextWin32, WM_COMMAND dispatch); UIA is tolerable only for
OUR OWN WinForms and transient OS message boxes. (2) When a crash is delayed
past a "successful" pipeline, read the WER faulting module BEFORE re-touching
pipeline logic (feedback_check_memory_before_crash_chase). (3) Before adding a
new dialog interaction, grep for an existing "NO UIA" helper first - if one
exists, its existence IS the warning.

## 2026-06-13: Hardcoded UI strings are ALWAYS English

**What happened:** the UDP deletion-recovery dialog shipped with a Turkish
title/subtitle ("Admin UDP tanimi korunuyor..."), copied from the tone of the
pre-existing "UDP Kilitli" popup. The user corrected: fixed (hardcoded) UI
messages are always English; Turkish appears only in admin-DB-supplied content
(naming-rule messages, etc.).

**Why (the trap):** one pre-existing Turkish hardcoded popup ("UDP Kilitli")
looked like a convention and got copied into five new enforcement popups + a
dialog. A single counter-example is not a convention - the project rule
(CLAUDE.md "Dil") puts all FIXED text in English; per-customer language lives
in admin data.

**How to apply:** any new MessageBox/dialog literal -> English. If a string
needs to be customer-facing Turkish, it belongs in the admin DB (message
columns), not in code. Swept and converted all six existing occurrences
(UDP Locked / UDP Required / recovery dialog) the same day.

---

## 2026-06-19 - Do not invent enforcement gates the user never asked for; verify admin data/query semantics live

**Correction:** for the Datatype Library whitelist I added an `AND DBMS_VERSION.STATUS='ACTIVE'`
gate AND a `DATATYPE_VERSION` per-version join on my own initiative. The user had
defined the type at the DBMS level (DATATYPE_LIBRARY) with the version still DRAFT and
no version link, so my query loaded an EMPTY set and the whole feature was silently
off. The user's report was simply "test ettim olmadi".

**Why (the trap):** I assumed a curation lifecycle (DRAFT = not enforced, ACTIVE =
enforced) that sounded plausible but was never stated. The admin app's OWN query for
"types of this version" (`DatatypeLibraryService.GetDatatypesForVersion`) does not
filter STATUS - so my gate contradicted the source of truth and disabled the feature.

**How to apply:** before enforcing against admin data, READ the admin code that writes
it and/or query the live DB to confirm where/how the value is actually stored, and do
NOT add filters (status gates, version links) the user did not specify. A hidden gate
that yields an empty set looks identical to "feature works, nothing configured" - it
fails silently. Match the admin's own read query. Relates to
feedback_challenge_proposals + feedback_memory_verify_live.

---

## 2026-06-22 - A resolved CONFIG is NOT a proxy for "Mart model"; gate every Mart-driven feature on IsMartModel

**Near-miss (caught by adversarial review, before the user saw it):** the new Integrate
tab gated its visibility on `ctx.IsInitialized && ctx.ActiveConfigId > 0` only. Since
2026-06-13 a LOCAL .erwin file can ALSO be config-initialized (ActiveConfigId set,
MartPath = the local FILE path). `ParseParentFolder` splits on '\' too, so a local file
like `C:\work\Dev\Sales.erwin` yields parent "Dev", which then falsely matched an
ENVIRONMENT.NAME and rendered a confident "in environment Dev" promotion row with an
active button - on a model that is not in the Mart at all.

**Why (the trap):** I treated "has a config" as "is a Mart model." That stopped being
true on 2026-06-13 when local files gained config resolution for validation features.
Every OTHER Mart pipeline in ModelConfigForm already guards `if (!ctx.IsMartModel)`
(BtnMartReview_Click, the Generate-DDL route, the local-model branch that disables the
Mart buttons) precisely because the Mart engines AV on non-Mart PUs - the new feature
was the one that forgot it.

**How to apply:** when adding ANY Mart-driven feature (Review, Generate DDL, Merge,
Integrate, ...), gate it on `ConfigContextService.Instance.IsMartModel`, not just on a
resolved config. ActiveConfigId > 0 proves a config mapping exists, NOT that the model
is Mart-hosted. Mirror the existing `if (!ctx.IsMartModel)` idiom already used by every
other Mart pipeline in the form. Relates to reference_view_no_physical_name (local
models are first-class config citizens now).

---

## 2026-06-23 - "isNew" (snapshot-absence) is NOT "new column" for Model Explorer adds; gate new-vs-existing on _pendingNamedAttrs

**Bug (user-reported, log-confirmed):** adding a column via Model Explorer, the required-field
rule fired ("Comment mandatory") but "Revert Change" did NOT remove the column. Log proved:
the column arrives with placeholder name `<default>`, gets snapshotted + parked in
`_pendingNamedAttrs` (naming deferred); when renamed to its real name it is validated with
`isNew=False` (objectId already in `_attributeSnapshots`), so the Required dialog opened in
UPDATE mode -> Cancel ran `TryRevertAttributeProperty` (blanked the Comment) instead of
`TryDeleteNewAttribute` (discard). Column stayed. Diagram/Column-Editor adds set the real name
immediately -> isNew=True -> CREATE mode -> "Discard New Column" -> deletes, which is why it
"worked from other surfaces".

**Why (the trap):** `isNew` is derived from snapshot ABSENCE. A Model-Explorer column passes
through a placeholder->rename pending flow, so by the time its real name commits it is brand-new
but `isNew=False`. Snapshot-absence and "is a new column the user is still creating" are NOT the
same thing once the pending-name machine is involved.

**How to apply:** for any new-vs-existing DECISION at name-commit time (here: discard vs revert),
OR-in `_pendingNamedAttrs` membership, not just `isNew`. Fix used a SEPARATE
`treatAsNewForRequired = isNew || IsAttributePendingNew(objectId)` ONLY for the required-field
cancel branch - left `isNew` untouched for naming-rule application (it also drives
ApplyNamingStandards new-vs-existing). `_pendingNamedAttrs` is ONLY ever populated on the
`isNew && placeholder-name` path, so it can never contain a real existing column (no false-delete).
SECONDARY structural lesson: when a deeply-nested validation method DELETES the COM object
(TryDeleteNewAttribute), the deletion must propagate UP (return bool) so every caller skips its
post-work (EnforceAllowedDatatypeWhitelist, snapshot re-add, locked-UDP enforcement) - otherwise
they touch a dead COM object. Mirror the existing isNew=True mid-foreach delete (proven safe).
Relates to reference_view_defer_like_tables (the pending-name machine) + feedback_rules_new_objects_only.

---

## 2026-06-23 - "DB unreachable" is NOT "no config mapping"; never take a destructive action on a config-resolution failure without distinguishing the two

**Bug (user-reported, log-confirmed, MY regression):** on one user's machine, opening a Mart model
gave NO validation popups. Log: `ConfigContext.Initialize error: Key not valid for use in specified
state.` (a DPAPI CryptographicException decrypting the config DB password) -> `Config not resolved ...
(path='')` -> `Config not resolved for a Mart-bound model (no CONFIG mapping) - warning + closing.`
-> the model was force-closed, validation off for the session. Worked on Kursat's account because
DPAPI decrypts there. The earlier DBMS-governance work's config-less-CLOSE logic
(`InitializeValidationService`) fired on ANY `ConfigContext.Initialize` failure for a Mart model,
conflating "DB reachable, mart path has no MODEL_CONFIG_MAPPING row" (genuinely config-less, close
is intended) with "couldn't even reach/decrypt the config DB" (we do NOT know if a mapping exists).

**Why (the trap):** a boolean "did config resolve?" hides WHY it failed. Closing the user's open
model (possible unsaved work) is destructive and must only happen when we are SURE the model is
config-less - which we are NOT when the DB was unreachable. DPAPI `CurrentUser` blobs are per-user
and per-machine, so a password encrypted under one account/profile throws "Key not valid for use in
specified state" on another (or transiently during an RDP/roaming-profile load) -> environment-specific
breakage that the author can never reproduce.

**How to apply:** before any destructive reaction to a config-resolution failure, distinguish DB-access
failure from genuine no-mapping. The clean signal already existed: `ConfigContextService.Initialize`
sets `LastErrorPath` to the resolved path ONLY on the no-mapping branch; every DB-access/crypto/
unconfigured failure leaves it null (the catch never assigns it; `LookupConfigId` opens the connection,
so a throw there precedes the path assignment). Fix: close only when
`resolvedButUnmapped = !string.IsNullOrEmpty(LastErrorPath)`; on a DB-access error keep the model OPEN,
degrade, surface a clear "configuration database unavailable" message (not "register this path"), and
let the user reopen/Reload-Config. General rule: gate every destructive add-in action (close model,
delete object, mass-rewrite) on a POSITIVE confirmation of the precondition, never on the mere absence
of success. Relates to feedback_no_silent_fallback + project_corporate_flow.

---

## 2026-06-23 - Read a model's DBMS from the model's own properties, NEVER by scraping erwin's status bar / window captions

**Bug (user-reported, log-confirmed, MY regression):** opening a Mart model whose mart folder is named
"Sql Server Models" popped a false "Model / Configuration DBMS Mismatch" and CLOSED the model, even
though the config said "SQL Server 2016/2017" and the model targets exactly that (the status bar showed
it). Log: `ReadDbmsFromErwinStatusBar: matched ... text='Mart://Mart/FibaBenzerleri/Sql Server
Models/FIBA-TEST : v1 : KKR'` then `DBMS mismatch: config='SQL Server 2016/2017' vs model='Mart://...
/Sql Server Models/...'`. `ReadActivePuTargetServer` tried the status bar FIRST; `LooksLikeDbmsLabel`
used `Contains("sql server")`, which matched the brand keyword sitting inside the FOLDER NAME of the MDI
title, so the add-in compared the config DBMS against the LOCATOR.

**Why (the trap):** erwin's status bar is an XTP custom-painted control that GetWindowText usually
cannot read, so the code brute-force enumerated child windows looking for a "DBMS-looking" string - and
a model's own MDI caption (a `Mart://.../<folder>/<model>` locator) is a child window whose text can
contain a DBMS brand word by coincidence of folder naming. Screen-scraping UI for authoritative data is
inherently fragile; the user's reaction ("pencere basligi neden?! model uzerinde zaten yaziyor,
konfigde de yaziyor") was exactly right.

**How to apply:** the model's DBMS is in the model's OWN data - SCAPI PropertyBag `Target_Server`
(brand id) + `Target_Server_Version` (engine major) - and the config's DBMS is in the admin DB
(ConfigContextService.DbmsLabel). Compare THOSE. Removed the status-bar path entirely
(ReadDbmsFromErwinStatusBar + LooksLikeDbmsLabel deleted); ReadActivePuTargetServer is now
PropertyBag-only, composed via the unit-tested DbmsLabelComposer. CheckDbmsMismatch already fail-safes
(either label blank -> NO mismatch), so an unreadable model never closes. General rule: never derive a
correctness-critical fact by scraping another app's window text/titles/status bars when the underlying
structured data is available from its API. Relates to reference_uithread_getwindowtext_hang +
the 2026-06-23 "positive confirmation before destructive action" lesson.

## 2026-06-23: A shared MetaShared interface member that the admin added breaks the add-in build until AddInPropertyMetadataService implements it - and never trust an Explore agent's claimed enum/struct values; read the live code

**Rule:** When the build fails with `CS0535 '<addin class>' does not implement interface member 'IPropertyMetadataService.<X>'`, do NOT assume your edit caused it. `IPropertyMetadataService` lives in the referenced `..\erwin-admin\MetaShared\MetaShared.csproj` (ProjectReference). The admin team adds members there (e.g. `List<ObjectRelation> GetRelations(int fromObjectTypeId)` for MC_OBJECT_RELATION); the add-in's `Services/AddInPropertyMetadataService.cs` then fails to compile until it implements them. Implement the member in the ADD-IN class (allowed - it is the add-in's impl), never edit MetaShared (admin-owned). Second rule: an Explore/agent summary can FABRICATE concrete values - one claimed `NamingRuleKind.Prefix = 45, Suffix = 47, ...`; the real enum has plain implicit values (0,1,2,...). Always open the live source before relying on an exact value/signature.

**Why:** MetaShared is the shared contract assembly between erwin-admin and erwin-addin. The Template naming feature was added on BOTH sides: admin added the `ObjectRelation` EF entity + `GetRelations` interface method + DbSet, so the moment the add-in references the updated MetaShared, its `IPropertyMetadataService` implementation is incomplete. This looks like "I broke the build" but is really "the contract grew." The enum-value hallucination wasted no time only because I verified against the file; relying on it would have produced a wrong/confusing enum edit.

**How to apply:** On an unexpected interface-not-implemented error, `grep -rn "interface I<Name>"` - if it is not in this repo, it is in MetaShared; read it, implement the missing member in the add-in impl class consistent with the sibling methods (EF via `CreateContext()` + `Include`/`OrderBy`). For runtime hot-path reads of an MC_* table, mirror `NamingStandardService` (cached raw-ADO via `DatabaseService` + `SqlDialect`, NOT EF) to avoid the EF cold-start; the EF interface method is the admin-parity/contract read. Reuse existing predicates by widening visibility (`NamingValidationEngine.IsRuleApplicable` made public) instead of reimplementing condition/ApplyOn logic.

## 2026-06-23: "Template" naming rule type is a value GENERATOR, wired into the per-column lifecycle hook (not a model walk), no-fallback on token resolution

**Rule:** The new `RULE_TYPE='Template'` is unlike Prefix/Suffix/Length/Regexp/Required: it does not validate a name, it RENDERS a target property's value from `VALUE_TEMPLATE` (tokens `{PropertyCode}` = same object, `{Alias.PropertyCode}` = related object via `MC_OBJECT_RELATION`) and writes it. It runs from `ValidationCoordinatorService.CheckEntityForChanges` per column (create moment = placeholder->real commit, or update), gated by the SAME `MatchesApplyOn` + `IsRuleApplicable` predicates, NEVER a full-model walk. `TEMPLATE_FILL_MODE` Always vs OnlyIfEmpty; AUTO_APPLY=true silent / false Yes-No confirm (mirrors naming). NO-FALLBACK: any unresolvable token (`NamingTemplateEngine.Render` throws `TemplateResolutionException`) skips the write and logs `ERROR_MESSAGE` - a half-rendered value never reaches the model. Idempotent: skip when rendered == current.

**Why:** Template generation could be (mis)read from the admin spec's "iterate all objects of the type" as a bulk model pass; that would violate the project's no-full-walk + rules-apply-to-new/changed-only invariants and could overwrite the whole model. The lifecycle hook already has the owning entity in scope, so `{Table.Physical_Name}` resolves with no reverse lookup. v1 ships COLUMN.Definition (example 1); TABLE.PrimaryKey is deferred because identifying WHICH `Key_Group` is the PK needs a live SCAPI discriminator probe (writing `kg.Properties("Name")` is proven, selecting the PK among an entity's key groups is not).

**How to apply:** Pure grammar/fill-mode logic is in `Services/NamingTemplateEngine.cs` (unit-tested, SCAPI-free); the global alias catalog is `Services/ObjectRelationCatalog.cs` (cached raw-ADO, reloaded next to naming standards only when a Template rule exists). To extend to TABLE.PrimaryKey or other object types, add a "target writer / relation navigator" adapter; the engine + catalog are generic. Do not route Template through the name-validation path - `EvaluateRule` has a deliberate `case Template: break;` no-op so it never emits a false violation.

## 2026-06-24: Model-name naming rules never fired on a Model Explorer inline rename - the model validator existed but was wired ONLY to the Model Editor dialog-close edge

**Rule:** When "validation works in the editor but not in Model Explorer" is reported for an object type, check WHICH lifecycle edge the validator is wired to. The model validator `ValidateModelOnEditorClose` (validates MODEL.Name regex/prefix/required + MODEL.Definition required, writes Required fields via RequiredFieldDialog with a regex re-prompt loop) was only triggered on the "Model 'X' Editor" DIALOG open->close transition. A Model Explorer inline label rename of the MODEL node never opens that dialog, so no model check fired - the exact model-level analog of the earlier column-add-via-Model-Explorer bug. Fix: a `ScanForModelRenameEventDriven` that runs on the SAME inline-edit-close edge (`_wasInlineEditOpen && !inlineEditOpen`) that already commits entity/column/view inline renames, comparing `root.Name` to an instance-field baseline `_modelNameSnapshot` and calling the existing validator on a change.

**Why:** erwin's in-place inline editor is one object-type-agnostic Win32 "Edit" control (`Win32Helper.IsInlineEditActive`), so the inline-edit-close edge is the canonical commit point for ALL Model-Explorer-originated renames (table, column, view, AND the model node). It provably does NOT fire on an MDI tab switch (a tab switch focuses an MDI child, never an "Edit" control), so reusing that edge sidesteps the rename-vs-switch ambiguity that a window-title or heartbeat approach has (a model rename ALSO changes the window title, so `CheckForModelChanges` can't distinguish them). A fresh `ValidationCoordinatorService` is created on every connect (`ModelConfigForm.InitializeModelServices`), so the instance-field snapshot is always for the current model - no cross-model staleness, no ObjectId keying needed.

**How to apply:** For a new top-level object's rename validation, hook the inline-edit-close edge alongside `ScanForRenamesEventDriven`/`CommitPendingViews`, baseline the name in `StartMonitoring` (before the timers start), guard with `_sessionLost || _disposed || _validationSuspended || _columnNamingCheckInProgress` (the modal pumps the loop and re-fires timers; the validator sets `_columnNamingCheckInProgress` before any dialog so both timers bail), and advance the baseline BEFORE validating then refresh it AFTER (the validator may write a corrected name). Evidence to confirm such a gap: the rule dump shows `[Regexp] MODEL.Name` etc. loaded, but `NamingValidate:` log lines appear only for Column/Table/View, never Model. Note (UX): the validator checks ALL model properties, so a name-only rename also surfaces a Required Definition dialog if Definition is empty (rare - Definition is validated at connect); consistent with the editor-close path.

## 2026-06-24: A "Revert Change" cancel on a generic editor-close validator does nothing unless it is given the prior value to revert to

**Rule:** `ValidateModelOnEditorClose` (and validators written for an "editor close" event) prompt for a Required/violating value but, on Cancel/"Revert Change", historically just logged "left as-is" and broke - they have NO concept of a prior value, so the rule-violating text the user typed stays. When such a validator is reused on a RENAME edge, the caller must pass the pre-rename value so the cancel branch can write it back. Fix: `ValidateModelOnEditorClose(string nameRevertValue, bool nameOnly)` - on `isNameCode && nameRevertValue != null` cancel, write `nameRevertValue` back in a `BeginNamedTransaction` (mirror the forward-write: `try root.Properties(writeAccessor).Value = x; catch root.Name = x;`); `ScanForModelRenameEventDriven` passes `oldName = _modelNameSnapshot` (captured BEFORE advancing the snapshot) and `nameOnly: true`. The no-arg editor-close caller keeps the old "left as-is" behavior via the `null` default.

**Why:** This is the SAME bug class as the column "Revert Change doesn't undo the add" (2026-06-23): a revert/cancel path that does not actually restore prior state. The user hit it on the model the moment the rename validator started firing. `nameOnly` also resolves the earlier reviewer NIT-4 (a name-only rename should not drag in a Required Definition prompt). Snapshot bookkeeping is advance-before-validate / refresh-after so neither a revert nor a successful re-prompt fill re-fires the scan (root.Name == snapshot next tick).

**How to apply:** When wiring an existing validate-on-close routine onto a rename/edit edge, audit its Cancel branch first - "revert" almost always needs the old value threaded in. Reverting to a still-invalid prior value settles (does not loop) because the cancel branch breaks without re-validating and the snapshot is refreshed to the reverted name. NOTE: the Model-Editor-DIALOG-close path still leaves an invalid name as-is on cancel (no prior value tracked there) - extend it only if a user hits that path.

## 2026-06-24: "Revert" on a Required violation = uniform rule across ALL object types - and a current request can contradict the user's own prior rule (surface it, do not silently override)

**Rule:** The behaviour of clicking "Revert Change" on a Required naming-rule dialog must be identical for MODEL / TABLE / VIEW / COLUMN: (a) revert restores the value; (b) reverting STOPS the cross-property dialog chain (a valid revert does not then pop the next property's dialog); (c) on an EXISTING object, if the reverted value is STILL invalid (e.g. empty Required baseline) the SAME dialog re-opens until valid - the user cannot escape a Required violation by reverting (the 2026-05-24 rule); (d) a NEW object escapes via Discard (delete). TABLE/VIEW already had (c) via `RevalidatePropertyAfterRevert` + a re-prompt loop; MODEL and COLUMN only had (a)+(b) and let the user escape. Applied (c) to MODEL (`ValidateModelOnEditorClose`: cancel -> revert -> re-validate -> `currentFail=fresh; continue` if invalid, else `return`) and COLUMN (`ValidateColumnNamingStandardCore`: wrapped the first dialog in a `while`, SITE 1; and made the OK-path re-prompt `while` SITE 2 re-validate-then-reprompt). Mirror the reference's safeguards: re-validation in a try/catch that treats a fault as valid (no infinite trap), and record the session dismissal on the revert-to-VALID path (parity - missing it on one site was a real divergence caught in review).

**Why:** The user asked to "apply this logic to all rules", and when I surfaced the two consistent directions (force-valid everywhere vs allow-escape everywhere), I discovered their CURRENT lean (revert -> stop+leave) directly contradicted their OWN documented 2026-05-24 rule (force valid, no escape via invalid-baseline revert; the comment at TableTypeMonitorService.cs ~2822-2832). Per "challenge proposals / question what the user says", I did NOT silently override the prior rule - I quoted it back and asked which way to unify. The user chose KEEP-2026-05-24-everywhere. Silently applying the first lean would have reversed a deliberate enforcement they wanted.

**How to apply:** When a "make it consistent everywhere" request touches a behaviour that already exists asymmetrically, find WHY the asymmetry exists (often a prior explicit rule documented in a comment) before unifying - the consistent answer might be the opposite of the user's first instinct. COLUMN's required-field loop has TWO cancel sites (first dialog + OK-path re-prompt while); both need the force-fix and must stay mutually exclusive (gated by the OK-vs-cancel `rc` check). New objects must keep escaping via Discard - only existing objects are trapped until valid. Note (intended UX, consistent with TABLE): an existing column's empty Required Schema_Ref/Owner is non-dismissable-by-revert - it loops to the Owner picker.

## 2026-06-26: PRIMARY KEY runtime support = a Template applier over Key_Group (Type="PK") - keep the property-code GENERIC and challenge "Physical_Name works on a Key_Group"

**Rule:** The admin's new "PRIMARY KEY" governance object type is erwin's Key_Group filtered to Key_Group_Type=="PK" (there is exactly one PK Key_Group per entity; INDEX/AK share the class so the Type filter is MANDATORY). Runtime support is `ApplyPrimaryKeyRules` - a near-exact parallel of `ApplyColumnTemplateRules`: GetTemplateRules("PRIMARY KEY") (cheap early-out), find the PK Key_Group via Collect(entity,"Key_Group")+Type=="PK" (proven pattern, mirrors IsAttributeInPrimaryKey), render with own=pkKg / related="Table"->parent entity (ResolveAlias("PRIMARY KEY",...)), FILL_MODE + idempotency + no-fallback, write `pkKg.Properties(rule.PropertyCode).Value`. Wired next to CheckEntityKeyGroups in CheckEntityForChanges. ReadUdpValue gained "primary key" => "Key_Group" for DEPENDS_ON. Two first-sight/failure HashSets (`_pkTemplateSeen`, `_pkTemplateWriteFailed`) MUST be cleared in ALL three rebaseline paths (StartMonitoring/RebaselineDeferred/TakeSnapshot) alongside the snapshot dicts - ObjectIds are model-scoped, an uncleared set mis-gates APPLY_ON across a model switch and leaks.

**Why:** The prompt asserted "Physical_Name works on a Key_Group, obj.Properties(code) directly". The codebase evidence CONTRADICTS that: every existing Key_Group write uses `Name` (CheckEntityKeyGroups), the curated KeyGroupCandidates probe list omits Physical_Name, and the verified `reference_view_no_physical_name` precedent shows not every erwin object surfaces Physical_Name (reading/writing it on the wrong class THROWS). So the PK constraint name is likely under `Name`, not `Physical_Name` - but only a live erwin check settles it. Per challenge-proposals: do NOT hardcode Physical_Name. The applier writes whatever `rule.PropertyCode` the admin set, catches a throwing write, records it (`_pkTemplateWriteFailed`) so it is not retried + log-spammed every tick, and surfaces the uncertainty to the user to live-verify.

**How to apply:** For a new SCAPI write target, never assume a property code transfers from another object class - check existing writes for that class + the metamodel probe's per-class candidate list, and keep the write generic over the admin-configured PropertyCode. Defer the heavy non-template (Prefix/Suffix/Required validate-and-prompt) chain and the object-existence mapping (which needs its own Key_Group_Type=="PK" filter in CheckRequiredObjectTypesExist - a naive "PRIMARY_KEY"->"Key_Group" mapping wrongly matches any index) until the core Template write is live-proven.

## 2026-06-29: Self-referential Template + FILL_MODE=Always = unbounded runaway (cursor-flicker) - the idempotency guard does NOT catch it

**Symptom (live):** PK Physical_Name grew `PK_PK_PK_..._%KeyName` and `[PK-TEMPLATE-APPLY]` fired every ~400ms (a transaction per heartbeat) -> the Column Editor mouse cursor flickered to busy constantly, and A1 "did not work" (the name was garbage).

**Root cause:** the admin template was `PK_{Physical_Name}` targeting `Physical_Name` - an OWN token equal to the rule's TARGET property. Each render reads the property it is about to write, so under FILL_MODE=Always the output feeds back as the next input and grows without bound. The idempotency guard `rendered == current` NEVER triggers because `rendered = "PK_" + current` always differs. The applier therefore wrote a transaction every tick (the scoped Column-Editor path runs CheckEntityForChanges -> ApplyPrimaryKeyRules every heartbeat).

**Fix:** `NamingTemplateEngine.ReferencesOwnProperty(template, targetCode)` - true when an OWN token (no `Alias.` prefix) equals the target property code. Both appliers (ApplyPrimaryKeyRules + ApplyColumnTemplateRules) refuse such a rule (log `[*-TEMPLATE-SKIP] ... self-referential ... use {Table.X} instead`, PK suppresses via `_pkTemplateWriteFailed` so it logs once). Related tokens `{Table.Physical_Name}` (with a dot) are never self-referential. The correct admin template seeds from the PARENT: `PK_{Table.Physical_Name}` (or `{Table.Name}`).

**Why it matters / how to apply:** (1) A value-GENERATOR rule that reads its own output is inherently non-convergent - guard it statically (own-token == target), do not rely on the value-equality idempotency check. (2) Any per-heartbeat applier that WRITES is a cursor-flicker + model-churn risk; the dominant cost is the transaction, not the COM reads - a correct convergent template writes once then is idempotent (no per-tick write). (3) The live log is `%TEMP%\erwin-addin-debug.log` = `C:\Users\Kursat\AppData\Local\Temp\2\erwin-addin-debug.log`; the `c:\work` copy was stale for days - always confirm log mtime before trusting it. (4) A naming/template rule silently never loads if its CONFIG_ID != open config, IS_ACTIVE=0, or its property def has DBMS_VERSION_ID != NULL (loader filter).

## 2026-06-29: "column is PK" naming condition cannot be a property read - resolve via the Key_Group_Member walk

**Rule:** erwin exposes NO readable Attribute property for primary-key membership. A DEPENDS_ON condition like `prop[IsPrimaryKey] in [True]` (or Is_PK / Primary_Key) reads EMPTY via `attr.Properties(code).Value` (proven live: `[TEMPLATE-COND] prop[IsPrimaryKey]=''`), so the rule silently never fires. PK membership is only knowable via the Key_Group graph (`IsAttributeInPrimaryKey`: entity's Key_Group with Key_Group_Type=="PK" -> Key_Group_Member rows -> Attribute_Ref match).

**Fix shipped:** `NamingValidationEngine.IsPkMembershipCondition(rule)` recognizes a built-in-property condition whose code is in {IsPrimaryKey, Is_PK, Primary_Key, PrimaryKey, Is_Primary_Key}. A new `IsRuleApplicable(rule, objType, scapiObject, bool? pkMembership)` overload evaluates such a condition against a caller-resolved boolean instead of a property read (null override = byte-identical old behaviour - git-diff verified, all existing 3-arg callers unchanged). `ApplyColumnTemplateRules` resolves PK membership lazily (once per column, only when a rule asks) via a local `IsAttributePrimaryKeyMember` (mirrors IsAttributeInPrimaryKey) and passes it in. So an admin `prop[IsPrimaryKey] in [True]` COLUMN rule now works with NO admin change.

**Why / how to apply:** before assuming a DEPENDS_ON condition property exists at runtime, check whether the codebase reads it anywhere - if PK/FK/AK-ness is only derived via the Key_Group_Member walk (never a property read), the generic property-condition path can't see it and needs a caller-resolved override. Always add a `[TEMPLATE-COND]` style diagnostic that logs the live read value (`prop[X]='<val>'`) so a never-firing conditional rule is debuggable instead of silently skipped (no-silent-swallow). Note: a COLUMN Physical_Name Template that fires on PK columns RENAMES them (PK column -> `PK_<table>`); composite PKs collide to the same name (admin-authoring concern, user-managed per 2026-06-29 decision).

## 2026-06-29: Model switch-back absorbs added tables - heartbeat baselines too late; fix = baseline at connect (with partial-read guard)

**Symptom (live):** Open Mart model A, switch to model B (rules work), switch BACK to A, add a table -> naming rules DON'T run. Waiting ~10 s before adding works.

**Root cause:** A fresh `ValidationCoordinatorService` is created per connect (ModelConfigForm.cs:1935 `new ...` via ReinitializeForModelSwitch -> Dispose+Initialize), so the heartbeat counters start at -1. The heartbeat's FIRST tick only fires after the connect-settle + model-UDP one-shot (~10 s), and on that first tick (`isFirstTick`, counters==-1) it SILENTLY baselines every entity without validating. Any table added in that ~10 s window is absorbed into the baseline -> never flagged. (Agent's first guess "StartMonitoring doesn't reset the counters" was WRONG - fresh instance already has -1; verified before applying.)

**Fix:** `BaselineDiagramHeartbeat()` called synchronously from StartMonitoring captures the heartbeat baseline (`_entityIdSnapshot`/`_entityAttrIdSnapshot`/`_entityAttrCountSnapshot`/`_entityDisplayNameSnapshot`/`_lastTotal*Count`) for the active model AT CONNECT (model is loaded - `_session.Open()` blocks, LoadTablesComboBox already walked it). Then the first tick is no longer isFirstTick; entities present at connect are known (not validated), entities added AFTER connect are new -> validated. Absorb window shrinks from ~10 s to the connect duration.

**Why / how to apply:** (1) When moving a baseline EARLIER, watch the load-race: a transient reconnect while erwin is still reloading (Mart Save-As+Cancel) can hand a PARTIAL/empty entity walk; the first tick then sees late-loaded entities as "new" and spuriously validates the whole model. Guard with the codebase's existing partial-read pattern (ModelConfigForm.cs:2081 fires on Count==0): if the connect-time entity walk returns 0, reset to -1 and defer to the first-tick baseline. (2) When replicating a derived value (entity displayName) into a baseline snapshot, copy the EXACT read the consumer uses (`entity.Properties("Physical_Name").Value?.ToString() ?? ""`, not `entity.Physical_Name`) or the first diff is a phantom rename. (3) Always verify an agent's "X isn't reset" root cause against the instance lifecycle (new vs reused) before applying - here the instance is recreated, so the counter-reset fix was moot.

## 2026-06-29: Naming rule conditions: single -> ordered AND/OR list (MC_NAMING_RULE_CONDITION), no flat-column fallback

**Rule:** Naming rule DEPENDS_ON evaluation moved from one flat condition on MC_NAMING_STANDARD to an ordered list in MC_NAMING_RULE_CONDITION (ID, NAMING_STANDARD_ID, ORDER_INDEX, CONNECTOR NULL@0 then AND/OR, DEPENDS_ON_UDP_ID XOR DEPENDS_ON_PROPERTY_DEF_ID, DEPENDS_ON_PROPERTY_VALUES CSV). The flat columns were migrated + DROPPED, so the addin reads ONLY the sub-table - removed the flat DependsOn* fields from NamingStandardRule + the flat columns/joins from GetQuery (else the SELECT breaks once columns drop). Loader: `LoadRuleConditions` (3 dialects, JOIN MC_NAMING_STANDARD for CONFIG_ID filter + LEFT JOIN udp/cond_pd for names, ORDER BY NSID,ORDER_INDEX, attach by id, skip both/no-source term, error propagates so a conditional rule is never silently unconditional). Eval: `IsRuleApplicable` folds `rule.Conditions` strictly LEFT-TO-RIGHT (no precedence): `result=match(t0); for i>=1: if (isOr==result) continue; result=match(ti)` - this short-circuit IS provably identical to `result = isOr?result||m:result&&m` (verified all 4 cases) and saves a COM read. Empty list = unconditional. Unknown/empty connector => AND (never loosen). PK-membership is per-term: a rule mixing a PK term + a normal term uses the caller-resolved bool for the PK term only.

**Why / how to apply:** (1) When admin drops DB columns in parallel, REMOVE them from the addin query, don't just stop reading them - a stray SELECT of a dropped column fails the whole load. (2) A unit test that feeds a fake SCAPI object through a `dynamic` read path (ReadBuiltinPropertyValue does `scapiObject.Properties(code).Value` via dynamic) MUST make the fake type PUBLIC: the RuntimeBinder runs in the CALLEE assembly (ErwinAddIn) and cannot bind to a private/internal test type, so the read bind-fails and is swallowed as "" - every value-dependent assertion then silently fails. (3) Scope check: DependsOn* also appears on PredefinedColumnRule (DependsOnUdpValue) + PropertyDef - separate systems, NOT MC_NAMING_STANDARD; grep the owning TYPE before mass-editing. (4) Admin contract: once the addin reads the sub-table, tell the admin team the bridge flat columns can be dropped.

## 2026-06-30: MDI tab-switch not detected -> rules don't run on the newly-active model (window-title locator regex truncated at first space)

**Symptom:** Switch between CORE BANKING MDI tabs in the same Mart (e.g. to "...EFT"), add a table -> naming rules do NOT run; add-in stays bound to the previous model. Worked "after ~10s" only by coincidence (a different code path).

**Root cause:** `PuLocatorReader.ReadFromWindowTitle()` parsed the active locator with `@"\[(?<base>(?:[Mm]art://)[^\s\]]+)..."`. The `[^\s\]]+` class excludes SPACE, so the locator was cut at the first space. erwin Mart paths/model names contain spaces ("Core Banking", "CORE BANKING ... BULK REMITTANCE EFT"), so EVERY CORE BANKING model collapsed to the identical truncated locator `Mart://Mart/FibaBenzerleri/Core`. The `effectiveCount>1` MDI-tab-switch branch compares the parsed locator to `_lastObservedTitleLocator`; both truncated identically -> never differ -> switch never detected -> no reconnect -> ConfigContext + heartbeat stay on the old model. `FindPuIndexMatchingTitleLocator`/`ExtractMartStem` were collaterally broken too (they consumed the truncated value). Diagnosable in the log: `[TabPoll] ... titleLoc='Mart://Mart/FibaBenzerleri/Core'` for every model + zero reconnects across a 12-min window of tab switching.

**Fix:** (1) Regex now captures the full locator with spaces, anchored on the version marker that follows it: `@"(?<base>[Mm]art://.+?)\s*:\s*v(?<v>\d+)"` (lazy base; the only " : v<digits>" in a Mart caption is the version separator; no leading "[" required so it also parses a bare MDI-child caption). Shared `PuLocatorReader.ParseLocatorFromCaption` (public, unit-tested). (2) Better SOURCE than the main-frame title: read the ACTIVE MDI CHILD caption. `Win32Helper.GetActiveMdiChild(main)` = find "MDIClient" child + `SendMessageTimeout(WM_MDIGETACTIVE=0x0229, SMTO_ABORTIFHUNG)`; `PuLocatorReader.ReadFromActiveMdiChild(out hwnd)`. A modal dialog/compare-wizard is NOT an MDI child, so it cannot steal the child caption (the old main-frame approach needed an empty-title debounce for exactly that theft). (3) `ModelConfigForm` effectiveCount>1 branch now triggers on the active-child HWND change (parse-free) OR the locator-string change, with the regex-fixed main-frame title as fallback when erwin is not on a standard MDI frame.

**Why / how to apply:** (1) When parsing a Windows title/caption that embeds a path, NEVER use `[^\s...]` - real paths and model names contain spaces. Anchor on a stable trailing delimiter (here the " : vN" version marker) and let the body be lazy. (2) The erwin window title is the WRONG ground truth for "which model is active" - it is shared, decorated, and stolen by dialogs. The ACTIVE MDI CHILD is the real signal; the codebase already had the MDI plumbing (`MartMartAutomation`: MDIClient lookup, WM_MDIGETACTIVE, GetTitle) - reuse it. (3) Test caption parsers against REAL strings pulled from the live log, not idealized ones (the double-space " :  v2  : " + trailing diagram + dirty "*" only show up in production captions). (4) When a tab-switch "is not detected", grep the log for `[TabPoll] titleLoc=` first - identical values across models is the truncation tell.

## 2026-06-30: "AND not applied" was a MODEL-scoped condition UDP read on the wrong object (not an AND/fold bug)

**Symptom:** Multi-condition rule 1167 `[Prefix] PFXC_ TABLE.Physical_Name cond=udp[TableClass] in [Log] AND udp[Application] in [UYG547 | ...]` worked in MetaRepoTmp but NOT in SQL_BUYUKMODEL (prefix never added). User read it as an AND bug.

**Root cause (log-proven):** The fold is correct - it applied PFXC_ in MetaRepoTmp when both terms matched, stripped it when not. The real failure: `Application` is an ENTITY (table) UDP in MetaRepoTmp but a MODEL UDP in SQL_BUYUKMODEL. `NamingValidationEngine.ReadUdpValue` always builds the path from the RULE's target object type (Table -> `Entity.Physical.Application`) and reads it on the entity. In SQL_BUYUKMODEL that throws `COMException: "Entity.Physical.Application is not valid class id or class name for object or property"` (caught + logged), the term reads empty, AND short-circuits false, no prefix. The same UDP NAME living at different object levels across models is the trap. Confirmed by the paired log: `[ModelUDP] 'Application' changed: ... -> 'UYG547 | ...'` (model-level) right next to the entity-read throw.

**Fix:** Resolve a condition UDP against its LIVE owner. `ReadUdpValue` reads the entity/column first; only when SCAPI reports `"not valid class"` (the signal that the property is not on that object's class) does it re-read from the MODEL root `Model.Physical.<udp>` via `NamingValidationEngine.ModelRootProvider` (a `Func<dynamic>` set by `ValidationCoordinatorService.StartMonitoring` to `() => _session?.ModelObjects?.Root`, refreshed every connect). `TryReadModelUdp` returns null = "could not attempt" (caller logs the original error), "" = "attempted but absent" (genuine typo). This makes a model-level condition (model's Application) gate a TABLE rule model-wide, and auto-handles the per-model topology difference (entity UDP in one model, model UDP in another).

**Why / how to apply:** (1) Before fixing the obvious-named bug ("AND"), read the log for the ACTUAL failing operation - here a swallowed-then-logged COMException on the second term, not the boolean logic. The fold had unit tests + an adversarial review proving it correct; trust that and look elsewhere. (2) An erwin UDP is not bound to one object type globally - the SAME name can be a table UDP in model A and a model UDP in model B. Never assume a condition UDP lives on the rule's target object; resolve it against the live model and fall through to the model root on the "not valid class" signal. (3) Threading a model-root accessor as a static `Func<dynamic>` provider set per-connect beats adding an optional param to every IsRuleApplicable/ApplyNamingStandards caller. (4) This was a PRE-EXISTING limitation (single flat DEPENDS_ON would fail the same way); the new multi-condition AND just surfaced it by mixing a table UDP + a model UDP in one rule.

## 2026-06-30: PRIMARY KEY "required" must be PER-TABLE, not model-wide (object-existence rule scope depends on the object type)

**Symptom:** Required-PRIMARY-KEY rule (1168) added; opened+closed a PK-less table editor; no warning. Log: `CheckRequiredObjectTypesExist: rule#1168 OK - 'Key_Group' object present (PRIMARY KEY)` ran ONCE at reload, no table name.

**Root cause:** The generic object-existence check (`TableTypeMonitorService.CheckRequiredObjectTypesExist`) does `modelObjects.Collect(root, scapiType)` - a MODEL-WIDE "does ANY object of type X exist" check, run once at connect (from CheckModelRequiredUdpsOnce). That is correct for Subject Area / Domain etc., but for PRIMARY KEY it is meaningless: a PK is table-owned, so "the model has some PK somewhere" always passes once any table has one. The user's intent is per-table ("every table must own a PK").

**Fix:** Scope follows the object type. PRIMARY KEY is excluded from the model-wide pass (early `continue` on `IsPrimaryKeyObjectType`) and enforced PER-TABLE in the new `CheckTablePrimaryKeyRequired(entity, modelObjects, tableName, isNew)`, called from `RunScopedTableNamingCheckCore` right after `ValidateNamingStandard` (so all three scoped triggers - Entity Editor close, Model Explorer name-commit, diagram add-commit - cover it, with the same `isNew` for APPLY_ON). It does `Collect(entity, "Key_Group")` + `Key_Group_Type=="PK"`; if none and an applicable existence rule exists, warn (rule message; English dialog title). Pure decision split into `ComputePkRequirementWarning` (13 unit tests: APPLY_ON x hasPk x PK-filter x message). Adversarial review (2 finders + verify): NO code bugs; confirmed trigger coverage, modal reentrancy guard, dead-entity safety, correct-entity scoping.

**Why / how to apply:** (1) "Required object of type X" is not one rule - the ENFORCEMENT SCOPE depends on whether X is model-owned (Subject Area -> model-wide, check once) or container-owned (PRIMARY KEY -> per-table, check on each scoped table touch). Don't implement a table-owned existence as a model-wide has-any. (2) Reuse the proven per-table primitive: `Collect(entity,"Key_Group")` + `Key_Group_Type=="PK"` already exists in `ApplyPrimaryKeyRules`; the existence check is the same collect with an inverted "none -> warn". (3) Per-table-on-touch (not a model-open sweep) is the right default - it honours `mem:feedback_rules_new_objects_only` + `mem:feedback_no_full_walks_in_change_detection`; a full-table PK sweep at model open would violate both (user confirmed: keep on-touch, warn every close). (4) Wire new per-table checks at `RunScopedTableNamingCheckCore`'s post-ValidateNamingStandard point - it is the single funnel all scoped triggers pass through, so one call site covers editor-close + Model-Explorer-commit + diagram-commit.

## 2026-07-01: Two prefix rules on one property stacked without bound (single-StartsWith apply is not order-independent)

**Symptom:** Two prefix rules on TABLE.Physical_Name (rule#1167 PFXC_ + rule#17 Vp). Adding a table produced 'VpPFXC_AbcLog' then 'VpPFXC_VpPFXC_AbcLog' then 'PFXC_VpPFXC_VpPFXC_AbcLog'... runaway.

**Root cause:** `ApplyNamingStandards` pass-2 added each applicable prefix behind `!result.StartsWith(rule.Prefix)`. With two prefix rules, each prepends, so the LAST-applied ends up in front. On the next scoped check (a rename by one rule re-fires the check via ScanForRenamesEventDriven), the OTHER rule's prefix is no longer at the front -> its StartsWith fails -> it re-adds. Both re-add every pass. The prior two-pass design only stripped NON-applicable affixes, so it fixed the double-SUFFIX case but not two front-prefixes.

**Fix (NamingValidationEngine.ApplyNamingStandards):** order-independent idempotent apply. Precompute applicability once (dict, keyed by rule instance - also removes the duplicate per-rule IsRuleApplicable SCAPI reads). Then STRIP every managed prefix/suffix that is stale (rule not applicable) OR will-be-reapplied, in a WHILE loop (two prefixes can each mask the other from one StartsWith), down to the clean core - leaving applicable-but-deferred (AUTO_APPLY=false) affixes untouched (the forward pass won't re-add them, so stripping would drop one). Then re-apply willApply affixes once in SORT_ORDER. Result is stable: feeding it back is a no-op. Tests: two-prefix-no-stack (idempotent re-feed) + prefix+suffix-stable.

**Why / how to apply:** (1) A forward-only "add if StartsWith fails" affix apply is NOT idempotent when >1 rule targets the same position - each hides the others. The stable shape is: strip-to-core (loop, order-independent) then rebuild in a fixed order. (2) Any per-object naming that RENAMES re-triggers the scoped check (rename detection), so the apply MUST be a fixed point (f(f(x))==f(x)) or it runs away across re-checks. (3) Precompute IsRuleApplicable once per rule and reuse - it's a SCAPI condition read (and after the model-UDP fix it may throw+catch), not free to call per-pass.

## 2026-07-01: Naming-rule condition on a RELATED object type (SCHEMA.Name) read on the wrong object

**Symptom:** rule#1169 `[Prefix] PF_ ... cond=udp[TableClass] in [Log] AND prop[Name] in [DM]` where C2's "Name" is owned by the SCHEMA object type (the table's Owner). User set Owner='DM' but the prefix never applied - C2 was always False.

**Root cause:** The addin read `prop[Name]` on the rule's TARGET (the table entity) = the table's own name ('Abc'), ignoring that the condition property is owned by a DIFFERENT object type (SCHEMA). A naming-rule condition carries no object-type column of its own (admin `MetaShared/Data/Entities/NamingRuleCondition.cs`) - the object type comes from the SOURCE: `MC_PROPERTY_DEF.OBJECT_TYPE_ID -> MC_OBJECT_TYPE.NAME` (TABLE.Name and SCHEMA.Name are DISTINCT property defs). The addin loaded the property CODE but not its owning object type, so it always read on the target. (The desktop admin summary showed "[Erwin · SCHEMA] Name" correctly; the web admin summary dropped SCHEMA - a web-display bug, separate from the addin.)

**Fix:** (1) Loader (`NamingStandardService.GetConditionsQuery` 3 dialects + `LoadRuleConditions`): JOIN `MC_OBJECT_TYPE cond_ot ON cond_ot.ID = cond_pd.OBJECT_TYPE_ID` and load `NamingRuleCondition.DependsOnPropertyObjectType`. (2) Evaluator (`NamingValidationEngine.ReadConditionPropertyValue`): when the condition property's object type == the rule target, read directly; when it names a RELATED type, resolve from the target. SCHEMA.Name is projected onto a table/view as `Name_Qualifier` (the derived Schema_Ref name), so SCHEMA.Name -> read `Name_Qualifier` (log-confirmed = 'DM'). Other related types log "not supported" + read empty. `DescribeTerm` + the ModelConfigForm dump qualify related props (prop[SCHEMA.Name]).

**Why / how to apply:** (1) A condition's object type is NOT on the condition row - it is the owning type of the chosen UDP/PropertyDef. Load it (join to MC_OBJECT_TYPE) and never assume the property lives on the rule's target. (2) erwin surfaces an object's owning SCHEMA name directly as `Name_Qualifier` - use that instead of navigating to the Schema object for SCHEMA.Name. (3) When a runtime feature depends on admin-side data (the object type), read the admin repo (`c:\Users\Kursat\Repos\erwin-admin`) to learn the exact storage (entity + seed) rather than guessing the DB shape. (4) Same class of bug as the model-scoped UDP fix ([[mem: reference_condition_udp_model_scope]]): the value's real owner is not the rule's target - resolve it, do not read blindly.

## 2026-07-01: PK-required warning repeated back-to-back (6s time-debounce could not cover the modal-stretched auto-rename chain)

**Symptom:** The 1168 PK-required warning popped several times for ONE table add. Log: same entity warned at 20:22:08 ('Abc') and 20:22:14 ('VpAbcLog') - 6.2 s apart, so the 6000 ms per-entity time debounce missed it; the 3rd ('PF_VpAbcLog', +2.5 s) was suppressed.

**Root cause:** Adding a table re-fires the scoped naming check after EACH auto-rename as prefix rules become applicable (Abc -> Vp... -> PF_...; PF_ is rule#1169, newly applying once its SCHEMA.Name condition was fixed). Each re-check runs the per-table PK-existence check and pops the same warning. The chain spans more than the debounce window because required-UDP modals + staged renames stretch it past 6 s, so a fixed time window cannot bound it.

**Fix:** Replaced the time debounce with a session HashSet `_pkWarnedEntityIds` keyed on entity ObjectId - warn ONCE per entity while it lacks a PK. Cleared for an entity the moment it gains a PK (ComputePkRequirementWarning returns null), so removing the PK later warns again. This supersedes the earlier "warn every close" behaviour, at the user's request ("aynı uyarı birden fazla verilmemeli").

**Why / how to apply:** A time-window debounce is the wrong tool to collapse a chain whose length is unbounded (modal-driven). Key the suppression on a STABLE identity (the entity ObjectId, which survives the renames) and a STATE boundary (has-PK / no-PK), not wall-clock. Clear on the positive state transition so the guard self-heals.

## 2026-07-02: Creation-cascade isNew continuation + Datatype Library user picker

**(1) ApplyOn=Create rules dropped mid-chain:** Prefix(Create) applied on check#1 (isNew=true) RENAMES the table; the rename re-fires the scoped check on a later tick, AFTER ValidateCommittedPendingAttrs' finally cleared _creationGestureEntityIds -> follow-up ran isNew=false -> Suffix(Create) never fired. User semantic: every check in the chain triggered by ONE creation gesture must see the object's INITIAL state. Fix: `_creationCascadeEntityIds` (entity ObjectId) - ARMED in RunScopedTableNamingCheckCore when a Create-context check renamed the entity (live name after ValidateNamingStandard != nameForMatch); DISARMED at the first stable (no-rename) Create-context check = the fixed point, or when the entity died mid-check. Read by IsEntityInCreationGesture (both sets) + the rename-scan isNew bridge. Lesson: any state flag consumed by FOLLOW-UP work that our own writes trigger (rename -> scan -> check) must live until the chain reaches a fixed point, not until the current drain's finally; and Update-only rules correctly stay OFF during the whole creation chain.

**(2) Datatype Library enforcement now asks the user:** was force-set to restore-or-first-allowed + OK-only info. Now: the automatic safe value is STILL written first (invariant: the model never holds a disallowed type, even while a modal is up), then `Forms/AllowedDatatypePickerForm` (house chrome copied from RequiredFieldDialog) lets the user pick the allowed base type (locked combo; "(n)" marks parameterized) and enter the parameter (n or n,m; optional; prefilled from the attempted type e.g. 18 from char(18)); pick is written in a second transaction with re-read + IsAllowed round-trip verification. Cancel keeps the automatic value ("Keep Automatic Choice" button). CRITICAL detail: erwin's delayed SECOND combo-commit re-applies the disallowed value with the popup suppressed (dedup window) - that duplicate must re-enforce the USER'S pick (`_allowedDatatypeUserPicks` per ObjectId), not the fallback, or it silently clobbers what the user just chose. Pure statics Compose/IsValidParameter/ExtractParameter unit-tested (22 cases).

## 2026-07-02 (addendum): Cascade adversarial-review outcomes (1 BUG + 3 RISK confirmed, 3 claims rejected by code tracing)

**Confirmed + fixed:** (1) cascadeId was read AFTER ValidateNamingStandard - a Required-Cancel DELETE inside the call can leave a dead COM proxy whose ObjectId throws, skipping the disarm and leaking the armed id; hoist the ObjectId read BEFORE the call while the entity is alive. (2) A leaked id permanently defeats IsEntityInCreationGesture's Count==0 fast path -> full Collect("Entity") walk on every later naming check (the banned full-walk class); GC both gesture/cascade sets in the heartbeat dead-entity removal loop (dead ids are already computed there). (3) The cascade re-runs Step-2 (AUTO_APPLY=false) and would re-ask the IDENTICAL declined Yes/No suggestion every pass; added `_declinedNameSuggestions` (ObjectId -> declined suggested name) session-dismissal - a DIFFERENT suggestion naturally re-asks, Yes clears. (4) The name-based first-match gesture probe can bleed Create onto a same-named sibling; added an authoritative OBJECT-ID upgrade in RunScopedTableNamingCheckCore (after the entity is matched) so the cascade binds to the exact entity.

**Rejected claims (worth remembering why):** "unbounded lifetime / stale-name flush misses" - editor-close runs Flush FIRST + rename-scan SECOND in the same tick, so the scan re-queues under the live name; "no lifecycle reset" - a FRESH ValidationCoordinatorService is constructed per connect (ModelConfigForm ~1969), instance sets cannot survive a model switch; "TTMS direct call sites bypass arming" - CheckForTableTypeChanges is caller-less dead code. Lesson: run the adversarial verify pass - 3 of 7 confident findings were false alarms that code tracing killed, and the 4 real ones were all lifecycle edges (read-order on COM proxies, GC of id sets, modal re-ask, name-vs-id identity), not the core algorithm.

## 2026-07-02 (2): Discard must cancel the entity's pending warnings + canonical affix form must validate clean

**(1) Discard -> bogus "PK required" popup:** Required prompts run INSIDE ValidateNamingStandard; Discard there deletes the entity and returns. Core then ran CheckTablePrimaryKeyRequired on the DEAD entity - and a deleted r10 proxy's Collect(entity,"Key_Group") can return EMPTY instead of throwing, so members=0 read as "no PK" and warned for a table that no longer exists. Fix: discard gate in RunScopedTableNamingCheckCore - re-read the live name after ValidateNamingStandard (a live entity always has at least a placeholder name; empty = deleted) and SKIP all remaining checks. Principle (user): a discard cancels every pending warning for that entity. Lesson: on erwin r10, deleted-object proxies fail UNRELIABLY (some calls throw, some return empty) - never use "it would throw" as the deleted-entity guard; probe liveness explicitly.

**(2) Unsatisfiable Prefix validation loop:** two prefix rules compose 'VpPF_X' (both applied, user decision), but per-rule Prefix VALIDATION is StartsWith - the inner prefix can never be at position 0, so rule#1169 stayed violated forever and the Required-field re-prompt looped (user typed the same name 6+ times, then discarded). Fix: canonical-form acceptance in ValidateObjectName - if an affix (Prefix/Suffix) violation is present but ApplyNamingStandards(autoOnly:false) would leave the name UNCHANGED, the name already carries every applicable affix in its canonical slot -> drop those positional violations (log). Genuinely missing affixes still flag (apply would change the name). Lesson: when an APPLY becomes multi-rule/canonical, the VALIDATE side must accept the canonical composition too - per-rule positional checks (StartsWith/EndsWith) are only valid for a single affix per property.

**(3) Test-suite flake root-caused + fixed:** ExistenceRuleTests and NamingStandardEngineTests both seed the NamingStandardService.Instance SINGLETON via SeedForTesting; xUnit runs different classes in parallel, so one class's seed/clear races the other's reads (pass isolated, flake in full runs). Fix: `[Collection("NamingStandardSingleton")]` on both classes serializes them. Lesson: any test that seeds a process-wide singleton must share a collection with every other test class touching that singleton.

## 2026-07-02 (3): ApplyOn=Create rule whose condition is filled DURING Required-UDP never fired (placeholder-commit rename ran isNew=false)

**Symptom (Furkan, MetaRepoZeynep/DW_Physical, rule#1175):** `[Prefix] DIM_ apply=Create cond=udp[TableClass] in [Dimension] AND prop[SCHEMA.Name] in [DM]`. Add a Dimension table, set Owner=DM - DIM_ never applied.

**Root cause (timing):** For a new entity the checks run in this order: (1) commit-edge scoped check isNew=true - but SCHEMA.Name (Name_Qualifier) is still empty because Owner has not been entered yet, so 1175 is correctly not-applicable; (2) the Required-UDP prompt then sets Owner=DM; (3) the inline-edit-close ScanForRenamesEventDriven detects the placeholder rename 'E/17' -> 'FURKANS' AGAIN (its own snapshot still held 'E/17') and re-runs the scoped check - now Name_Qualifier=DM so 1175's conditions ARE met - but this check ran isNew=FALSE (wasPending already consumed by the commit-edge, _creationGestureEntityIds already drained in the finally), and 1175 is ApplyOn=Create -> MatchesApplyOn(Create,false)=false -> skipped forever. The Create window closed before the Create-rule's condition became true.

**Fix:** In both rename-detection sites (ScanForRenamesEventDriven inline-edit-close + DiagramHeartbeatTick rename branch) compute `entityIsNew = wasPending || inCreationGesture || IsPlaceholderEntityName(oldName)`. A rename whose OLD name is an erwin placeholder ("E/17"/"<default>"/"") is unambiguously the first real naming = a creation commit, even after the pending/gesture sets drained. So the post-Required-UDP settle check runs isNew=true, 1175 applies DIM_, the rename arms the existing cascade, and the fixed-point check disarms.

**Why / how to apply:** Create-context must be derivable from the DURABLE signal (the from-name being a placeholder), not only from transient sets (_pendingNamedEntities / gesture) that a prior drain already emptied. Required-UDP runs BETWEEN the first naming check and the settle check, and it can set the very properties (Owner/Schema) that Create-only conditions gate on - so the settle check, not just the first check, must be Create. A rename FROM a placeholder can never be a genuine update, so isNew=true there is always safe.

## 2026-07-07: Picked datatype bypassed regex rules (Model Explorer had no editor-close pass); glossary retried undecryptable creds forever

**Two related datatype bugs (Kursat, nvarchar(4200) with an admin "length <= 4000" Column.Physical_Data_Type rule):**
1. Column Property Editor: user picks nvarchar(4200), the rule warning only appears when the EDITOR CLOSES (~3s later), not while picking.
2. Model Explorer "New" column: the same nvarchar(4200) is accepted with NO warning at all.

**Root cause (single method):** `EnforceAllowedDatatypeWhitelist` is the ONLY caller of `AllowedDatatypePickerForm.Show`, and after the user picks a value it writes it and RETURNS - it never runs any naming/regex validation on the pick. Whether the rule fires afterward is pure luck of the call site: only the type-change branch (`ProcessAttributeChanges` dataTypeChanged, 6671) runs `ValidateColumnNamingStandard` right after Enforce; the other 3 sites (ProcessNewAttribute, the two placeholder->real name commits) run naming BEFORE Enforce or not at all. Bug 1 was "rescued" only by the Column-Editor CLOSE edge (`WindowMonitorTimer_Tick` -> `FinalValidateClosedTable` -> re-scan), which re-validates the whole table. Bug 2 (Model Explorer) never opens an editor, so that rescue edge never fires -> the pick is never regex-checked. Worse, Enforce PRE-ADVANCES the snapshot to the picked value (`curr.PhysicalDataType = liveAfterPick`), and the caller stores that mutated snapshot as the baseline, so the heartbeat fingerprint sees baseline==live forever and the change path can never re-fire. The picker's own `AcceptIfValid` only checked parameter SYNTAX (4200 is a valid number), never the admin rules, and had no hook to inject them.

**Fix (two parts, both approved):**
1. **Picker rule gate (primary):** `AllowedDatatypePickerForm.Show` takes an optional `Func<string,string?> validate`; `AcceptIfValid`'s decision extracted to pure static `ValidateComposition(entry, paramText, ruleValidate)` (unit-tested, 8 cases). Enforce passes `candidate => ValidateDatatypeCandidate(attr, candidate, treatAsNew)`, which runs `NamingValidationEngine.ValidateObjectName("Column", candidate, attr, "Physical_Data_Type", isNew)` UI-lessly and returns the admin's own message. A violation keeps the user in the dialog (inline error, form grows for long messages), so a rule-breaking datatype can NEVER leave the picker or be written. Closes BOTH bugs for the user-pick path.
2. **Post-enforcement safety net (warning-only):** after the final value settles, Enforce re-runs `ValidateDatatypeCandidate` on the live value once; no-op on the (already-gated) user pick, but catches the Cancel/forced-fallback value violating a rule. Warning-only (AddinMessageDialog), NOT the full `ValidateColumnNamingStandard` re-run the first analysis suggested - that would re-prompt Physical_Name rules already shown in the same gesture (double-modal). `treatAsNew = isNew || IsAttributePendingNew(objId)` threaded via a new `isNew` param on Enforce (3 new-column sites pass true, the type-change site false) so the picker validates the exact Create/Update/Both rules the editor-close pass would.

**Glossary (same session, Kursat):** the dev DB's CONNECTION_DEF password was not valid base64, so `PasswordEncryptionService.DecryptConnectionSecret` THREW (FormatException) before the existing DPAPI `decryptFailed` heuristic could run. It bubbled to the outer catch and, because every validation gesture does `if (!IsLoaded) LoadGlossary()`, re-fired on EVERY column (log spam, repeated repo-DB hits). Fix: wrap the two Decrypt calls in try/catch and route BOTH the exception and the heuristic through one `LatchCredentialFailure(configId)` that (a) latches the failure keyed on ActiveConfigId so subsequent loads short-circuit with no DB hit, (b) queues a one-time user warning drained on the STA by `TryConsumeCredentialWarning` in `ValidateGlossary` (a single modal, not per-column), (c) sets LastError. Latch auto-clears when ActiveConfigId changes; `ResetCredentialFailureLatch()` (called from `RunConfigReload`, i.e. explicit Reload/Change-DB) re-arms so a creds fix is retried once. Passive gestures still respect the latch.

**Lessons:**
- **A picker that writes a value must validate it at the point of picking, not rely on a downstream pass.** The regex rule only "worked" in the editor because of an unrelated editor-close re-scan; Model Explorer exposed that the pick itself was never validated. Validate at the source (inside the picker) - synchronous, inline, and it blocks the bad value before any write.
- **Pre-advancing a snapshot to a just-written value permanently blinds change-detection.** Enforce advancing `curr.PhysicalDataType` = the picked value, then the caller storing it as baseline, means baseline==live forever - the fingerprint short-circuit never re-fires. Any "enforce then store snapshot" path must run validation BEFORE the store, or the value is never re-examined.
- **When you refine a user-approved plan, say so.** The first analysis proposed re-running full `ValidateColumnNamingStandard` as the safety net; that double-prompts Physical_Name. Narrowed it to a datatype-only warning-only check and told the user why.
- **A decrypt that can THROW must be caught next to the call.** The DPAPI heuristic assumed Decrypt returns a bad string; a non-base64 ciphertext throws first and skipped the whole handler. And any "load on every gesture if not loaded" pattern needs a failure LATCH or it hammers the source forever on a permanent error.

## 2026-07-07 (2): idempotent affix apply stripped a user-typed suffix on a NEW column ("AbcDate" -> "Abc")

**Symptom (Kursat, MetaRepoZeynep/VpFurkanHistory):** add column "AbcDate" in the Column Editor, press Enter -> silently renamed to "Abc". Dialog: "Naming standard applied: Column ... 'AbcDate' -> 'Abc'". User: "there is NO such rule."

**Log:** `rule#1032 [Suffix] COLUMN.Physical_Name cond=[1 term(s)] -> applicable=False` then `rule#1032 stale Suffix='Date' stripped from 'AbcDate'` -> `affixes 'AbcDate' -> 'Abc'`.

**Root cause:** rule#1032 IS a real rule - a CONDITIONAL Suffix='Date' rule (adds "Date" to columns meeting a UDP condition). For this column the condition is not met (applicable=False), correctly. But the idempotent-affix apply (`NamingValidationEngine.ApplyNamingStandards`, the 2026-07-01 strip-then-reapply) strips any name ENDING WITH a non-applicable rule's suffix, assuming it is a now-stale application of that rule. It cannot tell the rule-added affix apart from a coincidental user-typed one, so "AbcDate" (Date typed by the user) lost its "Date". The stale-strip's legitimate purpose is removing an affix that became obsolete after a conditioning-UDP flip (documented 2026-05-07), which only happens on an EXISTING object.

**Fix (one line + rationale):** in the strip loop, `if (!applicable[rule] && isNew) continue;`. A brand-new object has NO naming history - no rule has ever applied to it - so a name matching a non-applicable rule's affix is user text, never a stale decoration. The stale-strip is preserved for existing objects (isNew=false), and applicable rules still strip-then-reapply for idempotency. Tests: `AffixStaleStripTests` (new keeps user affix / existing still strips stale / applicable still applies).

**Known residual (flagged to user):** the SAME coincidence can still fire on an EXISTING column being edited - e.g. editing "OrderDate"'s comment could strip it to "Order" via a non-applicable Suffix='Date' rule. Fixing that correctly needs conditioning-change detection (strip only when the conditioning UDP actually flipped this gesture), which means threading prev-state into the stateless engine - a larger change deferred pending user approval, not folded into this report's fix.

**Lessons:**
- **A stale-affix strip cannot distinguish a rule-added affix from coincidental user text without history.** The only clean signal is "no history" = a new object, where any match is user intent. Use isNew as the discriminator; never strip a non-applicable affix on a new object.
- **"There is no such rule" often means "no rule I expected to touch THIS object".** The rule existed and evaluated correctly (applicable=False); the defect was the strip acting on a non-applicable rule. Read the NamingApply log lines (cond=... applicable=..., stale ... stripped) to separate "rule fired" from "rule's affix coincidentally matched".

## 2026-07-07 (3): startup dev DB pick was wiped by Execute's fresh-invocation ClearCache (false DBMS mismatch)

**Symptom (Kursat, MetaRepoZeynep):** open Mart/.../FIBA-TEST -> "Model / Configuration DBMS Mismatch: config specifies SQL Server 2019/2022 but model targets 2016/2017". But the config IS 2016/2017 and the model IS 2016/2017 (version 13). User: "isn't it taking the first DB selection correctly?"

**Log proof:** same config #1012 resolved DBMS_VERSION_ID=299 (2019/2022) after STARTUP pick of MetaRepoZeynep, but 298 (2016/2017, NO mismatch) after an explicit CHANGE DB to the same MetaRepoZeynep. Two different repo DBs both hold a config #1012 named 'SQLServer_PhysicalDataModel_' with DIFFERENT DBMS_VERSION_ID; the add-in was reading the WRONG DB's copy.

**Root cause (ordering):** ErwinAddIn.Execute runs, in order: (1) DevDatabaseSelector.TrySelectAndOverride (ClearCache + OverrideConfig -> picked MetaRepoZeynep), (2) CheckLicense, (3) a BLIND `DatabaseService.ClearCache()` whose job is "re-read the registry on every fresh invocation" (so admins editing HKCU are picked up without restart). Step 3 wiped the step-1 override, so ConfigContext read config #1012 from the REGISTRY DB (MetaRepoTmp, where #1012 = 299/2019-2022) instead of the picked MetaRepoZeynep (#1012 = 298/2016-2017) -> false mismatch. Change DB worked because RunConfigReload (2026-07-07 fix) does NOT ClearCache in dev, so its override survived.

**Fix:** track override state at the DatabaseService level - new `DatabaseService.OverrideConfig(cfg)` (sets `_cachedConfig` + reader + `_isOverridden=true`), `ClearCache()` sets `_isOverridden=false`, `IsOverridden` getter. DevDatabaseSelector routes through `DatabaseService.OverrideConfig` (not the raw reader). ErwinAddIn.Execute's fresh-invocation ClearCache is now guarded: `if (IsOverridden) skip (keep the picked DB) else ClearCache`. Prod is unchanged (no override -> IsOverridden false -> clears as before). Chose a DatabaseService-level flag over `bool IsOverridden` on IBootstrapService because that interface lives in the shared erwin-admin/MetaShared project (cross-repo change + other implementers); a DatabaseService flag needs no interface change and is not `#if`-coupled.

**Lessons:**
- **A "reset to defaults on every fresh start" step must not run AFTER a deliberate override.** The Execute ClearCache is correct for prod (re-read registry) but must respect an in-memory override. Any global "clear cache / reload" that can wipe a deliberate override needs an `IsOverridden` guard.
- **Same-ID config rows across DBs are a trap.** config #1012 existed in BOTH MetaRepoTmp and MetaRepoZeynep with different DBMS. When "the value is wrong but sometimes right", suspect READING THE WRONG SOURCE, not a bad value - the DBMS_VERSION_ID flipping 299<->298 for the same config ID was the tell.
- **Trust the user's instinct as a hypothesis to verify, not a fact.** "Isn't it taking the first DB selection correctly?" was exactly right - the startup pick was applied then wiped. Verified against the log (startup=299 vs Change-DB=298) before fixing.

## 2026-07-07 (4): type-conditioned suffix applied only on editor close (editor) / never (Model Explorer) - new-column Enforce sites lacked the C3 replay

**Symptom (Kursat):** rule#1032 "Date-typed columns get suffix 'Date'" (condition on Physical_Data_Type, 1 term). In the Column Editor: create column 'Abc', pick datatype 'Date' via the whitelist picker - suffix appears only when the EDITOR CLOSES ('Abc' -> 'AbcDate' at close). Via Model Explorer: same gesture, suffix NEVER applied (repeat report).

**Root cause:** the C3 polymorphic replay (re-run ValidateColumnNamingStandard after a datatype settles, precisely for type-conditioned name rules) exists ONLY on the type-change branch of ProcessAttributeChanges. The three NEW-COLUMN Enforce call sites (ValidateCommittedPendingAttrs, CheckEntityForChanges rename branch, ProcessNewAttribute) run naming BEFORE Enforce and nothing after, so a type settled by the picker/forced fallback never re-evaluates the name rules. Editor case got rescued late by the editor-close pass (heartbeat/new-attr materialization at close); Model Explorer has no close edge, so it never fired. Workflow exploration also established: condition sources (DEPENDS_ON_UDP_NAME) are NOT in the watched-property set (GetPropertyCodes = rule TARGET codes only); GetRelevantUdpNames exists but its 2 consumers are dead code; heartbeat is count-delta based so UDP-only/property-only changes never select an entity for rescan.

**Fix:** in EnforceAllowedDatatypeWhitelist, after the pick/fallback settles (inside the !suppressPopup block, after the dedup restamp), `if (isNew) { ValidateColumnNamingStandard(attr, curr, isNew:true); flush ShowConsolidatedPopup; }` - mirrors the C3 replay. isNew-gated because the type-change site (isNew:false) already replays itself. This REPLACED the same-day warning-only datatype safety net (the replay's Step 3b re-checks Physical_Data_Type rules too; keeping both = double dialogs). Suffix now applies immediately after the pick, editor open or not; Model Explorer path no longer depends on a close edge. Suppressed-duplicate path (erwin's delayed 2nd combo-commit) deliberately does not replay - the first pass did.

**Lessons:**
- **When a fix adds a "post-X re-check", audit ALL call sites of X, not the one in the bug report.** The C3 replay was added (2026-05-17) only where the type change was DETECTED; the sites where the type is CHANGED BY US (enforcement) were left bare. Any site that mutates a rule-conditioning property must re-run the dependent validation itself.
- **"Works in the editor" can be an accident of a close-edge rescue, not design.** The editor-close final pass masked the missing replay for months; Model Explorer exposed it because no close edge exists there. When a behavior only manifests at a session boundary, ask what SHOULD have fired earlier.
- **Same-day fixes can supersede each other - reconcile, don't stack.** The morning's warning-only safety net + this replay would both fire on the same value; replaced rather than stacked.
- Condition-UDP values are watched NOWHERE (only rule targets are). If an admin ever conditions COLUMN naming on a UDP set AFTER creation, that change is invisible to every pipeline (fast path diffs name+type only; heartbeat is count-based). WON'T-DO (user decided 2026-07-07 they have no UDP-conditioned column naming rules): the bounded candidate-set watch was NOT built. Only re-open if such a rule is added.

## 2026-07-08: applicable Suffix rule corrupted "UpdateDate" -> "UpDate" (case-insensitive affix match ate the word's own letters)

**Symptom (Kursat):** new column "UpdateDate", set datatype Date -> renamed to "UpDate". rule#1032 = Suffix='Date', which became applicable once datatype=Date (via the picker + the new post-Enforce replay).

**Log:** `rule#1032 [Suffix] ... applicable=True` then `affixes 'UpdateDate' -> 'UpDate'`.

**Root cause:** `ApplyNamingStandards`' strip/reapply used case-INsensitive (OrdinalIgnoreCase) affix matching, so the letters "date" INSIDE the word "Update" were treated as the rule's "Date" affix. The strip `while(changed)` loop stripped "Date" from "UpdateDate" -> "Update", then matched again ("Update" ends with "date" ignore-case) -> "Up", and the re-apply appended "Date" -> "UpDate". A first fix attempt (strip-each-rule-at-most-once) was INSUFFICIENT: it stopped the double-strip but the case-insensitive re-apply guard still saw "Update" as already ending in the affix, so "UpdateDate" -> "Update" (real "Date" lost). Unit tests caught this - the fix was wrong until the real cause (case-insensitivity) was addressed.

**Fix:** make affix boundary matching CASE-SENSITIVE (Ordinal) in all four checks (strip prefix/suffix + reapply prefix/suffix). The affix "Date" now matches only the token "Date", never the "date" inside "Update". "UpdateDate" (already ends with exact "Date") round-trips unchanged; "Update" (ends with lowercase "date") gets "Date" appended. Real affix tokens (Vp, PF, DM_, Date, _LOG) are authored in fixed case, so exact matching is also the truer "already applied" test. Kept strip-once as belt-and-suspenders. Tests: AffixStaleStripTests + "does_not_double_strip" (UpdateDate, isNew true/false) + "appended_when_missing" (Update -> UpdateDate).

**Lessons:**
- **A first fix that a unit test rejects is a gift - it means the root cause is elsewhere.** strip-once was a plausible fix for the double-strip symptom but the test proved the real cause was case-insensitivity. Write the test to the DESIRED OUTPUT ("UpdateDate" -> "UpdateDate"), not to the mechanism you're changing, so a wrong fix fails loudly.
- **Case-insensitive substring/affix matching silently eats real words.** "date" is inside "Update", "log" inside "catalog", "id" inside "video". An affix token should match exact-case; the letters happening to appear inside an ordinary word are not the affix. Prefer Ordinal for affix add/strip.
- **Trace the whole strip->reapply round-trip, not just the strip.** The bug needed BOTH the greedy strip AND the case-insensitive reapply guard to produce "UpDate"; fixing only one leaves it broken ("Update").

## 2026-07-08 (2): erwin auto-uniquify rename ('Pre_Abc' -> 'Pre_Abc__1069') re-validated isNew=false, skipping Create rules

**Symptom (Kursat, config #1012):** add a 2nd column "Abc" (a "Pre_Abc" already exists from the 1st). Prefix rule#1171 makes it "Pre_Abc"; erwin then UNIQUIFIES to "Pre_Abc__1069" (collision). The add-in DID re-validate the erwin-assigned name, but with isNew=FALSE, so the Create-scoped PascalCase rule#1127 was skipped and the digit/"__" name passed. User: "erwin ismi değiştirince, o zincirde kural kontrolleri baştan yapılmalı" + "not only columns - tables/views too".

**Root cause:** the ONLY per-column brand-new signal (_pendingNamedAttrs / IsAttributePendingNew) is CONSUMED at the placeholder->real-name commit that happens moments earlier. When erwin then appends "__NNNN", ProcessAttributeChanges' physicalNameChanged branch calls ValidateColumnNamingStandard with default isNew=false, treatAsNew = false || IsAttributePendingNew(id)=false. Step 3 Physical_Name validation DOES run (silently - no log) but NamingValidationEngine.ValidateObjectName gates every rule via MatchesApplyOn(rule,isNew): apply=Create + isNew=false -> `continue` -> rule#1127 skipped. (apply=Both rule#1171 fires but is a no-op since 'Pre_Abc__1069' already starts with 'Pre_'.) NamingValidate log lines are Step 3b only; Physical_Name (Step 3) is silent, so its absence at 14:58:12 did NOT mean it was skipped - it ran, gated out.

**Fix (general, all object types):** new pure `NamingValidationEngine.IsAutoUniquifyRename(prevName, newName)` = `newName == prevName + "__" + <digits>` (Ordinal). OR it into the isNew of every rename->validate site: column `ValidateColumnNamingStandardCore` treatAsNew (baselinePhysicalName vs state.PhysicalName - one place, covers all column paths); entity bridges (DiagramHeartbeatTick entityIsNew + ScanForRenamesEventDriven entityIsNew); view drift-rename branch (TableTypeMonitorService, prev = _keyGroupSnapshots["V_"+id]). So an erwin-assigned uniquify name re-validates as create and apply=Create rules re-fire. Tests: AutoUniquifyRenameTests (16 cases). 501 tests green.

**Why isNew=true is safe here (not over-enforcement):** the uniquified name is derived from an ALREADY-prefixed/affixed name ('Pre_Abc' -> 'Pre_Abc__1069'), so Step-1 auto-apply Create affix rules are no-ops (the affix is already present) - only the VALIDATE Create rules (regex/length) newly fire, which is exactly the goal (catch the invalid '__NNNN' name). The '__<digits>' signature is specific enough that a deliberate user rename won't match; if one did, the name is invalid anyway so flagging is correct.

**Lessons:**
- **When erwin re-assigns a name AFTER our naming ran, re-validate it as a fresh create.** erwin's collision uniquify ('__NNNN') produces a name the user never chose; it must go through the same Create-rule gate a fresh name would. A signature check (name + '__' + digits) is the object-type-agnostic discriminator.
- **A consumed "brand-new" flag leaves later same-gesture events mis-scoped.** _pendingNamedAttrs is drained at first name commit; any FURTHER auto-rename in the same gesture (erwin uniquify) then reads isNew=false. Don't rely solely on a one-shot pending flag for new-ness across a multi-step creation gesture - add an independent signal (here: the uniquify signature).
- **Silent validation steps make logs misleading.** Physical_Name (Step 3) logs nothing; only Step 3b logs 'NamingValidate:'. The absence of a Physical_Name log line is NOT evidence the rule was skipped - confirm via the isNew gate, not the log.

## 2026-07-08 (3): "OID" on a Log table skipped from glossary - predefined-column set was global, not entity-scoped

**Symptom (Kursat, MetaRepoZeynep):** create a table, set TableClass UDP='Log', add column "OID" (NOT predefined for a Log table; it IS in the glossary). The add-in did NOT load glossary values for it. Log: `Physical name changed: LOG_LOG.<default> -> OID` then `Glossary validation skipped (predefined column): LOG_LOG.OID`. But OID is predefined only for TableClass='Parametre' (`ApplyPredefined(UDP 'TableClass'='Parametre'): added 'OID'`).

**Root cause:** `GetPredefinedColumnNames(dynamic entity)` IGNORED its entity arg and returned `PredefinedColumnService.GetAll()` - EVERY predefined column across ALL table classes. So "OID" (gated on TableClass='Parametre') was in the set for a 'Log' table too, and `ValidateGlossary`'s skip `predefinedColumnNames.Contains(state.PhysicalName)` (the ONLY consumer) skipped it. The set that is treated as predefined did not match the set ApplyPredefined actually adds (which IS entity-scoped by the DEPENDS_ON_UDP condition).

**Fix:** new `PredefinedColumnService.GetApplicableNames(entity)` = unconditional rows + conditional rows whose `Entity.Physical.{DependsOnUdpName}` == `DependsOnUdpValue` (same applicability as ApplyPredefined / FindApplicableLockedRule). Extracted a pure static core `GetApplicableNames(columns, readUdp)` (unit-tested, UDP read once per distinct name) + a `ReadEntityUdp` COM helper. `GetPredefinedColumnNames` now calls it. So OID counts as predefined only on a Parametre table; on a Log table it is a normal column and gets glossary. Tests: PredefinedColumnApplicabilityTests (6). 507 tests green.

**Lessons:**
- **A "predefined/auto-added column" set MUST be scoped by the same condition that adds it.** ApplyPredefined adds columns per TableClass (DEPENDS_ON_UDP); any set derived from "predefined" (glossary-skip, naming-exempt) must apply the SAME per-entity condition, never a global GetAll(). A name colliding across table classes ("OID") exposes the leak.
- **A method that takes `entity` but ignores it is a red flag.** GetPredefinedColumnNames(entity) never touched entity - the scoping was silently global. When a per-object method doesn't read the object, suspect a missing scope.

## 2026-07-08 (4): datatype picker + naming Required dialog overlapped (WindowMonitorTimer_Tick missed the _isProcessingChange gate)

**Symptom (Kursat):** after the uniquify-rename fix [2026-07-08(2)] correctly made rule#1127 fire on 'Pre_Abc__1069', the "Datatype not allowed" picker and the naming "Required field" dialog appeared STACKED (should be sequential). Log: `AllowedDatatype ... forced to 'varchar(1)'` (picker shown) then, while it was up, `renamed to 'Pre_Abc__1069' during inline-edit` -> `NamingValidate isNew=True` -> 2nd modal on top.

**Root cause:** EnforceAllowedDatatypeWhitelist shows its picker while `_isProcessingChange` is set (by ProcessNewAttribute/ProcessAttributeChanges), and the modal pumps the message loop. `MonitorTimer_Tick` gates on `_isProcessingChange` (bails), but `WindowMonitorTimer_Tick` (the 100ms timer that runs the inline-edit-close -> ValidateCommittedPendingAttrs -> naming path) gated on `_lockedDialogShowing`/`_columnNamingCheckInProgress`/`_scopedCheckInProgress` but NOT `_isProcessingChange`. So while the picker pumped, WindowMonitorTimer re-entered, detected erwin's uniquify inline-edit edge, and stacked the naming Required popup on top. The datatype picker sets none of the naming guards - only `_isProcessingChange` covers it. (My uniquify fix surfaced this: before it, the isNew=false re-validate showed no naming modal, so nothing stacked.)

**Fix (v1 insufficient -> v2 dedicated flag):** v1 added `if (_isProcessingChange) return;` to WindowMonitorTimer_Tick - STILL overlapped in the field. Reason: the picker is ALSO shown from ValidateCommittedPendingAttrs -> Enforce (3865) AFTER ProcessAttributeChanges already reset _isProcessingChange to false, so that flag is not reliably set while the picker is up. v2 = a DEDICATED `_datatypePickerShowing` flag set for the ENTIRE `AllowedDatatypePickerForm.Show` (try/finally so an exception can't wedge it), checked in BOTH timer entry gates (MonitorTimer + WindowMonitorTimer). Placed before any window-edge state is read so the inline-edit-close edge survives and the naming dialog fires SEQUENTIALLY on the next tick after the picker closes. (The naming Comment/Required dialog was already covered by `_columnNamingCheckInProgress`; only the datatype picker lacked a guard.)

**Lessons:**
- **A shared in-flight flag is NOT a reliable modal guard - use a dedicated flag scoped to the modal's Show.** `_isProcessingChange` looked like it covered the picker, but the picker outlives / is shown outside that flag's frame on one call path (Enforce from the pending-commit path, after ProcessAttributeChanges reset it). Guard a modal with a flag set exactly around its Show (try/finally), not with a coarse "am I processing" flag.
- **Every modal-showing path needs its OWN re-entrancy guard checked by ALL timers.** Naming modals set `_columnNamingCheckInProgress`/`_scopedCheckInProgress`; the datatype picker set nothing of its own. When adding a modal, give it a dedicated flag and verify EVERY timer bails on it.
- **Enabling a previously-skipped modal can expose a latent stacking bug.** The uniquify fix didn't create the gap; it made a second modal actually appear, revealing the picker never had a guard. Re-check re-entrancy whenever you make a dialog fire in a new situation.
- **Bail BEFORE mutating edge state** so a deferred tick re-detects the edge; a level-triggered `_wasXOpen` compared to the live state survives the bail and fires next tick.

## 2026-07-09: Datatype Library picker was term-type-blind - it let the user override a BUSINESS_TERM lock

**Symptom (Kursat):** column mapped to a glossary Business term (type fully fixed). User changes its type -> term policy correctly REVERTS + warns -> but then the whitelist picker opens and lets the user pick ANY allowed type ('user picked NUMBER(45)' on a VARCHAR2(250 CHAR) Business column) - silently defeating the term lock. User spec: the picker's type combo must be enabled/disabled per the term type's base-changeability, the parameter field per the length-changeability.

**Mechanism (log+code proven):** (1) EnforceTermTypePolicy reverts and shows a MODAL; while the modal pumps, erwin's delayed 2nd combo-commit re-writes the disallowed value. (2) EnforceAllowedDatatypeWhitelist runs next, deliberately live-reads (sees the re-committed value), and shows the picker - it never read curr.TermTypeCanonical. (3) The pick is written unconditionally and curr.PhysicalDataType advanced, so baseline==live and the term policy never re-fires. Lock semantics (from EnforceTermTypePolicy): BUSINESS_TERM = base+length locked; AMORPH_DATA_TYPE = base free/length locked; AMORPH_DATA_LENGTH = base locked/length free; AMORPH/null/unknown = free.

**Fix:**
- New pure `TermTypeLocks` (single source): `Get(canonical) -> (lockBase, lockLength)` + `Honors(candidate, authoritative, lockBase, lockLength)` (base OrdinalIgnoreCase, length Ordinal - mirrors the policy). Unit-tested (25 cases incl. the live NUMBER(45) override).
- Enforce now derives locks from `curr.TermTypeCanonical` (already on the snapshot; no DB read). Authoritative value = prev.PhysicalDataType, else glossary cache (`GetGlossaryAuthoritativeDatatype` -> GetUdpValues PHYSICAL_DATA_TYPE) for the prev==null new-column sites.
- Term lock overrides the automatic target: a locked column snaps back to the term-authoritative value EVEN if that value is not whitelist-allowed (glossary/whitelist disagreement = admin-data conflict; the add-in must not "fix" it by mutating a locked type - warn instead).
- BUSINESS_TERM (or locked base not representable in the whitelist combo): NO picker at all - nothing is choosable; one warn-only dialog explains the type is fixed (+ admin note on conflict). Authoritative value remembered in _allowedDatatypeUserPicks so the delayed duplicate commit re-enforces it silently.
- Partial locks: picker opens with the locked half pinned+disabled - `lockType` disables the combo (pinned to the authoritative base via preselect), `lockParam` disables the parameter field (pinned via prefill); message notes what is fixed. Remembered-pick re-enforcement now vetted by `TermTypeLocks.Honors` so a stale pre-fix pick cannot resurrect an override.

**Lessons:**
- **Two enforcement machines over the same property MUST share the constraint model.** Term policy and whitelist enforcement each "owned" Physical_Data_Type; the second one's UI (picker) could undo the first one's decision because it did not know the constraint existed. Any corrective UI must be gated by ALL constraints on the value, not just its own.
- **A corrective modal that lets the user choose is itself a write path.** Auditing writes for policy compliance must include values coming back from dialogs (the pick write was unconditional).
- **"Reverted + snapshot advanced" hides later overrides.** Advancing the baseline to the picker's value made the term policy permanently blind to the override. When one machine advances a shared baseline, ask which OTHER machine loses its trigger.
