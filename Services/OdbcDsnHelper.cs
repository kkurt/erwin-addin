using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Raised when no usable SQL Server ODBC driver could be located.
    /// <see cref="Exception.Message"/> is a user-friendly explanation suitable for UI display;
    /// <see cref="TechnicalDetail"/> is the diagnostic detail for logs.
    /// </summary>
    public class OdbcDriverNotFoundException : Exception
    {
        public string TechnicalDetail { get; }

        public OdbcDriverNotFoundException(string userMessage, string technicalDetail)
            : base(userMessage)
        {
            TechnicalDetail = technicalDetail;
        }
    }


    /// <summary>
    /// Creates and deletes transient User ODBC DSN entries so erwin SCAPI RE
    /// (which only accepts ODBC DSN via connStr `5=&lt;name&gt;`) can be driven
    /// WITHOUT requiring the user to pre-create a DSN.
    ///
    /// All entries are written under HKCU (no admin needed) and are prefixed with
    /// <see cref="DsnPrefix"/> so stale entries from crashed runs can be swept out.
    /// </summary>
    public static class OdbcDsnHelper
    {
        public const string DsnPrefix = "EliteSoft_Temp_";
        private const string OdbcIniPath = @"Software\ODBC\ODBC.INI";
        private const string OdbcSourcesPath = @"Software\ODBC\ODBC.INI\ODBC Data Sources";
        private const string OdbcDriversPath = @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers";

        /// <summary>
        /// Preflight result describing whether a usable SQL Server ODBC driver exists.
        /// </summary>
        public class DriverCheck
        {
            public bool IsOk { get; set; }
            public string DriverName { get; set; }
            public string DllPath { get; set; }
            /// <summary>User-friendly error message when IsOk=false.</summary>
            public string UserMessage { get; set; }
            /// <summary>Extra technical detail for logging.</summary>
            public string TechnicalDetail { get; set; }
        }

        /// <summary>
        /// Verify an installed SQL Server ODBC driver is actually usable:
        /// registry entry exists, Driver= value points to a file that is present on disk.
        /// Returns the best usable driver, or a populated DriverCheck explaining the failure.
        /// </summary>
        public static DriverCheck CheckSqlServerDriver(Action<string> log)
        {
            var installed = ListSqlServerDrivers();
            if (installed.Count == 0)
            {
                return new DriverCheck
                {
                    IsOk = false,
                    UserMessage =
                        "No Microsoft SQL Server ODBC driver is installed on this machine.\n\n" +
                        "Please install 'Microsoft ODBC Driver 18 for SQL Server' (recommended) or 'ODBC Driver 17'.\n" +
                        "Download: https://learn.microsoft.com/sql/connect/odbc/download-odbc-driver-for-sql-server",
                    TechnicalDetail =
                        $@"HKLM\{OdbcDriversPath} has no SQL Server entry."
                };
            }

            // Order matters: erwin SCAPI is verified to talk to the legacy "SQL Server"
            // ODBC driver (SQLSRV32.dll). Modern "ODBC Driver 17/18" silently fail in
            // RE on this install. Try legacy first; fall back to modern only if missing.
            string[] preferred = {
                "SQL Server",
                "SQL Server Native Client 11.0",
                "SQL Server Native Client 10.0",
                "ODBC Driver 18 for SQL Server",
                "ODBC Driver 17 for SQL Server"
            };

            var ordered = preferred.Where(installed.Contains)
                                   .Concat(installed.Except(preferred))
                                   .Distinct()
                                   .ToList();

            var failures = new List<string>();
            foreach (var name in ordered)
            {
                string dll;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\ODBC\ODBCINST.INI\{name}");
                    dll = key?.GetValue("Driver")?.ToString();
                }
                catch (Exception ex)
                {
                    failures.Add($"{name}: registry read error: {ex.Message}");
                    continue;
                }
                if (string.IsNullOrWhiteSpace(dll))
                {
                    failures.Add($"{name}: registry has no 'Driver' value.");
                    continue;
                }
                if (!File.Exists(dll))
                {
                    failures.Add($"{name}: DLL missing on disk ({dll}).");
                    continue;
                }

                log?.Invoke($"OdbcDsn: driver check OK — '{name}' at '{dll}'");
                return new DriverCheck { IsOk = true, DriverName = name, DllPath = dll };
            }

            return new DriverCheck
            {
                IsOk = false,
                UserMessage =
                    "A SQL Server ODBC driver is registered in Windows but the underlying DLL is missing.\n\n" +
                    "Please reinstall 'Microsoft ODBC Driver 18 for SQL Server'.\n" +
                    "Download: https://learn.microsoft.com/sql/connect/odbc/download-odbc-driver-for-sql-server",
                TechnicalDetail = string.Join(" | ", failures)
            };
        }

        /// <summary>
        /// Create a transient SQL Server DSN in HKCU and return its name.
        /// Picks the newest installed SQL Server ODBC driver automatically.
        /// Caller MUST call <see cref="DeleteDsn"/> in a finally block.
        /// </summary>
        /// <summary>
        /// Create a transient SQL Server User DSN.
        /// DSN is always written with Trusted_Connection=Yes — verified to be the value
        /// erwin's RE wizard creates and works for BOTH SQL auth and Windows auth (the
        /// auth mode is decided by AUTHENTICATION=4|8 in the connection string passed
        /// to ReverseEngineer, not by this DSN field).
        /// </summary>
        public static string CreateTempSqlServerDsn(
            string server, string database, string userName, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(server))
                throw new ArgumentException("server is required", nameof(server));
            if (string.IsNullOrWhiteSpace(database))
                throw new ArgumentException("database is required", nameof(database));

            // Preflight: verify a usable driver is installed (registry + DLL on disk).
            var check = CheckSqlServerDriver(log);
            if (!check.IsOk)
                throw new OdbcDriverNotFoundException(check.UserMessage, check.TechnicalDetail);

            string driverName = check.DriverName;
            string driverPath = check.DllPath;

            string dsnName = DsnPrefix + Guid.NewGuid().ToString("N").Substring(0, 12);
            log?.Invoke($"OdbcDsn: creating temp DSN '{dsnName}' driver='{driverName}' dll='{driverPath}'");

            // erwin RE accepts User DSN (HKCU) — verified via the wizard's API Connection
            // String against a manually-created User DSN. HKLM write would need admin and
            // is not required.
            bool hkcuOk = WriteDsnEntries(Registry.CurrentUser, dsnName, driverName, driverPath,
                                          server, database, userName, "HKCU", log);
            if (!hkcuOk)
                throw new InvalidOperationException(
                    "Could not write User DSN to HKCU. Check registry permissions.");

            log?.Invoke($"OdbcDsn: User DSN '{dsnName}' written (Trusted_Connection=Yes)");
            return dsnName;
        }

        /// <summary>
        /// Try to write DSN config + index entry under the given root.
        /// Returns false (without throwing) if access is denied — caller decides whether that's fatal.
        /// </summary>
        private static bool WriteDsnEntries(RegistryKey root, string dsnName, string driverName,
            string driverPath, string server, string database, string userName,
            string rootLabel, Action<string> log)
        {
            try
            {
                using (var dsnKey = root.CreateSubKey($@"{OdbcIniPath}\{dsnName}"))
                {
                    if (dsnKey == null) { log?.Invoke($"OdbcDsn: {rootLabel} CreateSubKey returned null"); return false; }
                    // EXACT minimal value set verified against a working user-created DSN.
                    // No Trusted_Connection (default), no Description — extras may make SCAPI reject.
                    dsnKey.SetValue("Driver", driverPath);
                    dsnKey.SetValue("Server", server);
                    dsnKey.SetValue("Database", database);
                    dsnKey.SetValue("LastUser", userName ?? "");
                }
                using (var sourcesKey = root.CreateSubKey(OdbcSourcesPath))
                {
                    if (sourcesKey == null) { log?.Invoke($"OdbcDsn: {rootLabel} sources null"); return false; }
                    sourcesKey.SetValue(dsnName, driverName);
                }
                return true;
            }
            catch (System.Security.SecurityException sex)
            {
                log?.Invoke($"OdbcDsn: {rootLabel} write blocked (need admin?): {sex.Message}");
                return false;
            }
            catch (UnauthorizedAccessException uex)
            {
                log?.Invoke($"OdbcDsn: {rootLabel} write unauthorized: {uex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                log?.Invoke($"OdbcDsn: {rootLabel} write error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Delete a previously created DSN. Safe to call with an unknown name.
        /// Any registry failure is logged and swallowed (best-effort cleanup).
        /// </summary>
        public static void DeleteDsn(string dsnName, Action<string> log)
        {
            if (string.IsNullOrWhiteSpace(dsnName)) return;
            DeleteDsnUnder(Registry.CurrentUser, dsnName, "HKCU", log);
            DeleteDsnUnder(Registry.LocalMachine, dsnName, "HKLM", log);
            log?.Invoke($"OdbcDsn: deleted '{dsnName}' (best-effort, both hives)");
        }

        private static void DeleteDsnUnder(RegistryKey root, string dsnName, string label, Action<string> log)
        {
            try { root.DeleteSubKeyTree($@"{OdbcIniPath}\{dsnName}", throwOnMissingSubKey: false); }
            catch (System.Security.SecurityException) { /* admin required for HKLM is fine to skip */ }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { log?.Invoke($"OdbcDsn: {label} DeleteSubKeyTree error: {ex.Message}"); }

            try
            {
                using var sourcesKey = root.OpenSubKey(OdbcSourcesPath, writable: true);
                sourcesKey?.DeleteValue(dsnName, throwOnMissingValue: false);
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { log?.Invoke($"OdbcDsn: {label} DeleteValue error: {ex.Message}"); }
        }

        /// <summary>
        /// Remove any temp DSN entries left over from prior crashed runs.
        /// Call once at add-in startup.
        /// </summary>
        public static int CleanupStale(Action<string> log)
        {
            int removed = 0;
            removed += CleanupStaleUnder(Registry.CurrentUser, "HKCU", log);
            removed += CleanupStaleUnder(Registry.LocalMachine, "HKLM", log);
            if (removed > 0) log?.Invoke($"OdbcDsn: cleaned up {removed} stale DSN(s) total");
            return removed;
        }

        private static int CleanupStaleUnder(RegistryKey root, string label, Action<string> log)
        {
            int removed = 0;
            try
            {
                using var sourcesKey = root.OpenSubKey(OdbcSourcesPath, writable: true);
                if (sourcesKey == null) return 0;

                var stale = sourcesKey.GetValueNames()
                    .Where(n => n.StartsWith(DsnPrefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var name in stale)
                {
                    DeleteDsnUnder(root, name, label, log);
                    removed++;
                }
            }
            catch (System.Security.SecurityException) { /* HKLM may need admin */ }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { log?.Invoke($"OdbcDsn: {label} CleanupStale error: {ex.Message}"); }
            return removed;
        }

        /// <summary>
        /// Return all installed SQL Server ODBC driver names (HKLM list).
        /// </summary>
        public static List<string> ListSqlServerDrivers()
        {
            var result = new List<string>();
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(OdbcDriversPath);
                if (key == null) return result;
                foreach (var n in key.GetValueNames())
                {
                    if (n.IndexOf("SQL", StringComparison.OrdinalIgnoreCase) >= 0)
                        result.Add(n);
                }
            }
            catch
            {
                // Intentionally surfaces up as empty list; callers decide how to react.
            }
            return result;
        }

        /// <summary>
        /// Pick the newest / most capable SQL Server ODBC driver installed.
        /// Preference order: Driver 18 &gt; Driver 17 &gt; Native Client 11 &gt; legacy "SQL Server".
        /// </summary>
        public static string GetBestSqlServerDriver(Action<string> log)
        {
            var installed = ListSqlServerDrivers();
            if (installed.Count == 0)
            {
                log?.Invoke("OdbcDsn: NO SQL Server ODBC driver found in HKLM");
                return null;
            }

            // Mirror CheckSqlServerDriver() ordering: legacy "SQL Server" first
            // because that is what erwin SCAPI's RE accepts.
            string[] preferred = {
                "SQL Server",
                "SQL Server Native Client 11.0",
                "SQL Server Native Client 10.0",
                "ODBC Driver 18 for SQL Server",
                "ODBC Driver 17 for SQL Server"
            };
            foreach (var p in preferred)
            {
                var match = installed.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    log?.Invoke($"OdbcDsn: picked driver '{match}'");
                    return match;
                }
            }

            var any = installed.First();
            log?.Invoke($"OdbcDsn: no preferred driver matched; using first available '{any}'");
            return any;
        }
    }
}
