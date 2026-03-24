using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.MetaCenter.Shared.Data;
using EliteSoft.MetaCenter.Shared.Data.Entities;
using EliteSoft.MetaCenter.Shared.Services;
using EliteSoft.Erwin.AddIn.Forms;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads project standards from DB (MC_ tables) and applies them to erwin model objects.
    /// Detects model's target DB platform via SCAPI and loads only matching property definitions.
    /// </summary>
    public class PropertyApplicatorService : IDisposable
    {
        private readonly dynamic _session;
        private readonly IPropertyMetadataService _metadataService;
        private bool _disposed;

        // Cached data
        private Platform _detectedPlatform;
        private string _targetServerValue;
        private List<PropertyDef> _tablePropertyDefs;
        private Dictionary<int, string> _projectStandardMap; // PropertyDefId -> Value
        private List<QuestionDef> _questions; // Question definitions for this platform+TABLE
        private int _projectId;
        private int _tableObjectTypeId;

        public event Action<string> OnLog;

        public PropertyApplicatorService(dynamic session, IPropertyMetadataService metadataService)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _metadataService = metadataService ?? throw new ArgumentNullException(nameof(metadataService));
        }

        #region Initialization

        /// <summary>
        /// Initialize: detect platform from model, find project by model path, load property defs + standards.
        /// </summary>
        public bool Initialize()
        {
            try
            {
                // 1. Detect platform from model's target server
                _detectedPlatform = DetectPlatform();
                if (_detectedPlatform == null)
                {
                    Log("PropertyApplicator: Could not detect model platform");
                    return false;
                }
                Log($"PropertyApplicator: Detected platform = {_detectedPlatform.Name} (ID={_detectedPlatform.Id})");

                // 2. Find project by model file path
                _projectId = FindProjectId();
                if (_projectId <= 0)
                {
                    Log("PropertyApplicator: No matching project found in DB for this model");
                    return false;
                }
                Log($"PropertyApplicator: Matched project ID = {_projectId}");

                // 3. Get TABLE object type
                var objectTypes = _metadataService.GetObjectTypes();
                var tableType = objectTypes.FirstOrDefault(o => o.Name == "TABLE");
                if (tableType == null)
                {
                    Log("PropertyApplicator: TABLE object type not found in DB");
                    return false;
                }
                _tableObjectTypeId = tableType.Id;

                // 4. Load property definitions for this platform + TABLE
                _tablePropertyDefs = _metadataService.GetPropertyDefs(_detectedPlatform.Id, tableType.Id);
                Log($"PropertyApplicator: Loaded {_tablePropertyDefs.Count} property definitions for {_detectedPlatform.Name} TABLE");

                // 5. Load project standards
                var standards = _metadataService.GetProjectStandards(_projectId);
                Log($"PropertyApplicator: Raw standards from DB for PROJECT_ID={_projectId}: {standards.Count} record(s)");
                foreach (var s in standards)
                {
                    var matchingDef = _tablePropertyDefs.FirstOrDefault(pd => pd.Id == s.PropertyDefId);
                    Log($"  Standard: PropertyDefId={s.PropertyDefId}, Value='{s.Value}', MatchesDef={matchingDef != null}{(matchingDef != null ? $" ({matchingDef.PropertyCode})" : "")}");
                }

                Log($"PropertyApplicator: Table PropertyDef IDs: [{string.Join(", ", _tablePropertyDefs.Select(pd => $"{pd.Id}:{pd.PropertyCode}"))}]");

                _projectStandardMap = standards
                    .Where(s => _tablePropertyDefs.Any(pd => pd.Id == s.PropertyDefId))
                    .ToDictionary(s => s.PropertyDefId, s => s.Value);
                Log($"PropertyApplicator: Loaded {_projectStandardMap.Count} project standards (after filter)");

                // 6. Load question definitions for this platform + TABLE
                _questions = _metadataService.GetQuestions(_detectedPlatform.Id, tableType.Id);
                Log($"PropertyApplicator: Loaded {_questions.Count} question(s) for {_detectedPlatform.Name} TABLE");

                return true;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: Initialize error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find the PROJECT record by matching model file path.
        /// PROJECT.PATH is set by erwin-admin when a project is registered.
        /// </summary>
        private int FindProjectId()
        {
            try
            {
                var bootstrapService = new RegistryBootstrapService();
                var config = bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured)
                {
                    Log("PropertyApplicator: Database not configured");
                    return -1;
                }

                // 1. Try matching by full file path
                string modelPath = ReadModelFilePath();
                if (!string.IsNullOrEmpty(modelPath))
                {
                    Log($"PropertyApplicator: Model file path = '{modelPath}'");
                    string normalizedModelPath = modelPath.Replace("/", "\\").TrimEnd('\\').ToUpperInvariant();

                    using (var context = new RepoDbContext(config))
                    {
                        var project = context.Projects
                            .AsEnumerable()
                            .FirstOrDefault(p => !string.IsNullOrEmpty(p.Path) &&
                                p.Path.Replace("/", "\\").TrimEnd('\\').ToUpperInvariant() == normalizedModelPath);

                        if (project != null)
                        {
                            Log($"PropertyApplicator: Matched project by full path, ID={project.Id}");
                            return project.Id;
                        }
                    }
                }

                // Log all projects for debugging
                using (var ctx = new RepoDbContext(config))
                {
                    var allProjects = ctx.Projects.ToList();
                    Log($"PropertyApplicator: DB projects ({allProjects.Count}): [{string.Join(", ", allProjects.Select(p => $"ID={p.Id} PATH='{p.Path}'"))}]");
                }

                // 2. Fallback: match model name against filename portion of PROJECT.PATH
                string modelName = ReadModelName();
                if (!string.IsNullOrEmpty(modelName))
                {
                    Log($"PropertyApplicator: Model name = '{modelName}', trying name-based match");

                    using (var context = new RepoDbContext(config))
                    {
                        var project = context.Projects
                            .AsEnumerable()
                            .FirstOrDefault(p => !string.IsNullOrEmpty(p.Path) &&
                                MatchesModelName(p.Path, modelName));

                        if (project != null)
                        {
                            Log($"PropertyApplicator: Matched project by model name, ID={project.Id}, PATH='{project.Path}'");
                            return project.Id;
                        }
                    }

                    Log($"PropertyApplicator: No project matched model name '{modelName}'");
                }
                else
                {
                    Log("PropertyApplicator: Could not read model file path or name");
                }

                // 3. Fallback: use root project (PATH = "\")
                using (var context = new RepoDbContext(config))
                {
                    var rootProject = context.Projects
                        .AsEnumerable()
                        .FirstOrDefault(p => p.Path == "\\" || p.Path == "/" || p.Path == "\\\\");

                    if (rootProject != null)
                    {
                        Log($"PropertyApplicator: Using root project fallback, ID={rootProject.Id}");
                        return rootProject.Id;
                    }

                    // 4. Last resort: use first project in DB
                    var firstProject = context.Projects.OrderBy(p => p.Id).FirstOrDefault();
                    if (firstProject != null)
                    {
                        Log($"PropertyApplicator: Using first project as fallback, ID={firstProject.Id}, PATH='{firstProject.Path}'");
                        return firstProject.Id;
                    }
                }

                Log("PropertyApplicator: No projects found in DB");
                return -1;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: FindProjectId error: {ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Check if a PROJECT.PATH contains the model name as its filename (without extension).
        /// E.g. PATH="C:\Models\AchModel.erwin" matches modelName="AchModel"
        /// </summary>
        private bool MatchesModelName(string projectPath, string modelName)
        {
            try
            {
                string fileName = System.IO.Path.GetFileNameWithoutExtension(projectPath);
                return string.Equals(fileName, modelName, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Read model file path from SCAPI.
        /// Tries multiple properties: File_Name, then Name as fallback.
        /// </summary>
        private string ReadModelFilePath()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return null;

                // 1. Try File_Name property (full path for file-based models)
                try
                {
                    string path = root.Properties("File_Name").Value?.ToString();
                    Log($"PropertyApplicator: File_Name = '{path ?? "(null)"}'");
                    if (!string.IsNullOrEmpty(path) && !path.StartsWith("%"))
                        return path;
                }
                catch (Exception ex)
                {
                    Log($"PropertyApplicator: File_Name read failed: {ex.Message}");
                }

                // 2. Try other path-related properties
                string[] pathProps = { "File_Path", "Model_File_Name", "Source_File_Name" };
                foreach (var propName in pathProps)
                {
                    try
                    {
                        string val = root.Properties(propName).Value?.ToString();
                        if (!string.IsNullOrEmpty(val) && !val.StartsWith("%"))
                        {
                            Log($"PropertyApplicator: {propName} = '{val}'");
                            return val;
                        }
                    }
                    catch { }
                }

                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Read model name from SCAPI root object.
        /// </summary>
        private string ReadModelName()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                return root?.Properties("Name").Value?.ToString();
            }
            catch { return null; }
        }

        /// <summary>
        /// Reload standards from DB (e.g. after admin changes).
        /// </summary>
        public void ReloadStandards()
        {
            try
            {
                var standards = _metadataService.GetProjectStandards(_projectId);
                _projectStandardMap = standards
                    .Where(s => _tablePropertyDefs.Any(pd => pd.Id == s.PropertyDefId))
                    .ToDictionary(s => s.PropertyDefId, s => s.Value);
                Log($"PropertyApplicator: Reloaded {_projectStandardMap.Count} project standards");
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ReloadStandards error: {ex.Message}");
            }
        }

        #endregion

        #region Platform Detection

        /// <summary>
        /// Detect platform from model's target server property via SCAPI.
        /// First tries exact match, then partial match (platform name starts with detected vendor).
        /// </summary>
        private Platform DetectPlatform()
        {
            try
            {
                var platforms = _metadataService.GetPlatforms();
                Log($"PropertyApplicator: DB platforms: [{string.Join(", ", platforms.Select(p => $"'{p.Name}' (ID={p.Id})"))}]");

                string targetServer = ReadTargetServer();

                if (string.IsNullOrEmpty(targetServer))
                {
                    Log("PropertyApplicator: Target_Server property is empty");
                    _targetServerValue = null;
                    return null;
                }

                _targetServerValue = targetServer;
                Log($"PropertyApplicator: Model Target_Server = '{targetServer}'");

                // 1. Exact match against MC_PLATFORM.NAME (case-insensitive)
                var match = platforms.FirstOrDefault(p =>
                    string.Equals(p.Name, targetServer, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    Log($"PropertyApplicator: Platform exact match = '{match.Name}'");
                    return match;
                }

                // 2. Partial match: platform name starts with detected vendor (e.g. "ORACLE" matches "Oracle 19c")
                match = platforms.FirstOrDefault(p =>
                    p.Name != null && p.Name.StartsWith(targetServer, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    Log($"PropertyApplicator: Platform partial match = '{match.Name}' (starts with '{targetServer}')");
                    return match;
                }

                // 3. Reverse partial: detected name starts with platform name (e.g. detected "Oracle 19c" matches platform "ORACLE")
                match = platforms.FirstOrDefault(p =>
                    p.Name != null && targetServer.StartsWith(p.Name, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    Log($"PropertyApplicator: Platform reverse partial match = '{match.Name}'");
                    return match;
                }

                Log($"PropertyApplicator: No platform match for '{targetServer}'");
                return null;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: DetectPlatform error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Read Target_Server from model root via SCAPI.
        /// erwin stores Target_Server as a numeric enum ID (e.g. 174 = Oracle).
        /// We probe the model to determine the DBMS platform.
        /// </summary>
        private string ReadTargetServer()
        {
            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return null;

                // 1. Probe all root properties for debugging
                string[] probeProperties = new[]
                {
                    "Target_Server", "DBMS", "Type_Name", "Long_Id",
                    "ClassName", "Name", "Logical_Physical_Type"
                };

                foreach (string propName in probeProperties)
                {
                    try
                    {
                        var val = root.Properties(propName).Value;
                        string strVal = val?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(strVal))
                            Log($"PropertyApplicator: root.{propName} = '{strVal}'");
                    }
                    catch { }
                }

                // 2. Try root.ClassName (object class name, not a property)
                try
                {
                    string className = root.ClassName?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(className))
                        Log($"PropertyApplicator: root.ClassName = '{className}'");
                }
                catch { }

                // 3. Determine platform by probing for platform-specific child objects.
                // This is the most reliable method — if Oracle objects exist, it's Oracle.
                string detectedByProbe = DetectPlatformByObjectProbe(modelObjects, root);
                if (!string.IsNullOrEmpty(detectedByProbe))
                {
                    Log($"PropertyApplicator: Detected platform by object probe = '{detectedByProbe}'");
                    return detectedByProbe;
                }

                // 4. Fallback: return Target_Server numeric value
                try
                {
                    string ts = root.Properties("Target_Server").Value?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(ts))
                        return ts;
                }
                catch { }

                return null;
            }
            catch (Exception ex)
            {
                Log($"PropertyApplicator: ReadTargetServer error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Detect platform by probing for platform-specific SCAPI object types.
        /// If Oracle_Physical_Object_Storage can be collected, it's an Oracle model.
        /// Returns MC_PLATFORM.NAME value (ORACLE, MSSQL, POSTGRESQL) or null.
        /// </summary>
        private string DetectPlatformByObjectProbe(dynamic modelObjects, dynamic root)
        {
            // Oracle: try collecting Oracle-specific object types
            try
            {
                dynamic oracleCheck = modelObjects.Collect(root, "Oracle_Physical_Object_Storage", -1);
                // If no exception, Oracle objects exist in the metamodel → it's Oracle
                Log($"PropertyApplicator: Oracle probe OK (count={oracleCheck?.Count ?? 0})");
                return "ORACLE";
            }
            catch
            {
                Log("PropertyApplicator: Oracle probe failed (not Oracle model)");
            }

            // SQL Server: try collecting SQL Server-specific object types
            try
            {
                dynamic mssqlCheck = modelObjects.Collect(root, "SQL_Server_Index", -1);
                Log($"PropertyApplicator: MSSQL probe OK (count={mssqlCheck?.Count ?? 0})");
                return "MSSQL";
            }
            catch
            {
                Log("PropertyApplicator: MSSQL probe failed (not MSSQL model)");
            }

            // PostgreSQL: try collecting PostgreSQL-specific object types
            try
            {
                dynamic pgCheck = modelObjects.Collect(root, "Postgres_Tablespace", -1);
                Log($"PropertyApplicator: PostgreSQL probe OK (count={pgCheck?.Count ?? 0})");
                return "POSTGRESQL";
            }
            catch
            {
                Log("PropertyApplicator: PostgreSQL probe failed (not PostgreSQL model)");
            }

            return null;
        }

        #endregion

        #region Apply Standards to Entity

        /// <summary>
        /// Apply properties to a new entity (table).
        /// Flow: 1) Show question wizard (if questions exist) → 2) Apply question rule values
        ///       → 3) Fill remaining from project standards (fallback)
        /// Priority: Question rules > Project standards
        /// </summary>
        public void ApplyStandardsToEntity(dynamic entity, string physicalName)
        {
            if (_tablePropertyDefs == null)
                return;

            try
            {
                // Merged property values: PropertyDefId -> Value
                var mergedValues = new Dictionary<int, string>();

                // 1. Start with project standards as baseline
                if (_projectStandardMap != null)
                {
                    foreach (var kvp in _projectStandardMap)
                        mergedValues[kvp.Key] = kvp.Value;
                }

                // 2. Show question wizard if questions exist — answers override standards
                if (_questions != null && _questions.Count > 0)
                {
                    Log($"PropertyApplicator: Showing question wizard for '{physicalName}' ({_questions.Count} question(s))");

                    try
                    {
                        using (var wizard = new QuestionWizardForm(physicalName, _questions))
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
                                Log("PropertyApplicator: Wizard cancelled or no values — using project standards only");
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
                int applied = 0;
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

                    bool success = ApplyPropertyToModel(entity, physicalName, def, value);
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
        private bool ApplyPropertyToModel(dynamic entity, string physicalName, PropertyDef def, string value)
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
                        // Partition properties are complex — may require separate handling
                        Log($"PropertyApplicator: PARTITIONED not yet implemented for '{physicalName}'");
                        return false;

                    case "PARTITION_TYPE":
                    case "PARTITION_KEY":
                    case "PARTITION_TBS_RULE":
                        Log($"PropertyApplicator: {def.PropertyCode} not yet implemented for '{physicalName}'");
                        return false;

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
                    "NOLOGGING" => "No Logging",
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

        public Platform DetectedPlatform => _detectedPlatform;
        public string TargetServerValue => _targetServerValue;
        public int StandardCount => _projectStandardMap?.Count ?? 0;
        public int QuestionCount => _questions?.Count ?? 0;
        public List<PropertyDef> TablePropertyDefs => _tablePropertyDefs;
        public bool IsInitialized => _detectedPlatform != null && _tablePropertyDefs != null;

        #endregion

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _tablePropertyDefs = null;
            _projectStandardMap = null;
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
