using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// One glossary row in "Domain Like Glossary" mode: a (domain, column) pair plus the
    /// mapped values that get applied to the erwin column when the user picks it.
    /// </summary>
    public sealed class DomainGlossaryRow
    {
        public string Domain { get; set; } = "";
        public string ColumnName { get; set; } = "";

        /// <summary>
        /// Raw value of the _NAMING_STANDARD_ column for this row. Names a naming standard;
        /// the add-in resolves it (admin deliberately does no lookup). Empty = none.
        /// </summary>
        public string NamingStandard { get; set; } = "";

        /// <summary>TARGET_FIELD -&gt; value, for the ordinary (non-marker) mapping rows.</summary>
        public Dictionary<string, string> Values { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads the "Domain Like Glossary" definition from DG_TABLE_MAPPING
    /// (MAPPING_CODE='DOMAIN_GLOSSARY') + DG_TABLE_MAPPING_COLUMN, and caches the external
    /// rows as domain -&gt; column -&gt; row.
    ///
    /// <para>Deliberately a SIBLING of <see cref="GlossaryService"/>, not a mode inside it:
    /// that service is built around the _MATCH_ marker (a missing one is a hard load failure)
    /// and a cache keyed by match value. This mode has no match column at all - the user picks
    /// the row through a popup - so bending the proven service would risk shipped behaviour for
    /// nothing. The two modes are mutually exclusive anyway (admin rejects arming both), so at
    /// most one of the two services is ever loaded.</para>
    ///
    /// <para>Only the DG_DATA_SOURCE (named-SQL) path is supported. The admin web never writes
    /// TABLE_NAME for this mapping, so the legacy composed-SELECT branch is intentionally not
    /// ported - if a definition somehow has no DATA_SOURCE_ID we fail loudly instead of
    /// guessing a query.</para>
    /// </summary>
    public sealed class DomainGlossaryService
    {
        private static readonly Lazy<DomainGlossaryService> _instance =
            new Lazy<DomainGlossaryService>(() => new DomainGlossaryService());

        public static DomainGlossaryService Instance => _instance.Value;

        /// <summary>Two-level config key that arms the whole mode. Admin: Glossary &gt; Options.</summary>
        public const string SettingKey = "USE_DOMAIN_LIKE_GLOSSARY";

        /// <summary>Refresh interval (minutes) for this definition. Separate from the standard one.</summary>
        public const string LoadIntervalKey = "DOMAIN_GLOSSARY_LOAD_INTERVAL";

        private const string MappingCode = "DOMAIN_GLOSSARY";

        // Marker TARGET_FIELDs. Compared ORDINAL and case-sensitively, exactly like the
        // standard glossary's _MATCH_ / _TERM_TYPE_ - admin writes these literals verbatim.
        private const string MarkerDomain = "_DOMAIN_";
        private const string MarkerColumn = "_COLUMN_";
        private const string MarkerNamingStandard = "_NAMING_STANDARD_";

        // domain -> column -> row. Both levels case-insensitive: erwin object names and
        // external glossary values routinely differ in casing.
        private Dictionary<string, Dictionary<string, DomainGlossaryRow>> _byDomain =
            new Dictionary<string, Dictionary<string, DomainGlossaryRow>>(StringComparer.OrdinalIgnoreCase);

        // Ordinary mapping rows: sourceCol -> (targetType, targetField, isLocked). Kept so the
        // apply path can resolve a TARGET_FIELD's type the same way GlossaryService does.
        // REPLACED wholesale at the end of a load, never mutated in place: the refresh runs on a
        // background thread, so a reader must always see a complete previous state or a complete
        // new one - never a half-cleared list.
        private List<(string sourceCol, string targetType, string targetField, bool isLocked)> _valueMappings =
            new List<(string, string, string, bool)>();

        private string _domainSourceColumn = "";
        private string _columnSourceColumn = "";
        private string _namingStandardSourceColumn = "";

        private bool _isEnabled;
        private bool _isLoaded;
        private string _lastError;
        private int _loadedConfigId = -1;

        // Cached effective flag + the config it was read for. ConfigContextService.GetEffective
        // opens a RepoDbContext and issues up to two queries PER CALL, and the mode gate runs for
        // every column of every validation pass - so the flag is read once per config and served
        // from memory afterwards. Reload() re-arms it, which is how an admin-side toggle lands.
        private int _flagConfigId = -1;
        private bool _flagValue;

        private DomainGlossaryService() { }

        /// <summary>Effective USE_DOMAIN_LIKE_GLOSSARY as of the last load attempt.</summary>
        public bool IsEnabled => _isEnabled;

        /// <summary>
        /// Cheap per-column gate: is this config in Domain Like Glossary mode? Cached per config
        /// (see <see cref="_flagConfigId"/>) because the caller runs it for every column.
        /// </summary>
        public bool IsModeArmed()
        {
            var ctx = ConfigContextService.Instance;
            if (!ctx.IsInitialized) return false;

            int cfgId = ctx.ActiveConfigId;
            if (_flagConfigId == cfgId) return _flagValue;

            _flagValue = ctx.GetEffectiveBool(SettingKey, false);
            _flagConfigId = cfgId;
            return _flagValue;
        }

        public bool IsLoaded => _isLoaded;

        public string LastError => _lastError;

        /// <summary>Total number of cached (domain, column) rows.</summary>
        public int Count => _byDomain.Values.Sum(d => d.Count);

        /// <summary>Distinct domains, ordered for display in the picker's first combo.</summary>
        public IReadOnlyList<string> Domains => DomainsIn(_byDomain);

        /// <summary>
        /// Columns of one domain, ordered. Empty (never null) for an unknown domain, so the
        /// picker can bind unconditionally.
        /// </summary>
        public IReadOnlyList<string> ColumnsOf(string domain) => ColumnsIn(_byDomain, domain);

        /// <summary>The row for a (domain, column) pair, or null when either is unknown.</summary>
        public DomainGlossaryRow Find(string domain, string column) => FindIn(_byDomain, domain, column);

        // --- Pure index helpers. Extracted so the instance members above and the unit tests run
        // the SAME lookup code without needing a repo/external DB. ---

        internal static Dictionary<string, Dictionary<string, DomainGlossaryRow>> NewIndex() =>
            new Dictionary<string, Dictionary<string, DomainGlossaryRow>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Buckets rows by domain then column. First row wins on a duplicate (domain, column):
        /// the external query owns the ordering, so a later duplicate is data noise, not an update.
        /// Rows missing either key are dropped - they cannot be reached through the two combos.
        /// </summary>
        internal static Dictionary<string, Dictionary<string, DomainGlossaryRow>> BuildIndex(
            IEnumerable<DomainGlossaryRow> rows)
        {
            var index = NewIndex();
            if (rows == null) return index;

            foreach (var row in rows)
            {
                if (row == null) continue;
                if (string.IsNullOrWhiteSpace(row.Domain) || string.IsNullOrWhiteSpace(row.ColumnName)) continue;

                if (!index.TryGetValue(row.Domain, out var cols))
                {
                    cols = new Dictionary<string, DomainGlossaryRow>(StringComparer.OrdinalIgnoreCase);
                    index[row.Domain] = cols;
                }
                if (!cols.ContainsKey(row.ColumnName)) cols[row.ColumnName] = row;
            }
            return index;
        }

        internal static IReadOnlyList<string> DomainsIn(
            Dictionary<string, Dictionary<string, DomainGlossaryRow>> index) =>
            index == null
                ? Array.Empty<string>()
                : index.Keys.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();

        internal static IReadOnlyList<string> ColumnsIn(
            Dictionary<string, Dictionary<string, DomainGlossaryRow>> index, string domain)
        {
            if (index == null || string.IsNullOrWhiteSpace(domain)) return Array.Empty<string>();
            if (!index.TryGetValue(domain.Trim(), out var cols)) return Array.Empty<string>();
            return cols.Keys.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        }

        internal static DomainGlossaryRow FindIn(
            Dictionary<string, Dictionary<string, DomainGlossaryRow>> index, string domain, string column)
        {
            if (index == null || string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(column)) return null;
            if (!index.TryGetValue(domain.Trim(), out var cols)) return null;
            return cols.TryGetValue(column.Trim(), out var row) ? row : null;
        }

        /// <summary>Role a DG_TABLE_MAPPING_COLUMN row plays in this mapping.</summary>
        internal enum MappingRole
        {
            /// <summary>Empty SOURCE_COLUMN or empty TARGET_FIELD - unusable, dropped.</summary>
            Ignored,
            DomainMarker,
            ColumnMarker,
            NamingStandardMarker,
            /// <summary>An ordinary mapping row: its value IS written back to erwin.</summary>
            Value,
        }

        /// <summary>
        /// Classifies one mapping row. The three markers are CONFIGURATION - they say which source
        /// column carries which role - so they must never be classified as <see cref="MappingRole.Value"/>:
        /// a marker that reached the value list would be written onto every matched erwin column as
        /// a UDP literally named "_DOMAIN_". Comparison is ordinal and case-sensitive because admin
        /// writes these literals verbatim.
        /// </summary>
        internal static MappingRole ClassifyRow(string sourceColumn, string targetField)
        {
            if (string.IsNullOrEmpty(sourceColumn)) return MappingRole.Ignored;
            if (targetField == MarkerDomain) return MappingRole.DomainMarker;
            if (targetField == MarkerColumn) return MappingRole.ColumnMarker;
            if (targetField == MarkerNamingStandard) return MappingRole.NamingStandardMarker;
            return string.IsNullOrEmpty(targetField) ? MappingRole.Ignored : MappingRole.Value;
        }

        /// <summary>
        /// TARGET_TYPE for a TARGET_FIELD (UDP / ERWIN_PROPERTY / DB_PROPERTY), or null when the
        /// field is not mapped. Same contract as <see cref="GlossaryService.GetTargetType"/> so it
        /// can be handed to the shared apply path as a delegate.
        /// </summary>
        public string GetTargetType(string targetField)
        {
            if (string.IsNullOrEmpty(targetField)) return null;
            var mapping = _valueMappings.FirstOrDefault(
                m => m.targetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            return mapping.targetField == null ? null : mapping.targetType;
        }

        /// <summary>Whether a TARGET_FIELD is admin-locked. Same contract as GlossaryService.</summary>
        public bool GetIsLocked(string targetField)
        {
            if (string.IsNullOrEmpty(targetField)) return false;
            var mapping = _valueMappings.FirstOrDefault(
                m => m.targetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            return mapping.targetField != null && mapping.isLocked;
        }

        /// <summary>
        /// True when this cache reflects the config the add-in is currently attached to.
        /// Mirrors <c>GlossaryService.IsLoadedForConfig</c>: a stale cache from a previously
        /// opened model must not be consulted.
        /// </summary>
        public bool IsLoadedForConfig(int configId) => _isLoaded && _loadedConfigId == configId;

        /// <summary>
        /// Re-reads the definition. Also drops the cached mode flag so an admin-side toggle of
        /// USE_DOMAIN_LIKE_GLOSSARY is picked up on the next refresh instead of being pinned for
        /// the lifetime of the session.
        /// </summary>
        public void Reload()
        {
            _flagConfigId = -1;
            LoadDomainGlossary();
        }

        private static void Log(string message) => AddinLogger.Log(message);

        /// <summary>
        /// (Re)reads the definition and the external rows. Returns false when the mode is off,
        /// unconfigured, or the read failed - never throws to the caller, and never silently
        /// substitutes an empty cache for an error (LastError always carries the reason).
        /// </summary>
        public bool LoadDomainGlossary()
        {
            // The previous cache is deliberately NOT cleared up front: a refresh may run on a
            // background thread while the picker is reading, and a failed refresh should leave the
            // last good data in place rather than blanking the popup. Every field is replaced only
            // once its new value is fully read.
            _lastError = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized; cannot resolve the domain-like glossary mapping.";
                    _isEnabled = false;
                    _isLoaded = false;
                    Log($"DomainGlossaryService: {_lastError}");
                    return false;
                }

                // Stamp the config this load-state reflects BEFORE any return below, so
                // IsLoadedForConfig is scoped to it (same discipline as GlossaryService).
                _loadedConfigId = ctx.ActiveConfigId;

                _isEnabled = ctx.GetEffectiveBool(SettingKey, false);
                long tFlag = sw.ElapsedMilliseconds;
                // Keep the cheap gate in step with what this load just read.
                _flagValue = _isEnabled;
                _flagConfigId = _loadedConfigId;
                if (!_isEnabled)
                {
                    _isLoaded = false;
                    Log($"DomainGlossaryService: {SettingKey} effective=false - domain-like glossary disabled, not loading.");
                    return false;
                }

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Repository database not configured.";
                    _isLoaded = false;
                    Log($"DomainGlossaryService: {_lastError}");
                    return false;
                }

                string repoDbType = DatabaseService.Instance.GetDbType();
                int? mappingId = null;
                int? dataSourceId = null;
                int connectionDefId = 0;
                string sqlText = null;

                long tRepoOpen, tMapping, tDataSource, tColumns, tConnDef;
                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    tRepoOpen = sw.ElapsedMilliseconds;

                    using (var cmd = DatabaseService.Instance.CreateCommand(GetMappingQuery(repoDbType), conn))
                    {
                        var pCfg = cmd.CreateParameter();
                        pCfg.ParameterName = SqlDialect.Param(repoDbType, "cfgId");
                        pCfg.Value = ctx.ActiveConfigId;
                        cmd.Parameters.Add(pCfg);

                        using (var reader = cmd.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                mappingId = Convert.ToInt32(reader["ID"]);
                                dataSourceId = reader["DATA_SOURCE_ID"] == DBNull.Value
                                    ? (int?)null
                                    : Convert.ToInt32(reader["DATA_SOURCE_ID"]);
                            }
                        }
                    }

                    tMapping = sw.ElapsedMilliseconds;
                    if (mappingId == null)
                    {
                        _lastError = "Domain like glossary not configured.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError} Configure the Domain Like Glossary tab in Admin > Glossary.");
                        return false;
                    }

                    if (dataSourceId == null)
                    {
                        // The admin web always writes DATA_SOURCE_ID for this mapping. No data
                        // source means the row was hand-edited; fail loudly rather than invent a query.
                        _lastError = $"Domain like glossary mapping ID={mappingId} has no DATA_SOURCE_ID.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    using (var cmdDs = DatabaseService.Instance.CreateCommand(GetDataSourceQuery(repoDbType), conn))
                    {
                        var pDs = cmdDs.CreateParameter();
                        pDs.ParameterName = SqlDialect.Param(repoDbType, "dsId");
                        pDs.Value = dataSourceId.Value;
                        cmdDs.Parameters.Add(pDs);

                        using (var reader = cmdDs.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                _lastError = $"DG_DATA_SOURCE ID={dataSourceId} not found.";
                                _isLoaded = false;
                                Log($"DomainGlossaryService: {_lastError}");
                                return false;
                            }

                            connectionDefId = reader["CONNECTION_DEF_ID"] == DBNull.Value
                                ? 0
                                : Convert.ToInt32(reader["CONNECTION_DEF_ID"]);
                            sqlText = reader["SQL_TEXT"]?.ToString()?.Trim();
                        }
                    }

                    if (string.IsNullOrEmpty(sqlText))
                    {
                        _lastError = $"DG_DATA_SOURCE ID={dataSourceId} has empty SQL_TEXT.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }
                    if (connectionDefId == 0)
                    {
                        _lastError = $"DG_DATA_SOURCE ID={dataSourceId} has no CONNECTION_DEF_ID.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    tDataSource = sw.ElapsedMilliseconds;

                    if (!ReadMappingColumns(conn, repoDbType, mappingId.Value)) return false;
                    tColumns = sw.ElapsedMilliseconds;

                    // Both markers drive the picker's two combos; without them there is nothing to
                    // show. Name the missing one(s) and the admin field that supplies each - a
                    // bare "one of these is missing" costs a DB round-trip to diagnose.
                    var missing = new List<string>();
                    if (string.IsNullOrEmpty(_domainSourceColumn)) missing.Add("_DOMAIN_ (admin: Domain Column)");
                    if (string.IsNullOrEmpty(_columnSourceColumn)) missing.Add("_COLUMN_ (admin: Column)");
                    if (missing.Count > 0)
                    {
                        _lastError = $"Domain like glossary mapping ID={mappingId} is missing: {string.Join(", ", missing)}. "
                            + "Set it on the Domain Like Glossary tab in Admin > Glossary and save.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    if (!ResolveExternalConnection(conn, repoDbType, connectionDefId,
                            out string extDbType, out string extConnStr))
                    {
                        return false;
                    }

                    tConnDef = sw.ElapsedMilliseconds;

                    LoadRows(extDbType, extConnStr, sqlText);
                }

                _isLoaded = true;
                // Timing is logged because this read crosses the network to the external glossary
                // DB: when the picker feels slow, this line says whether the load is the cause.
                // Per-phase breakdown: "the load is slow" is useless without knowing WHICH step pays.
                Log($"DomainGlossaryService: timing flag={tFlag}ms repoOpen={tRepoOpen - tFlag}ms "
                    + $"mapping={tMapping - tRepoOpen}ms dataSource={tDataSource - tMapping}ms "
                    + $"mappingCols={tColumns - tDataSource}ms connDef={tConnDef - tColumns}ms "
                    + $"externalRead={sw.ElapsedMilliseconds - tConnDef}ms");
                Log($"DomainGlossaryService: Loaded {Count} row(s) in {_byDomain.Count} domain(s) in {sw.ElapsedMilliseconds}ms "
                    + $"(domain='{_domainSourceColumn}', columnType='{_columnSourceColumn}', "
                    + $"namingStandard='{(string.IsNullOrEmpty(_namingStandardSourceColumn) ? "-" : _namingStandardSourceColumn)}')");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                Log($"DomainGlossaryService.LoadDomainGlossary error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Splits DG_TABLE_MAPPING_COLUMN into the three markers and the ordinary value mappings.
        /// The markers are configuration, NOT value mappings: their values are never written back
        /// to erwin, so they must stay out of <see cref="_valueMappings"/> - the same discipline
        /// the standard glossary applies to _TERM_TYPE_.
        /// </summary>
        private bool ReadMappingColumns(DbConnection conn, string repoDbType, int mappingId)
        {
            var valueMappings = new List<(string, string, string, bool)>();
            string domainCol = "", columnCol = "", namingCol = "";
            bool hasIsLocked = HasIsLockedColumn(conn, repoDbType);

            using (var cmd = DatabaseService.Instance.CreateCommand(
                       GetMappingColumnQuery(repoDbType, hasIsLocked), conn))
            {
                var param = cmd.CreateParameter();
                param.ParameterName = SqlDialect.Param(repoDbType, "mappingId");
                param.Value = mappingId;
                cmd.Parameters.Add(param);

                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        string sourceCol = reader["SOURCE_COLUMN"]?.ToString()?.Trim() ?? "";
                        string targetField = reader["TARGET_FIELD"]?.ToString()?.Trim() ?? "";
                        string targetType = "";
                        try { targetType = reader["TARGET_TYPE"]?.ToString()?.Trim() ?? ""; }
                        catch (Exception ex)
                        {
                            // Pre-TARGET_TYPE repos: the column is absent from the result set.
                            Log($"DomainGlossaryService: TARGET_TYPE unreadable ({ex.Message}) - treating as untyped.");
                            targetType = "";
                        }

                        bool isLocked = false;
                        if (hasIsLocked)
                        {
                            try { isLocked = reader["IS_LOCKED"] != DBNull.Value && Convert.ToInt32(reader["IS_LOCKED"]) != 0; }
                            catch (Exception ex)
                            {
                                Log($"DomainGlossaryService: IS_LOCKED unreadable for '{targetField}' ({ex.Message}) - treating as unlocked.");
                                isLocked = false;
                            }
                        }

                        switch (ClassifyRow(sourceCol, targetField))
                        {
                            case MappingRole.DomainMarker: domainCol = sourceCol; break;
                            case MappingRole.ColumnMarker: columnCol = sourceCol; break;
                            case MappingRole.NamingStandardMarker: namingCol = sourceCol; break;
                            case MappingRole.Value: valueMappings.Add((sourceCol, targetType, targetField, isLocked)); break;
                            default: break; // Ignored: no source column or no target field.
                        }
                    }
                }
            }

            // Publish only once everything read cleanly (see the field comment: readers may run
            // concurrently with a background refresh).
            _valueMappings = valueMappings;
            _domainSourceColumn = domainCol;
            _columnSourceColumn = columnCol;
            _namingStandardSourceColumn = namingCol;
            return true;
        }

        private bool ResolveExternalConnection(
            DbConnection conn, string repoDbType, int connectionDefId,
            out string extDbType, out string extConnStr)
        {
            extDbType = "MSSQL";
            extConnStr = null;

            using (var cmd = DatabaseService.Instance.CreateCommand(GetConnectionDefQuery(repoDbType), conn))
            {
                var param = cmd.CreateParameter();
                param.ParameterName = SqlDialect.Param(repoDbType, "connId");
                param.Value = connectionDefId;
                cmd.Parameters.Add(param);

                using (var reader = cmd.ExecuteReader())
                {
                    if (!reader.Read())
                    {
                        _lastError = $"CONNECTION_DEF ID={connectionDefId} not found.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    extDbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL";
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
                        // Corrupt / legacy / foreign-user ciphertext. Surface the real cause; never
                        // fall back to the repo credentials (that connects as the WRONG user and
                        // turns into a misleading "table does not exist" later).
                        _lastError = $"Domain like glossary credential decrypt failed: {decEx.Message}";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    // DPAPI is per Windows user: a secret encrypted by another account decrypts to
                    // empty or returns the ciphertext unchanged.
                    if (string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser))
                    {
                        _lastError = "Domain like glossary credentials could not be decrypted "
                            + "(encrypted by a different Windows account). Re-enter them in Admin.";
                        _isLoaded = false;
                        Log($"DomainGlossaryService: {_lastError}");
                        return false;
                    }

                    extConnStr = BuildConnectionString(extDbType, host, port, dbSchema, username, password);
                }
            }

            if (string.IsNullOrEmpty(extConnStr))
            {
                _lastError = $"CONNECTION_DEF ID={connectionDefId} did not yield a connection string.";
                _isLoaded = false;
                Log($"DomainGlossaryService: {_lastError}");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Runs the data source's own SQL and buckets the rows by domain then column. The admin
        /// author owns the column list, so the SELECT is used verbatim (no composition).
        /// First row wins on a duplicate (domain, column) pair.
        /// </summary>
        private void LoadRows(string extDbType, string extConnStr, string sqlText)
        {
            var rows = new List<DomainGlossaryRow>();

            using (var connection = DatabaseService.Instance.CreateConnection(extDbType, extConnStr))
            {
                connection.Open();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = sqlText;
                    using (var reader = cmd.ExecuteReader())
                    {
                        // Log a missing source column ONCE rather than per row: a 14k-row glossary
                        // would otherwise flood the log with the same message.
                        var reportedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        while (reader.Read())
                        {
                            string domain = ReadCell(reader, _domainSourceColumn, reportedMissing);
                            string column = ReadCell(reader, _columnSourceColumn, reportedMissing);
                            if (string.IsNullOrEmpty(domain) || string.IsNullOrEmpty(column)) continue;

                            var row = new DomainGlossaryRow
                            {
                                Domain = domain,
                                ColumnName = column,
                                NamingStandard = string.IsNullOrEmpty(_namingStandardSourceColumn)
                                    ? ""
                                    : ReadCell(reader, _namingStandardSourceColumn, reportedMissing),
                            };

                            foreach (var (sourceCol, _, targetField, _) in _valueMappings)
                                row.Values[targetField] = ReadCell(reader, sourceCol, reportedMissing);

                            rows.Add(row);
                        }
                    }
                }
            }

            _byDomain = BuildIndex(rows);
        }

        private static string ReadCell(DbDataReader reader, string column, HashSet<string> reportedMissing)
        {
            if (string.IsNullOrEmpty(column)) return "";
            try
            {
                return reader[column]?.ToString()?.Trim() ?? "";
            }
            catch (Exception ex)
            {
                if (reportedMissing.Add(column))
                    Log($"DomainGlossaryService: column '{column}' not in the data source result set ({ex.Message}) - reading as empty.");
                return "";
            }
        }

        #region SQL (dialect-specific, mirroring GlossaryService)

        private string GetMappingQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $@"SELECT ""ID"", ""DATA_SOURCE_ID"" FROM ""DG_TABLE_MAPPING""
                            WHERE ""MAPPING_CODE"" = '{MappingCode}' AND ""CONFIG_ID"" = @cfgId
                            LIMIT 1";
                case "ORACLE":
                    return $@"SELECT ID, DATA_SOURCE_ID FROM DG_TABLE_MAPPING
                            WHERE MAPPING_CODE = '{MappingCode}' AND CONFIG_ID = :cfgId
                            FETCH FIRST 1 ROWS ONLY";
                case "MSSQL":
                default:
                    return $@"SELECT TOP 1 [ID], [DATA_SOURCE_ID] FROM [dbo].[DG_TABLE_MAPPING]
                            WHERE [MAPPING_CODE] = '{MappingCode}' AND [CONFIG_ID] = @cfgId";
            }
        }

        private string GetDataSourceQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""CONNECTION_DEF_ID"", ""SQL_TEXT"" FROM ""DG_DATA_SOURCE"" WHERE ""ID"" = @dsId";
                case "ORACLE":
                    return @"SELECT CONNECTION_DEF_ID, SQL_TEXT FROM DG_DATA_SOURCE WHERE ID = :dsId";
                case "MSSQL":
                default:
                    return @"SELECT [CONNECTION_DEF_ID], [SQL_TEXT] FROM [dbo].[DG_DATA_SOURCE] WHERE [ID] = @dsId";
            }
        }

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

        /// <summary>
        /// Whether DG_TABLE_MAPPING_COLUMN carries the optional IS_LOCKED column (2026-07
        /// migration). Probing keeps pre-migration repos loading instead of failing the whole
        /// read with "Invalid column name 'IS_LOCKED'". A probe error is logged, not swallowed.
        /// </summary>
        private bool HasIsLockedColumn(DbConnection conn, string dbType)
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
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
            catch (Exception ex)
            {
                Log($"DomainGlossaryService: IS_LOCKED probe failed ({ex.Message}) - treating the column as absent.");
                return false;
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

        #endregion
    }
}
