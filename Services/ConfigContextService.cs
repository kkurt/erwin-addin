using System;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using EliteSoft.MetaAdmin.Shared.Data;

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
        /// <summary>
        /// Path-shaped value relevant to <see cref="LastError"/>: the raw
        /// locator when the model was not Mart-bound, or the parsed mart
        /// path when no CONFIG mapping exists. Empty when LastError is not
        /// path-related (DB not configured, generic error). Surfaced in
        /// the UI so admins can copy/paste it into the MODEL_CONFIG_MAPPING
        /// configuration without retyping.
        /// </summary>
        public string LastErrorPath { get; private set; }

        /// <summary>CONFIG.ID resolved for the active mart path; -1 when not initialized.</summary>
        public int ActiveConfigId { get; private set; } = -1;
        public string ActiveConfigName { get; private set; }

        /// <summary>CONFIG.CORPORATE_ID — kept for UI labels; nullable since the column is nullable.</summary>
        public int? CorporateId { get; private set; }
        public string CorporateName { get; private set; }

        /// <summary>CONFIG.DBMS_VERSION_ID — used by PropertyApplicator to scope MC_PROPERTY_DEF / MC_QUESTION_DEF.</summary>
        public int? DbmsVersionId { get; private set; }

        /// <summary>Composed "{DBMS} {Version}" label (e.g. "Oracle 19c") of the
        /// config's DBMS version, resolved from DBMS_LIBRARY + DBMS_VERSION. The
        /// add-in compares this against the open model's live target server to
        /// detect a model/config DBMS mismatch. Null when it can't be resolved.</summary>
        public string DbmsLabel { get; private set; }

        /// <summary>
        /// The MODEL_CONFIG_MAPPING key the active model resolved (or failed to
        /// resolve) against. For Mart models this is the mart path stem, e.g.
        /// "Kursat/MetaRepo"; for LOCAL .erwin files (2026-06-13) it is the
        /// plain file path, e.g. "C:\work\...\EK_KART.erwin" -
        /// the same string the Configuration Warning dialog offers to copy, so
        /// what the admin registers is exactly what the lookup queries.
        /// </summary>
        public string MartPath { get; private set; }

        /// <summary>
        /// True when the active model is Mart-hosted. Local-file models can now
        /// resolve a CONFIG too (validation features), but every Mart pipeline
        /// (Review, Generate DDL routes - they drive Mart commands and AV on
        /// non-Mart PUs, EM_GDM null deref verified 2026-05-08) must stay gated
        /// on this flag, NOT on <see cref="IsInitialized"/> alone.
        /// </summary>
        public bool IsMartModel { get; private set; }

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
                LastErrorPath = null;
                ActiveConfigId = -1;
                ActiveConfigName = null;
                CorporateId = null;
                CorporateName = null;
                DbmsVersionId = null;
                DbmsLabel = null;
                MartPath = null;
                IsMartModel = false;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    // The HklmFirstBootstrapReader probes both hives in order
                    // (HKLM then HKCU). Neither yielded a populated config, so
                    // surface both candidate paths so the user/admin can decide
                    // which one to seed. install.bat (calling install-impl.ps1)
                    // owns the HKCU write path; corporate IT typically seeds
                    // HKLM with its own tooling.
                    LastError = "No configuration found in HKLM\\Software\\EliteSoft\\MetaRepo\\Bootstrap or HKCU\\Software\\EliteSoft\\MetaRepo\\Bootstrap. Please run install.bat to configure the add-in.";
                    Log($"ConfigContext: {LastError}");
                    return false;
                }

                // Resolve the MODEL_CONFIG_MAPPING key: Mart stem for Mart
                // models, canonical file locator for LOCAL .erwin files
                // (2026-06-13: local models previously short-circuited here
                // BEFORE any DB lookup, so registering the path the warning
                // dialog offered could never work - the classic trap).
                string martPath = ParseMartPath(locator);
                if (!string.IsNullOrEmpty(martPath))
                {
                    IsMartModel = true;
                    MartPath = martPath;
                    Log($"ConfigContext: mart path = '{martPath}'");
                }
                else
                {
                    string localPath = ParseLocalModelPath(locator);
                    if (string.IsNullOrEmpty(localPath))
                    {
                        LastError = "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.";
                        LastErrorPath = locator ?? "";
                        Log($"ConfigContext: locator is neither Mart nor a local model file (locator='{locator}')");
                        return false;
                    }
                    martPath = localPath;
                    MartPath = localPath;
                    Log($"ConfigContext: local model path = '{localPath}' (Mart-only features stay disabled)");
                }

                string dbType = DatabaseService.Instance.GetDbType();

                int? configId = LookupConfigId(dbType, martPath);
                if (configId == null)
                {
                    LastError = "No configuration is defined for the model you are trying to load. Add-in controls will be disabled.";
                    LastErrorPath = martPath;
                    Log($"ConfigContext: no mapping for martPath='{martPath}'");
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

            // Single regex governs how we read the active PU's Mart path stem.
            var m = Regex.Match(locator, @"Mart://Mart/(?<path>[^?&]+?)(?:[?&]|$)",
                RegexOptions.IgnoreCase);
            if (!m.Success) return null;

            string path = m.Groups["path"].Value.Trim().Trim('/');
            return string.IsNullOrEmpty(path) ? null : path;
        }

        /// <summary>
        /// Canonical MODEL_CONFIG_MAPPING key for a LOCAL .erwin file locator.
        /// Returns the PLAIN file path WITHOUT the "erwin://" scheme (user
        /// decision 2026-06-13: "C:\work\...\EK_KART.erwin" reads better and is
        /// what the DB stores). Returns null for Mart locators (those go
        /// through <see cref="ParseMartPath"/>) and for anything that does not
        /// look like an erwin file locator. The query suffix (first '?') and
        /// trailing slashes are trimmed so the string is stable; the
        /// Configuration Warning dialog shows THIS exact value, keeping
        /// "what the admin registers" == "what the lookup queries". No
        /// collision with Mart keys: those are catalog stems like
        /// "Demo/SQL/1_DEV/KKR", never drive-letter paths.
        /// </summary>
        public static string ParseLocalModelPath(string locator)
        {
            if (string.IsNullOrWhiteSpace(locator)) return null;
            if (locator.IndexOf("Mart://", StringComparison.OrdinalIgnoreCase) >= 0) return null;

            string path = locator.Trim();
            const string scheme = "erwin://";
            if (!path.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return null;

            // Work on the body after the scheme. Only '?' starts a query -
            // '&' can legally appear inside a Windows file path, so never
            // split on it.
            string body = path.Substring(scheme.Length);
            int q = body.IndexOf('?');
            if (q >= 0) body = body.Substring(0, q);
            body = body.Trim().TrimEnd('\\', '/');

            return string.IsNullOrEmpty(body) ? null : body;
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
                    p.ParameterName = SqlDialect.Param(dbType, "martPath");
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
            // Join DBMS_LIBRARY + DBMS_VERSION so the config's DBMS label
            // ("{DBMS} {Version}", e.g. "Oracle 19c") is resolved in one round-trip
            // for the model/config mismatch check.
            string cfgQuery = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT c.""NAME"", c.""CORPORATE_ID"", c.""DBMS_VERSION_ID"", dl.""DISPLAY_NAME"" AS ""DBMS_NAME"", dv.""VERSION_CODE"" FROM ""CONFIG"" c LEFT JOIN ""DBMS_VERSION"" dv ON dv.""ID"" = c.""DBMS_VERSION_ID"" LEFT JOIN ""DBMS_LIBRARY"" dl ON dl.""ID"" = dv.""DBMS_ID"" WHERE c.""ID"" = @id",
                "ORACLE"     => @"SELECT c.NAME, c.CORPORATE_ID, c.DBMS_VERSION_ID, dl.DISPLAY_NAME AS DBMS_NAME, dv.VERSION_CODE FROM CONFIG c LEFT JOIN DBMS_VERSION dv ON dv.ID = c.DBMS_VERSION_ID LEFT JOIN DBMS_LIBRARY dl ON dl.ID = dv.DBMS_ID WHERE c.ID = :id",
                _            => @"SELECT c.[NAME], c.[CORPORATE_ID], c.[DBMS_VERSION_ID], dl.[DISPLAY_NAME] AS [DBMS_NAME], dv.[VERSION_CODE] FROM [dbo].[CONFIG] c LEFT JOIN [DBMS_VERSION] dv ON dv.[ID] = c.[DBMS_VERSION_ID] LEFT JOIN [DBMS_LIBRARY] dl ON dl.[ID] = dv.[DBMS_ID] WHERE c.[ID] = @id"
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(cfgQuery, conn))
                {
                    var p = cmd.CreateParameter();
                    p.ParameterName = SqlDialect.Param(dbType, "id");
                    p.Value = configId;
                    cmd.Parameters.Add(p);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return false;
                        ActiveConfigName = r["NAME"] == DBNull.Value ? "" : r["NAME"].ToString().Trim();
                        CorporateId = r["CORPORATE_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["CORPORATE_ID"]);
                        DbmsVersionId = r["DBMS_VERSION_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["DBMS_VERSION_ID"]);
                        var dbmsName = r["DBMS_NAME"] == DBNull.Value ? "" : r["DBMS_NAME"].ToString().Trim();
                        var verCode  = r["VERSION_CODE"] == DBNull.Value ? "" : r["VERSION_CODE"].ToString().Trim();
                        DbmsLabel = (string.IsNullOrEmpty(dbmsName) && string.IsNullOrEmpty(verCode))
                            ? null : $"{dbmsName} {verCode}".Trim();
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
                            p.ParameterName = SqlDialect.Param(dbType, "id");
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

        #region Two-level effective-value resolver (model CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY -> code default)

        /// <summary>
        /// Resolve the EFFECTIVE raw string value of a policy key under the
        /// two-level cascade (added 2026-06-04 alongside the admin CORPORATE_PROPERTY
        /// table):
        ///   1. MODEL override: the CONFIG_PROPERTY row for <see cref="ActiveConfigId"/>,
        ///      if a row exists (model "(Inherit)" == that row is absent/deleted);
        ///   2. else CORPORATE default: the CORPORATE_PROPERTY row for
        ///      <see cref="CorporateId"/> (= CONFIG.CORPORATE_ID), if a row exists;
        ///   3. else null -> the caller applies its built-in code default.
        ///
        /// This is a CASCADE, not an error-fallback. A MISSING row at either level
        /// is normal and yields the cascade/default. A REAL DB read error is NOT
        /// swallowed here - it propagates so the caller surfaces it to the user/log
        /// (rule: never silently fall back to the default on a genuine read failure).
        /// Returns null when the addin is unconfigured (no bootstrap / no config),
        /// which is the same "no value -> default" outcome the addin already degrades
        /// to elsewhere.
        ///
        /// Uses the shared EF <see cref="RepoDbContext"/> + the MetaShared
        /// ConfigProperty / CorporateProperty entities so the per-DbType quoting is
        /// handled by EF (no hand-written MSSQL/PG/Oracle SQL to keep in sync).
        /// </summary>
        public string GetEffective(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            var config = DatabaseService.Instance.GetConfig();
            if (config == null || !config.IsConfigured) return null; // unconfigured -> default

            using (var ctx = new RepoDbContext(config))
            {
                if (ActiveConfigId > 0)
                {
                    string model = ctx.ConfigProperties
                        .Where(p => p.ConfigId == ActiveConfigId && p.Key == key)
                        .Select(p => p.Value)
                        .FirstOrDefault();
                    if (model != null) return model; // model row present (incl. empty string) wins
                }

                if (CorporateId.HasValue)
                {
                    string corp = ctx.CorporateProperties
                        .Where(p => p.CorporateId == CorporateId.Value && p.Key == key)
                        .Select(p => p.Value)
                        .FirstOrDefault();
                    if (corp != null) return corp;
                }
            }

            return null;
        }

        /// <summary>
        /// Effective bool for <paramref name="key"/>. Primary parse is the spec
        /// format bool.TryParse ("True"/"False", case-insensitive); a legacy
        /// "Yes"/"1" value is also honoured so pre-existing rows do not silently
        /// flip to the default. Missing at both levels -> <paramref name="defaultValue"/>.
        /// </summary>
        public bool GetEffectiveBool(string key, bool defaultValue)
            => ParseEffectiveBool(GetEffective(key), defaultValue);

        /// <summary>Effective int for <paramref name="key"/>; missing/unparseable -> <paramref name="defaultValue"/>.</summary>
        public int GetEffectiveInt(string key, int defaultValue)
            => ParseEffectiveInt(GetEffective(key), defaultValue);

        /// <summary>Effective enum for <paramref name="key"/> (case-insensitive); missing/unparseable -> <paramref name="defaultValue"/>.</summary>
        public TEnum GetEffectiveEnum<TEnum>(string key, TEnum defaultValue) where TEnum : struct
            => ParseEffectiveEnum(GetEffective(key), defaultValue);

        // --- Pure (DB-free, unit-testable) value parsers. The DB cascade lives in
        // GetEffective; these turn its raw string into bool/int/enum. Public static to
        // mirror ParseMartPath - the test project covers them directly (the cascade
        // itself is exercised live when the add-in attaches to a Mart model). ---

        /// <summary>
        /// Primary parse is bool.TryParse ("True"/"False", case-insensitive); a legacy
        /// "Yes"/"1" (or "No"/"0") value is also honoured so pre-existing rows do not
        /// silently flip to the default. Null/blank/unparseable -> <paramref name="defaultValue"/>.
        /// </summary>
        public static bool ParseEffectiveBool(string value, bool defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            string v = value.Trim();
            if (bool.TryParse(v, out bool b)) return b;
            if (v.Equals("Yes", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (v.Equals("No", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            return defaultValue;
        }

        /// <summary>int.TryParse on the trimmed value; null/blank/unparseable -> <paramref name="defaultValue"/>.</summary>
        public static int ParseEffectiveInt(string value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            return int.TryParse(value.Trim(), out int i) ? i : defaultValue;
        }

        /// <summary>
        /// Case-insensitive Enum.TryParse + Enum.IsDefined guard (so a numeric string or
        /// an out-of-range name does not slip through). Null/blank/unparseable -> default.
        /// </summary>
        public static TEnum ParseEffectiveEnum<TEnum>(string value, TEnum defaultValue) where TEnum : struct
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            return Enum.TryParse(value.Trim(), ignoreCase: true, out TEnum e) && Enum.IsDefined(typeof(TEnum), e)
                ? e : defaultValue;
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
