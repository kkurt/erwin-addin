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
        private Dictionary<string, Dictionary<string, string>> _subjectAreaWatchedProperties;

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
                                    // New view — just snapshot, validate on name change
                                    _keyGroupSnapshots["V_" + viewId] = viewName;
                                    var bucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                    ReadWatchedProperties(view, "View", bucket);
                                    _viewWatchedProperties[viewId] = bucket;
                                }
                                else if (nameChanged)
                                {
                                    ValidateNamingStandard("View", viewName, view);
                                    _keyGroupSnapshots["V_" + viewId] = viewName;
                                    if (!_viewWatchedProperties.TryGetValue(viewId, out var refreshBucket))
                                    {
                                        refreshBucket = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                        _viewWatchedProperties[viewId] = refreshBucket;
                                    }
                                    ReadWatchedProperties(view, "View", refreshBucket);
                                }
                                else
                                {
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
                                }
                            }
                            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"View naming check error: {ex.Message}"); }
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

            // Step 1: silently apply AUTO_APPLY=true rules
            if (scapiBoxed != null)
            {
                string afterAuto = NamingValidationEngine.ApplyNamingStandards(objectType, physicalName, scapiBoxed, autoOnly: true, isNew: isNew);
                if (!string.Equals(afterAuto, physicalName, StringComparison.Ordinal))
                {
                    int transId = _session.BeginNamedTransaction("ApplyAutoNamingStandard");
                    try
                    {
                        scapiObject.Properties("Physical_Name").Value = afterAuto;
                        _session.CommitTransaction(transId);
                        Log($"Naming standard auto-applied (silent): '{physicalName}' -> '{afterAuto}'");
                        // Modal popup (was a transient ToastNotification until
                        // 2026-05-27): user explicitly asked for an OK-to-
                        // dismiss confirmation so silent rename auto-apply
                        // cannot be missed. Owner is null so the dialog
                        // anchors to ErwinAddIn.ActiveForm's screen per
                        // AddinMessageDialog's own multi-monitor logic.
                        AddinMessageDialog.Show(
                            $"{objectType} '{physicalName}' -> '{afterAuto}'",
                            "Naming standard applied",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);

                        // Update snapshot
                        string objectId = scapiObject.ObjectId?.ToString() ?? "";
                        if (_entitySnapshots.ContainsKey(objectId))
                            _entitySnapshots[objectId].PhysicalName = afterAuto;

                        physicalName = afterAuto;
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"ApplyAutoNamingStandard rollback error: {rbEx.Message}"); }
                        Log($"Naming standard silent auto-apply failed: {ex.Message}");
                    }
                }
            }

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
                            requiredCancelHandled = TryDeleteNewEntity(scapiObject, physicalName);
                            break; // new entity is either gone or revert failed; either way exit this rf
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
                                    requiredCancelHandled = TryDeleteNewEntity(scapiObject, physicalName);
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
                                    $"'{kvp.Key}' UDP'si kilitli. Yeni deger ('{kvp.Value}') reddedildi; '{oldValue}' olarak korundu.",
                                    "UDP Kilitli",
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
