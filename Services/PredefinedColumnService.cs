using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Represents a PREDEFINED_COLUMN entry. Applicability is carried by an ordered
    /// AND/OR condition list (<see cref="Conditions"/>, rows of
    /// <c>MC_PREDEFINED_COLUMN_CONDITION</c>) - the SAME contract naming rules use
    /// (WP#280). Two shapes admin can author:
    ///   * conditional   : one or more condition terms; the column is added to a new
    ///                     table only when the folded AND/OR result is TRUE.
    ///   * unconditional : no condition terms (<see cref="Conditions"/> empty); the
    ///                     column is added to EVERY new table under this config.
    /// The former flat <c>DEPENDS_ON_UDP_ID</c>/<c>DEPENDS_ON_UDP_VALUE</c> columns
    /// were dropped; the migration moved each old single condition to a single
    /// ORDER_INDEX=0 row so migrated columns fold to the identical boolean.
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
        public int SortOrder { get; set; }

        /// <summary>
        /// The column's ordered DEPENDS_ON conditions (rows of
        /// <c>MC_PREDEFINED_COLUMN_CONDITION</c>, ORDER_INDEX order), folded strictly
        /// left-to-right (no precedence) by
        /// <see cref="NamingValidationEngine.AreConditionsSatisfied"/> - the exact same
        /// engine and row type (<see cref="NamingRuleCondition"/>) naming rules use.
        /// Empty == the column is unconditional (always applies). This list is the SOLE
        /// authority; the former flat <c>DEPENDS_ON_UDP_*</c> columns were dropped
        /// (WP#280).
        /// </summary>
        public List<NamingRuleCondition> Conditions { get; } = new List<NamingRuleCondition>();

        /// <summary>
        /// True when the row carries no conditions - applies to every
        /// new entity unconditionally.
        /// </summary>
        public bool IsUnconditional => Conditions.Count == 0;

        // Backward compat - old code uses .Name
        public string Name => ColumnName;
    }

    /// <summary>
    /// Service for loading and caching PREDEFINED_COLUMN entries. Applicability is
    /// carried by an ordered AND/OR condition list per column
    /// (<c>MC_PREDEFINED_COLUMN_CONDITION</c>), evaluated by the shared
    /// <see cref="NamingValidationEngine.AreConditionsSatisfied"/> engine (WP#280).
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
                        pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int rowConfigId = Convert.ToInt32(reader["CONFIG_ID"]);

                                string colName = reader["COLUMN_NAME"]?.ToString()?.Trim() ?? "";
                                if (string.IsNullOrEmpty(colName)) continue;

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
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"])
                                });
                            }
                        }
                    }

                    // Load each column's ordered AND/OR condition list from
                    // MC_PREDEFINED_COLUMN_CONDITION onto the columns just loaded
                    // (reusing the open connection). Inside the try so a
                    // condition-load failure fails the whole load instead of
                    // silently treating conditional columns as unconditional -
                    // exactly as NamingStandardService.LoadRuleConditions does.
                    LoadColumnConditions(connection, ctx.ActiveConfigId, dbType);

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
                            pc.""DEFAULT_VALUE"",
                            pc.""SORT_ORDER""
                            FROM ""PREDEFINED_COLUMN"" pc
                            WHERE pc.""CONFIG_ID"" = @cfgId
                            ORDER BY pc.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT pc.ID, pc.CONFIG_ID, pc.COLUMN_NAME, pc.DATA_TYPE, pc.NULLABLE,
                            pc.IS_PRIMARY_KEY, pc.IS_LOCKED,
                            pc.DEFAULT_VALUE,
                            pc.SORT_ORDER
                            FROM PREDEFINED_COLUMN pc
                            WHERE pc.CONFIG_ID = :cfgId
                            ORDER BY pc.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT pc.[ID], pc.[CONFIG_ID], pc.[COLUMN_NAME], pc.[DATA_TYPE], pc.[NULLABLE],
                            pc.[IS_PRIMARY_KEY], pc.[IS_LOCKED],
                            pc.[DEFAULT_VALUE],
                            pc.[SORT_ORDER]
                            FROM [dbo].[PREDEFINED_COLUMN] pc
                            WHERE pc.[CONFIG_ID] = @cfgId
                            ORDER BY pc.[SORT_ORDER]";
            }
        }

        /// <summary>
        /// Loads <c>MC_PREDEFINED_COLUMN_CONDITION</c> for the active config and attaches
        /// each row (ORDER_INDEX order) to its owning column's
        /// <see cref="PredefinedColumn.Conditions"/>, reusing the already-open
        /// <paramref name="connection"/>. A row that names neither source or both is
        /// skipped + logged (mirrors the admin XOR CHECK). Lets a query failure propagate
        /// so the caller fails the whole load rather than treating conditional columns as
        /// unconditional. A direct clone of
        /// <c>NamingStandardService.LoadRuleConditions</c> - the two child tables share the
        /// exact same shape and the <see cref="NamingRuleCondition"/> row type (WP#280).
        /// </summary>
        private void LoadColumnConditions(System.Data.Common.DbConnection connection, int configId, string dbType)
        {
            if (_columns.Count == 0) return;

            var byId = new Dictionary<int, PredefinedColumn>();
            foreach (var c in _columns) byId[c.Id] = c;

            string query = GetConditionQuery(dbType);
            int loaded = 0, skipped = 0;

            using (var command = DatabaseService.Instance.CreateCommand(query, connection))
            {
                var pCfg = command.CreateParameter();
                pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
                pCfg.Value = configId;
                command.Parameters.Add(pCfg);

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int columnId = Convert.ToInt32(reader["PREDEFINED_COLUMN_ID"]);
                        if (!byId.TryGetValue(columnId, out var col)) continue; // condition for a column we did not load (other config)

                        int? udpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]);
                        int? propDefId = reader["DEPENDS_ON_PROPERTY_DEF_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_PROPERTY_DEF_ID"]);

                        // Mirror admin's CK XOR: each term must name EXACTLY one source.
                        if (udpId.HasValue == propDefId.HasValue)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"PREDEFINED_COLUMN_CONDITION SKIP: column {columnId} order {reader["ORDER_INDEX"]} names {(udpId.HasValue ? "BOTH" : "NO")} source(s) - term dropped");
                            skipped++;
                            continue;
                        }

                        col.Conditions.Add(new NamingRuleCondition
                        {
                            OrderIndex = Convert.ToInt32(reader["ORDER_INDEX"]),
                            Connector = reader["CONNECTOR"] == DBNull.Value ? null : reader["CONNECTOR"]?.ToString()?.Trim(),
                            DependsOnUdpId = udpId,
                            DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyDefId = propDefId,
                            DependsOnPropertyCode = reader["COND_PROPERTY_CODE"] == DBNull.Value ? "" : reader["COND_PROPERTY_CODE"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyObjectType = reader["COND_PROPERTY_OBJECT_TYPE"] == DBNull.Value ? "" : reader["COND_PROPERTY_OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                            DependsOnPropertyValues = reader["DEPENDS_ON_PROPERTY_VALUES"] == DBNull.Value ? "" : reader["DEPENDS_ON_PROPERTY_VALUES"]?.ToString()?.Trim() ?? "",
                        });
                        loaded++;
                    }
                }
            }

            // Defensive: the fold relies on ORDER_INDEX order. The query already sorts,
            // but a per-column sort guarantees it regardless of provider quirks.
            foreach (var col in _columns)
                col.Conditions.Sort((a, b) => a.OrderIndex.CompareTo(b.OrderIndex));

            System.Diagnostics.Debug.WriteLine(
                $"PredefinedColumnService: loaded {loaded} condition term(s), skipped {skipped}");
        }

        private static string GetConditionQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT cc.""PREDEFINED_COLUMN_ID"", cc.""ORDER_INDEX"", cc.""CONNECTOR"",
                            cc.""DEPENDS_ON_UDP_ID"", cc.""DEPENDS_ON_PROPERTY_DEF_ID"", cc.""DEPENDS_ON_PROPERTY_VALUES"",
                            udp.""NAME"" AS ""UDP_NAME"",
                            cond_pd.""PROPERTY_CODE"" AS ""COND_PROPERTY_CODE"",
                            cond_ot.""NAME"" AS ""COND_PROPERTY_OBJECT_TYPE""
                            FROM ""MC_PREDEFINED_COLUMN_CONDITION"" cc
                            JOIN ""PREDEFINED_COLUMN"" pc ON pc.""ID"" = cc.""PREDEFINED_COLUMN_ID""
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON udp.""ID"" = cc.""DEPENDS_ON_UDP_ID""
                            LEFT JOIN ""MC_PROPERTY_DEF"" cond_pd ON cond_pd.""ID"" = cc.""DEPENDS_ON_PROPERTY_DEF_ID""
                            LEFT JOIN ""MC_OBJECT_TYPE"" cond_ot ON cond_ot.""ID"" = cond_pd.""OBJECT_TYPE_ID""
                            WHERE pc.""CONFIG_ID"" = @cfgId
                            ORDER BY cc.""PREDEFINED_COLUMN_ID"", cc.""ORDER_INDEX""";
                case "ORACLE":
                    return @"SELECT cc.PREDEFINED_COLUMN_ID, cc.ORDER_INDEX, cc.CONNECTOR,
                            cc.DEPENDS_ON_UDP_ID, cc.DEPENDS_ON_PROPERTY_DEF_ID, cc.DEPENDS_ON_PROPERTY_VALUES,
                            udp.NAME AS UDP_NAME,
                            cond_pd.PROPERTY_CODE AS COND_PROPERTY_CODE,
                            cond_ot.NAME AS COND_PROPERTY_OBJECT_TYPE
                            FROM MC_PREDEFINED_COLUMN_CONDITION cc
                            JOIN PREDEFINED_COLUMN pc ON pc.ID = cc.PREDEFINED_COLUMN_ID
                            LEFT JOIN MC_UDP_DEFINITION udp ON udp.ID = cc.DEPENDS_ON_UDP_ID
                            LEFT JOIN MC_PROPERTY_DEF cond_pd ON cond_pd.ID = cc.DEPENDS_ON_PROPERTY_DEF_ID
                            LEFT JOIN MC_OBJECT_TYPE cond_ot ON cond_ot.ID = cond_pd.OBJECT_TYPE_ID
                            WHERE pc.CONFIG_ID = :cfgId
                            ORDER BY cc.PREDEFINED_COLUMN_ID, cc.ORDER_INDEX";
                default: // SQL Server
                    return @"SELECT cc.[PREDEFINED_COLUMN_ID], cc.[ORDER_INDEX], cc.[CONNECTOR],
                            cc.[DEPENDS_ON_UDP_ID], cc.[DEPENDS_ON_PROPERTY_DEF_ID], cc.[DEPENDS_ON_PROPERTY_VALUES],
                            udp.[NAME] AS [UDP_NAME],
                            cond_pd.[PROPERTY_CODE] AS [COND_PROPERTY_CODE],
                            cond_ot.[NAME] AS [COND_PROPERTY_OBJECT_TYPE]
                            FROM [dbo].[MC_PREDEFINED_COLUMN_CONDITION] cc
                            JOIN [dbo].[PREDEFINED_COLUMN] pc ON pc.[ID] = cc.[PREDEFINED_COLUMN_ID]
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] udp ON udp.[ID] = cc.[DEPENDS_ON_UDP_ID]
                            LEFT JOIN [dbo].[MC_PROPERTY_DEF] cond_pd ON cond_pd.[ID] = cc.[DEPENDS_ON_PROPERTY_DEF_ID]
                            LEFT JOIN [dbo].[MC_OBJECT_TYPE] cond_ot ON cond_ot.[ID] = cond_pd.[OBJECT_TYPE_ID]
                            WHERE pc.[CONFIG_ID] = @cfgId
                            ORDER BY cc.[PREDEFINED_COLUMN_ID], cc.[ORDER_INDEX]";
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
                    pCfg.ParameterName = SqlDialect.Param(dbType, "cfgId");
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
        /// True when <paramref name="columnName"/> matches the admin-defined name
        /// of ANY loaded LOCKED predefined column (case-insensitive). Name-only,
        /// entity-free check used to EXEMPT locked predefined columns from naming
        /// standards: their exact name is part of the admin definition and
        /// "Locked" means the column must not be renamed (by the user OR by a
        /// naming rule). Without this, a suffix rule (e.g. _DATE on DateTime)
        /// would rewrite "CreateDate" -> "CreateDate_DATE", break the name-based
        /// locked-order match, and the column would be wrongly moved to the table
        /// end. Conditional locked rows are included: if a column bearing a locked
        /// name is present on an entity it was created by that rule, so the name
        /// is admin-owned regardless of the gating UDP. 2026-06-09.
        /// </summary>
        public bool IsLockedColumnName(string columnName)
        {
            if (!_isLoaded) return false;
            return IsLockedColumnName(_columns, columnName);
        }

        /// <summary>
        /// Pure name-match core for <see cref="IsLockedColumnName(string)"/>: true
        /// when any LOCKED row in <paramref name="columns"/> carries
        /// <paramref name="columnName"/> as its admin name (case-insensitive).
        /// State-free so it can be unit-tested without a DB load, mirroring
        /// <see cref="ComputeColumnsWedgedInLockedBlock"/>.
        /// </summary>
        public static bool IsLockedColumnName(IEnumerable<PredefinedColumn> columns, string columnName)
        {
            if (columns == null || string.IsNullOrEmpty(columnName)) return false;
            return columns.Any(c => c != null && c.IsLocked
                && string.Equals(c.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
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

                // Conditional rule: evaluate its full AND/OR condition list against the
                // entity via the shared engine (WP#280). It reads each gating UDP with
                // the same sparse-storage guard the old single-UDP compare used (an
                // unassigned UDP reads "" -> the IN-match cannot hold -> not locked, so
                // the user can edit). "Conditional locks release when the condition no
                // longer holds" - the 2026-05-24 semantic - is preserved.
                if (entity == null) continue;
                if (NamingValidationEngine.AreConditionsSatisfied(rule.Conditions, "Table", entity))
                    return rule;
            }
            return null;
        }

        /// <summary>
        /// Names of the predefined columns that currently APPLY to the given entity: every
        /// unconditional row, plus each conditional row whose ordered AND/OR condition list
        /// folds TRUE (via <see cref="NamingValidationEngine.AreConditionsSatisfied"/>). This
        /// is the SAME applicability that ApplyPredefined uses to auto-add columns, so the set
        /// treated as "predefined" (e.g. exempt from glossary loading) exactly matches the set
        /// that was actually added. Entity-scoped by design: a column gated on
        /// <c>TableClass='Parametre'</c> must NOT count as predefined on a
        /// <c>TableClass='Log'</c> table - otherwise a user column that happens to share a name
        /// (e.g. "OID") is wrongly skipped from the glossary.
        /// </summary>
        public HashSet<string> GetApplicableNames(dynamic entity)
        {
            if (!_isLoaded) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return GetApplicableNames(_columns, entity);
        }

        /// <summary>
        /// Applicability core: the columns from <paramref name="columns"/> whose ordered
        /// AND/OR condition list evaluates TRUE against <paramref name="entity"/> (an
        /// empty list == unconditional == always). Evaluated by the shared
        /// <see cref="NamingValidationEngine.AreConditionsSatisfied"/> engine (WP#280), so
        /// predefined applicability is bit-for-bit the same fold naming rules use, with the
        /// same sparse-storage / model-scoped-UDP handling. Unit-testable by passing a fake
        /// entity that implements <c>.Properties(path).Value</c> (see the naming engine
        /// tests' PartialEntity/FakeProp pattern).
        /// </summary>
        public static HashSet<string> GetApplicableNames(IEnumerable<PredefinedColumn> columns, dynamic entity)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (columns == null) return names;

            foreach (var col in columns)
            {
                if (col == null || string.IsNullOrEmpty(col.ColumnName)) continue;
                if (NamingValidationEngine.AreConditionsSatisfied(col.Conditions, "Table", entity))
                    names.Add(col.ColumnName);
            }
            return names;
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
