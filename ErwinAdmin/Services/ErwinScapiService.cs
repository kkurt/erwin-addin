using System;
using System.Collections.Generic;
using System.Linq;
using EliteSoft.Erwin.Admin.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service for erwin SCAPI (COM) operations
    /// </summary>
    public interface IErwinScapiService : IDisposable
    {
        /// <summary>
        /// Gets whether SCAPI is initialized
        /// </summary>
        bool IsInitialized { get; }

        /// <summary>
        /// Gets the initialization error if any
        /// </summary>
        string InitializationError { get; }

        /// <summary>
        /// Gets the currently loaded model
        /// </summary>
        dynamic CurrentModel { get; }

        /// <summary>
        /// Loads a model from Mart Server
        /// </summary>
        ModelLoadResult LoadFromMart(MartConnectionInfo connection, string modelPath);

        /// <summary>
        /// Loads a model from file
        /// </summary>
        ModelLoadResult LoadFromFile(string filePath);

        /// <summary>
        /// Closes the current model
        /// </summary>
        void CloseCurrentModel();

        /// <summary>
        /// Gets the model name
        /// </summary>
        string GetModelName();

        /// <summary>
        /// Gets all UDP definitions from the model
        /// </summary>
        List<UdpDefinition> GetUdpDefinitions();

        /// <summary>
        /// Gets UDP values for the Model object itself
        /// </summary>
        List<UdpValue> GetModelUdpValues();

        /// <summary>
        /// Gets UDP values with debug logging
        /// </summary>
        List<UdpValue> GetModelUdpValues(Action<string> log);

        /// <summary>
        /// Gets naming standards from the model
        /// </summary>
        List<NamingStandard> GetNamingStandards(Action<string> log = null);
    }

    /// <summary>
    /// Represents a Naming Standard in the model
    /// </summary>
    public class NamingStandard
    {
        public string Name { get; set; }
        public string ObjectType { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Represents a UDP (User Defined Property) definition
    /// </summary>
    public class UdpDefinition
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string AppliesTo { get; set; }
        public string DefaultValue { get; set; }
    }

    /// <summary>
    /// Represents a UDP value on an object
    /// </summary>
    public class UdpValue
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public class ModelLoadResult
    {
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public int? HResult { get; set; }

        public static ModelLoadResult Successful() => new ModelLoadResult { Success = true };

        public static ModelLoadResult Failed(string errorMessage, int? hresult = null) =>
            new ModelLoadResult { Success = false, ErrorMessage = errorMessage, HResult = hresult };
    }

    /// <summary>
    /// Implementation of erwin SCAPI service
    /// </summary>
    public sealed class ErwinScapiService : IErwinScapiService
    {
        private dynamic _scapi;
        private dynamic _currentModel;
        private dynamic _session;
        private bool _disposed;

        public bool IsInitialized => _scapi != null;
        public string InitializationError { get; private set; }
        public dynamic CurrentModel => _currentModel;

        public ErwinScapiService()
        {
            Initialize();
        }

        private void Initialize()
        {
            try
            {
                var scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType == null)
                {
                    InitializationError = "erwin SCAPI not found - please install erwin Data Modeler";
                    return;
                }

                _scapi = Activator.CreateInstance(scapiType);
                if (_scapi == null)
                {
                    InitializationError = "Failed to create SCAPI instance";
                }
            }
            catch (Exception ex)
            {
                InitializationError = $"SCAPI initialization error: {ex.Message}";
            }
        }

        public ModelLoadResult LoadFromMart(MartConnectionInfo connection, string modelPath)
        {
            if (!IsInitialized)
            {
                return ModelLoadResult.Failed(InitializationError ?? "SCAPI not initialized");
            }

            try
            {
                CloseCurrentModel();

                var martUrl = connection.GetMartUrl(modelPath);
                _currentModel = _scapi.PersistenceUnits.Add(martUrl, "RDO=Yes");

                if (_currentModel == null)
                {
                    return ModelLoadResult.Failed("Model returned null");
                }

                // Create session for model access
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                return ModelLoadResult.Successful();
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                return ModelLoadResult.Failed(comEx.Message, comEx.HResult);
            }
            catch (Exception ex)
            {
                return ModelLoadResult.Failed(ex.Message);
            }
        }

        public ModelLoadResult LoadFromFile(string filePath)
        {
            if (!IsInitialized)
            {
                return ModelLoadResult.Failed(InitializationError ?? "SCAPI not initialized");
            }

            try
            {
                CloseCurrentModel();

                _currentModel = _scapi.PersistenceUnits.Add(filePath, "RDO=Yes");

                if (_currentModel == null)
                {
                    return ModelLoadResult.Failed("Model returned null");
                }

                // Create session for model access
                _session = _scapi.Sessions.Add();
                _session.Open(_currentModel);

                return ModelLoadResult.Successful();
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                return ModelLoadResult.Failed(comEx.Message, comEx.HResult);
            }
            catch (Exception ex)
            {
                return ModelLoadResult.Failed(ex.Message);
            }
        }

        public void CloseCurrentModel()
        {
            // Close session first
            if (_session != null)
            {
                try { _session.Close(); } catch { }
                _session = null;
            }

            if (_currentModel == null) return;

            try
            {
                _scapi?.PersistenceUnits.Remove(_currentModel);
            }
            catch { }
            finally
            {
                _currentModel = null;
            }
        }

        public string GetModelName()
        {
            if (_currentModel == null) return null;

            try
            {
                return _currentModel.Name;
            }
            catch
            {
                return null;
            }
        }

        public List<UdpDefinition> GetUdpDefinitions()
        {
            var result = new List<UdpDefinition>();

            if (_currentModel == null)
            {
                System.Diagnostics.Debug.WriteLine("GetUdpDefinitions: _currentModel is null");
                return result;
            }

            try
            {
                // Try to get UDP definitions from ModelObjects collection
                // In SCAPI, UDP definitions are stored as ModelObjects with specific class
                System.Diagnostics.Debug.WriteLine($"Model Name: {_currentModel.Name}");

                // Method 1: Try UDP_Definitions directly
                try
                {
                    var udpDefs = _currentModel.UDP_Definitions;
                    System.Diagnostics.Debug.WriteLine($"UDP_Definitions count: {udpDefs?.Count ?? 0}");

                    if (udpDefs != null && udpDefs.Count > 0)
                    {
                        foreach (var udp in udpDefs)
                        {
                            try
                            {
                                var def = new UdpDefinition
                                {
                                    Name = udp.Name ?? "",
                                    DataType = GetUdpDataTypeName(udp.Data_Type),
                                    AppliesTo = GetUdpAppliesToName(udp.Applies_To),
                                    DefaultValue = udp.Default_Value ?? ""
                                };
                                result.Add(def);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error reading UDP: {ex.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UDP_Definitions error: {ex.Message}");
                }

                // Method 2: Try ModelObjects with UDP class if Method 1 returned nothing
                if (result.Count == 0)
                {
                    try
                    {
                        // Get all model objects and filter UDP definitions
                        var modelObjects = _currentModel.ModelObjects;
                        System.Diagnostics.Debug.WriteLine($"ModelObjects count: {modelObjects?.Count ?? 0}");

                        if (modelObjects != null)
                        {
                            foreach (var obj in modelObjects)
                            {
                                try
                                {
                                    string className = obj.ClassName;
                                    if (className == "UDP_Definition" || className == "User_Defined_Property")
                                    {
                                        var def = new UdpDefinition
                                        {
                                            Name = obj.Name ?? "",
                                            DataType = "Unknown",
                                            AppliesTo = "Unknown",
                                            DefaultValue = ""
                                        };

                                        // Try to get properties
                                        try { def.DataType = GetUdpDataTypeName((int)obj.Data_Type); } catch { }
                                        try { def.AppliesTo = GetUdpAppliesToName((int)obj.Applies_To); } catch { }
                                        try { def.DefaultValue = obj.Default_Value ?? ""; } catch { }

                                        result.Add(def);
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"ModelObjects error: {ex.Message}");
                    }
                }

                System.Diagnostics.Debug.WriteLine($"Total UDPs found: {result.Count}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetUdpDefinitions error: {ex.Message}");
            }

            return result;
        }

        public List<UdpValue> GetModelUdpValues() => GetModelUdpValues(null);

        public List<UdpValue> GetModelUdpValues(Action<string> log)
        {
            var result = new List<UdpValue>();
            log ??= s => System.Diagnostics.Debug.WriteLine(s);

            if (_currentModel == null)
            {
                log("[UDP] _currentModel is null");
                return result;
            }

            if (_session == null)
            {
                log("[UDP] _session is null - cannot access ModelObjects");
                return result;
            }

            try
            {
                log("[UDP] Getting Model UDP values via Session...");

                dynamic modelObjects = null;
                try
                {
                    modelObjects = _session.ModelObjects;
                    log($"[UDP] Got ModelObjects from Session, Count: {modelObjects.Count}");
                }
                catch (Exception ex)
                {
                    log($"[UDP] Failed to get ModelObjects: {ex.Message}");
                    return result;
                }

                // Get the root/model object first
                dynamic modelRoot = null;
                try
                {
                    modelRoot = modelObjects.Root;
                    string rootClassName = "unknown";
                    try { rootClassName = modelRoot.ClassName; } catch { }
                    log($"[UDP] Root object: {modelRoot?.Name ?? "(no name)"}, ClassName: {rootClassName}");
                }
                catch (Exception ex)
                {
                    log($"[UDP] Failed to get Root: {ex.Message}");
                }

                // Known UDP names we're looking for
                var udpNames = new[] { "ES_DatabaseName", "ES_SchemaName", "ES_FullName", "ES_Code" };

                // First, let's check both Logical and Physical values explicitly
                log("[UDP] === Checking Logical vs Physical UDP values ===");
                foreach (var udpName in udpNames)
                {
                    string logicalValue = null;
                    string physicalValue = null;

                    // Try Model.Logical
                    try { logicalValue = modelRoot?.Properties($"Model.Logical.{udpName}").Value?.ToString(); } catch { }
                    // Try Model.Physical
                    try { physicalValue = modelRoot?.Properties($"Model.Physical.{udpName}").Value?.ToString(); } catch { }

                    log($"[UDP]   {udpName}: Logical='{logicalValue ?? "(null)"}', Physical='{physicalValue ?? "(null)"}'");
                }
                log("[UDP] ===================================");

                // Try multiple strategies to find UDP values

                // Strategy 1: Try to get Subject_Area if it exists
                dynamic subjectArea = null;
                try
                {
                    var saCollection = modelObjects.Collect("Subject_Area");
                    int count = saCollection?.Count ?? 0;
                    log($"[UDP] Subject_Area collection count: {count}");
                    if (count > 0)
                    {
                        try { subjectArea = saCollection.Item(1); }
                        catch { try { subjectArea = saCollection.Item(0); } catch { } }

                        if (subjectArea != null)
                            log($"[UDP] Got Subject_Area: {subjectArea?.Name ?? "(no name)"}");
                    }
                }
                catch (Exception ex)
                {
                    log($"[UDP] Subject_Area not available: {ex.Message}");
                }

                // Strategy 2: Try Default_Subject_Area property on model
                if (subjectArea == null && modelRoot != null)
                {
                    try
                    {
                        subjectArea = modelRoot.Default_Subject_Area;
                        log($"[UDP] Got Default_Subject_Area: {subjectArea?.Name ?? "(no name)"}");
                    }
                    catch (Exception ex)
                    {
                        log($"[UDP] Default_Subject_Area not available: {ex.Message}");
                    }
                }

                // Create a list of objects to try for UDP values
                var objectsToTry = new List<(dynamic obj, string name)>();
                if (subjectArea != null) objectsToTry.Add((subjectArea, "Subject_Area"));
                if (modelRoot != null) objectsToTry.Add((modelRoot, "Model"));

                // Property path patterns to try for each object
                // Try Physical first (most common for database generation), then Logical
                var pathPatterns = new[]
                {
                    "{0}",                              // Direct: ES_DatabaseName
                    "Subject_Area.Physical.{0}",        // Subject_Area.Physical.ES_DatabaseName
                    "Model.Physical.{0}",               // Model.Physical.ES_DatabaseName
                    "Physical.{0}",                     // Physical.ES_DatabaseName
                    "Subject_Area.Logical.{0}",         // Subject_Area.Logical.ES_DatabaseName
                    "Model.Logical.{0}",                // Model.Logical.ES_DatabaseName
                    "Logical.{0}",                      // Logical.ES_DatabaseName
                };

                foreach (var udpName in udpNames)
                {
                    string value = null;

                    foreach (var (obj, objName) in objectsToTry)
                    {
                        if (!string.IsNullOrEmpty(value)) break;

                        foreach (var pattern in pathPatterns)
                        {
                            if (!string.IsNullOrEmpty(value)) break;

                            string propPath = string.Format(pattern, udpName);
                            try
                            {
                                value = obj.Properties(propPath).Value?.ToString();
                                if (!string.IsNullOrEmpty(value))
                                {
                                    log($"[UDP] Found {udpName} on {objName} via '{propPath}': {value}");
                                }
                            }
                            catch { }
                        }
                    }

                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(new UdpValue { Name = udpName, Value = value });
                    }
                }

                // Strategy 3: Try to get UDP values from UDP_Definitions
                try
                {
                    var udpDefs = _currentModel.UDP_Definitions;
                    if (udpDefs != null && udpDefs.Count > 0)
                    {
                        log($"[UDP] Checking {udpDefs.Count} UDP definitions...");
                        foreach (var udpDef in udpDefs)
                        {
                            try
                            {
                                string defName = udpDef.Name;
                                if (result.Exists(u => u.Name == defName)) continue;

                                string value = null;
                                foreach (var (obj, objName) in objectsToTry)
                                {
                                    if (!string.IsNullOrEmpty(value)) break;

                                    foreach (var pattern in pathPatterns)
                                    {
                                        if (!string.IsNullOrEmpty(value)) break;

                                        string propPath = string.Format(pattern, defName);
                                        try
                                        {
                                            value = obj.Properties(propPath).Value?.ToString();
                                            if (!string.IsNullOrEmpty(value))
                                            {
                                                log($"[UDP] Found {defName} on {objName} via '{propPath}': {value}");
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                if (!string.IsNullOrEmpty(value))
                                {
                                    result.Add(new UdpValue { Name = defName, Value = value });
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    log($"[UDP] UDP_Definitions scan error: {ex.Message}");
                }

                // Strategy 4: If still no results, enumerate all properties on available objects
                if (result.Count == 0)
                {
                    log("[UDP] No UDPs found with standard methods, enumerating properties...");
                    foreach (var (obj, objName) in objectsToTry)
                    {
                        try
                        {
                            // Try to enumerate Properties collection
                            var props = obj.Properties;
                            if (props != null)
                            {
                                int propCount = 0;
                                try { propCount = props.Count; } catch { }
                                log($"[UDP] {objName} has {propCount} properties");

                                int shown = 0;
                                foreach (var prop in props)
                                {
                                    if (shown >= 10) { log($"[UDP]   ... (stopping at 10)"); break; }
                                    try
                                    {
                                        string propName = prop.Name;
                                        string propValue = prop.Value?.ToString() ?? "(null)";
                                        // Only show ES_ prefixed properties
                                        if (propName.StartsWith("ES_"))
                                        {
                                            log($"[UDP]   {propName} = {propValue}");
                                            if (!string.IsNullOrEmpty(propValue) && propValue != "(null)")
                                            {
                                                result.Add(new UdpValue { Name = propName, Value = propValue });
                                            }
                                        }
                                        shown++;
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log($"[UDP] Properties enumeration on {objName} failed: {ex.Message}");
                        }
                    }
                }

                log($"[UDP] Total Model UDP values found: {result.Count}");
            }
            catch (Exception ex)
            {
                log($"[UDP] GetModelUdpValues error: {ex.Message}");
            }

            return result;
        }

        private static string GetUdpDataTypeName(int dataType)
        {
            // erwin UDP data types
            return dataType switch
            {
                1 => "Text",
                2 => "Integer",
                3 => "Real",
                4 => "Date/Time",
                5 => "List",
                _ => $"Unknown ({dataType})"
            };
        }

        private static string GetUdpAppliesToName(int appliesTo)
        {
            // erwin object types - these are bitmask values
            // Common values based on erwin documentation
            if (appliesTo == 0) return "None";

            var types = new List<string>();

            if ((appliesTo & 1) != 0) types.Add("Entity");
            if ((appliesTo & 2) != 0) types.Add("Attribute");
            if ((appliesTo & 4) != 0) types.Add("Relationship");
            if ((appliesTo & 8) != 0) types.Add("Key Group");
            if ((appliesTo & 16) != 0) types.Add("Subject Area");
            if ((appliesTo & 32) != 0) types.Add("Model");
            if ((appliesTo & 64) != 0) types.Add("Domain");
            if ((appliesTo & 128) != 0) types.Add("Default");

            return types.Count > 0 ? string.Join(", ", types) : $"Type({appliesTo})";
        }

        public List<NamingStandard> GetNamingStandards(Action<string> log = null)
        {
            var result = new List<NamingStandard>();
            log ??= s => System.Diagnostics.Debug.WriteLine(s);

            if (_session == null)
            {
                log("[NamingStd] _session is null");
                return result;
            }

            try
            {
                log("[NamingStd] Getting Naming Standards from model...");

                dynamic modelObjects = _session.ModelObjects;
                log($"[NamingStd] ModelObjects count: {modelObjects.Count}");

                // Try multiple class names for Naming Standards
                // Documentation says it's "NSM_Option" (Naming Standard Module Option)
                string[] classNames = {
                    "NSM_Option",           // Correct class name from erwin metamodel
                    "Naming_Standard",
                    "Naming Standard",
                    "NamingStandard"
                };

                bool foundViaCollect = false;

                foreach (var className in classNames)
                {
                    if (foundViaCollect) break;

                    try
                    {
                        var nsCollection = modelObjects.Collect(className);
                        int count = nsCollection?.Count ?? 0;

                        if (count > 0)
                        {
                            log($"[NamingStd] Found {count} objects via Collect('{className}')");
                            foundViaCollect = true;

                            for (int i = 0; i < count; i++)
                            {
                                try
                                {
                                    dynamic ns = null;
                                    try { ns = nsCollection.Item(i); }
                                    catch { try { ns = nsCollection.Item(i + 1); } catch { } }

                                    if (ns != null)
                                    {
                                        string name = "";
                                        string objType = "";
                                        string desc = "";

                                        try { name = ns.Name ?? ""; } catch { }
                                        try { objType = ns.Object_Type?.ToString() ?? ""; } catch { }
                                        try { desc = ns.Definition ?? ""; } catch { }

                                        if (string.IsNullOrEmpty(objType))
                                        {
                                            try { objType = ns.Properties("Object_Type").Value?.ToString() ?? ""; } catch { }
                                        }

                                        result.Add(new NamingStandard
                                        {
                                            Name = name,
                                            ObjectType = objType,
                                            Description = desc
                                        });

                                        log($"[NamingStd]   [{i}] {name} ({objType})");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    log($"[NamingStd]   Error reading item {i}: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch { }
                }

                // If Collect didn't work, iterate through all objects and log unique class names
                if (!foundViaCollect)
                {
                    log("[NamingStd] Collect failed, scanning all ModelObjects...");

                    // Collect unique class names to help debugging
                    var uniqueClasses = new HashSet<string>();
                    int namingRelatedCount = 0;

                    foreach (var obj in modelObjects)
                    {
                        try
                        {
                            string objClassName = obj.ClassName;
                            uniqueClasses.Add(objClassName);

                            // Check if class name contains "naming" or "standard"
                            string lowerClassName = objClassName.ToLower();
                            if (lowerClassName.Contains("naming") || lowerClassName.Contains("standard") || lowerClassName.Contains("name_map"))
                            {
                                string name = "";
                                string objType = "";
                                string desc = "";

                                try { name = obj.Name ?? ""; } catch { }
                                try { objType = obj.Object_Type?.ToString() ?? ""; } catch { }
                                try { desc = obj.Definition ?? ""; } catch { }

                                result.Add(new NamingStandard
                                {
                                    Name = name,
                                    ObjectType = objType,
                                    Description = desc
                                });

                                namingRelatedCount++;
                                log($"[NamingStd]   Found ({objClassName}): {name}");
                            }
                        }
                        catch { }
                    }

                    // Log some unique class names to help identify the correct one
                    log($"[NamingStd] Unique class names in model (sample): {string.Join(", ", uniqueClasses.Take(20))}");
                    log($"[NamingStd] Found {namingRelatedCount} naming-related objects");
                }

                log($"[NamingStd] Total Naming Standards found: {result.Count}");
            }
            catch (Exception ex)
            {
                log($"[NamingStd] GetNamingStandards error: {ex.Message}");
            }

            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseCurrentModel();
            _scapi = null;
        }
    }
}
