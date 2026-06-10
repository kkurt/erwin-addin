# DDL Generation pipeline: no model-switch + robust v1 open/teardown

## Findings (2026-06-10, erwin-addin-debug.log + code, workflow-verified)
- First failures (01:50-02:07): model CLEAN -> erwin refused Mart > Review (cmd 1168) with the
  "There have been no changes to model since it was checked out" box. Documented precondition,
  not a regression (MartMartAutomation.cs:696-698). WaitForNewDialog has no title filter so the
  info box was mis-captured as the wizard; generic error at ModelConfigForm.cs:4273-4277.
- Retry 02:36 (model DIRTY, target v1 confirmed by "[PICK] version combo set to index 3 (v1)"):
  failure moved to STEP 1. WaitForNewMartMdiChild(8000ms) timed out on a cold erwin; the v1 PU
  WAS loaded (PRE-CLOSE diag: 2 PUs) and its MDI child appeared LATE (title showed v1 at 02:38).
- After every failed run the finally restarts the reconnect timer; the tick sees the leftover v1
  locator "not in known set" (ModelConfigForm.cs:1041) -> ConnectToModel(v1) FULL re-init (the
  perceived "addin reload") -> UDP sync applies 6 creates INTO the pipeline's v1 copy (dirty!)
  -> form binds to v1 -> RebuildRightCombo rebuilds relative to active v1 so the Target combo
  only shows "v1" (user's "only last version in config" observation = symptom, not cause).
- USER REQUIREMENT: the pipeline must NEVER cause a model switch / reconnect / UDP sync on the
  PU it opened itself.

## Plan
### Phase 1 - reconnect guard (the explicit requirement)
- [x] Register the pipeline-owned version locator (active locator with version=vN) in a new
      _pipelineOwnedLocators set when the cross-version pipeline opens vN (ModelConfigForm).
- [x] ReconnectTimer_Tick new-PU scan: skip pipeline-owned locators (same pattern as the
      ;Duplicate=YES guard) + self-prune once the copy's PU is gone (prevents stale guard
      suppressing a later DELIBERATE open of that version).
- [x] Tab-switch path: if currentTitleLoc maps to a pipeline-owned locator (stem+version
      match via IsPipelineOwnedTitleLocator), skip reconnect + re-arm detector.
- [x] finally: POST-CLOSE check via IsPuLocatorStillLoaded; owned PU gone -> unregister;
      still present -> keep guard + LOUD warning (folded into the failure modal, or its own
      "cleanup needed" modal on success). No silent state.

### Phase 2 - STEP 1 robustness (why the dirty retry failed)
- [x] Raise the version-child wait 8000 -> 30000 ms (cold erwin + Mart roundtrip).
- [x] Two-stage WaitForNewMartMdiChild: find the NEW HANDLE title-independently (erwin writes
      the final "Mart://" title only after load), then wait for the Mart title on it.
      beforeChildren snapshot switched to EnumAllMdiChildHandles accordingly.
- [x] Abort path: teardown adopts a LATE-arriving version child by title token (": vN :"),
      excluding PreexistingChildren and only when OpenPosted (never grabs a user tab).

### Phase 3 - teardown correctness
- [x] CloseReviewSession rework: SweepVersionChildCloseDialogs handles Save Models / Close
      Model AND an UNCONDITIONAL Mart Offline watch (clean child raises it directly); child
      death VERIFIED via WaitForWindowGone + one WM_CLOSE retry; unclosable child reported
      loudly (caller guard + warning take over).
- [x] Re-activate the original active MDI child (CCSession.OriginalChild) as the teardown's
      last step.

### Phase 4 - clean-model precondition UX (first night's failure)
- [x] ProbeDirty dirty gate (ProbeActiveModelDirtyForReview) at the top of the cross-version
      branch; blocks ONLY on a positive "clean" reading; clear Turkish message, pipeline not
      launched (nothing opened, nothing to leak).
- [x] Validate the window captured after WM_COMMAND 1168: CollectDialogStaticText detects
      "There have been no changes", dismissed via real OK button (ClickDialogButtonByTextWin32,
      IDOK fallback), CCSession.ReviewRefusedNoChanges -> precise error instead of generic.

### Review fixes (adversarial review confirmed 8/18 findings; all fixed)
- [x] CRITICAL: three guard-blind ConnectToModel(0) paths (disconnected / both count-drop
      branches) could adopt the leftover -> FindFirstAdoptablePuIndex everywhere + HARD
      refusal gate at the top of ConnectToModel (no call site can bind a pipeline-owned PU).
