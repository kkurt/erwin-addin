using System;
using System.Collections.Generic;
using EliteSoft.Erwin.Admin.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service for browsing Mart catalog using SCAPI ModelDirectories
    /// </summary>
    public interface IMartCatalogService : IDisposable
    {
        /// <summary>
        /// Gets whether the service is connected to a Mart
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the last error message
        /// </summary>
        string LastError { get; }

        /// <summary>
        /// Connects to a Mart server using SCAPI
        /// </summary>
        bool Connect(MartConnectionInfo connectionInfo);

        /// <summary>
        /// Disconnects from the Mart server
        /// </summary>
        void Disconnect();

        /// <summary>
        /// Gets the root catalog entries (libraries)
        /// </summary>
        List<CatalogNode> GetRootCatalog();

        /// <summary>
        /// Gets child entries for a directory
        /// </summary>
        List<CatalogNode> GetChildren(string parentLocator);

        /// <summary>
        /// Event raised when log messages occur
        /// </summary>
        event EventHandler<LogEventArgs> LogMessage;
    }

    /// <summary>
    /// Represents a node in the catalog tree
    /// </summary>
    public class CatalogNode
    {
        public string Name { get; set; }
        public string Locator { get; set; }
        public CatalogNodeType NodeType { get; set; }
        public bool HasChildren { get; set; }

        public CatalogNode(string name, string locator, CatalogNodeType nodeType, bool hasChildren = false)
        {
            Name = name;
            Locator = locator;
            NodeType = nodeType;
            HasChildren = hasChildren;
        }
    }

    /// <summary>
    /// Type of catalog node
    /// </summary>
    public enum CatalogNodeType
    {
        Library,
        Directory,
        Model
    }

    /// <summary>
    /// SCAPI-based implementation of Mart catalog browsing
    /// </summary>
    public sealed class MartCatalogService : IMartCatalogService
    {
        private dynamic _scapi;
        private dynamic _martDirectory;
        private MartConnectionInfo _connectionInfo;
        private bool _disposed;

        // Cache of connected library directories to avoid reconnecting
        private readonly Dictionary<string, dynamic> _connectedLibraries = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);

        public bool IsConnected => _martDirectory != null;
        public string LastError { get; private set; }

        public event EventHandler<LogEventArgs> LogMessage;

        public MartCatalogService()
        {
            InitializeScapi();
        }

        private void InitializeScapi()
        {
            try
            {
                // First try to get the running erwin instance (if erwin DM is open)
                try
                {
                    Log(LogLevel.Debug, "Trying to get running erwin instance...");
                    _scapi = System.Runtime.InteropServices.Marshal.GetActiveObject("erwin9.SCAPI");
                    if (_scapi != null)
                    {
                        Log(LogLevel.Info, "Connected to running erwin instance!");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, $"No running erwin instance: {ex.Message}");
                }

                // Fall back to creating new instance
                var scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                if (scapiType == null)
                {
                    LastError = "erwin SCAPI not found - please install erwin Data Modeler";
                    Log(LogLevel.Error, LastError);
                    return;
                }

                _scapi = Activator.CreateInstance(scapiType);
                if (_scapi == null)
                {
                    LastError = "Failed to create SCAPI instance";
                    Log(LogLevel.Error, LastError);
                }
                else
                {
                    Log(LogLevel.Info, "SCAPI initialized (new instance)");
                }
            }
            catch (Exception ex)
            {
                LastError = $"SCAPI initialization error: {ex.Message}";
                Log(LogLevel.Error, LastError);
            }
        }

        public bool Connect(MartConnectionInfo connectionInfo)
        {
            if (_scapi == null)
            {
                LastError = "SCAPI not initialized";
                return false;
            }

            try
            {
                Disconnect();
                _connectionInfo = connectionInfo;

                // First, check if erwin already has ModelDirectories connected
                Log(LogLevel.Debug, "Checking existing ModelDirectories...");
                var existingCount = _scapi.ModelDirectories.Count;
                Log(LogLevel.Debug, $"  Existing ModelDirectories count: {existingCount}");

                for (int i = 1; i <= existingCount; i++)
                {
                    try
                    {
                        var dir = _scapi.ModelDirectories.Item(i);
                        var name = (string)dir.Name;
                        var locator = (string)dir.Locator;
                        var dirType = (int)dir.Type;
                        Log(LogLevel.Debug, $"  [{i}] Name={name}, Type={dirType}, Locator={locator}");

                        // If we find a Mart directory, use it
                        if (name == "Mart" || dirType == 2)
                        {
                            _martDirectory = dir;
                            Log(LogLevel.Info, $"Using existing Mart directory: {name}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Debug, $"  [{i}] Error: {ex.Message}");
                    }
                }

                // If no existing Mart directory, add new one
                if (_martDirectory == null)
                {
                    // Build SCAPI Mart locator string
                    // Format: mart://Mart/?SRV=server;PRT=port;ASR=MartServer;UID=user;PSW=password
                    var locator = BuildMartLocator(connectionInfo);
                    Log(LogLevel.Info, $"Connecting to Mart: {locator.Replace(connectionInfo.Password, "****")}");

                    // Add directory to ModelDirectories collection
                    _martDirectory = _scapi.ModelDirectories.Add(locator);

                    if (_martDirectory == null)
                    {
                        LastError = "Failed to connect to Mart - directory returned null";
                        Log(LogLevel.Error, LastError);
                        return false;
                    }
                }

                Log(LogLevel.Info, $"Connected to Mart successfully. Directory name: {_martDirectory.Name}");
                return true;
            }
            catch (System.Runtime.InteropServices.COMException comEx)
            {
                LastError = $"Mart connection error (COM): {comEx.Message} (0x{comEx.HResult:X8})";
                Log(LogLevel.Error, LastError);
                return false;
            }
            catch (Exception ex)
            {
                LastError = $"Mart connection error: {ex.Message}";
                Log(LogLevel.Error, LastError);
                return false;
            }
        }

        public void Disconnect()
        {
            // Clear cached library connections
            _connectedLibraries.Clear();

            if (_martDirectory == null) return;

            try
            {
                // Remove all ModelDirectories
                if (_scapi != null)
                {
                    while (_scapi.ModelDirectories.Count > 0)
                    {
                        try
                        {
                            var dir = _scapi.ModelDirectories.Item(1);
                            _scapi.ModelDirectories.Remove(dir, true);
                        }
                        catch { break; }
                    }
                }
                Log(LogLevel.Info, "Disconnected from Mart");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Warning, $"Error during disconnect: {ex.Message}");
            }
            finally
            {
                _martDirectory = null;
                _connectionInfo = null;
            }
        }

        public List<CatalogNode> GetRootCatalog()
        {
            var result = new List<CatalogNode>();

            if (_scapi == null || _connectionInfo == null)
            {
                LastError = "Not connected to Mart";
                return result;
            }

            try
            {
                Log(LogLevel.Debug, "Getting root catalog (libraries) dynamically...");

                // Dump root directory properties first
                if (_martDirectory != null)
                {
                    Log(LogLevel.Debug, "=== Root Mart Directory Properties ===");
                    try { Log(LogLevel.Debug, $"  Name: {_martDirectory.Name}"); } catch { }
                    try { Log(LogLevel.Debug, $"  Locator: {_martDirectory.Locator}"); } catch { }
                    try { Log(LogLevel.Debug, $"  Type: {_martDirectory.Type}"); } catch { }
                    try { Log(LogLevel.Debug, $"  Flags: {_martDirectory.Flags}"); } catch { }
                }

                // Strategy 1: Try LocateDirectory with "*" pattern from root
                if (_martDirectory != null)
                {
                    Log(LogLevel.Debug, "Strategy 1: Trying LocateDirectory('*') from root...");
                    try
                    {
                        var subDir = _martDirectory.LocateDirectory("*");
                        int count = 0;
                        while (subDir != null)
                        {
                            var name = (string)subDir.Name;
                            var locator = (string)subDir.Locator;
                            Log(LogLevel.Debug, $"  Found library: {name} (Locator: {locator})");
                            result.Add(new CatalogNode(name, string.IsNullOrEmpty(locator) ? name : locator, CatalogNodeType.Library, true));
                            subDir = _martDirectory.LocateDirectoryNext();
                            count++;
                        }
                        Log(LogLevel.Debug, $"  LocateDirectory('*') found {count} libraries");
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Debug, $"  Root enumeration error: {ex.Message}");
                    }
                }

                // Strategy 2: Try empty pattern
                if (result.Count == 0 && _martDirectory != null)
                {
                    Log(LogLevel.Debug, "Strategy 2: Trying LocateDirectory('') from root...");
                    try
                    {
                        var subDir = _martDirectory.LocateDirectory("");
                        int count = 0;
                        while (subDir != null)
                        {
                            var name = (string)subDir.Name;
                            var locator = (string)subDir.Locator;
                            Log(LogLevel.Debug, $"  Found library: {name}");
                            result.Add(new CatalogNode(name, string.IsNullOrEmpty(locator) ? name : locator, CatalogNodeType.Library, true));
                            subDir = _martDirectory.LocateDirectoryNext();
                            count++;
                        }
                        Log(LogLevel.Debug, $"  LocateDirectory('') found {count} libraries");
                    }
                    catch (Exception ex)
                    {
                        Log(LogLevel.Debug, $"  Root enumeration error: {ex.Message}");
                    }
                }

                // Strategy 3: If standard enumeration didn't work, discover by probing
                if (result.Count == 0)
                {
                    Log(LogLevel.Debug, "Strategy 3: Discovering libraries by probing...");
                    var discoveredLibraries = DiscoverLibraries();
                    result.AddRange(discoveredLibraries);
                }

                Log(LogLevel.Info, $"Found {result.Count} libraries in Mart");
            }
            catch (Exception ex)
            {
                LastError = $"Error getting root catalog: {ex.Message}";
                Log(LogLevel.Error, LastError);
            }

            return result;
        }

        private List<CatalogNode> DiscoverLibraries()
        {
            // Cannot enumerate libraries dynamically via SCAPI
            // Return empty list - user must enter path manually
            Log(LogLevel.Debug, "  SCAPI cannot enumerate libraries dynamically - returning empty list");
            return new List<CatalogNode>();
        }

        private bool TryConnectLibrary(string libName, out string actualName, out dynamic libDir)
        {
            actualName = null;
            libDir = null;

            try
            {
                var libLocator = BuildMartLocator(_connectionInfo, libName);
                Log(LogLevel.Debug, $"      Trying: {libName}");
                Log(LogLevel.Debug, $"      Locator: {libLocator.Replace(_connectionInfo.Password, "****")}");

                libDir = _scapi.ModelDirectories.Add(libLocator);

                if (libDir == null)
                {
                    Log(LogLevel.Debug, $"      {libName}: null");
                    return false;
                }

                actualName = (string)libDir.Name;
                var dirType = (int)libDir.Type;
                var dirLocator = (string)libDir.Locator;
                var dirFlags = (int)libDir.Flags;
                Log(LogLevel.Debug, $"      {libName}: OK (Type={dirType}, Flags={dirFlags})");
                Log(LogLevel.Debug, $"      Returned Locator: '{dirLocator}'");

                // Type 2 seems to be a valid Mart library
                return true;
            }
            catch (Exception ex)
            {
                Log(LogLevel.Debug, $"      {libName}: {ex.Message}");
                return false;
            }
        }

        public List<CatalogNode> GetChildren(string parentIdentifier)
        {
            var result = new List<CatalogNode>();

            if (_scapi == null)
            {
                LastError = "SCAPI not initialized";
                return result;
            }

            if (string.IsNullOrEmpty(parentIdentifier))
            {
                return GetRootCatalog();
            }

            try
            {
                Log(LogLevel.Debug, $"Getting children for: {parentIdentifier}");

                // First check our cache of connected libraries
                dynamic parentDir = null;

                if (_connectedLibraries.TryGetValue(parentIdentifier, out var cachedDir))
                {
                    parentDir = cachedDir;
                    Log(LogLevel.Debug, $"  Using cached library connection: {parentIdentifier}");
                }
                else
                {
                    // Try to find in ModelDirectories collection
                    var dirCount = _scapi.ModelDirectories.Count;
                    Log(LogLevel.Debug, $"  ModelDirectories count: {dirCount}");

                    for (int i = 1; i <= dirCount; i++)
                    {
                        var dir = _scapi.ModelDirectories.Item(i);
                        var dirName = (string)dir.Name;

                        if (dirName.Equals(parentIdentifier, StringComparison.OrdinalIgnoreCase))
                        {
                            parentDir = dir;
                            Log(LogLevel.Debug, $"  Found in ModelDirectories: {dirName}");
                            break;
                        }
                    }

                    // If still not found, try to connect
                    if (parentDir == null && _connectionInfo != null)
                    {
                        Log(LogLevel.Debug, $"  Connecting to: {parentIdentifier}");
                        var locatorToUse = BuildMartLocator(_connectionInfo, parentIdentifier);

                        try
                        {
                            parentDir = _scapi.ModelDirectories.Add(locatorToUse);
                            if (parentDir != null)
                            {
                                var actualName = (string)parentDir.Name;
                                _connectedLibraries[actualName] = parentDir;
                                Log(LogLevel.Debug, $"  Connected to: {actualName}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Debug, $"  Failed to connect: {ex.Message}");
                        }
                    }
                }

                if (parentDir == null)
                {
                    LastError = $"Could not access directory: {parentIdentifier}";
                    Log(LogLevel.Warning, LastError);
                    return result;
                }

                // Debug: Dump directory properties
                DumpDirectoryProperties(parentDir);

                // Try to check if DirectoryExists returns useful info
                Log(LogLevel.Debug, "  Checking DirectoryExists for known patterns...");
                try
                {
                    // Try to find if specific subdirectories exist (from user's screenshot)
                    var knownSubdirs = new[] { "PublicationSystemSample_Modified", "Test", "Sample", "*" };
                    foreach (var subName in knownSubdirs)
                    {
                        try
                        {
                            bool exists = parentDir.DirectoryExists(subName);
                            Log(LogLevel.Debug, $"    DirectoryExists('{subName}'): {exists}");
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Debug, $"    DirectoryExists('{subName}'): error - {ex.Message}");
                        }
                    }
                }
                catch { }

                // Try DirectoryUnitExists for known models
                Log(LogLevel.Debug, "  Checking DirectoryUnitExists...");
                try
                {
                    bool exists = parentDir.DirectoryUnitExists("*");
                    Log(LogLevel.Debug, $"    DirectoryUnitExists('*'): {exists}");
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, $"    DirectoryUnitExists error: {ex.Message}");
                }

                // Enumerate subdirectories (categories)
                Log(LogLevel.Debug, "  Enumerating subdirectories...");
                try
                {
                    // Try different Locator values for enumeration
                    string[] locatorPatterns = { "", "*", null };
                    foreach (var pattern in locatorPatterns)
                    {
                        try
                        {
                            Log(LogLevel.Debug, $"    Trying LocateDirectory with pattern: '{pattern ?? "null"}'");
                            dynamic subDir;
                            if (pattern == null)
                                subDir = parentDir.LocateDirectory();
                            else
                                subDir = parentDir.LocateDirectory(pattern);

                            int count = 0;
                            while (subDir != null)
                            {
                                var name = (string)subDir.Name;
                                Log(LogLevel.Debug, $"      Found subdirectory: {name}");
                                result.Add(new CatalogNode(name, $"{parentIdentifier}/{name}", CatalogNodeType.Directory, true));
                                subDir = parentDir.LocateDirectoryNext();
                                count++;
                            }
                            Log(LogLevel.Debug, $"    Pattern '{pattern ?? "null"}' found {count} subdirectories");
                            if (count > 0) break;
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Debug, $"    Pattern '{pattern ?? "null"}' error: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, $"    Subdirectory enumeration error: {ex.Message}");
                }

                // Enumerate models (directory units)
                Log(LogLevel.Debug, "  Enumerating models...");
                try
                {
                    string[] locatorPatterns = { "", "*", null };
                    foreach (var pattern in locatorPatterns)
                    {
                        try
                        {
                            Log(LogLevel.Debug, $"    Trying LocateDirectoryUnit with pattern: '{pattern ?? "null"}'");
                            dynamic model;
                            if (pattern == null)
                                model = parentDir.LocateDirectoryUnit();
                            else
                                model = parentDir.LocateDirectoryUnit(pattern);

                            int count = 0;
                            while (model != null)
                            {
                                var name = (string)model.Name;
                                var locator = (string)model.Locator;
                                Log(LogLevel.Debug, $"      Found model: {name}");
                                result.Add(new CatalogNode(name, locator, CatalogNodeType.Model, false));
                                model = parentDir.LocateDirectoryUnitNext();
                                count++;
                            }
                            Log(LogLevel.Debug, $"    Pattern '{pattern ?? "null"}' found {count} models");
                            if (count > 0) break;
                        }
                        catch (Exception ex)
                        {
                            Log(LogLevel.Debug, $"    Pattern '{pattern ?? "null"}' error: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, $"    Model enumeration error: {ex.Message}");
                }

                Log(LogLevel.Info, $"Found {result.Count} items in {parentIdentifier}");
            }
            catch (Exception ex)
            {
                LastError = $"Error getting children: {ex.Message}";
                Log(LogLevel.Error, LastError);
            }

            return result;
        }

        private void DumpDirectoryProperties(dynamic directory)
        {
            try
            {
                Log(LogLevel.Debug, "  === Directory Properties ===");

                try { Log(LogLevel.Debug, $"    Name: {directory.Name}"); } catch { }
                try { Log(LogLevel.Debug, $"    Locator: {directory.Locator}"); } catch { }
                try { Log(LogLevel.Debug, $"    Type: {directory.Type}"); } catch { }
                try { Log(LogLevel.Debug, $"    Flags: {directory.Flags}"); } catch { }

                // Try to get PropertyBag
                try
                {
                    var propBag = directory.PropertyBag();
                    if (propBag != null)
                    {
                        Log(LogLevel.Debug, $"    PropertyBag count: {propBag.Count}");
                        for (int i = 1; i <= propBag.Count; i++)
                        {
                            try
                            {
                                var propName = propBag.Name(i);
                                var propValue = propBag.Value(propName);
                                Log(LogLevel.Debug, $"      [{i}] {propName} = {propValue}");
                            }
                            catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(LogLevel.Debug, $"    PropertyBag error: {ex.Message}");
                }

                Log(LogLevel.Debug, "  === End Properties ===");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Debug, $"  Property dump error: {ex.Message}");
            }
        }

        private string BuildMartLocator(MartConnectionInfo connectionInfo, string path = "")
        {
            // Build SCAPI-compatible Mart locator
            // There are multiple possible formats to try:
            // Format 1: mart://Mart/path?SRV=server;PRT=port;ASR=MartServer;UID=user;PSW=password
            // Format 2: mart://server:port/Mart/path with ASR parameter
            // Format 3: mart://Mart?TRC=NO;SRV=server;... (old format)

            var server = connectionInfo.ServerName;
            var port = connectionInfo.Port;

            // Build locator string with connection parameters
            // Use forward slash after Mart, then path, then ? for parameters
            var pathPart = string.IsNullOrEmpty(path) ? "" : $"{path}";

            // Format: mart://Mart/LibraryName?SRV=server;PRT=port;ASR=MartServer;UID=user;PSW=password
            var locator = $"mart://Mart/{pathPart}?SRV={server};PRT={port};ASR=MartServer;UID={connectionInfo.Username};PSW={connectionInfo.Password}";

            return locator;
        }

        private string BuildMartLocatorAlt(MartConnectionInfo connectionInfo, string path = "")
        {
            // Alternative format: try including ASR before other params
            var server = connectionInfo.ServerName;
            var port = connectionInfo.Port;
            var pathPart = string.IsNullOrEmpty(path) ? "" : $"{path}";

            // Try: mart://Mart/path?ASR=MartServer;SRV=server;PRT=port;UID=user;PSW=password
            var locator = $"mart://Mart/{pathPart}?ASR=MartServer;SRV={server};PRT={port};UID={connectionInfo.Username};PSW={connectionInfo.Password}";

            return locator;
        }

        private void Log(LogLevel level, string message)
        {
            LogMessage?.Invoke(this, new LogEventArgs(level, message, null));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Disconnect();
            _scapi = null;
        }
    }
}
