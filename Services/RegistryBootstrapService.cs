using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.MetaAdmin.Shared.Services;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Registry-based bootstrap config reader.
    /// Reads Admin DB connection from HKCU (per-user, set by erwin-admin).
    /// Credentials are DPAPI-encrypted with CurrentUser scope.
    /// </summary>
    [ComVisible(false)]
    public class RegistryBootstrapService : IBootstrapService
    {
        private const string BaseKey = @"Software\EliteSoft\MetaRepo";
        private const string SubKey = "Bootstrap";
        private BootstrapConfig _cachedConfig;

        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            // Read from HKCU only (per-user)
            _cachedConfig = ReadFromRegistry(Registry.CurrentUser, useMachineScope: false);
            return _cachedConfig;
        }

        private BootstrapConfig ReadFromRegistry(RegistryKey root, bool useMachineScope)
        {
            var fullKey = $@"{BaseKey}\{SubKey}";

            using (var key = root.OpenSubKey(fullKey))
            {
                if (key == null)
                    return null;

                try
                {
                    var host = key.GetValue("Host", "")?.ToString() ?? "";
                    var database = key.GetValue("Database", "")?.ToString() ?? "";

                    // IsConfigured is now computed: Host + Database must be non-empty
                    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database))
                        return null;

                    var encryptedUsername = key.GetValue("Username", "")?.ToString() ?? "";
                    var encryptedPassword = key.GetValue("Password", "")?.ToString() ?? "";

                    string username = PasswordEncryptionService.Decrypt(encryptedUsername) ?? encryptedUsername;
                    string password = PasswordEncryptionService.Decrypt(encryptedPassword) ?? encryptedPassword;

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
            return @"HKCU\Software\EliteSoft\MetaRepo\Bootstrap";
        }
    }
}
