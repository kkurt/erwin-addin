using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.MetaAdmin.Shared.Services;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Registry-based bootstrap config reader.
    /// Scope (HKCU vs HKLM) determined by registry.scope file next to the executable.
    /// DPAPI encryption follows scope: HKLM = LocalMachine, HKCU = CurrentUser.
    /// </summary>
    [ComVisible(false)]
    public class RegistryBootstrapService : IBootstrapService
    {
        private const string BaseKey = @"Software\EliteSoft\MetaRepo";
        private const string SubKey = "Bootstrap";
        private BootstrapConfig _cachedConfig;

        private static bool? _useMachineScope;

        /// <summary>
        /// Determines registry scope from registry.scope file.
        /// HKLM = machine-wide (production), HKCU = per-user (development, default).
        /// </summary>
        private static bool UseMachineScope
        {
            get
            {
                if (_useMachineScope == null)
                {
                    try
                    {
                        // CANNOT use AppContext.BaseDirectory: for a COM-hosted DLL
                        // loaded into erwin.exe, that returns erwin's install dir,
                        // not ours. Use this assembly's own location instead so the
                        // scope file next to our DLL (written by install.ps1) is found.
                        var asmDir = Path.GetDirectoryName(typeof(RegistryBootstrapService).Assembly.Location);
                        var scopeFile = Path.Combine(asmDir ?? string.Empty, "registry.scope");
                        if (File.Exists(scopeFile))
                        {
                            var content = File.ReadAllText(scopeFile).Trim();
                            _useMachineScope = content.Equals("HKLM", StringComparison.OrdinalIgnoreCase);
                        }
                        else
                        {
                            _useMachineScope = false; // default: HKCU
                        }
                    }
                    catch
                    {
                        _useMachineScope = false;
                    }
                    System.Diagnostics.Debug.WriteLine($"RegistryBootstrapService: Scope = {(_useMachineScope.Value ? "HKLM" : "HKCU")}");
                }
                return _useMachineScope.Value;
            }
        }

        private static RegistryKey RootKey => UseMachineScope ? Registry.LocalMachine : Registry.CurrentUser;

        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            _cachedConfig = ReadFromRegistry();
            return _cachedConfig;
        }

        private BootstrapConfig ReadFromRegistry()
        {
            var fullKey = $@"{BaseKey}\{SubKey}";

            using (var key = RootKey.OpenSubKey(fullKey))
            {
                if (key == null)
                    return null;

                try
                {
                    var host = key.GetValue("Host", "")?.ToString() ?? "";
                    var database = key.GetValue("Database", "")?.ToString() ?? "";

                    // IsConfigured is computed: Host + Database must be non-empty
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database))
                        return null;

                    var encryptedUsername = key.GetValue("Username", "")?.ToString() ?? "";
                    var encryptedPassword = key.GetValue("Password", "")?.ToString() ?? "";

                    // DPAPI scope follows registry scope
                    var dpapiScope = UseMachineScope
                        ? DataProtectionScope.LocalMachine
                        : DataProtectionScope.CurrentUser;

                    string username = PasswordEncryptionService.Decrypt(encryptedUsername, dpapiScope)
                                      ?? encryptedUsername;
                    string password = PasswordEncryptionService.Decrypt(encryptedPassword, dpapiScope)
                                      ?? encryptedPassword;

                    return new BootstrapConfig
                    {
                        DbType = key.GetValue("DbType", "MSSQL")?.ToString() ?? "MSSQL",
                        Host = host,
                        Port = key.GetValue("Port", "1433")?.ToString() ?? "1433",
                        Database = database,
                        Username = username,
                        Password = password
                    };
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"RegistryBootstrapService: Read error: {ex.Message}");
                    return null;
                }
            }
        }

        public void SaveConfig(BootstrapConfig config)
        {
            // Read-only from add-in side; configuration is done in erwin-admin
        }

        public bool IsConfigured()
        {
            var config = GetConfig();
            return config != null && config.IsConfigured;
        }

        public void ClearCache()
        {
            _cachedConfig = null;
        }

        public void DeleteConfig()
        {
            // Read-only from add-in side
        }

        public string GetConfigFilePath()
        {
            var hive = UseMachineScope ? "HKLM" : "HKCU";
            return $@"{hive}\Software\EliteSoft\MetaRepo\Bootstrap";
        }
    }
}
