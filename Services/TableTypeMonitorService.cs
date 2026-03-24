using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Event args for TABLE_TYPE UDP changes
    /// </summary>
    public class TableTypeChangedEventArgs : EventArgs
    {
        public string TableName { get; set; }
        public string PhysicalName { get; set; }
        public string TableTypeValue { get; set; }
        public string OldPhysicalName { get; set; }
        public string NewPhysicalName { get; set; }
        public TableTypeEntry TableTypeEntry { get; set; }
    }

    /// <summary>
    /// Service to monitor TABLE_TYPE UDP changes on entities (tables)
    /// and automatically apply prefix based on NAME_EXTENSION_LOCATION
    /// </summary>
    public class TableTypeMonitorService : IDisposable
    {
        private readonly dynamic _session;
        private bool _isMonitoring;
        private bool _disposed;

        // Snapshot of entity TABLE_TYPE values: ObjectId -> (PhysicalName, TableTypeValue)
        private Dictionary<string, EntitySnapshot> _entitySnapshots;

        // Property applicator for applying project standards to new tables
        private PropertyApplicatorService _propertyApplicator;

        // Event for logging
        public event Action<string> OnLog;

        public TableTypeMonitorService(dynamic session)
        {
            _session = session;
            _entitySnapshots = new Dictionary<string, EntitySnapshot>();
        }

        /// <summary>
        /// Set the property applicator service for applying project standards to new tables.
        /// </summary>
        public void SetPropertyApplicator(PropertyApplicatorService applicator)
        {
            _propertyApplicator = applicator;
        }

        /// <summary>
        /// Start monitoring TABLE_TYPE UDP changes (timer managed by ValidationCoordinatorService)
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
                    string tableTypeValue = "";

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

                    // Get TABLE_TYPE UDP value
                    try
                    {
                        tableTypeValue = entity.Properties("Entity.Physical.TABLE_TYPE").Value?.ToString() ?? "";
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TABLE_TYPE read error: {ex.Message}"); }

                    if (!string.IsNullOrEmpty(objectId))
                    {
                        _entitySnapshots[objectId] = new EntitySnapshot
                        {
                            ObjectId = objectId,
                            EntityName = entityName,
                            PhysicalName = physicalName,
                            TableTypeValue = tableTypeValue
                        };
                    }
                }

                Log($"TableTypeMonitorService: Snapshot taken - {_entitySnapshots.Count} entities");
            }
            catch (Exception ex)
            {
                Log($"TableTypeMonitorService.TakeSnapshot error: {ex.Message}");
            }
        }

        /// <summary>
        /// Timer tick - check for TABLE_TYPE UDP changes
        /// </summary>


        /// <summary>
        /// Check for TABLE_TYPE UDP value changes (called by ValidationCoordinatorService)
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
        /// Check for TABLE_TYPE UDP value changes using pre-collected entity list (avoids double model scan)
        /// </summary>
        public void CheckForTableTypeChanges(dynamic allEntities)
        {
            try
            {
                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;

                    string objectId = "";
                    string entityName = "";
                    string physicalName = "";
                    string tableTypeValue = "";

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

                    // Get TABLE_TYPE UDP value
                    try
                    {
                        tableTypeValue = entity.Properties("Entity.Physical.TABLE_TYPE").Value?.ToString() ?? "";
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"TABLE_TYPE read error: {ex.Message}"); }

                    // Check if this is a new entity or TABLE_TYPE changed
                    bool isNew = !_entitySnapshots.ContainsKey(objectId);
                    bool tableTypeChanged = !isNew &&
                        !string.IsNullOrEmpty(tableTypeValue) &&
                        _entitySnapshots[objectId].TableTypeValue != tableTypeValue;

                    // Check if physical name changed (user manually edited the table name)
                    bool physicalNameChanged = !isNew &&
                        _entitySnapshots[objectId].PhysicalName != physicalName;

                    if (tableTypeChanged)
                    {
                        string oldTableType = _entitySnapshots[objectId].TableTypeValue;
                        Log($"TABLE_TYPE changed for '{physicalName}': '{oldTableType}' -> '{tableTypeValue}'");

                        // Update snapshot FIRST to prevent duplicate popups
                        _entitySnapshots[objectId] = new EntitySnapshot
                        {
                            ObjectId = objectId,
                            EntityName = entityName,
                            PhysicalName = physicalName,
                            TableTypeValue = tableTypeValue
                        };

                        // If old TABLE_TYPE had predefined columns, ask user if they want to delete them
                        if (!string.IsNullOrEmpty(oldTableType))
                        {
                            var oldTableTypeEntry = TableTypeService.Instance.GetByName(oldTableType);
                            if (oldTableTypeEntry != null)
                            {
                                RemoveOldPredefinedColumnsWithConfirmation(entity, oldTableTypeEntry, physicalName);
                            }
                        }

                        // Get the TableTypeEntry for the new value
                        var tableTypeEntry = TableTypeService.Instance.GetByName(tableTypeValue);
                        if (tableTypeEntry != null)
                        {
                            // Apply affix based on NAME_EXTENSION_LOCATION
                            ApplyTableTypeAffix(entity, physicalName, tableTypeEntry);
                        }
                    }
                    else if (physicalNameChanged)
                    {
                        // Physical name changed (user manually edited table name)
                        string oldPhysicalName = _entitySnapshots[objectId].PhysicalName;
                        Log($"Physical name changed for entity: '{oldPhysicalName}' -> '{physicalName}'");

                        // If TABLE_TYPE is set, check if affix needs to be reapplied
                        if (!string.IsNullOrEmpty(tableTypeValue))
                        {
                            var tableTypeEntry = TableTypeService.Instance.GetByName(tableTypeValue);
                            if (tableTypeEntry != null && !tableTypeEntry.HasAffixApplied(physicalName))
                            {
                                Log($"Affix missing after name change, reapplying for TABLE_TYPE '{tableTypeValue}'");
                                ApplyTableTypeAffix(entity, physicalName, tableTypeEntry);
                            }
                        }

                        // Update snapshot with new name
                        _entitySnapshots[objectId].PhysicalName = physicalName;
                    }
                    else if (isNew)
                    {
                        // Add new entity to snapshot
                        _entitySnapshots[objectId] = new EntitySnapshot
                        {
                            ObjectId = objectId,
                            EntityName = entityName,
                            PhysicalName = physicalName,
                            TableTypeValue = tableTypeValue
                        };

                        // Add new entity to diagram automatically
                        AddEntityToDiagram(entity, physicalName);

                        // Apply project standard properties (LOGGING, COMPRESSION, etc.)
                        if (_propertyApplicator != null && _propertyApplicator.IsInitialized)
                        {
                            _propertyApplicator.ApplyStandardsToEntity(entity, physicalName);
                        }

                        // If new entity already has TABLE_TYPE set, apply affix
                        if (!string.IsNullOrEmpty(tableTypeValue))
                        {
                            var tableTypeEntry = TableTypeService.Instance.GetByName(tableTypeValue);
                            if (tableTypeEntry != null && !tableTypeEntry.HasAffixApplied(physicalName))
                            {
                                Log($"New entity '{physicalName}' with TABLE_TYPE '{tableTypeValue}'");
                                ApplyTableTypeAffix(entity, physicalName, tableTypeEntry);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CheckForTableTypeChanges error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ask user if they want to remove old predefined columns when TABLE_TYPE changes
        /// </summary>
        private void RemoveOldPredefinedColumnsWithConfirmation(dynamic entity, TableTypeEntry oldTableTypeEntry, string tableName)
        {
            try
            {
                // Ensure PredefinedColumnService is loaded
                if (!PredefinedColumnService.Instance.IsLoaded)
                {
                    PredefinedColumnService.Instance.LoadPredefinedColumns();
                }

                var oldPredefinedColumns = PredefinedColumnService.Instance.GetByTableTypeId(oldTableTypeEntry.Id);
                if (!oldPredefinedColumns.Any())
                {
                    Log($"No predefined columns to remove for old TABLE_TYPE '{oldTableTypeEntry.Name}'");
                    return;
                }

                // Get existing attributes (columns) of the entity
                dynamic modelObjects = _session.ModelObjects;
                var columnsToRemove = new List<dynamic>();
                var columnNamesToRemove = new List<string>();

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
                                // Check if this column is from the old TABLE_TYPE's predefined columns
                                if (oldPredefinedColumns.Any(pc => pc.Name.Equals(attrName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    columnsToRemove.Add(attr);
                                    columnNamesToRemove.Add(attrName);
                                }
                            }
                            catch (Exception ex) { Log($"RemoveOldPredefined: Error reading attr name: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error checking existing columns: {ex.Message}");
                    return;
                }

                if (!columnsToRemove.Any())
                {
                    Log($"No matching predefined columns found to remove for old TABLE_TYPE '{oldTableTypeEntry.Name}'");
                    return;
                }

                // Show confirmation dialog
                string columnList = string.Join(", ", columnNamesToRemove);
                var result = MessageBox.Show(
                    $"TABLE_TYPE has changed for table '{tableName}'.\n\n" +
                    $"Do you want to delete the following columns that were added for the old TABLE_TYPE '{oldTableTypeEntry.Name}'?\n\n" +
                    $"{columnList}",
                    "Delete Old Predefined Columns?",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.Yes)
                {
                    int deletedCount = 0;
                    foreach (dynamic attr in columnsToRemove)
                    {
                        try
                        {
                            int transId = _session.BeginNamedTransaction("DeleteOldPredefinedColumn");
                            try
                            {
                                string attrName = attr.Name ?? "";
                                modelObjects.Remove(attr);
                                _session.CommitTransaction(transId);
                                deletedCount++;
                                Log($"Deleted old predefined column '{attrName}'");
                            }
                            catch (Exception ex)
                            {
                                try { _session.RollbackTransaction(transId); }
                                catch (Exception rbEx) { Log($"Rollback failed: {rbEx.Message}"); }
                                Log($"Error deleting column: {ex.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"Error removing attribute: {ex.Message}");
                        }
                    }

                    if (deletedCount > 0)
                    {
                        Log($"Deleted {deletedCount} old predefined column(s)");
                    }
                }
                else
                {
                    Log($"User chose to keep old predefined columns");
                }
            }
            catch (Exception ex)
            {
                Log($"RemoveOldPredefinedColumnsWithConfirmation error: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply affix to table physical name based on TABLE_TYPE entry
        /// First removes any existing affix, then applies the new one
        /// Also adds predefined columns for the TABLE_TYPE
        /// </summary>
        private void ApplyTableTypeAffix(dynamic entity, string currentPhysicalName, TableTypeEntry tableTypeEntry)
        {
            try
            {
                // Check if the correct affix is already applied
                bool affixAlreadyApplied = tableTypeEntry.HasAffixApplied(currentPhysicalName);

                if (!affixAlreadyApplied)
                {
                    // First, remove any existing affix from other TABLE_TYPEs
                    string cleanName = TableTypeService.Instance.RemoveAllAffixes(currentPhysicalName);

                    if (cleanName != currentPhysicalName)
                    {
                        Log($"Removed old affix: '{currentPhysicalName}' -> '{cleanName}'");
                    }

                    // Now apply the new affix
                    string newPhysicalName = tableTypeEntry.ApplyAffix(cleanName);

                    if (newPhysicalName != currentPhysicalName)
                    {
                        Log($"Applying affix: '{currentPhysicalName}' -> '{newPhysicalName}'");

                        // Update the Physical_Name
                        int transId = _session.BeginNamedTransaction("ApplyTableTypeAffix");
                        try
                        {
                            entity.Properties("Physical_Name").Value = newPhysicalName;
                            _session.CommitTransaction(transId);

                            Log($"Successfully renamed table to '{newPhysicalName}'");

                            // Update snapshot with new name
                            string objectId = entity.ObjectId?.ToString() ?? "";
                            if (_entitySnapshots.ContainsKey(objectId))
                            {
                                _entitySnapshots[objectId].PhysicalName = newPhysicalName;
                            }
                        }
                        catch (Exception ex)
                        {
                            try { _session.RollbackTransaction(transId); }
                            catch (Exception rbEx) { Log($"ApplyAffix: Rollback failed: {rbEx.Message}"); }
                            Log($"Error applying affix: {ex.Message}");
                        }
                    }
                    else
                    {
                        Log($"No affix change needed for '{currentPhysicalName}'");
                    }
                }
                else
                {
                    Log($"Correct affix already applied to '{currentPhysicalName}'");
                }

                // Add predefined columns for this TABLE_TYPE
                AddPredefinedColumns(entity, tableTypeEntry);
            }
            catch (Exception ex)
            {
                Log($"ApplyTableTypeAffix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Add predefined columns to the entity based on TABLE_TYPE
        /// </summary>
        private void AddPredefinedColumns(dynamic entity, TableTypeEntry tableTypeEntry)
        {
            try
            {
                // Ensure PredefinedColumnService is loaded
                if (!PredefinedColumnService.Instance.IsLoaded)
                {
                    PredefinedColumnService.Instance.LoadPredefinedColumns();
                }

                var predefinedColumns = PredefinedColumnService.Instance.GetByTableTypeId(tableTypeEntry.Id);
                if (!predefinedColumns.Any())
                {
                    Log($"No predefined columns for TABLE_TYPE '{tableTypeEntry.Name}'");
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
                            catch (Exception ex) { Log($"AddPredefined: Error reading attr name: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error getting existing columns: {ex.Message}");
                }

                int addedCount = 0;
                foreach (var predefinedCol in predefinedColumns)
                {
                    // Skip if column already exists
                    if (existingColumnNames.Contains(predefinedCol.Name))
                    {
                        Log($"Column '{predefinedCol.Name}' already exists, skipping");
                        continue;
                    }

                    try
                    {
                        // Use ErwinUtilities.CreateAttribute which is already working
                        dynamic newAttribute = ErwinUtilities.CreateAttribute(_session, entity, predefinedCol.Name);

                        if (newAttribute != null)
                        {
                            // Set additional properties in separate transaction
                            int transId = _session.BeginNamedTransaction("SetAttributeProperties");
                            try
                            {
                                // Set physical name
                                try
                                {
                                    newAttribute.Properties("Physical_Name").Value = predefinedCol.Name;
                                }
                                catch (Exception ex) { Log($"AddPredefined: Failed to set Physical_Name for '{predefinedCol.Name}': {ex.Message}"); }

                                // Set data type
                                try
                                {
                                    newAttribute.Properties("Physical_Data_Type").Value = predefinedCol.DataType;
                                }
                                catch (Exception ex) { Log($"AddPredefined: Failed to set Physical_Data_Type for '{predefinedCol.Name}': {ex.Message}"); }

                                // Set nullability
                                try
                                {
                                    newAttribute.Properties("Null_Option_Type").Value = predefinedCol.Nullable ? 0 : 1;
                                }
                                catch (Exception ex) { Log($"AddPredefined: Failed to set Null_Option_Type for '{predefinedCol.Name}': {ex.Message}"); }

                                _session.CommitTransaction(transId);
                            }
                            catch (Exception ex)
                            {
                                try { _session.RollbackTransaction(transId); }
                                catch (Exception rbEx) { Log($"AddPredefined: Rollback failed: {rbEx.Message}"); }
                                Log($"AddPredefined: Transaction failed for '{predefinedCol.Name}': {ex.Message}");
                            }

                            addedCount++;
                            Log($"Added predefined column '{predefinedCol.Name}' ({predefinedCol.DataType})");
                        }
                        else
                        {
                            Log($"Failed to create attribute '{predefinedCol.Name}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"Error adding column '{predefinedCol.Name}': {ex.Message}");
                    }
                }

                if (addedCount > 0)
                {
                    Log($"Added {addedCount} predefined column(s) to entity");
                }
            }
            catch (Exception ex)
            {
                Log($"AddPredefinedColumns error: {ex.Message}");
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
            public string TableTypeValue { get; set; }
        }
    }
}
