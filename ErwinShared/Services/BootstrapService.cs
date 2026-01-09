using System;
using System.IO;
using EliteSoft.Erwin.Shared.Models;
using Newtonsoft.Json;

namespace EliteSoft.Erwin.Shared.Services
{
    /// <summary>
    /// Service for managing local bootstrap configuration.
    /// Stores Repository DB connection info in a local JSON file.
    /// </summary>
    public class BootstrapService
    {
        private readonly string _configFilePath;
        private BootstrapConfig _cachedConfig;

        public BootstrapService()
        {
            // Store in user's AppData folder
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var configDir = Path.Combine(appDataPath, "EliteSoft", "ErwinExtension");

            if (!Directory.Exists(configDir))
            {
                Directory.CreateDirectory(configDir);
            }

            _configFilePath = Path.Combine(configDir, "bootstrap.json");
        }

        /// <summary>
        /// Gets the bootstrap configuration. Returns null if not configured.
        /// </summary>
        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
            {
                return _cachedConfig;
            }

            if (!File.Exists(_configFilePath))
            {
                return null;
            }

            try
            {
                var json = File.ReadAllText(_configFilePath);
                _cachedConfig = JsonConvert.DeserializeObject<BootstrapConfig>(json);
                return _cachedConfig;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Saves the bootstrap configuration to local file.
        /// </summary>
        public void SaveConfig(BootstrapConfig config)
        {
            config.IsConfigured = true;
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(_configFilePath, json);
            _cachedConfig = config;
        }

        /// <summary>
        /// Checks if bootstrap configuration exists and is valid.
        /// </summary>
        public bool IsConfigured()
        {
            var config = GetConfig();
            return config != null && config.IsConfigured;
        }

        /// <summary>
        /// Clears the cached configuration (forces reload on next GetConfig).
        /// </summary>
        public void ClearCache()
        {
            _cachedConfig = null;
        }

        /// <summary>
        /// Deletes the bootstrap configuration file.
        /// </summary>
        public void DeleteConfig()
        {
            if (File.Exists(_configFilePath))
            {
                File.Delete(_configFilePath);
            }
            _cachedConfig = null;
        }

        /// <summary>
        /// Gets the path to the config file (for debugging/info purposes).
        /// </summary>
        public string GetConfigFilePath()
        {
            return _configFilePath;
        }
    }
}
