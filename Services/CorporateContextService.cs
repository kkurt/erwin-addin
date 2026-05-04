using System;
using System.Data.Common;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Resolves the active CONFIG row for the model the addin is attached to, by parsing
    /// the active mart path out of the PU locator and looking it up in MODEL_CONFIG_MAPPING.
    ///
    /// Replaces the old CorporateContextService model (corporate + EffectiveModelIds list +
    /// "All Corporates" fallback). Schema after the rename of MODEL→CONFIG +
    /// MODEL_PROPERTY→CONFIG_PROPERTY:
    ///   - one mart path -> exactly one config (MODEL_CONFIG_MAPPING is a unique map)
    ///   - no per-corporate or "all corporates" fallback — if the mart path is unmapped,
    ///     the addin runs unconfigured and surfaces an actionable error
    ///   - all child tables (UDP defs, naming standards, predefined columns, ...) filter
    ///     by CONFIG_ID, not MODEL_ID and not a list of effective IDs
    /// </summary>
    public class ConfigContextService
    {
        private static ConfigContextService _instance;
        private static readonly object _lock = new object();

        public bool IsInitialized { get; private set; }
        public string LastError { get; private set; }

        /// <summary>CONFIG.ID resolved for the active mart path; -1 when not initialized.</summary>
        public int ActiveConfigId { get; private set; } = -1;
        public string ActiveConfigName { get; private set; }

        /// <summary>CONFIG.CORPORATE_ID — kept for UI labels; nullable since the column is nullable.</summary>
        public int? CorporateId { get; private set; }
        public string CorporateName { get; private set; }

        /// <summary>CONFIG.DBMS_VERSION_ID — used by PropertyApplicator to scope MC_PROPERTY_DEF / MC_QUESTION_DEF.</summary>
        public int? DbmsVersionId { get; private set; }

        /// <summary>The mart path the active model lives under, e.g. "Kursat/MetaRepo". Null for local-file models.</summary>
        public string MartPath { get; private set; }

        public event Action<string> OnLog;

        public static ConfigContextService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new ConfigContextService();
                    }
                }
                return _instance;
            }
        }

        private ConfigContextService() { }

        /// <summary>
        /// Initialize from the active model's mart locator. Returns false (and sets
        /// LastError) when the mart path is unmapped, the DB is not configured, or
        /// any DB error occurs. Callers should treat this as "addin runs in read-only,
        /// no config-driven services" rather than crashing.
        /// </summary>
        public bool Initialize(string locator)
        {
            try
            {
                IsInitialized = false;
                LastError = null;
                ActiveConfigId = -1;
                ActiveConfigName = null;
                CorporateId = null;
                CorporateName = null;
                DbmsVersionId = null;
                MartPath = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    LastError = "Database not configured. Please run Admin panel to configure the database connection.";
                    Log($"ConfigContext: {LastError}");
                    return false;
                }

                string martPath = ParseMartPath(locator);
                if (string.IsNullOrEmpty(martPath))
                {
                    LastError = $"Active model is not on a Mart server (locator='{locator}'). Mart-bound models only.";
                    Log($"ConfigContext: {LastError}");
                    return false;
                }
                MartPath = martPath;
                Log($"ConfigContext: mart path = '{martPath}'");

                string dbType = DatabaseService.Instance.GetDbType();

                int? configId = LookupConfigId(dbType, martPath);
                if (configId == null)
                {
                    LastError = $"No CONFIG mapped to mart path '{martPath}'. Add a MODEL_CONFIG_MAPPING row in Admin.";
                    Log($"ConfigContext: {LastError}");
                    return false;
                }
                ActiveConfigId = configId.Value;

                if (!LoadConfigRow(dbType, ActiveConfigId))
                {
                    LastError = $"CONFIG row {ActiveConfigId} disappeared after lookup — DB inconsistency.";
                    Log($"ConfigContext: {LastError}");
                    return false;
                }

                IsInitialized = true;
                Log($"ConfigContext: ready — config '{ActiveConfigName}' (ID={ActiveConfigId}), corporate='{CorporateName ?? "(none)"}' (ID={CorporateId?.ToString() ?? "(null)"}), DBMS_VERSION_ID={DbmsVersionId?.ToString() ?? "(null)"}");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log($"ConfigContext.Initialize error: {ex.Message}");
                return false;
            }
        }

        #region Mart path parsing

        /// <summary>
        /// Parse the Mart path stem (e.g. "Kursat/MetaRepo") out of an erwin
        /// PU locator. Two real-world shapes are accepted (taken from
        /// VersionCompareService):
        ///   "Mart://Mart/&lt;lib&gt;/&lt;model&gt;?...VNO=N..."
        ///   "erwin://Mart://Mart/&lt;lib&gt;/&lt;model&gt;?&amp;version=N&amp;modelLongId=..."
        /// The path stem is everything between "Mart://Mart/" and the first
        /// '?' or '&amp;' (whichever comes first), trimmed of leading/trailing
        /// slashes. The exact same value is what admin's
        /// <c>ModelMappingService.GetByMartPath</c> compares against
        /// MODEL_CONFIG_MAPPING.MART_PATH (verbatim, case-sensitive on the DB
        /// side). Returns null for local-file locators or anything we cannot
        /// parse.
        /// </summary>
        public static string ParseMartPath(string locator)
        {
            if (string.IsNullOrWhiteSpace(locator)) return null;

            // Mirrors VersionCompareService.BuildMartLocatorForTarget so a
            // single regex governs how we read the active PU's path stem.
            var m = Regex.Match(locator, @"Mart://Mart/(?<path>[^?&]+?)(?:[?&]|$)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            string path = m.Groups["path"].Value.Trim().Trim('/');
            return string.IsNullOrEmpty(path) ? null : path;
        }

        #endregion

        #region DB queries

        private int? LookupConfigId(string dbType, string martPath)
        {
            string query = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""CONFIG_ID"" FROM ""MODEL_CONFIG_MAPPING"" WHERE ""MART_PATH"" = @martPath",
                "ORACLE"     => @"SELECT CONFIG_ID FROM MODEL_CONFIG_MAPPING WHERE MART_PATH = :martPath",
                _            => @"SELECT [CONFIG_ID] FROM [dbo].[MODEL_CONFIG_MAPPING] WHERE [MART_PATH] = @martPath"
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = dbType == "ORACLE" ? ":martPath" : "@martPath";
                    p.Value = martPath;
                    cmd.Parameters.Add(p);
                    var v = cmd.ExecuteScalar();
                    if (v == null || v == DBNull.Value) return null;
                    return Convert.ToInt32(v);
                }
            }
        }

        private bool LoadConfigRow(string dbType, int configId)
        {
            string cfgQuery = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""NAME"", ""CORPORATE_ID"", ""DBMS_VERSION_ID"" FROM ""CONFIG"" WHERE ""ID"" = @id",
                "ORACLE"     => @"SELECT NAME, CORPORATE_ID, DBMS_VERSION_ID FROM CONFIG WHERE ID = :id",
                _            => @"SELECT [NAME], [CORPORATE_ID], [DBMS_VERSION_ID] FROM [dbo].[CONFIG] WHERE [ID] = @id"
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(cfgQuery, conn))
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = dbType == "ORACLE" ? ":id" : "@id";
                    p.Value = configId;
                    cmd.Parameters.Add(p);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;
                        ActiveConfigName = r["NAME"] == DBNull.Value ? "" : r["NAME"].ToString().Trim();
                        CorporateId = r["CORPORATE_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["CORPORATE_ID"]);
                        DbmsVersionId = r["DBMS_VERSION_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["DBMS_VERSION_ID"]);
                    }
                }
            }

            if (CorporateId.HasValue)
            {
                string nameQuery = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => @"SELECT ""NAME"" FROM ""MC_CORPORATE"" WHERE ""ID"" = @id",
                    "ORACLE"     => @"SELECT NAME FROM MC_CORPORATE WHERE ID = :id",
                    _            => @"SELECT [NAME] FROM [dbo].[MC_CORPORATE] WHERE [ID] = @id"
                };
                try
                {
                    using (var conn = DatabaseService.Instance.CreateConnection())
                    {
                        conn.Open();
                        using (var cmd = DatabaseService.Instance.CreateCommand(nameQuery, conn))
                        {
                            var p = cmd.CreateParameter();
                            p.ParameterName = dbType == "ORACLE" ? ":id" : "@id";
                            p.Value = CorporateId.Value;
                            cmd.Parameters.Add(p);
                            var v = cmd.ExecuteScalar();
                            CorporateName = v?.ToString()?.Trim();
                        }
                    }
                }
                catch (Exception ex) { Log($"ConfigContext: corporate name lookup error: {ex.Message}"); }
            }

            return true;
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
