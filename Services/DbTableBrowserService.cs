using System;
using System.Collections.Generic;
using System.Data.Odbc;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads the list of base tables from a configured ODBC database connection.
    /// Used by the "From DB" Generate DDL flow so the table picker shows the
    /// ACTUAL database tables instead of the active model's entities; the user
    /// then chooses which DB tables to compare against.
    /// </summary>
    internal static class DbTableBrowserService
    {
        /// <summary>
        /// Returns "schema.table" entries fetched from the live DB. Honors a
        /// schema filter when supplied (case-insensitive). Returns null on
        /// failure - caller should fall back to model-side tables.
        /// </summary>
        public static List<string> FetchTables(
            int dbTypeCode,
            string host,
            string database,
            string dsnName,
            bool useNative,
            bool useWindowsAuth,
            string user,
            string password,
            string schemaFilter,
            Action<string> log)
        {
            string connStr = BuildOdbcConnectionString(dbTypeCode, host, database, dsnName,
                useNative, useWindowsAuth, user, password);
            if (string.IsNullOrEmpty(connStr))
            {
                log?.Invoke("[DBLIST] no usable connection string - aborting");
                return null;
            }
            string sql = BuildListTablesSql(dbTypeCode, schemaFilter);
            if (string.IsNullOrEmpty(sql))
            {
                log?.Invoke($"[DBLIST] no list-tables SQL for dbTypeCode={dbTypeCode}");
                return null;
            }

            log?.Invoke($"[DBLIST] querying DB tables (dbType={dbTypeCode}, schema='{schemaFilter}')...");
            var tables = new List<string>();
            try
            {
                using (var conn = new OdbcConnection(connStr))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = sql;
                        cmd.CommandTimeout = 30;
                        using (var rdr = cmd.ExecuteReader())
                        {
                            while (rdr.Read())
                            {
                                string schema = rdr.IsDBNull(0) ? "" : rdr.GetString(0);
                                string name = rdr.IsDBNull(1) ? "" : rdr.GetString(1);
                                if (string.IsNullOrEmpty(name)) continue;
                                tables.Add(string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"[DBLIST] query failed: {ex.Message}");
                return null;
            }

            log?.Invoke($"[DBLIST] fetched {tables.Count} table(s) from DB");
            return tables;
        }

        private static string BuildOdbcConnectionString(
            int dbTypeCode, string host, string database, string dsnName,
            bool useNative, bool useWindowsAuth, string user, string password)
        {
            // ODBC DSN path - simplest, just use the named DSN.
            if (!useNative)
            {
                if (string.IsNullOrWhiteSpace(dsnName)) return null;
                string s = $"DSN={dsnName.Trim()}";
                if (!useWindowsAuth && !string.IsNullOrWhiteSpace(user))
                    s += $";UID={user.Trim()};PWD={password ?? ""}";
                return s;
            }

            // Native path - build a driver-specific connection string. Mirrors
            // DbConnectionForm.BuildOdbcConnectionString so the same drivers
            // are used across the addin.
            if (string.IsNullOrWhiteSpace(host)) return null;
            string driver;
            switch (dbTypeCode)
            {
                case 16: // SQL Server
                case 18: // SQL Azure
                    driver = "{ODBC Driver 17 for SQL Server}";
                    break;
                case 35: // PostgreSQL
                    driver = "{PostgreSQL ANSI}";
                    break;
                case 10: // Oracle
                    driver = "{Oracle in OraDB19Home1}";
                    break;
                case 8:  // MySQL
                    driver = "{MySQL ODBC 8.0 Unicode Driver}";
                    break;
                default:
                    driver = "{SQL Server}";
                    break;
            }
            string conn = $"Driver={driver};Server={host.Trim()}";
            if (!string.IsNullOrWhiteSpace(database))
                conn += $";Database={database.Trim()}";
            if (useWindowsAuth)
                conn += ";Trusted_Connection=Yes";
            else if (!string.IsNullOrWhiteSpace(user))
                conn += $";UID={user.Trim()};PWD={password ?? ""}";
            return conn;
        }

        private static string BuildListTablesSql(int dbTypeCode, string schemaFilter)
        {
            string s = (schemaFilter ?? "").Trim();
            switch (dbTypeCode)
            {
                case 16: // SQL Server
                case 18: // SQL Azure
                {
                    string sql = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
                    if (!string.IsNullOrEmpty(s))
                        sql += $" AND LOWER(TABLE_SCHEMA) = LOWER('{Esc(s)}')";
                    sql += " ORDER BY TABLE_SCHEMA, TABLE_NAME";
                    return sql;
                }
                case 35: // PostgreSQL
                case 8:  // MySQL
                case 21: // Snowflake
                {
                    string sql = "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
                    if (!string.IsNullOrEmpty(s))
                        sql += $" AND LOWER(TABLE_SCHEMA) = LOWER('{Esc(s)}')";
                    sql += " ORDER BY TABLE_SCHEMA, TABLE_NAME";
                    return sql;
                }
                case 10: // Oracle
                {
                    string sql = "SELECT OWNER, TABLE_NAME FROM ALL_TABLES";
                    if (!string.IsNullOrEmpty(s))
                        sql += $" WHERE UPPER(OWNER) = UPPER('{Esc(s)}')";
                    sql += " ORDER BY OWNER, TABLE_NAME";
                    return sql;
                }
                case 2: // DB2
                {
                    string sql = "SELECT TABSCHEMA, TABNAME FROM SYSCAT.TABLES WHERE TYPE = 'T'";
                    if (!string.IsNullOrEmpty(s))
                        sql += $" AND UPPER(TABSCHEMA) = UPPER('{Esc(s)}')";
                    sql += " ORDER BY TABSCHEMA, TABNAME";
                    return sql;
                }
                default:
                    return null;
            }
        }

        private static string Esc(string s) => (s ?? "").Replace("'", "''");
    }
}
