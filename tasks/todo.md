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
