# Value Template v2: UDP target + {Udp:...} source + pipe functions (2026-07-19) - DONE + LIVE-VERIFIED

## Live verification result (2026-07-19, MetaRepoTmp+Zeynep, config 1012)
- [x] Kural A (UDP target + funcs + related source): column 'Xyz' ->
      [TEMPLATE-APPLY] Attribute.Physical.TemplateTargetTest='TABL_xyz'.
- [x] Kural B ({Udp:Application|upper|left:3}): with Application model UDP set
      -> 'UYG'; with it empty -> [TEMPLATE-SKIP] (never-write-empty contract).
- [x] Applied at NAME-COMMIT moment (editor still open) - value visible without
      closing the Column Editor (user requirement).
- [x] CRASH FIX: writing at editor-CLOSE raced GDM teardown -> fatal AV in
      EM_GDM!GDMActionSummary::GraftPostState (dump erwin.exe.51960.dmp). Moved
      the apply into the pending-name drain (proven-safe editor-open window, same
      as required-UDP prompt); editor-close heartbeat stays as idempotent catch-up.
      Added [TEMPLATE-WRITE] pre-write marker so a future native death is traceable.
- [x] Seed rules + test UDP defs REMOVED from both DBs after the test.


## Request (user, 2026-07-19)
Admin side done, migration 9 live (verified on ALL 9 MetaRepo* DBs: TARGET_UDP_ID
column + CK_MC_NAMING_TARGET_XOR + MC_UDP_DEFINITION present). Extend the add-in
Template resolver:
1. New token source {Udp:Name} - read a UDP of the SAME object (name may contain ':').
2. Per-token pipe function chain, left to right: trim, upper, lower, left:n,
   right:n, substr:start:len, replace:a:b.
3. If rule has TARGET_UDP_ID -> write rendered value into that UDP instead of a
   property. XOR with PROPERTY_DEF_ID (DB CK enforces).
4. PRESERVE contract: FillMode (OnlyIfEmpty/Always), ApplyOn (Create/Update/Both),
   AND/OR condition gating, "error out, never write empty". Same pipeline, no
   separate "UDP formula" path.

## Current state (explored 2026-07-19)
- Grammar lives in pure NamingTemplateEngine.Render (Services/NamingTemplateEngine.cs:69).
  Sources today: {Prop} (no dot) + {Alias.Prop} (first dot). NO pipe, NO Udp:.
- Apply sites: ApplyColumnTemplateRules (ValidationCoordinatorService.cs:5427) and
  ApplyPrimaryKeyRules (:5679). Write = obj.Properties(rule.PropertyCode).Value.
  TABLE-object template rules have NO apply site today (unchanged by this work).
- Loader GetQuery/LoadStandards (NamingStandardService.cs:565/322) does NOT read
  TARGET_UDP_ID; rule model has no TargetUdp fields.
- UDP read for conditions: NamingValidationEngine.ReadUdpValue (:742) - owner class
  from objectType + "{Owner}.Physical.{name}" + Model.Physical fallback (private).
- UDP value write canonical: UdpRuntimeService.TrySetUdpProperty (:681) - set, on
  reject Properties.Add then retry. Currently private instance (uses no state).
- Live DB: MC_UDP_DEFINITION.OBJECT_TYPE in {MODEL, TABLE, COLUMN}. Rule 1167 =
  the only live Template (PK_{Table.Physical_Name}, PK, property target).

## Status (2026-07-19)
- [x] Steps 1-7 done: engine v2 + loader + apply sites + 38 new tests.
      736/736 green; both flavors 0 warn / 0 err. Loader SQL verbatim-verified
      on MetaRepoZeynep (rule 1176 resolves TARGET_UDP_NAME/OBJECT_TYPE; 1167
      untouched).
- [ ] Step 8 in-erwin part: seeded UDP 2040 'TemplateTargetTest' (COLUMN) +
      rule 1176 '{Udp:Application|upper|left:3}_{Physical_Name|lower}' ->
      TARGET_UDP_ID 2040, APPLY_ON=Create, Always, AUTO_APPLY=1, config 1012.
      erwin was RUNNING at install time - awaiting user OK to restart, then:
      new column in a 1012 model (e.g. SQL/1_DEV/EK_KART) -> expect
      [TEMPLATE-APPLY] ... Attribute.Physical.TemplateTargetTest='APP_<col>'.
      Cleanup after test: DELETE the two seeded rows (script in scratchpad).

## Plan
- [ ] 1. NamingTemplateEngine grammar v2 (pure, all unit-testable):
      token = SOURCE ("|" FUNC(":"ARG)*)*. Split inner token on '|': seg0=SOURCE,
      rest=funcs. SOURCE dispatch ORDER: "Udp:" prefix (OrdinalIgnoreCase) FIRST
      (rest = UDP name, may contain ':' and '.'), else first-dot Alias.Prop, else
      own Prop. New optional 4th delegate udpReader; {Udp:X} with null reader =
      TemplateResolutionException (no silent skip).
- [ ] 2. Function chain evaluator in the engine: 7 funcs, left to right.
      Malformed = TemplateResolutionException (unknown name, wrong arg count,
      non-int/negative n). After the full chain the FINAL value must be non-empty,
      else throw (extends the never-write-empty contract).
- [ ] 3. Self-ref guards pipe-aware: ReferencesOwnProperty must compare the SOURCE
      (strip |chain) - {Physical_Name|upper} targeting Physical_Name IS self-ref.
      New ReferencesOwnUdp(template, udpName) for UDP-target rules (runaway guard,
      same rationale as PK_ '+Always' runaway).
- [ ] 4. Rule model + loader: NamingStandardRule += TargetUdpId(int?),
      TargetUdpName, TargetUdpObjectType. All 3 SQL dialects: select
      ns.TARGET_UDP_ID + LEFT JOIN MC_UDP_DEFINITION tudp -> NAME/OBJECT_TYPE.
      Reader maps; both-set rows (CK-violating) skip+log like condition XOR skip.
      GetTemplateRules filter: ValueTemplate + (PropertyCode OR TargetUdpName).
- [ ] 5. Apply sites (Column + PK), shared flow unchanged (ApplyOn -> conditions ->
      self-ref -> Render -> FillMode -> idempotent -> AutoApply prompt -> write):
      if TargetUdpId set -> target path "{OwnerClass}.Physical.{TargetUdpName}",
      current-value read sparse-safe, write via TrySetUdpProperty (make it
      internal static in UdpRuntimeService - it uses no instance state - and
      reuse, no duplicate). Guard: TargetUdpObjectType must equal the rule's
      object type (COLUMN rule -> COLUMN UDP), mismatch = skip + [TEMPLATE-SKIP]
      log, never silent. udpReader delegate = public wrapper over
      NamingValidationEngine.ReadUdpValue (keeps Model.Physical fallback so
      {Udp:ApplicationCode} on a column reads the MODEL UDP).
- [ ] 6. Tests (NamingTemplateEngineTests + new): each func, chaining order,
      malformed funcs, empty-after-chain, {Udp:name-with-colon-and-dot}, no
      udpReader, back-compat (pipeless templates byte-identical), pipe-aware
      self-ref, loader filter. Full suite green.
- [ ] 7. Build both flavors 0 warn / 0 err.
- [ ] 8. LIVE verification on real model (build-and-run): (a) rule 1167 PK
      property template unchanged; (b) new COLUMN rule with TARGET_UDP_ID +
      {Udp:...} source + function chain writes expected UDP value; seed test rule
      in MetaRepoZeynep config 1012, then remove it.

## Assumptions (flagged for user)
- A1 upper/lower = ToUpperInvariant/ToLowerInvariant (NOT tr-TR; DB-identifier
  context, consistent with glossary CASE_INSENSITIVE=OrdinalIgnoreCase decision).
- A2 Func names case-insensitive; replace args used verbatim (no trim), 'a'
  non-empty, args cannot contain ':' or '|' (grammar separators); numeric args
  int >= 0.
- A3 left:n / right:n with n >= length -> whole string; substr start beyond end ->
  empty (then the final-empty error applies if nothing else remains).
