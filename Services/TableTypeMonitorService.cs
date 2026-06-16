using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service to monitor entity changes, enforce naming standards,
    /// and add UDP-based predefined columns automatically.
    /// </summary>
    public class TableTypeMonitorService : IDisposable
    {
        private readonly dynamic _session;
        private bool _isMonitoring;
        private bool _disposed;

        /// <summary>
        /// Optional probe injected by ValidationCoordinatorService.SetTableTypeMonitor
        /// that returns true when the named entity is currently inside a
        /// placeholder-commit gesture. Used by ValidateNamingStandard
        /// (and any direct caller path that bypasses
        /// RunScopedTableNamingCheck) to override isNew=false to true so
        /// Update-only rules stay filtered during the creation flow.
        /// Verified necessary 2026-06-01: the Required-input re-run loop
        /// inside ValidateNamingStandard fires after the Required field
        /// dialog closes, with isNew=false, which made rule#22 _PRM
        /// (Update + Parametre) apply on a brand-new entity contrary to
        /// user rule. Null-safe: if the coordinator never injected the
        /// probe (test code, early bootstrap) ValidateNamingStandard
        /// keeps the caller's isNew unchanged.
        /// </summary>
        internal Func<string, bool> CreationGestureProbe { get; set; }

        // Snapshot of entity state: ObjectId -> EntitySnapshot
        private Dictionary<string, EntitySnapshot> _entitySnapshots;
        /// <summary>
        /// Per-View / per-Subject_Area watched-property bag. Keyed on the
        /// SCAPI object's ObjectId (no V_/SA_ prefix - the two object
        /// classes never collide because erwin guarantees globally unique
        /// IDs across classes). Mirrors EntitySnapshot.WatchedProperties
        /// for objects that don't get a full snapshot struct.
        /// </summary>
        private Dictionary<string, Dictionary<string, string>> _viewWatchedProperties;

        // View-object checks (2026-06-12, "table checks for views" Faz 1):
        // first completed view walk is a silent baseline (pre-existing views
        // must NOT fire the new-view pipeline on model open); per-view UDP
        // value snapshots back the locked-View-UDP enforcement with the same
        // "first non-empty set is allowed, later edits revert" semantic as
        // the table path.
        private bool _viewBaselineDone;
        private readonly Dictionary<string, Dictionary<string, string>> _viewUdpSnapshots =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // View name-commit deferral (2026-06-14): mirror the entity
        // _pendingNamedEntities machine so a freshly dropped view does NOT run
        // its pipeline (UDP defaults + naming auto-apply + required UDP) on the
        // placeholder name "V/<n>" before the user has typed a real name. A
        // placeholder-named new view is HELD here until the inline-edit close
        // edge (CommitPendingViews, fired by the coordinator) or a rename to a
        // real name commits it. Keyed by RAW viewId (not the "V_"+id snapshot
        // key) so the live name is re-resolved by ObjectId at commit time. The
        // drag-create stale-pending guard reuses the entity StalePendingEntityMs
        // window (single-sourced - no duplicate constant).
        private readonly HashSet<string> _pendingViews = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _pendingViewAddedAt =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private Dictionary<string, Dictionary<string, string>> _subjectAreaWatchedProperties;

        // UDP-deletion recovery buffer (2026-06-12, "Part A"): admin UDP values
        // snapshotted when the UDP editor OPENS - i.e. BEFORE a definition delete
        // can wipe them. Keyed by object id. Independent of the lazy background
        // UDP backfill (which may not have filled _entitySnapshots yet at editor
        // close), so the close-edge recovery never loses a value. Rebuilt on each
        // editor open.
        private readonly Dictionary<string, Dictionary<string, string>> _udpRecoveryEntity =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<string, string>> _udpRecoveryView =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // Throttle counter for expensive operations (Key_Group/View/SA scans)
        private int _cycleCount;
        private const int HeavyScanInterval = 4; // Every 4 cycles (2 seconds at 500ms tick)

        // Phase-1A startup optimization (2026-05-05):
        // Sync TakeSnapshot at startup (called from ModelConfigForm) used to walk all entities
        // + ReadUdpValues each. On large models this added several seconds to a 27s freeze.
        // We now skip the upfront TakeSnapshot; the first CheckForTableTypeChanges call
        // (invoked at end of ValidationCoordinator's first full cycle) silently baselines
        // _entitySnapshots without firing the heavy isNew branch (AddEntityToDiagram /
        // ApplyStandardsToEntity / ApplyDefaults / ValidateNamingStandard). After the first
        // call this flag flips and existing entities are treated as known.
        private bool _initialScanCycle = true;

        // Phase-1A UDP backfill: silent populate skips ReadUdpValues for speed (UI burst control).
        // Once the initial scan completes, subsequent CheckForTableTypeChanges calls drain entities
        // whose UdpValues snapshot is still empty, in chunks of UdpBackfillBatchSize per call,
        // so UDP value-change detection re-activates for pre-existing entities without freezing
        // the UI. Bounded work per tick keeps the cycle latency stable.
        private bool _udpBackfillPending;
        private const int UdpBackfillBatchSize = 30;

        // Snapshot of Key_Group (Index) names: ObjectId -> PhysicalName
        private Dictionary<string, string> _keyGroupSnapshots;

        // Diagnostic: log "skipped UDP check" reason at most once per entity per session
        // (so the user can see WHY nothing fires when they change a UDP value)
        private readonly HashSet<string> _diagLoggedEmptyUdp = new HashSet<string>();

        // Session-level Required-popup dismissal (added 2026-05-21,
        // extended 2026-05-24). Key is "{objectId}|{propertyCode}", value
        // is the property value at dismiss-time. When the user clicks
        // Cancel on a Required popup (Update mode) for a property whose
        // baseline is also invalid, the revert is a no-op and the next
        // tick's validation would re-fire the same popup. Storing the
        // dismissed-value lets us short-circuit only while the value is
        // unchanged; once the user types a different value (valid OR
        // invalid) the dismiss naturally expires and the popup can re-
        // surface. Without the value check the flag stuck forever: type a
        // long valid Description, then later truncate to 'kkk' and no
        // warning fired (bug 2026-05-24). Intentionally NOT persisted -
        // admin intent is that Required rules nag indefinitely, this is
        // a per-session "I will deal with it later" courtesy that resets
        // on next model open.
        private readonly Dictionary<string, string> _dismissedRequiredKeys = new Dictionary<string, string>(StringComparer.Ordinal);

        // Property applicator for applying project standards to new tables
        private PropertyApplicatorService _propertyApplicator;

        // UDP runtime service for applying UDP defaults and dependency evaluation
        private UdpRuntimeService _udpRuntimeService;

        // Event for logging
        public event Action<string> OnLog;

        public TableTypeMonitorService(dynamic session)
        {
            _session = session;
            _entitySnapshots = new Dictionary<string, EntitySnapshot>();
            _keyGroupSnapshots = new Dictionary<string, string>();
            _viewWatchedProperties = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _subjectAreaWatchedProperties = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Set the property applicator service for applying project standards to new tables.
        /// </summary>
        public void SetPropertyApplicator(PropertyApplicatorService applicator)
        {
            _propertyApplicator = applicator;
        }

        /// <summary>
        /// Set the UDP runtime service for applying UDP defaults and evaluating dependencies.
        /// </summary>
        public void SetUdpRuntimeService(UdpRuntimeService service)
        {
            _udpRuntimeService = service;
        }

        /// <summary>
        /// Apply new-entity side effects for an entity that the diagram heartbeat
        /// has just detected as newly seen with a real (non-placeholder) name:
        /// diagram add, model standard properties + question wizard, optional
        /// PK-index cleanup, and UDP defaults.
        ///
        /// Why this lives here and not in ValidationCoordinator:
        ///   The legacy isNew branch in <see cref="CheckForTableTypeChanges()"/>
        ///   owned this whole pipeline. Phase-2D (2026-05-06) removed the
        ///   periodic full-model scan that used to drive that call, but the
        ///   diagram-side discovery in ValidationCoordinator only kept the
        ///   naming-check delegation - the wizard / standards / UDP path was
        ///   silently dropped. Keeping the pipeline in this service preserves
        ///   single-responsibility (Coordinator decides "when", Monitor knows
        ///   "what to do") and matches the existing
        ///   <see cref="ValidateNamingStandard"/> delegation pattern.
        ///
        /// Snapshot/baseline bookkeeping is intentionally NOT done here -
        /// ValidationCoordinator's <c>_entityIdSnapshot</c> already drives the
        /// "newly seen" decision and updates per tick. Re-baselining inside
        /// this method would duplicate that state.
        /// </summary>
        public void OnNewEntityDetected(dynamic entity, string physicalName)
        {
            if (entity == null || string.IsNullOrEmpty(physicalName)) return;

            try
            {
                AddEntityToDiagram(entity, physicalName);
            }
            catch (Exception ex)
            {
                Log($"OnNewEntityDetected: AddEntityToDiagram error for '{physicalName}': {ex.Message}");
            }

            // Standards + question wizard. ApplyStandardsToEntity opens the
            // wizard modally when MC_QUESTION_DEF rows exist for the active
            // config + DBMS version; user answers map to property values.
            if (_propertyApplicator != null && _propertyApplicator.IsInitialized)
            {
                try
                {
                    _propertyApplicator.ApplyStandardsToEntity(entity, physicalName);

                    if (_propertyApplicator.IsPropertyEnabled("DELETE_AUTO_CREATED_INDEX_PK"))
                    {
                        DeleteAutoCreatedPKIndex(entity, physicalName);
                    }
                }
                catch (Exception ex)
                {
                    Log($"OnNewEntityDetected: ApplyStandardsToEntity error for '{physicalName}': {ex.Message}");
                }
            }

            if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
            {
                try
                {
                    _udpRuntimeService.ApplyDefaults(entity);
                    Log($"UDP defaults applied for new entity '{physicalName}'");
                }
                catch (Exception ex)
                {
                    Log($"OnNewEntityDetected: UDP defaults error for '{physicalName}': {ex.Message}");
                }

                // Required-UDP enforcement (admin's IS_REQUIRED=true flag,
                // 2026-05-15). ApplyDefaults above only seeds UDPs that have
                // a DEFAULT_VALUE in MC_UDP_DEFINITION; required UDPs whose
                // admin entry has no default (e.g. TABLE_TYPE marked as
                // required with no fixed default - user must explicitly pick
                // FACT / DIMENSION / etc) stay empty and silently violate
                // the contract.
                //
                // Per 2026-05-20 contract: on Cancel / [X], the new entity is
                // deleted in the same transaction (so the user explicitly
                // backed out of creation). We bail out of the rest of the
                // pipeline (predefined columns etc.) because the target
                // entity no longer exists.
                bool cancelledAndDeleted = false;
                try
                {
                    cancelledAndDeleted = PromptForMissingRequiredUdps(entity, physicalName);
                }
                catch (Exception ex)
                {
                    Log($"OnNewEntityDetected: required-UDP prompt error for '{physicalName}': {ex.Message}");
                }

                if (cancelledAndDeleted)
                {
                    Log($"OnNewEntityDetected: '{physicalName}' discarded by user via required-UDP Cancel - skipping remaining new-entity steps");
                    return;
                }
            }

            // Unconditional predefined columns (admin's 2026-05-14 extension).
            // Conditional columns are still added later when CheckForUdpValueChanges
            // fires for the UDP cascades that ApplyDefaults seeded - separate path,
            // separate gate. Unconditional rows have no UDP gate, so the timer
            // path will never pick them up; they must be applied here, on the
            // single "new entity" event, or they'll never land on the table.
            // Idempotent: ApplyPredefinedColumnsToEntity skips columns already
            // present on the entity, so a re-fire from a rebaseline cycle is
            // a no-op.
            try
            {
                AddUnconditionalPredefinedColumns(entity, physicalName);
            }
            catch (Exception ex)
            {
                Log($"OnNewEntityDetected: AddUnconditionalPredefinedColumns error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Discover any Table-scope UDP definitions marked IS_REQUIRED whose
        /// current value on <paramref name="entity"/> is still empty, then
        /// open <see cref="Forms.RequiredUdpForm"/> so the user picks values.
        /// On DialogResult.OK the picked values are written back through the
        /// UDP runtime (same path ApplyDefaults uses), which fires the usual
        /// dependency cascades + locked-UDP/term-type pipeline through the
        /// next timer tick.
        /// <para>
        /// Per the 2026-05-20 contract Cancel / [X] deletes the new entity
        /// (Create-mode dialog). The transaction also clears our snapshot
        /// bookkeeping so the next monitor tick treats the entity as gone
        /// rather than firing a "disappeared" hayalet event. Returns true
        /// when the entity was deleted so the caller can short-circuit the
        /// remaining new-entity pipeline steps.
        /// </para>
        /// </summary>
        /// <returns>True when the user cancelled and the entity was
        /// deleted; false when no required UDPs were missing, when the
        /// user supplied values via OK, or when the delete itself failed
        /// (failure is logged and the entity is left as-is).</returns>
        private bool PromptForMissingRequiredUdps(dynamic entity, string physicalName)
        {
            // Read current Table UDP values once. ReadUdpValues swallows
            // per-property COM errors so a misconfigured UDP (e.g. metamodel
            // missing the Property_Type) does not block the prompt for
            // unrelated required UDPs.
            Dictionary<string, string> currentValues;
            try { currentValues = _udpRuntimeService.ReadUdpValues((object)entity, "Table"); }
            catch (Exception ex)
            {
                Log($"PromptForMissingRequiredUdps: ReadUdpValues failed on '{physicalName}': {ex.Message}");
                currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var missing = UdpDefinitionService.Instance
                .GetByObjectType("Table")
                .Where(d => d != null && d.IsRequired)
                .Where(d =>
                {
                    if (!currentValues.TryGetValue(d.Name, out var v)) return true;
                    return string.IsNullOrEmpty(v);
                })
                .ToList();

            if (missing.Count == 0) return false;

            Log($"PromptForMissingRequiredUdps: '{physicalName}' missing {missing.Count} required UDP(s): {string.Join(", ", missing.Select(m => m.Name))}");

            using (var form = new Forms.RequiredUdpForm(physicalName, missing, Forms.RequiredOperationMode.Create, "Table"))
            {
                var result = form.ShowDialog();
                if (result != DialogResult.OK || form.SelectedValues.Count == 0)
                {
                    Log($"PromptForMissingRequiredUdps: user cancelled for '{physicalName}' - deleting new entity");
                    return TryDeleteNewEntity(entity, physicalName);
                }

                try
                {
                    _udpRuntimeService.WriteUdpValues((object)entity, form.SelectedValues, "Table");
                    Log($"PromptForMissingRequiredUdps: wrote {form.SelectedValues.Count} required UDP value(s) on '{physicalName}'");
                }
                catch (Exception ex)
                {
                    Log($"PromptForMissingRequiredUdps: WriteUdpValues failed on '{physicalName}': {ex.Message}");
                    return false;
                }

                // Run dependency cascades for each picked value so any
                // dependent UDP (e.g. TABLE_TYPE -> derived flag) is
                // refreshed immediately instead of waiting for the next
                // tick.
                foreach (var kvp in form.SelectedValues)
                {
                    try { _udpRuntimeService.HandleUdpValueChange(entity, kvp.Key, kvp.Value); }
                    catch (Exception ex) { Log($"PromptForMissingRequiredUdps: dependency cascade failed for '{kvp.Key}': {ex.Message}"); }
                }

                // Conditional predefined columns (2026-05-24 fix): admin can
                // attach predefined columns to a UDP value (e.g. "every Log
                // table gets a LogTime column"). The legacy cascade lived
                // inside the dead CheckForUdpValueChanges path; with the
                // Required-UDP popup now being the canonical "user picked
                // the conditional UDP" event we trigger AddPredefinedColumnsForUdp
                // directly here so the column lands on the same gesture
                // that filled the UDP. Per-value try/catch keeps a broken
                // predefined-column rule from poisoning the cascade for
                // other UDPs picked in the same form.
                foreach (var kvp in form.SelectedValues)
                {
                    try
                    {
                        var conditional = PredefinedColumnService.Instance.GetByUdpCondition(kvp.Key, kvp.Value);
                        if (conditional != null && conditional.Any())
                        {
                            AddPredefinedColumnsForUdp(entity, kvp.Key, kvp.Value, physicalName);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"PromptForMissingRequiredUdps: predefined-column cascade failed for '{kvp.Key}'='{kvp.Value}' on '{physicalName}': {ex.Message}");
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Enforce MODEL-level required UDPs (admin Object Type = MODEL,
        /// IS_REQUIRED=true). Triggered once when the model is opened/connected,
        /// which we treat as the "Update" context, so only UDPs whose Apply On is
        /// Update or Both (default/empty == Both) are enforced; Create-only UDPs
        /// (new-model gesture) are skipped. Unlike the new-table path, a MODEL
        /// cannot be deleted on Cancel - missing values are simply left empty and
        /// re-prompted on the next open. Best-effort: never throws into the
        /// caller. Returns true when a prompt was shown.
        /// </summary>
        /// <param name="modelRoot">The SCAPI model Root object (Model owner).</param>
        /// <param name="modelName">Display name for the dialog header.</param>
        public bool PromptForMissingRequiredModelUdps(dynamic modelRoot, string modelName)
        {
            if (modelRoot == null) return false;
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return false;

            Dictionary<string, string> currentValues;
            try { currentValues = _udpRuntimeService.ReadUdpValues((object)modelRoot, "Model"); }
            catch (Exception ex)
            {
                Log($"PromptForMissingRequiredModelUdps: ReadUdpValues failed on model '{modelName}': {ex.Message}");
                return false;
            }

            var missing = UdpDefinitionService.Instance
                .GetByObjectType("Model")
                .Where(d => d != null && d.IsRequired)
                .Where(d => AppliesOnUpdate(d.ApplyOn))   // model-open == Update context
                .Where(d =>
                {
                    if (!currentValues.TryGetValue(d.Name, out var v)) return true;
                    return string.IsNullOrEmpty(v);
                })
                .ToList();

            if (missing.Count == 0) return false;

            Log($"PromptForMissingRequiredModelUdps: model '{modelName}' missing {missing.Count} required UDP(s): {string.Join(", ", missing.Select(m => m.Name))}");

            using (var form = new Forms.RequiredUdpForm(modelName, missing, Forms.RequiredOperationMode.Update, "Model"))
            {
                var result = form.ShowDialog();
                if (result != DialogResult.OK || form.SelectedValues.Count == 0)
                {
                    // A MODEL has nothing to delete/revert: leave the UDPs empty;
                    // they are re-prompted the next time the model is opened.
                    Log($"PromptForMissingRequiredModelUdps: user cancelled for model '{modelName}' - left empty (re-prompts on next open).");
                    return true;
                }

                try
                {
                    _udpRuntimeService.WriteUdpValues((object)modelRoot, form.SelectedValues, "Model");
                    Log($"PromptForMissingRequiredModelUdps: wrote {form.SelectedValues.Count} required UDP value(s) on model '{modelName}'.");
                }
                catch (Exception ex)
                {
                    Log($"PromptForMissingRequiredModelUdps: WriteUdpValues failed on model '{modelName}': {ex.Message}");
                }
                return true;
            }
        }

        /// <summary>
        /// Map an admin OBJECT_TYPE name to the SCAPI Collect class for the
        /// object-existence check. Returns null for types that have no
        /// meaningful "at least one exists" semantic so the caller skips them:
        /// MODEL is the root itself (always present) and any unmapped/unknown
        /// type is logged-and-skipped rather than warned. Mirrors the owner-class
        /// switch in <see cref="NamingValidationEngine"/> (table->Entity etc.).
        /// </summary>
        private static string ScapiCollectTypeForExistence(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return null;
            // Normalise admin's "SUBJECT AREA" / "SUBJECT_AREA" and casing.
            switch (objectType.Trim().ToUpperInvariant().Replace(' ', '_'))
            {
                case "TABLE":        return "Entity";
                case "VIEW":         return "View";
                case "COLUMN":       return "Attribute";
                case "INDEX":        return "Key_Group";
                case "SUBJECT_AREA": return "Subject_Area";
                default:             return null;   // MODEL (always exists) / unknown
            }
        }

        /// <summary>
        /// Model-open existence check (2026-06-15): an object-type-only Required
        /// naming rule (Property "(none)") asserts "the model must contain at
        /// least one object of this type". Loaded via
        /// <see cref="NamingStandardService.GetObjectExistenceRules"/>. WARN-ONLY
        /// (a missing object type cannot be auto-created); all violations are
        /// consolidated into a single popup. ApplyOn and DEPENDS_ON conditions
        /// are intentionally ignored - a model-level existence assertion has no
        /// per-object to scope or condition against (admin disables the ApplyOn
        /// combo for this rule form). Runs once per model open from
        /// <c>ValidationCoordinatorService.CheckModelRequiredUdpsOnce</c>.
        /// Best-effort: never throws into the caller.
        /// </summary>
        /// <param name="modelRoot">The SCAPI model Root object.</param>
        public void CheckRequiredObjectTypesExist(dynamic modelRoot)
        {
            if (!NamingStandardService.Instance.IsLoaded) return;
            var rules = NamingStandardService.Instance.GetObjectExistenceRules();
            if (rules == null || rules.Count == 0) return;

            dynamic modelObjects;
            dynamic root;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelRoot ?? modelObjects.Root;
            }
            catch (Exception ex)
            {
                Log($"CheckRequiredObjectTypesExist: model access failed: {ex.Message}");
                return;
            }

            // Cache the has-any result per SCAPI type so two rules on the same
            // type only Collect once (rare, but cheap to guard).
            var hasAnyByType = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var violations = new List<string>();

            foreach (var rule in rules)
            {
                string scapiType = ScapiCollectTypeForExistence(rule.ObjectType);
                if (scapiType == null)
                {
                    Log($"CheckRequiredObjectTypesExist: rule#{rule.Id} object type '{rule.ObjectType}' has no existence semantic - skipped");
                    continue;
                }

                if (!hasAnyByType.TryGetValue(scapiType, out bool hasAny))
                {
                    hasAny = false;
                    try
                    {
                        dynamic coll = modelObjects.Collect(root, scapiType);
                        if (coll != null)
                        {
                            // Existence only - stop at the first object.
                            foreach (dynamic _ in coll) { hasAny = true; break; }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"CheckRequiredObjectTypesExist: Collect('{scapiType}') failed for rule#{rule.Id}: {ex.Message}");
                        continue;
                    }
                    hasAnyByType[scapiType] = hasAny;
                }

                if (!hasAny)
                {
                    string msg = !string.IsNullOrEmpty(rule.ErrorMessage)
                        ? rule.ErrorMessage
                        : $"At least one {rule.ObjectType} must exist in the model.";
                    Log($"CheckRequiredObjectTypesExist: rule#{rule.Id} VIOLATED - no '{scapiType}' object ({rule.ObjectType})");
                    if (!violations.Contains(msg)) violations.Add(msg);
                }
                else
                {
                    Log($"CheckRequiredObjectTypesExist: rule#{rule.Id} OK - '{scapiType}' object present ({rule.ObjectType})");
                }
            }

            if (violations.Count == 0) return;

            string body = violations.Count == 1
                ? violations[0]
                : "The model is missing required object types:\n\n - " + string.Join("\n - ", violations);
            AddinMessageDialog.Show(body, "Required object types", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>
        /// True when a UDP whose admin "Apply On" is <paramref name="applyOn"/>
        /// should fire in the UPDATE context (model open == update). Mirrors
        /// <see cref="UdpValidationEngine"/>'s gate: Both always applies, else the
        /// operation must match. Null/blank defaults to Both. Create-only -> false.
        /// </summary>
        public static bool AppliesOnUpdate(string applyOn)
        {
            if (string.IsNullOrWhiteSpace(applyOn)) return true; // default Both
            string a = applyOn.Trim();
            return a.Equals("Both", StringComparison.OrdinalIgnoreCase)
                || a.Equals("Update", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Remove a newly-created entity in response to the user discarding
        /// its Required popup. Wraps the SCAPI <c>modelObjects.Remove</c>
        /// call (same primitive proven by <see cref="ColumnValidationService"/>'s
        /// PLEASE_CHANGE_IT cleanup) in a named transaction, then clears
        /// our snapshot bookkeeping so the next monitor tick does not see
        /// a phantom "entity disappeared" event for an object we
        /// intentionally removed.
        /// </summary>
        /// <summary>
        /// Resolve the closed list of valid values for a Required-field
        /// popup, when the property is one of erwin's reference-typed
        /// columns (currently Owner / Name_Qualifier / Schema_Ref - all
        /// of which are SCVT_OBJID columns that erwin refuses to accept
        /// unless the value matches an existing Schema object's name).
        /// Returns a sorted, distinct list of Schema names from the live
        /// model. Returns <c>null</c> for properties that do not have a
        /// fixed value set; the dialog then renders its default free-text
        /// TextBox. 2026-05-25 user request.
        /// </summary>
        private System.Collections.Generic.IReadOnlyList<string> ResolveRequiredFieldChoices(string propertyCode)
        {
            if (string.IsNullOrEmpty(propertyCode)) return null;
            // Owner-family aliases all resolve to the same Schema list.
            bool isOwnerLike =
                string.Equals(propertyCode, "Owner", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyCode, "Name_Qualifier", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyCode, "Schema_Ref", StringComparison.OrdinalIgnoreCase)
                || string.Equals(propertyCode, "Schema", StringComparison.OrdinalIgnoreCase);
            if (!isOwnerLike) return null;

            var names = new System.Collections.Generic.SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            dynamic modelObjects = null;
            dynamic root = null;
            dynamic schemas = null;
            try
            {
                modelObjects = _session?.ModelObjects;
                root = modelObjects?.Root;
                if (root == null) return null;
                schemas = modelObjects.Collect(root, "Schema");
                if (schemas == null) return null;
                foreach (dynamic sch in schemas)
                {
                    if (sch == null) continue;
                    string schName;
                    try { schName = sch.Name?.ToString() ?? ""; }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(schName)) names.Add(schName);
                }
            }
            catch (Exception ex)
            {
                Log($"ResolveRequiredFieldChoices err for '{propertyCode}': {ex.Message}");
            }
            // Returning an empty list would render an empty combo and trap
            // the user. Fall back to null (free text) so they at least have
            // a chance to type a name; the SCAPI write will still surface
            // the existing "must be an existing Schema" error if they
            // invent one.
            return names.Count > 0 ? new System.Collections.Generic.List<string>(names) : null;
        }

        /// <returns>True when the entity was successfully removed; false on
        /// any failure (caller treats false as "leave the entity alone" so
        /// the user can manually clean up).</returns>
        /// <summary>
        /// Snapshots current Table + View admin-UDP values into the recovery
        /// buffer (<see cref="_udpRecoveryEntity"/> / <see cref="_udpRecoveryView"/>).
        /// Called on UDP-editor OPEN so the values are captured BEFORE a
        /// definition delete can wipe them. Bounded: only admin-defined UDP
        /// names are read (a handful) per object; exits immediately when no
        /// Table/View UDP definitions exist. Rare user-initiated gesture, so the
        /// one-off walk is acceptable (same cost class as the restore walk).
        /// </summary>
        public void CaptureUdpRecoverySnapshot()
        {
            _udpRecoveryEntity.Clear();
            _udpRecoveryView.Clear();
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (!UdpDefinitionService.Instance.IsLoaded) return;

            bool hasTableDefs = UdpDefinitionService.Instance.GetByObjectType("Table").Any();
            bool hasViewDefs = UdpDefinitionService.Instance.GetByObjectType("View").Any();
            if (!hasTableDefs && !hasViewDefs) return;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                if (hasTableDefs)
                {
                    dynamic entities = modelObjects.Collect(root, "Entity");
                    if (entities != null)
                    {
                        foreach (dynamic entity in entities)
                        {
                            if (entity == null) continue;
                            try
                            {
                                string objectId = entity.ObjectId?.ToString() ?? "";
                                if (string.IsNullOrEmpty(objectId)) continue;
                                Dictionary<string, string> vals = _udpRuntimeService.ReadUdpValues(entity, "Table");
                                var nonEmpty = vals
                                    .Where(kv => !string.IsNullOrEmpty(kv.Value))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                                if (nonEmpty.Count > 0) _udpRecoveryEntity[objectId] = nonEmpty;
                            }
                            catch (Exception ex) { Log($"CaptureUdpRecoverySnapshot: entity read error: {ex.Message}"); }
                        }
                    }
                }

                if (hasViewDefs)
                {
                    dynamic views = modelObjects.Collect(root, "View");
                    if (views != null)
                    {
                        foreach (dynamic view in views)
                        {
                            if (view == null) continue;
                            try
                            {
                                string viewId = view.ObjectId?.ToString() ?? "";
                                if (string.IsNullOrEmpty(viewId)) continue;
                                Dictionary<string, string> vals = _udpRuntimeService.ReadUdpValues((object)view, "View");
                                var nonEmpty = vals
                                    .Where(kv => !string.IsNullOrEmpty(kv.Value))
                                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                                if (nonEmpty.Count > 0) _udpRecoveryView[viewId] = nonEmpty;
                            }
                            catch (Exception ex) { Log($"CaptureUdpRecoverySnapshot: view read error: {ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CaptureUdpRecoverySnapshot failed: {ex.Message}");
            }
            Log($"CaptureUdpRecoverySnapshot: buffered {_udpRecoveryEntity.Count} entit{(_udpRecoveryEntity.Count == 1 ? "y" : "ies")} + {_udpRecoveryView.Count} view(s) with admin UDP values.");
        }

        /// <summary>
        /// Restores Table and View UDP values after their DEFINITIONS were
        /// recreated by the UDP-editor-close recovery (deleting a Property_Type
        /// wipes every instance value with it). Source = the recovery buffer
        /// captured on editor OPEN (<see cref="CaptureUdpRecoverySnapshot"/>),
        /// NOT the lazy background snapshots - so a value is never lost just
        /// because the backfill had not run yet. One-off Entity/View walks;
        /// per-object failures are logged, never thrown. Restored values equal
        /// what was in the model before the delete, so the change observers see
        /// no diff afterwards.
        /// </summary>
        public void RestoreTrackedUdpValues(IReadOnlyCollection<string> tableUdpNames, IReadOnlyCollection<string> viewUdpNames)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;

            int entitiesRestored = 0, viewsRestored = 0;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                if (tableUdpNames != null && tableUdpNames.Count > 0 && _udpRecoveryEntity.Count > 0)
                {
                    dynamic entities = modelObjects.Collect(root, "Entity");
                    if (entities != null)
                    {
                        foreach (dynamic entity in entities)
                        {
                            if (entity == null) continue;
                            try
                            {
                                string objectId = entity.ObjectId?.ToString() ?? "";
                                if (string.IsNullOrEmpty(objectId)) continue;
                                if (!_udpRecoveryEntity.TryGetValue(objectId, out var snapVals) || snapVals == null) continue;

                                var toWrite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var name in tableUdpNames)
                                {
                                    if (snapVals.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
                                        toWrite[name] = v;
                                }
                                if (toWrite.Count == 0) continue;

                                _udpRuntimeService.WriteUdpValues(entity, toWrite, "Table");
                                entitiesRestored++;
                            }
                            catch (Exception ex) { Log($"RestoreTrackedUdpValues: entity restore error: {ex.Message}"); }
                        }
                    }
                }

                if (viewUdpNames != null && viewUdpNames.Count > 0 && _udpRecoveryView.Count > 0)
                {
                    dynamic views = modelObjects.Collect(root, "View");
                    if (views != null)
                    {
                        foreach (dynamic view in views)
                        {
                            if (view == null) continue;
                            try
                            {
                                string viewId = view.ObjectId?.ToString() ?? "";
                                if (string.IsNullOrEmpty(viewId)) continue;
                                if (!_udpRecoveryView.TryGetValue(viewId, out var snapVals) || snapVals == null) continue;

                                var toWrite = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var name in viewUdpNames)
                                {
                                    if (snapVals.TryGetValue(name, out var v) && !string.IsNullOrEmpty(v))
                                        toWrite[name] = v;
                                }
                                if (toWrite.Count == 0) continue;

                                _udpRuntimeService.WriteUdpValues((object)view, toWrite, "View");
                                viewsRestored++;
                            }
                            catch (Exception ex) { Log($"RestoreTrackedUdpValues: view restore error: {ex.Message}"); }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"RestoreTrackedUdpValues failed: {ex.Message}");
            }
            Log($"RestoreTrackedUdpValues: values restored on {entitiesRestored} entit{(entitiesRestored == 1 ? "y" : "ies")}, {viewsRestored} view(s).");
        }

        /// <summary>
        /// Routes the Required-popup Cancel "discard the new object" contract to
        /// the right delete + bookkeeping for the object's type. Views MUST go
        /// through <see cref="TryDeleteNewView"/> (cleans the V_ name snapshot,
        /// watched-property and UDP buckets); routing them to
        /// <see cref="TryDeleteNewEntity"/> removed the COM object but left the
        /// view tracking dictionaries stale, so the new-view pipeline kept
        /// driving a dead object (review finding 2026-06-12).
        /// </summary>
        private bool DiscardNewObjectForRequiredCancel(string objectType, dynamic scapiObject, string name)
        {
            if (string.Equals(objectType, "View", StringComparison.OrdinalIgnoreCase))
            {
                string viewId = "";
                try { viewId = scapiObject.ObjectId?.ToString() ?? ""; }
                catch (Exception ex) { Log($"DiscardNewObjectForRequiredCancel: view ObjectId read failed for '{name}': {ex.Message}"); }
                return TryDeleteNewView(scapiObject, viewId, name);
            }
            return TryDeleteNewEntity(scapiObject, name);
        }

        private bool TryDeleteNewEntity(dynamic entity, string physicalName)
        {
            if (entity == null) return false;

            string objectId = "";
            try { objectId = entity.ObjectId?.ToString() ?? ""; }
            catch (Exception ex) { Log($"TryDeleteNewEntity: ObjectId read failed for '{physicalName}': {ex.Message}"); }

            int transId = 0;
            bool transOpen = false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                transId = _session.BeginNamedTransaction("DiscardNewEntity");
                transOpen = true;

                modelObjects.Remove(entity);

                _session.CommitTransaction(transId);
                transOpen = false;

                if (!string.IsNullOrEmpty(objectId))
                    _entitySnapshots.Remove(objectId);

                Log($"TryDeleteNewEntity: removed '{physicalName}' (objectId={objectId})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDeleteNewEntity: remove failed for '{physicalName}': {ex.Message}");
                if (transOpen)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"TryDeleteNewEntity: rollback failed: {rbEx.Message}"); }
                }
                return false;
            }
        }

        /// <summary>
        /// Revert a property on an existing entity to its pre-edit baseline
        /// value, in response to the user cancelling the Required popup
        /// (Update mode). Uses <see cref="NamingValidationEngine.WriteAccessorFor"/>
        /// to map the rule's read accessor to the correct write accessor
        /// (e.g. Name_Qualifier reads → Schema_Ref writes). Also resets
        /// the snapshot's PhysicalName / WatchedProperties entry so the
        /// next monitor tick does not see the revert itself as a drift.
        /// </summary>
        /// <returns>True when the revert succeeded; false on any failure
        /// (caller treats false as "leave the entity in its current
        /// invalid state and let the consolidated warning popup fire").</returns>
        private bool TryRevertEntityProperty(dynamic entity, string objectId, string physicalName, string propertyCode, string oldValue)
        {
            if (entity == null || string.IsNullOrEmpty(propertyCode)) return false;

            string writeAccessor = NamingValidationEngine.WriteAccessorFor(propertyCode);
            string newValue = oldValue ?? "";
            int transId = 0;
            bool transOpen = false;
            try
            {
                transId = _session.BeginNamedTransaction("RevertRequiredField");
                transOpen = true;

                entity.Properties(writeAccessor).Value = newValue;

                _session.CommitTransaction(transId);
                transOpen = false;

                // Sync snapshot so the next tick's drift detection doesn't
                // re-fire on the revert. PhysicalName is the rename-baseline
                // mirror; the WatchedProperties entry covers all other
                // rule-watched properties uniformly.
                if (!string.IsNullOrEmpty(objectId) && _entitySnapshots.TryGetValue(objectId, out var snap))
                {
                    if (string.Equals(propertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                        snap.PhysicalName = newValue;
                    snap.WatchedProperties[propertyCode] = newValue;
                }

                Log($"TryRevertEntityProperty: '{physicalName}' {propertyCode} reverted to '{newValue}'"
                    + (writeAccessor != propertyCode ? $" (write accessor='{writeAccessor}')" : ""));
                return true;
            }
            catch (Exception ex)
            {
                if (transOpen)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"TryRevertEntityProperty: rollback failed: {rbEx.Message}"); }
                    transOpen = false;
                }

                // erwin SCVT_OBJID quirk (Schema_Ref / etc.): the property is
                // an object-reference column, not a string column. Writing ""
                // is rejected ("Failed to set ... from string ''"). When the
                // baseline value we wanted to revert to was empty in the
                // first place (the entity never had a Schema bound, the
                // initial read failed with "does not use a property of ...
                // type"), the "revert" is conceptually a no-op - there is
                // nothing to restore. Treat that path as success so the
                // caller records the session dismissal and the user is not
                // told the cancel "failed" when the revert was a no-op by
                // virtue of the baseline matching the current state.
                bool emptyBaseline = string.IsNullOrEmpty(newValue);
                bool looksLikeObjIdRejection = ex.Message != null
                    && (ex.Message.IndexOf("SCVT_OBJID", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("from string ''", StringComparison.OrdinalIgnoreCase) >= 0);
                if (emptyBaseline && looksLikeObjIdRejection)
                {
                    Log($"TryRevertEntityProperty: '{physicalName}' {propertyCode} no-op revert (empty baseline on SCVT_OBJID property)");
                    return true;
                }

                Log($"TryRevertEntityProperty: revert failed for '{physicalName}'.{propertyCode}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start monitoring entity changes (timer managed by ValidationCoordinatorService)
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;
            _isMonitoring = true;
            Log("TableTypeMonitorService: Monitoring started (timer managed by coordinator)");
        }

        /// <summary>
        /// Stop monitoring
        /// </summary>
        public void StopMonitoring()
        {
            _isMonitoring = false;
        }

        /// <summary>
        /// Phase-1B (2026-05-06): drop the entity snapshot dictionary and arm the
        /// silent-population flag so the next CheckForTableTypeChanges call re-baselines
        /// without firing AddEntityToDiagram / ApplyStandardsToEntity / ApplyDefaults /
        /// ValidateNamingStandard. Replaces sync TakeSnapshot() in bulk-create paths.
        /// </summary>
        public void RebaselineDeferred()
        {
            _entitySnapshots.Clear();
            _keyGroupSnapshots.Clear();
            _initialScanCycle = true;
            _diagLoggedEmptyUdp.Clear();
            // View tracking re-baselines with everything else: the next view
            // walk re-snapshots silently instead of treating every existing
            // view as freshly created.
            _viewBaselineDone = false;
            _viewUdpSnapshots.Clear();
            // Drop any held placeholder views so a model reload starts clean.
            _pendingViews.Clear();
            _pendingViewAddedAt.Clear();
            Log("TableTypeMonitorService: Snapshot dropped, deferred rebaseline scheduled (next cycle silent)");
        }

        /// <summary>
        /// Take initial snapshot of all entities
        /// </summary>
        public void TakeSnapshot()
        {
            try
            {
                _entitySnapshots.Clear();

                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string objectId = "";
                    string entityName = "";
                    string physicalName = "";

                    try { objectId = entity.ObjectId?.ToString() ?? ""; }
                    catch (Exception ex) { Log($"ObjectId read error: {ex.Message}"); continue; }
                    try { entityName = entity.Name ?? ""; }
                    catch (Exception ex) { Log($"Entity Name read error: {ex.Message}"); }
                    try
                    {
                        string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physicalName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : entityName;
                    }
                    catch (Exception ex) { physicalName = entityName; Log($"Physical_Name read error: {ex.Message}"); }

                    if (!string.IsNullOrEmpty(objectId))
                    {
                        var snapshot = new EntitySnapshot
                        {
                            ObjectId = objectId,
                            EntityName = entityName,
                            PhysicalName = physicalName
                        };

                        // Also snapshot UDP values for dependency monitoring
                        if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
                        {
                            snapshot.UdpValues = _udpRuntimeService.ReadUdpValues((object)entity);
                        }

                        // Snapshot every property the active naming-rule set
                        // targets on Table, so the next tick can detect a
                        // user clearing / editing one of them without
                        // renaming the entity. Reads via SCAPI; failures are
                        // treated as empty per the same contract Step 3b
                        // uses in ValidateNamingStandard.
                        RefreshWatchedProperties(entity, snapshot);

                        _entitySnapshots[objectId] = snapshot;
                    }
                }

                // Snapshot Key_Group (Index) names
                _keyGroupSnapshots.Clear();
                try
                {
                    dynamic allKeyGroups = modelObjects.Collect(root, "Key_Group");
                    if (allKeyGroups != null)
                    {
                        foreach (dynamic kg in allKeyGroups)
                        {
                            if (kg == null) continue;
                            try
                            {
                                string kgId = kg.ObjectId?.ToString() ?? "";
                                string kgName = kg.Name ?? "";
                                if (!string.IsNullOrEmpty(kgId))
                                    _keyGroupSnapshots[kgId] = kgName;
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Key_Group snapshot item error: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Key_Group snapshot error: {ex.Message}"); }

                int populatedCount = _entitySnapshots.Values.Count(s => s.UdpValues.Count > 0);
                int emptyCount = _entitySnapshots.Count - populatedCount;
                Log($"TableTypeMonitorService: Snapshot taken - {_entitySnapshots.Count} entities ({populatedCount} with UDP values tracked, {emptyCount} without), {_keyGroupSnapshots.Count} key groups");

                // Sample first entity's UDP values for diagnostics (so user can see what's tracked)
                var sampleSnap = _entitySnapshots.Values.FirstOrDefault(s => s.UdpValues.Count > 0);
                if (sampleSnap != null)
                {
                    string sampleStr = string.Join(", ", sampleSnap.UdpValues.Take(5).Select(kv => $"{kv.Key}='{kv.Value}'"));
                    Log($"  Sample entity '{sampleSnap.PhysicalName}': {sampleStr}");
                }
                _diagLoggedEmptyUdp.Clear();
            }
            catch (Exception ex)
            {
                Log($"TableTypeMonitorService.TakeSnapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check for entity changes (called by ValidationCoordinatorService)
        /// </summary>
        public void CheckForTableTypeChanges()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return;

                CheckForTableTypeChanges(allEntities);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForTableTypeChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Check for entity changes using pre-collected entity list (avoids double model scan).
        /// Detects new entities, physical name changes, and UDP value changes.
        /// </summary>
        public void CheckForTableTypeChanges(dynamic allEntities)
        {
            // (MODEL_PATH baseline + read-only enforcement removed 2026-05-07 along
            // with the SetModelPathValue startup write path - the UDP is no longer
            // populated by the add-in, so there is nothing to enforce.)

            try
            {
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string objectId = "";
                    string entityName = "";
                    string physicalName = "";

                    try { objectId = entity.ObjectId?.ToString() ?? ""; }
                    catch (Exception ex) { Log($"ObjectId read error: {ex.Message}"); continue; }
                    try { entityName = entity.Name ?? ""; }
                    catch (Exception ex) { Log($"Entity Name read error: {ex.Message}"); }
                    try
                    {
                        string physName = entity.Properties("Physical_Name").Value?.ToString() ?? "";
                        physicalName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : entityName;
                    }
                    catch (Exception ex) { physicalName = entityName; Log($"Physical_Name read error: {ex.Message}"); }

                    // Check if this is a new entity or physical name changed
                    bool isNew = !_entitySnapshots.ContainsKey(objectId);

                    // Check if physical name changed (user manually edited the table name)
                    bool physicalNameChanged = !isNew &&
                        _entitySnapshots[objectId].PhysicalName != physicalName;

                    if (physicalNameChanged)
                    {
                        // Physical name changed (user manually edited table name)
                        string oldPhysicalName = _entitySnapshots[objectId].PhysicalName;
                        Log($"Physical name changed for entity: '{oldPhysicalName}' -> '{physicalName}'");

                        // Validate Table naming standards (with auto-apply)
                        ValidateNamingStandard("Table", physicalName, entity);

                        // Update snapshot with new name
                        _entitySnapshots[objectId].PhysicalName = physicalName;

                        // Keep watched-property snapshot in sync after rename
                        // so the next tick's diff doesn't false-fire on
                        // properties the user didn't actually touch.
                        RefreshWatchedProperties(entity, _entitySnapshots[objectId]);
                    }
                    else if (!isNew)
                    {
                        // Watched-property drift detection (2026-05-17): a
                        // naming rule can target ANY property the admin
                        // chose (e.g. TABLE.Name_Qualifier for the Owner
                        // popup), not just Physical_Name. If the user
                        // clears or edits one of those properties on an
                        // existing entity, re-fire ValidateNamingStandard
                        // so the Required popup pops just like the new-
                        // entity / rename paths.
                        if (DetectWatchedPropertyChange(entity, _entitySnapshots[objectId], out string changedProperty, out string oldVal, out string newVal))
                        {
                            Log($"Watched property changed on '{physicalName}': {changedProperty} '{oldVal}' -> '{newVal}' - re-running naming check");
                            ValidateNamingStandard("Table", physicalName, entity);
                            // Refresh the entire snapshot map post-validation
                            // in case the validator wrote back any defaults
                            // / auto-apply changes that should not re-fire
                            // the diff on the next tick.
                            RefreshWatchedProperties(entity, _entitySnapshots[objectId]);
                        }
                    }

                    // Check for UDP value changes only on existing entities with known snapshots
                    // This reads UDP values via COM — only done for entities already in snapshot
                    if (!isNew && !physicalNameChanged && _udpRuntimeService != null && _udpRuntimeService.IsInitialized
                        && _entitySnapshots.ContainsKey(objectId))
                    {
                        if (_entitySnapshots[objectId].UdpValues.Count > 0)
                        {
                            CheckForUdpValueChanges(entity, objectId, physicalName);
                        }
                        else if (!_udpBackfillPending && _diagLoggedEmptyUdp.Add(objectId))
                        {
                            // Diagnostic: snapshot has zero tracked UDP values for this entity.
                            // Means ReadUdpValues returned empty at snapshot time. Either
                            // UdpDefinitionService had no defs for "Table" then, or every
                            // Properties("Entity.Physical.<udp>") access threw and was swallowed.
                            // Suppressed while Phase-1A UDP backfill is still pending — emptiness
                            // there is expected, not a problem to flag.
                            Log($"[Diag] Skipping UDP check for '{physicalName}' (id={objectId.Substring(0, Math.Min(8, objectId.Length))}...): snapshot has 0 tracked UDP values");
                        }
                    }

                    if (isNew)
                    {
                        // Add new entity to snapshot
                        _entitySnapshots[objectId] = new EntitySnapshot
                        {
                            ObjectId = objectId,
                            EntityName = entityName,
                            PhysicalName = physicalName
                        };

                        if (_initialScanCycle)
                        {
                            // Phase-1A initial silent population: this entity already existed
                            // when the addin loaded. Do NOT apply standards / defaults / naming
                            // validation retroactively (rules new/changed only). UDP values are
                            // also NOT snapshotted here to keep the burst short — UDP change
                            // detection for these entities will activate the next time
                            // CheckForUdpValueChanges sees them with non-empty UdpValues, which
                            // happens after a user-driven re-snapshot or rename.
                        }
                        else
                        {
                            // Add new entity to diagram automatically
                            AddEntityToDiagram(entity, physicalName);

                            // Apply model standard properties (LOGGING, COMPRESSION, etc.)
                            if (_propertyApplicator != null && _propertyApplicator.IsInitialized)
                            {
                                _propertyApplicator.ApplyStandardsToEntity(entity, physicalName);

                                // Delete auto-created PK index if model property is enabled
                                if (_propertyApplicator.IsPropertyEnabled("DELETE_AUTO_CREATED_INDEX_PK"))
                                {
                                    DeleteAutoCreatedPKIndex(entity, physicalName);
                                }
                            }

                            // Apply UDP defaults for new entity
                            if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
                            {
                                try
                                {
                                    _udpRuntimeService.ApplyDefaults(entity);
                                    Log($"UDP defaults applied for new entity '{physicalName}'");

                                    // Snapshot UDP values after defaults applied (so first check doesn't re-trigger)
                                    _entitySnapshots[objectId].UdpValues = _udpRuntimeService.ReadUdpValues((object)entity);
                                }
                                catch (Exception ex)
                                {
                                    Log($"UDP defaults error for '{physicalName}': {ex.Message}");
                                }
                            }

                            // Validate Table naming standards for new entity (with auto-apply).
                            // isNew=true so the Required-popup Cancel branch deletes
                            // the entity rather than reverting (no pre-edit state to
                            // revert to on a freshly-created object).
                            ValidateNamingStandard("Table", physicalName, entity, isNew: true);

                            // RefreshWatchedProperties is only safe when the entity
                            // still exists. ValidateNamingStandard's Cancel branch may
                            // have deleted it; check the snapshot before touching it.
                            if (_entitySnapshots.TryGetValue(objectId, out var refreshSnap))
                                RefreshWatchedProperties(entity, refreshSnap);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForTableTypeChanges error: {ex.Message}");
            }

            // Check Key_Group, View, Subject Area naming (throttled — 3 model scans per call)
            _cycleCount++;
            if (_cycleCount % HeavyScanInterval == 0)
            {
                CheckKeyGroupAndViewNaming();
            }

            // Phase-1A: first call was a silent baseline. Subsequent calls treat genuinely
            // new entities normally (apply standards, defaults, naming validation, etc.).
            if (_initialScanCycle)
            {
                _initialScanCycle = false;
                Log($"TableTypeMonitorService: Initial silent population complete - {_entitySnapshots.Count} entities baselined; live entity validation now active");

                // 2026-05-05: UDP value backfill DISABLED on big-model evidence. Per-entity
                // ReadUdpValues was measured at ~1s on SQL_BUYUKMODEL (280 entities), so a
                // batch of 30 produced ~30s STA-thread blocks every full cycle for ~10 minutes.
                // erwin UI froze (right-click menus would not open). The backfill code below
                // is preserved (unreachable) for reference; if value-change detection on
                // pre-existing entities is needed later, it must be driven by a user-initiated
                // refresh or a different (lighter) probe — not a periodic background walk.
                // The pending flag remains false so BackfillUdpValuesChunk is never invoked.
            }
            // _udpBackfillPending is intentionally never set true under Phase-1A.
        }

        /// <summary>
        /// Phase-1A: read UdpValues for up to UdpBackfillBatchSize entities per call whose
        /// snapshot was created during the initial silent population (Count == 0). Walks the
        /// caller-supplied allEntities collection, looks each entity up by ObjectId in the
        /// snapshot dict, and fills it in via UdpRuntimeService.ReadUdpValues.
        /// When no empty snapshots remain, the diag-logged set is cleared (so any later genuine
        /// "0 UDPs" cases get a fresh diagnostic line) and the pending flag flips.
        /// </summary>
        private void BackfillUdpValuesChunk(dynamic allEntities)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized)
            {
                _udpBackfillPending = false;
                return;
            }

            int processed = 0;
            int remaining = 0;
            int errors = 0;

            try
            {
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string objectId = "";
                    try { objectId = entity.ObjectId?.ToString() ?? ""; }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"UDP backfill ObjectId read error: {ex.Message}"); continue; }
                    if (string.IsNullOrEmpty(objectId)) continue;

                    if (!_entitySnapshots.TryGetValue(objectId, out EntitySnapshot snap)) continue;
                    if (snap.UdpValues != null && snap.UdpValues.Count > 0) continue;

                    if (processed >= UdpBackfillBatchSize)
                    {
                        remaining++;
                        continue;
                    }

                    try
                    {
                        snap.UdpValues = _udpRuntimeService.ReadUdpValues((object)entity);
                        processed++;
                    }
                    catch (Exception ex)
                    {
                        errors++;
                        Log($"UDP backfill failed for entity '{snap.PhysicalName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"UDP backfill chunk error: {ex.Message}");
                return;
            }

            if (processed > 0)
                Log($"TableTypeMonitorService: UDP backfill processed {processed} entit{(processed == 1 ? "y" : "ies")}{(errors > 0 ? $" ({errors} error(s))" : string.Empty)}, {remaining} remaining");

            if (remaining == 0)
            {
                _udpBackfillPending = false;
                // Reset the diagnostic-logged set so any entity that legitimately has no UDP
                // values (post-backfill) can produce a fresh log line on the next check.
                _diagLoggedEmptyUdp.Clear();
                int populated = _entitySnapshots.Values.Count(s => s.UdpValues.Count > 0);
                Log($"TableTypeMonitorService: UDP value backfill complete - {populated}/{_entitySnapshots.Count} entit{(_entitySnapshots.Count == 1 ? "y" : "ies")} have tracked UDP values");
            }
        }

        /// <summary>
        /// Public driver for the View + Subject Area scan. Wired into
        /// ValidationCoordinatorService.MonitorTimer_Tick's periodic block
        /// (2026-06-12): the historical driver, CheckForTableTypeChanges, has
        /// had NO caller since Phase-2D, so the whole view/SA scan - including
        /// the pre-existing rename/drift naming checks - was dead code and
        /// none of the table checks ever fired for views (the user-reported
        /// symptom). The caller is responsible for the _scopedCheckInProgress
        /// gate: the new-view pipeline opens modal dialogs whose message pump
        /// re-fires the monitor timers.
        /// </summary>
        public void RunViewAndSubjectAreaScan() => CheckKeyGroupAndViewNaming();

        /// <summary>
        /// Check for new/renamed Key_Group (Index) and View objects and validate naming standards.
        /// </summary>
        private void CheckKeyGroupAndViewNaming()
        {
            if (!NamingStandardService.Instance.IsLoaded) return;

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                // Key_Group (Index) monitoring moved to ValidationCoordinatorService.CheckEntityKeyGroups
                // (runs during entity batch scan — no separate Collect needed)

                // View monitoring
                try
                {
                    dynamic allViews = modelObjects.Collect(root, "View");
                    if (allViews != null)
                    {
                        // New views found this pass. The pipeline (modal Required
                        // popup, possible Cancel-delete) MUST run after the COM
                        // enumeration completes - removing a member while the
                        // collection is being iterated can wedge the enumerator.
                        var newViewsThisPass = new List<(dynamic View, string Id, string Name)>();

                        foreach (dynamic view in allViews)
                        {
                            if (view == null) continue;
                            try
                            {
                                string viewId = view.ObjectId?.ToString() ?? "";
                                string viewName = "";
                                try
                                {
                                    string physName = view.Properties("Physical_Name").Value?.ToString() ?? "";
                                    viewName = (!string.IsNullOrEmpty(physName) && !physName.StartsWith("%")) ? physName : (view.Name ?? "");
                                }
                                catch (Exception ex) { viewName = view.Name ?? ""; System.Diagnostics.Debug.WriteLine($"View Physical_Name read error: {ex.Message}"); }

                                if (string.IsNullOrEmpty(viewId) || string.IsNullOrEmpty(viewName)) continue;

                                bool isNew = !_keyGroupSnapshots.ContainsKey("V_" + viewId);
                                bool nameChanged = !isNew && _keyGroupSnapshots["V_" + viewId] != viewName;

                                if (isNew)
                                {
                                    // Snapshot first (a reentrant tick from the
                                    // pipeline's modal must see this view as known,
                                    // never double-firing the pipeline).
                                    _keyGroupSnapshots["V_" + viewId] = viewName;
                                    var bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    ReadWatchedProperties(view, "View", bucket);
                                    _viewWatchedProperties[viewId] = bucket;
                                    RefreshViewUdpSnapshot(view, viewId);

                                    // Pre-existing views on the first walk are
                                    // baseline, not user gestures; only act once
                                    // baselined.
                                    if (_viewBaselineDone)
                                    {
                                        // DEFER like tables: a view dropped with
                                        // erwin's placeholder name ("V/<n>") is
                                        // HELD until the user commits a real name
                                        // (inline-edit close -> CommitPendingViews,
                                        // or the rename branch below). Firing now
                                        // would pop "Naming standard applied" on
                                        // the placeholder before the user types.
                                        // A view created already-named (paste /
                                        // scripted create) is not a placeholder,
                                        // so it fires immediately - table parity.
                                        if (IsPlaceholderViewName(viewName))
                                        {
                                            _pendingViews.Add(viewId);
                                            _pendingViewAddedAt[viewId] = DateTime.UtcNow;
                                            Log($"[PENDING-VIEW] viewId={viewId} name='{viewName}' - placeholder, holding for inline-edit commit / rename");
                                        }
                                        else
                                        {
                                            newViewsThisPass.Add((view, viewId, viewName));
                                        }
                                    }
                                }
                                else if (nameChanged)
                                {
                                    // A pending placeholder view whose name just
                                    // changed = the user committed a real name via
                                    // rename. Treat it as the creation gesture
                                    // (full new-view pipeline, isNew) instead of
                                    // the existing-view drift check - mirror the
                                    // entity heartbeat rename branch (wasPending =>
                                    // isNew). Route through newViewsThisPass so the
                                    // modal / possible Cancel-delete fires AFTER
                                    // the COM enumerator is released (never inside
                                    // it - would wedge the live enumerator).
                                    bool wasPendingView = _pendingViews.Remove(viewId);
                                    _pendingViewAddedAt.Remove(viewId);
                                    if (wasPendingView)
                                    {
                                        _keyGroupSnapshots["V_" + viewId] = viewName;
                                        if (IsPlaceholderViewName(viewName))
                                        {
                                            // placeholder -> placeholder (e.g. user
                                            // cleared the name back): keep deferred.
                                            _pendingViews.Add(viewId);
                                            _pendingViewAddedAt[viewId] = DateTime.UtcNow;
                                        }
                                        else
                                        {
                                            Log($"[PENDING-VIEW] viewId={viewId} committed via rename to '{viewName}' - firing new-view pipeline (isNew)");
                                            newViewsThisPass.Add((view, viewId, viewName));
                                        }
                                    }
                                    else
                                    {
                                        // Genuine drift rename of an already-
                                        // committed view (Update context).
                                        ValidateNamingStandard("View", viewName, view);
                                        _keyGroupSnapshots["V_" + viewId] = viewName;
                                        if (!_viewWatchedProperties.TryGetValue(viewId, out var refreshBucket))
                                        {
                                            refreshBucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                            _viewWatchedProperties[viewId] = refreshBucket;
                                        }
                                        ReadWatchedProperties(view, "View", refreshBucket);
                                        CheckViewUdpChanges(view, viewId, viewName);
                                    }
                                }
                                else
                                {
                                    // A view still HELD pending a name commit runs
                                    // NO validation - drift/UDP checks would fire the
                                    // naming popup on the placeholder before the user
                                    // has named it (the whole point of the deferral).
                                    // Its checks run once CommitPendingViews fires the
                                    // full pipeline. (No-op today since View rules are
                                    // Name-only and Name routes through nameChanged,
                                    // but this enforces the invariant if admin adds a
                                    // rule on a non-Name View property.)
                                    if (_pendingViews.Contains(viewId))
                                        continue;

                                    // Drift detection (2026-05-17): a naming rule on
                                    // View.<some property> fires the Required popup
                                    // when the property is cleared on an existing view.
                                    if (!_viewWatchedProperties.TryGetValue(viewId, out var prevBucket))
                                    {
                                        prevBucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        ReadWatchedProperties(view, "View", prevBucket);
                                        _viewWatchedProperties[viewId] = prevBucket;
                                    }
                                    else if (DetectScapiPropertyChange(view, "View", prevBucket,
                                        out string changedProp, out string oldVal, out string newVal))
                                    {
                                        Log($"Watched property changed on view '{viewName}': {changedProp} '{oldVal}' -> '{newVal}' - re-running naming check");
                                        ValidateNamingStandard("View", viewName, view);
                                        ReadWatchedProperties(view, "View", prevBucket);
                                    }
                                    CheckViewUdpChanges(view, viewId, viewName);
                                }
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"View naming check error: {ex.Message}"); }
                        }

                        // First completed walk = baseline established.
                        _viewBaselineDone = true;

                        foreach (var nv in newViewsThisPass)
                        {
                            try { OnNewViewDetected(nv.View, nv.Id, nv.Name); }
                            catch (Exception ex) { Log($"OnNewViewDetected error for '{nv.Name}': {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"View collect error: {ex.Message}"); }

                // Subject Area monitoring
                try
                {
                    dynamic allSAs = modelObjects.Collect(root, "Subject_Area");
                    if (allSAs != null)
                    {
                        foreach (dynamic sa in allSAs)
                        {
                            if (sa == null) continue;
                            try
                            {
                                string saId = sa.ObjectId?.ToString() ?? "";
                                string saName = sa.Name ?? "";
                                if (string.IsNullOrEmpty(saId) || string.IsNullOrEmpty(saName)) continue;

                                string saKey = "SA_" + saId;
                                bool isNew = !_keyGroupSnapshots.ContainsKey(saKey);
                                bool nameChanged = !isNew && _keyGroupSnapshots[saKey] != saName;

                                if (isNew)
                                {
                                    // New SA — just snapshot, validate on name change
                                    _keyGroupSnapshots[saKey] = saName;
                                    var bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    ReadWatchedProperties(sa, "Subject Area", bucket);
                                    _subjectAreaWatchedProperties[saId] = bucket;
                                }
                                else if (nameChanged)
                                {
                                    ValidateNamingStandard("Subject Area", saName, sa);
                                    _keyGroupSnapshots[saKey] = saName;
                                    if (!_subjectAreaWatchedProperties.TryGetValue(saId, out var refreshBucket))
                                    {
                                        refreshBucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        _subjectAreaWatchedProperties[saId] = refreshBucket;
                                    }
                                    ReadWatchedProperties(sa, "Subject Area", refreshBucket);
                                }
                                else
                                {
                                    // Drift detection (2026-05-17): same logic as
                                    // the View branch above.
                                    if (!_subjectAreaWatchedProperties.TryGetValue(saId, out var prevBucket))
                                    {
                                        prevBucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        ReadWatchedProperties(sa, "Subject Area", prevBucket);
                                        _subjectAreaWatchedProperties[saId] = prevBucket;
                                    }
                                    else if (DetectScapiPropertyChange(sa, "Subject Area", prevBucket,
                                        out string changedProp, out string oldVal, out string newVal))
                                    {
                                        Log($"Watched property changed on subject area '{saName}': {changedProp} '{oldVal}' -> '{newVal}' - re-running naming check");
                                        ValidateNamingStandard("Subject Area", saName, sa);
                                        ReadWatchedProperties(sa, "Subject Area", prevBucket);
                                    }
                                }
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Subject_Area naming check error: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Subject_Area collect error: {ex.Message}"); }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckKeyGroupAndViewNaming error: {ex.Message}");
            }
        }

        /// <summary>
        /// New-VIEW pipeline (2026-06-12, "table checks for views" Faz 1) - the
        /// view-object analogue of <see cref="OnNewEntityDetected"/>:
        ///   1. naming standard apply (Create context),
        ///   2. UDP defaults from the admin's View-scoped definitions,
        ///   3. required-View-UDP prompt (Cancel discards the new view, same
        ///      contract as tables).
        /// Deliberately OUT of scope (user decision 2026-06-12): predefined
        /// columns (view columns derive from the SELECT - nothing to add) and
        /// the question wizard (its admin config is TABLE-scoped). View COLUMN
        /// checks were also ruled out (Faz 2 cancelled).
        /// </summary>
        private void OnNewViewDetected(dynamic view, string viewId, string viewName)
        {
            if (view == null || string.IsNullOrEmpty(viewName)) return;
            Log($"OnNewViewDetected: '{viewName}' - running view-object pipeline (UDP defaults + naming + required UDPs)");

            // 1. UDP defaults FIRST (table-path order, see the timer isNew
            // branch: defaults run before naming so UDP-conditional naming
            // rules evaluate against the seeded values).
            if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
            {
                try
                {
                    _udpRuntimeService.ApplyDefaults(view, "View");
                    Log($"UDP defaults applied for new view '{viewName}'");
                }
                catch (Exception ex)
                {
                    Log($"OnNewViewDetected: UDP defaults error for '{viewName}': {ex.Message}");
                }
            }

            // 2. Naming standard (Create context: ApplyOn=Create/Both rules).
            // Its Required-popup Cancel may DELETE the view (routed through
            // TryDeleteNewView, which removes the V_ snapshot) - bail out
            // before touching the dead COM object.
            try
            {
                ValidateNamingStandard("View", viewName, view, isNew: true);
            }
            catch (Exception ex)
            {
                Log($"OnNewViewDetected: naming standard error for '{viewName}': {ex.Message}");
            }
            if (!_keyGroupSnapshots.ContainsKey("V_" + viewId))
            {
                Log($"OnNewViewDetected: '{viewName}' discarded during naming validation - pipeline stopped");
                return;
            }

            // Rules may have renamed the view; re-read so the snapshot and the
            // dialogs below carry the final name (otherwise the next tick sees
            // our own rename as a user rename). erwin r10 Views carry no
            // Physical_Name property - a view's name lives in "Name".
            try
            {
                string finalName = view.Name ?? viewName;
                if (!string.IsNullOrEmpty(finalName) && finalName != viewName)
                {
                    _keyGroupSnapshots["V_" + viewId] = finalName;
                    viewName = finalName;
                }
            }
            catch (Exception ex) { Log($"OnNewViewDetected: final-name re-read failed for '{viewName}': {ex.Message}"); }

            // 3. Required View UDPs (Cancel deletes the new view, same contract
            // as tables) - on delete, stop before re-reading the dead object.
            if (_udpRuntimeService != null && _udpRuntimeService.IsInitialized)
            {
                bool cancelledAndDeleted = false;
                try
                {
                    cancelledAndDeleted = PromptForMissingRequiredViewUdps(view, viewId, viewName);
                }
                catch (Exception ex)
                {
                    Log($"OnNewViewDetected: required-UDP prompt error for '{viewName}': {ex.Message}");
                }
                if (cancelledAndDeleted)
                {
                    Log($"OnNewViewDetected: '{viewName}' discarded by user via required-UDP Cancel");
                    return;
                }
            }

            // Refresh both baselines now that the pipeline's own writes are in:
            // - watched properties, so the next drift pass does not re-validate
            //   our own naming/UDP writes as user edits (table parity);
            // - UDP values, so seeds register as the locked baseline ("first
            //   non-empty set allowed").
            try
            {
                if (!_viewWatchedProperties.TryGetValue(viewId, out var bucket))
                {
                    bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _viewWatchedProperties[viewId] = bucket;
                }
                ReadWatchedProperties(view, "View", bucket);
            }
            catch (Exception ex) { Log($"OnNewViewDetected: watched-property refresh failed for '{viewName}': {ex.Message}"); }
            RefreshViewUdpSnapshot(view, viewId);
        }

        /// <summary>
        /// True when a view still wears erwin's auto-assigned placeholder name
        /// that the user has not yet replaced. The new-view pipeline is HELD
        /// (see <see cref="_pendingViews"/>) until the placeholder is committed
        /// via the inline-edit close edge or replaced by a real name, mirroring
        /// the entity <c>IsPlaceholderEntityName</c> contract.
        /// <para>
        /// erwin's diagram auto-name for a new view is the View analogue of the
        /// entity "E/&lt;digits&gt;" form. CRITICAL separator gotcha (live-verified
        /// 2026-06-14): the raw <c>Properties("Name")</c> value is "V/&lt;n&gt;"
        /// (forward slash - logged as liveValue='V/1'), but the <c>view.Name</c>
        /// COM accessor that <see cref="CheckKeyGroupAndViewNaming"/> feeds in
        /// here RENDERS the slash as an UNDERSCORE -> "V_&lt;n&gt;" (the title-bar
        /// fold, same '/'->'_' transform documented for entities). The first live
        /// test missed every placeholder because this matched only "V/" while the
        /// caller passed "V_1". We therefore accept EITHER separator. Views have
        /// NO Physical_Name on r10 SCAPI, so the caller passes view.Name.
        /// </para>
        /// </summary>
        private static bool IsPlaceholderViewName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            if (name.Equals("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("<default>", StringComparison.OrdinalIgnoreCase)) return true;
            // The auto-applied placeholder we write when an AUTO_APPLY naming
            // rule fails (Phase-2H parity with the entity path).
            if (name.StartsWith("PLEASE CHANGE IT", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("PLEASE_CHANGE_IT", StringComparison.OrdinalIgnoreCase)) return true;
            // erwin diagram auto-name "V/<digits>" (raw) rendered as "V_<digits>"
            // by view.Name. Accept BOTH separators; match conservatively (digits-
            // only tail) so a user name like "V_Sales", "V/2_FOO" or "V_6_VVV"
            // still counts as a real, committed name.
            if (name.Length >= 3 && name[0] == 'V' && (name[1] == '/' || name[1] == '_'))
            {
                bool allDigits = true;
                for (int i = 2; i < name.Length; i++)
                {
                    if (!char.IsDigit(name[i])) { allDigits = false; break; }
                }
                if (allDigits) return true;
            }
            return false;
        }

        /// <summary>
        /// Commit edge for deferred new views (2026-06-14): fire the full
        /// new-view pipeline for every view held in <see cref="_pendingViews"/>.
        /// The coordinator calls this from the inline-edit close edge (the same
        /// open-&gt;closed signal that commits a pending table name) and from the
        /// stale-pending drag-create guard. The view name is re-resolved LIVE by
        /// ObjectId at commit time (an AUTO_APPLY naming rule could have renamed
        /// it between hold and commit; views carry no Physical_Name so the read
        /// is view.Name only). Each id is removed from the pending set BEFORE
        /// <see cref="OnNewViewDetected"/> opens its modals, so a reentrant tick
        /// during the modal pump never re-processes it. Fires are queued and run
        /// AFTER the COM walk so a Required-UDP Cancel-delete cannot wedge the
        /// live enumerator.
        /// </summary>
        public void CommitPendingViews()
        {
            if (!NamingStandardService.Instance.IsLoaded) return;
            if (_pendingViews.Count == 0) return;

            var toFire = new List<(dynamic View, string ViewId, string LiveName)>();
            dynamic modelObjects = null;
            dynamic root = null;
            dynamic allViews = null;
            bool walkComplete = false;
            try
            {
                modelObjects = _session.ModelObjects;
                root = modelObjects.Root;
                allViews = modelObjects.Collect(root, "View");
                if (allViews != null)
                {
                    foreach (dynamic view in allViews)
                    {
                        if (view == null) continue;
                        string viewId;
                        try { viewId = view.ObjectId?.ToString() ?? ""; }
                        catch (Exception ex) { Log($"CommitPendingViews: ObjectId read failed: {ex.Message}"); continue; }
                        if (string.IsNullOrEmpty(viewId) || !_pendingViews.Contains(viewId)) continue;

                        // Live name (views have no Physical_Name - "Name" only).
                        string liveName;
                        try { liveName = view.Name ?? ""; }
                        catch (Exception ex) { Log($"CommitPendingViews: name read failed for viewId={viewId}: {ex.Message}"); liveName = ""; }

                        // Lift OUT of pending BEFORE firing (the modal pump can
                        // re-enter this method; the early removal makes the
                        // reentrant pass a no-op = no double popup).
                        _pendingViews.Remove(viewId);
                        _pendingViewAddedAt.Remove(viewId);

                        // Advance the name snapshot to the committed live name so
                        // the next MonitorTimer view scan sees nameChanged=false
                        // and does NOT re-run naming validation a second time in
                        // Update context (the view is no longer pending, so the
                        // nameChanged branch would fall through to the drift path
                        // with isNew=false). The entity machine gets this for free
                        // by advancing _entityDisplayNameSnapshot every tick; the
                        // view scan only advances on its own branches, so the
                        // commit edge must do it here. OnNewViewDetected's own
                        // final-name re-read still refines this if the pipeline
                        // renames the view further (e.g. a suffix rule).
                        if (!string.IsNullOrEmpty(viewId))
                            _keyGroupSnapshots["V_" + viewId] = liveName;

                        toFire.Add((view, viewId, liveName));
                    }
                }
                walkComplete = true;
            }
            catch (Exception ex)
            {
                Log($"CommitPendingViews: view collect error: {ex.Message}");
            }

            // Any id still pending after a COMPLETE walk was not found in the
            // live model = the user deleted/undid the placeholder before
            // committing. Drop it so the stale guard does not re-walk forever.
            // (Skip on an incomplete walk so a transient COM error does not lose
            // a still-live pending view.)
            if (walkComplete && _pendingViews.Count > 0)
            {
                foreach (var goneId in _pendingViews.ToList())
                {
                    _pendingViews.Remove(goneId);
                    _pendingViewAddedAt.Remove(goneId);
                    Log($"[PENDING-VIEW] viewId={goneId} vanished before commit - dropped");
                }
            }

            foreach (var (view, viewId, liveName) in toFire)
            {
                Log($"[PENDING-VIEW] commit edge for viewId={viewId} liveName='{liveName}' - firing new-view pipeline (isNew)");
                try { OnNewViewDetected(view, viewId, liveName); }
                catch (Exception ex) { Log($"CommitPendingViews: OnNewViewDetected error for viewId={viewId} ('{liveName}'): {ex.Message}"); }
            }
        }

        /// <summary>
        /// True when any deferred view has been pending at least
        /// <paramref name="staleMs"/> ms (or carries no timestamp). Backs the
        /// coordinator's drag-create stale-pending guard for views without
        /// exposing the pending maps. Mirrors the entity stale predicate.
        /// </summary>
        public bool HasStalePendingViews(int staleMs)
        {
            if (_pendingViews.Count == 0) return false;
            var nowUtc = DateTime.UtcNow;
            foreach (var id in _pendingViews)
            {
                if (!_pendingViewAddedAt.TryGetValue(id, out var addedAt)
                    || (nowUtc - addedAt).TotalMilliseconds >= staleMs)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// View analogue of <see cref="PromptForMissingRequiredUdps"/>: prompts
        /// for View-scoped IS_REQUIRED UDPs that are still empty after defaults.
        /// Cancel discards the new view (mirrors the new-table contract). The
        /// table version's predefined-column cascade has no view counterpart;
        /// the UDP dependency cascade runs via HandleUdpValueChange("View").
        /// </summary>
        /// <returns>True when the user cancelled and the view was deleted.</returns>
        private bool PromptForMissingRequiredViewUdps(dynamic view, string viewId, string viewName)
        {
            Dictionary<string, string> currentValues;
            try { currentValues = _udpRuntimeService.ReadUdpValues((object)view, "View"); }
            catch (Exception ex)
            {
                Log($"PromptForMissingRequiredViewUdps: ReadUdpValues failed on '{viewName}': {ex.Message}");
                currentValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var missing = UdpDefinitionService.Instance
                .GetByObjectType("View")
                .Where(d => d != null && d.IsRequired)
                .Where(d =>
                {
                    if (!currentValues.TryGetValue(d.Name, out var v)) return true;
                    return string.IsNullOrEmpty(v);
                })
                .ToList();

            if (missing.Count == 0) return false;

            Log($"PromptForMissingRequiredViewUdps: '{viewName}' missing {missing.Count} required UDP(s): {string.Join(", ", missing.Select(m => m.Name))}");

            using (var form = new Forms.RequiredUdpForm(viewName, missing, Forms.RequiredOperationMode.Create, "View"))
            {
                var result = form.ShowDialog();
                if (result != DialogResult.OK || form.SelectedValues.Count == 0)
                {
                    Log($"PromptForMissingRequiredViewUdps: user cancelled for '{viewName}' - deleting new view");
                    return TryDeleteNewView(view, viewId, viewName);
                }

                try
                {
                    _udpRuntimeService.WriteUdpValues((object)view, form.SelectedValues, "View");
                    Log($"PromptForMissingRequiredViewUdps: wrote {form.SelectedValues.Count} required UDP value(s) on '{viewName}'");
                }
                catch (Exception ex)
                {
                    Log($"PromptForMissingRequiredViewUdps: WriteUdpValues failed on '{viewName}': {ex.Message}");
                    return false;
                }

                // Dependency cascade (DEPENDS_ON_UDP_ID chains between View UDPs)
                // - same gesture-driven refresh as the table path.
                foreach (var kvp in form.SelectedValues)
                {
                    try { _udpRuntimeService.HandleUdpValueChange(view, kvp.Key, kvp.Value, "View"); }
                    catch (Exception ex) { Log($"PromptForMissingRequiredViewUdps: dependency cascade failed for '{kvp.Key}': {ex.Message}"); }
                }
                return false;
            }
        }

        /// <summary>
        /// Removes a newly-created view after the user discarded its Required
        /// popup (mirrors <see cref="TryDeleteNewEntity"/>). Cleans the view
        /// tracking snapshots so the next walk does not report a phantom
        /// "view disappeared".
        /// </summary>
        private bool TryDeleteNewView(dynamic view, string viewId, string viewName)
        {
            if (view == null) return false;

            int transId = 0;
            bool transOpen = false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                transId = _session.BeginNamedTransaction("DiscardNewView");
                transOpen = true;

                modelObjects.Remove(view);

                _session.CommitTransaction(transId);
                transOpen = false;

                _keyGroupSnapshots.Remove("V_" + viewId);
                _viewWatchedProperties.Remove(viewId);
                _viewUdpSnapshots.Remove(viewId);
                // A still-pending view discarded via Required-UDP Cancel must
                // not linger in the pending set (would re-fire on the stale guard).
                _pendingViews.Remove(viewId);
                _pendingViewAddedAt.Remove(viewId);

                Log($"TryDeleteNewView: removed '{viewName}' (objectId={viewId})");
                return true;
            }
            catch (Exception ex)
            {
                Log($"TryDeleteNewView: remove failed for '{viewName}': {ex.Message}");
                if (transOpen)
                {
                    try { _session.RollbackTransaction(transId); }
                    catch (Exception rbEx) { Log($"TryDeleteNewView: rollback failed: {rbEx.Message}"); }
                }
                return false;
            }
        }

        /// <summary>Re-reads the view's UDP values into the lock baseline snapshot.</summary>
        private void RefreshViewUdpSnapshot(dynamic view, string viewId)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (string.IsNullOrEmpty(viewId)) return;
            try
            {
                // Zero View-scoped definitions -> ReadUdpValues returns empty at
                // zero property-read cost; skip storing an empty bucket.
                var values = _udpRuntimeService.ReadUdpValues((object)view, "View");
                if (values.Count > 0)
                    _viewUdpSnapshots[viewId] = values;
            }
            catch (Exception ex)
            {
                Log($"RefreshViewUdpSnapshot: read failed for view {viewId}: {ex.Message}");
            }
        }

        /// <summary>
        /// View-UDP change observer - mirrors the table semantics in
        /// <see cref="CheckForUdpValueChanges"/>:
        ///   - LOCKED UDPs: the FIRST non-empty assignment is allowed (wizards /
        ///     ApplyDefaults / the Required popup seed the field), every later
        ///     edit is reverted with the "UDP Locked" warning;
        ///   - other changes run the dependency cascade
        ///     (<see cref="UdpRuntimeService.HandleUdpValueChange"/>) and re-run
        ///     the View naming standard so UDP-conditional naming rules
        ///     (e.g. a prefix scoped to a View UDP value) follow the edit;
        ///   - cascade writes are absorbed silently by the wholesale snapshot
        ///     re-read at the end (same timing-based system-write exclusion as
        ///     the table block - read once BEFORE the cascade, re-read after).
        /// Cost-gated: exits when the admin defined no View UDPs at all.
        /// </summary>
        private void CheckViewUdpChanges(dynamic view, string viewId, string viewName)
        {
            if (_udpRuntimeService == null || !_udpRuntimeService.IsInitialized) return;
            if (!UdpDefinitionService.Instance.IsLoaded) return;

            List<UdpDefinitionRuntime> viewDefs;
            try
            {
                viewDefs = UdpDefinitionService.Instance
                    .GetByObjectType("View")
                    .Where(d => d != null)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log($"CheckViewUdpChanges: definition read failed: {ex.Message}");
                return;
            }
            if (viewDefs.Count == 0) return;

            Dictionary<string, string> current;
            try { current = _udpRuntimeService.ReadUdpValues((object)view, "View"); }
            catch (Exception ex)
            {
                Log($"CheckViewUdpChanges: ReadUdpValues failed on '{viewName}': {ex.Message}");
                return;
            }

            if (!_viewUdpSnapshots.TryGetValue(viewId, out var prev))
            {
                // No baseline yet (e.g. tracking started before the UDP runtime
                // initialized): seed silently, enforce from the next pass.
                if (current.Count > 0) _viewUdpSnapshots[viewId] = current;
                return;
            }

            var changedPairs = new List<KeyValuePair<string, string>>();
            foreach (var def in viewDefs)
            {
                current.TryGetValue(def.Name, out var newValue);
                prev.TryGetValue(def.Name, out var oldValue);
                newValue ??= "";
                oldValue ??= "";
                if (newValue == oldValue) continue;

                if (def.IsLocked && !string.IsNullOrEmpty(oldValue))
                {
                    try
                    {
                        _udpRuntimeService.WriteUdpValues(
                            (object)view,
                            new Dictionary<string, string> { [def.Name] = oldValue },
                            "View");
                        Log($"UDP '{def.Name}' is locked on view '{viewName}' - reverted '{newValue}' -> '{oldValue}'");

                        AddinMessageDialog.Show(
                            $"UDP '{def.Name}' is locked by the administrator. The new value ('{newValue}') was rejected; '{oldValue}' was kept.",
                            "UDP Locked",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Warning);
                    }
                    catch (Exception revertEx)
                    {
                        Log($"CheckViewUdpChanges: revert failed for '{def.Name}' on '{viewName}': {revertEx.Message}");
                    }
                    // Snapshot keeps oldValue - the rejected edit never becomes
                    // baseline and never feeds the cascade.
                    continue;
                }

                // Required-UDP clear protection - same contract as the table
                // observer: a required View UDP that had a value must not be
                // cleared; restore + warn. Initial population stays the
                // new-view prompt's job.
                if (def.IsRequired && string.IsNullOrEmpty(newValue) && !string.IsNullOrEmpty(oldValue))
                {
                    try
                    {
                        _udpRuntimeService.WriteUdpValues(
                            (object)view,
                            new Dictionary<string, string> { [def.Name] = oldValue },
                            "View");
                        Log($"UDP '{def.Name}' is required on view '{viewName}' - cleared value restored to '{oldValue}'");

                        AddinMessageDialog.Show(
                            $"UDP '{def.Name}' is required. The value cannot be cleared; '{oldValue}' was restored.",
                            "UDP Required",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Warning);
                    }
                    catch (Exception revertEx)
                    {
                        Log($"CheckViewUdpChanges: required restore failed for '{def.Name}' on '{viewName}': {revertEx.Message}");
                    }
                    // Snapshot keeps oldValue; the rejected clear never feeds
                    // the cascade.
                    continue;
                }

                Log($"UDP '{def.Name}' changed on view '{viewName}': '{oldValue}' -> '{newValue}'");
                changedPairs.Add(new KeyValuePair<string, string>(def.Name, newValue));
            }

            if (changedPairs.Count > 0)
            {
                // Dependency cascade + naming re-validation, table parity. The
                // cascade may write further UDPs; those writes land AFTER the
                // `current` read above and are captured silently by the re-read
                // below, never re-entering the lock check.
                foreach (var kvp in changedPairs)
                {
                    try { _udpRuntimeService.HandleUdpValueChange(view, kvp.Key, kvp.Value, "View"); }
                    catch (Exception ex) { Log($"CheckViewUdpChanges: dependency cascade failed for '{kvp.Key}' on '{viewName}': {ex.Message}"); }
                }
                try { ValidateNamingStandard("View", viewName, view); }
                catch (Exception ex) { Log($"CheckViewUdpChanges: naming re-validation failed for '{viewName}': {ex.Message}"); }

                RefreshViewUdpSnapshot(view, viewId);
            }
            else
            {
                // Only locked reverts (or nothing): keep the baseline as-is so
                // rejected values never become baseline.
                _viewUdpSnapshots[viewId] = prev;
            }
        }

        /// <summary>
        /// Delete erwin's auto-created PK index (XPK/XPKE prefix) from a newly created entity.
        /// Only called during new entity detection, so user-created indexes are never affected.
        /// </summary>
        private void DeleteAutoCreatedPKIndex(dynamic entity, string physicalName)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic keyGroups = null;
                try { keyGroups = modelObjects.Collect(entity, "Key_Group"); }
                catch { return; }
                if (keyGroups == null || keyGroups.Count == 0) return;

                foreach (dynamic kg in keyGroups)
                {
                    if (kg == null) continue;
                    try
                    {
                        string kgName = kg.Name ?? "";
                        string kgType = "";
                        try { kgType = kg.Properties("Key_Group_Type").Value?.ToString() ?? ""; } catch { }

                        if (kgType == "PK" && kgName.StartsWith("XPK", StringComparison.OrdinalIgnoreCase))
                        {
                            int transId = _session.BeginNamedTransaction("DisableAutoCreatedPK");
                            try
                            {
                                kg.Properties("Generate_As_Constraint").Value = false;
                                _session.CommitTransaction(transId);
                                Log($"Auto-created PK index '{kgName}' disabled (Generate_As_Constraint=false) for '{physicalName}'");
                            }
                            catch (Exception ex)
                            {
                                try { _session.RollbackTransaction(transId); } catch { }
                                Log($"Failed to disable auto-created PK index '{kgName}': {ex.Message}");
                            }
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"DeleteAutoCreatedPKIndex item error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DeleteAutoCreatedPKIndex error: {ex.Message}");
            }
        }

        /// <summary>
        /// Add an entity to the main diagram.
        /// All ER_Diagram objects live at the model root level.
        /// Selects the diagram with "Main" in the name, or the one with most shapes.
        /// </summary>
        private void AddEntityToDiagram(dynamic entity, string entityName)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                string entityId = entity.ObjectId?.ToString() ?? "";

                // Find ALL ER_Diagram objects (they all live at model root level)
                dynamic allDiagrams = null;
                try { allDiagrams = modelObjects.Collect(root, "ER_Diagram", -1); } catch { }

                if (allDiagrams == null || allDiagrams.Count == 0)
                {
                    Log($"AddEntityToDiagram: No ER_Diagram found in model");
                    return;
                }

                // Select target diagram: prefer "Main" in name, fallback to most shapes
                dynamic targetDiagram = null;
                string targetDiagramName = "";
                int maxShapeCount = -1;

                foreach (dynamic diag in allDiagrams)
                {
                    if (diag == null) continue;
                    string dName = "";
                    try { dName = diag.Name ?? ""; } catch { }

                    int shapeCount = 0;
                    try { shapeCount = modelObjects.Collect(diag, "ER_Model_Shape").Count; } catch { }

                    bool isMain = dName.IndexOf("Main", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isMain || shapeCount > maxShapeCount)
                    {
                        targetDiagram = diag;
                        targetDiagramName = dName;
                        maxShapeCount = shapeCount;
                        if (isMain) break;
                    }
                }

                if (targetDiagram == null)
                {
                    Log($"AddEntityToDiagram: No suitable diagram found");
                    return;
                }

                // Guard: entity may already have a shape on the target diagram (erwin can
                // auto-add shapes when modelObjects.Add('Entity') is called from CreateTableCopy
                // etc.). Adding a second one stacks two shapes on the same entity, both wired
                // to the same Model_Object_Ref. We then write Anchor_Point with a malformed
                // single-int string ("200" instead of int[] {x,y}), which leaves the duplicate
                // at an undefined position. Suspected cause of the intermittent black-bar
                // rendering artifacts on entity headers reported 2026-04-30. Cheap, safe to skip.
                int existingShapeCount = 0;
                bool alreadyOnDiagram = false;
                try
                {
                    dynamic preExistingShapes = modelObjects.Collect(targetDiagram, "ER_Model_Shape");
                    if (preExistingShapes != null)
                    {
                        foreach (dynamic shape in preExistingShapes)
                        {
                            if (shape == null) continue;
                            existingShapeCount++;
                            try
                            {
                                string refId = shape.Properties("Model_Object_Ref").Value?.ToString() ?? "";
                                if (string.Equals(refId, entityId, StringComparison.Ordinal))
                                {
                                    alreadyOnDiagram = true;
                                }
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Shape ref read error: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { Log($"AddEntityToDiagram: pre-scan error: {ex.Message}"); }

                if (alreadyOnDiagram)
                {
                    Log($"AddEntityToDiagram: '{entityName}' already has a shape on '{targetDiagramName}' (skipped, {existingShapeCount} existing shapes total)");
                    return;
                }

                Log($"AddEntityToDiagram: '{entityName}' will be added to '{targetDiagramName}' (currently {existingShapeCount} shape(s) on diagram)");

                // Check if target diagram belongs to a Subject_Area (non-default SA needs membership)
                dynamic targetParent = root;
                try
                {
                    dynamic subjectAreas = modelObjects.Collect(root, "Subject_Area");
                    foreach (dynamic sa in subjectAreas)
                    {
                        if (sa == null) continue;
                        try
                        {
                            foreach (dynamic child in modelObjects.Collect(sa))
                            {
                                if (child == null) continue;
                                try
                                {
                                    if (child.ClassName == "ER_Diagram" &&
                                        child.ObjectId?.ToString() == targetDiagram.ObjectId?.ToString())
                                    {
                                        targetParent = sa;
                                    }
                                }
                                catch { }
                                if (targetParent != root) break;
                            }
                        }
                        catch { }
                        if (targetParent != root) break;
                    }
                }
                catch { }

                // SA membership (only for non-default Subject Areas)
                if (targetParent != root)
                {
                    try
                    {
                        int saTrans = _session.BeginNamedTransaction("AddSAMembership");
                        try
                        {
                            targetParent.Properties("User_Attached_Objects_Ref").Value = entityId;
                            _session.CommitTransaction(saTrans);
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(saTrans); } catch { }
                            Log($"AddEntityToDiagram: SA membership failed: {ex.Message}");
                        }
                    }
                    catch { }
                }

                // Create ER_Model_Shape on the diagram
                int transId = _session.BeginNamedTransaction("AddEntityToDiagram");
                try
                {
                    dynamic diagChildren = modelObjects.Collect(targetDiagram);
                    dynamic newShape = diagChildren.Add("ER_Model_Shape");

                    // Link shape to entity
                    newShape.Properties("Model_Object_Ref").Value = entityId;

                    // Required for visibility
                    newShape.Properties("Display_Level_Physical").Value = 1;

                    // Position near existing shapes (offset from first shape with valid Anchor_Point)
                    string anchorStr = "200";
                    try
                    {
                        dynamic existingShapes = modelObjects.Collect(targetDiagram, "ER_Model_Shape");
                        foreach (dynamic shape in existingShapes)
                        {
                            if (shape == null) continue;
                            try
                            {
                                dynamic ap = shape.Properties("Anchor_Point").Value;
                                if (ap is int[] ia && ia.Length >= 2)
                                {
                                    anchorStr = (ia[0] + 100).ToString();
                                    break;
                                }
                            }
                            catch { continue; }
                        }
                    }
                    catch { }

                    // NOTE: anchorStr is currently a single int as string (e.g. "200"), not the
                    // int[]{x,y} or "x,y" format erwin's Anchor_Point typically expects. Logged
                    // verbatim so we can inspect what we're actually writing if rendering glitches
                    // recur. Suspected contributor to the intermittent black-bar artifact.
                    string anchorWriteResult = "ok";
                    try { newShape.Properties("Anchor_Point").Value = anchorStr; }
                    catch (Exception ex) { anchorWriteResult = $"err: {ex.Message}"; }

                    _session.CommitTransaction(transId);
                    Log($"AddEntityToDiagram: '{entityName}' added to '{targetDiagramName}' (Anchor_Point='{anchorStr}', write={anchorWriteResult}, total shapes now={existingShapeCount + 1})");
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"AddEntityToDiagram failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"AddEntityToDiagram error: {ex.Message}");
            }
        }

        /// <summary>
        /// Silently apply AUTO_APPLY=true prefix/suffix naming rules that live
        /// on ONE property code and persist the result; returns the (possibly
        /// unchanged) value. Step 1 of <see cref="ValidateNamingStandard"/>
        /// calls this for Physical_Name (the object's primary name); the
        /// Step 3b per-property loop calls it for every OTHER property code
        /// that carries rules - notably View.Name, which has no Physical_Name
        /// accessor at all, so a view's suffix/prefix rules are authored on
        /// "Name". Before this existed those rules were never auto-applied: the
        /// un-suffixed name fell through to validate-only and, because the same
        /// property also carries an IS_REQUIRED rule (the VIEW.Name regexp),
        /// escalated to the Required-input popup instead of silently adding the
        /// suffix (user-reported 2026-06-13).
        /// </summary>
        private string AutoApplyNamingForProperty(string objectType, string propertyCode, string currentValue, dynamic scapiObject, bool isNew)
        {
            if (scapiObject == null) return currentValue;

            // object-box for the engine call so its internal LINQ lambdas stay
            // compile-time resolved (dynamic dispatch breaks them - same reason
            // ValidateNamingStandard boxes scapiObject before calling in).
            object scapiBoxed = scapiObject;
            string afterAuto = NamingValidationEngine.ApplyNamingStandards(
                objectType, currentValue, scapiBoxed, autoOnly: true, propertyCode: propertyCode, isNew: isNew);
            if (string.Equals(afterAuto, currentValue, StringComparison.Ordinal))
                return currentValue;

            // Name_Qualifier is read-only; its writes must target Schema_Ref.
            // For every accessor the addin auto-applies today (Physical_Name,
            // Name) the write code equals the read code.
            string writeAccessor = NamingValidationEngine.WriteAccessorFor(propertyCode);
            int transId = _session.BeginNamedTransaction("ApplyAutoNamingStandard");
            try
            {
                scapiObject.Properties(writeAccessor).Value = afterAuto;
                _session.CommitTransaction(transId);
                Log($"Naming standard auto-applied (silent): '{objectType}.{propertyCode}' '{currentValue}' -> '{afterAuto}'");
                // Modal OK-to-dismiss confirmation (user asked 2026-05-27 that a
                // silent rename cannot be missed). Owner null -> anchors to the
                // active form's screen via AddinMessageDialog's monitor logic.
                AddinMessageDialog.Show(
                    $"{objectType} '{currentValue}' -> '{afterAuto}'",
                    "Naming standard applied",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                // Only the primary-name snapshot is keyed by Physical_Name;
                // other property codes do not participate in _entitySnapshots.
                if (string.Equals(propertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                {
                    string objectId = scapiObject.ObjectId?.ToString() ?? "";
                    if (_entitySnapshots.ContainsKey(objectId))
                        _entitySnapshots[objectId].PhysicalName = afterAuto;
                }
                return afterAuto;
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"ApplyAutoNamingStandard rollback error: {rbEx.Message}"); }
                Log($"Naming standard silent auto-apply failed for '{objectType}.{propertyCode}': {ex.Message}");
                return currentValue;
            }
        }

        /// <summary>
        /// Validate object name against naming standards and log/show results.
        /// </summary>
        /// <summary>
        /// Validate naming standards and apply prefix/suffix.
        /// AUTO_APPLY=true rules are applied silently (no popup).
        /// AUTO_APPLY=false rules that would change the name prompt the user before applying.
        /// Exposed as internal so ValidationCoordinator can reuse it on the
        /// Column-Editor-open transition for the parent entity (Glossary-style
        /// scoped check on the table currently in focus).
        /// </summary>
        internal void ValidateNamingStandard(string objectType, string physicalName, dynamic scapiObject = null, bool isNew = false, IDictionary<string, string> baselineOverride = null)
        {
            if (!NamingStandardService.Instance.IsLoaded) return;

            // Creation-gesture override (2026-06-01): see
            // CreationGestureProbe XML doc. The Required-input re-run
            // loop further down this method calls into the engine with
            // the caller's original isNew, so by overriding once here
            // every subsequent ApplyNamingStandards / ValidateObjectName
            // call (Steps 1, 2, 3, 3b, and the inner fresh re-eval at
            // line 1841) sees the corrected flag.
            if (!isNew && CreationGestureProbe != null)
            {
                try
                {
                    if (CreationGestureProbe(physicalName))
                    {
                        Log($"ValidateNamingStandard: '{physicalName}' is in active creation gesture - overriding isNew=False to True so Update-only rules stay filtered (e.g. rule#22 _PRM on TableClass=Parametre)");
                        isNew = true;
                    }
                }
                catch (Exception ex) { Log($"ValidateNamingStandard: CreationGestureProbe threw {ex.GetType().Name}: {ex.Message} - keeping caller isNew={isNew}"); }
            }

            // Capture baseline state at method entry. Step 1 auto-apply may
            // mutate the snapshot mid-method, but a Required-popup Cancel
            // (handled at the end) needs to revert all the way back to the
            // user's pre-edit values - not to the auto-applied intermediate.
            // For an isNew entity the baseline is effectively empty (the
            // entity will be deleted instead of reverted), so this map is
            // only consulted on the !isNew branch.
            //
            // baselineOverride takes precedence over _entitySnapshots so an
            // editor-open caller (ValidationCoordinator.DiffWatchedPropertiesAndFire)
            // can supply the live pre-edit values directly - critical for
            // auto-generated / user-created entities that never made it into
            // _entitySnapshots (the legacy periodic walk that filled that
            // map was removed in Phase-2D, 2026-05-06).
            string baselinePhysicalName = physicalName ?? "";
            Dictionary<string, string> baselineWatched = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string snapshotId = "";
            if (scapiObject != null && !isNew)
            {
                try { snapshotId = scapiObject.ObjectId?.ToString() ?? ""; }
                catch (Exception ex) { Log($"ValidateNamingStandard: ObjectId read failed for '{physicalName}': {ex.Message}"); }

                if (baselineOverride != null && baselineOverride.Count > 0)
                {
                    foreach (var kvp in baselineOverride)
                        baselineWatched[kvp.Key] = kvp.Value ?? "";
                    if (baselineOverride.TryGetValue("Physical_Name", out var pn) && !string.IsNullOrEmpty(pn))
                        baselinePhysicalName = pn;
                }
                else if (!string.IsNullOrEmpty(snapshotId) && _entitySnapshots.TryGetValue(snapshotId, out var baselineSnap))
                {
                    baselinePhysicalName = baselineSnap.PhysicalName ?? physicalName ?? "";
                    if (baselineSnap.WatchedProperties != null)
                    {
                        foreach (var kvp in baselineSnap.WatchedProperties)
                            baselineWatched[kvp.Key] = kvp.Value ?? "";
                    }
                }
            }

            // scapiObject MUST be passed so UDP-conditional rules (DEPENDS_ON_UDP_ID) can read
            // the live UDP value off the entity. Without it, IsRuleApplicable returns false for
            // any UDP-conditional rule and the prefix is silently dropped.
            // Cast to object to keep the call compile-time resolved (otherwise dynamic dispatch
            // breaks the LINQ lambdas inside the engine — see CheckEntityKeyGroups for prior art).
            object scapiBoxed = scapiObject;

            // Step 1: silently apply AUTO_APPLY=true rules to the object's
            // primary name (Physical_Name). Property codes other than
            // Physical_Name (e.g. View.Name, which has no Physical_Name
            // accessor) are auto-applied per-property in the Step 3b loop.
            physicalName = AutoApplyNamingForProperty(objectType, "Physical_Name", physicalName, scapiObject, isNew);

            // Step 2: ask user about AUTO_APPLY=false rules that would still change the name
            if (scapiBoxed != null)
            {
                string afterAll = NamingValidationEngine.ApplyNamingStandards(objectType, physicalName, scapiBoxed, autoOnly: false, isNew: isNew);
                if (!string.Equals(afterAll, physicalName, StringComparison.Ordinal))
                {
                    var answer = AddinMessageDialog.Show(
                        $"Naming standard suggests changes for '{physicalName}':\n\n" +
                        $"'{physicalName}' -> '{afterAll}'\n\n" +
                        $"Apply?",
                        "Naming Standard",
                        System.Windows.Forms.MessageBoxButtons.YesNo,
                        System.Windows.Forms.MessageBoxIcon.Question);

                    if (answer == System.Windows.Forms.DialogResult.Yes)
                    {
                        int transId = _session.BeginNamedTransaction("ApplyManualNamingStandard");
                        try
                        {
                            scapiObject.Properties("Physical_Name").Value = afterAll;
                            _session.CommitTransaction(transId);
                            Log($"Naming standard applied (user confirmed): '{physicalName}' -> '{afterAll}'");

                            string objectId = scapiObject.ObjectId?.ToString() ?? "";
                            if (_entitySnapshots.ContainsKey(objectId))
                                _entitySnapshots[objectId].PhysicalName = afterAll;

                            physicalName = afterAll;
                            return; // User-applied — re-validate on next tick
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"ApplyManualNamingStandard rollback error: {rbEx.Message}"); }
                            Log($"Naming standard manual apply failed: {ex.Message}");
                        }
                    }
                }
            }

            // Step 3: Validate remaining issues (warning popup for un-fixable rules e.g. regex/length)
            string nameToValidate = physicalName;
            if (scapiObject != null)
            {
                try
                {
                    string currentPhys = scapiObject.Properties("Physical_Name").Value?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(currentPhys) && !currentPhys.StartsWith("%"))
                        nameToValidate = currentPhys;
                }
                catch { }
            }

            var results = NamingValidationEngine.ValidateObjectName(objectType, nameToValidate, scapiBoxed, isNew: isNew);
            var failures = results.Where(r => !r.IsValid).ToList();

            // Step 3b (2026-05-16): the admin can author rules on any
            // PROPERTY_DEF, not just Physical_Name. Iterate every property
            // code that has rules for this object type, read the live
            // value via direct SCAPI access (admin's PROPERTY_CODE is now
            // the exact erwin accessor name, verified empirically across
            // SQL Server / Oracle / DB2 z/OS / PostgreSQL on 2026-05-16),
            // and accumulate failures.
            if (scapiObject != null)
            {
                foreach (var propertyCode in NamingStandardService.Instance.GetPropertyCodes(objectType))
                {
                    if (string.Equals(propertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                        continue; // already covered by the Physical_Name run above

                    string propValue;
                    try
                    {
                        propValue = scapiObject.Properties(propertyCode)?.Value?.ToString() ?? "";
                    }
                    catch (Exception ex)
                    {
                        // SCAPI says "Entity class does not use a property
                        // of <X> type or the property failed to satisfy a
                        // property collection filter conditions". On r10.10
                        // this happens for a brand-new entity that has not
                        // yet been assigned the property (e.g. a freshly
                        // dropped table has no Name_Qualifier instance
                        // until the user sets Owner in the Database Object
                        // Properties dialog). This is the EXACT state a
                        // "Length > 0" rule is meant to catch - treat the
                        // unset property as an empty string and run the
                        // validation so the popup fires. If admin's
                        // PROPERTY_CODE is just wrong, the empty value
                        // makes the rule fire constantly across every
                        // entity, which is the loudest possible signal
                        // for admin to notice and fix the code.
                        propValue = "";
                        Log($"Naming standard: SCAPI did not surface '{objectType}.{propertyCode}' on this entity (treating as empty): {ex.Message}");
                    }

                    // Diagnostic (2026-05-25): help triage "Required popup
                    // didn't fire for property X on new entity Y" reports
                    // by surfacing the live value the engine sees.
                    Log($"NamingValidate: '{objectType}.{propertyCode}' on '{physicalName}' liveValue='{propValue}' isNew={isNew}");

                    // Auto-apply AUTO_APPLY=true prefix/suffix rules that live
                    // on THIS property code before validating (Steps 1/2 only
                    // cover Physical_Name). Without this a VIEW.Name '_VVV'
                    // suffix is never added and the un-suffixed name escalates
                    // to the Required-input popup because Name also carries a
                    // required regexp rule (user-reported 2026-06-13).
                    string autoApplied = AutoApplyNamingForProperty(objectType, propertyCode, propValue, scapiObject, isNew);
                    if (!string.Equals(autoApplied, propValue, StringComparison.Ordinal))
                        propValue = autoApplied;

                    var extraResults = NamingValidationEngine.ValidateObjectName(
                        objectType, propValue, scapiBoxed, propertyCode, isNew: isNew);
                    failures.AddRange(extraResults.Where(r => !r.IsValid));
                }
            }

            // Required-input pass (2026-05-17 C3 follow-up, updated 2026-05-20):
            // Req=true rules produce "Required" violations when the property
            // is empty - per spec the user is FORCED to provide a value.
            // The dialog is opened in CREATE mode for new entities (Cancel
            // deletes the entity) or UPDATE mode for existing edits (Cancel
            // reverts the property to its pre-edit baseline). Both Cancel
            // branches break the loop so any subsequent Required failures
            // are suppressed - per the 2026-05-20 contract the user has
            // explicitly abandoned the edit/creation, so chaining further
            // popups on the same already-discarded object would be noise.
            // Done in two phases (collect-then-prompt) so each dialog runs
            // cleanly against the previous transaction's commit.
            bool requiredCancelHandled = false;
            if (scapiObject != null && failures.Count > 0)
            {
                // Pattern violations (Length / Regexp / non-AutoApply Prefix /
                // non-AutoApply Suffix) on a property that ALSO carries an
                // IS_REQUIRED=true rule must be enforced through the modal
                // input popup, not the OK-and-forget warning. Admin signalled
                // "user must fill this field" by setting Required; "ddd"
                // satisfying Required but failing Length>10 has to keep
                // prompting until the value clears all rules. We keep one
                // entry per PropertyCode so a property with multiple failures
                // gets a single chain of popups (Required first if present,
                // then the re-prompt loop drains the rest).
                var requiredProps = NamingStandardService.Instance.GetRequiredPropertyCodes(objectType);
                var requiredFailures = failures
                    .Where(f => f.Rule != null
                                && !string.IsNullOrEmpty(f.Rule.PropertyCode)
                                && (string.Equals(f.RuleName, "Required", StringComparison.Ordinal)
                                    || (requiredProps != null && requiredProps.Contains(f.Rule.PropertyCode))))
                    .GroupBy(f => f.Rule.PropertyCode, StringComparer.OrdinalIgnoreCase)
                    // Prefer a Required entry as the seed; otherwise take the first
                    // failure (typically Length / Regexp) - the re-prompt loop will
                    // surface the remaining ones with the right error message anyway.
                    .Select(g => g.FirstOrDefault(x => string.Equals(x.RuleName, "Required", StringComparison.Ordinal)) ?? g.First())
                    .ToList();

                // Session-level dismissal pre-pass: drop any Required failure
                // the user already cancelled on this exact (objectId, property)
                // pair earlier in the session AND whose current value still
                // matches the value at dismiss-time. If the value has changed
                // since dismiss, the user has clearly engaged with the field
                // again - we clear the flag and let the popup re-surface.
                // Drops both the popup and the consolidated warning entry so
                // a known-empty Definition or Owner on an existing entity
                // does not nag every tick after the user explicitly said
                // "later" - but the moment they touch it again, the rule
                // applies again.
                if (!string.IsNullOrEmpty(snapshotId))
                {
                    var toRemove = new List<NamingValidationResult>();
                    foreach (var rf in requiredFailures)
                    {
                        string key = $"{snapshotId}|{rf.Rule.PropertyCode}";
                        if (!_dismissedRequiredKeys.TryGetValue(key, out var dismissedValue))
                            continue;

                        string liveValue = "";
                        try { liveValue = scapiObject?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                        catch { liveValue = ""; }

                        if (string.Equals(liveValue, dismissedValue, StringComparison.Ordinal))
                        {
                            Log($"Required field popup suppressed (session-dismissed): {objectType}.{rf.Rule.PropertyCode} on '{physicalName}'");
                            toRemove.Add(rf);
                        }
                        else
                        {
                            _dismissedRequiredKeys.Remove(key);
                            Log($"Required field dismiss cleared (value changed '{dismissedValue}' -> '{liveValue}'): {objectType}.{rf.Rule.PropertyCode} on '{physicalName}'");
                        }
                    }
                    foreach (var dismissed in toRemove)
                    {
                        requiredFailures.Remove(dismissed);
                        failures.Remove(dismissed);
                    }
                }

                foreach (var rf in requiredFailures)
                {
                    // Show the OBJECT NAME (the table) + a friendly property label,
                    // e.g. "VpDBMS_LIBRARY (Comment)" - the raw "Table.Definition"
                    // was meaningless to the user (2026-06-06).
                    string fieldLabel = $"{physicalName} ({NamingValidationEngine.FriendlyPropertyLabel(rf.Rule.PropertyCode)})";
                    var cancelMode = isNew ? Forms.RequiredOperationMode.Create : Forms.RequiredOperationMode.Update;

                    // Pre-fill the dialog with the property's current value -
                    // a pure Required violation reads "" (the empty state), but a
                    // Length / Regexp violation on a property that already has
                    // a partial value (e.g. Description="ddd" violating
                    // Length>10) lets the user extend instead of retyping
                    // from scratch.
                    string seedValue = "";
                    try { seedValue = scapiObject?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                    catch { seedValue = ""; }

                    // Cancel-with-invalid-revert re-prompt loop (2026-05-24
                    // user rule: "10 dan küçük girmeye izin vermemeli, boş
                    // bırakmaya da"). On existing entities the user could
                    // previously escape every Required popup by clicking
                    // Revert when the baseline was itself invalid - one
                    // dismiss persisted forever, every subsequent tick
                    // suppressed the popup. We now re-show the popup
                    // IMMEDIATELY while the post-revert value still
                    // violates the rule. User must provide a valid value
                    // (or delete the entity from the diagram between
                    // popups). New entities still escape via Discard.
                    // Property-aware choice list (2026-05-25 user request).
                    // For list-typed required properties (currently Owner /
                    // Name_Qualifier / Schema_Ref -> existing Schema objects),
                    // resolve the set of valid values from the model so the
                    // dialog renders a locked ComboBox instead of a free
                    // text input. Without this, the user could type an
                    // arbitrary string and erwin would later reject the
                    // SCAPI write (SCVT_OBJID column expects an existing
                    // Schema, not a name). Returns null for properties
                    // without a fixed choice list - dialog falls back to
                    // TextBox.
                    var fieldChoices = ResolveRequiredFieldChoices(rf.Rule.PropertyCode);

                    string currentMessage = rf.ErrorMessage;
                    string currentSeed = seedValue;
                    System.Windows.Forms.DialogResult rc;
                    string typed;
                    while (true)
                    {
                        rc = RequiredFieldDialog.Show(
                            title: "Required field",
                            message: currentMessage,
                            fieldLabel: fieldLabel,
                            out typed,
                            owner: null,
                            initialValue: currentSeed,
                            mode: cancelMode,
                            objectKind: objectType,
                            choices: fieldChoices);

                        if (rc == System.Windows.Forms.DialogResult.OK && !string.IsNullOrEmpty(typed))
                            break; // user typed a value - fall through to write+re-prompt logic below

                        Log($"Required field dialog cancelled: {fieldLabel} (mode={cancelMode})");
                        if (isNew)
                        {
                            requiredCancelHandled = DiscardNewObjectForRequiredCancel(objectType, scapiObject, physicalName);
                            break; // new object is either gone or revert failed; either way exit this rf
                        }

                        string baseline = string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase)
                            ? baselinePhysicalName
                            : (baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "");
                        requiredCancelHandled = TryRevertEntityProperty(scapiObject, snapshotId, physicalName, rf.Rule.PropertyCode, baseline);

                        string postRevertValue = "";
                        try { postRevertValue = scapiObject?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                        catch { postRevertValue = ""; }
                        bool revertedIsValid = RevalidatePropertyAfterRevert(scapiBoxed, objectType, rf.Rule.PropertyCode, postRevertValue, failures);

                        if (revertedIsValid)
                        {
                            // Reverted value passes every rule - this is the
                            // intended dismiss path: store the value and exit.
                            if (!string.IsNullOrEmpty(snapshotId))
                            {
                                _dismissedRequiredKeys[$"{snapshotId}|{rf.Rule.PropertyCode}"] = postRevertValue;
                                Log($"Required field dismissed for session: {snapshotId}|{rf.Rule.PropertyCode} value='{postRevertValue}'");
                            }
                            break;
                        }

                        // Reverted value still violates - pull the freshest
                        // failure message (Required vs Length etc.) and loop
                        // back to the popup. NO dismiss flag is stored, so
                        // even if revert wrote nothing the user is forced to
                        // address the violation right now.
                        var freshFailure = failures.FirstOrDefault(f => f.Rule != null
                            && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                        currentMessage = freshFailure?.ErrorMessage ?? currentMessage;
                        currentSeed = postRevertValue;
                        Log($"Required field re-prompt after Cancel (post-revert still invalid): {fieldLabel}");
                    }

                    if (rc != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(typed))
                    {
                        if (requiredCancelHandled) break;
                        // Delete/revert failed - fall through to next iteration so
                        // the remaining Required failures still get a chance. The
                        // consolidated warning at the end will surface whichever
                        // ones the user didn't address.
                        continue;
                    }

                    int transId = _session.BeginNamedTransaction("RequiredFieldFill");
                    string writeAccessor = NamingValidationEngine.WriteAccessorFor(rf.Rule.PropertyCode);
                    try
                    {
                        // Read-vs-write accessor split: e.g. Name_Qualifier reads
                        // are derived from Schema_Ref but writes have to target
                        // Schema_Ref directly. See WriteAccessorFor for why.
                        scapiObject.Properties(writeAccessor).Value = typed;
                        _session.CommitTransaction(transId);
                        Log($"Required field filled by user: {fieldLabel} = '{typed}'"
                            + (writeAccessor != rf.Rule.PropertyCode ? $" (write accessor='{writeAccessor}')" : ""));
                        failures.Remove(rf);

                        // Clear any stale session dismissal for this property
                        // - the user just provided a valid value so the next
                        // time it goes empty again the popup SHOULD reappear.
                        if (!string.IsNullOrEmpty(snapshotId))
                            _dismissedRequiredKeys.Remove($"{snapshotId}|{rf.Rule.PropertyCode}");

                        // Refresh the WatchedProperties snapshot for this
                        // entity so the next DiagramHeartbeat tick doesn't
                        // see the value we just wrote as a phantom drift
                        // (verified bug 2026-05-17: log line "Watched
                        // property changed ... '' -> 'dbo' - re-running
                        // naming check" fired 1.2 s after the user
                        // successfully filled the popup). Read via the rule's
                        // read accessor since Schema_Ref writes materialise
                        // back through Name_Qualifier on the next read.
                        try
                        {
                            string objId = scapiObject.ObjectId?.ToString() ?? "";
                            if (!string.IsNullOrEmpty(objId)
                                && _entitySnapshots.TryGetValue(objId, out var snap))
                            {
                                string readBack;
                                try { readBack = scapiObject.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? typed; }
                                catch { readBack = typed; }
                                snap.WatchedProperties[rf.Rule.PropertyCode] = readBack;
                            }
                        }
                        catch (Exception snapEx)
                        {
                            Log($"Required field watched-snapshot refresh failed: {snapEx.Message}");
                        }

                        // Keep snapshot + local state in sync when the user
                        // just edited Physical_Name so the next monitor tick
                        // does not re-detect this as a rename.
                        if (string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase))
                        {
                            nameToValidate = typed;
                            physicalName = typed;
                            try
                            {
                                string objectId = scapiObject.ObjectId?.ToString() ?? "";
                                if (_entitySnapshots.ContainsKey(objectId))
                                    _entitySnapshots[objectId].PhysicalName = typed;
                            }
                            catch (Exception snapEx) { Log($"Required field snapshot update failed: {snapEx.Message}"); }
                        }

                        // Pattern-rule re-prompt loop (2026-05-24): the
                        // value the user just typed satisfied the Required
                        // gate, but the SAME property might also have
                        // Length / Regexp / Prefix-not-AutoApply rules
                        // attached (admin layered "must be non-empty" on
                        // top of "must be >= 10 chars"). The legacy flow
                        // showed those as a consolidated WARNING popup
                        // that the user dismissed with OK, leaving the
                        // bad-but-non-empty value behind. Per user
                        // feedback, the popup chain should keep pushing
                        // until the value clears EVERY rule on the
                        // property (or the user cancels). We re-read the
                        // live value, re-validate against just this
                        // PropertyCode's ruleset, and re-open the same
                        // input dialog with the next violation's message
                        // until the property is clean.
                        string currentTyped = typed;
                        while (true)
                        {
                            // Read what is actually on the entity now -
                            // SCAPI may have transformed the value
                            // (Schema_Ref objId, Turkish-I normalize, etc).
                            string liveValue;
                            try { liveValue = scapiObject.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                            catch { liveValue = currentTyped; }

                            var freshResults = NamingValidationEngine.ValidateObjectName(
                                objectType, liveValue, scapiBoxed, rf.Rule.PropertyCode, isNew: isNew);
                            var freshFailure = freshResults?.FirstOrDefault(r => !r.IsValid);
                            if (freshFailure == null)
                            {
                                // Property clears every rule now - drop
                                // any non-Required failures for this
                                // PropertyCode from the consolidated batch
                                // so they are not shown twice.
                                failures.RemoveAll(f => f.Rule != null
                                    && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                break;
                            }

                            Log($"Required field re-prompt for {fieldLabel}: '{liveValue}' still violates rule#{freshFailure.Rule?.Id} ({freshFailure.RuleName})");
                            var rc2 = RequiredFieldDialog.Show(
                                title: "Required field",
                                message: freshFailure.ErrorMessage,
                                fieldLabel: fieldLabel,
                                out string typed2,
                                owner: null,
                                initialValue: liveValue,
                                mode: cancelMode,
                                objectKind: objectType,
                                choices: fieldChoices);

                            if (rc2 != System.Windows.Forms.DialogResult.OK || string.IsNullOrEmpty(typed2))
                            {
                                Log($"Required field re-prompt cancelled for {fieldLabel} (mode={cancelMode})");
                                if (isNew)
                                {
                                    requiredCancelHandled = DiscardNewObjectForRequiredCancel(objectType, scapiObject, physicalName);
                                }
                                else
                                {
                                    string baseline = string.Equals(rf.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase)
                                        ? baselinePhysicalName
                                        : (baselineWatched.TryGetValue(rf.Rule.PropertyCode, out var bv) ? bv : "");
                                    requiredCancelHandled = TryRevertEntityProperty(scapiObject, snapshotId, physicalName, rf.Rule.PropertyCode, baseline);
                                    string postRevertValue2 = "";
                                    try { postRevertValue2 = scapiObject?.Properties(rf.Rule.PropertyCode)?.Value?.ToString() ?? ""; }
                                    catch { postRevertValue2 = ""; }
                                    bool revertedIsValid2 = RevalidatePropertyAfterRevert(scapiBoxed, objectType, rf.Rule.PropertyCode, postRevertValue2, failures);
                                    if (!string.IsNullOrEmpty(snapshotId) && revertedIsValid2)
                                    {
                                        _dismissedRequiredKeys[$"{snapshotId}|{rf.Rule.PropertyCode}"] = postRevertValue2;
                                        Log($"Required field dismissed for session: {snapshotId}|{rf.Rule.PropertyCode} value='{postRevertValue2}'");
                                    }
                                }
                                // Remove pending non-Required failures
                                // for this property either way so the
                                // consolidated popup does not re-surface
                                // a violation the user already explicitly
                                // declined.
                                failures.RemoveAll(f => f.Rule != null
                                    && string.Equals(f.Rule.PropertyCode, rf.Rule.PropertyCode, StringComparison.OrdinalIgnoreCase));
                                break;
                            }

                            int loopTransId = _session.BeginNamedTransaction("RequiredFieldFillRepeat");
                            try
                            {
                                scapiObject.Properties(writeAccessor).Value = typed2;
                                _session.CommitTransaction(loopTransId);
                                Log($"Required field re-filled by user: {fieldLabel} = '{typed2}'");
                                currentTyped = typed2;
                            }
                            catch (Exception loopEx)
                            {
                                try { _session.RollbackTransaction(loopTransId); } catch { }
                                Log($"Required field re-write failed for {fieldLabel}: {loopEx.Message}");
                                AddinMessageDialog.Show(
                                    $"Failed to write '{typed2}' to {fieldLabel}.\n\nSCAPI error:\n{loopEx.Message}",
                                    "Required field write failed",
                                    System.Windows.Forms.MessageBoxButtons.OK,
                                    System.Windows.Forms.MessageBoxIcon.Error);
                                break;
                            }
                        }

                        if (requiredCancelHandled) break;
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"RequiredFieldFill rollback error: {rbEx.Message}"); }
                        Log($"Required field write failed for {fieldLabel}: {ex.Message}");

                        // SCAPI rejected the value - surface it to the user so they
                        // know why the field still appears empty. Most common case
                        // (2026-05-17): Schema_Ref is an SCVT_OBJID column, so a
                        // schema name that doesn't already exist as a Schema
                        // object cannot be assigned. The error message from erwin
                        // mentions the SCAPI type which is enough to give the
                        // admin a starting point.
                        bool isSchemaRef = string.Equals(writeAccessor, "Schema_Ref", StringComparison.OrdinalIgnoreCase);
                        string userMessage = isSchemaRef
                            ? $"Cannot set '{typed}' as Owner. erwin's Schema_Ref expects an existing Schema object, " +
                              $"so the value must match an Owner already defined in this model. " +
                              $"Open Database Object Properties to create the Schema first.\n\n" +
                              $"SCAPI error:\n{ex.Message}"
                            : $"Failed to write '{typed}' to {fieldLabel}.\n\nSCAPI error:\n{ex.Message}";
                        AddinMessageDialog.Show(
                            userMessage,
                            "Required field write failed",
                            System.Windows.Forms.MessageBoxButtons.OK,
                            System.Windows.Forms.MessageBoxIcon.Error);
                    }
                }
            }

            // Required-Cancel short-circuit: the user explicitly discarded
            // the new entity (Create) or reverted the edit (Update). Any
            // remaining failures belong to an object that no longer exists
            // or is back to a state we already accepted, so the consolidated
            // warning popup and the PLEASE_CHANGE_IT rename below would
            // both be confusing noise. Bail before reaching them.
            if (requiredCancelHandled)
            {
                Log($"ValidateNamingStandard: Cancel-handled, suppressing remaining warnings for '{physicalName}'");
                return;
            }

            if (failures.Count > 0)
            {
                foreach (var f in failures)
                {
                    // Diagnostic suffix: when the failing check is a Regexp
                    // rule, also log the EXACT stored pattern (truncated for
                    // log readability). Without this an admin reading the
                    // log cannot tell whether the regex stored in DB is
                    // what they typed in the admin UI - we hit exactly that
                    // confusion on 2026-05-15 (admin typed `^.{0,3}$` but a
                    // different value was rejecting every name).
                    string diag = "";
                    if (f.Rule != null && string.Equals(f.RuleName, "Regexp", StringComparison.Ordinal))
                    {
                        string p = f.Rule.RegexpPattern ?? "";
                        if (p.Length > 80) p = p.Substring(0, 77) + "...";
                        diag = $" [pattern(len={(f.Rule.RegexpPattern ?? "").Length})='{p}']";
                    }
                    Log($"Naming standard violation ({f.RuleName}): '{nameToValidate}' — {f.ErrorMessage}{diag}");
                }

                // Phase-2H (2026-05-13): if ANY failing rule is AUTO_APPLY=true,
                // rewrite the object name to "PLEASE_CHANGE_IT" instead of
                // showing the warning popup. Same UX pattern the glossary path
                // already uses for failing columns: the auto-applied rule
                // already gave the user one transformation attempt (Step 1
                // above); if the result still violates the rule, the rule's
                // intent is "I will fix this for you" and the strongest
                // fallback that fits the regex-cant-fix-it case is to force
                // the user to retype the name.
                //
                // Only renames when we have a scapiObject to write back to
                // (the live-fire paths always pass one; static external callers
                // that pass null fall through to the popup as before).
                // Only Physical_Name failures from a Prefix/Suffix rule can
                // trigger the PLEASE_CHANGE_IT auto-rename: the placeholder
                // makes sense as a name override but is meaningless for
                // other properties (Owner, Definition, ...) and for
                // validate-only rule kinds (Required/Length/Regexp) which
                // never had an "I will fix this for you" promise to break.
                // The atomic-rule loader already forces AutoApply=false on
                // non-Prefix/Suffix kinds, so checking AutoApply alone would
                // be sufficient - the explicit RuleType test below mirrors
                // the spec for clarity and survives any future loader bug.
                bool anyAutoApplyFailing = scapiObject != null
                    && failures.Any(f => f.Rule != null
                                         && f.Rule.AutoApply
                                         && (f.Rule.RuleType == NamingRuleKind.Prefix
                                             || f.Rule.RuleType == NamingRuleKind.Suffix)
                                         && string.Equals(f.Rule.PropertyCode, "Physical_Name", StringComparison.OrdinalIgnoreCase));

                // Phase-2H popup-then-rename ordering (2026-05-13): show the
                // violation message BEFORE rewriting the name, so the user
                // first reads "this name is invalid" and then sees the
                // PLEASE_CHANGE_IT placeholder land in the table after they
                // dismiss the popup. The reversed order (rename then popup)
                // confused users because the popup talked about a name they
                // could no longer see in the diagram.
                string messages = string.Join("\n", failures.Select(f => $"• {f.ErrorMessage}"));
                string popupBody = anyAutoApplyFailing
                    ? $"Naming standard violation(s) for '{nameToValidate}':\n\n{messages}\n\n" +
                      $"The name will be reset to 'PLEASE_CHANGE_IT'. Please choose a name that matches the standard."
                    : $"Naming standard violation(s) for '{nameToValidate}':\n\n{messages}";
                AddinMessageDialog.Show(
                    popupBody,
                    "Naming Standard",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);

                if (anyAutoApplyFailing)
                {
                    string placeholder = "PLEASE_CHANGE_IT";
                    int transId = _session.BeginNamedTransaction("ApplyPleaseChangeItOnAutoFail");
                    try
                    {
                        scapiObject.Properties("Physical_Name").Value = placeholder;
                        try { scapiObject.Properties("Name").Value = placeholder; }
                        catch (Exception nameEx) { Log($"PLEASE_CHANGE_IT: Failed to set Name on '{nameToValidate}': {nameEx.Message}"); }
                        _session.CommitTransaction(transId);
                        Log($"Naming standard auto-fail fallback: '{nameToValidate}' -> '{placeholder}' (popup acknowledged, name reset)");

                        // Update entity snapshot so the next monitor tick
                        // doesn't see this as a fresh rename and loop back
                        // through ValidateNamingStandard.
                        string objectId = "";
                        try { objectId = scapiObject.ObjectId?.ToString() ?? ""; }
                        catch (Exception idEx) { Log($"PLEASE_CHANGE_IT: ObjectId read failed: {idEx.Message}"); }
                        if (!string.IsNullOrEmpty(objectId) && _entitySnapshots.ContainsKey(objectId))
                        {
                            _entitySnapshots[objectId].PhysicalName = placeholder;
                        }
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rbEx) { Log($"ApplyPleaseChangeItOnAutoFail rollback error: {rbEx.Message}"); }
                        Log($"PLEASE_CHANGE_IT rename failed for '{nameToValidate}': {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Check if any UDP values changed on an existing entity and trigger dependency evaluation.
        /// </summary>
        private void CheckForUdpValueChanges(dynamic entity, string objectId, string physicalName)
        {
            try
            {
                if (!_entitySnapshots.ContainsKey(objectId)) return;
                var snapshot = _entitySnapshots[objectId];

                // Read current UDP values
                var currentValues = _udpRuntimeService.ReadUdpValues((object)entity);
                if (currentValues.Count == 0) return;

                // Compare with snapshot — only process changes
                bool anyChanged = false;
                foreach (var kvp in currentValues)
                {
                    string oldValue = "";
                    snapshot.UdpValues.TryGetValue(kvp.Key, out oldValue);
                    oldValue = oldValue ?? "";

                    if (kvp.Value != oldValue)
                    {
                        // Locked-UDP enforcement: admin marks a UDP as locked via
                        // MC_UDP_DEFINITION.IS_LOCKED. The first non-empty assignment
                        // is allowed (so wizards and ApplyDefaults can seed the
                        // field) but every subsequent edit is reverted to the
                        // previous value. Initial set is detected by an empty
                        // oldValue - the snapshot for new entities is populated
                        // immediately after ApplyDefaults so the next tick already
                        // sees the seeded value as "old".
                        //
                        // User-only intent (confirmed 2026-05-13): Lock must NOT
                        // block system writes (dependency cascade via
                        // HandleUdpValueChange, ApplyDefaults, glossary auto-fill).
                        // This semantic is currently preserved by timing rather
                        // than an explicit "system write" flag:
                        //
                        //   1. currentValues is read ONCE per tick BEFORE the
                        //      HandleUdpValueChange / AddPredefinedColumnsForUdp
                        //      calls inside this foreach. Any UDP that dependency
                        //      logic writes downstream lands in the model AFTER
                        //      currentValues was read, so this foreach iteration
                        //      will not observe it as a diff against snapshot.
                        //   2. The "if (anyChanged) -> re-read updatedValues"
                        //      block below the foreach captures those dependency
                        //      writes into the snapshot SILENTLY, never routing
                        //      them through this lock check.
                        //   3. ApplyDefaults only writes when current value is
                        //      empty (see UdpRuntimeService.ApplyDefaults), so
                        //      it can only ever execute the "initial set"
                        //      branch and oldValue stays "" -> not blocked.
                        //
                        // DO NOT reorder this foreach with the re-read block, and
                        // DO NOT call HandleUdpValueChange BEFORE the foreach is
                        // built. Doing either would route dependency-driven writes
                        // through this lock and revert them, breaking the
                        // user-only intent. If a future refactor needs that
                        // ordering, switch to an explicit "system write"
                        // suppression flag on WriteUdpValues instead.
                        var udpDef = UdpDefinitionService.Instance.IsLoaded
                            ? UdpDefinitionService.Instance.GetByName("Table", kvp.Key)
                            : null;
                        if (udpDef != null && udpDef.IsLocked && !string.IsNullOrEmpty(oldValue))
                        {
                            try
                            {
                                _udpRuntimeService.WriteUdpValues(
                                    entity,
                                    new Dictionary<string, string> { [kvp.Key] = oldValue },
                                    "Table");
                                Log($"UDP '{kvp.Key}' is locked on '{physicalName}' - reverted '{kvp.Value}' -> '{oldValue}'");

                                AddinMessageDialog.Show(
                                    $"UDP '{kvp.Key}' is locked by the administrator. The new value ('{kvp.Value}') was rejected; '{oldValue}' was kept.",
                                    "UDP Locked",
                                    System.Windows.Forms.MessageBoxButtons.OK,
                                    System.Windows.Forms.MessageBoxIcon.Warning);
                            }
                            catch (Exception revertEx)
                            {
                                Log($"UDP lock revert failed for '{kvp.Key}' on '{physicalName}': {revertEx.Message}");
                            }
                            // Snapshot stays at oldValue; do NOT run dependency
                            // evaluation, predefined-column hooks, or naming
                            // validation for a change we just rejected.
                            continue;
                        }

                        // Required-UDP clear protection (2026-06-12): an admin
                        // IS_REQUIRED UDP must never lose its value once set -
                        // clearing it (e.g. picking the blank row of a List UDP)
                        // is rejected and the previous value restored, same
                        // warn-and-revert contract as the lock above. Initial
                        // population stays the create-time prompt's job (empty
                        // oldValue never triggers this).
                        if (udpDef != null && udpDef.IsRequired
                            && string.IsNullOrEmpty(kvp.Value) && !string.IsNullOrEmpty(oldValue))
                        {
                            try
                            {
                                _udpRuntimeService.WriteUdpValues(
                                    entity,
                                    new Dictionary<string, string> { [kvp.Key] = oldValue },
                                    "Table");
                                Log($"UDP '{kvp.Key}' is required on '{physicalName}' - cleared value restored to '{oldValue}'");

                                AddinMessageDialog.Show(
                                    $"UDP '{kvp.Key}' is required. The value cannot be cleared; '{oldValue}' was restored.",
                                    "UDP Required",
                                    System.Windows.Forms.MessageBoxButtons.OK,
                                    System.Windows.Forms.MessageBoxIcon.Warning);
                            }
                            catch (Exception revertEx)
                            {
                                Log($"Required-UDP restore failed for '{kvp.Key}' on '{physicalName}': {revertEx.Message}");
                            }
                            // Snapshot stays at oldValue - the rejected clear is
                            // never treated as a change.
                            continue;
                        }

                        anyChanged = true;
                        Log($"UDP '{kvp.Key}' changed on '{physicalName}': '{oldValue}' -> '{kvp.Value}'");

                        // Evaluate dependencies for this change
                        _udpRuntimeService.HandleUdpValueChange(entity, kvp.Key, kvp.Value);

                        // Check if this UDP change triggers predefined columns
                        var newPredefined = PredefinedColumnService.Instance.GetByUdpCondition(kvp.Key, kvp.Value);
                        if (newPredefined.Any())
                        {
                            AddPredefinedColumnsForUdp(entity, kvp.Key, kvp.Value, physicalName);
                        }

                        snapshot.UdpValues[kvp.Key] = kvp.Value;
                    }
                }

                // Only re-read if dependencies actually changed something
                if (anyChanged)
                {
                    var updatedValues = _udpRuntimeService.ReadUdpValues((object)entity);
                    foreach (var kvp in updatedValues)
                    {
                        snapshot.UdpValues[kvp.Key] = kvp.Value;
                    }

                    // UDP value change can affect UDP-conditional naming rules
                    // (e.g. TABLE_TYPE='LOG' triggers a 'LOG_' prefix rule scoped to TABLE_TYPE=LOG).
                    // Without this call, naming validation only runs on rename / new entity, so
                    // setting a UDP that has a conditional naming rule would never apply the prefix.
                    ValidateNamingStandard("Table", physicalName, entity);
                }
            }
            catch (Exception ex)
            {
                Log($"CheckForUdpValueChanges error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Re-validate rules for a single property after the user clicked
        /// Cancel on a Required popup and we reverted the value. Replaces
        /// any existing failure entries for this property in the
        /// consolidated <paramref name="failures"/> list with the result
        /// of validating the post-revert value, so the end-of-method
        /// warning surface reflects current model state (not the pre-
        /// cancel value). Returns <c>true</c> when the reverted value
        /// satisfies every rule (caller may safely set the dismiss flag),
        /// <c>false</c> when violations remain (caller MUST NOT set the
        /// dismiss flag - leaving it unset lets the next validation pass
        /// re-fire the popup so the user is forced to address it).
        /// 2026-05-24 user requests: "her ihtimale karşı yine kurallar
        /// revert edilen üzerinde çalıştırılmalı" + "Revert sonrası hala
        /// invalid ise Popup'ı TEKRAR göster".
        /// </summary>
        private bool RevalidatePropertyAfterRevert(object scapiBoxed, string objectType, string propertyCode, string postRevertValue, List<NamingValidationResult> failures)
        {
            if (failures == null || string.IsNullOrEmpty(propertyCode)) return true;
            try
            {
                // Drop every existing failure entry for this property -
                // they were computed against the pre-revert value and are
                // now stale.
                failures.RemoveAll(f => f.Rule != null
                    && string.Equals(f.Rule.PropertyCode, propertyCode, StringComparison.OrdinalIgnoreCase));

                // Post-revert re-validation runs only for existing entities
                // (the Cancel branch that calls this never fires for new
                // ones - new entities are deleted on Cancel instead). So
                // pass isNew=false unconditionally.
                var freshResults = NamingValidationEngine.ValidateObjectName(
                    objectType, postRevertValue, scapiBoxed, propertyCode, isNew: false);
                if (freshResults == null) return true;

                var freshFailures = freshResults.Where(r => !r.IsValid).ToList();
                if (freshFailures.Count == 0)
                {
                    Log($"Post-revert re-validation: {objectType}.{propertyCode}='{postRevertValue}' satisfies all rules");
                    return true;
                }

                failures.AddRange(freshFailures);
                foreach (var f in freshFailures)
                {
                    Log($"Post-revert re-validation: {objectType}.{propertyCode}='{postRevertValue}' still violates rule#{f.Rule?.Id} ({f.RuleName}) - dismiss NOT set, popup will re-fire");
                }
                return false;
            }
            catch (Exception ex)
            {
                Log($"RevalidatePropertyAfterRevert err for {objectType}.{propertyCode}: {ex.Message}");
                return true; // safer default: prevent loop on internal errors
            }
        }

        /// <summary>
        /// Add predefined columns to the entity based on a UDP condition match.
        /// Called when a UDP value change matches predefined column rules.
        /// </summary>
        /// <summary>
        /// Re-evaluate every conditional predefined-column rule against
        /// the entity's CURRENT UDP values. For each rule whose
        /// <c>DEPENDS_ON_UDP_VALUE</c> matches the entity's current UDP
        /// value, apply its predefined columns (idempotent: already-
        /// present columns are skipped by
        /// <see cref="ApplyPredefinedColumnsToEntity"/>). Used by the
        /// Entity / Column Editor close paths to pick up UDP changes the
        /// user made via the grid - the live per-tick UDP watcher was
        /// disabled 2026-05-22 due to erwin crashes, so the close-edge
        /// pass is the only place we can re-evaluate without paying the
        /// per-tick read cost. Columns added by a rule whose UDP no
        /// longer matches are intentionally NOT removed - admin's intent
        /// was "add these when UDP=X", and dropping them later would
        /// risk losing user-typed column metadata.
        /// </summary>
        public void ReevaluateConditionalPredefinedColumns(dynamic entity, string physicalName)
        {
            if (entity == null || string.IsNullOrEmpty(physicalName)) return;
            try
            {
                if (!PredefinedColumnService.Instance.IsLoaded)
                {
                    PredefinedColumnService.Instance.LoadPredefinedColumns();
                }

                // Distinct list of UDP names referenced by any conditional
                // rule. Reading per-UDP once keeps the SCAPI cost bounded
                // to O(rules) regardless of how many predefined columns
                // each rule produces.
                var conditionalRules = PredefinedColumnService.Instance.GetAll()
                    .Where(c => c != null && !string.IsNullOrEmpty(c.DependsOnUdpName) && !string.IsNullOrEmpty(c.DependsOnUdpValue))
                    .ToList();
                Log($"ReevaluateConditionalPredefinedColumns: '{physicalName}' - {conditionalRules.Count} conditional rule(s) loaded (total loaded={PredefinedColumnService.Instance.GetAll().Count()})");
                if (conditionalRules.Count == 0) return;

                var udpNames = conditionalRules.Select(c => c.DependsOnUdpName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var liveUdp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var udpName in udpNames)
                {
                    string path = $"Entity.Physical.{udpName}";
                    try
                    {
                        liveUdp[udpName] = entity.Properties(path)?.Value?.ToString() ?? "";
                        Log($"ReevaluateConditionalPredefinedColumns: read '{path}' on '{physicalName}' = '{liveUdp[udpName]}'");
                    }
                    catch (Exception ex)
                    {
                        Log($"ReevaluateConditionalPredefinedColumns: read '{path}' on '{physicalName}' failed: {ex.Message}");
                        liveUdp[udpName] = "";
                    }
                }

                // Distinct (udpName, udpValue) pairs that match the live
                // state. AddPredefinedColumnsForUdp re-queries by the same
                // key so calling it once per pair is sufficient.
                var firedPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in conditionalRules)
                {
                    if (!liveUdp.TryGetValue(rule.DependsOnUdpName, out var liveVal))
                    {
                        Log($"ReevaluateConditionalPredefinedColumns: rule '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}' SKIP - liveUdp has no entry for '{rule.DependsOnUdpName}'");
                        continue;
                    }
                    if (string.IsNullOrEmpty(liveVal))
                    {
                        Log($"ReevaluateConditionalPredefinedColumns: rule '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}' SKIP - live value empty");
                        continue;
                    }
                    if (!rule.DependsOnUdpValue.Equals(liveVal, StringComparison.OrdinalIgnoreCase))
                    {
                        Log($"ReevaluateConditionalPredefinedColumns: rule '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}' SKIP - live value='{liveVal}' (mismatch)");
                        continue;
                    }
                    Log($"ReevaluateConditionalPredefinedColumns: rule '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}' MATCH live='{liveVal}' on '{physicalName}'");

                    string key = rule.DependsOnUdpName + "\0" + rule.DependsOnUdpValue;
                    if (!firedPairs.Add(key)) continue;

                    try
                    {
                        AddPredefinedColumnsForUdp(entity, rule.DependsOnUdpName, rule.DependsOnUdpValue, physicalName);
                    }
                    catch (Exception ex)
                    {
                        Log($"ReevaluateConditionalPredefinedColumns: AddPredefinedColumnsForUdp err for '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}' on '{physicalName}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ReevaluateConditionalPredefinedColumns error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Read-only check: is the attribute (identified by ObjectId) a
        /// current PK member of the given entity? Walks the entity's
        /// Key_Group collection, finds the one with Key_Group_Type="PK",
        /// then walks its Key_Group_Member rows looking for a matching
        /// Attribute_Ref. Used by locked-predefined-column enforcement
        /// (2026-05-25) to detect PK drift on locked columns.
        /// </summary>
        public bool IsAttributeInPrimaryKey(dynamic entity, string attrObjectId)
        {
            if (entity == null || string.IsNullOrEmpty(attrObjectId)) return false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic groups = modelObjects.Collect(entity, "Key_Group");
                if (groups == null) return false;
                foreach (dynamic kg in groups)
                {
                    string kgType = null;
                    try { kgType = kg.Properties("Key_Group_Type").Value?.ToString(); }
                    catch { continue; }
                    if (!string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase)) continue;

                    dynamic members = modelObjects.Collect(kg, "Key_Group_Member");
                    if (members == null) return false;
                    foreach (dynamic m in members)
                    {
                        string memberAttrRef = null;
                        try { memberAttrRef = m.Properties("Attribute_Ref").Value?.ToString(); }
                        catch { continue; }
                        if (string.Equals(memberAttrRef, attrObjectId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    return false; // there is exactly one PK Key_Group per entity
                }
            }
            catch (Exception ex)
            {
                Log($"IsAttributeInPrimaryKey err: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Public entry to add an attribute to the entity's PK Key_Group.
        /// Idempotent (no-op when already a member). Wraps the existing
        /// private helper so the locked-column enforcement code (in
        /// <c>ValidationCoordinatorService</c>) can promote a column to
        /// PK without duplicating the Key_Group / Key_Group_Member
        /// transaction logic. 2026-05-25.
        /// </summary>
        public void EnsureAttributeInPrimaryKey(dynamic entity, dynamic attr, string columnName)
        {
            if (entity == null || attr == null) return;
            try { MakeAttributePrimaryKey(entity, attr, columnName ?? string.Empty, "LOCKED-ENFORCE"); }
            catch (Exception ex) { Log($"EnsureAttributeInPrimaryKey err for '{columnName}': {ex.Message}"); }
        }

        /// <summary>
        /// Remove an attribute from the entity's PK Key_Group. No-op
        /// when the attribute is not a current PK member. Used by
        /// locked-predefined-column PK enforcement when admin authored
        /// the rule with IS_PRIMARY_KEY=false but the user added the
        /// column to the PK manually. 2026-05-25.
        /// </summary>
        public void RemoveAttributeFromPrimaryKey(dynamic entity, string attrObjectId, string columnName)
        {
            if (entity == null || string.IsNullOrEmpty(attrObjectId)) return;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic groups = modelObjects.Collect(entity, "Key_Group");
                if (groups == null) return;
                dynamic pkGroup = null;
                foreach (dynamic kg in groups)
                {
                    string kgType = null;
                    try { kgType = kg.Properties("Key_Group_Type").Value?.ToString(); }
                    catch { continue; }
                    if (string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase))
                    {
                        pkGroup = kg;
                        break;
                    }
                }
                if (pkGroup == null) return;

                dynamic members = modelObjects.Collect(pkGroup, "Key_Group_Member");
                if (members == null) return;

                dynamic targetMember = null;
                foreach (dynamic m in members)
                {
                    string memberAttrRef = null;
                    try { memberAttrRef = m.Properties("Attribute_Ref").Value?.ToString(); }
                    catch { continue; }
                    if (string.Equals(memberAttrRef, attrObjectId, StringComparison.OrdinalIgnoreCase))
                    {
                        targetMember = m;
                        break;
                    }
                }
                if (targetMember == null) return;

                int txId = _session.BeginNamedTransaction("RemoveLockedPkMember");
                try
                {
                    // SCAPI removal uses the parent's Remove(child) on the
                    // member collection. The Add path used Collect(pkGroup).
                    // Add("Key_Group_Member") so the inverse is symmetric.
                    modelObjects.Collect(pkGroup).Remove(targetMember);
                    _session.CommitTransaction(txId);
                    Log($"Removed '{columnName}' from PK Key_Group (locked rule says IS_PRIMARY_KEY=false)");
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(txId); } catch (Exception rbEx) { Log($"RemoveAttributeFromPrimaryKey rollback err: {rbEx.Message}"); }
                    Log($"RemoveAttributeFromPrimaryKey FAILED for '{columnName}': {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Log($"RemoveAttributeFromPrimaryKey err for '{columnName}': {ex.Message}");
            }
        }

        /// Attribute definition copied when a wedged user column is moved to the
        /// end of the table (locked-column order enforcement, 2026-06-07).
        /// Mirrors <c>ModelConfigForm.CopyAttributeProperties</c> so a moved
        /// column keeps its full shape. Name + Physical_Name are handled
        /// separately (they drive the create + the re-read match).
        private static readonly string[] MovableAttributeProperties =
        {
            "Physical_Data_Type",
            "Logical_Data_Type",
            "Null_Option",
            "Definition",
            "Note",
            "Default_Value",
            "Parent_Domain_Ref"
        };

        /// <summary>
        /// True when the attribute identified by <paramref name="attrObjectId"/>
        /// is a member of ANY Key_Group on the entity (PK, AK, or a foreign-key
        /// group). Generalises <see cref="IsAttributeInPrimaryKey"/> to all key
        /// types. Used by locked-column ORDER enforcement (2026-06-07) as the
        /// "safe to delete + re-add" gate: a plain owned column (member of no
        /// key group) can be moved to the table end without destroying a key or
        /// foreign-key relationship; a key member cannot, so the enforcement
        /// only warns for those.
        /// </summary>
        public bool IsColumnKeyMember(dynamic entity, string attrObjectId)
        {
            if (entity == null || string.IsNullOrEmpty(attrObjectId)) return false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic groups = modelObjects.Collect(entity, "Key_Group");
                if (groups == null) return false;
                foreach (dynamic kg in groups)
                {
                    dynamic members;
                    try { members = modelObjects.Collect(kg, "Key_Group_Member"); }
                    catch { continue; }
                    if (members == null) continue;
                    foreach (dynamic m in members)
                    {
                        string memberAttrRef = null;
                        try { memberAttrRef = m.Properties("Attribute_Ref").Value?.ToString(); }
                        catch { continue; }
                        if (string.Equals(memberAttrRef, attrObjectId, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }
            catch (Exception ex) { Log($"IsColumnKeyMember err: {ex.Message}"); }
            return false;
        }

        /// <summary>
        /// Move a wedged user column to the END of the entity's attribute list,
        /// preserving the locked predefined-column block at the start (locked
        /// column order enforcement, 2026-06-07). erwin SCAPI exposes no column
        /// reorder, so the only mechanism is capture-properties -> delete ->
        /// re-add at the end (the collection's <c>Add("Attribute")</c> appends).
        /// CALLER MUST first confirm the column is safe to move via
        /// <see cref="IsColumnKeyMember"/> == false - deleting a key/FK column
        /// would destroy the relationship. Returns true when the column ended up
        /// re-created at the end.
        /// </summary>
        public bool MoveColumnToEnd(dynamic entity, string columnName, string entityName)
        {
            if (entity == null || string.IsNullOrEmpty(columnName)) return false;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic attrs = modelObjects.Collect(entity, "Attribute");
                if (attrs == null) return false;

                // 1. Locate the target + capture its full definition BEFORE delete.
                dynamic target = null;
                string physName = columnName;
                var captured = new Dictionary<string, object>();
                foreach (dynamic a in attrs)
                {
                    if (a == null) continue;
                    string aName;
                    try { aName = a.Name ?? ""; } catch { continue; }
                    if (!string.Equals(aName, columnName, StringComparison.OrdinalIgnoreCase)) continue;

                    target = a;
                    try
                    {
                        string p = a.Properties("Physical_Name").Value?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(p) && !p.StartsWith("%")) physName = p;
                    }
                    catch { /* keep the logical name as physical fallback */ }
                    foreach (string prop in MovableAttributeProperties)
                    {
                        try
                        {
                            var v = a.Properties(prop).Value;
                            if (v != null)
                            {
                                string sv = v.ToString();
                                if (!string.IsNullOrEmpty(sv) && !sv.StartsWith("%"))
                                    captured[prop] = v;
                            }
                        }
                        catch { /* property not surfaced on this attr - skip */ }
                    }
                    break;
                }
                if (target == null)
                {
                    Log($"MoveColumnToEnd: column '{columnName}' not found on '{entityName}'");
                    return false;
                }

                // 2. Delete the wedged column.
                int delTx = _session.BeginNamedTransaction("MoveColumnToEnd-Delete");
                try
                {
                    modelObjects.Remove(target);
                    _session.CommitTransaction(delTx);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(delTx); } catch (Exception rb) { Log($"MoveColumnToEnd delete rollback err: {rb.Message}"); }
                    Log($"MoveColumnToEnd delete FAILED for '{columnName}' on '{entityName}': {ex.Message}");
                    return false;
                }

                // 3. Re-create it - Add("Attribute") appends to the END.
                dynamic newAttr = ErwinUtilities.CreateAttribute(_session, entity, columnName);
                if (newAttr == null)
                {
                    Log($"MoveColumnToEnd: re-create returned null for '{columnName}' on '{entityName}' (column lost)");
                    return false;
                }

                // 4. Re-apply the captured definition.
                int setTx = _session.BeginNamedTransaction("MoveColumnToEnd-Reapply");
                try
                {
                    try { newAttr.Properties("Physical_Name").Value = physName; } catch (Exception ex) { Log($"MoveColumnToEnd reapply Physical_Name err: {ex.Message}"); }
                    foreach (var kv in captured)
                    {
                        try { newAttr.Properties(kv.Key).Value = kv.Value; }
                        catch (Exception ex) { Log($"MoveColumnToEnd reapply '{kv.Key}' err: {ex.Message}"); }
                    }
                    _session.CommitTransaction(setTx);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(setTx); } catch (Exception rb) { Log($"MoveColumnToEnd reapply rollback err: {rb.Message}"); }
                    Log($"MoveColumnToEnd reapply FAILED for '{columnName}' on '{entityName}': {ex.Message}");
                }

                Log($"MoveColumnToEnd: '{columnName}' moved to end of '{entityName}' (locked column order preserved)");
                return true;
            }
            catch (Exception ex)
            {
                Log($"MoveColumnToEnd err for '{columnName}' on '{entityName}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Re-create a specific predefined column on the given entity.
        /// Used by the locked-column delete-restore path
        /// (<c>ValidationCoordinatorService.RestoreDeletedLockedColumns</c>):
        /// the user just deleted a column whose name matched a locked
        /// predefined-column rule, so we re-apply just that rule. Goes
        /// through the same <see cref="ApplyPredefinedColumnsToEntity"/>
        /// path the normal add flow uses, so the column comes back with
        /// the exact properties admin authored (datatype, nullable, PK,
        /// default, in-PK membership). Idempotent: if a column with the
        /// same name still exists (e.g. the heartbeat fired during a
        /// rapid recreate gesture) the inner name-check skips it.
        /// </summary>
        public void RestoreSpecificPredefinedColumn(dynamic entity, PredefinedColumn rule, string physicalName)
        {
            if (entity == null || rule == null) return;
            try
            {
                string contextLabel = rule.IsUnconditional
                    ? "LOCKED-RESTORE unconditional"
                    : $"LOCKED-RESTORE UDP '{rule.DependsOnUdpName}'='{rule.DependsOnUdpValue}'";
                ApplyPredefinedColumnsToEntity(entity, new[] { rule }, physicalName, contextLabel);
            }
            catch (Exception ex)
            {
                Log($"RestoreSpecificPredefinedColumn err for '{rule.ColumnName}' on '{physicalName}': {ex.Message}");
            }
        }

        private void AddPredefinedColumnsForUdp(dynamic entity, string udpName, string udpValue, string physicalName)
        {
            try
            {
                // Ensure PredefinedColumnService is loaded
                if (!PredefinedColumnService.Instance.IsLoaded)
                {
                    PredefinedColumnService.Instance.LoadPredefinedColumns();
                }

                var predefinedColumns = PredefinedColumnService.Instance.GetByUdpCondition(udpName, udpValue);
                if (!predefinedColumns.Any()) return;

                string context = $"UDP '{udpName}'='{udpValue}'";
                ApplyPredefinedColumnsToEntity(entity, predefinedColumns, physicalName, context);
            }
            catch (Exception ex)
            {
                Log($"AddPredefinedColumnsForUdp error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Add the config's UNCONDITIONAL predefined columns to <paramref name="entity"/>.
        /// Called once when a brand-new entity is first detected so the
        /// "always apply" rows admin authored (DEPENDS_ON_UDP_ID = NULL,
        /// 2026-05-14 schema extension) land on every new table independently
        /// of UDP cascades. Idempotent through the in-entity name check inside
        /// <see cref="ApplyPredefinedColumnsToEntity"/>, so re-firing on a
        /// rebaseline cycle does not duplicate columns.
        /// </summary>
        private void AddUnconditionalPredefinedColumns(dynamic entity, string physicalName)
        {
            try
            {
                if (!PredefinedColumnService.Instance.IsLoaded)
                {
                    PredefinedColumnService.Instance.LoadPredefinedColumns();
                }

                var unconditional = PredefinedColumnService.Instance.GetUnconditional();
                if (!unconditional.Any()) return;

                ApplyPredefinedColumnsToEntity(entity, unconditional, physicalName, "unconditional");
            }
            catch (Exception ex)
            {
                Log($"AddUnconditionalPredefinedColumns error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Shared "create + populate attributes" loop for both conditional and
        /// unconditional predefined-column flows. Reads the entity's existing
        /// attribute names once, then for each input column either skips
        /// (already present) or creates the attribute and sets
        /// Physical_Name / Physical_Data_Type / Null_Option_Type / default
        /// inside a single named transaction. Logs every per-column success +
        /// failure so a missing column on a new table is traceable from the
        /// debug log alone. <paramref name="contextLabel"/> is appended to
        /// log lines so it is clear from the log which branch fired.
        /// </summary>
        private void ApplyPredefinedColumnsToEntity(
            dynamic entity,
            IEnumerable<PredefinedColumn> columnsToApply,
            string physicalName,
            string contextLabel)
        {
            // Snapshot the entity's current attribute names so we can skip any
            // pre-existing ones (the user may have hand-added them, or a
            // previous tick may have applied the conditional version).
            dynamic modelObjects = _session.ModelObjects;
            var existingColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                dynamic attributes = modelObjects.Collect(entity, "Attribute");
                if (attributes != null)
                {
                    foreach (dynamic attr in attributes)
                    {
                        try
                        {
                            string attrName = attr.Name ?? "";
                            if (!string.IsNullOrEmpty(attrName))
                                existingColumnNames.Add(attrName);
                        }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): Error reading attr name: {ex.Message}"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyPredefined({contextLabel}): existing-columns read failed on '{physicalName}': {ex.Message}");
            }

            int addedCount = 0;
            foreach (var col in columnsToApply)
            {
                if (existingColumnNames.Contains(col.ColumnName))
                {
                    Log($"ApplyPredefined({contextLabel}): '{col.ColumnName}' already exists on '{physicalName}', skipping");
                    continue;
                }

                try
                {
                    dynamic newAttribute = ErwinUtilities.CreateAttribute(_session, entity, col.ColumnName);
                    if (newAttribute == null)
                    {
                        Log($"ApplyPredefined({contextLabel}): CreateAttribute returned null for '{col.ColumnName}' on '{physicalName}'");
                        continue;
                    }

                    int transId = _session.BeginNamedTransaction("SetPredefinedColumnProperties");
                    try
                    {
                        try { newAttribute.Properties("Physical_Name").Value = col.ColumnName; }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set Physical_Name '{col.ColumnName}': {ex.Message}"); }

                        try { newAttribute.Properties("Physical_Data_Type").Value = col.DataType; }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set Physical_Data_Type '{col.ColumnName}': {ex.Message}"); }

                        try { newAttribute.Properties("Null_Option_Type").Value = col.Nullable ? 0 : 1; }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set Null_Option_Type '{col.ColumnName}': {ex.Message}"); }

                        if (!string.IsNullOrEmpty(col.DefaultValue))
                        {
                            // Default-value SCAPI accessor varies between
                            // erwin builds. Probe once, cache, reuse.
                            string defAccessor = ErwinUtilities.ResolveAttributeDefaultAccessor(newAttribute);
                            if (string.IsNullOrEmpty(defAccessor))
                            {
                                Log($"ApplyPredefined({contextLabel}): default skipped for '{col.ColumnName}' - no default-value accessor available");
                            }
                            else
                            {
                                try { newAttribute.Properties(defAccessor).Value = col.DefaultValue; }
                                catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set default ({defAccessor}) '{col.ColumnName}': {ex.Message}"); }
                            }
                        }

                        // Comment (admin DEFINITION, 2026-06-08) -> erwin column
                        // Definition. Only written when admin authored one, so a
                        // blank comment does not stomp anything.
                        if (!string.IsNullOrEmpty(col.Comment))
                        {
                            try { newAttribute.Properties("Definition").Value = col.Comment; }
                            catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set Definition (comment) '{col.ColumnName}': {ex.Message}"); }
                        }

                        _session.CommitTransaction(transId);
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); }
                        catch (Exception rbEx) { Log($"ApplyPredefined({contextLabel}): rollback failed: {rbEx.Message}"); }
                        Log($"ApplyPredefined({contextLabel}): transaction failed on '{col.ColumnName}': {ex.Message}");
                        continue; // do not count a failed transaction as added
                    }

                    // PK membership runs OUTSIDE the property-setting transaction
                    // above because the Key_Group_Member.Add call commits its
                    // own enclosed transaction; nesting them caused erwin r10.10
                    // to reject the parent commit with "ESX-3 transaction not
                    // open" on a 2026-05-14 dry run. Failure here is logged but
                    // does not roll back the attribute itself - a PK-less
                    // predefined column is still a valid (and recoverable)
                    // outcome that the user can fix by hand.
                    if (col.IsPrimaryKey)
                    {
                        try { MakeAttributePrimaryKey(entity, newAttribute, col.ColumnName, contextLabel); }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): PK promote failed for '{col.ColumnName}': {ex.Message}"); }
                    }

                    addedCount++;
                    string pkSuffix = col.IsPrimaryKey ? " [PK]" : "";
                    Log($"ApplyPredefined({contextLabel}): added '{col.ColumnName}' ({col.DataType}){pkSuffix} to '{physicalName}'");
                }
                catch (Exception ex)
                {
                    Log($"ApplyPredefined({contextLabel}): error adding '{col.ColumnName}' to '{physicalName}': {ex.Message}");
                }
            }

            if (addedCount > 0)
                Log($"ApplyPredefined({contextLabel}): added {addedCount} column(s) to '{physicalName}'");
        }

        /// <summary>
        /// Promote <paramref name="newAttribute"/> into <paramref name="entity"/>'s
        /// Primary Key Key_Group. erwin auto-creates exactly one Key_Group with
        /// Key_Group_Type == "PK" when an entity is first created, so we find
        /// that group and append a new Key_Group_Member that references the
        /// attribute via Attribute_Ref (the property name erwin uses on
        /// EMX:Key_Group_Member - verified against real .erwin XML 2026-05-14
        /// in ErwinAlterDdl/test_files/erwin/backup_dont_consider).
        ///
        /// Idempotent: if a member already exists for this attribute the
        /// method exits without throwing or duplicating. If no PK Key_Group
        /// is present yet (unusual - happens when an entity is mid-creation
        /// and erwin hasn't seeded its default key group yet), we create one
        /// in-place; the resulting group inherits the entity's owning model
        /// scope automatically through Collect(entity).Add("Key_Group").
        /// </summary>
        private void MakeAttributePrimaryKey(dynamic entity, dynamic newAttribute, string columnName, string contextLabel)
        {
            string attrObjectId = null;
            try { attrObjectId = newAttribute.ObjectId?.ToString(); }
            catch (Exception ex)
            {
                Log($"ApplyPredefined({contextLabel}): PK promote skipped - cannot read attribute ObjectId for '{columnName}': {ex.Message}");
                return;
            }
            if (string.IsNullOrEmpty(attrObjectId))
            {
                Log($"ApplyPredefined({contextLabel}): PK promote skipped - empty attribute ObjectId for '{columnName}'");
                return;
            }

            dynamic modelObjects = _session.ModelObjects;

            // 1) Find the entity's PK Key_Group. erwin creates this lazily,
            //    so we accept "not found" and create one below; on any
            //    existing entity it is always present.
            dynamic pkGroup = null;
            try
            {
                dynamic groups = modelObjects.Collect(entity, "Key_Group");
                if (groups != null)
                {
                    foreach (dynamic kg in groups)
                    {
                        string kgType = null;
                        try { kgType = kg.Properties("Key_Group_Type").Value?.ToString(); }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): Key_Group_Type read err for '{columnName}': {ex.Message}"); }
                        if (string.Equals(kgType, "PK", StringComparison.OrdinalIgnoreCase))
                        {
                            pkGroup = kg;
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyPredefined({contextLabel}): Key_Group enumerate err for '{columnName}': {ex.Message}");
            }

            // 2) Create a PK group only if the entity truly has none.
            if (pkGroup == null)
            {
                int kgTransId = _session.BeginNamedTransaction("CreatePkKeyGroup");
                try
                {
                    pkGroup = modelObjects.Collect(entity).Add("Key_Group");
                    if (pkGroup != null)
                    {
                        try { pkGroup.Properties("Key_Group_Type").Value = "PK"; }
                        catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set Key_Group_Type=PK err for '{columnName}': {ex.Message}"); }
                    }
                    _session.CommitTransaction(kgTransId);
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(kgTransId); }
                    catch (Exception rbEx) { Log($"ApplyPredefined({contextLabel}): PK Key_Group rollback err: {rbEx.Message}"); }
                    Log($"ApplyPredefined({contextLabel}): could not create PK Key_Group for '{columnName}': {ex.Message}");
                    return;
                }
            }
            if (pkGroup == null)
            {
                Log($"ApplyPredefined({contextLabel}): no PK Key_Group available for '{columnName}'");
                return;
            }

            // 3) Idempotency: bail if the attribute is already a member.
            try
            {
                dynamic existingMembers = modelObjects.Collect(pkGroup, "Key_Group_Member");
                if (existingMembers != null)
                {
                    foreach (dynamic m in existingMembers)
                    {
                        string memberAttrRef = null;
                        try { memberAttrRef = m.Properties("Attribute_Ref").Value?.ToString(); }
                        catch { /* try next */ }
                        if (string.Equals(memberAttrRef, attrObjectId, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"ApplyPredefined({contextLabel}): '{columnName}' already a PK member, skipping promote");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"ApplyPredefined({contextLabel}): PK member enumerate err for '{columnName}': {ex.Message}");
            }

            // 4) Append the member. Attribute_Ref carries the ObjectId of the
            //    target attribute (a GUID-shaped string in erwin's storage).
            int memberTransId = _session.BeginNamedTransaction("AddPkKeyGroupMember");
            try
            {
                dynamic newMember = modelObjects.Collect(pkGroup).Add("Key_Group_Member");
                if (newMember == null)
                {
                    _session.RollbackTransaction(memberTransId);
                    Log($"ApplyPredefined({contextLabel}): Key_Group_Member.Add returned null for '{columnName}'");
                    return;
                }
                try { newMember.Properties("Attribute_Ref").Value = attrObjectId; }
                catch (Exception ex)
                {
                    _session.RollbackTransaction(memberTransId);
                    Log($"ApplyPredefined({contextLabel}): set Attribute_Ref err for '{columnName}': {ex.Message}");
                    return;
                }
                _session.CommitTransaction(memberTransId);
                Log($"ApplyPredefined({contextLabel}): '{columnName}' promoted to PK");
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(memberTransId); }
                catch (Exception rbEx) { Log($"ApplyPredefined({contextLabel}): PK member rollback err: {rbEx.Message}"); }
                Log($"ApplyPredefined({contextLabel}): Key_Group_Member.Add failed for '{columnName}': {ex.Message}");
            }
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopMonitoring();
        }

        /// <summary>
        /// Seed (but do not diff against) the entity's watched-property
        /// snapshot. Called by <c>ValidationCoordinatorService.DiagramHeartbeatTick</c>
        /// on EVERY entity it walks, regardless of editor-open state, so
        /// the "before edit" state is captured before the user starts
        /// modifying properties. Without this seed, the drift check
        /// running for the first time after editor-close would baseline
        /// against the POST-edit state and miss the change.
        /// </summary>
        public void EnsureEntitySnapshotSeeded(dynamic entity, string physicalName)
        {
            if (!_isMonitoring) return;
            if (entity == null) return;
            if (!NamingStandardService.Instance.IsLoaded) return;

            string objectId;
            try { objectId = entity.ObjectId?.ToString() ?? ""; }
            catch { return; }
            if (string.IsNullOrEmpty(objectId)) return;
            if (_entitySnapshots.ContainsKey(objectId)) return;

            var snapshot = new EntitySnapshot
            {
                ObjectId = objectId,
                EntityName = "",
                PhysicalName = physicalName ?? "",
            };
            RefreshWatchedProperties(entity, snapshot);
            _entitySnapshots[objectId] = snapshot;
        }

        /// <summary>
        /// Per-tick drift check entry point invoked by
        /// <c>ValidationCoordinatorService.DiagramHeartbeatTick</c> ONLY
        /// when no edit dialog is open (Table Editor / Column Editor
        /// closed). Compares the entity's live SCAPI values against the
        /// previously seeded snapshot; on the first change found we
        /// re-fire <see cref="ValidateNamingStandard"/> and refresh the
        /// snapshot to avoid re-firing on the same delta. If the entity
        /// is not yet in the snapshot dictionary the call is a no-op -
        /// the seeding pass <see cref="EnsureEntitySnapshotSeeded"/>
        /// should have run first; not finding an entry here means an
        /// unusual lifecycle (e.g. snapshot reset mid-tick) and skipping
        /// is safer than baselining post-edit.
        /// </summary>
        public void CheckEntityPropertyDrift(dynamic entity, string physicalName)
        {
            if (!_isMonitoring) return;
            if (entity == null) return;
            if (!NamingStandardService.Instance.IsLoaded) return;

            string objectId;
            try { objectId = entity.ObjectId?.ToString() ?? ""; }
            catch { return; }
            if (string.IsNullOrEmpty(objectId)) return;

            if (!_entitySnapshots.TryGetValue(objectId, out var snapshot))
                return;

            if (DetectWatchedPropertyChange(entity, snapshot,
                out string changedProperty, out string oldVal, out string newVal))
            {
                Log($"Watched property changed on '{physicalName}': {changedProperty} '{oldVal}' -> '{newVal}' - re-running naming check");
                ValidateNamingStandard("Table", physicalName, entity);
                RefreshWatchedProperties(entity, snapshot);
            }
        }

        /// <summary>
        /// Read every property the loaded naming-standard set targets on
        /// the given <paramref name="objectType"/> (excluding
        /// <c>Physical_Name</c>, which has its own first-class snapshot
        /// field on the wrapping struct) and write the live SCAPI value
        /// into <paramref name="bucket"/>. Failures are stored as empty
        /// strings - same contract Step 3b uses when evaluating the rule.
        /// </summary>
        private void ReadWatchedProperties(dynamic obj, string objectType, Dictionary<string, string> bucket)
        {
            if (obj == null || bucket == null) return;
            try
            {
                bucket.Clear();
                foreach (var code in NamingStandardService.Instance.GetPropertyCodes(objectType))
                {
                    if (string.IsNullOrEmpty(code)) continue;
                    if (string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase)) continue;

                    string value;
                    try { value = obj.Properties(code)?.Value?.ToString() ?? ""; }
                    catch { value = ""; }
                    bucket[code] = value;
                }
            }
            catch (Exception ex)
            {
                Log($"ReadWatchedProperties({objectType}) error: {ex.Message}");
            }
        }

        /// <summary>
        /// Convenience wrapper for the Table path: hands the
        /// <see cref="EntitySnapshot"/> WatchedProperties dict to
        /// <see cref="ReadWatchedProperties"/>.
        /// </summary>
        private void RefreshWatchedProperties(dynamic entity, EntitySnapshot snapshot)
        {
            if (snapshot == null) return;
            ReadWatchedProperties(entity, "Table", snapshot.WatchedProperties);
        }

        /// <summary>
        /// Diff the live SCAPI values for every watched property on
        /// <paramref name="objectType"/> against the
        /// <paramref name="previous"/> bucket. Returns true on the first
        /// change found; the property code + old + new value are reported
        /// via out parameters for the diagnostic log line.
        /// </summary>
        private bool DetectScapiPropertyChange(dynamic obj, string objectType,
            Dictionary<string, string> previous,
            out string changedProperty, out string oldValue, out string newValue)
        {
            changedProperty = "";
            oldValue = "";
            newValue = "";
            if (obj == null || previous == null) return false;
            try
            {
                foreach (var code in NamingStandardService.Instance.GetPropertyCodes(objectType))
                {
                    if (string.IsNullOrEmpty(code)) continue;
                    if (string.Equals(code, "Physical_Name", StringComparison.OrdinalIgnoreCase)) continue;

                    string current;
                    try { current = obj.Properties(code)?.Value?.ToString() ?? ""; }
                    catch { current = ""; }

                    string prev = previous.TryGetValue(code, out var v) ? (v ?? "") : "";

                    if (!string.Equals(current, prev, StringComparison.Ordinal))
                    {
                        changedProperty = code;
                        oldValue = prev;
                        newValue = current;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DetectScapiPropertyChange({objectType}) error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Convenience wrapper for the Table path: hands the
        /// <see cref="EntitySnapshot"/> WatchedProperties dict to
        /// <see cref="DetectScapiPropertyChange"/>.
        /// </summary>
        private bool DetectWatchedPropertyChange(dynamic entity, EntitySnapshot snapshot,
            out string changedProperty, out string oldValue, out string newValue)
        {
            changedProperty = oldValue = newValue = "";
            if (snapshot == null) return false;
            return DetectScapiPropertyChange(entity, "Table", snapshot.WatchedProperties,
                out changedProperty, out oldValue, out newValue);
        }

        /// <summary>
        /// Snapshot of entity state
        /// </summary>
        private class EntitySnapshot
        {
            public string ObjectId { get; set; }
            public string EntityName { get; set; }
            public string PhysicalName { get; set; }
            /// <summary>UDP name -> value snapshot for dependency change detection</summary>
            public Dictionary<string, string> UdpValues { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            /// <summary>
            /// Live SCAPI value cache for every PROPERTY_CODE that has at least
            /// one active naming-standard rule on Table (other than
            /// Physical_Name, which already has its own snapshot field).
            /// Diffed on each monitor tick - any change re-fires
            /// <see cref="ValidateNamingStandard"/> so e.g. clearing the
            /// Owner column on an existing table triggers the Required popup
            /// just like the original new-entity / rename paths do.
            /// </summary>
            public Dictionary<string, string> WatchedProperties { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
