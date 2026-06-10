using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.MetaAdmin.Shared.Services;
using Microsoft.Win32;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Add-in side bootstrap config reader. Probes HKCU FIRST, then HKLM
    /// (precedence flipped 2026-06-08). The DPAPI scope used to decrypt the
    /// encrypted fields is derived from whichever hive the values actually came
    /// from: CurrentUser for HKCU, LocalMachine for HKLM. This lets a single
    /// binary serve three real-world deployments:
    ///
    ///   - personal user install: config read from HKCU (the common case)
    ///   - corporate GPO/MSI seeded: only HKLM populated, HKCU absent -> HKLM
    ///   - dev / migration / override: the user's current HKCU wins over any
    ///     stale machine-wide HKLM seed, so admin changes take effect without
    ///     first clearing HKLM
    ///
    /// HKLM is read-only from the addin's perspective; corporate IT seeds it
    /// with its own tooling (and is responsible for encrypting credentials
    /// with LocalMachine DPAPI scope). install-impl.ps1 writes HKCU only.
    /// The class name is historical - HKCU is now read first.
    ///
    /// See docs/INSTALL.md for the complete decision tree.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class HklmFirstBootstrapReader : IBootstrapService
    {
        private const string SubKeyPath = @"Software\EliteSoft\MetaRepo\Bootstrap";
        private BootstrapConfig _cachedConfig;
        private string _cachedSourceHive;

        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            // Precedence flipped 2026-06-08 (user request): HKCU FIRST, then HKLM.
            // The per-user HKCU seed (what install.bat / the dev edits, and what
            // the admin tool points at) now wins; HKLM is the corporate/shared
            // fallback. Previously HKLM-first meant a stale machine-wide seed
            // (e.g. an old KKBDemo bootstrap) silently shadowed the user's
            // current HKCU config, so admin changes were invisible to the add-in.
            // A populated seed must carry both DBHost and DBName; a partial seed
            // is treated as absent and we fall through.
            var hkcuConfig = TryReadHive(Registry.CurrentUser, DataProtectionScope.CurrentUser);
            if (hkcuConfig != null && hkcuConfig.IsConfigured)
            {
                _cachedConfig = hkcuConfig;
                _cachedSourceHive = "HKCU";
                return _cachedConfig;
            }

            var hklmConfig = TryReadHive(Registry.LocalMachine, DataProtectionScope.LocalMachine);
            if (hklmConfig != null)
            {
                _cachedConfig = hklmConfig;
                _cachedSourceHive = "HKLM";
                return _cachedConfig;
            }

            _cachedSourceHive = null;
            return null;
        }

        public bool IsConfigured()
        {
            var config = GetConfig();
            return config != null && config.IsConfigured;
        }

        public void ClearCache()
        {
            _cachedConfig = null;
            _cachedSourceHive = null;
        }

        /// <summary>
        /// In-memory override of the cached config WITHOUT touching the registry.
        /// Part of the IBootstrapService contract (added to MetaShared); mirrors
        /// RegistryBootstrapService.OverrideConfig. Used by short-lived/headless
        /// flows that must run against a specific connection without disturbing
        /// the stored HKLM/HKCU Bootstrap. The add-in itself never writes the
        /// registry (see SaveConfig), so this is the only runtime path to point
        /// the reader at a caller-supplied config.
        /// </summary>
        public void OverrideConfig(BootstrapConfig config)
        {
            _cachedConfig = config;
            _cachedSourceHive = "OVERRIDE";
        }

        /// <summary>
        /// Diagnostic getter: indicates which hive the cached config came from
        /// ("HKLM" or "HKCU"). Returns null when no config is cached or none
        /// could be read. Useful for surfacing the active source in UI labels
        /// without exposing internal Registry handles.
        /// </summary>
        public string CachedSourceHive => _cachedSourceHive;

        /// <summary>
        /// Add-in never writes bootstrap config at runtime. install-impl.ps1 owns
        /// the write path (HKCU only, DPAPI CurrentUser). This method is part
        /// of the IBootstrapService contract; calling it throws to make
        /// accidental misuse obvious rather than silently corrupting state.
        /// </summary>
        public void SaveConfig(BootstrapConfig config)
        {
            throw new NotSupportedException(
                "Add-in does not write bootstrap config at runtime. " +
                "Run installer/install.bat to seed HKCU. HKLM is corporate IT's responsibility.");
        }

        /// <summary>
        /// Add-in never deletes bootstrap config at runtime. Uninstall is
        /// driven by install-impl.ps1 -Uninstall (HKCU only).
        /// </summary>
        public void DeleteConfig()
        {
            throw new NotSupportedException(
                "Add-in does not delete bootstrap config at runtime. " +
                "Run installer/uninstall.bat to remove HKCU values.");
        }

        public string GetConfigFilePath()
        {
            // Reflect the actual source hive so error messages stay accurate.
            // Before any read has resolved a hive, list both candidates so the
            // user knows the resolver tried both.
            if (_cachedSourceHive == "HKLM") return $@"HKLM\{SubKeyPath}";
            if (_cachedSourceHive == "HKCU") return $@"HKCU\{SubKeyPath}";
            return $@"HKLM\{SubKeyPath} or HKCU\{SubKeyPath}";
        }

        private static BootstrapConfig TryReadHive(RegistryKey hive, DataProtectionScope dpapiScope)
        {
            using (var key = hive.OpenSubKey(SubKeyPath))
            {
                if (key == null) return null;

                var host = key.GetValue("DBHost") as string;
                var database = key.GetValue("DBName") as string;
                // Empty-but-present DBHost/DBName means "not configured here" -
                // do not return a partial config that would short-circuit the
                // HKCU fallback. The MachineKey path always demands both.
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database))
                    return null;

                return new BootstrapConfig
                {
                    DbType = (key.GetValue("DBType") as string) ?? "MSSQL",
                    Host = host,
                    Port = (key.GetValue("DBPort") as string) ?? "1433",
                    Database = database,
                    Username = DpapiDecrypt(key.GetValue("DBUserName") as string, dpapiScope),
                    Password = DpapiDecrypt(key.GetValue("DBPassword") as string, dpapiScope),
                };
            }
        }

        /// <summary>
        /// DPAPI Unprotect with the supplied scope. An empty or null input
        /// returns empty. A decryption failure (wrong scope, corrupt blob,
        /// foreign DPAPI master key) is rethrown so the caller surfaces a
        /// real error instead of silently returning ciphertext - matches the
        /// project rule against swallowing exceptions.
        /// </summary>
        private static string DpapiDecrypt(string base64Cipher, DataProtectionScope scope)
        {
            if (string.IsNullOrEmpty(base64Cipher))
                return "";

            var cipherBytes = Convert.FromBase64String(base64Cipher);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, scope);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
    }
}
