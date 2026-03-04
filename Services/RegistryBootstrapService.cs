using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using EliteSoft.MetaCenter.Shared.Models;
using EliteSoft.MetaCenter.Shared.Services;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Registry-based bootstrap config reader.
    /// Reads Admin DB connection from Windows Registry (HKCU\Software\EliteSoft\MetaCenter\Bootstrap).
    /// Credentials are DPAPI-encrypted, matching erwin-admin's RegistryBootstrapService.
    /// </summary>
    [ComVisible(false)]
    public class RegistryBootstrapService : IBootstrapService
    {
        private const string BaseKey = @"Software\EliteSoft\MetaCenter";
        private const string SubKey = "Bootstrap";
        private BootstrapConfig _cachedConfig;

        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            var fullKey = $@"{BaseKey}\{SubKey}";

            using (var key = Registry.CurrentUser.OpenSubKey(fullKey))
            {
                if (key == null)
                    return null;

                var isConfigured = key.GetValue("IsConfigured");
                if (isConfigured == null || (isConfigured is int intVal && intVal == 0))
                    return null;

                try
                {
                    var encryptedUsername = key.GetValue("Username", "")?.ToString() ?? "";
                    var encryptedPassword = key.GetValue("Password", "")?.ToString() ?? "";

                    _cachedConfig = new BootstrapConfig
                    {
                        DbType = key.GetValue("DbType", "MSSQL")?.ToString() ?? "MSSQL",
                        Host = key.GetValue("Host", "localhost")?.ToString() ?? "localhost",
                        Port = key.GetValue("Port", "1433")?.ToString() ?? "1433",
                        Database = key.GetValue("Database", "")?.ToString() ?? "",
                        Username = PasswordEncryptionService.Decrypt(encryptedUsername) ?? encryptedUsername,
                        Password = PasswordEncryptionService.Decrypt(encryptedPassword) ?? encryptedPassword,
                        IsConfigured = true
                    };
                    return _cachedConfig;
                }
                catch
                {
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
            return @"HKCU\Software\EliteSoft\MetaCenter\Bootstrap";
        }
    }
}