- [x] MAJOR: tab-switch matcher stem-only + locator-different fallback could pick the
      leftover -> pipeline-owned skip in both loops + version-equality requirement when both
      sides expose a version.
- [x] MAJOR: finally's SCAPI walks (LogSessionPUs / IsPuLocatorStillLoaded) could deadlock
      the STA when teardown left a modal up -> IsErwinMainWindowBlockedByModal gate; guard
      stays armed + warning shown instead.
- [x] MAJOR: leftover inflated PU-count bookkeeping (masked the user closing their own
      model) -> guard sweep moved before count==0 return, effectiveCount (real models only)
      used in both count-drop checks + tab-switch gate, pipeline-owned excluded from
      _knownLocators / _lastConnectPuCount seeding.
- [x] MAJOR: ProbeDirty-based dirty gate was INERT on r10.10 (none of the probed property
      names exist on the PU; always "assume dirty") -> gate now reads erwin's own GUI signal,
      the title asterisk of the active MDI child (IsActiveMdiChildDirtyByTitle; matches
      Review accept/refuse in every logged incident; null/unknown -> proceed, erwin decides).
- [x] MINOR: teardown worst case 27s, silent-clean path +2.5s -> childKnownGone skips the
      Save Models / Close Model waits a dead child cannot raise; retry sweep shortened
      (quickRetry); second WaitForWindowGone 5s -> 3s.
- [x] MINOR: late-child adoption was a single instant Mart-title scan (misses the still
      loading child it exists for) -> 10s / 250ms poll.
- [x] Hardening: BuildVersionLocator refuses inputs whose swap is a no-op (VNO form /
      missing version= / same version) so the ACTIVE locator can never enter the guard;
      IsPuLocatorStillLoaded keeps the guard armed when any PU read fails.
- [x] NEW tests/PipelineOwnedLocatorTests.cs (13 cases, reflection on the private statics).

### Live test round 1 (2026-06-10 10:25-10:47) - findings + fixes
What WORKED: two-stage MDI wait (v1 title landed at 8.8-10s, old 8s budget would have failed
all three runs); refusal-box detection + precise Turkish error; teardown closed v1, dismissed
Mart Offline, re-activated v4, disarmed guard; NO model-switch/UDP onto the v1 copy.
Learning: title-asterisk can be TRUE while erwin's Review still says "no changes since
checkout" (run 1: asterisk present, UDP creates=0, erwin refused) - asterisk-free => certainly
clean holds, asterisk => not necessarily "Review will accept". Gate design (block only on
positive clean) is correct; refusal detection owns the rest.
REMAINING BUG FOUND (crash chain, erwin died 10:47 coreclr 0xC0000005):
1. Review wizard appeared LATER than the 6s WaitForNewDialog budget (erwin builds the
   ;Duplicate=YES copy first, 7-15s on this machine) -> pipeline aborted, wizard left open
   with the Duplicate PU alive.
2. 3s after teardown a TRANSIENT empty window-title read ('' for one tick, raw title fine
   150ms later) fired the tab-switch; the locator-different fallback had a pipeline-owned
   skip but NO ;Duplicate=YES skip -> add-in bound the Duplicate (ConnectToModel(1) 10:43:29).
3. User closed the leftover wizard -> erwin released the Duplicate -> _currentModel = dead
   RCW -> next Generate DDL click AV'd in ParseActivePuVersion (event log confirms stack).
- [x] Wizard-open wait 6000 -> 30000 ms (refusal box still arrives <200ms, no cost).
- [x] Teardown late-wizard watch (WizardLaunchPosted + DialogsBeforeWizard, 15s poll,
      no-changes-box excluded) -> IDCANCEL releases the Duplicate under our control.
- [x] ;Duplicate=YES excluded from EVERY bind path: tab-switch matcher, locator-different
      fallback, and the ConnectToModel hard refusal gate (unconditional - erwin's own Review
      button creates these too).
- [x] Two-tick debounce for EMPTY title reads in the tab-switch detector (real switch to a
      local tab still fires after 1s; a one-tick caption steal no longer reconnects).