- A4 UDP-target only meaningful for COLUMN/TABLE-object rules today (PK has no
  matching UDP OBJECT_TYPE); mismatch guarded+logged. TABLE template rules still
  have no apply site (pre-existing, out of scope).


# DDLGENERATOR build flavor: dedicated DDL-generation add-in (2026-07-11) - PLAN APPROVED 2026-07-12

## Phase 7 - Unattended robustness (2026-07-13, live-test findings)
- [x] Self-healing restart: Mart server enforces a ~4h ABSOLUTE session
      timeout (keep-alive ping can't extend it; an in-place drop -> "Access
      Denied" modal -> stalled worker -> erwin crash). DDL-generator now
      restarts erwin for a fresh session: PROACTIVE (session age >=
      MartSessionMaxAgeMinutes=210, idle only) + REACTIVE (keep-alive detects
      drop). MartMartAutomation.RequestErwinRestart: dismiss blocking modal ->
      WM_CLOSE main -> popup dismisser -> 20s force-kill fallback. Watcher
      relaunches. _martLoginTimeUtc stamped at login.
- [x] Startup popup auto-dismiss (blocks add-in load, must be handled BEFORE
      add-in loads -> WATCHER): license-expiry warning + Welcome/Start Page
      DISABLE erwin's main frame. Watcher.DismissBlockingStartupDialog
      (GetWindow(main,GW_ENABLEDPOPUP) -> WM_COMMAND IDOK) called each
      Wait-ForModel iteration. Add-in DismissBlockingStartupDialog is the
      post-load backstop.
- [x] Configuration Warning suppressed in DDLGENERATOR: a config-less model
      (the bootstrap) used to pop "Add-in loaded with controls disabled" modal
      (nobody clicks OK on the worker VM). `#if DDLGENERATOR` -> log + degrade
      silently, no modal.
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] OPEN: MartSessionMaxAgeMinutes hardcoded 210 - move to DDL_GENERATION_CONF
      if the server timeout differs per site. Confirm ~4h is the real timeout
      (single observation 2026-07-13: 18:26 login -> 22:26 drop).
- [ ] LIVE TEST: leave running >3.5h -> proactive restart before the 4h drop;
      + license popup path when license nears expiry.



## Requirements (user, 2026-07-11)
1. Compile-time flavor: built with a "DDLGenerator" flag the worker mode is
   ALWAYS on - the checkbox is removed.
2. The watcher for this flavor loads the add-in as soon as erwin runs (no
   model-open wait).
3. Auto Mart login on load: Mart tab > Connect; if Authentication shows
   "Server Authentication" fill User Name + Password (from DB), Windows auth
   fills nothing; click Connect; dismiss the optional "Mart Connected
   Successfully" OK box. Keep-alive: every N minutes (N from DB, default 5)
   Mart > Open then Cancel; last-activity timestamp also reset by a DDL job
   START; keep-alive must NEVER run while a DDL generation is active.
   Auth type + credentials + timeout interval all come from the admin DB.
4. UI: only the General tab visible, a "DDL Generation MODE ON!" banner, no
   other buttons (dev controls still visible in DEV builds).

## Phase 0 - Spikes (must close before coding; ~half day, on the worker VM)
- [x] S1 RESOLVED 2026-07-12 (live tests on the dev machine):
      (a) WM_COMMAND(1181) on a model-less erwin = NO-OP, even with the
          Welcome dialog closed (MFC UPDATE_COMMAND_UI disable confirmed).
      (b) NO startup-autoload registry value exists (Add-Ins\<name> has only
          Menu Identifier / ProgID / Invoke Method / Invoke EXE).
      (c) OPTION F PROVEN: start erwin WITH a bootstrap .erwin argument
          (copy of BlankTemplate.erwin) -> title 'erwin DM - ddlgen-bootstrap'
          -> post 1181 -> add-in LOADED (dev DB picker appeared = Execute ran).
      DECISION: DdlGeneratorMode watcher launches erwin with a bundled
      bootstrap model (installer ships it); existing wait-for-model + post
      flow stays unchanged; the DDLGENERATOR add-in closes the bootstrap
      model (discard) right after load, then logs into Mart. (Add-in
      surviving model-less is already proven in production logs.)
- [x] S2 RESOLVED 2026-07-12 (user RECON capture): Mart > Connect =
      WM_COMMAND 1059. New const CMD_MART_CONNECT = 1059.
