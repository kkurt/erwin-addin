using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Reads the DDL-generator instance's Mart-login + keep-alive configuration
    /// from DDL_GENERATION_CONF (a table owned by the admin system; the add-in
    /// only READS it). Pure DB concern (mirrors <see cref="DdlQueueService"/>):
    /// dialect-agnostic via <see cref="DatabaseService"/>, readable with NO
    /// model open (the bootstrap HKCU DB connection), and it NEVER swallows a
    /// decrypt failure silently (project rule) - a bad ciphertext is logged
    /// loudly and surfaced as null so the caller warns instead of connecting
    /// with wrong/blank credentials.
    ///
    /// Row selection (user decision 2026-07-12): the table is corporate-scoped
    /// but the worker VM serves a SINGLE corporate, so exactly ONE row is
    /// expected. Zero rows -&gt; login disabled; two-or-more rows -&gt; ambiguous
    /// (refused, loud log) rather than silently picking one.
    /// Only used by the DDLGENERATOR build flavor.
    /// </summary>
    public class DdlWorkerConfigService
    {
        private static DdlWorkerConfigService _instance;
        private static readonly object _lock = new object();

        public static DdlWorkerConfigService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null) _instance = new DdlWorkerConfigService();
                    }
                }
                return _instance;
            }
        }

        private DdlWorkerConfigService() { }

        /// <summary>
        /// Loads the single configuration row. Returns null when there are zero
        /// rows, two-or-more rows (ambiguous), or the Server-auth credentials
        /// cannot be decrypted (all logged). Windows-auth rows never touch the
        /// credential columns, so they load even if those columns hold garbage.
        /// </summary>
        public DdlWorkerConfig ReadActiveConfig(Action<string> log)
        {
            string dbType = DatabaseService.Instance.GetDbType();

            // All rows, ordered - we read the first and then check for a second
            // to enforce the single-row contract (no TOP/LIMIT so an accidental
            // second row is DETECTED rather than silently dropped).
            string sql = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""MART_AUTH_TYPE"",""MART_USER"",""MART_PASSWORD"",""MART_SERVER"",""MART_PORT"",""MART_USE_SSL"",""KEEPALIVE_MINUTES"",""ERWIN_CHECK_INTERVAL_SECONDS"",""CORPORATE_ID"" FROM ""DDL_GENERATION_CONF"" ORDER BY ""ID"" ASC",
                "ORACLE"     => @"SELECT MART_AUTH_TYPE, MART_USER, MART_PASSWORD, MART_SERVER, MART_PORT, MART_USE_SSL, KEEPALIVE_MINUTES, ERWIN_CHECK_INTERVAL_SECONDS, CORPORATE_ID FROM DDL_GENERATION_CONF ORDER BY ID ASC",
                _            => @"SELECT [MART_AUTH_TYPE],[MART_USER],[MART_PASSWORD],[MART_SERVER],[MART_PORT],[MART_USE_SSL],[KEEPALIVE_MINUTES],[ERWIN_CHECK_INTERVAL_SECONDS],[CORPORATE_ID] FROM [dbo].[DDL_GENERATION_CONF] ORDER BY [ID] ASC",
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        log?.Invoke("DdlWorkerConfig: DDL_GENERATION_CONF has no rows. Mart auto-login disabled until the admin adds one.");
                        return null;
                    }

                    string rawAuth = reader["MART_AUTH_TYPE"]?.ToString();
                    var authType = DdlWorkerConfig.ParseAuthType(rawAuth, out bool recognized);
                    if (!recognized)
                        log?.Invoke($"DdlWorkerConfig: unrecognized MART_AUTH_TYPE '{rawAuth}' - defaulting to WINDOWS auth.");

                    int corporateId = reader["CORPORATE_ID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["CORPORATE_ID"]);
                    string martServer = NullIfEmpty(reader["MART_SERVER"]?.ToString());
                    int? martPort = reader["MART_PORT"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MART_PORT"]);
                    bool useSsl = reader["MART_USE_SSL"] != DBNull.Value && Convert.ToBoolean(reader["MART_USE_SSL"]);
                    int keepAlive = DdlWorkerConfig.NormalizeKeepAliveMinutes(
                        reader["KEEPALIVE_MINUTES"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["KEEPALIVE_MINUTES"]));
                    int checkInterval = DdlWorkerConfig.NormalizeErwinCheckIntervalSeconds(
                        reader["ERWIN_CHECK_INTERVAL_SECONDS"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["ERWIN_CHECK_INTERVAL_SECONDS"]));

                    string encUser = reader["MART_USER"]?.ToString()?.Trim() ?? "";
                    string encPass = reader["MART_PASSWORD"]?.ToString()?.Trim() ?? "";

                    // Single-row contract: a second row means the operator seeded
                    // more than one corporate on a single-corporate worker VM.
                    // Refuse rather than guess which one to log into.
                    if (reader.Read())
                    {
                        log?.Invoke("DdlWorkerConfig: DDL_GENERATION_CONF has MORE THAN ONE row - ambiguous for a single-worker VM. Mart auto-login disabled. Leave exactly one row (the worker's corporate).");
                        return null;
                    }

                    string userName = null;
                    string password = null;
                    if (authType == MartAuthType.Server)
                    {
                        try
                        {
                            userName = EliteSoft.MetaAdmin.Services.PasswordEncryptionService.DecryptConnectionSecret(encUser);
                            password = EliteSoft.MetaAdmin.Services.PasswordEncryptionService.DecryptConnectionSecret(encPass);
                        }
                        catch (Exception ex)
                        {
                            // Ciphertext not decodable (corrupt / wrong key / non-base64).
                            // Same policy as the glossary credential latch: loud log, no
                            // silent fallback - return null so the caller warns and does
                            // NOT attempt a login with blank credentials.
                            log?.Invoke($"DdlWorkerConfig: Server-auth credential decrypt FAILED ({ex.Message}). Mart auto-login disabled until the admin re-enters credentials.");
                            return null;
                        }

                        // DecryptConnectionSecret can also return the input unchanged
                        // when the shared key does not match; treat an empty/echoed
                        // user name as a decrypt failure (mirror GlossaryService).
                        bool decryptFailed = string.IsNullOrEmpty(userName)
                            || (userName.Length > 50 && userName == encUser);
                        if (decryptFailed)
                        {
                            log?.Invoke("DdlWorkerConfig: Server-auth credentials did not decrypt (empty/echoed). Mart auto-login disabled.");
                            return null;
                        }
                    }

                    var cfg = new DdlWorkerConfig
                    {
                        AuthType = authType,
                        UserName = userName,
                        Password = password,
                        MartServer = martServer,
                        MartPort = martPort,
                        UseSsl = useSsl,
                        KeepAliveMinutes = keepAlive,
                        ErwinCheckIntervalSeconds = checkInterval,
                        CorporateId = corporateId,
                    };
                    log?.Invoke($"DdlWorkerConfig: loaded (corp={corporateId}, auth={authType}, server={(martServer ?? "(erwin default)")}, port={(martPort?.ToString() ?? "(erwin default)")}, ssl={useSsl}, keepAlive={keepAlive}min, checkInterval={checkInterval}s).");
                    return cfg;
                }
            }
        }

        private static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