### Live test round 2 (2026-06-10 11:04-11:46, crash-fix build) - results
1. v4 vs v4 clean: correct "no diff"; BLACK RECTANGLES on the diagram afterwards - the known
   DWM compositor leak; log shows "[DWM-WARMUP] ... VISIBLE" yet the bridge still logged
   "opening hidden wizard" (warm-up flag may not reach the bridge). PRE-EXISTING, cosmetic,
   separate follow-up - NOT from today's changes.
2. clean vs v1: gate read DIRTY (asterisk present AGAIN on an untouched model - asterisk
   over-reports on this model right after open), Review posted, erwin refused, box detected
   and dismissed via OK, precise Turkish error, teardown PERFECT (v1 closed, Mart Offline
   dismissed, v4 re-activated, 1 PU, guard disarmed). ~29 s wasted but no leftovers.
3. dirty vs v1: END-TO-END SUCCESS. Review wizard took 7.6 s to appear (the new 30 s budget
   saved the run; the old 6 s would have failed). DDL captured (3419 chars), Duplicate
   released under control, every teardown step verified, no reconnect/UDP side effects.

### Clean-model cross-version support (user requirement: "not-dirty v4 vs v1 must work")
- [x] Dirty gate converted from BLOCKER to ROUTER: positively clean -> launch directly via
      Complete Compare (WM_COMMAND 1082; LEFT = last-saved baseline, no dirty precondition -
      the dormant Faz-3 entry, built+tested 2026-05-29).
- [x] In-flight fallback INSIDE DriveCompareToResolveDifferences: when erwin refuses Review
      (gate had said dirty - the asterisk over-reports), the refusal box is dismissed and the
      compare relaunches via 1082 in the SAME session, reusing the open v1 child and the
      shared wizard navigation. Refusal+relaunch-failed gets its own precise error.
      (Semantics: erwin's refusal PROVES open state == checked-out baseline, so CC's
      last-saved LEFT is the same compare - deterministic routing, not a silent fallback.)

### Live test round 3 (2026-06-10 13:41-13:53) - ALL ROUTES VERIFIED, user: "hepsi oldu!"
- v4 vs v4 (OnFE): DDL 1727 chars. NO black rectangles this round (nothing was changed for
  them; the one-time DWM warm-up ran in both rounds, failed 11:06 / worked 13:43 -> warm-up
  is unreliable, follow-up stays open: the VISIBLE flag may not reach the bridge).
- dirty vs v1 (Review): RD reached, DDL 3420 chars, teardown verified, guard disarmed.
- clean vs v1 (NEW CC route, first live run): probe=clean -> direct 1082 -> RD -> DDL 1693
  chars, teardown verified, guard disarmed. Asterisk over-report is intermittent (absent
  this time), so both the direct route and the refusal-relaunch matter.

### Status
- DONE and live-verified end-to-end. Build 0 warnings / 0 errors; tests 235/235.
- Open follow-up: same-version path DWM black-rectangle warm-up reliability.
- Commits pending user's split decision (ModelConfigForm.cs carries unrelated uncommitted
  session-tracking edits): proposal = feat(tracking) first, then one fix(ddl) commit.

## Verify
- Build 0/0, existing tests green.
- Live: cold-erwin dirty v4 vs v1 -> DDL produced, NO reconnect / UDP dialog / leftover tab /
  Mart Offline. Clean-model run -> clear message, nothing opened, form still bound to v4.

---

# Addin Session Tracking + Remote Shutdown

## Goal
One ADDIN_SESSION row per erwin/addin run. Heartbeat LAST_SEEN; obey admin SHUTDOWN_TYPE
(GRACEFUL/FORCE); stamp END_TIME on close. Corporate-gated via CORPORATE_PROPERTY. Best-effort:
DB errors never block modeling.

## Approach (decided from recon)
- **EF (RepoDbContext)** for ALL DB ops - entities already in MetaShared:
  `AddinSession` (table ADDIN_SESSION), `Corporate` (MC_CORPORATE), `CorporateProperty`.
  `new RepoDbContext(DatabaseService.Instance.GetConfig())` is the exact pattern GetEffective uses.
  EF handles dialect + identity, so NO hand-written MSSQL/Oracle/Postgres SQL.
- New process-level singleton `Services/SessionTrackingService.cs` (mirrors DatabaseService.Instance).
  Survives model switches; one session per erwin process.
- Background only: initial INSERT + heartbeat run on `System.Timers.Timer` / Task.Run (OFF erwin's
  STA UI thread, so no host hang per the UI-thread-hang lesson). Close actions are thread-safe.