- [x] S3 RESOLVED 2026-07-12 (Ctrl+Alt+D dump of 'Connect to Mart' #32770):
      Server Name  = Edit    id=1005
      Port         = Edit    id=1007
      Use SSL      = Button  id=35797 (checkbox)
      App Name     = Edit    id=1020 (disabled)
      Authentication = ComboBox id=1011 (text e.g. 'Server Authentication')
      User Name    = Edit    id=1012
      Password     = Edit    id=1013
      Recent Conns = SysListView32 id=1017
      Connect      = Button  id=1002   Cancel=2  Help=9
      Phase-4 automation drives these BY ID (GetDlgItem), not by text.
      Bonus finding: the bootstrap model opened READ-ONLY (title suffix
      '(Read-Only)') - ship the bootstrap .erwin with the read-only file
      attribute so its close can never raise a save prompt.

## Phase 0 status: COMPLETE (S1+S2+S3). Next: Phase 1 after user approval.

## Phase 1 - Build flavor - DONE 2026-07-12
- [x] csproj: `-p:DdlGenerator=true` adds `DDLGENERATOR` to DefineConstants
      (mirrors the PackagedBuild=true -> PACKAGED pattern; combinable with
      both PACKAGED and DEV).
- [x] IsDdlDedicatedInstance: compile-time (`#if DDLGENERATOR` true, else
      false). chkDdlWorker checkbox REMOVED everywhere (creation, reveal
      gesture, Designer field, CheckedChanged incl. the live-toggle re-init);
      HKCU DdlWorker\Enabled flag code deleted. Worker auto-starts from
      ModelConfigForm_Load via InitializeDdlWorker() (#if DDLGENERATOR only).
      Normal builds cannot ever start the worker (no caller of
      StartDdlWorker outside the flavor).
- [x] build-and-run.ps1 + package.ps1: -DdlGenerator switch -> passes
      -p:DdlGenerator=true (package keeps PackagedBuild=true too).
- [x] Single-worker mutex: Local\EliteSoft.ErwinAddIn.DdlWorker acquired in
      InitializeDdlWorker; not acquired -> LOUD log + red status, worker NOT
      started. AbandonedMutexException treated as acquired (prior owner died).
- [x] Verified: both flavors build 0 warn / 0 err; 605/605 tests; raw-byte
      string check proves the flavor-only code exists ONLY in the
      -p:DdlGenerator=true DLL.

## Phase 2 - UI restriction (DDLGENERATOR only) - DONE 2026-07-12
- [x] ApplyDdlGeneratorUiRestrictions (DdlWorker partial, ctor after
      InitializeGeneralTab): tabValidation/tabTableProcesses/tabDdlGeneration
      REMOVED from the TabControl (not disposed - the worker pipeline drives
      the DDL tab's controls programmatically; tabIntegrate never appears in
      DDL-only mode, Debug Log tab was already retired).
- [x] Red banner "DDL Generation MODE ON!" top-right of the General header
      (x=360, clear of title and cards); subtitle text swapped to "Dedicated
      DDL generation instance..."; form title suffix " - DDL Generator".
- [x] Buttons hidden in flavor: General-tab "Close erwin" + the bottom
      status-bar Close (either would kill the worker with one click).
      `#if DEV` controls (Change DB / Reload Config, RECON hotkeys) untouched.
- [x] build-and-run-ddlgenerator.ps1 wrapper added (user request): calls
      build-and-run.ps1 -DdlGenerator (same-CLSID replace warning in header).
- [x] Both flavors 0 warn / 0 err; 605/605 tests.
      NOTE: banner placement needs one live visual check on the next
      -DdlGenerator dev install (absolute coords; expected clear, unverified).

## Phase 3 - Worker config table + service - DONE 2026-07-12 (CORRECTED to real schema)
- CORRECTION: DDL_GENERATION_CONF is an EXISTING admin-system table, not one we
  create. My initial CREATE-TABLE script was wrong (USERNAME/PASSWORD/IS_ACTIVE)
  and was DELETED. Real schema (live DB MetaRepoZeynep): ID, CORPORATE_ID,
  API_KEY_HASH, MART_USER, MART_PASSWORD (encrypted), UPDATED_AT, MART_SERVER,
  MART_PORT, MART_USE_SSL (bit), MART_AUTH_TYPE (default 'SERVER'),
  + KEEPALIVE_MINUTES (int NULL) - admin added this column 2026-07-12.
- Decisions: row selection = the single row (zero->disabled, 2+->ambiguous
  refuse); keep-alive minutes = the new KEEPALIVE_MINUTES column.
- [x] DdlWorkerConfigService.ReadActiveConfig: real columns; single-row contract
      (reads first row, then detects a second -> null+loud log); decrypts
      MART_USER/MART_PASSWORD (Server auth) via DecryptConnectionSecret, decrypt
      failure/echo -> null (no silent fallback). Windows auth skips creds.
      Reads MART_USE_SSL + CORPORATE_ID (logged).
- [x] DdlWorkerConfig POCO: + UseSsl, + CorporateId. ParseAuthType,
      NormalizeKeepAliveMinutes, IsKeepAliveDue unchanged.
- [x] 24 unit tests (DdlWorkerConfigTests, pure logic) - 629/629 green; both
      flavors 0 warn / 0 err. Live DB column verified via sqlcmd.

## Phase 4 - Mart auto-login automation - CODE DONE 2026-07-12 (needs live test)
- [x] MartMartAutomation.ConnectToMart(cfg, log) - all pure Win32:
      1. Post Mart>Connect (WM_COMMAND 1059) to XTPMainFrame; wait for the
         "Connect to Mart" #32770 dialog (10s). No dialog -> ProbeMartConnected
         (Mart>Open: picker => AlreadyConnected, "Connect to Mart" => not).
      2. EnsureAuthCombo: align combo 1011 to cfg.AuthType (CB_SELECTSTRING +
         CBN_SELCHANGE) so credential fields enable/disable right. Optional
         server/port -> ids 1005/1007.
      3. SERVER -> WM_SETTEXT user (1012) + pass (1013); WINDOWS -> nothing.
         Click Connect (1002; text match + id fallback).
      4. WaitForLoginOutcome (25s): Connect dialog closes = LoggedIn; success
         "erwin Data Modeler" box OK'd (OK only, never the checkbox); error box
         while dialog open = Failed; timeout = Failed + Cancel the dialog.
- [x] Worker gating: DdlWorkerTryStartNextJob has a `#if DDLGENERATOR` login
      gate - no job claim until _martLoginVerified. EnsureMartLogin (non-
      blocking): reads DDL_GENERATION_CONF once (60s backoff on
      missing/undecryptable), runs ConnectToMart on a background Task (25s+
      dialog waits must not freeze erwin UI), marshals result to
      OnMartLoginComplete (verified+stamp _lastMartActivityUtc | 60s retry).
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] LIVE TEST (next): assumptions to confirm on the worker erwin -
      (a) Mart>Connect (1059) works model-less;
      (b) the success box title is "erwin Data Modeler" / text contains
          "Connected"/"Successfully" (WaitForLoginOutcome keys on that);
      (c) the auth combo items start with "Server"/"Windows".
      All are logged verbatim ([MART-LOGIN] ...) so one run captures any drift.

## Phase 5 - Keep-alive ping - CODE DONE 2026-07-12 (needs live test)
- [x] _lastMartActivityUtc stamped on: login success, ping success, JOB
      COMPLETION (both OnDdlWorkerCloseComplete paths, `#if DDLGENERATOR`).
- [x] MartMartAutomation.PingMartSession = ProbeMartConnected (Mart>Open ->
      picker=alive+IDCANCEL / "Connect to Mart"=dropped+IDCANCEL), shared with
      the login probe via a `tag` param ([MART-KEEPALIVE] prefix).
- [x] Worker tick gate (after login-verified, before claim):
      MaybeStartKeepAlivePing - IsKeepAliveDue(_lastMartActivityUtc, now,
      _keepAliveMinutes, busy, pingActive); busy = _ddlQueueActive ||
      _martMartPipelineActive || _currentDdlJob!=null (defensive; tick already
      returned on the first two). Ping runs on a background Task (dialog waits
      must not freeze UI); returns true so no claim while pinging.
- [x] Live-refresh: the ping task re-reads DDL_GENERATION_CONF for the current
      KEEPALIVE_MINUTES (admin edit takes effect within one interval); login
      also seeds it. OnKeepAlivePingComplete: alive -> stamp; dropped ->
      _martLoginVerified=false + immediate re-login (login gate drives
      Mart>Connect again).
- [x] Both flavors 0 warn / 0 err; 629/629 tests.
- [ ] LIVE TEST: with KEEPALIVE_MINUTES=1, leave the worker idle > 1 min and
      confirm [MART-KEEPALIVE] due -> ping OK cycle; then confirm a job resets
      the clock (no ping right after a job).

## Phase 6 - Watcher + bootstrap auto-load - DEV DONE 2026-07-12 (prod installer TODO)
- [x] installer/assets/ddlgen-bootstrap.erwin (copy of BlankTemplate, git-tracked).
- [x] autostart-watcher.ps1: reads HKCU DdlGeneratorMode/BootstrapModelPath/
      ErwinExePath; in DDL-gen mode, when erwin is NOT running it LAUNCHES
      erwin itself with the bootstrap (Start-ErwinWithBootstrap, Resolve-ErwinExe
      known-paths fallback), then the existing Wait-ForModel + post flow runs
      untouched. Non-DDL builds unaffected (mode flag = 0).
- [x] Add-in (DDLGENERATOR): MartMartAutomation.CloseBootstrapModelIfActive
      (title marker "ddlgen-bootstrap" -> WM_CLOSE active MDI child, read-only
      so no save prompt). Worker tick bootstrap gate runs BEFORE the login
      gate; one-shot (_bootstrapHandled). Never touches a non-bootstrap model.
- [x] build-and-run.ps1 -DdlGenerator: copies bootstrap to installDir (read-
      only), writes the 3 HKCU values; normal build clears DdlGeneratorMode=0.
- [x] Both flavors 0 warn / 0 err; watcher + build-and-run parse clean.
- [ ] PROD installer (install-impl.ps1 / package.ps1 -DdlGenerator): copy
      bootstrap + write HKCU flags (same as build-and-run). NOT done yet -
      dev flow (build-and-run-ddlgenerator.ps1) covers testing first.
- [ ] LIVE TEST: run build-and-run-ddlgenerator.ps1, CLOSE erwin, watch the
      watcher launch erwin+bootstrap -> add-in loads -> [DDL-BOOTSTRAP] closes
      it -> [MART-LOGIN] -> job. autostart.log + erwin-addin-debug.log.

## Phase 7 - End-to-end verification + docs
- [ ] Fresh logon -> watcher -> erwin (no model) -> add-in loads -> auto
      login -> job -> no-diff job -> 5-min keep-alive observed -> second job.
      Log markers: [DDL-ONLY], [MART-LOGIN], [MART-KEEPALIVE], [FORM].
- [ ] README + docs/ARCHITECTURE.md + memory update.

## Decisions (user, 2026-07-12)
1. Credentials: stored in DDL_GENERATION_CONF encrypted the same way the
   glossary CONNECTION_DEF credentials are (DecryptConnectionSecret,
   erwin-admin writes them).
2. Normal interactive builds lose the worker entirely (checkbox removed;
   worker exists only in the DDLGENERATOR flavor). CONFIRMED.
3. Keep-alive stamp at job END. CONFIRMED.
4. Spikes (Phase 0) are the first implementation step. CONFIRMED.
   S1 outcome still unknown (model-less load) - riskiest item; plan assumes
   one of the two paths (WM_COMMAND post OR erwin startup-autoload) works.

---

# DDL-dedicated instance mode + form hide during automation (2026-07-11 round 3) - DONE

## Findings (job-6 retest + manual CC hang, log 20:17-20:41)
- Job 6 SUCCEEDED end-to-end: empty-RD no-diff detected, row DONE (ddlLen=80 note),
  quiesced close worked first try (Mart Offline dismissed, HandleSessionLost reset).
- User then MANUALLY opened v2+v1 and launched Complete Compare: the add-in had
  adopted BOTH models (full validation init) and UDP-synced BOTH (creates=6,
  updates=2 each - both dirtied), holding a live session on v1. The manual
  compare stuck at "Comparing / Processing Left Model"; during the hang the
  add-in was idle (timers modal-guarded; only DB-only glossary refresh ran).
  Add-in's background interference (dirty writes + open session + walks) is the
  only delta vs vanilla erwin.

## Plan
- [x] A: DDL-only mode: when chkDdlWorker is ON the instance is DDL-dedicated:
      InitializeValidationService skips glossary/naming/predefined/dependency/
      UDP sync/UDP runtime/monitors/validate-tab; keeps ConfigContext + DBMS
      mismatch guard + General tab + PopulateVersionCombos (DDL gates + combo).
      IsDdlDedicatedInstance predicate lives in the DdlWorker partial.
- [x] B: glossary auto-refresh tick no-ops in DDL-only mode.
- [x] C: checkbox live-toggle re-runs InitializeValidationService(closeConfigLess
      MartModel:false) so the mode applies without restart.
- [x] D: HideFormForAutomation/RestoreFormAfterAutomation: manual pipeline hides
      at start, restores at tail (only when _ddlWorkerState==Idle); worker jobs
      hide at claim and restore in OnDdlWorkerCloseComplete (success + give-up),
      so the Save-Models checkbox mouse-sim can never land on add-in UI.
- [x] E: build 0 warn/0 err + 605 tests green.

## Review
- Manual-CC-hang root: add-in was IDLE during the hang (timers modal-guarded);
  delta vs vanilla erwin = UDP-sync dirty writes on BOTH manually opened models
  + live session + walks. DDL-only mode removes all of it on the worker
  instance. If the hang reproduces with the add-in quiet, it is native erwin
  behavior (test with worker checkbox OFF + fresh erwin to isolate).
- Worker still needs: ConfigContext (job gates), DBMS-mismatch guard,
  PopulateVersionCombos (right-version combo), reconnect tick (adoption).

---

# Auto-DDL worker: no-diff compare freezes erwin (job 4 incident, 2026-07-11) - DONE

## Root cause (from %TEMP%\erwin-addin-debug.log 17:34-17:43)
1. Job 4 (v2 vs v1) had NO differences. After CC_COMPARE the pipeline only waits
   1.5s for a popup then 10s for Resolve Differences / Type Resolution. The no-diff
   outcome (erwin info box arriving AFTER the compare finishes, or RD simply never
   opening) is not watched, so the run dies with the generic "did not reach Resolve
   Differences" FAILED.
2. Teardown posts IDCANCEL to the CC wizard and NEVER verifies it closed. It did not
   close: POST-CLOSE diag still shows the ;Duplicate=YES PU; user screenshot shows the
   wizard alive on the Right Model page.
3. Worker cleanup: dirty v2 model cannot close while the CC wizard holds the
   ;Duplicate PU, so CloseActiveMartModelDiscardingChanges returns false and
   OnDdlWorkerCloseComplete retries FOREVER every ~15s (WM_CLOSE + Save-Models sweep +
   ForceForeground each pass): erwin unusable = the reported freeze.
   Side finding: a SECOND erwin instance (PID 65360) ran the worker simultaneously
   (claimed job 2 mid-flight) - HKCU flag is per-user, both processes saw enabled=True.

## Plan
- [x] A: CCSession.CompareNoDifferences flag + IsNoDifferenceInfoText (pure, testable)
      + combined post-Compare watcher (RD | Type Resolution | info box) in
      DriveCompareToResolveDifferences only.
- [x] B: CloseCcWizardVerified escalation (IDCANCEL, verify, dismiss blocking
      child dialog by OK, IDCANCEL again, CC_CLOSE, verify; loud logs). Used on the
      no-diff exit AND in CloseReviewSession instead of the blind IDCANCEL+Sleep(800).
- [x] C: ModelConfigForm cross-version branch: CompareNoDifferences means script=""
      (NOT an error): interactive shows info status, queue writes DONE with the
      explicit note "-- No differences detected between the compared versions; no
      alter DDL required." (upgraded from the silent empty-DDL contract).
- [x] D: DdlWorker cleanup retry CAP (4 attempts): then loud log + Idle +
      DdlWorkerActiveUnattended=false; worker stops hammering, resumes when the
      operator closes the model (Idle guard already waits for model-less).
- [x] E: unit tests for IsNoDifferenceInfoText (19 cases).
- [x] F: build clean (main project 0 warnings / 0 errors) + full test run 605/605.

## Round 2 (2026-07-11 evening, job-5 retest findings)
erwin does NOT show an info box for identical versions: it OPENS Resolve
Differences with an EMPTY diff grid (job-5 log: no "listview ready (items=N)"
line = count stayed 0 for the whole poll; the arrow click on blank canvas can
never fire an EDR tx -> old error "Apply-to-Right did not register (no EDR tx)").
Also the worker's model close aborted after every Save-Models discard (Mart
Offline never raised) because the close ran with the reconnect tick + validation
walks resumed and the add-in's SCAPI session still open on the job model; the
pipeline's v1 child (no session, monitoring suspended) closed clean seconds
earlier.
- [x] ApplyToRightOutcome enum (Applied | NoDifferences | Failed) in the shared
      ApplyToRightArrowAndWaitForRas: empty grid confirmed with a +3.5s
      count-only watch -> NoDifferences (both Review and From-DB pipelines).
