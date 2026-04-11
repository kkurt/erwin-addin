using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

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

        // Snapshot of Key_Group (Index) names: ObjectId -> PhysicalName
        private Dictionary<string, string> _keyGroupSnapshots;

        // MODEL_PATH read-only enforcement
        private string _modelPathOriginalValue;

        /// <summary>
        /// Silently restore MODEL_PATH if user changed it.
        /// </summary>
        private void EnforceModelPathReadOnly()
        {
            if (_modelPathOriginalValue == null) return;
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return;

                string currentValue = "";
                try { currentValue = root.Properties("Model.Physical.MODEL_PATH").Value?.ToString() ?? ""; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EnforceModelPath: Read failed: {ex.Message}"); return; }

                if (currentValue != _modelPathOriginalValue)
                {
                    int transId = _session.BeginNamedTransaction("EnforceModelPath");
                    try
                    {
                        root.Properties("Model.Physical.MODEL_PATH").Value = _modelPathOriginalValue;
                        _session.CommitTransaction(transId);
                        Log($"MODEL_PATH restored (user changed '{currentValue}' → '{_modelPathOriginalValue}')");
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"EnforceModelPath: Rollback failed: {rbEx.Message}"); }
                        Log($"EnforceModelPath: Write failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"EnforceModelPath error: {ex.Message}"); }
        }

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
                            snapshot.UdpValues = _udpRuntimeService.ReadUdpValues(entity);
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

                // Snapshot MODEL_PATH for read-only enforcement
                try
                {
                    _modelPathOriginalValue = root.Properties("Model.Physical.MODEL_PATH").Value?.ToString() ?? "";
                }
                catch (Exception ex) { _modelPathOriginalValue = null; System.Diagnostics.Debug.WriteLine($"MODEL_PATH snapshot error: {ex.Message}"); }

                Log($"TableTypeMonitorService: Snapshot taken - {_entitySnapshots.Count} entities, {_keyGroupSnapshots.Count} key groups");
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
            // Read-only enforcement: restore MODEL_PATH if user changed it
            EnforceModelPathReadOnly();

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
                        && _entitySnapshots.ContainsKey(objectId) && _entitySnapshots[objectId].UdpValues.Count > 0)
                    {
                        CheckForUdpValueChanges(entity, objectId, physicalName);
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
                                _entitySnapshots[objectId].UdpValues = _udpRuntimeService.ReadUdpValues(entity);
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

                    try { newShape.Properties("Anchor_Point").Value = anchorStr; } catch { }

                    _session.CommitTransaction(transId);
                    Log($"AddEntityToDiagram: '{entityName}' added to '{targetDiagramName}'");
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
        /// Validate naming standards and auto-apply prefix/suffix if AUTO_APPLY=true.
        /// Shows confirmation dialog before auto-applying.
        /// </summary>
        private void ValidateNamingStandard(string objectType, string physicalName, dynamic scapiObject = null)
        {
            if (!NamingStandardService.Instance.IsLoaded) return;

            // Step 1: Check if auto-apply would change the name
            if (scapiObject != null && NamingValidationEngine.HasAutoApplyChanges(objectType, physicalName))
            {
                string newName = NamingValidationEngine.ApplyNamingStandards(objectType, physicalName);

                var answer = System.Windows.Forms.MessageBox.Show(
                    $"Naming standard requires changes for '{physicalName}':\n\n" +
                    $"'{physicalName}' → '{newName}'\n\n" +
                    $"Apply automatically?",
                    "Naming Standard — Auto Apply",
                    System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Question);

                if (answer == System.Windows.Forms.DialogResult.Yes)
                {
                    int transId = _session.BeginNamedTransaction("ApplyNamingStandard");
                    try
                    {
                        scapiObject.Properties("Physical_Name").Value = newName;
                        _session.CommitTransaction(transId);
                        Log($"Naming standard auto-applied: '{physicalName}' → '{newName}'");

                        // Update snapshot
                        string objectId = scapiObject.ObjectId?.ToString() ?? "";
                        if (_entitySnapshots.ContainsKey(objectId))
                            _entitySnapshots[objectId].PhysicalName = newName;

                        return; // Auto-applied — skip further validation (will re-validate on next tick)
                    }
                    catch (Exception ex)
                    {
                        try { _session.RollbackTransaction(transId); } catch { }
                        Log($"Naming standard auto-apply failed: {ex.Message}");
                    }
                }
            }

            // Step 2: Validate (after auto-apply or if user declined)
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

            var results = NamingValidationEngine.ValidateObjectName(objectType, nameToValidate);
            var failures = results.Where(r => !r.IsValid).ToList();

            if (failures.Count > 0)
            {
                foreach (var f in failures)
                {
                    Log($"Naming standard violation ({f.RuleName}): '{nameToValidate}' — {f.ErrorMessage}");
                }

                string messages = string.Join("\n", failures.Select(f => $"• {f.ErrorMessage}"));
                System.Windows.Forms.MessageBox.Show(
                    $"Naming standard violation(s) for '{nameToValidate}':\n\n{messages}",
                    "Naming Standard",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
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
                var currentValues = _udpRuntimeService.ReadUdpValues(entity);
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
                    var updatedValues = _udpRuntimeService.ReadUdpValues(entity);
                    foreach (var kvp in updatedValues)
                    {
                        snapshot.UdpValues[kvp.Key] = kvp.Value;
                    }
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
                if (!predefinedColumns.Any())
                {
                    return;
                }

                // Get existing attributes (columns) of the entity
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
                                {
                                    existingColumnNames.Add(attrName);
                                }
                            }
                            catch (Exception ex) { Log($"AddPredefinedForUdp: Error reading attr name: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error getting existing columns for '{physicalName}': {ex.Message}");
                }

                int addedCount = 0;
                foreach (var col in predefinedColumns)
                {
                    // Skip if column already exists
                    if (existingColumnNames.Contains(col.ColumnName))
                    {
                        Log($"Column '{col.ColumnName}' already exists on '{physicalName}', skipping");
                        continue;
                    }

                    try
                    {
                        dynamic newAttribute = ErwinUtilities.CreateAttribute(_session, entity, col.ColumnName);

                        if (newAttribute != null)
                        {
                            int transId = _session.BeginNamedTransaction("SetPredefinedColumnProperties");
                            try
                            {
                                // Set physical name
                                try
                                {
                                    newAttribute.Properties("Physical_Name").Value = col.ColumnName;
                                }
                                catch (Exception ex) { Log($"AddPredefinedForUdp: Failed to set Physical_Name for '{col.ColumnName}': {ex.Message}"); }

                                // Set data type
                                try
                                {
                                    newAttribute.Properties("Physical_Data_Type").Value = col.DataType;
                                }
                                catch (Exception ex) { Log($"AddPredefinedForUdp: Failed to set Physical_Data_Type for '{col.ColumnName}': {ex.Message}"); }

                                // Set nullability
                                try
                                {
                                    newAttribute.Properties("Null_Option_Type").Value = col.Nullable ? 0 : 1;
                                }
                                catch (Exception ex) { Log($"AddPredefinedForUdp: Failed to set Null_Option_Type for '{col.ColumnName}': {ex.Message}"); }

                                // Set default value if specified
                                if (!string.IsNullOrEmpty(col.DefaultValue))
                                {
                                    try
                                    {
                                        newAttribute.Properties("Physical_Default_Value").Value = col.DefaultValue;
                                    }
                                    catch (Exception ex) { Log($"AddPredefinedForUdp: Failed to set default value for '{col.ColumnName}': {ex.Message}"); }
                                }

                                _session.CommitTransaction(transId);
                            }
                            catch (Exception ex)
                            {
                                try { _session.RollbackTransaction(transId); }
                                catch (Exception rbEx) { Log($"AddPredefinedForUdp: Rollback failed: {rbEx.Message}"); }
                                Log($"AddPredefinedForUdp: Transaction failed for '{col.ColumnName}': {ex.Message}");
                            }

                            addedCount++;
                            Log($"Added predefined column '{col.ColumnName}' ({col.DataType}) for UDP '{udpName}'='{udpValue}'");
                        }
                        else
                        {
                            Log($"Failed to create attribute '{col.ColumnName}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error adding predefined column '{col.ColumnName}': {ex.Message}");
                    }
                }

                if (addedCount > 0)
                {
                    Log($"Added {addedCount} predefined column(s) to '{physicalName}' for UDP '{udpName}'='{udpValue}'");
                }
            }
            catch (Exception ex)
            {
                Log($"AddPredefinedColumnsForUdp error for '{physicalName}': {ex.Message}");
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
