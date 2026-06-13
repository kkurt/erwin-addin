# Admin UDP definition deletion protection (2026-06-12, "Silme engellenemez mi?")

## Final decision (user, 2026-06-13): Part A ONLY - "A yeterli", Part B CANCELLED
Part B (UI Delete-blocking spike) dropped: two captures showed the trash-button
WM_COMMAND never reaches the top-level dialog (goes to a nested #32770), recon
would need a toolbar-dump diagnostic + fragile subclassing. Part A's instant-undo
was made loss-proof instead and live-verified 2026-06-13 00:19-00:21 (3 runs,
"values restored on 2 entities" each time, user-confirmed "OK deyince geri geldi").

## Part A final shape (live-verified)
1. UDP editor OPEN edge (closed->open): capture admin Table/View UDP values into a
   recovery buffer (CaptureUdpRecoverySnapshot; objectId->values, admin names only)
   + coordinator copies model values (_udpRecoveryModel). Captured BEFORE any
   definition delete can wipe them - independent of the lazy backfill (the cause
   of the first test's "restored on 0 entities" loss).
2. UDP editor CLOSE edge: RunUdpSyncIfNeeded recreates deleted defs (policy dialog)
   -> afterApply restores values FROM THE RECOVERY BUFFER.
3. Context-specific dialog wording (user request): recovery path shows
   "Admin UDP tanimi korunuyor" + undo subtitle instead of the generic
   "Sync UDP definitions from config?" (UdpSyncDialog title/subtitle overrides).

## Part 1 design (reuses existing pieces)
- Detector EXISTS: VCS.IsUdpEditorOpen (title "User Defined Properties*", verified
  2026-05-22 r10.10); its close-edge consumer was removed 2026-05-22 - re-add.
- VCS: _udpEditorWasOpen + OnUdpEditorClosed event (WindowMonitorTimer edge) +
  RestoreModelUdpValues(names) from _lastModelUdpValues.
- MCF: RunUdpSyncIfNeeded(afterApply) callback invoked after Apply in BOTH branches
  (silent + WARN dialog closure); HandleUdpEditorClosed -> RunUdpSyncIfNeeded(restore
  continuation); RestoreValuesForCreatedDefs(diff) groups Creates by ObjectType.
- TTMS: RestoreTrackedUdpValues(tableNames, viewNames) - one-off Entity/View walks,
  WriteUdpValues from _entitySnapshots/_viewUdpSnapshots (recovery context, full walk OK).
- Values restored == snapshot values -> observers see no diff, no popup storm.

## Also this turn (done earlier)
- Required-UDP VALUE-clear protection in all 3 observers (table/view/model) - warn+restore.
- Diagnosis: "Column Order Locked" popup = legacy VpTTT_LOG _DATE columns (pre-exemption
  era), surfaced by first editor-close re-eval; unrelated to UDP deletion. Cleanup advised.

# Extend table checks to Views

## Recon result (3-explorer inventory, 2026-06-12)

### What fires for Views TODAY
- Naming validation on RENAME + watched-property drift (TableTypeMonitorService.CheckKeyGroupAndViewNaming ~1050-1128; "V_" keys in _keyGroupSnapshots)
- Naming validation on model-editor close (ModelConfigForm.ValidateModelOnEditorClose ~3113)
- NOTHING else. New view = snapshot-only (no pipeline).

### What fires for Tables but NOT Views (the gap)
1. NEW-object pipeline (OnNewEntityDetected): naming apply (Create), UDP ApplyDefaults,
   PromptForMissingRequiredUdps, question wizard, predefined columns, PK-index delete.
2. Locked UDP enforcement (revert on drift).
3. Column-level checks (naming/glossary/locked/required) - views have NO column handling at all.

### Infra readiness (all verified by recon)
- UdpRuntimeService: Read/Write/ApplyDefaults objectType "View" -> "View.Physical.*" READY
- UdpDefinitionService.GetByObjectType("View") READY; admin MC_OBJECT_TYPE 'VIEW' seeded
- NamingStandardService/Engine: object-type generic, "View" rules already consumable
- RequiredUdpForm: objectKind parameterized (mode Create/Update) READY
- View detection loop EXISTS (isNew currently snapshot-only -> natural hook for FireNewViewPipeline)

### Open feasibility unknown (Faz 2 blocker)
- Do r10 SCAPI views expose columns as child objects (Collect(view,"Attribute") or "View_Column")?
  NO code evidence either way; API txt doc has no class catalog. Needs a LIVE probe before
  any view-column check is promised.

## Plan

### Faz 1 - View object-level checks (no unknown, mirrors table pipeline minus columns)
- [ ] FireNewViewPipeline / OnNewViewDetected: on isNew with real (non-%) name:
      naming standard apply (Create context) + UDP ApplyDefaults("View") +
      PromptForMissingRequiredUdps generalized to objectType "View" (Cancel = leave empty?
      a view CAN be deleted like a new table - decide in implementation, default: same as
      table = Discard New View deletes it)
- [ ] Locked View UDP enforcement (mirror HandleLockedUdpDrift for "View")
- [ ] Tests for the pure decision helpers; build 0/0
- OUT OF SCOPE (proposed): predefined columns (views derive columns from SELECT),
  question wizard (admin config TABLE-scoped), PK-index delete (n/a)

### Faz 2 - CANCELLED (user 2026-06-12: view column checks not needed)
Scope decision: only view OBJECT-level checks (Faz 1). No view-column probe, no column
naming/glossary for views.

## Review (DONE 2026-06-12)
- Implemented Faz 1 + adversarial review (2 reviewers + per-finding verification): 7 confirmed
  findings, ALL fixed:
  1+6. Required-Cancel during naming validation now routes views through TryDeleteNewView
       (DiscardNewObjectForRequiredCancel) and OnNewViewDetected bails when the view was
       discarded (no more dead-COM-object pipeline).
  2.   Pipeline order = defaults -> naming (table parity; UDP-conditional naming rules see
       seeds); CheckViewUdpChanges now runs HandleUdpValueChange cascade + naming
       re-validation on view UDP edits (was locked-revert only).
  3.   Watched-property baseline refreshed at pipeline end (no phantom drift).
  4.   CRITICAL: the whole view/SA scan was DEAD CODE (driver CheckForTableTypeChanges lost
       its caller in Phase-2D - the actual root cause of "view'lar icin calismiyorlar").
       New public RunViewAndSubjectAreaScan() wired into MonitorTimer_Tick's periodic block.
  5.   PromptForMissingRequiredViewUdps return captured; snapshot not resurrected post-delete.
  7.   Scan wrapped in _scopedCheckInProgress acquire/release (modal-pump reentrancy gate).
- Build 0/0, tests 248/248. NOT yet live-verified (needs deploy + new view in erwin).
- NOTE: Subject Area rename/drift checks were also dead and are now LIVE again.
