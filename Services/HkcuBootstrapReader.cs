using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.MetaAdmin.Shared.Services;
using Microsoft.Win32;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Add-in side bootstrap config reader. Reads HKCU ONLY (per-user hive):
    /// <c>HKCU\Software\EliteSoft\MetaRepo\Bootstrap</c>, with the encrypted
    /// username/password fields decrypted using DPAPI CurrentUser scope.
    ///
    /// HKLM reading was removed 2026-07-02 (user decision). Rationale: the
    /// add-in is deployed exclusively through install-impl.ps1, which writes
    /// HKCU only, and the historical machine-wide HKLM seed path was never used
    /// in practice. Reading a single hive:
    ///   - removes the "stale HKLM shadows the current HKCU" class of bugs that
    ///     the earlier HKCU-first-then-HKLM precedence was already fighting, and
    ///   - drops the per-hive DPAPI-scope branching (HKLM = LocalMachine scope,
    ///     HKCU = CurrentUser scope) that made credential decryption ambiguous.
    /// Machine-wide / corporate seeding, if ever reintroduced, must go through a
    /// deliberate design rather than a silent fallback here.
    ///
    /// The add-in never writes bootstrap config at runtime (see
    /// <see cref="SaveConfig"/>); install-impl.ps1 owns the write path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public class HkcuBootstrapReader : IBootstrapService
    {
        private const string SubKeyPath = @"Software\EliteSoft\MetaRepo\Bootstrap";
        private BootstrapConfig _cachedConfig;

        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig != null)
                return _cachedConfig;

            // HKCU only. A populated seed must carry both DBHost and DBName;
            // an empty-but-present or partial seed is treated as absent (null).
            _cachedConfig = TryReadHkcu();
            return _cachedConfig;
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

        /// <summary>
        /// In-memory override of the cached config WITHOUT touching the registry.
        /// Part of the IBootstrapService contract; used by short-lived/headless
        /// flows that must run against a specific connection without disturbing
        /// the stored HKCU Bootstrap. The add-in itself never writes the registry
        /// (see <see cref="SaveConfig"/>), so this is the only runtime path to
        /// point the reader at a caller-supplied config.
        /// </summary>
        public void OverrideConfig(BootstrapConfig config)
        {
            _cachedConfig = config;
        }

        /// <summary>
        /// Add-in never writes bootstrap config at runtime. install-impl.ps1 owns
        /// the write path (HKCU only, DPAPI CurrentUser). This method is part of
        /// the IBootstrapService contract; calling it throws to make accidental
        /// misuse obvious rather than silently corrupting state.
        /// </summary>
        public void SaveConfig(BootstrapConfig config)
        {
            throw new NotSupportedException(
                "Add-in does not write bootstrap config at runtime. " +
                "Run installer/install.bat to seed HKCU.");
        }

        /// <summary>
        /// Add-in never deletes bootstrap config at runtime. Uninstall is driven
        /// by install-impl.ps1 -Uninstall (HKCU only).
        /// </summary>
        public void DeleteConfig()
        {
            throw new NotSupportedException(
                "Add-in does not delete bootstrap config at runtime. " +
                "Run installer/uninstall.bat to remove HKCU values.");
        }

        public string GetConfigFilePath() => $@"HKCU\{SubKeyPath}";

        private static BootstrapConfig TryReadHkcu()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(SubKeyPath))
            {
                if (key == null) return null;

                var host = key.GetValue("DBHost") as string;
                var database = key.GetValue("DBName") as string;
                // Empty-but-present DBHost/DBName means "not configured" - do not
                // return a partial config. Both are required.
                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database))
                    return null;

                return new BootstrapConfig
                {
                    DbType = (key.GetValue("DBType") as string) ?? "MSSQL",
                    Host = host,
                    Port = (key.GetValue("DBPort") as string) ?? "1433",
                    Database = database,
                    Username = DpapiDecrypt(key.GetValue("DBUserName") as string),
                    Password = DpapiDecrypt(key.GetValue("DBPassword") as string),
                };
            }
        }

        /// <summary>
        /// DPAPI Unprotect with CurrentUser scope (HKCU credentials are always
        /// encrypted under the current user's DPAPI master key by
        /// install-impl.ps1). An empty or null input returns empty. A decryption
        /// failure (corrupt blob, or a blob copied from another user/machine
        /// whose master key is not present here) is rethrown so the caller
        /// surfaces a real error instead of silently returning ciphertext -
        /// matches the project rule against swallowing exceptions.
        /// </summary>
        private static string DpapiDecrypt(string base64Cipher)
        {
            if (string.IsNullOrEmpty(base64Cipher))
                return "";

            var cipherBytes = Convert.FromBase64String(base64Cipher);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(plainBytes);
        }
    }
}
