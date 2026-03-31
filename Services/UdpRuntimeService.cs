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
        private bool _disposed;
        private bool _initialized;

        public event Action<string> OnLog;

        public bool IsInitialized => _initialized;

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

                bool depLoaded = UdpDependencyService.Instance.LoadDependencies();
                if (!depLoaded)
                {
                    Log($"UdpRuntime: Failed to load dependencies: {UdpDependencyService.Instance.LastError}");
                }
                else
                {
                    Log($"UdpRuntime: Loaded {UdpDependencyService.Instance.Count} dependency rules");
                }

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

                // Check which UDPs need to be created
                var missingDefs = new List<UdpDefinitionRuntime>();
                foreach (var def in definitions)
                {
                    string defOwnerClass = GetScapiOwnerClass(def.ObjectType);
                    if (defOwnerClass == null) continue; // Unknown object type

                    string fullName = $"{defOwnerClass}.Physical.{def.Name}";
                    if (existingUdpNames.Contains(fullName))
                    {
                        _verifiedUdps.Add(def.Name);
                        Log($"UdpRuntime: {fullName} already exists - skipping");
                    }
                    else
                    {
                        missingDefs.Add(def);
                    }
                }

                if (missingDefs.Count == 0)
                {
                    Log("UdpRuntime: All UDPs already exist - nothing to create");
                    return;
                }

                // Create missing UDPs (each with its own owner class based on ObjectType)
                int createdCount = 0;
                foreach (var def in missingDefs)
                {
                    string defOwnerClass = GetScapiOwnerClass(def.ObjectType);
                    if (defOwnerClass == null) continue;
                    if (CreateUdpInMetamodel(mmObjects, def, defOwnerClass, metamodelSession))
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
        private bool CreateUdpInMetamodel(dynamic mmObjects, UdpDefinitionRuntime def, string ownerClass, dynamic metamodelSession)
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
                    if (def.ListOptions.Count > 0)
                    {
                        string validValues = string.Join(",", def.ListOptions.Select(o => o.Value));
                        TrySetProperty(udpType, "tag_Udp_Values_List", validValues);
                        Log($"UdpRuntime: {fullName} List values = '{validValues}'");
                    }
                    else
                    {
                        Log($"UdpRuntime: WARNING - {fullName} is List type but has 0 list options in MC_UDP_LIST_OPTION!");
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
        /// If GlossaryMatchColumn and GlossaryValueColumn are set, looks up the value from Glossary.
        /// Otherwise returns the static DefaultValue.
        /// </summary>
        private string ResolveDefaultValue(UdpDefinitionRuntime def, string columnName)
        {
            // If glossary mapping is configured and we have a column name, resolve from Glossary
            if (!string.IsNullOrEmpty(def.GlossaryMatchColumn) &&
                !string.IsNullOrEmpty(def.GlossaryValueColumn) &&
                !string.IsNullOrEmpty(columnName))
            {
                var glossaryService = GlossaryService.Instance;
                if (glossaryService != null && glossaryService.IsLoaded)
                {
                    var entry = glossaryService.GetEntry(columnName);
                    if (entry != null)
                    {
                        string glossaryValue = GetGlossaryFieldValue(entry, def.GlossaryValueColumn);
                        if (!string.IsNullOrEmpty(glossaryValue))
                        {
                            Log($"UdpRuntime: Glossary resolved '{def.Name}' for column '{columnName}': {def.GlossaryValueColumn}='{glossaryValue}'");
                            return glossaryValue;
                        }
                    }
                }
            }

            // Fallback to static default
            return def.DefaultValue;
        }

        /// <summary>
        /// Get a field value from a GlossaryEntry by column name.
        /// </summary>
        private string GetGlossaryFieldValue(GlossaryEntry entry, string columnName)
        {
            switch (columnName?.ToUpper())
            {
                case "NAME": return entry.Name;
                case "DATA_TYPE": return entry.DataType;
                case "OWNER": return entry.Owner;
                case "DB_TYPE": return entry.DbType;
                case "KVKK": return entry.Kvkk ? "1" : "0";
                case "PCIDSS": return entry.Pcidss ? "1" : "0";
                case "CLASSIFICATION": return entry.Classification;
                case "COMMENT": return entry.Comment;
                default:
                    Log($"UdpRuntime: Unknown glossary column '{columnName}'");
                    return null;
            }
        }

        /// <summary>
        /// Evaluate dependency rules when a parent UDP value changes.
        /// Supports chained dependencies (child of one rule can be parent of another).
        /// </summary>
        public Dictionary<string, string> EvaluateDependencies(
            string parentUdpName,
            string parentValue,
            Dictionary<string, string> currentValues)
        {
            var childUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!UdpDependencyService.Instance.IsLoaded) return childUpdates;

            var rules = UdpDependencyService.Instance.GetByParent(parentUdpName);

            foreach (var rule in rules)
            {
                bool conditionMet = EvaluateCondition(rule.ConditionOperator, rule.ConditionValues, parentValue);

                if (conditionMet)
                {
                    childUpdates[rule.ChildUdpName] = rule.ChildValue;
                    Log($"UdpRuntime: Dependency triggered: {rule.ParentUdpName}='{parentValue}' -> {rule.ChildUdpName}='{rule.ChildValue}'");
                }
            }

            // Chained dependencies: check if any child is also a parent
            var chainedUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var update in childUpdates)
            {
                var chainedRules = UdpDependencyService.Instance.GetByParent(update.Key);
                if (chainedRules.Any())
                {
                    // Update currentValues with the new child value for chained evaluation
                    var updatedValues = new Dictionary<string, string>(currentValues, StringComparer.OrdinalIgnoreCase);
                    updatedValues[update.Key] = update.Value;

                    var chained = EvaluateChainedDependencies(update.Key, update.Value, updatedValues, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parentUdpName });
                    foreach (var c in chained)
                    {
                        chainedUpdates[c.Key] = c.Value;
                    }
                }
            }

            foreach (var c in chainedUpdates)
            {
                childUpdates[c.Key] = c.Value;
            }

            return childUpdates;
        }

        private Dictionary<string, string> EvaluateChainedDependencies(
            string parentUdpName,
            string parentValue,
            Dictionary<string, string> currentValues,
            HashSet<string> visited)
        {
            var updates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (visited.Contains(parentUdpName)) return updates; // Prevent circular
            visited.Add(parentUdpName);

            var rules = UdpDependencyService.Instance.GetByParent(parentUdpName);
            foreach (var rule in rules)
            {
                bool conditionMet = EvaluateCondition(rule.ConditionOperator, rule.ConditionValues, parentValue);
                if (conditionMet)
                {
                    updates[rule.ChildUdpName] = rule.ChildValue;
                    Log($"UdpRuntime: Chained dependency: {rule.ParentUdpName}='{parentValue}' -> {rule.ChildUdpName}='{rule.ChildValue}'");

                    // Recurse
                    var chained = EvaluateChainedDependencies(rule.ChildUdpName, rule.ChildValue, currentValues, visited);
                    foreach (var c in chained)
                        updates[c.Key] = c.Value;
                }
            }

            return updates;
        }

        private bool EvaluateCondition(string op, string conditionValues, string actualValue)
        {
            if (string.IsNullOrEmpty(op) || string.IsNullOrEmpty(conditionValues))
                return false;

            var values = conditionValues.Split(',').Select(v => v.Trim()).ToArray();

            switch (op)
            {
                case "Equals":
                    return string.Equals(actualValue, values[0], StringComparison.OrdinalIgnoreCase);
                case "NotEquals":
                    return !string.Equals(actualValue, values[0], StringComparison.OrdinalIgnoreCase);
                case "In":
                    return values.Any(v => string.Equals(v, actualValue, StringComparison.OrdinalIgnoreCase));
                case "NotIn":
                    return !values.Any(v => string.Equals(v, actualValue, StringComparison.OrdinalIgnoreCase));
                case "GreaterThan":
                    return decimal.TryParse(actualValue, out decimal gtActual) &&
                           decimal.TryParse(values[0], out decimal gtVal) && gtActual > gtVal;
                case "LessThan":
                    return decimal.TryParse(actualValue, out decimal ltActual) &&
                           decimal.TryParse(values[0], out decimal ltVal) && ltActual < ltVal;
                case "Between":
                    if (values.Length >= 2 &&
                        decimal.TryParse(actualValue, out decimal btActual) &&
                        decimal.TryParse(values[0], out decimal btMin) &&
                        decimal.TryParse(values[1], out decimal btMax))
                    {
                        return btActual >= btMin && btActual <= btMax;
                    }
                    return false;
                default:
                    return false;
            }
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
