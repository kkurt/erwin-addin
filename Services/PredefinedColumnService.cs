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
        public string DefaultValue { get; set; }
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

                                _columns.Add(new PredefinedColumn
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ModelId = rowConfigId,
                                    ColumnName = colName,
                                    DataType = reader["DATA_TYPE"]?.ToString()?.Trim() ?? "",
                                    Nullable = reader["NULLABLE"] != DBNull.Value && Convert.ToBoolean(reader["NULLABLE"]),
                                    IsPrimaryKey = isPrimaryKey,
                                    DefaultValue = reader["DEFAULT_VALUE"] == DBNull.Value ? "" : reader["DEFAULT_VALUE"]?.ToString() ?? "",
                                    DependsOnUdpId = dependsOnUdpId,
                                    DependsOnUdpValue = reader["DEPENDS_ON_UDP_VALUE"] == DBNull.Value ? "" : reader["DEPENDS_ON_UDP_VALUE"]?.ToString()?.Trim() ?? "",
                                    DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"])
                                });
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"PredefinedColumnService: Loaded {_columns.Count} entries");
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
                            pc.""IS_PRIMARY_KEY"",
                            pc.""DEFAULT_VALUE"", pc.""DEPENDS_ON_UDP_ID"", pc.""DEPENDS_ON_UDP_VALUE"",
                            pc.""SORT_ORDER"",
                            udp.""NAME"" AS ""UDP_NAME""
                            FROM ""PREDEFINED_COLUMN"" pc
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON pc.""DEPENDS_ON_UDP_ID"" = udp.""ID""
                            WHERE pc.""CONFIG_ID"" = @cfgId
                            ORDER BY pc.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT pc.ID, pc.CONFIG_ID, pc.COLUMN_NAME, pc.DATA_TYPE, pc.NULLABLE,
                            pc.IS_PRIMARY_KEY,
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
                            pc.[IS_PRIMARY_KEY],
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

        public void Reload(string platformDbType = null)
        {
            LoadPredefinedColumns(platformDbType);
        }
    }
}
