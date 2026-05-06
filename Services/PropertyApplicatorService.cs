using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.MetaAdmin.Shared.Data;
using EliteSoft.MetaAdmin.Shared.Data.Entities;
using EliteSoft.MetaAdmin.Shared.Services;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.Erwin.AddIn.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads model standards from DB (MC_ tables) and applies them to erwin model objects.
    /// Detects model's target DB platform via SCAPI and loads only matching property definitions.
    /// </summary>
    public class PropertyApplicatorService : IDisposable
    {
        private readonly dynamic _session;
        private readonly IPropertyMetadataService _metadataService;
        private bool _disposed;

        // Cached data — after the schema rename, scoping is by (CONFIG_ID, DBMS_VERSION_ID)
        // resolved up-front in ConfigContextService. Old MC_PLATFORM detection from
        // Target_Server / MODEL_PATH UDP / MODEL.PATH lookup is gone.
        private int _dbmsVersionId;
        private int _configId;
        private List<PropertyDef> _tablePropertyDefs;
        private Dictionary<int, string> _modelStandardMap; // PropertyDefId -> Value
        private List<QuestionDef> _questions; // Question definitions for this DBMS_VERSION+TABLE
        private int _tableObjectTypeId;

        public event Action<string> OnLog;

        public PropertyApplicatorService(dynamic session, IPropertyMetadataService metadataService)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        }

        #region Initialization

        /// <summary>
        /// Initialize: read CONFIG_ID + DBMS_VERSION_ID from ConfigContextService,
        /// load property defs / standards / questions scoped on those.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    Log("PropertyApplicator: ConfigContext not initialized — aborting");
                    return false;
                }
                _configId = ctx.ActiveConfigId;
                _dbmsVersionId = ctx.DbmsVersionId ?? 0;
                if (_dbmsVersionId <= 0)
                {
                    Log("PropertyApplicator: CONFIG.DBMS_VERSION_ID is null — cannot scope MC_PROPERTY_DEF; admin must pick a DBMS version on the config");
                    return false;
                }
                Log($"PropertyApplicator: config={_configId}, dbms_version={_dbmsVersionId}");

                // TABLE object type
                List<ObjectType> objectTypes;
                using (AddinLogger.BeginScope("metadataService.GetObjectTypes"))
                    objectTypes = _metadataService.GetObjectTypes();
                var tableType = objectTypes.FirstOrDefault(o => o.Name == "TABLE");
                if (tableType == null)
                {
                    Log("PropertyApplicator: TABLE object type not found in DB");
                    return false;
                }
                _tableObjectTypeId = tableType.Id;

                // Property definitions for this DBMS version + TABLE (no CONFIG_ID filter
                // — MC_PROPERTY_DEF is global after the rename; the only scope key is
                // (DBMS_VERSION_ID, OBJECT_TYPE_ID), with DBMS_VERSION_ID NULL meaning
                // "applies to any DBMS").
                using (AddinLogger.BeginScope($"metadataService.GetPropertyDefs(dbms={_dbmsVersionId},obj={tableType.Id})"))
                    _tablePropertyDefs = _metadataService.GetPropertyDefs(_dbmsVersionId, tableType.Id);
                Log($"PropertyApplicator: Loaded {_tablePropertyDefs.Count} property definitions for DBMS_VERSION={_dbmsVersionId} TABLE");

                // Model standards — these stay per-config (MC_MODEL_STANDARD.CONFIG_ID).
                List<ModelStandard> standards;
                using (AddinLogger.BeginScope($"metadataService.GetModelStandards(config={_configId})"))
                    standards = _metadataService.GetModelStandards(_configId);
                Log($"PropertyApplicator: Raw standards from DB for CONFIG_ID={_configId}: {standards.Count} record(s)");
                foreach (var s in standards)
                {
                    var matchingDef = _tablePropertyDefs.FirstOrDefault(pd => pd.Id == s.PropertyDefId);
                    Log($"  Standard: PropertyDefId={s.PropertyDefId}, Value='{s.Value}', MatchesDef={matchingDef != null}{(matchingDef != null ? $" ({matchingDef.PropertyCode})" : "")}");
                }

                Log($"PropertyApplicator: Table PropertyDef IDs: [{string.Join(", ", _tablePropertyDefs.Select(pd => $"{pd.Id}:{pd.PropertyCode}"))}]");

                _modelStandardMap = standards
                    .Where(s => _tablePropertyDefs.Any(pd => pd.Id == s.PropertyDefId))
                    .ToDictionary(s => s.PropertyDefId, s => s.Value);
                Log($"PropertyApplicator: Loaded {_modelStandardMap.Count} model standards (after filter)");

                // Question definitions for this DBMS version + TABLE
                using (AddinLogger.BeginScope($"metadataService.GetQuestions(dbms={_dbmsVersionId},obj={tableType.Id})"))
                    _questions = _metadataService.GetQuestions(_dbmsVersionId, tableType.Id);
                Log($"PropertyApplicator: Loaded {_questions.Count} question(s) for DBMS_VERSION={_dbmsVersionId} TABLE");

                return true;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: Initialize error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Apply Standards to Entity

        /// <summary>
        /// Apply properties to a new entity (table).
        /// Flow: 1) Show question wizard (if questions exist) → 2) Apply question rule values
        ///       → 3) Fill remaining from model standards (fallback)
        /// Priority: Question rules > Model standards
        /// </summary>
        public void ApplyStandardsToEntity(dynamic entity, string physicalName)
        {
            if (_tablePropertyDefs == null)
                return;

            try
            {
                // Merged property values: PropertyDefId -> Value
                var mergedValues = new Dictionary<int, string>();

                // 1. Start with model standards as baseline
                if (_modelStandardMap != null)
                {
                    foreach (var kvp in _modelStandardMap)
                        mergedValues[kvp.Key] = kvp.Value;
                }

                // 2. Show question wizard if questions exist — answers override standards
                if (_questions != null && _questions.Count > 0)
                {
                    Log($"PropertyApplicator: Showing question wizard for '{physicalName}' ({_questions.Count} question(s))");

                    try
                    {
                        using (var wizard = new QuestionWizardForm(physicalName, _questions, _session, entity))
                        {
                            var result = wizard.ShowDialog();

                            if (result == DialogResult.OK && wizard.PropertyValues.Count > 0)
                            {
                                Log($"PropertyApplicator: Wizard completed — {wizard.PropertyValues.Count} property value(s) from answers");

                                // Override standards with question rule values
                                foreach (var kvp in wizard.PropertyValues)
                                {
                                    mergedValues[kvp.Key] = kvp.Value;
                                    var def = _tablePropertyDefs.FirstOrDefault(d => d.Id == kvp.Key);
                                    Log($"  Question rule: {def?.PropertyCode ?? kvp.Key.ToString()} = '{kvp.Value}'");
                                }
                            }
                            else
                            {
                                Log("PropertyApplicator: Wizard cancelled or no values — using model standards only");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"PropertyApplicator: Wizard error: {ex.Message}");
                    }
                }

                if (mergedValues.Count == 0)
                {
                    Log($"PropertyApplicator: No values to apply for '{physicalName}'");
                    return;
                }

                // 3. Apply merged values to model
                //    Two passes: first standard properties, then compound properties (PARTITIONED)
                //    This ensures PhysicalStorage etc. are created before partition attempts
                int applied = 0;
                PropertyDef deferredPartitionDef = null;
                string deferredPartitionValue = null;

                foreach (var def in _tablePropertyDefs)
                {
                    if (!mergedValues.TryGetValue(def.Id, out var value))
                        continue;

                    // Check dependency: skip if parent property condition not met
                    if (def.DependsOnPropertyDefId.HasValue)
                    {
                        if (mergedValues.TryGetValue(def.DependsOnPropertyDefId.Value, out var parentValue))
                        {
                            if (!string.Equals(parentValue, def.DependsOnValue, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        else
                        {
                            continue;
                        }
                    }

                    // Defer PARTITIONED to second pass (compound operation, must run last)
                    if (def.PropertyCode == "PARTITIONED")
                    {
                        deferredPartitionDef = def;
                        deferredPartitionValue = value;
                        continue;
                    }

                    bool success = ApplyPropertyToModel(entity, physicalName, def, value, mergedValues);
                    if (success) applied++;
                }

                // Second pass: apply deferred partition creation
                if (deferredPartitionDef != null && deferredPartitionValue != null)
                {
                    bool success = ApplyPropertyToModel(entity, physicalName, deferredPartitionDef, deferredPartitionValue, mergedValues);
                    if (success) applied++;
                }

                if (applied > 0)
                    Log($"PropertyApplicator: Applied {applied} property value(s) to '{physicalName}'");
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyStandardsToEntity error for '{physicalName}': {ex.Message}");
            }
        }

        /// <summary>
        /// Apply a single property to the erwin model.
        /// Maps PropertyCode to the correct erwin SCAPI path.
        /// </summary>
        private bool ApplyPropertyToModel(dynamic entity, string physicalName, PropertyDef def, string value, Dictionary<int, string> mergedValues = null)
        {
            try
            {
                switch (def.PropertyCode)
                {
                    case "LOGGING":
                        return ApplyLogging(entity, physicalName, value);

                    case "COMPRESSION":
                        return ApplyCompression(entity, physicalName, value);

                    case "TABLESPACE_NAME":
                        return ApplyTablespaceName(entity, physicalName, value);

                    case "PARALLEL_ENABLED":
                        return ApplyParallelEnabled(entity, physicalName, value);

                    case "PARALLEL_DEGREE":
                        return ApplyParallelDegree(entity, physicalName, value);

                    case "PARTITIONED":
                        return ApplyPartitioned(entity, physicalName, value, mergedValues);

                    case "PARTITION_TYPE":
                        // Applied as part of PARTITIONED — skip standalone
                        return false;

                    case "PARTITION_KEY":
                        // Applied as part of PARTITIONED — skip standalone
                        return false;

                    case "PARTITION_TBS_RULE":
                        Log($"PropertyApplicator: PARTITION_TBS_RULE='{value}' noted for '{physicalName}'");
                        return true;

                    default:
                        Log($"PropertyApplicator: Unknown property code '{def.PropertyCode}'");
                        return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyPropertyToModel '{def.PropertyCode}' error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Oracle Property Mappings

        /// <summary>
        /// LOGGING → Entity → Oracle_Physical_Object_Storage → Oracle_Logging
        /// erwin action: Creates Physical Storage Object, sets Oracle_Logging and Oracle_Data_Storage_Type
        /// </summary>
        private bool ApplyLogging(dynamic entity, string physicalName, string value)
        {
            try
            {
                // Map MC value to erwin value
                string erwinValue = value.ToUpperInvariant() switch
                {
                    "LOGGING" => "Logging",
                    "NOLOGGING" => "Nologging",
                    _ => null
                };
                if (erwinValue == null) return false;

                dynamic modelObjects = _session.ModelObjects;

                // Find or create Oracle_Physical_Object_Storage for this entity
                dynamic storage = FindOrCreatePhysicalStorage(entity, physicalName, modelObjects);
                if (storage == null) return false;

                // Set Oracle_Logging
                int transId = _session.BeginNamedTransaction("SetLogging");
                try
                {
                    storage.Properties("Oracle_Logging").Value = erwinValue;
                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Set LOGGING='{erwinValue}' on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: SetLogging transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyLogging error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// COMPRESSION → Entity → Oracle_Physical_Object_Storage → Oracle_Compression
        /// </summary>
        private bool ApplyCompression(dynamic entity, string physicalName, string value)
        {
            try
            {
                // Map MC value to erwin value
                string erwinValue = value.ToUpperInvariant() switch
                {
                    "NOCOMPRESS" => "Nocompress",
                    "BASIC" => "Basic",
                    "OLTP" => "OLTP",
                    "QUERY_HIGH" => "Query High",
                    "QUERY_LOW" => "Query Low",
                    "ARCHIVE_HIGH" => "Archive High",
                    "ARCHIVE_LOW" => "Archive Low",
                    _ => null
                };
                if (erwinValue == null) return false;

                dynamic modelObjects = _session.ModelObjects;
                dynamic storage = FindOrCreatePhysicalStorage(entity, physicalName, modelObjects);
                if (storage == null) return false;

                int transId = _session.BeginNamedTransaction("SetCompression");
                try
                {
                    storage.Properties("Oracle_Compression").Value = erwinValue;
                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Set COMPRESSION='{erwinValue}' on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: SetCompression transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyCompression error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// TABLESPACE_NAME → Entity → Oracle_Physical_Object_Storage → Oracle_Tablespace_Name
        /// </summary>
        private bool ApplyTablespaceName(dynamic entity, string physicalName, string value)
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic storage = FindOrCreatePhysicalStorage(entity, physicalName, modelObjects);
                if (storage == null) return false;

                int transId = _session.BeginNamedTransaction("SetTablespaceName");
                try
                {
                    storage.Properties("Oracle_Tablespace_Name").Value = value;
                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Set TABLESPACE_NAME='{value}' on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: SetTablespaceName transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyTablespaceName error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// PARALLEL_ENABLED → Entity → Oracle_Physical_Object_Storage → Oracle_Parallel
        /// </summary>
        private bool ApplyParallelEnabled(dynamic entity, string physicalName, string value)
        {
            try
            {
                bool enabled = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
                string erwinValue = enabled ? "Parallel" : "No Parallel";

                dynamic modelObjects = _session.ModelObjects;
                dynamic storage = FindOrCreatePhysicalStorage(entity, physicalName, modelObjects);
                if (storage == null) return false;

                int transId = _session.BeginNamedTransaction("SetParallel");
                try
                {
                    storage.Properties("Oracle_Parallel").Value = erwinValue;
                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Set PARALLEL='{erwinValue}' on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: SetParallel transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyParallelEnabled error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// PARALLEL_DEGREE → Entity → Oracle_Physical_Object_Storage → Oracle_Parallel_Degree
        /// </summary>
        private bool ApplyParallelDegree(dynamic entity, string physicalName, string value)
        {
            try
            {
                if (!int.TryParse(value, out int degree)) return false;

                dynamic modelObjects = _session.ModelObjects;
                dynamic storage = FindOrCreatePhysicalStorage(entity, physicalName, modelObjects);
                if (storage == null) return false;

                int transId = _session.BeginNamedTransaction("SetParallelDegree");
                try
                {
                    storage.Properties("Oracle_Parallel_Degree").Value = degree;
                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Set PARALLEL_DEGREE={degree} on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: SetParallelDegree transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyParallelDegree error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// PARTITIONED → Creates Oracle_Entity_Partition under entity.
        /// Compound operation: also reads PARTITION_TYPE and PARTITION_KEY from mergedValues.
        /// SCAPI hierarchy: Entity → Oracle_Entity_Partition → Partition_Column
        ///   Oracle_Entity_Partition properties: Oracle_Entity_Partition_Type (Range/Hash/List)
        ///   Partition_Column properties: Attribute_Ref, Partition_Columns_Order_Ref
        /// </summary>
        private bool ApplyPartitioned(dynamic entity, string physicalName, string value, Dictionary<int, string> mergedValues)
        {
            try
            {
                // Only apply when PARTITIONED = true/yes
                bool isPartitioned = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "1", StringComparison.OrdinalIgnoreCase);

                if (!isPartitioned)
                {
                    Log($"PropertyApplicator: PARTITIONED='{value}' (not partitioned) — skipping for '{physicalName}'");
                    return false;
                }

                // Look up companion values from mergedValues
                string partitionType = null;
                string partitionKey = null;

                if (mergedValues != null)
                {
                    foreach (var def in _tablePropertyDefs)
                    {
                        if (def.PropertyCode == "PARTITION_TYPE" && mergedValues.TryGetValue(def.Id, out var ptVal))
                            partitionType = ptVal;
                        else if (def.PropertyCode == "PARTITION_KEY" && mergedValues.TryGetValue(def.Id, out var pkVal))
                            partitionKey = pkVal;
                    }
                }

                if (string.IsNullOrEmpty(partitionType))
                {
                    Log($"PropertyApplicator: PARTITIONED=true but PARTITION_TYPE is missing for '{physicalName}' — cannot create partition");
                    return false;
                }

                // Map MC partition type to erwin Oracle_Entity_Partition_Type value
                string erwinPartitionType = partitionType.ToUpperInvariant() switch
                {
                    "RANGE" => "Range",
                    "HASH" => "Hash",
                    "LIST" => "List",
                    "RANGE-RANGE" => "Range-Range",
                    "RANGE-HASH" => "Range-Hash",
                    "RANGE-LIST" => "Range-List",
                    "LIST-RANGE" => "List-Range",
                    "LIST-HASH" => "List-Hash",
                    "LIST-LIST" => "List-List",
                    "HASH-HASH" => "Hash-Hash",
                    "HASH-LIST" => "Hash-List",
                    "HASH-RANGE" => "Hash-Range",
                    _ => partitionType // Use as-is if no mapping
                };

                Log($"PropertyApplicator: Creating partition on '{physicalName}': Type={erwinPartitionType}, Key={partitionKey ?? "(none)"}");

                dynamic modelObjects = _session.ModelObjects;

                // Check if partition already exists
                try
                {
                    dynamic existingPartitions = modelObjects.Collect(entity, "Oracle_Entity_Partition");
                    if (existingPartitions != null && existingPartitions.Count > 0)
                    {
                        Log($"PropertyApplicator: Partition already exists on '{physicalName}' (count={existingPartitions.Count}) — skipping");
                        return false;
                    }
                }
                catch { }

                // Create Oracle_Entity_Partition under entity
                int transId = _session.BeginNamedTransaction("CreatePartition");
                try
                {
                    dynamic entityChildren = modelObjects.Collect(entity);
                    dynamic partition = entityChildren.Add("Oracle_Entity_Partition");

                    if (partition == null)
                    {
                        _session.RollbackTransaction(transId);
                        Log($"PropertyApplicator: Failed to create Oracle_Entity_Partition for '{physicalName}'");
                        return false;
                    }

                    // Set partition type
                    partition.Properties("Oracle_Entity_Partition_Type").Value = erwinPartitionType;
                    Log($"PropertyApplicator: Set Oracle_Entity_Partition_Type='{erwinPartitionType}'");

                    // Set partition column if PARTITION_KEY is specified
                    if (!string.IsNullOrEmpty(partitionKey))
                    {
                        // Find the attribute (column) by physical name within this entity
                        string attributeRef = FindAttributeRef(entity, partitionKey, modelObjects);

                        if (!string.IsNullOrEmpty(attributeRef))
                        {
                            // Create Partition_Column child under the partition
                            dynamic partitionChildren = modelObjects.Collect(partition);
                            dynamic partitionColumn = partitionChildren.Add("Partition_Column");

                            if (partitionColumn != null)
                            {
                                partitionColumn.Properties("Attribute_Ref").Value = attributeRef;
                                partitionColumn.Properties("Partition_Columns_Order_Ref").Value = partitionKey;
                                Log($"PropertyApplicator: Created Partition_Column: Attribute_Ref='{attributeRef}', Key='{partitionKey}'");
                            }
                            else
                            {
                                Log($"PropertyApplicator: Warning — could not create Partition_Column for key '{partitionKey}'");
                            }
                        }
                        else
                        {
                            Log($"PropertyApplicator: Warning — attribute '{partitionKey}' not found on entity '{physicalName}', partition created without key column");
                        }
                    }

                    _session.CommitTransaction(transId);
                    Log($"PropertyApplicator: Successfully created partition on '{physicalName}'");
                    return true;
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: CreatePartition transaction error: {ex.Message}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ApplyPartitioned error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find an attribute's reference ID within an entity by its physical column name.
        /// Used to set Partition_Column.Attribute_Ref.
        /// </summary>
        private string FindAttributeRef(dynamic entity, string columnPhysicalName, dynamic modelObjects)
        {
            try
            {
                dynamic attributes = modelObjects.Collect(entity, "Attribute");
                if (attributes == null) return null;

                for (int i = 0; i < attributes.Count; i++)
                {
                    dynamic attr = attributes.Item(i);
                    try
                    {
                        string physName = attr.Properties("Physical_Name").Value?.ToString();
                        if (string.Equals(physName, columnPhysicalName, StringComparison.OrdinalIgnoreCase))
                        {
                            // Return the attribute's Long_Id as reference
                            string longId = attr.Properties("Long_Id").Value?.ToString();
                            if (!string.IsNullOrEmpty(longId))
                                return longId;

                            // Fallback: try Name
                            return attr.Properties("Name").Value?.ToString();
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: FindAttributeRef error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Physical Storage Object Helper

        /// <summary>
        /// Find existing Oracle_Physical_Object_Storage for entity, or create one.
        /// In erwin, this is a child object of Entity that holds Oracle physical properties.
        /// </summary>
        private dynamic FindOrCreatePhysicalStorage(dynamic entity, string physicalName, dynamic modelObjects)
        {
            try
            {
                // Try to find existing storage object
                try
                {
                    dynamic storageCollection = modelObjects.Collect(entity, "Oracle_Physical_Object_Storage");
                    if (storageCollection != null && storageCollection.Count > 0)
                    {
                        return storageCollection.Item(0);
                    }
                }
                catch { }

                // Create new storage object
                int transId = _session.BeginNamedTransaction("CreatePhysicalStorage");
                try
                {
                    dynamic entityChildren = modelObjects.Collect(entity);
                    dynamic storage = entityChildren.Add("Oracle_Physical_Object_Storage");

                    if (storage != null)
                    {
                        // Set storage name (convention: tableName_Storage)
                        try { storage.Properties("Name").Value = $"{physicalName}_Storage"; } catch { }

                        // Set Data_Storage_Type to Regular (required by erwin)
                        try { entity.Properties("Oracle_Data_Storage_Type").Value = "Regular"; } catch { }

                        _session.CommitTransaction(transId);
                        Log($"PropertyApplicator: Created Physical Storage Object for '{physicalName}'");
                        return storage;
                    }
                    else
                    {
                        _session.RollbackTransaction(transId);
                        Log($"PropertyApplicator: Failed to create Physical Storage for '{physicalName}'");
                        return null;
                    }
                }
                catch (Exception ex)
                {
                    try { _session.RollbackTransaction(transId); } catch { }
                    Log($"PropertyApplicator: CreatePhysicalStorage transaction error: {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: FindOrCreatePhysicalStorage error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Public Properties

        public int ConfigId => _configId;
        public int DbmsVersionId => _dbmsVersionId;
        public int StandardCount => _modelStandardMap?.Count ?? 0;
        public int QuestionCount => _questions?.Count ?? 0;
        public List<PropertyDef> TablePropertyDefs => _tablePropertyDefs;
        public bool IsInitialized => _configId > 0 && _tablePropertyDefs != null;

        /// <summary>
        /// Check if a CONFIG_PROPERTY (KEY,VALUE) row exists for the active config and is
        /// enabled. Values "Yes", "True", or "1" are considered enabled. The "All Models"
        /// (ID=1) fallback is gone post-rename — schema requires a per-config row.
        /// </summary>
        public bool IsPropertyEnabled(string propertyKey)
        {
            try
            {
                var bootstrapService = new RegistryBootstrapService();
                var config = bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured) return false;
                if (_configId <= 0) return false;

                using (var context = new RepoDbContext(config))
                {
                    var prop = context.ConfigProperties
                        .FirstOrDefault(p => p.ConfigId == _configId && p.Key == propertyKey);
                    if (prop == null) return false;

                    return prop.Value.Equals("Yes", StringComparison.OrdinalIgnoreCase)
                        || prop.Value.Equals("True", StringComparison.OrdinalIgnoreCase)
                        || prop.Value == "1";
                }
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: IsPropertyEnabled({propertyKey}) error: {ex.Message}");
                return false;
            }
        }

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tablePropertyDefs = null;
            _modelStandardMap = null;
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
