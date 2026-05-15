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

        // Snapshot of entity state: ObjectId -> EntitySnapshot
        private Dictionary<string, EntitySnapshot> _entitySnapshots;

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
                // the contract. The standalone UdpValidationEngine.ValidateAll
                // already detects this case but was never invoked from the
                // new-entity path; this block runs it and opens a modal that
                // forces the user to pick before the pipeline returns.
                //
                // Cancel / [X] leaves the entity with empty values - the
                // subsequent validation tick will surface the same error
                // through the standard error-reporting path, so we do NOT
                // delete the entity from here.
                try
                {
                    PromptForMissingRequiredUdps(entity, physicalName);
                }
                catch (Exception ex)
                {
                    Log($"OnNewEntityDetected: required-UDP prompt error for '{physicalName}': {ex.Message}");
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
        ///
        /// Cancel + [X] returns without writing anything; the entity keeps
        /// its empty values, and the subsequent
        /// <see cref="UdpRuntimeService.ValidateBeforeSave"/> tick will
        /// continue reporting the violation through the standard channel.
        /// We do NOT delete the entity from here - rules apply to new
        /// objects only and the user may have a reason to defer the pick.
        /// </summary>
        private void PromptForMissingRequiredUdps(dynamic entity, string physicalName)
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

            if (missing.Count == 0) return;

            Log($"PromptForMissingRequiredUdps: '{physicalName}' missing {missing.Count} required UDP(s): {string.Join(", ", missing.Select(m => m.Name))}");

            using (var form = new Forms.RequiredUdpForm(physicalName, missing))
            {
                var result = form.ShowDialog();
                if (result != DialogResult.OK || form.SelectedValues.Count == 0)
                {
                    Log($"PromptForMissingRequiredUdps: user cancelled or supplied no values for '{physicalName}'");
                    return;
                }

                try
                {
                    _udpRuntimeService.WriteUdpValues((object)entity, form.SelectedValues, "Table");
                    Log($"PromptForMissingRequiredUdps: wrote {form.SelectedValues.Count} required UDP value(s) on '{physicalName}'");
                }
                catch (Exception ex)
                {
                    Log($"PromptForMissingRequiredUdps: WriteUdpValues failed on '{physicalName}': {ex.Message}");
                    return;
                }

                // Run dependency cascades for each picked value so any
                // conditional predefined column / dependent UDP that hangs
                // off TABLE_TYPE etc. fires immediately, instead of waiting
                // for the next user edit to trip CheckForUdpValueChanges.
                foreach (var kvp in form.SelectedValues)
                {
                    try { _udpRuntimeService.HandleUdpValueChange(entity, kvp.Key, kvp.Value); }
                    catch (Exception ex) { Log($"PromptForMissingRequiredUdps: dependency cascade failed for '{kvp.Key}': {ex.Message}"); }
                }
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

                            // Validate Table naming standards for new entity (with auto-apply)
                            ValidateNamingStandard("Table", physicalName, entity);
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
                                }
                                else if (nameChanged)
                                {
                                    ValidateNamingStandard("View", viewName, view);
                                    _keyGroupSnapshots["V_" + viewId] = viewName;
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
                                }
                                else if (nameChanged)
                                {
                                    ValidateNamingStandard("Subject Area", saName, sa);
                                    _keyGroupSnapshots[saKey] = saName;
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
        internal void ValidateNamingStandard(string objectType, string physicalName, dynamic scapiObject = null)
        {
            if (!NamingStandardService.Instance.IsLoaded) return;

            // scapiObject MUST be passed so UDP-conditional rules (DEPENDS_ON_UDP_ID) can read
            // the live UDP value off the entity. Without it, IsRuleApplicable returns false for
            // any UDP-conditional rule and the prefix is silently dropped.
            // Cast to object to keep the call compile-time resolved (otherwise dynamic dispatch
            // breaks the LINQ lambdas inside the engine — see CheckEntityKeyGroups for prior art).
            object scapiBoxed = scapiObject;

            // Step 1: silently apply AUTO_APPLY=true rules
            if (scapiBoxed != null)
            {
                string afterAuto = NamingValidationEngine.ApplyNamingStandards(objectType, physicalName, scapiBoxed, autoOnly: true);
                if (!string.Equals(afterAuto, physicalName, StringComparison.Ordinal))
                {
                    int transId = _session.BeginNamedTransaction("ApplyAutoNamingStandard");
                    try
                    {
                        scapiObject.Properties("Physical_Name").Value = afterAuto;
                        _session.CommitTransaction(transId);
                        Log($"Naming standard auto-applied (silent): '{physicalName}' -> '{afterAuto}'");

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
                string afterAll = NamingValidationEngine.ApplyNamingStandards(objectType, physicalName, scapiBoxed, autoOnly: false);
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

            var results = NamingValidationEngine.ValidateObjectName(objectType, nameToValidate, scapiBoxed);
            var failures = results.Where(r => !r.IsValid).ToList();

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
                bool anyAutoApplyFailing = scapiObject != null
                    && failures.Any(f => f.Rule != null && f.Rule.AutoApply);

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
        /// Add predefined columns to the entity based on a UDP condition match.
        /// Called when a UDP value change matches predefined column rules.
        /// </summary>
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
                            try { newAttribute.Properties("Physical_Default_Value").Value = col.DefaultValue; }
                            catch (Exception ex) { Log($"ApplyPredefined({contextLabel}): set default '{col.ColumnName}': {ex.Message}"); }
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
        /// Snapshot of entity state
        /// </summary>
        private class EntitySnapshot
        {
            public string ObjectId { get; set; }
            public string EntityName { get; set; }
            public string PhysicalName { get; set; }
            /// <summary>UDP name -> value snapshot for dependency change detection</summary>
            public Dictionary<string, string> UdpValues { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
