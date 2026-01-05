using System;
using System.Collections.Generic;
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

                // Get the Subject_Area object - UDP values are stored on Subject_Area, not Model
                // Use _session.ModelObjects (not _currentModel.ModelObjects)
                dynamic subjectArea = null;
                try
                {
                    var modelObjects = _session.ModelObjects;
                    log($"[UDP] Got ModelObjects from Session");
                    var saCollection = modelObjects.Collect("Subject_Area");
                    log($"[UDP] Subject_Area count: {saCollection?.Count ?? 0}");
                    if (saCollection != null && saCollection.Count > 0)
                    {
                        subjectArea = saCollection.Item(0);
                        log($"[UDP] Got Subject_Area: {subjectArea?.Name ?? "(no name)"}");
                    }
                }
                catch (Exception ex)
                {
                    log($"[UDP] Failed to get Subject_Area collection: {ex.Message}");
                }

                // Fallback to Root if Subject_Area not found
                if (subjectArea == null)
                {
                    try
                    {
                        subjectArea = _session.ModelObjects.Root;
                        log($"[UDP] Using Root object: {subjectArea?.Name ?? "(no name)"}");
                    }
                    catch (Exception ex)
                    {
                        log($"[UDP] Failed to get Root: {ex.Message}");
                    }
                }

                if (subjectArea == null)
                {
                    log("[UDP] No Subject_Area or Root object found");
                    return result;
                }

                // Known UDP names that apply to Subject_Area
                var udpNames = new[] { "ES_DatabaseName", "ES_SchemaName", "ES_FullName", "ES_Code" };

                foreach (var udpName in udpNames)
                {
                    string value = null;

                    // Method 1: Try direct property name (simple UDP)
                    if (string.IsNullOrEmpty(value))
                    {
                        try
                        {
                            value = subjectArea.Properties(udpName).Value?.ToString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                log($"[UDP] Found via direct: {udpName} = {value}");
                            }
                        }
                        catch (Exception ex)
                        {
                            log($"[UDP] Direct {udpName} failed: {ex.Message}");
                        }
                    }

                    // Method 2: Try Logical format (Subject_Area.Logical.ES_DatabaseName)
                    if (string.IsNullOrEmpty(value))
                    {
                        try
                        {
                            value = subjectArea.Properties($"Subject_Area.Logical.{udpName}").Value?.ToString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                log($"[UDP] Found via Logical: {udpName} = {value}");
                            }
                        }
                        catch { }
                    }

                    // Method 3: Try Physical format (Subject_Area.Physical.ES_DatabaseName)
                    if (string.IsNullOrEmpty(value))
                    {
                        try
                        {
                            value = subjectArea.Properties($"Subject_Area.Physical.{udpName}").Value?.ToString();
                            if (!string.IsNullOrEmpty(value))
                            {
                                log($"[UDP] Found via Physical: {udpName} = {value}");
                            }
                        }
                        catch { }
                    }

                    if (!string.IsNullOrEmpty(value))
                    {
                        result.Add(new UdpValue { Name = udpName, Value = value });
                    }
                }

                // Also try to get any other UDPs from UDP_Definitions that apply to Subject_Area
                try
                {
                    var udpDefs = _currentModel.UDP_Definitions;
                    if (udpDefs != null)
                    {
                        log($"[UDP] Checking {udpDefs.Count} UDP definitions for additional UDPs...");
                        foreach (var udpDef in udpDefs)
                        {
                            try
                            {
                                string defName = udpDef.Name;
                                // Skip if already found
                                if (result.Exists(u => u.Name == defName)) continue;

                                string value = null;
                                try { value = subjectArea.Properties(defName).Value?.ToString(); } catch { }
                                if (string.IsNullOrEmpty(value))
                                {
                                    try { value = subjectArea.Properties($"Subject_Area.Logical.{defName}").Value?.ToString(); } catch { }
                                }
                                if (string.IsNullOrEmpty(value))
                                {
                                    try { value = subjectArea.Properties($"Subject_Area.Physical.{defName}").Value?.ToString(); } catch { }
                                }

                                if (!string.IsNullOrEmpty(value))
                                {
                                    result.Add(new UdpValue { Name = defName, Value = value });
                                    log($"[UDP] Found additional UDP: {defName} = {value}");
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

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CloseCurrentModel();
            _scapi = null;
        }
    }
}