- [x] Review caller: NoDifferences -> script="" + precise status; queue row goes
      DONE with EMPTY RESULT_DDL (no placeholder note - misleading; user
      2026-07-11). From-DB caller: returns (empty, null) -> informational dbMode
      status.
- [x] Defensive tail branch narrowed to script==null so the explicit "" reaches
      the informational no-diff status label.
- [x] Worker cleanup QUIESCE before WM_CLOSE: StopReconnectTimer +
      SuspendValidation + StopMonitoring x2 + CloseCurrentSession; on success
      AND give-up -> HandleSessionLost() (canonical disconnected reset; without
      it the tick's count==0 early-return + suspended monitoring would leave
      _isConnected latched and the worker stuck).
- [x] Build 0 warn / 0 err; tests 605/605.

## Review (2026-07-11)
- New file Services/CcCompareOutcome.cs (public static, pure text classifier,
  #nullable enable) + tests/CcCompareOutcomeTests.cs.
- MartMartAutomation: watcher only ACTS on "erwin Data Modeler"-titled message
  boxes (same family the old 1.5s popup guard targeted); any other new dialog
  (e.g. compare progress meter) is logged once and left alone - zero new risk to
  healthy compares. Unknown erwin boxes keep the old No/Cancel dismiss + abort.
- CloseCcWizardVerified deliberately has NO ForceDestroy (CC engine corruption,
  see reference_cross_version_orphan_unsolved) - worst case it reports loudly and
  the worker's bounded cleanup keeps erwin usable.
- Freeze is eliminated in EVERY branch: even if erwin's no-diff wording is not in
  the classifier, the box is logged verbatim (ground truth for extending the
  list), the run fails explicitly, the wizard close is verified, and the cleanup
  loop is capped at 4 attempts (~1 min) instead of forever.
- NOT changed: dual-erwin-instance worker guard (see Deferred).

## Deferred (noted for user)
- Single-worker mutex per logon session (two erwin processes both ran the worker).
- Exact no-diff info-box title/text ground truth: watcher logs FULL title+text of any
  unexpected dialog so the next live run captures it even if classification misses.

---

# Manual-rename revalidation + Properties-pane dropdown coverage (2026-07-10) - DONE

## From live test of the A-F fixes
- test 1 (picker idle -> uniquify -> rule fires): OK
- test 2 (dialogs show live name): OK
- test 3 (Model Explorer rename existing column to a digit -> rule should fire): FAILED -> fixed
- limitation 1a (Properties-pane dropdown datatype edit unobserved): user chose selection-scoped
  fingerprint -> implemented

## Fixes
- [x] Bug-3 (columns): SPLIT `treatAsNew` into `revalidateAsNew` (validation scope: apply=Create
      rules fire on ANY real rename) vs `treatAsNew` (identity: Cancel deletes-vs-reverts). Manual
      rename now fires rule#1127 but Cancel REVERTS (does not delete the pre-existing column).
      Trigger = NamingValidationEngine.RenameRequiresRevalidation (pure, tested).
- [x] Table + View: same split in the shared TableTypeMonitorService.ValidateNamingStandard
      (revalidateAsNew param + internal RenameRequiresRevalidation detection); threaded a revalidate
      bool through the table heartbeat; view rename site passes it directly. Cancel still reverts,
      never deletes the table/view. (User earlier: "diger objelerde de (Tablo,View) vardir".)
- [x] Task (a): SelectionScopedAttributeCheck - Overview-pane selected entity fingerprinted each
      heartbeat (editor-closed), handle-cached + backoff so no per-second full child enum. Catches
      Properties-pane dropdown datatype/name edits on existing columns.
- [x] Task (a) HARDENING (user: "bazen kaciyor" - edit not caught first time, caught on re-select):
      Overview tracks DIAGRAM selection but a Model-Explorer TREE column selection does not sync, so
      the edited entity was never fingerprinted until re-selected. Fix = fingerprint selected entity
      PLUS a bounded round-robin slice (RollingRescanPerHeartbeat=3) of the baselined working set, so
      the edited table is re-checked within a few seconds regardless of the Overview, no re-select
      needed. Bounded (touched entities only), no spurious popup (stable entity short-circuits).
- [x] Tests: RenameRevalidationTests, OverviewSelectionParseTests. DEV 0/0, packaged 0/0, 571 green.

## Verification (user live)
- [ ] test 3 re-run: rename existing column to a digit name -> rule#1127 fires with LIVE name; Cancel reverts (does NOT delete the column)
- [ ] table/view manual rename to a digit -> naming rule fires; Cancel reverts (does NOT delete)
- [ ] Properties-pane datatype dropdown change on existing column -> caught in ~1-2s ([SEL-SCOPE] + rule)
- [ ] confirm the manual-rename prefix/suffix RE-APPLY side effect is acceptable (else narrow revalidateAsNew to validation-only)

## Still open (declined / caveat)
- Definition/Comment-only Properties-pane edits: no observer (user declined).
- [SEL-SCOPE] relies on Overview reflecting the selection; verify with the log line.

---

# Model Explorer / modal-race validation gaps (2026-07-09) - DONE (A-F approved + implemented)

## Bugs (user report + log/code verified)
- bug-1: erwin auto-uniquify rename (Pre_Abc -> Pre_Abc__1070/__1073) committed WHILE a modal
  was pumping is never validated: rule#1127 (Regexp) bypass. Log never contains the new name.
- bug-2: dialogs print stale names: picker said 'TEST.Pre_Abc' while live was Pre_Abc__1070;
  "Naming standard applied" said 'Abc' -> 'Pre_Abc' while live was Pre_Abc__1073.
- structural: right-dock Properties pane edits on EXISTING objects have no watcher at all.

## Root causes
1. `_datatypePickerShowing` gates BOTH timers for the whole picker modal (57 s in the repro);
   the uniquify commit lands mid-modal, no detector sees it.
2. Post-modal only Physical_Data_Type is live re-read (VCS ~:7134); curr.PhysicalName stays
   stale; the isNew replay validates the STALE name; IsAutoUniquifyRename compares stale-vs-stale
   (baseline snapshot and state are the SAME object/value) so it can never fire here.
3. After the gesture (pending-new consumed), no detector ever rescans that attribute:
   heartbeat is count-only (rename = no delta), ScanForRenamesEventDriven walks ENTITIES only.
   The snapshot-vs-live diff sits unread forever.
4. Dialog texts are built from pre-captured state: picker msg (~:7070), "Naming standard
   applied" (~:7528), required-field fieldLabel built once outside the re-prompt loop (~:7671).

## Fix plan
- [x] A. Helper ReadLivePhysicalName(attr, fallback) + RefreshNameAfterModal(attr, state, ctx):
      live re-read + sync (placeholder-safe), mirroring the live datatype re-read discipline.
- [x] B. Post-modal rename catch in EnforceAllowedDatatypeWhitelist: entry refresh + refresh after
      picker/warn-only/term dialogs; replay condition isNew || renameCaught (Core's
      IsAutoUniquifyRename baseline bridge decides Create-vs-Update for !isNew).
- [x] C. _attrRecheckQueue + ScheduleAttributeRecheck + DrainAttributeRecheckQueue (MonitorTimer):
      targeted live-vs-snapshot re-diff per attr ObjectId, routed through ProcessAttributeChanges.
      Scheduled at Enforce exits, Core Step1/2 name writes, snapshot-advance sites, inline-edit close.
- [x] D. Dialogs resolve LIVE name: picker path via entry refresh; "Naming standard applied"
      re-reads before AND after the modal (Steps 2/3 continue with the live '__NNNN' name);
      required-field label rebuilt per pass; Naming/Domain queue entries carry Attribute/ObjectId,
      ShowConsolidatedPopup prints LiveColumnNameFor.
- [x] E. Gate consistency: _datatypePickerShowing -> _validationModalShowing (+ShowValidationModal
      wrapping warn-only + term dialogs); WindowMonitorTimer bails on _isProcessingChange/_isCheckingForChanges.
- [x] F. Properties-pane / Model Explorer F2 coverage: Win32Helper.GetFocusedInlineEditText reads
      the in-place editor's initial text on the OPEN edge; SelectInlineEditCandidates (pure, cap 8,
      names before types, overflow logged) matches snapshots; close edge schedules rechecks.
      KNOWN GAP: pane datatype edits via dropdown-only and Definition/Comment-only pane edits.

