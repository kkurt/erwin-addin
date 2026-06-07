using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Represents a PREDEFINED_COLUMN entry. The UDP-condition fields are
    /// nullable to support BOTH shapes admin can author:
    ///   * conditional   : both DependsOnUdpId and DependsOnUdpValue set,
    ///                     column added only when the named UDP takes the
    ///                     listed value on a new table.
    ///   * unconditional : DependsOnUdpId == null (DEPENDS_ON_UDP_ID stored
    ///                     as SQL NULL); column added to EVERY new table
    ///                     under this config regardless of UDP state.
    /// </summary>
    public class PredefinedColumn
    {
        public int Id { get; set; }
        public int ModelId { get; set; }
        public string ColumnName { get; set; }
        public string DataType { get; set; }
        public bool Nullable { get; set; }
        /// <summary>
        /// When true, after the attribute is created on the new entity we
        /// add it to the entity's Primary Key Key_Group (Key_Group_Type="PK").
        /// Matches admin's IS_PRIMARY_KEY column (2026-05-14 schema extension).
        /// Combines naturally with Nullable=false in admin (PK columns are
        /// not nullable) but the addin does NOT enforce that constraint -
        /// it trusts whatever shape the admin row carries.
        /// </summary>
        public bool IsPrimaryKey { get; set; }
        /// <summary>
        /// Admin "this column is locked" flag (2026-05-24 schema extension,
        /// IS_LOCKED bit NOT NULL DEFAULT 0). When true and the rule
        /// currently applies to an entity (unconditional, or the gating
        /// UDP value matches), the user is prevented from renaming,
        /// re-typing, or deleting the resulting column - any such gesture
        /// is reverted by ValidationCoordinatorService and a
        /// LockedColumnDialog surfaces. Conditional locks are released
        /// the moment the UDP value no longer matches, by design (admin
        /// asked for "while condition holds" semantics, not "once locked
        /// always locked"). 2026-05-24 user rule.
        /// </summary>
        public bool IsLocked { get; set; }
        public string DefaultValue { get; set; }

        /// <summary>
        /// User-facing "Comment" (admin column <c>DEFINITION</c>, 2026-06-08).
        /// When set, the addin writes it to the created column's erwin
        /// <c>Definition</c> property. Optional - empty when admin left it blank
        /// or the admin DB predates the column (best-effort load, see
        /// <see cref="PredefinedColumnService.TryLoadComments"/>).
        /// </summary>
        public string Comment { get; set; } = "";
        // Nullable to match admin's schema (admin authored 2026-05-14:
        // "rule-independent predefined columns"). HasValue==false means
        // the column is unconditional and should be added to every new
        // table; HasValue==true means it is gated by a UDP/value match.
        public int? DependsOnUdpId { get; set; }
        public string DependsOnUdpValue { get; set; }
        public string DependsOnUdpName { get; set; } // Resolved from JOIN
        public int SortOrder { get; set; }

        /// <summary>
        /// True when the row carries no UDP gating - applies to every
        /// new entity unconditionally.
        /// </summary>
        public bool IsUnconditional => !DependsOnUdpId.HasValue;

        // Backward compat - old code uses .Name
        public string Name => ColumnName;
    }

    /// <summary>
    /// Service for loading and caching PREDEFINED_COLUMN entries.
    /// Columns are conditioned on UDP values (DEPENDS_ON_UDP_ID + DEPENDS_ON_UDP_VALUE)
    /// and filtered by DB_TYPE (platform-specific columns).
    /// </summary>
    public class PredefinedColumnService
    {
        private static PredefinedColumnService _instance;
        private static readonly object _lock = new object();

        private List<PredefinedColumn> _columns;
        private bool _isLoaded;
        private string _lastError;

        public static PredefinedColumnService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new PredefinedColumnService();
                    }
                }
                return _instance;
            }
        }

        private PredefinedColumnService()
        {
            _columns = new List<PredefinedColumn>();
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _columns.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Load predefined columns for the active config.
        /// </summary>
        /// <remarks>
        /// The legacy <c>platformDbType</c> parameter is kept for source-compat
        /// with older call sites; it is ignored. Admin's 2026-05-14 schema
        /// refactor dropped the per-row <c>DB_TYPE</c> column entirely (the
        /// "this column only applies to Oracle vs MSSQL" filter migrated to
        /// the UDP-condition flow + IsPrimaryKey flag), so there is nothing
        /// to filter against here anymore.
        /// </remarks>
        public bool LoadPredefinedColumns(string platformDbType = null)
        {
            try
            {
                _columns.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    return false;
                }

                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"PredefinedColumnService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        var pCfg = command.CreateParameter();
                        pCfg.ParameterName = dbType == "ORACLE" ? ":cfgId" : "@cfgId";
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int rowConfigId = Convert.ToInt32(reader["CONFIG_ID"]);

                                string colName = reader["COLUMN_NAME"]?.ToString()?.Trim() ?? "";
                                if (string.IsNullOrEmpty(colName)) continue;

                                // DEPENDS_ON_UDP_ID is read as nullable so the
                                // unconditional shape (SQL NULL) is preserved.
                                // Treating SQL NULL as the sentinel 0 was
                                // ambiguous because some test runs returned 0
                                // from a legitimately-zero UDP id in another
                                // codebase; null is the unambiguous "no UDP
                                // gate" marker that admin's schema commits to.
                                int? dependsOnUdpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value
                                    ? (int?)null
                                    : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]);

                                // IS_PRIMARY_KEY is admin's 2026-05-14 flag; rows
                                // authored before that migration store it as NULL
                                // which we treat as false (the historical default).
                                bool isPrimaryKey = reader["IS_PRIMARY_KEY"] != DBNull.Value &&
                                                    Convert.ToBoolean(reader["IS_PRIMARY_KEY"]);

                                // IS_LOCKED is admin's 2026-05-24 schema extension
                                // (bit NOT NULL DEFAULT 0). Nullable-safe read
                                // for forward-compat with admin DBs that may
                                // not yet have the column.
                                bool isLocked = reader["IS_LOCKED"] != DBNull.Value &&
                                                Convert.ToBoolean(reader["IS_LOCKED"]);

                                _columns.Add(new PredefinedColumn
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ModelId = rowConfigId,
                                    ColumnName = colName,
                                    DataType = reader["DATA_TYPE"]?.ToString()?.Trim() ?? "",
                                    Nullable = reader["NULLABLE"] != DBNull.Value && Convert.ToBoolean(reader["NULLABLE"]),
                                    IsPrimaryKey = isPrimaryKey,
                                    IsLocked = isLocked,
                                    DefaultValue = reader["DEFAULT_VALUE"] == DBNull.Value ? "" : reader["DEFAULT_VALUE"]?.ToString() ?? "",
                                    DependsOnUdpId = dependsOnUdpId,
                                    DependsOnUdpValue = reader["DEPENDS_ON_UDP_VALUE"] == DBNull.Value ? "" : reader["DEPENDS_ON_UDP_VALUE"]?.ToString()?.Trim() ?? "",
                                    DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"])
                                });
                            }
                        }
                    }

                    // Best-effort second pass for the optional "Comment" field
                    // (admin column DEFINITION, 2026-06-08). Kept separate from
                    // the main load so an older admin DB that predates the column
                    // still loads every other predefined-column field instead of
                    // failing the whole query. A missing column leaves Comment=""
                    // and is logged, not thrown.
                    TryLoadComments(connection, ctx.ActiveConfigId, dbType);
                }

                _isLoaded = true;
                int lockedCount = _columns.Count(c => c.IsLocked);
                System.Diagnostics.Debug.WriteLine($"PredefinedColumnService: Loaded {_columns.Count} entries ({lockedCount} locked)");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"PredefinedColumnService.LoadPredefinedColumns error: {ex.Message}");
                return false;
            }
        }

        private string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT pc.""ID"", pc.""CONFIG_ID"", pc.""COLUMN_NAME"", pc.""DATA_TYPE"", pc.""NULLABLE"",
                            pc.""IS_PRIMARY_KEY"", pc.""IS_LOCKED"",
                            pc.""DEFAULT_VALUE"", pc.""DEPENDS_ON_UDP_ID"", pc.""DEPENDS_ON_UDP_VALUE"",
                            pc.""SORT_ORDER"",
                            udp.""NAME"" AS ""UDP_NAME""
                            FROM ""PREDEFINED_COLUMN"" pc
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON pc.""DEPENDS_ON_UDP_ID"" = udp.""ID""
                            WHERE pc.""CONFIG_ID"" = @cfgId
                            ORDER BY pc.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT pc.ID, pc.CONFIG_ID, pc.COLUMN_NAME, pc.DATA_TYPE, pc.NULLABLE,
                            pc.IS_PRIMARY_KEY, pc.IS_LOCKED,
                            pc.DEFAULT_VALUE, pc.DEPENDS_ON_UDP_ID, pc.DEPENDS_ON_UDP_VALUE,
                            pc.SORT_ORDER,
                            udp.NAME AS UDP_NAME
                            FROM PREDEFINED_COLUMN pc
                            LEFT JOIN MC_UDP_DEFINITION udp ON pc.DEPENDS_ON_UDP_ID = udp.ID
                            WHERE pc.CONFIG_ID = :cfgId
                            ORDER BY pc.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT pc.[ID], pc.[CONFIG_ID], pc.[COLUMN_NAME], pc.[DATA_TYPE], pc.[NULLABLE],
                            pc.[IS_PRIMARY_KEY], pc.[IS_LOCKED],
                            pc.[DEFAULT_VALUE], pc.[DEPENDS_ON_UDP_ID], pc.[DEPENDS_ON_UDP_VALUE],
                            pc.[SORT_ORDER],
                            udp.[NAME] AS [UDP_NAME]
                            FROM [dbo].[PREDEFINED_COLUMN] pc
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] udp ON pc.[DEPENDS_ON_UDP_ID] = udp.[ID]
                            WHERE pc.[CONFIG_ID] = @cfgId
                            ORDER BY pc.[SORT_ORDER]";
            }
        }

        /// <summary>
        /// Best-effort load of the optional per-column "Comment" (admin column
        /// <c>DEFINITION</c>, 2026-06-08) which the addin applies as the erwin
        /// column Definition. Run as a SEPARATE pass on the already-open
        /// connection so an admin DB that predates the column does not fail the
        /// whole predefined-column load: a missing column (or any error) is
        /// logged and leaves every <see cref="PredefinedColumn.Comment"/> empty.
        /// Merges into the already-loaded rows by ID.
        /// </summary>
        private void TryLoadComments(System.Data.Common.DbConnection connection, int configId, string dbType)
        {
            if (_columns.Count == 0) return;
            try
            {
                string q;
                switch (dbType?.ToUpper())
                {
                    case "POSTGRESQL":
                        q = @"SELECT ""ID"", ""DEFINITION"" FROM ""PREDEFINED_COLUMN"" WHERE ""CONFIG_ID"" = @cfgId";
                        break;
                    case "ORACLE":
                        q = @"SELECT ID, DEFINITION FROM PREDEFINED_COLUMN WHERE CONFIG_ID = :cfgId";
                        break;
                    case "MSSQL":
                    default:
                        q = @"SELECT [ID], [DEFINITION] FROM [dbo].[PREDEFINED_COLUMN] WHERE [CONFIG_ID] = @cfgId";
                        break;
                }

                var byId = new Dictionary<int, PredefinedColumn>();
                foreach (var c in _columns) byId[c.Id] = c;

                using (var command = DatabaseService.Instance.CreateCommand(q, connection))
                {
                    var pCfg = command.CreateParameter();
                    pCfg.ParameterName = dbType == "ORACLE" ? ":cfgId" : "@cfgId";
                    pCfg.Value = configId;
                    command.Parameters.Add(pCfg);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int id = Convert.ToInt32(reader["ID"]);
                            string comment = reader["DEFINITION"] == DBNull.Value
                                ? ""
                                : reader["DEFINITION"]?.ToString() ?? "";
                            if (byId.TryGetValue(id, out var col))
                                col.Comment = comment;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"PredefinedColumnService.TryLoadComments: optional DEFINITION column not loaded ({ex.Message})");
            }
        }

        /// <summary>
        /// Get predefined columns that match a specific UDP condition.
        /// Used when a UDP value changes — find columns conditioned on that UDP+value.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetByUdpCondition(string udpName, string udpValue)
        {
            if (!_isLoaded) return Enumerable.Empty<PredefinedColumn>();

            return _columns.Where(c =>
                !string.IsNullOrEmpty(c.DependsOnUdpName) &&
                c.DependsOnUdpName.Equals(udpName, StringComparison.OrdinalIgnoreCase) &&
                c.DependsOnUdpValue.Equals(udpValue, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.SortOrder);
        }

        /// <summary>
        /// Get predefined columns conditioned on a specific UDP (any value).
        /// Used to find all columns that depend on a given UDP.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetByUdpName(string udpName)
        {
            if (!_isLoaded) return Enumerable.Empty<PredefinedColumn>();

            return _columns.Where(c =>
                !string.IsNullOrEmpty(c.DependsOnUdpName) &&
                c.DependsOnUdpName.Equals(udpName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.SortOrder);
        }

        /// <summary>
        /// Get unconditional predefined columns (no UDP gate). Added to every
        /// new entity regardless of UDP values - the "always apply" rows
        /// admin authored 2026-05-14 alongside the existing UDP-conditional
        /// shape. Result is SortOrder-sorted so deterministic column order
        /// is preserved across runs.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetUnconditional()
        {
            if (!_isLoaded) return Enumerable.Empty<PredefinedColumn>();
            return _columns.Where(c => c.IsUnconditional).OrderBy(c => c.SortOrder);
        }

        /// <summary>
        /// Get all loaded predefined columns.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetAll() => _columns;

        /// <summary>
        /// Get every locked predefined column row. Used by the locked-column
        /// enforcement (rename / re-type / delete revert). Includes BOTH
        /// unconditional locked rows AND conditional locked rows - the
        /// caller decides whether a conditional row currently applies to
        /// a given entity via <see cref="FindApplicableLockedRule"/>.
        /// 2026-05-24.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetLocked()
        {
            if (!_isLoaded) return Enumerable.Empty<PredefinedColumn>();
            return _columns.Where(c => c.IsLocked).OrderBy(c => c.SortOrder);
        }

        /// <summary>
        /// Look up the locked rule (if any) that currently APPLIES to the
        /// given column name on the given entity. Conditional locked rules
        /// only protect the column while their gating UDP value matches the
        /// entity's current UDP state - the "while condition holds" semantic
        /// the user confirmed 2026-05-24. Returns null when no locked rule
        /// matches; caller treats that as "the column is free to edit".
        /// </summary>
        /// <param name="entity">Live SCAPI Entity reference for reading UDP state.</param>
        /// <param name="columnName">Physical column name to check.</param>
        public PredefinedColumn FindApplicableLockedRule(dynamic entity, string columnName)
        {
            if (!_isLoaded || string.IsNullOrEmpty(columnName)) return null;

            foreach (var rule in _columns)
            {
                if (!rule.IsLocked) continue;
                if (!string.Equals(rule.ColumnName, columnName, StringComparison.OrdinalIgnoreCase)) continue;

                // Unconditional locked rule always applies.
                if (rule.IsUnconditional) return rule;

                // Conditional rule: read the gating UDP on this entity and
                // compare. Wrap in try/catch because reading a UDP that the
                // entity has never been assigned can throw on r10.10 (sparse
                // storage) - treat as "not applicable" which is the safe
                // default (column not locked, user can edit). Without this
                // guard a single broken UDP would surface as a SCAPI exception
                // every tick the user clicked the column.
                if (entity == null) continue;
                try
                {
                    string path = $"Entity.Physical.{rule.DependsOnUdpName}";
                    string liveValue = entity.Properties(path)?.Value?.ToString() ?? "";
                    if (string.Equals(liveValue, rule.DependsOnUdpValue, StringComparison.OrdinalIgnoreCase))
                        return rule;
                }
                catch
                {
                    // SCAPI returned "Entity class does not use a property of
                    // ... type" - the entity has not been assigned this UDP,
                    // so the gating condition cannot be met. Skip this rule.
                }
            }
            return null;
        }

        public void Reload(string platformDbType = null)
        {
            LoadPredefinedColumns(platformDbType);
        }

        /// <summary>
        /// Pure order-enforcement helper (2026-06-07). Given a table's CURRENT
        /// column order (<paramref name="actualOrder"/>, the names as
        /// <c>Collect("Attribute")</c> returns them) and the set of locked
        /// predefined column names that apply to the entity
        /// (<paramref name="lockedNames"/>), return the non-locked columns that
        /// the user has wedged INTO or in front of the locked block - i.e. every
        /// non-locked column that sits before the last locked column.
        /// <para>
        /// The rule (user-confirmed 2026-06-07): locked predefined columns must
        /// stay as a contiguous block at the START of the table in their
        /// SORT_ORDER; every user-added column must sit AFTER them. A non-locked
        /// column appearing before the last locked column violates that and must
        /// be moved to the end. Result preserves the violating columns' relative
        /// order. Case-insensitive. No SCAPI, no DB - unit-testable in isolation.
        /// </para>
        /// </summary>
        public static List<string> ComputeColumnsWedgedInLockedBlock(
            IReadOnlyList<string> actualOrder, ICollection<string> lockedNames)
        {
            var result = new List<string>();
            if (actualOrder == null || actualOrder.Count == 0) return result;
            if (lockedNames == null || lockedNames.Count == 0) return result;

            var lockedSet = new HashSet<string>(lockedNames, StringComparer.OrdinalIgnoreCase);

            int lastLockedIndex = -1;
            for (int i = 0; i < actualOrder.Count; i++)
            {
                if (!string.IsNullOrEmpty(actualOrder[i]) && lockedSet.Contains(actualOrder[i]))
                    lastLockedIndex = i;
            }
            if (lastLockedIndex < 0) return result; // no locked column present - nothing to enforce

            for (int i = 0; i < lastLockedIndex; i++)
            {
                string name = actualOrder[i];
                if (string.IsNullOrEmpty(name)) continue;
                if (!lockedSet.Contains(name))
                    result.Add(name);
            }
            return result;
        }
    }
}
