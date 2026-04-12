using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Orchestrator service for UDP runtime operations.
    /// Reads/writes UDP values from/to erwin SCAPI, applies defaults,
    /// evaluates dependencies, and runs validation.
    /// Uses metamodel session with Property_Type class to create UDPs.
    /// </summary>
    public class UdpRuntimeService : IDisposable
    {
        private readonly dynamic _session;
        private readonly dynamic _scapi;
        private readonly dynamic _currentModel;
        private DependencySetRuntimeService _dependencySetService;
        private bool _disposed;
        private bool _initialized;

        public event Action<string> OnLog;

        public bool IsInitialized => _initialized;

        public void SetDependencySetService(DependencySetRuntimeService service)
        {
            _dependencySetService = service;
        }

        /// <summary>
        /// Read current Model-level UDP values from erwin model root.
        /// Used for cascade filtering in dependency sets.
        /// </summary>
        /// <summary>
        /// Read model-level UDP values using actual Property_Type names from erwin metamodel.
        /// Uses existingUdpNames for correct case-sensitive property paths.
        /// </summary>
        public Dictionary<string, string> ReadModelUdpValues(HashSet<string> existingUdpNames = null)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return values;

                // Find Model.Physical.* entries from existing Property_Type names
                var modelPaths = (existingUdpNames ?? new HashSet<string>())
                    .Where(n => n.StartsWith("Model.Physical.", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                Log($"UdpRuntime.ReadModelUdpValues: Found {modelPaths.Count} Model.Physical.* Property_Type(s)");

                foreach (var path in modelPaths)
                {
                    try
                    {
                        // Use the EXACT name from erwin metamodel (correct case)
                        string val = root.Properties(path)?.Value?.ToString() ?? "";
                        string udpName = path.Substring("Model.Physical.".Length);

                        if (!string.IsNullOrEmpty(val))
                        {
                            values[udpName] = val;
                            Log($"UdpRuntime.ReadModelUdpValues: {udpName} = '{val}' (path='{path}')");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"UdpRuntime.ReadModelUdpValues: '{path}' error: {ex.Message}");
                    }
                }

                if (values.Count == 0)
                    Log($"UdpRuntime.ReadModelUdpValues: No model UDP values set");
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime.ReadModelUdpValues error: {ex.Message}");
            }
            return values;
        }

        // Track which UDPs have been verified/created in the erwin model
        private HashSet<string> _verifiedUdps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public UdpRuntimeService(dynamic session, dynamic scapi, dynamic currentModel)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            _currentModel = currentModel ?? throw new ArgumentNullException(nameof(currentModel));
        }

        /// <summary>
        /// Initialize: load ALL UDP definitions and dependency rules from DB,
        /// then ensure all UDPs exist in the erwin model via metamodel session.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // Load ALL object types (Table, Column, View, etc.)
                bool defLoaded = UdpDefinitionService.Instance.LoadDefinitions();
                if (!defLoaded)
                {
                    Log($"UdpRuntime: Failed to load definitions: {UdpDefinitionService.Instance.LastError}");
                    return false;
                }

                var objectTypes = UdpDefinitionService.Instance.GetLoadedObjectTypes().ToList();
                Log($"UdpRuntime: Loaded {UdpDefinitionService.Instance.Count} UDP definitions for [{string.Join(", ", objectTypes)}]");

                // Ensure all UDP definitions exist in the erwin model (all object types)
                EnsureUdpsExistInModel();

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime.Initialize error: {ex.Message}");
                return false;
            }
        }

        #region UDP Creation in erwin Model (Metamodel Session + Property_Type)

        /// <summary>
        /// Get the erwin SCAPI owner class name for a given object type.
        /// </summary>
        private string GetScapiOwnerClass(string objectType)
        {
            switch (objectType?.ToLower())
            {
                case "table": return "Entity";
                case "column": return "Attribute";
                case "view": return "View";
                case "procedure": return "Stored_Procedure";
                case "model": return "Model";
                case "subject area": return "Subject_Area";
                default:
                    Log($"UdpRuntime: Unknown object type '{objectType}' — skipping");
                    return null;
            }
        }

        /// <summary>
        /// Map admin UDP_TYPE to erwin metamodel tag_Udp_Data_Type integer.
        /// erwin data types: 1=Text, 2=Integer, 3=DateTime, 4=Float, 6=List
        /// </summary>
        private int MapUdpTypeToErwinDataTypeId(string udpType)
        {
            switch (udpType?.ToLower())
            {
                case "int": return 2;
                case "real": return 4;
                case "date": return 3;
                case "bool": return 2;  // Boolean stored as integer 0/1
                case "list": return 6;
                case "text":
                default: return 1;
            }
        }

        /// <summary>
        /// Ensure all UDP definitions from DB exist in the erwin model.
        /// Opens a separate metamodel session (level 1) to create Property_Type objects.
        /// Handles all object types (Table→Entity, Column→Attribute, etc.)
        /// </summary>
        private void EnsureUdpsExistInModel()
        {
            var definitions = UdpDefinitionService.Instance.GetAll().ToList();
            if (definitions.Count == 0) return;

            dynamic metamodelSession = null;
            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // 1 = SCD_SL_M1 = Metamodel level

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                // Collect all existing Property_Type names in one pass
                var existingUdpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        try { existingUdpNames.Add(pt.Name ?? ""); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Property_Type name read error: {ex.Message}"); }
                    }
                }
                catch (Exception ex)
                {
                    Log($"UdpRuntime: Metamodel Property_Type enumeration failed: {ex.Message}");
                }
                Log($"UdpRuntime: Found {existingUdpNames.Count} existing Property_Type entries");

                // Read current model-level UDP values for cascade filtering
                // Use existingUdpNames to find correct case for property paths
                var modelUdpValues = ReadModelUdpValues(existingUdpNames);
                if (modelUdpValues.Count > 0)
                    Log($"UdpRuntime: Model UDP values for cascade: {string.Join(", ", modelUdpValues.Select(kv => $"{kv.Key}='{kv.Value}'"))}");

                // Check which UDPs need to be created, and which existing List UDPs need value updates
                var missingDefs = new List<UdpDefinitionRuntime>();
                var existingListUdpsToUpdate = new List<(UdpDefinitionRuntime def, string fullName)>();

                foreach (var def in definitions)
                {
                    string defOwnerClass = GetScapiOwnerClass(def.ObjectType);
                    if (defOwnerClass == null) continue;

                    string fullName = $"{defOwnerClass}.Physical.{def.Name}";
                    if (existingUdpNames.Contains(fullName))
                    {
                        _verifiedUdps.Add(def.Name);

                        // Check if dependency set has options for this UDP (with cascade filter)
                        if (_dependencySetService != null && _dependencySetService.IsLoaded)
                        {
                            var depOptions = _dependencySetService.GetListUdpOptions(def.Name, modelUdpValues);
                            if (depOptions != null && depOptions.Count > 0)
                            {
                                existingListUdpsToUpdate.Add((def, fullName));
                                continue;
                            }
                        }

                        Log($"UdpRuntime: {fullName} already exists - skipping");
                    }
                    else
                    {
                        missingDefs.Add(def);
                    }
                }

                // Update existing List UDPs with dependency set values
                if (existingListUdpsToUpdate.Count > 0)
                {
                    UpdateExistingListUdps(mmObjects, mmRoot, existingListUdpsToUpdate, metamodelSession, modelUdpValues);
                }

                if (missingDefs.Count == 0 && existingListUdpsToUpdate.Count == 0)
                {
                    Log("UdpRuntime: All UDPs already exist - nothing to create");
                    return;
                }
                if (missingDefs.Count == 0)
                {
                    return;
                }

                // Create missing UDPs (each with its own owner class based on ObjectType)
                int createdCount = 0;
                foreach (var def in missingDefs)
                {
                    string defOwnerClass = GetScapiOwnerClass(def.ObjectType);
                    if (defOwnerClass == null) continue;
                    if (CreateUdpInMetamodel(mmObjects, def, defOwnerClass, metamodelSession, modelUdpValues))
                        createdCount++;
                }

                Log($"UdpRuntime: Created {createdCount}/{missingDefs.Count} UDP(s) in erwin model");
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime.EnsureUdpsExistInModel error: {ex.Message}");
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Metamodel session close error: {ex.Message}"); }
                }
            }
        }

        /// <summary>
        /// Create a single UDP in the erwin metamodel via Property_Type.
        /// Follows the same pattern as EnsureAllUdpsExist in ModelConfigForm.
        /// </summary>
        /// <summary>
        /// Update existing List UDPs with values from dependency set external tables.
        /// </summary>
        private void UpdateExistingListUdps(dynamic mmObjects, dynamic mmRoot, List<(UdpDefinitionRuntime def, string fullName)> udpsToUpdate, dynamic metamodelSession, Dictionary<string, string> modelUdpValues)
        {
            try
            {
                dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                foreach (var (def, fullName) in udpsToUpdate)
                {
                    try
                    {
                        // Find the existing Property_Type by name
                        dynamic targetPt = null;
                        foreach (dynamic pt in propertyTypes)
                        {
                            if (pt == null) continue;
                            try
                            {
                                string ptName = pt.Name ?? "";
                                if (ptName.Equals(fullName, StringComparison.OrdinalIgnoreCase))
                                {
                                    targetPt = pt;
                                    break;
                                }
                            }
                            catch { }
                        }

                        if (targetPt == null) continue;

                        var depOptions = _dependencySetService.GetListUdpOptions(def.Name, modelUdpValues);
                        if (depOptions == null || depOptions.Count == 0) continue;

                        string validValues = string.Join(",", depOptions);

                        int transId = metamodelSession.BeginNamedTransaction($"UpdateListUDP_{def.Name}");
                        try
                        {
                            // Ensure UDP type is List (dataTypeId=6) for dropdown display
                            TrySetProperty(targetPt, "tag_Udp_Data_Type", 6);
                            TrySetProperty(targetPt, "tag_Udp_Values_List", validValues);
                            metamodelSession.CommitTransaction(transId);
                            _verifiedUdps.Add(def.Name);
                            Log($"UdpRuntime: {fullName} updated as List from dependency set ({depOptions.Count} items): {validValues}");
                        }
                        catch (Exception ex)
                        {
                            try { metamodelSession.RollbackTransaction(transId); } catch { }
                            Log($"UdpRuntime: Failed to update {fullName}: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"UdpRuntime: UpdateExistingListUdps item error for {fullName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime: UpdateExistingListUdps error: {ex.Message}");
            }
        }

        private bool CreateUdpInMetamodel(dynamic mmObjects, UdpDefinitionRuntime def, string ownerClass, dynamic metamodelSession, Dictionary<string, string> modelUdpValues = null)
        {
            string fullName = $"{ownerClass}.Physical.{def.Name}";
            int transId = metamodelSession.BeginNamedTransaction($"CreateUDP_{def.Name}");
            try
            {
                dynamic udpType = mmObjects.Add("Property_Type");
                udpType.Properties("Name").Value = fullName;

                TrySetProperty(udpType, "tag_Udp_Owner_Type", ownerClass);
                TrySetProperty(udpType, "tag_Is_Physical", true);
                TrySetProperty(udpType, "tag_Is_Logical", false);

                int dataTypeId = MapUdpTypeToErwinDataTypeId(def.UdpType);
                TrySetProperty(udpType, "tag_Udp_Data_Type", dataTypeId);

                // For List-type UDPs, set valid values as comma-separated string
                if (def.UdpType?.Equals("List", StringComparison.OrdinalIgnoreCase) == true)
                {
                    string validValues = null;

                    if (def.ListOptions.Count > 0)
                    {
                        // Static list options from MC_UDP_LIST_OPTION
                        validValues = string.Join(",", def.ListOptions.Select(o => o.Value));
                    }

                    // Override/supplement with dependency set external table data
                    if (_dependencySetService != null && _dependencySetService.IsLoaded)
                    {
                        var depOptions = _dependencySetService.GetListUdpOptions(def.Name, modelUdpValues);
                        if (depOptions != null && depOptions.Count > 0)
                        {
                            validValues = string.Join(",", depOptions);
                            Log($"UdpRuntime: {fullName} List values from dependency set ({depOptions.Count} items)");
                        }
                    }

                    if (!string.IsNullOrEmpty(validValues))
                    {
                        TrySetProperty(udpType, "tag_Udp_Values_List", validValues);
                        Log($"UdpRuntime: {fullName} List values = '{validValues}'");
                    }
                    else
                    {
                        Log($"UdpRuntime: WARNING - {fullName} is List type but has no list options!");
                    }
                }

                // Set default value if specified
                if (!string.IsNullOrEmpty(def.DefaultValue))
                {
                    TrySetProperty(udpType, "tag_Udp_Default_Value", def.DefaultValue);
                }

                TrySetProperty(udpType, "tag_Order", "1");
                TrySetProperty(udpType, "tag_Is_Locally_Defined", true);

                metamodelSession.CommitTransaction(transId);
                _verifiedUdps.Add(def.Name);
                Log($"UdpRuntime: {fullName} UDP created (type={def.UdpType}, dataTypeId={dataTypeId})");
                return true;
            }
            catch (Exception ex)
            {
                try { metamodelSession.RollbackTransaction(transId); } catch (Exception rbEx) { Log($"UdpRuntime: Rollback failed: {rbEx.Message}"); }
                if (ex.Message.Contains("must be unique") || ex.Message.Contains("EBS-1057"))
                {
                    Log($"UdpRuntime: {fullName} already exists (unique constraint)");
                    _verifiedUdps.Add(def.Name);
                    return true; // Already exists — treat as success
                }
                Log($"UdpRuntime: Error creating {fullName}: {ex.Message}");
                return false;
            }
        }

        private void TrySetProperty(dynamic obj, string propertyName, object value)
        {
            try
            {
                obj.Properties(propertyName).Value = value;
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime: Could not set {propertyName}: {ex.Message}");
            }
        }

        #endregion

        /// <summary>
        /// Get the SCAPI property path prefix for a given object type.
        /// </summary>
        private string GetPropertyPathPrefix(string objectType)
        {
            switch (objectType?.ToLower())
            {
                case "table": return "Entity.Physical";
                case "column": return "Attribute.Physical";
                case "view": return "View.Physical";
                case "procedure": return "Stored_Procedure.Physical";
                case "model": return "Model.Physical";
                case "subject area": return "Subject_Area.Physical";
                default:
                    Log($"UdpRuntime: Unknown object type '{objectType}' for property path — skipping");
                    return null;
            }
        }

        /// <summary>
        /// Read current UDP values from an erwin object.
        /// </summary>
        /// <param name="entity">The erwin SCAPI object</param>
        /// <param name="objectType">Object type filter: "Table", "Column", etc.</param>
        public Dictionary<string, string> ReadUdpValues(dynamic entity, string objectType = "Table")
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!_initialized) return values;

            string prefix = GetPropertyPathPrefix(objectType);
            if (prefix == null) return values;

            foreach (var def in UdpDefinitionService.Instance.GetByObjectType(objectType))
            {
                try
                {
                    string path = $"{prefix}.{def.Name}";
                    var prop = entity.Properties(path);
                    string val = prop?.Value?.ToString() ?? "";
                    values[def.Name] = val;
                }
                catch
                {
                    // UDP not defined in erwin model — skip silently
                }
            }

            return values;
        }

        /// <summary>
        /// Apply default values to an erwin object for all UDP definitions.
        /// For Column UDPs with GlossaryMatchColumn/GlossaryValueColumn, resolves default from Glossary.
        /// Only writes if the current value is empty.
        /// </summary>
        /// <param name="entity">The erwin SCAPI object (entity or attribute)</param>
        /// <param name="objectType">Object type: "Table", "Column", etc.</param>
        /// <param name="columnName">Column physical name (used for Glossary mapping, null for non-column objects)</param>
        public void ApplyDefaults(dynamic entity, string objectType = "Table", string columnName = null)
        {
            if (!_initialized) return;

            string prefix = GetPropertyPathPrefix(objectType);
            if (prefix == null) return;
            var allDefs = UdpDefinitionService.Instance.GetByObjectType(objectType);

            var valuesToWrite = new Dictionary<string, string>();

            foreach (var def in allDefs)
            {
                try
                {
                    // Resolve default value: glossary mapping takes priority over static default
                    string resolvedDefault = ResolveDefaultValue(def, columnName);
                    if (string.IsNullOrEmpty(resolvedDefault)) continue;

                    string path = $"{prefix}.{def.Name}";
                    string currentValue = "";
                    try
                    {
                        currentValue = entity.Properties(path)?.Value?.ToString() ?? "";
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"ApplyDefaults: Read '{def.Name}' error: {ex.Message}"); }

                    if (string.IsNullOrEmpty(currentValue))
                    {
                        valuesToWrite[def.Name] = resolvedDefault;
                    }
                }
                catch (Exception ex)
                {
                    Log($"UdpRuntime.ApplyDefaults: Error checking '{def.Name}': {ex.Message}");
                }
            }

            if (valuesToWrite.Count > 0)
            {
                WriteUdpValues(entity, valuesToWrite, objectType);
                Log($"UdpRuntime: Applied {valuesToWrite.Count} default value(s) for {objectType}");

                // After setting defaults, evaluate dependencies for each default
                var currentValues = ReadUdpValues(entity, objectType);
                var allUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var kvp in valuesToWrite)
                {
                    var depUpdates = EvaluateDependencies(kvp.Key, kvp.Value, currentValues);
                    foreach (var update in depUpdates)
                    {
                        allUpdates[update.Key] = update.Value;
                        currentValues[update.Key] = update.Value;
                    }
                }

                if (allUpdates.Count > 0)
                {
                    WriteUdpValues(entity, allUpdates, objectType);
                    Log($"UdpRuntime: Applied {allUpdates.Count} dependency-driven default(s)");
                }
            }
        }

        /// <summary>
        /// Resolve the default value for a UDP definition.
        /// For Column UDPs, checks glossary mapping (DG_TABLE_MAPPING_COLUMN) first.
        /// Falls back to static DefaultValue.
        /// </summary>
        private string ResolveDefaultValue(UdpDefinitionRuntime def, string columnName)
        {
            // Check glossary for dynamic value (Column UDPs only)
            if (!string.IsNullOrEmpty(columnName))
            {
                var glossaryService = GlossaryService.Instance;
                if (glossaryService != null && glossaryService.IsLoaded)
                {
                    var udpValues = glossaryService.GetUdpValues(columnName);
                    if (udpValues != null)
                    {
                        // Try exact match first, then prefixed variants: [UDP] Name, plain Name
                        string glossaryValue = null;
                        if (udpValues.TryGetValue(def.Name, out glossaryValue) && !string.IsNullOrEmpty(glossaryValue))
                        { }
                        else if (udpValues.TryGetValue($"[UDP] {def.Name}", out glossaryValue) && !string.IsNullOrEmpty(glossaryValue))
                        { }

                        if (!string.IsNullOrEmpty(glossaryValue))
                        {
                            Log($"UdpRuntime: Glossary resolved '{def.Name}' for column '{columnName}': '{glossaryValue}'");
                            return glossaryValue;
                        }
                    }
                }
            }

            // Fallback to static default
            return def.DefaultValue;
        }

        /// <summary>
        /// Evaluate dependency rules when a parent UDP value changes.
        /// Uses DependencySetRuntimeService for set-based cascading dependencies.
        /// </summary>
        public Dictionary<string, string> EvaluateDependencies(
            string parentUdpName,
            string parentValue,
            Dictionary<string, string> currentValues)
        {
            var childUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (_dependencySetService == null || !_dependencySetService.IsLoaded)
                return childUpdates;

            var updates = _dependencySetService.EvaluateUdpChange(parentUdpName, parentValue, currentValues);

            foreach (var update in updates)
            {
                if (update.UpdateType == DependencyUpdateType.SetValue && !string.IsNullOrEmpty(update.Value))
                {
                    childUpdates[update.UdpName] = update.Value;
                }
                else if (update.UpdateType == DependencyUpdateType.SetListOptions && update.ListOptions != null)
                {
                    // For list options, set first value as default if available
                    if (update.ListOptions.Count > 0)
                        childUpdates[update.UdpName] = update.ListOptions[0];
                }
            }

            return childUpdates;
        }

        /// <summary>
        /// Write UDP values to an erwin object within a single transaction.
        /// </summary>
        public void WriteUdpValues(dynamic entity, Dictionary<string, string> values, string objectType = "Table")
        {
            if (values == null || values.Count == 0) return;

            string prefix = GetPropertyPathPrefix(objectType);
            if (prefix == null) return;
            int transId = _session.BeginNamedTransaction("WriteUdpValues");

            try
            {
                foreach (var kvp in values)
                {
                    try
                    {
                        string path = $"{prefix}.{kvp.Key}";
                        entity.Properties(path).Value = kvp.Value;
                    }
                    catch (Exception ex)
                    {
                        Log($"UdpRuntime.WriteUdpValues: Failed to set '{kvp.Key}' = '{kvp.Value}': {ex.Message}");
                    }
                }

                _session.CommitTransaction(transId);
            }
            catch (Exception ex)
            {
                try { _session.RollbackTransaction(transId); }
                catch (Exception rbEx) { Log($"UdpRuntime: Rollback failed: {rbEx.Message}"); }
                Log($"UdpRuntime.WriteUdpValues transaction error: {ex.Message}");
            }
        }

        /// <summary>
        /// Validate UDP values on an entity before save.
        /// </summary>
        public List<UdpValidationResult> ValidateBeforeSave(dynamic entity, string operation, string objectType = "Table")
        {
            if (!_initialized)
                return new List<UdpValidationResult>();

            var values = ReadUdpValues(entity, objectType);
            return UdpValidationEngine.ValidateAll(objectType, values, operation);
        }

        /// <summary>
        /// Full processing flow for a new or updated entity:
        /// 1. Apply defaults (if new)
        /// 2. Evaluate dependencies
        /// 3. Validate
        /// Returns validation results (empty if all valid).
        /// </summary>
        public List<UdpValidationResult> ProcessEntityUdps(dynamic entity, string operation, string objectType = "Table")
        {
            if (!_initialized)
                return new List<UdpValidationResult>();

            if (operation.Equals("Create", StringComparison.OrdinalIgnoreCase))
            {
                ApplyDefaults(entity, objectType);
            }

            return ValidateBeforeSave(entity, operation, objectType);
        }

        /// <summary>
        /// Handle a specific UDP value change: evaluate dependencies and write child updates.
        /// </summary>
        public void HandleUdpValueChange(dynamic entity, string changedUdpName, string newValue, string objectType = "Table")
        {
            if (!_initialized) return;

            var currentValues = ReadUdpValues(entity, objectType);
            currentValues[changedUdpName] = newValue;

            var updates = EvaluateDependencies(changedUdpName, newValue, currentValues);
            if (updates.Count > 0)
            {
                WriteUdpValues(entity, updates, objectType);
                Log($"UdpRuntime: Applied {updates.Count} dependency update(s) after '{changedUdpName}' changed to '{newValue}'");
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
        }
    }
}
