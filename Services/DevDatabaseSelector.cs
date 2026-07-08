#if DEV
using System;
using System.Collections.Generic;
using System.Windows.Forms;
using EliteSoft.MetaAdmin.Shared.Models;
using Microsoft.Data.SqlClient;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// DEV-ONLY (compiled only when the DEV symbol is defined - i.e. non-packaged
    /// developer builds; never in a package). At add-in startup, BEFORE any DB
    /// connection (license read, config resolution), this lists the local MSSQL
    /// server's <c>MetaRepo*</c> databases and lets the developer pick one to run
    /// against.
    ///
    /// The choice OVERRIDES the registry bootstrap IN-MEMORY for this session via
    /// <see cref="IBootstrapService.OverrideConfig"/> - nothing is written to the
    /// registry. Host + SQL login come from the registry bootstrap (so the developer
    /// does not retype credentials); only the DATABASE NAME is chosen here.
    /// </summary>
    internal static class DevDatabaseSelector
    {
        /// <summary>
        /// Returns true when a database was picked and the config overridden. Returns
        /// false when the registry is unconfigured, the server is not MSSQL, no
        /// <c>MetaRepo*</c> database exists, enumeration fails, or the developer
        /// cancelled. The caller ABORTS the add-in load in every false case (the dev
        /// must consciously choose a database).
        /// </summary>
        public static bool TrySelectAndOverride(Action<string> log)
        {
            // Registry bootstrap supplies the server host + SQL login (we only swap the
            // database name). Read it once; a genuine read error is surfaced, not swallowed.
            BootstrapConfig reg;
            try
            {
                reg = DatabaseService.Instance.GetConfig();
            }
            catch (Exception ex)
            {
                log?.Invoke($"DevDatabaseSelector: registry bootstrap read failed: {ex.Message} - aborting.");
                Forms.AddinMessageDialog.Show(
                    "DEV database picker: the registry bootstrap connection could not be read.\n\n" + ex.Message,
                    "DEV - MetaRepo Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (reg == null || !reg.IsConfigured)
            {
                log?.Invoke("DevDatabaseSelector: registry bootstrap not configured - cannot enumerate; aborting.");
                Forms.AddinMessageDialog.Show(
                    "DEV database picker: no registry bootstrap connection is configured, so the local\n" +
                    "server / credentials to enumerate databases are unknown.\n\nRun install.bat first.",
                    "DEV - MetaRepo Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (!string.Equals(reg.DbType, "MSSQL", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke($"DevDatabaseSelector: registry DbType is '{reg.DbType}', not MSSQL - dev picker supports MSSQL only; aborting.");
                Forms.AddinMessageDialog.Show(
                    $"DEV database picker supports MSSQL only (registry DbType is '{reg.DbType}').",
                    "DEV - MetaRepo Database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // Enumerate MetaRepo* databases on the SAME server, using the SAME login,
            // connected to master.
            var databases = new List<string>();
            try
            {
                var masterCfg = new BootstrapConfig
                {
                    DbType = "MSSQL",
                    Host = reg.Host,
                    Port = reg.Port,
                    Database = "master",
                    Username = reg.Username,
                    Password = reg.Password,
                };
                using (var conn = new SqlConnection(masterCfg.GetConnectionString()))
                {
                    conn.Open();
                    using (var cmd = new SqlCommand(
                        "SELECT name FROM sys.databases WHERE name LIKE 'MetaRepo%' ORDER BY name", conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            databases.Add(reader.GetString(0));
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"DevDatabaseSelector: could not list MetaRepo* databases on '{reg.Host}': {ex.Message} - aborting.");
                Forms.AddinMessageDialog.Show(
                    $"DEV database picker: could not list MetaRepo* databases on '{reg.Host}'.\n\n{ex.Message}",
                    "DEV - MetaRepo Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (databases.Count == 0)
            {
                log?.Invoke($"DevDatabaseSelector: no MetaRepo* database found on '{reg.Host}' - aborting.");
                Forms.AddinMessageDialog.Show(
                    $"DEV database picker: no database named 'MetaRepo*' was found on '{reg.Host}'.",
                    "DEV - MetaRepo Database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            string picked = Forms.DevDatabasePickerForm.Show(reg.Host, databases);
            if (string.IsNullOrEmpty(picked))
            {
                log?.Invoke("DevDatabaseSelector: developer cancelled the DB picker - aborting load.");
                return false;
            }

            // Override the bootstrap in-memory (host + login from registry, database from
            // the picker). ClearCache() first so both the DatabaseService and reader caches
            // drop the registry config, then OverrideConfig installs the picked one.
            var overrideCfg = new BootstrapConfig
            {
                DbType = "MSSQL",
                Host = reg.Host,
                Port = reg.Port,
                Database = picked,
                Username = reg.Username,
                Password = reg.Password,
            };
            // ClearCache() first (drops both caches AND any prior override flag), then
            // OverrideConfig installs the picked config and RE-arms the override flag, so a
            // later blind ClearCache (ErwinAddIn.Execute's fresh-invocation registry re-read)
            // skips itself and the picked DB survives to config init. Route through
            // DatabaseService.OverrideConfig (not the raw reader) so IsOverridden is set.
            DatabaseService.Instance.ClearCache();
            DatabaseService.Instance.OverrideConfig(overrideCfg);
            log?.Invoke($"DevDatabaseSelector: DEV override -> {reg.Host}/{picked} (registry DB '{reg.Database}' ignored for this session; registry NOT modified).");
            return true;
        }
    }
}
#endif
