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

        public UdpRuntimeService(dynamic session, dynamic scapi, dynamic currentModel)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _scapi = scapi ?? throw new ArgumentNullException(nameof(scapi));
            _currentModel = currentModel ?? throw new ArgumentNullException(nameof(currentModel));
        }

        /// <summary>
        /// Initialize: load all UDP definitions from the admin DB and apply
        /// the per-connect dependency-set cascade refresh to existing
        /// Property_Type entries.
        /// </summary>
        /// <remarks>
        /// As of 2026-05-16 (Phase 4 of the Admin -> Model UDP sync feature)
        /// this service no longer creates / updates / deletes UDPs by itself.
        /// That responsibility moved to <see cref="UdpSyncEngine"/>, which
        /// runs ahead of <see cref="Initialize"/> in
        /// <c>ModelConfigForm.InitializeModelServices</c> and obtains user
        /// consent via the sync dialog before touching the metamodel.
        ///
        /// What remains here is the runtime cascade: when a model UDP value
        /// changes, dependent List UDPs need their <c>tag_Udp_Values_List</c>
        /// recomputed from the currently-applicable cascade rule. That
        /// recompute is admin-diff-independent and must run on every
        /// connect, which is why it lives here next to the value-level
        /// <see cref="ApplyDefaults"/> / <see cref="WriteUdpValues"/> APIs.
        /// </remarks>
        /// <param name="preFetchedPropertyTypeNames">
        /// Legacy parameter from the silent ensure/drift era; ignored after
        /// the Phase 4 refactor. Kept on the signature so the caller in
        /// <c>ModelConfigForm</c> compiles unchanged until Phase 5 removes
        /// the argument.
        /// </param>
        public bool Initialize(HashSet<string> preFetchedPropertyTypeNames = null)
        {
            _ = preFetchedPropertyTypeNames; // intentionally unused; see remarks
            try
            {
                bool defLoaded = UdpDefinitionService.Instance.LoadDefinitions();
                if (!defLoaded)
                {
                    Log($"UdpRuntime: Failed to load definitions: {UdpDefinitionService.Instance.LastError}");
                    return false;
                }

                var objectTypes = UdpDefinitionService.Instance.GetLoadedObjectTypes().ToList();
                Log($"UdpRuntime: Loaded {UdpDefinitionService.Instance.Count} UDP definitions for [{string.Join(", ", objectTypes)}]");

                _initialized = true;

                // Apply runtime dependency-set cascade values. Independent of
                // admin diff; needs to run on every connect because the active
                // cascade depends on the current model UDP values.
                UpdateDependencySetListValues();

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
        /// Per erwin API Reference 15 (page 169, tag_Udp_Data_Type table):
        ///   1 = Integer, 2 = Text, 3 = Date, 4 = Command, 5 = Real, 6 = List.
        /// Default is Text (2) - the same fallback erwin itself documents
        /// ("Assumes the Text type if it is not specified.").
        ///
        /// Boolean has no native erwin datatype; the convention here is to
        /// store it as Text so values like 'True'/'False' remain readable.
        /// Admins who want a dropdown should pick List in MC_UDP_DEFINITION
        /// and supply 'True,False' as the list options instead.
        /// </summary>
        private int MapUdpTypeToErwinDataTypeId(string udpType)
        {
            switch (udpType?.ToLower())
            {
                case "int":
                case "integer":
                    return 1;
                case "text":
                    return 2;
                case "date":
                case "datetime":
                    return 3;
                case "command":
                    return 4;
                case "real":
                case "float":
                case "decimal":
                    return 5;
                case "list":
                    return 6;
                default:
                    return 2;
            }
        }

        /// <summary>
        /// Refresh <c>tag_Udp_Values_List</c> on every existing Property_Type
        /// whose dependency-set rule produces a non-empty option list for the
        /// current model UDP state. This is the runtime cascade: an admin
        /// might define List UDP "DOMAIN" whose available options depend on
        /// the value of model-level UDP "ENVIRONMENT" - flipping ENVIRONMENT
        /// from DEV to PROD changes which DOMAIN values are valid. The diff
        /// path in <see cref="UdpSyncEngine"/> handles static admin-side
        /// changes; this method handles the dynamic side that cannot be
        /// computed without reading the model.
        /// </summary>
        /// <remarks>
        /// Self-contained: opens its own level-1 metamodel session, walks
        /// Property_Type once, applies per-UDP transactions, closes the
        /// session. Safe to call from any connect path. Returns silently
        /// when no dependency-set service is configured.
        /// </remarks>
        public void UpdateDependencySetListValues()
        {
            if (_dependencySetService == null || !_dependencySetService.IsLoaded)
                return;
            // Short-circuit when no mappings are configured. Without this,
            // a fresh dev DB with 0 dep-sets still pays the metamodel
            // session-open + Property_Type Collect cost (~1.7 s against
            // a 1500-entry model, verified 2026-05-16) for nothing.
            if (_dependencySetService.MappingCount == 0)
            {
                Log("UdpRuntime.UpdateDependencySetListValues: no dep-set mappings configured - skipping metamodel walk");
                return;
            }

            var definitions = UdpDefinitionService.Instance.GetAll().ToList();
            if (definitions.Count == 0) return;

            dynamic metamodelSession = null;
            try
            {
                metamodelSession = _scapi.Sessions.Add();
                metamodelSession.Open(_currentModel, 1); // SCD_SL_M1

                dynamic mmObjects = metamodelSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                var existingByName = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                    foreach (dynamic pt in propertyTypes)
                    {
                        if (pt == null) continue;
                        string n;
                        try { n = pt.Name ?? ""; }
                        catch { continue; }
                        if (string.IsNullOrEmpty(n)) continue;
                        if (!existingByName.ContainsKey(n))
                        {
                            existingByName[n] = pt;
                            existingNames.Add(n);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"UdpRuntime.UpdateDependencySetListValues: Property_Type enumeration failed: {ex.Message}");
                    return;
                }

                // The cascade evaluator filters on the current model-level UDP
                // values. ReadModelUdpValues uses the existingNames set to find
                // the correct case-sensitive Property_Type paths.
                var modelUdpValues = ReadModelUdpValues(existingNames);
                if (modelUdpValues.Count > 0)
                    Log($"UdpRuntime: Model UDP values for cascade: {string.Join(", ", modelUdpValues.Select(kv => $"{kv.Key}='{kv.Value}'"))}");

                int updated = 0;
                foreach (var def in definitions)
                {
                    string ownerClass = GetScapiOwnerClass(def.ObjectType);
                    if (ownerClass == null) continue;
                    string fullName = $"{ownerClass}.Physical.{def.Name}";
                    if (!existingByName.TryGetValue(fullName, out var targetPt)) continue;

                    var depOptions = _dependencySetService.GetListUdpOptions(def.Name, modelUdpValues);
                    if (depOptions == null || depOptions.Count == 0) continue;

                    string validValues = string.Join(",", depOptions);

                    int transId = metamodelSession.BeginNamedTransaction($"UpdateListUDP_{def.Name}");
                    try
                    {
                        // Ensure the UDP is flagged as a List so the editor
                        // renders the dropdown. The sync engine should have
                        // already done this if the admin entry is List type,
                        // but we re-assert here defensively in case the
                        // sync was skipped (DB offline path).
                        TrySetProperty(targetPt, "tag_Udp_Data_Type", 6);
                        TrySetProperty(targetPt, "tag_Udp_Values_List", validValues);
                        metamodelSession.CommitTransaction(transId);
                        updated++;
                        Log($"UdpRuntime: {fullName} list values updated from dependency set ({depOptions.Count} items): {validValues}");
                    }
                    catch (Exception ex)
                    {
                        try { metamodelSession.RollbackTransaction(transId); }
                        catch (Exception rbEx) { Log($"UdpRuntime: dep-set rollback failed for {fullName}: {rbEx.Message}"); }
                        Log($"UdpRuntime: dep-set update failed for {fullName}: {ex.Message}");
                    }
                }

                if (updated > 0)
                    Log($"UdpRuntime.UpdateDependencySetListValues: applied to {updated} UDP(s)");
            }
            catch (Exception ex)
            {
                Log($"UdpRuntime.UpdateDependencySetListValues error: {ex.Message}");
            }
            finally
            {
                if (metamodelSession != null)
                {
                    try { metamodelSession.Close(); }
                    catch (Exception ex) { Log($"UdpRuntime: dep-set session close failed: {ex.Message}"); }
                }
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
                string path = $"{prefix}.{def.Name}";
                string val = "";
                try
                {
                    var prop = entity.Properties(path);
                    val = prop?.Value?.ToString() ?? "";
                }
                catch
                {
                    // erwin stores UDP values sparsely — Properties() throws when the
                    // entity has never had this UDP set. We MUST still track it as ""
                    // so a later write (e.g. user picking TABLE_TYPE=LOG via the property
                    // panel) is detected as a "" -> "LOG" diff in CheckForUdpValueChanges.
                    // Previously this catch swallowed silently and snapshot.UdpValues
                    // stayed empty, blocking the entire UDP-driven pipeline.
                }
                values[def.Name] = val;
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
                if (update.UpdateType == DependencyUpdateType.SetValue)
                {
                    childUpdates[update.UdpName] = update.Value ?? "";
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
            if (updates.Count == 0) return;

            // Filter out updates where value is already set (avoid redundant writes)
            var actualUpdates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in updates)
            {
                string existing = "";
                currentValues.TryGetValue(kvp.Key, out existing);
                if (!string.Equals(kvp.Value, existing ?? "", StringComparison.Ordinal))
                    actualUpdates[kvp.Key] = kvp.Value;
            }

            if (actualUpdates.Count > 0)
            {
                WriteUdpValues(entity, actualUpdates, objectType);
                Log($"UdpRuntime: Applied {actualUpdates.Count} dependency update(s) after '{changedUdpName}' changed to '{newValue}'");
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