- Reuse `ConfigContextService.ParseEffectiveBool/ParseEffectiveInt` (public static) for settings.
- Reuse `AddinSession.ShutdownTypes.Graceful/Force` constants (no magic strings).

## Behavior (all UTC)
1. Start() once per process (idempotent). Background:
   - cfg = DatabaseService.GetConfig(); if not configured -> log + stop.
   - corporateId = ctx.Corporates.Select(c => (int?)c.Id).FirstOrDefault()  // single MC_CORPORATE row
   - USER_TRACKING_ENABLED (CORPORATE_PROPERTY for that corp). Not "True" -> stop (do nothing).
   - interval = USER_TRACKING_INTERVAL_MINUTES (default 5; <=0/invalid -> 5).
   - INSERT AddinSession {CorporateId, WindowsUser=Environment.UserName, MachineName=Environment.MachineName,
     ProcessId=current pid, AppVersion, StartTime=UtcNow, LastSeen=UtcNow}; keep Id in memory.
   - start timer at interval minutes.
2. Tick (background, re-entry guarded):
   - load our row by Id; if missing -> stop. Set LastSeen=UtcNow; SaveChanges. Read ShutdownType.
   - GRACEFUL -> WriteEndTime(); graceful close.
   - FORCE    -> WriteEndTime(); force close.
   - null/empty -> nothing (admin may have cancelled).
3. WriteEndTime(): idempotent (guard). EndTime=UtcNow. NEVER touch SHUTDOWN_TYPE (admin owns it).
4. Normal close: AppDomain.ProcessExit -> WriteEndTime() (SHUTDOWN_TYPE stays null = user closed).
   Crash/kill -> no END_TIME + stale LAST_SEEN (admin detects crash - by design).

## Close mechanics
- GRACEFUL = Win32Helper.CloseErwinMainWindow() (PostMessage WM_CLOSE -> erwin's own Save? prompt).
- FORCE    = WriteEndTime() then immediate process exit (no save). DECISION below.

## Open decision (ask the user)
- FORCE termination: Environment.Exit(0) [clean managed exit, no Kill API, less SEP-risky] vs
  Process.GetCurrentProcess().Kill() [hardest/most immediate]. Recommend Environment.Exit(0).

## Files
- NEW: Services/SessionTrackingService.cs (singleton, EF, timer, close).
- EDIT: ErwinAddIn.cs (ExecuteBody) OR ModelConfigForm init -> SessionTrackingService.Instance.Start() once.
- (no MetaShared/admin changes; table comes via install/migration.)

## Verify
- Build 0/0; tests for the pure decision helper (DecideShutdownAction(string) -> enum).
- Never throw out of the service; every DB call try/catch + log (no silent swallow).

## Review (DONE 2026-06-09)
- NEW Services/SessionTrackingService.cs (process singleton, EF, System.Timers.Timer, best-effort).
- EDIT ErwinAddIn.cs ExecuteBody: SessionTrackingService.Instance.Start() after form Show (idempotent).
- NEW tests/SessionTrackingDecisionTests.cs (13 cases for DecideShutdownAction).
- FORCE = Environment.Exit(0) (user-confirmed).
- Build 0/0, tests 222/222.
- Spec cross-check: corporate via single MC_CORPORATE row; USER_TRACKING_ENABLED gate (absent=off);
  interval default 5 (<=0/invalid=5); INSERT with UserName/MachineName/pid/version/UtcNow; per-tick
  LAST_SEEN refresh + SHUTDOWN_TYPE read; GRACEFUL=WM_CLOSE (erwin Save? prompt), FORCE=exit; END_TIME
  at real close (ProcessExit) + explicit before FORCE exit; SHUTDOWN_TYPE/REQUESTED_* never written;
  EF change-tracking guarantees we only UPDATE the columns we own (LAST_SEEN / END_TIME), so a
  concurrent admin SHUTDOWN_TYPE write is never clobbered; all UtcNow; all DB calls try/catch+log.
- Deviation (flagged): for GRACEFUL, END_TIME is stamped at the ACTUAL exit (ProcessExit) not before
  posting WM_CLOSE, so a user who CANCELS erwin's Save? prompt is not falsely marked closed and a later
  FORCE is still obeyed. FORCE keeps the literal "END_TIME before close".