## Verification
- [x] Unit tests: InlineEditCandidateTests (5) - 549/549 total green
- [x] Builds: DEV 0/0, PackagedBuild 0/0
- [ ] Live repro (user): idle >30 s in picker until uniquify lands, confirm rule#1127 fires post-pick
- [ ] Live repro (user): dialog texts show the live (uniquified) name
- [ ] Live repro (user): Properties pane rename of existing column with digits triggers rules

---

# "Integrate" tab - environment promotion front-end (2026-06-22)

Read-only runtime consumer of the admin-side Integrate feature. The user, with a
Mart model open, sees which deployment environment the model is in (derived from
the Mart folder) and can promote it forward/back per the admin definitions. This
iteration: tab visibility + current-env detection + targets UI + a Merge SEAM
(placeholder, no destructive run).

## ADIM 0 findings (confirmed)
- UI: ModelConfigForm is a floating WinForms Form with a `tabControl` (4 TabPages,
  declared in ModelConfigForm.Designer.cs). New tab = Designer shell + runtime fill
  in ConnectToModel. Theme colors/fonts in Designer InitializeComponent; CreateInfoCard
  + AddinMessageDialog for styling; pnlValidationToolbar for a horizontal row.
- Open model Mart path: ConfigContextService.Instance.MartPath (e.g.
  "Kursat/MetaRepo/Dev/SalesModel"). Current env = parent folder = second-to-last segment.
- Mart access: 100% SCAPI / in-process + Win32 WM_COMMAND. No REST. CONFIRMED.
- Merge: no existing infra; native commands dispatched via PostMessage WM_COMMAND.
  Merge cmd id unknown (later WmCommandLogger discovery). This iteration = SEAM + log only.
