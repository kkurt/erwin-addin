using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Enforcement mode for a model element (column/term) that has NO match in the
    /// external glossary. Backed by the two-level config key GLOSSARY_REQUIRED_OPTION
    /// (2026-06-04); default OPTIONAL_SILENT. Only meaningful when USE_EXTERNAL_GLOSSARY
    /// effective=true. The member names match the stored VALUE strings verbatim.
    /// </summary>
    public enum GlossaryRequiredOption
    {
        /// <summary>Block: keep the existing warn + rename-to-"PLEASE CHANGE IT" / delete.</summary>
        REQUIRED,
        /// <summary>Allow the value but WARN (no rename / delete).</summary>
        OPTIONAL_WARNING,
        /// <summary>Allow silently - no popup, no rename / delete (default).</summary>
        OPTIONAL_SILENT
    }

    /// <summary>
    /// How a model element name is compared to the glossary match-column values when looking
    /// for an equal record. Backed by the two-level config key GLOSSARY_COMPARISON_TYPE
    /// (2026-07-09); default CASE_INSENSITIVE. Member names match the stored VALUE strings
    /// verbatim so <c>GetEffectiveEnum</c> parses them directly.
    /// </summary>
    public enum GlossaryComparisonType
    {
        /// <summary>Ordinal, case-SENSITIVE equality (only an exact case+content match).</summary>
        EXACT,
        /// <summary>INVARIANT case-insensitive equality (StringComparer.OrdinalIgnoreCase) - ASCII
        /// case-fold only, NOT tr-TR, so Turkish dotted/dotless I ('İ'/'ı') are not folded.</summary>
        CASE_INSENSITIVE
    }

    /// <summary>
    /// Glossary service with dynamic mapping support.
    /// Reads glossary config from DG_TABLE_MAPPING (MAPPING_CODE='GLOSSARY') + DG_TABLE_MAPPING_COLUMN.
    /// Cache: Dictionary&lt;matchValue, Dictionary&lt;targetUdp, value&gt;&gt;
    /// </summary>
    public class GlossaryService
    {
        private static GlossaryService _instance;
        private static readonly object _lock = new object();

        // Dynamic cache: matchValue → { targetUdp → value }
        private Dictionary<string, Dictionary<string, string>> _glossaryCache;
        private bool _isLoaded;
        private string _lastError;

        // Config-scoped load-state (2026-07-09). The ActiveConfigId the current load reflects (or -1
        // when ConfigContext was not initialized at load time). IsLoaded is scoped to it so a glossary
        // loaded under model A does NOT keep validating model B after an MDI model switch: the gate
        // (USE_EXTERNAL_GLOSSARY), DG_TABLE_MAPPING, required-option and comparison-type are all
        // config-scoped, so reusing model A's cache for model B was a correctness bug - external
        // glossary validation fired on a model whose USE_EXTERNAL_GLOSSARY=false. See IsLoadedForConfig.
        private int _loadedConfigId = -1;

        // Credential-failure latch (2026-07-07). When the glossary's CONNECTION_DEF
        // credentials cannot be decrypted (DPAPI is per-Windows-user, or the ciphertext
        // is corrupt/legacy base64), LoadGlossary is called from EVERY validation gesture
        // via the uniform "if (!IsLoaded) LoadGlossary()" pattern - each retry re-opens the
        // repo DB and re-hits the same failure, spamming the log. Once we KNOW the creds are
        // undecryptable for a config we latch it (keyed on ActiveConfigId) and short-circuit
        // subsequent loads: no DB hit, no retry. The latch auto-clears when the config
        // changes (different ActiveConfigId) or via ResetCredentialFailureLatch (explicit
        // reload / DB switch). _pendingCredentialWarning carries the one-time user warning,
        // drained on the STA by TryConsumeCredentialWarning.
        private int? _credentialFailureConfigId;
        private string _credentialFailureMessage;
        private string _pendingCredentialWarning;

        // Mapping metadata (for logging/debugging)
        private string _matchSourceColumn;
        private List<(string sourceCol, string targetType, string targetField, bool isLocked)> _valueMappings;
        private string _tableName;

        // Term-type metadata (Step 3 in Admin):
        //  _termTypeColumn: which glossary column holds the term-type label
        //                   (DG_TABLE_MAPPING_COLUMN row with target_field = '_TERM_TYPE_').
        //  _termTypeMap:    external label -> canonical concept code
        //                   (DG_TABLE_MAPPING_COLUMN rows with target_type = 'TERM_TYPE_MAP',
        //                    source_column = external value, target_field = canonical code).
        //  _termTypeByMatch: per-glossary-row, the canonical concept resolved from the row's
        //                    TERM_TYPE column value via _termTypeMap (null when unmapped).
        private string _termTypeColumn;
        private Dictionary<string, string> _termTypeMap;
        private Dictionary<string, string> _termTypeByMatch;

        // Two-level config (2026-06-04), resolved ONCE at LoadGlossary and cached for
        // the per-edit matcher: the external-glossary feature gate + the unmatched-
        // element enforcement mode (model CONFIG_PROPERTY -> corporate CORPORATE_PROPERTY
        // -> default). Avoids a per-column DB read.
        private bool _useExternalGlossary;
        private GlossaryRequiredOption _requiredOption = GlossaryRequiredOption.OPTIONAL_SILENT;

        // Comparison mode for model-name <-> glossary match-value lookups (GLOSSARY_COMPARISON_TYPE).
        // Resolved once per LoadGlossary; the match dictionaries (_glossaryCache, _termTypeByMatch)
        // are (re)built with the matching StringComparer so every ContainsKey/TryGetValue on the
        // model column name uses it. Default matches the historical behaviour (OrdinalIgnoreCase).
        private GlossaryComparisonType _comparisonType = GlossaryComparisonType.CASE_INSENSITIVE;
        private StringComparer _matchComparer = StringComparer.OrdinalIgnoreCase;

        /// <summary>The StringComparer for a comparison mode: EXACT -> Ordinal (case-sensitive);
        /// CASE_INSENSITIVE -> OrdinalIgnoreCase (INVARIANT ASCII fold, never tr-TR). Pure/static
        /// so it is unit-testable.</summary>
        public static StringComparer ResolveMatchComparer(GlossaryComparisonType type) =>
            type == GlossaryComparisonType.EXACT ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        public event Action<string> OnLog;

        public static GlossaryService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new GlossaryService();
                    }
                }
                return _instance;
            }
        }

        private GlossaryService()
        {
            _glossaryCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _valueMappings = new List<(string, string, string, bool)>();
            _termTypeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _termTypeByMatch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True only when a successful load exists AND it was captured under the config that is
        /// active NOW. A load captured under a different config (an MDI model switch flips
        /// <see cref="ConfigContextService.ActiveConfigId"/>) is reported as not-loaded, so the
        /// uniform "if (!IsLoaded) LoadGlossary()" callers re-read the glossary under the new config
        /// instead of validating the new model against the previous model's glossary.
        /// </summary>
        public bool IsLoaded => IsLoadedForConfig(_isLoaded, _loadedConfigId, CurrentConfigId());

        /// <summary>ActiveConfigId at "now", using the SAME formula the load path stamps
        /// <see cref="_loadedConfigId"/> with (mirrors the credential-latch id in LoadGlossary);
        /// ConfigContext not initialized -> -1. Two field reads: no DB, cheap to call per column.</summary>
        private static int CurrentConfigId() =>
            ConfigContextService.Instance.IsInitialized ? ConfigContextService.Instance.ActiveConfigId : -1;

        /// <summary>
        /// Pure decision (DB-free, unit-testable): is a glossary load-state usable for the active
        /// config? Only when a load succeeded (<paramref name="loaded"/>) AND it was captured under
        /// the same config now active. Loaded under a different config -> reload required (return false).
        /// </summary>
        public static bool IsLoadedForConfig(bool loaded, int loadedUnderConfigId, int currentConfigId) =>
            loaded && loadedUnderConfigId == currentConfigId;

        public int Count => _glossaryCache.Count;
        public string LastError => _lastError;

        /// <summary>True while the credential-failure latch is set (glossary credentials
        /// undecryptable for the active config); LoadGlossary short-circuits until it clears.</summary>
        public bool HasCredentialFailure => _credentialFailureConfigId.HasValue;

        /// <summary>
        /// Drain the one-time credential-failure warning: returns <c>true</c> and the message
        /// exactly once after a decrypt failure is latched, then clears it. Call from a UI/STA
        /// context (e.g. the validation path) so the user is told ONCE why the glossary will not
        /// load, without a modal on every column.
        /// </summary>
        public bool TryConsumeCredentialWarning(out string message)
        {
            message = _pendingCredentialWarning;
            if (string.IsNullOrEmpty(message)) return false;
            _pendingCredentialWarning = null;
            return true;
        }

        /// <summary>
        /// Clear the credential-failure latch so the next load attempts a fresh read (the admin
        /// may have re-entered the credentials). Used by the explicit DB-switch / config-reload
        /// path. Does not itself load - the next "if (!IsLoaded) LoadGlossary()" caller does.
        /// </summary>
        public void ResetCredentialFailureLatch()
        {
            _credentialFailureConfigId = null;
            _credentialFailureMessage = null;
            _pendingCredentialWarning = null;
        }

        /// <summary>
        /// Force the next "if (!IsLoaded) LoadGlossary()" caller to re-read the glossary, regardless
        /// of config id. Used by the explicit "Reload Config" / "Change DB" path: CONFIG.ID is an
        /// identity scoped PER repository DB, so switching to a different repo whose active model
        /// happens to reuse the same integer id would make the config-scoped <see cref="IsLoaded"/>
        /// wrongly report the previous repo's glossary as still valid. Flipping <c>_isLoaded</c> here
        /// defeats that cross-repo id collision. Does not itself load - the next gated caller does.
        /// (Do NOT call <see cref="Reload"/> on that path: it would run before ConfigContext is
        /// re-initialized and load under the OLD ActiveConfigId.)
        /// </summary>
        public void Invalidate() => _isLoaded = false;

        /// <summary>
        /// Record an undecryptable-credentials failure for <paramref name="configId"/>: set the
        /// user message on <see cref="LastError"/>, latch the config so LoadGlossary stops retrying,
        /// and queue the one-time user warning (drained by <see cref="TryConsumeCredentialWarning"/>).
        /// Always returns <c>false</c> (LoadGlossary's "not loaded" result).
        /// </summary>
        private bool LatchCredentialFailure(int configId)
        {
            _credentialFailureMessage =
                "Glossary connection credentials could not be decrypted for the current Windows user. "
                + "DPAPI encryption is per Windows account, so credentials seeded on a different machine "
                + "or login cannot be read here. Re-enter the glossary connection credentials in the admin "
                + "tool while logged in as the account erwin runs under.";
            _lastError = _credentialFailureMessage;
            _pendingCredentialWarning = _credentialFailureMessage;
            _credentialFailureConfigId = configId;
            _isLoaded = false;
            Log($"GlossaryService: {_lastError} (latched for config {configId}; further loads skipped until config/DB change or reload)");
            return false;
        }

        /// <summary>USE_EXTERNAL_GLOSSARY effective gate (set at LoadGlossary). When false the glossary is not loaded.</summary>
        public bool IsExternalGlossaryEnabled => _useExternalGlossary;
        /// <summary>GLOSSARY_REQUIRED_OPTION effective mode for unmatched elements (default OPTIONAL_SILENT).</summary>
        public GlossaryRequiredOption RequiredOption => _requiredOption;

        /// <summary>
        /// Load glossary using DG_TABLE_MAPPING config.
        /// </summary>
        public bool LoadGlossary()
        {
            try
            {
                _glossaryCache.Clear();
                _lastError = null;
                _matchSourceColumn = null;
                _valueMappings.Clear();
                _tableName = null;
                _termTypeColumn = null;
                _termTypeMap.Clear();
                _termTypeByMatch.Clear();

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Repository database not configured.";
                    _isLoaded = false;
                    Log($"GlossaryService: {_lastError}");
                    return false;
                }

                // 2026-06-04: gate the WHOLE external-glossary feature on the effective
                // USE_EXTERNAL_GLOSSARY (model CONFIG_PROPERTY -> corporate
                // CORPORATE_PROPERTY -> false). When disabled, do not load -> IsLoaded
                // stays false -> every downstream glossary matcher is naturally skipped
                // (they all early-return on !glossary.IsLoaded). A real DB read error
                // propagates to the outer catch (LastError surfaced), never a silent off.
                _useExternalGlossary = ConfigContextService.Instance.GetEffectiveBool("USE_EXTERNAL_GLOSSARY", false);
                // Stamp the config this load-state reflects BEFORE any return below (gate-off,
                // credential-latch, error, or success), so IsLoaded is scoped to it. ActiveConfigId
                // cannot change during this synchronous STA method, so this equals the currentCfgId
                // computed further down for the credential latch.
                _loadedConfigId = CurrentConfigId();
                if (!_useExternalGlossary)
                {
                    _isLoaded = false;
                    Log("GlossaryService: USE_EXTERNAL_GLOSSARY effective=false - external glossary disabled, not loading.");
                    return false;
                }
                // Enforcement mode for columns with no glossary match (default OPTIONAL_SILENT).
                _requiredOption = ConfigContextService.Instance.GetEffectiveEnum(
                    "GLOSSARY_REQUIRED_OPTION", GlossaryRequiredOption.OPTIONAL_SILENT);

                // Resolve the model-name <-> glossary-value comparison mode ONCE, then (re)build the
                // two match dictionaries with the corresponding comparer so every lookup uses it.
                // The dicts were Clear()ed above (early-return safety); a matching config change
                // (Reload Config / Change DB) re-runs LoadGlossary so this always reflects the
                // current effective value. _termTypeMap (external-label -> canonical) is NOT a
                // model-name match, so it stays OrdinalIgnoreCase.
                _comparisonType = ConfigContextService.Instance.GetEffectiveEnum(
                    "GLOSSARY_COMPARISON_TYPE", GlossaryComparisonType.CASE_INSENSITIVE);
                _matchComparer = ResolveMatchComparer(_comparisonType);
                _glossaryCache = new Dictionary<string, Dictionary<string, string>>(_matchComparer);
                _termTypeByMatch = new Dictionary<string, string>(_matchComparer);
                Log($"GlossaryService: USE_EXTERNAL_GLOSSARY=true, GLOSSARY_REQUIRED_OPTION={_requiredOption}, GLOSSARY_COMPARISON_TYPE={_comparisonType}");

                // Credential-failure latch: if a prior load already found this config's glossary
                // credentials undecryptable, do NOT re-open the repo DB and re-hit the same failure
                // on every validation gesture. Skip cheaply (no DB, no log spam), keeping the
                // original message in LastError. Auto re-arm when the config changed since.
                int currentCfgId = ConfigContextService.Instance.IsInitialized
                    ? ConfigContextService.Instance.ActiveConfigId
                    : -1;
                if (_credentialFailureConfigId.HasValue)
                {
                    if (_credentialFailureConfigId.Value == currentCfgId)
                    {
                        _isLoaded = false;
                        _lastError = _credentialFailureMessage;
                        return false;
                    }
                    // Config changed since the failure - re-arm and attempt a fresh load.
                    _credentialFailureConfigId = null;
                }

                string repoDbType = DatabaseService.Instance.GetDbType();

                // Step 1: Read DG_TABLE_MAPPING (MAPPING_CODE='GLOSSARY')
                int? mappingId = null;
                int? connectionDefId = null;
                int? dataSourceId = null;
                // Phase-4 (2026-05-07): when DATA_SOURCE_ID is set on the mapping, the
                // explicit SQL_TEXT from DG_DATA_SOURCE replaces the implicit
                // "SELECT ... FROM TABLE_NAME" path we used to build by hand.
                string explicitGlossarySql = null;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();

                    // Try DG_TABLE_MAPPING first (new dynamic mapping)
                    try
                    {
                        // CONFIG_ID scope (one config per mart path; addin won't even reach
                        // here when ConfigContextService.IsInitialized is false because
                        // ModelConfigForm gates initialization on it).
                        var ctx = ConfigContextService.Instance;
                        if (!ctx.IsInitialized)
                        {
                            _lastError = "ConfigContext not initialized; cannot resolve glossary mapping.";
                            _isLoaded = false;
                            Log($"GlossaryService: {_lastError}");
                            return false;
                        }
                        int cfgId = ctx.ActiveConfigId;

                        string mappingQuery = GetMappingQuery(repoDbType);
                        using (var cmd = DatabaseService.Instance.CreateCommand(mappingQuery, conn))
                        {
                            var pCfg = cmd.CreateParameter();
                            pCfg.ParameterName = SqlDialect.Param(repoDbType, "cfgId");
                            pCfg.Value = cfgId;
                            cmd.Parameters.Add(pCfg);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    mappingId = Convert.ToInt32(reader["ID"]);
                                    connectionDefId = reader["CONNECTION_DEF_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["CONNECTION_DEF_ID"]);
                                    _tableName = reader["TABLE_NAME"]?.ToString()?.Trim() ?? "";
                                    dataSourceId = reader["DATA_SOURCE_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DATA_SOURCE_ID"]);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GlossaryService: DG_TABLE_MAPPING not available ({ex.Message}), trying legacy CONNECTION_DEF...");
                    }

                    if (mappingId == null)
                    {
                        // Keep the user-facing status terse ("Not loaded - Glossary
                        // not configured.") while preserving the actionable admin
                        // hint in the log only (2026-06-08 user request).
                        _lastError = "Glossary not configured.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError} Configure GLOSSARY mapping in Admin > Data Governance.");
                        return false;
                    }

                    AddinLogger.LogDebug($"GlossaryService: Found mapping ID={mappingId}, table='{_tableName}', connDefId={connectionDefId}, dataSourceId={(dataSourceId?.ToString() ?? "null")}");

                    // Phase-4: when DATA_SOURCE_ID is set, fetch the named SQL + override
                    // CONNECTION_DEF_ID from DG_DATA_SOURCE. The mapping row's own
                    // CONNECTION_DEF_ID/TABLE_NAME stay informational only in this branch.
                    if (dataSourceId != null)
                    {
                        string dsQuery = GetDataSourceQuery(repoDbType);
                        using (var cmdDs = DatabaseService.Instance.CreateCommand(dsQuery, conn))
                        {
                            var pDs = cmdDs.CreateParameter();
                            pDs.ParameterName = SqlDialect.Param(repoDbType, "dsId");
                            pDs.Value = dataSourceId.Value;
                            cmdDs.Parameters.Add(pDs);

                            using (var reader = cmdDs.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    string dsName = reader["NAME"]?.ToString()?.Trim() ?? "";
                                    int dsConnId = reader["CONNECTION_DEF_ID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["CONNECTION_DEF_ID"]);
                                    explicitGlossarySql = reader["SQL_TEXT"]?.ToString()?.Trim();

                                    if (string.IsNullOrEmpty(explicitGlossarySql))
                                    {
                                        _lastError = $"DG_DATA_SOURCE ID={dataSourceId} has empty SQL_TEXT.";
                                        _isLoaded = false;
                                        Log($"GlossaryService: {_lastError}");
                                        return false;
                                    }
                                    if (dsConnId == 0)
                                    {
                                        _lastError = $"DG_DATA_SOURCE ID={dataSourceId} has no CONNECTION_DEF_ID.";
                                        _isLoaded = false;
                                        Log($"GlossaryService: {_lastError}");
                                        return false;
                                    }

                                    connectionDefId = dsConnId;
                                    AddinLogger.LogDebug($"GlossaryService: Using DG_DATA_SOURCE '{dsName}' (id={dataSourceId}, connId={dsConnId}, sqlLen={explicitGlossarySql.Length})");
                                }
                                else
                                {
                                    _lastError = $"DG_DATA_SOURCE ID={dataSourceId} not found.";
                                    _isLoaded = false;
                                    Log($"GlossaryService: {_lastError}");
                                    return false;
                                }
                            }
                        }
                    }
                    else if (string.IsNullOrEmpty(_tableName))
                    {
                        // Neither path provided — admin says "fail loudly".
                        _lastError = "Glossary mapping has neither TABLE_NAME nor DATA_SOURCE_ID configured.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    // Step 2: Read DG_TABLE_MAPPING_COLUMN. IS_LOCKED (glossary field
                    // lock) is OPTIONAL: repos that predate the 2026-07 migration do not
                    // have the column, so probe the catalog before selecting it -
                    // otherwise the whole load fails with "Invalid column name 'IS_LOCKED'".
                    bool hasIsLocked = HasIsLockedColumn(conn, repoDbType);
                    string colQuery = GetMappingColumnQuery(repoDbType, hasIsLocked);
                    using (var cmd2 = DatabaseService.Instance.CreateCommand(colQuery, conn))
                    {
                        var param = cmd2.CreateParameter();
                        param.ParameterName = SqlDialect.Param(repoDbType, "mappingId");
                        param.Value = mappingId.Value;
                        cmd2.Parameters.Add(param);

                        using (var reader = cmd2.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string sourceCol = reader["SOURCE_COLUMN"]?.ToString()?.Trim() ?? "";
                                string targetField = reader["TARGET_FIELD"]?.ToString()?.Trim() ?? "";
                                string targetType = "";
                                try { targetType = reader["TARGET_TYPE"]?.ToString()?.Trim() ?? ""; }
                                catch { targetType = ""; } // Column may not exist in older schemas

                                // IS_LOCKED (glossary field lock, admin migration 2026-07). A locked
                                // field is glossary-owned: its value is applied AND the user cannot
                                // keep an edit. Read only when the column exists (hasIsLocked probe
                                // above); a pre-migration repo leaves isLocked=false -> today's
                                // behaviour, and the SELECT never references the missing column.
                                bool isLocked = false;
                                if (hasIsLocked)
                                {
                                    try { isLocked = reader["IS_LOCKED"] != DBNull.Value && Convert.ToInt32(reader["IS_LOCKED"]) != 0; }
                                    catch { isLocked = false; }
                                }

                                if (string.IsNullOrEmpty(sourceCol)) continue;

                                if (targetField == "_MATCH_")
                                {
                                    _matchSourceColumn = sourceCol;
                                }
                                else if (targetField == "_TERM_TYPE_")
                                {
                                    // Config row: which glossary column holds the term-type label.
                                    // Not added to _valueMappings — its value never gets written
                                    // back to erwin; it's only used to look up the canonical concept.
                                    _termTypeColumn = sourceCol;
                                }
                                else if (string.Equals(targetType, "TERM_TYPE_MAP", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Mapping row: source_column = external label (e.g. "Business Term"),
                                    // target_field = canonical code (e.g. "BUSINESS_TERM").
                                    if (!string.IsNullOrEmpty(targetField))
                                        _termTypeMap[sourceCol] = targetField;
                                }
                                else if (!string.IsNullOrEmpty(targetField))
                                {
                                    _valueMappings.Add((sourceCol, targetType, targetField, isLocked));
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(_matchSourceColumn))
                    {
                        _lastError = "No _MATCH_ column defined in DG_TABLE_MAPPING_COLUMN.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    // Honour the USE_TERM_TYPE_MAPPING toggle from the admin Configuration
                    // panel. When the flag is unset/false, drop everything we read from the
                    // term-type rows so GetTermTypeCanonical always returns null and the
                    // policy in ValidationCoordinatorService stays a no-op. The flag lives
                    // in MODEL_PROPERTY scoped to the active model (with All-Models fallback)
                    // so the same code path adapts to per-model overrides.
                    if (!string.IsNullOrEmpty(_termTypeColumn) || _termTypeMap.Count > 0)
                    {
                        bool termTypeEnabled = ConfigContextService.Instance.GetEffectiveBool("USE_TERM_TYPE_MAPPING", false);
                        if (!termTypeEnabled)
                        {
                            Log($"GlossaryService: TermType mapping disabled by USE_TERM_TYPE_MAPPING flag — clearing {_termTypeMap.Count} concept mapping(s)");
                            _termTypeColumn = null;
                            _termTypeMap.Clear();
                        }
                    }

                    AddinLogger.LogDebug($"GlossaryService: Match column='{_matchSourceColumn}', {_valueMappings.Count} value mapping(s): [{string.Join(", ", _valueMappings.Select(m => $"{m.sourceCol}→{m.targetType}:{m.targetField}{(m.isLocked ? " [LOCKED]" : "")}"))}]");
                    if (!string.IsNullOrEmpty(_termTypeColumn))
                    {
                        AddinLogger.LogDebug($"GlossaryService: TermType column='{_termTypeColumn}', {_termTypeMap.Count} concept mapping(s): [{string.Join(", ", _termTypeMap.Select(kv => $"{kv.Key}->{kv.Value}"))}]");
                    }

                    // Step 3: Read CONNECTION_DEF for glossary DB connection
                    if (connectionDefId == null)
                    {
                        _lastError = "No CONNECTION_DEF_ID in GLOSSARY mapping.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    string glossaryConnStr = null;
                    string glossaryDbType = null;

                    string connQuery = GetConnectionDefQuery(repoDbType);
                    using (var cmd3 = DatabaseService.Instance.CreateCommand(connQuery, conn))
                    {
                        var param = cmd3.CreateParameter();
                        param.ParameterName = SqlDialect.Param(repoDbType, "connId");
                        param.Value = connectionDefId.Value;
                        cmd3.Parameters.Add(param);

                        using (var reader = cmd3.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                glossaryDbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL";
                                string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                                string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                                string dbSchema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "";
                                string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                                string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                                string username;
                                string password;
                                try
                                {
                                    username = EliteSoft.MetaAdmin.Services.PasswordEncryptionService.DecryptConnectionSecret(encUser);
                                    password = EliteSoft.MetaAdmin.Services.PasswordEncryptionService.DecryptConnectionSecret(encPass);
                                }
                                catch (Exception decEx)
                                {
                                    // The stored ciphertext is not even decodable (corrupt / legacy /
                                    // non-base64 value) - DecryptConnectionSecret throws (the classic
                                    // "input is not a valid Base-64 string"). Same user-visible cause as
                                    // the DPAPI-different-user case below, so handle it identically:
                                    // latch the config, warn once, stop retrying. (Previously this
                                    // exception bubbled to the outer catch and re-fired on every gesture.)
                                    Log($"GlossaryService: glossary credential decrypt failed: {decEx.Message}");
                                    return LatchCredentialFailure(currentCfgId);
                                }

                                // DPAPI is per Windows user (CurrentUser scope). If the CONNECTION_DEF
                                // credentials were encrypted by a DIFFERENT account than the one erwin
                                // runs under (classic prod symptom: seeded on another machine/login),
                                // Decrypt yields empty or returns the ciphertext unchanged.
                                //
                                // We deliberately do NOT fall back to the Bootstrap (repo) credentials
                                // here (removed 2026-07-06). That used to connect to the external
                                // glossary DB as the WRONG user: connect succeeds but the glossary query
                                // then fails with a misleading "table/view does not exist" (ORA-00942)
                                // instead of the real cause. Surface the true error and stop so the
                                // admin re-enters the credentials on the correct account (no silent
                                // fallback).
                                bool decryptFailed = string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser);
                                if (decryptFailed)
                                {
                                    return LatchCredentialFailure(currentCfgId);
                                }

                                glossaryConnStr = BuildConnectionString(glossaryDbType, host, port, dbSchema, username, password);
                                AddinLogger.LogDebug($"GlossaryService: Connection = {glossaryDbType}, {host}/{dbSchema}");
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(glossaryConnStr))
                    {
                        _lastError = $"CONNECTION_DEF ID={connectionDefId} not found or invalid.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    // Step 4: Load glossary data from external DB.
                    // explicitGlossarySql wins when DATA_SOURCE_ID is set (named-SQL path).
                    // Otherwise build the legacy "SELECT cols FROM TABLE_NAME" query.
                    LoadGlossaryData(glossaryDbType, glossaryConnStr, explicitGlossarySql);
                }

                _isLoaded = true;
                string sourceLabel = explicitGlossarySql != null
                    ? $"data-source[{dataSourceId}]"
                    : $"table='{_tableName}'";
                Log($"GlossaryService: Loaded {_glossaryCache.Count} entries ({sourceLabel}, match='{_matchSourceColumn}')");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                Log($"GlossaryService.LoadGlossary error: {ex.Message}");
                return false;
            }
        }

        private void LoadGlossaryData(string dbType, string connectionString, string explicitSql = null)
        {
            // Phase-4 (2026-05-07): two paths -
            //   - explicitSql != null: DG_DATA_SOURCE.SQL_TEXT was provided. Use it as-is.
            //     The admin author owns the column list there; the named SQL must include
            //     every column referenced by mapping rows (match + value + term-type).
            //   - explicitSql == null: legacy TABLE_NAME path. Build the SELECT ourselves
            //     from the mapping columns we already parsed.
            string query;
            if (!string.IsNullOrEmpty(explicitSql))
            {
                query = explicitSql;
            }
            else
            {
                var allCols = new List<string> { _matchSourceColumn };
                allCols.AddRange(_valueMappings.Select(m => m.sourceCol));
                if (!string.IsNullOrEmpty(_termTypeColumn))
                    allCols.Add(_termTypeColumn);
                var distinctCols = allCols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                string selectCols = string.Join(", ", distinctCols.Select(c => QuoteColumn(dbType, c)));
                string fromTable = QuoteTable(dbType, _tableName);
                query = $"SELECT {selectCols} FROM {fromTable}";
            }

            using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
            {
                connection.Open();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = query;
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string matchValue = "";
                            try { matchValue = reader[_matchSourceColumn]?.ToString()?.Trim() ?? ""; }
                            catch (Exception ex) { Log($"GlossaryService: Match column read error: {ex.Message}"); continue; }

                            if (string.IsNullOrEmpty(matchValue)) continue;

                            var udpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (sourceCol, targetType, targetField, isLocked) in _valueMappings)
                            {
                                try
                                {
                                    string val = reader[sourceCol]?.ToString()?.Trim() ?? "";
                                    udpValues[targetField] = val;
                                }
                                catch (Exception ex)
                                {
                                    Log($"GlossaryService: Column '{sourceCol}' read error: {ex.Message}");
                                    udpValues[targetField] = "";
                                }
                            }

                            if (!_glossaryCache.ContainsKey(matchValue))
                                _glossaryCache[matchValue] = udpValues;

                            // Resolve term-type concept for this row, if a term-type column is configured.
                            // Unmapped values (admin didn't pair them with any concept) leave _termTypeByMatch
                            // entry absent, which downstream treats as "no constraint" (same as TERM_TYPE NULL).
                            if (!string.IsNullOrEmpty(_termTypeColumn))
                            {
                                string rawTermType = "";
                                try { rawTermType = reader[_termTypeColumn]?.ToString()?.Trim() ?? ""; }
                                catch (Exception ex) { Log($"GlossaryService: TermType column '{_termTypeColumn}' read error: {ex.Message}"); }

                                if (!string.IsNullOrEmpty(rawTermType) && _termTypeMap.TryGetValue(rawTermType, out var canonical))
                                {
                                    if (!_termTypeByMatch.ContainsKey(matchValue))
                                        _termTypeByMatch[matchValue] = canonical;
                                }
                            }
                        }
                    }
                }
            }
        }

        #region Public API

        /// <summary>
        /// Check if a column name exists in the glossary.
        /// </summary>
        public bool HasEntry(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return false;
            return _glossaryCache.ContainsKey(columnName);
        }

        /// <summary>
        /// Alias for HasEntry (backward compatibility).
        /// </summary>
        public bool Exists(string columnName) => HasEntry(columnName);

        /// <summary>
        /// Get UDP values for a matched column name.
        /// Returns null if not found.
        /// </summary>
        public Dictionary<string, string> GetUdpValues(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return null;
            _glossaryCache.TryGetValue(columnName, out var values);
            return values;
        }

        /// <summary>
        /// Get the target type for a mapping target field (UDP, ERWIN_PROPERTY, DB_PROPERTY).
        /// </summary>
        public string GetTargetType(string targetField)
        {
            var mapping = _valueMappings.FirstOrDefault(m => m.targetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            return mapping.targetType ?? "";
        }

        /// <summary>
        /// Whether the mapped target field is admin-locked
        /// (DG_TABLE_MAPPING_COLUMN.IS_LOCKED, MAPPING_CODE='GLOSSARY' only). A
        /// locked field's value is glossary-owned: it is applied and the user
        /// cannot keep an edit (enforced by reverting to the glossary value).
        /// Unknown field or unlocked -> false (today's behaviour).
        /// </summary>
        public bool GetIsLocked(string targetField)
        {
            if (string.IsNullOrEmpty(targetField)) return false;
            var mapping = _valueMappings.FirstOrDefault(m => m.targetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            return mapping.isLocked;
        }

        /// <summary>
        /// The (targetField, targetType) pairs flagged IS_LOCKED=1, for the
        /// runtime lock-enforcement pass. Empty when nothing is locked
        /// (IS_LOCKED=0 everywhere) so enforcement is a no-op and behaviour
        /// stays identical to before the feature.
        /// </summary>
        public IReadOnlyList<(string targetField, string targetType)> GetLockedMappings()
        {
            return _valueMappings
                .Where(m => m.isLocked && !string.IsNullOrEmpty(m.targetField))
                .Select(m => (m.targetField, m.targetType))
                .ToList();
        }

        /// <summary>
        /// Resolve the canonical term-type concept for a glossary entry.
        /// Returns the canonical code (BUSINESS_TERM / AMORPH_DATA_TYPE / AMORPH_DATA_LENGTH /
        /// AMORPH) or null when the column is not in the glossary, the term-type column is
        /// unconfigured, the value is empty, or the value is unmapped. Null means "no constraint"
        /// for the downstream policy.
        /// </summary>
        public string GetTermTypeCanonical(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return null;
            return _termTypeByMatch.TryGetValue(columnName, out var canonical) ? canonical : null;
        }

        /// <summary>True when admin configured the optional term-type column in Step 3.</summary>
        public bool HasTermTypeConfig => !string.IsNullOrEmpty(_termTypeColumn);

        /// <summary>
        /// Reload with last used modelId.
        /// </summary>
        public void Reload()
        {
            LoadGlossary();
        }

        public bool IsConfigured => DatabaseService.Instance.IsConfigured;

        #endregion

        #region SQL Helpers

        private string GetMappingQuery(string dbType)
        {
            // Single CONFIG_ID lookup — DG_TABLE_MAPPING is keyed on CONFIG_ID after the
            // MODEL→CONFIG rename, no IN-list, no fallback chain.
            // Phase-4 (2026-05-07): also pulls DATA_SOURCE_ID. When that column is set,
            // the mapping resolves its connection + SQL through DG_DATA_SOURCE instead
            // of TABLE_NAME (named-SQL path - supports JOINs, computed columns, filters).
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""ID"", ""CONNECTION_DEF_ID"", ""TABLE_NAME"", ""DATA_SOURCE_ID"" FROM ""DG_TABLE_MAPPING""
                            WHERE ""MAPPING_CODE"" = 'GLOSSARY' AND ""CONFIG_ID"" = @cfgId
                            LIMIT 1";
                case "ORACLE":
                    return @"SELECT ID, CONNECTION_DEF_ID, TABLE_NAME, DATA_SOURCE_ID FROM DG_TABLE_MAPPING
                            WHERE MAPPING_CODE = 'GLOSSARY' AND CONFIG_ID = :cfgId
                            FETCH FIRST 1 ROWS ONLY";
                case "MSSQL":
                default:
                    return @"SELECT TOP 1 [ID], [CONNECTION_DEF_ID], [TABLE_NAME], [DATA_SOURCE_ID] FROM [dbo].[DG_TABLE_MAPPING]
                            WHERE [MAPPING_CODE] = 'GLOSSARY' AND [CONFIG_ID] = @cfgId";
            }
        }

        /// <summary>
        /// Phase-4 (2026-05-07): DG_DATA_SOURCE is a named SQL query bound to a
        /// CONNECTION_DEF. When DG_TABLE_MAPPING.DATA_SOURCE_ID is set we resolve the
        /// glossary connection and the SELECT statement through this row instead of
        /// deriving them from TABLE_NAME.
        /// </summary>
        private string GetDataSourceQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""NAME"", ""CONNECTION_DEF_ID"", ""SQL_TEXT""
                            FROM ""DG_DATA_SOURCE"" WHERE ""ID"" = @dsId";
                case "ORACLE":
                    return @"SELECT NAME, CONNECTION_DEF_ID, SQL_TEXT
                            FROM DG_DATA_SOURCE WHERE ID = :dsId";
                case "MSSQL":
                default:
                    return @"SELECT [NAME], [CONNECTION_DEF_ID], [SQL_TEXT]
                            FROM [dbo].[DG_DATA_SOURCE] WHERE [ID] = @dsId";
            }
        }

        // IS_LOCKED is appended only when the column exists (hasIsLocked). Older
        // repos without the glossary field-lock migration select the original 3
        // columns and behave exactly as before.
        private string GetMappingColumnQuery(string dbType, bool hasIsLocked)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $@"SELECT ""SOURCE_COLUMN"", ""TARGET_TYPE"", ""TARGET_FIELD""{(hasIsLocked ? @", ""IS_LOCKED""" : "")} FROM ""DG_TABLE_MAPPING_COLUMN""
                            WHERE ""TABLE_MAPPING_ID"" = @mappingId ORDER BY ""SORT_ORDER""";
                case "ORACLE":
                    return $@"SELECT SOURCE_COLUMN, TARGET_TYPE, TARGET_FIELD{(hasIsLocked ? ", IS_LOCKED" : "")} FROM DG_TABLE_MAPPING_COLUMN
                            WHERE TABLE_MAPPING_ID = :mappingId ORDER BY SORT_ORDER";
                case "MSSQL":
                default:
                    return $@"SELECT [SOURCE_COLUMN], [TARGET_TYPE], [TARGET_FIELD]{(hasIsLocked ? ", [IS_LOCKED]" : "")} FROM [dbo].[DG_TABLE_MAPPING_COLUMN]
                            WHERE [TABLE_MAPPING_ID] = @mappingId ORDER BY [SORT_ORDER]";
            }
        }

        /// <summary>
        /// Whether DG_TABLE_MAPPING_COLUMN carries the optional IS_LOCKED column.
        /// The glossary field-lock migration (2026-07) adds it; repos that predate
        /// it must still load (feature simply off), so we probe the catalog before
        /// putting IS_LOCKED in the SELECT - otherwise the whole glossary load fails
        /// with "Invalid column name 'IS_LOCKED'". A probe error -> treat as absent
        /// (logged, not swallowed) so the glossary still loads.
        /// </summary>
        private bool HasIsLockedColumn(System.Data.Common.DbConnection conn, string dbType)
        {
            string probe;
            switch (dbType?.ToUpper())
            {
                case "ORACLE":
                    probe = "SELECT COUNT(*) FROM USER_TAB_COLUMNS WHERE TABLE_NAME = 'DG_TABLE_MAPPING_COLUMN' AND COLUMN_NAME = 'IS_LOCKED'";
                    break;
                case "POSTGRESQL":
                    probe = "SELECT COUNT(*) FROM information_schema.columns WHERE lower(table_name) = 'dg_table_mapping_column' AND lower(column_name) = 'is_locked'";
                    break;
                case "MSSQL":
                default:
                    probe = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'DG_TABLE_MAPPING_COLUMN' AND COLUMN_NAME = 'IS_LOCKED'";
                    break;
            }

            try
            {
                using (var cmd = DatabaseService.Instance.CreateCommand(probe, conn))
                {
                    var v = cmd.ExecuteScalar();
                    bool present = v != null && v != DBNull.Value && Convert.ToInt32(v) > 0;
                    if (!present)
                        Log("GlossaryService: DG_TABLE_MAPPING_COLUMN.IS_LOCKED absent - glossary field-lock disabled (pre-migration repo).");
                    return present;
                }
            }
            catch (Exception ex)
            {
                Log($"GlossaryService: IS_LOCKED column probe failed ({ex.Message}) - treating as absent.");
                return false;
            }
        }

        private string GetConnectionDefQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""DB_TYPE"", ""HOST"", ""PORT"", ""DB_SCHEMA"", ""USERNAME"", ""PASSWORD""
                            FROM ""CONNECTION_DEF"" WHERE ""ID"" = @connId";
                case "ORACLE":
                    return @"SELECT DB_TYPE, HOST, PORT, DB_SCHEMA, USERNAME, PASSWORD
                            FROM CONNECTION_DEF WHERE ID = :connId";
                case "MSSQL":
                default:
                    return @"SELECT [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD]
                            FROM [dbo].[CONNECTION_DEF] WHERE [ID] = @connId";
            }
        }

        private string BuildConnectionString(string dbType, string host, string port, string database, string username, string password)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $"Host={host};Port={port};Database={database};Username={username};Password={password};";
                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={database})));User Id={username};Password={password};";
                case "MSSQL":
                default:
                    return $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True;";
            }
        }

        private string QuoteColumn(string dbType, string col)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL": return $"\"{col}\"";
                case "ORACLE": return col;
                case "MSSQL":
                default: return $"[{col}]";
            }
        }

        private string QuoteTable(string dbType, string table)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL": return $"\"{table}\"";
                case "ORACLE": return table;
                case "MSSQL":
                default: return $"[dbo].[{table}]";
            }
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