- Repo DB: INTEGRATE_ENABLED via ConfigContextService.GetEffectiveBool (already does the
  CONFIG_PROPERTY -> CORPORATE_PROPERTY -> false cascade, admin-identical). ENVIRONMENT /
  ENVIRONMENT_RELATION have NO EF entity (RepoDbContext is admin's, out of repo) -> read via
  raw ADO.NET dialect-aware (DatabaseService.CreateConnection/CreateCommand + SqlDialect.Param),
  mirroring LookupConfigId. No admin changes, no EF-version risk.

## User decisions (2026-06-22)
1. Tab hidden entirely when INTEGRATE_ENABLED off or model has no config (TabPage removed).
2. Not-in-managed-environment: single-line text "This model is not in a managed environment."
3. SCAPI / in-process + Win32 confirmed (no REST).

## Design (SOLID separation)
- Services/IntegrationEnvironmentService.cs: DTOs + DB reads (raw ADO, dialect-aware).
  - record IntegrationEnvironment(Id, ConfigId, Name, SortOrder, Description, ColorHex)
  - record IntegrationRelation(Id, ConfigId, FromEnvironmentId, ToEnvironmentId, RequiresApproval)
  - GetEnvironments(configId)            ORDER BY SORT_ORDER
  - GetRelationsFrom(configId, fromId)   WHERE CONFIG_ID=.. AND FROM_ENVIRONMENT_ID=..
  - No error swallowing: exceptions propagate, UI boundary shows them.
- Services/IntegrationPlanner.cs: PURE logic (DB-free, unit-tested).
  - ParseParentFolder(martPath) -> parent segment or null
  - ResolveCurrentEnvironment(martPath, environments) -> env or null (NAME match, OrdinalIgnoreCase)
  - BuildTargets(currentEnvId, relations, environments) -> ordered PromotionTarget list
    (target env + RequiresApproval), ordered by target SORT_ORDER
  - record PromotionTarget(IntegrationEnvironment Target, bool RequiresApproval)
- Services/MartMartAutomation.cs: PromoteViaMartMerge(sourceEnv, targetEnv, log) SEAM ->
  placeholder log "Merge will run here (steps pending)", no destructive action.
- ModelConfigForm.Designer.cs: tabIntegrate shell TabPage (NOT added to tabControl by
  default, so default = hidden). Inner content built at runtime.
- ModelConfigForm.cs:
  - SetIntegrateTabVisible(bool): add/remove tabIntegrate from tabControl.TabPages (append
    at end keeps order).
  - RebuildIntegrateTab(): states - not-in-env line / "No promotions from {env}" / 1 target
    static / N targets combo / approval info / Integrate button / DB-error red text.
  - combo SelectedIndexChanged -> refresh action area (button vs approval info).
  - Integrate button click -> PromoteViaMartMerge seam (placeholder).
  - Wire into ConnectToModel success path (after mismatch/config-less guards):
    enabled = IsInitialized && ActiveConfigId>0 && GetEffectiveBool("INTEGRATE_ENABLED", false)
    SetIntegrateTabVisible(enabled); if (enabled) RebuildIntegrateTab();

## Visual (single clean row)
  [Current env badge]  --->  [Target badge | v Combo]   [ Integrate ] | "Requires approval..."
  - 0 targets: "No promotions available from {CurrentEnv}." (no button)
  - 1 target: static target text
  - N targets: ComboBox of targets; action area updates on change
  - selected target RequiresApproval: info text, NO button (no run)
  - COLOR_HEX -> env badge background (optional)

## Plan (checkable)
- [ ] 1. IntegrationEnvironmentService.cs (DTOs + dialect-aware raw-ADO reads, no swallow).
- [ ] 2. IntegrationPlanner.cs (pure logic + PromotionTarget DTO).
- [ ] 3. IntegrationPlannerTests.cs (parent-folder, current-env resolve, build-targets ordering/approval).
- [ ] 4. MartMartAutomation.PromoteViaMartMerge seam (placeholder log).
- [ ] 5. Designer: tabIntegrate shell.
- [ ] 6. ModelConfigForm: SetIntegrateTabVisible + RebuildIntegrateTab + handlers + ConnectToModel wiring.
- [ ] 7. Build 0/0 + dotnet test green. Self-verify states by reasoning through each branch.

## NOT in scope (this iteration)
- No real Merge execution (seam + placeholder only; no WM_COMMAND posted).
- No writes to ENVIRONMENT / ENVIRONMENT_RELATION (read-only).
- No approval mechanism (ENVIRONMENT_RELATION_APPROVER untouched).
- No Mart catalog browsing (open model's own path is enough for current-env).
- No admin-project changes.

## Review (2026-06-22 - DONE, not yet committed)
All 7 plan items done. New: Services/IntegrationEnvironmentService.cs,
Services/IntegrationPlanner.cs, tests/ErwinAddIn.Tests/IntegrationPlannerTests.cs.
Edited: Services/MartMartAutomation.cs (PromoteViaMartMerge seam),
ModelConfigForm.Designer.cs (tabIntegrate shell), ModelConfigForm.cs (region + 2 wiring edits).

Verification:
- Build 0 errors / 0 new warnings (new files use #nullable enable; 3 pre-existing xUnit1012
  warnings are not mine).
- Tests 360/360 green; IntegrationPlannerTests 18/18.
- No em-dash in any new/edited line (ripgrep \x{2014} - only pre-existing comments match).
- Linchpin verified by hand (ModelConfigForm.cs:1548-1561): a genuine model switch clears
  _globalDataLoaded -> full path -> ConfigContext re-resolves, so RefreshIntegrateTab inside
  InitializeModelServices never reads a stale config (fast path only runs for same-model reconnect).

Adversarial review (Workflow, 11 agents, 6 dimensions): 5 raw findings, 3 confirmed.
- HIGH: Integrate gate missing IsMartModel guard -> a local .erwin (config-initialized since
  2026-06-13, MartPath = file path) could falsely resolve a "current environment" from its
  folder name. FIXED: added !ctx.IsMartModel to IsIntegrateEnabled (mirrors every other Mart
  feature). Lesson captured in lessons.md (2026-06-22).
- LOW: gate-read failure hides tab + logs (does not surface on screen). KEPT: matches the
  codebase's PropertyApplicator.IsPropertyEnabled gate-read convention (logged, not swallowed);
  the data reads the user actually looks at DO surface errors in red.
- LOW: duplicate ENVIRONMENT.NAME resolves to lowest SORT_ORDER. Added a Log so the admin data
  anomaly is not silent (planner stays pure).

Pending: real Mart Merge execution (seam placeholder only); approval mechanism; commit when asked.

## Graph redesign (2026-06-22 - DONE, not yet committed)
User asked to replace the single-row promotion UI with a graphical topology like the admin
Integrate screen, with an Integrate action ON the allowed arrows. Confirmed via sketches:
full topology (admin parity) + round play-icon button on allowed arrows.

Reuse check (Explore on erwin-admin, read-only): admin draws it in
EnvironmentRelationsSection.RelationDiagram (pure System.Drawing) but it is private/sealed and
lives in MetaAdmin.dll (the app, NOT the shared DLL the add-in references) + coupled to
ServiceLocator/AppTheme. NOT reusable. So I reimplemented the SAME visual language in the add-in
with zero new dependencies.

Changes:
- Services/IntegrationEnvironmentService.cs: GetRelationsFrom -> GetRelations(configId) (full topology).
- Forms/EnvironmentPipelineDiagram.cs (NEW): Panel that paints rounded env nodes (ColorHex border,
  current highlighted), directed Bezier arrows (forward arc up / backward down, AdjustableArrowCap,
  approval = orange + badge), and overlays a round play-button on each allowed (non-approval)
  transition out of current; tooltip per button; IntegrateRequested event -> OnIntegrateClicked seam.
- ModelConfigForm.cs RebuildIntegrateTab: builds the diagram in an AutoScroll surface + legend
  (play / approval) + hint ("No promotions..." / "All ... require approval"); removed the old
  FlowLayout row builders + 2 unused color fields + 2 unused helpers.

Verification: build 0/0, tests 360/360. Adversarial review (code-analyzer): no critical/high/medium;
one low (Button Font/Region not deterministically disposed) FIXED (shared static glyph font +
b.Region dispose in cleanup). No em-dash in new code; English strings; no swallow; no dead code.

---

# "Template" naming rule type - runtime applier (2026-06-23)

New `RULE_TYPE='Template'` (admin Rule Management). Generates a target property
value from a template (tokens `{PropertyCode}` own / `{Alias.PropertyCode}`
related via MC_OBJECT_RELATION) and writes it via SCAPI on the per-column
lifecycle hook. v1 = COLUMN.Definition (example 1). Approved decisions: per-object
lifecycle (no full walk); COLUMN first, TABLE.PrimaryKey deferred; AUTO_APPLY=false
= Yes/No confirm.

## Done
- [x] `Services/NamingTemplateEngine.cs` - pure renderer + `ShouldWrite` + `TemplateResolutionException` (no-fallback). 25 unit tests.
- [x] `Services/ObjectRelationCatalog.cs` - cached raw-ADO MC_OBJECT_RELATION loader, `ResolveAlias(fromType, alias)`.
- [x] `NamingStandardService` - `Template` enum value; `ValueTemplate`/`TemplateFillMode` fields; 3-dialect query columns; AutoApply mask includes Template; `GetTemplateRules(objectType)`.
- [x] `NamingValidationEngine` - `IsRuleApplicable` made public (reuse); `case Template: break;` no-op in `EvaluateRule`.
- [x] `ValidationCoordinatorService` - `ApplyColumnTemplateRules` + `ReadScapiProperty` + `ResolveColumnRelatedProperty`; wired into `CheckEntityForChanges` (create-commit + update, placeholder-guarded, reuse treatAsNew).
- [x] `AddInPropertyMetadataService.GetRelations(int fromObjectTypeId)` - EF impl of the new MetaShared interface member (build was broken by the admin-side contract growth; not our regression).
- [x] `ModelConfigForm` - diagnostic dump Template branch + catalog reload next to naming load (only when a Template rule exists).

## Verification
- Build 0 warning / 0 error (TreatWarningsAsErrors).
- `dotnet test` 385/385 (25 new NamingTemplateEngineTests; flaky NamingStandardEngineTests passed this run).
- NOT live-verified yet (user will test): open a Mart model whose config has the COLUMN.Definition Template rule, add a column -> Definition rendered from parent table name; AUTO_APPLY true silent / false Yes-No; OnlyIfEmpty respected; token failure -> ERROR_MESSAGE logged + NO write.
- NOT committed (waiting for explicit request).

## Deferred / next stage
- TABLE.PrimaryKey (example 2): write PK Key_Group name. Blocked on a live SCAPI
  probe to identify WHICH Key_Group of an entity is the PK (`kg.Properties("Name")`
  write is proven; PK discrimination is not). Engine + catalog are generic, so this
  is a "PK target writer" adapter + table lifecycle wiring.
- TABLE/VIEW direct-property Template rules (no example yet): same engine, more wiring.

---

# Bug: Model name rename in Model Explorer fired no naming checks (2026-06-24)

The model validator `ValidateModelOnEditorClose` (MODEL.Name regex/prefix/required +
MODEL.Definition required) was only triggered on the "Model 'X' Editor" dialog close.
Renaming the MODEL node via Model Explorer inline edit never opened that dialog -> no
check fired. Log evidence: MODEL.* rules ARE loaded (rule#1102 Regexp MODEL.Name,
#1104 Prefix, #1131 Required, #1103 Required MODEL.Definition, all apply=Both) but
`NamingValidate:` fires only for Column/Table/View, never Model. Model-level analog of
the column-add-via-Model-Explorer bug.

## Fix (Services/ValidationCoordinatorService.cs)
- [x] `_modelNameSnapshot` instance field (fresh per connect -> no cross-model staleness).
- [x] Baseline it in `StartMonitoring` from `root.Name` (before timers start).
- [x] `ScanForModelRenameEventDriven(source)` - reuses the existing `ValidateModelOnEditorClose`; re-entrancy + session guards; advances baseline before validating, refreshes after.
- [x] Wired into the inline-edit-close edge (same edge as entity/column/view renames; does NOT fire on tab switch).
- [x] Fixed a stale comment that claimed the model validator is "warn-only / no write-back" (it DOES write Required fields).

## Verification
- Build 0/0; `dotnet test` 385/385.
- Adversarial code-analyzer review: NO real bugs. NITs addressed (guard parity with sibling; stale comment). Open UX note: a name-only rename also re-checks Definition (rare popup).
- NOT live-verified (user will test): rename model in Model Explorer -> MODEL.Name regex/prefix/required fire (RequiredFieldDialog + regex re-prompt).
- NOT committed.

## Update 2026-06-24: model rename "Revert Change" did not restore the old name
- User test: rename fired the warning (fix works), but "Revert Change" kept the typed invalid name. Root cause: `ValidateModelOnEditorClose` cancel branch logged "left as-is" and broke, never reverting (it had no prior value).
- Fix: `ValidateModelOnEditorClose(nameRevertValue, nameOnly)`. Cancel on the name now writes `nameRevertValue` back in a transaction (symmetric with the forward write). `nameOnly:true` so a name-only rename does not also prompt for Definition. `ScanForModelRenameEventDriven` passes the pre-rename name + nameOnly.
- Build 0/0; tests 385/385. Adversarial review: no real bugs. Editor-dialog-close path unchanged (still "left as-is" on cancel). NOTE log file c:\work\erwin-addin-debug.log was stale (last write 2026-06-23 15:25) so this was diagnosed from code; user must redeploy + retest.
- Pre-existing NIT (out of scope): Turkish write-failure popup at ValidationCoordinatorService.cs ~3386 violates the English-strings rule.

## Update 2026-06-24 (2): "Revert Change" now also works in the Model Editor DIALOG
- User confirmed inline-rename revert works; asked for the same in the Model Editor dialog.
- Fix (WindowMonitorTimer_Tick model-editor block): capture `_modelEditorOpenName = Root.Name` on the editor OPEN transition (pre-edit name); on CLOSE call `ValidateModelOnEditorClose(nameRevertValue: _modelEditorOpenName, nameOnly: false)` so "Revert Change" restores the name; refresh `_modelNameSnapshot` after; null the captured name. nameOnly stays false (the editor can also edit Definition).
- Adversarial review: core change sound + fail-safe on COM timing. Risk "double-fire with the inline scan" is empirically unreachable (if the editor triggered the inline edge, last turn's inline revert would already have worked in the editor - it did not) AND backstopped by the same-tick snapshot refresh; documented rather than guarded with new state. Fixed stale `nameRevertValue` XML doc.
- Build 0/0; tests 385/385 (1 flaky NamingStandardService singleton race; green on re-run). NOT committed.
- LOG NOTE: the live log is %TEMP%\erwin-addin-debug.log; the c:\work\erwin-addin-debug.log copy the user shares was frozen at 2026-06-23 15:25 (latest tests not captured) - diagnosed from code. User must re-copy the fresh %TEMP% log to c:\work to share runtime evidence.

## Update 2026-06-24 (3): "Revert Change" on the first model dialog must stop the whole chain
- User: reverting the first warning (Name) still popped the next dialog (Definition). Revert should abort the entire chain.
- Root cause: in ValidateModelOnEditorClose the per-property `foreach` validates Name then Definition; the cancel/revert branch only `break`ed the inner `while`, so the foreach advanced to the next property and opened another dialog.
- Fix: cancel/revert branch now `return`s (exits the whole method) after the revert write, so no further model property is validated this round. OK/fill path still continues to the next property. Also corrected a stale inline comment.
- Build 0/0; tests 385/385. Traced all paths (editor nameOnly:false multi-prop, inline nameOnly:true single-prop, OK-fill, write-failure unaffected). NOT committed.
- LOG STILL STALE: c:\work\erwin-addin-debug.log mtime unchanged (2026-06-23 15:25). Live log is %TEMP%\erwin-addin-debug.log in the erwin process; the c:\work copy was not refreshed, so diagnosed from code + the user's exact symptom.

## Update 2026-06-24 (4): apply the 2026-05-24 "force valid Required on revert" rule to COLUMN + MODEL
- User: "this logic should be in all rules". On investigation, the cross-property chain-stop already held for TABLE/VIEW/COLUMN (only MODEL was fixed earlier). The real asymmetry was the 2026-05-24 force-fix (re-prompt if the reverted value is still invalid): TABLE/VIEW had it, COLUMN/MODEL let the user escape.
- IMPORTANT: the user's first lean (revert -> stop+leave) contradicted their own 2026-05-24 rule; surfaced it + asked; user chose KEEP-2026-05-24 + apply everywhere.
- Fix (ValidationCoordinatorService.cs): MODEL cancel branch re-validates -> re-prompt (continue) if invalid, return if valid. COLUMN: wrapped the first dialog in a `while` (SITE 1) + made the OK-path re-prompt `while` (SITE 2) re-validate-then-reprompt. Both: existing-object revert-to-invalid loops; new-object discards; valid revert dismisses + stops chain. Added try/catch (fault -> treat valid, no trap) + session dismissal parity.
- Adversarial review: 1 real bug (SITE 2 missing dismissal on revert-to-valid) FIXED; 1 NIT (exception guard) FIXED; 1 NIT (Schema_Ref non-dismissable-by-revert) = intended, consistent with TABLE.
- Build 0/0; tests 385/385. NOT committed.

## PRIMARY KEY object type runtime support (2026-06-26)
- Admin added "PRIMARY KEY" governance type = Key_Group (Key_Group_Type="PK"); admin can author naming rules (target usually the PK constraint name) incl. Template (PK_{Table.Name}).
- Done: `ApplyPrimaryKeyRules` (Template applier, parallel to ApplyColumnTemplateRules) + `ResolvePrimaryKeyRelatedProperty` ("Table" alias) + call site after CheckEntityKeyGroups + `_pkTemplateSeen`/`_pkTemplateWriteFailed` (cleared in all 3 rebaseline paths) + ReadUdpValue "primary key"=>"Key_Group".
- Adversarial review: 2 bugs FIXED - (1) PK sets not cleared on rebaseline; (2) write-failure log-spam guard. PK-detection/string-consistency/idempotency/exception-safety CONFIRMED-OK.
- Build 0/0; tests 385/385. NOT committed.
- OPEN (CHALLENGE - needs live verify): codebase evidence says a Key_Group has NO `Physical_Name` (all existing writes use `Name`; KeyGroupCandidates omits it; Views-have-no-Physical_Name precedent). Applier is generic (writes rule.PropertyCode); if the admin PK rule targets Physical_Name and it throws, the log shows `[PK-TEMPLATE-ERROR] writing 'Physical_Name' failed` and the write is suppressed. Live-verify the correct PK constraint-name property (likely `Name`).
- DEFERRED: non-template PK rules (Prefix/Suffix/Length/Regexp/Required validate+prompt) and PRIMARY KEY object-existence rule mapping (needs Type=="PK" filter).

## Update 2026-06-26: PRIMARY KEY deferred items done (non-template + filtered existence)
- (1) Non-template PK rules: `ApplyPrimaryKeyRules` now also runs a non-template pass mirroring the Index flow (CheckEntityKeyGroups): baseline-on-first-sight then auto-apply prefix/suffix + validate-warn on a value change, snapshot-gated via `_pkPropertySnapshots` (cleared in all 3 rebaseline paths), warn-only (no required-field force-fill). Generic over PropertyCode. Early-out now fires only when there are NO PK rules at all (template OR non-template).
- (2) Filtered existence: `ScapiCollectTypeForExistence` maps PRIMARY_KEY -> Key_Group; `CheckRequiredObjectTypesExist` filters members by Key_Group_Type=="PK" and caches under a distinct "Key_Group:PK" key so a PRIMARY KEY existence rule never shares INDEX's any-Key_Group result.
- Adversarial review: NO real bugs (2 cosmetic NITs left). Snapshot gating = exactly-once-per-change (no _pendingResults spam); template/non-template don't oscillate; missing Physical_Name degrades to inert; existence cacheKey isolates PK from INDEX; exception-safe. no-full-walk preserved (PK pass is scoped to the processed entity).
- Build 0/0; tests 385/385. NOT committed. Physical_Name-on-Key_Group still pending live verify (generic code handles either way).

## Update 2026-06-29: PRIMARY KEY Template LIVE-VERIFIED + Physical_Name uncertainty RESOLVED
- Live log proof: template 'PK_{Table.Physical_Name}' -> [PK-TEMPLATE-APPLY] table='TEST' Physical_Name='PK_TEST' (once, no error, no runaway). User confirmed this is the intended behavior ("PK name'i bu olmalı"), feature stays.
- RESOLVED: Key_Group.Physical_Name IS writable via SCAPI (the PK constraint name) - unlike Views which have no Physical_Name. The earlier ship-blocker uncertainty is cleared.
- Self-referential guard LIVE-VERIFIED: with the old 'PK_{Physical_Name}' it logged [PK-TEMPLATE-SKIP] once, 0 APPLY, flicker gone.
- Status: all PK work + self-ref guard done. Build 0/0, tests 395/395. Generic over PropertyCode (admin can target Name instead of Physical_Name if a visible-name rename is ever wanted - no code change). NOT committed.

# WP#280 review (2026-07-18) - Predefined columns: single "When UDP" -> ordered AND/OR list

Done (add-in side; admin backend/web/migration already shipped):
- Shared evaluator: extracted `NamingValidationEngine.AreConditionsSatisfied(list, objectType, obj, pk?)` from `IsRuleApplicable` (which now delegates). Both naming rules and predefined columns fold through it - one engine, no duplicated logic.
- `PredefinedColumn`: dropped `DependsOnUdp*`; added `List<NamingRuleCondition> Conditions` (reuses the naming row type); `IsUnconditional => Conditions.Count == 0`.
- Loader: main `PREDEFINED_COLUMN` query no longer selects the dropped columns/UDP join; new `LoadColumnConditions` reads `MC_PREDEFINED_COLUMN_CONDITION` - a faithful clone of `NamingStandardService.LoadRuleConditions` (XOR-skip, ORDER_INDEX sort, fails the load on error).
- Applicability: `GetApplicableNames` + `FindApplicableLockedRule` go through the shared fold; removed `GetByUdpCondition`/`GetByUdpName`/`AddPredefinedColumnsForUdp`.
- Reactive: `ReevaluateConditionalPredefinedColumns` re-evaluates every conditional column's full list; the two per-UDP call sites (required-UDP prompt after WriteUdpValues, and the UDP-change `anyChanged` block) now call it once each.
- Hardening (from adversarial review): a term whose source FK is set but resolved name is empty (dangling UDP - gating UDP deleted) previously hit the evaluator's vacuous-true fallback -> single-term column applied to EVERY table. Now returns false (gate cannot hold). Restores old predefined fail-safe AND hardens naming (shared path).

Verified:
- Build 0/0. Tests 687/687 (rewrote `PredefinedColumnApplicabilityTests` for unconditional / single-migrated / AND / OR / left-to-right-fold / dangling-FK; fakes MUST be `public` for cross-assembly dynamic dispatch).
- Live schema (MetaRepoZeynep): child-table columns match the loader exactly; flat `DEPENDS_ON_UDP_*` gone from PREDEFINED_COLUMN; the exact loader JOIN query runs and resolves UDP_NAME.
- Live data across all 9 MetaRepo* DBs: 146 condition terms, ALL at ORDER_INDEX=0 (regression guarantee: migrated single conditions fold identically), 0 comma-bearing values (CSV-split concern inert), 0 dangling UDP FKs.
- REMAINING: in-erwin UI runtime test (create Log/Parametre tables, watch the columns land) - not driven here to avoid disrupting the live erwin session. NOT committed.
