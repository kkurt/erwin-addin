using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Represents a PREDEFINED_COLUMN entry with UDP condition support.
    /// </summary>
    public class PredefinedColumn
    {
        public int Id { get; set; }
        public int ModelId { get; set; }
        public string ColumnName { get; set; }
        public string DataType { get; set; }
        public bool Nullable { get; set; }
        public string DefaultValue { get; set; }
        public int DependsOnUdpId { get; set; }
        public string DependsOnUdpValue { get; set; }
        public string DependsOnUdpName { get; set; } // Resolved from JOIN
        public string DbType { get; set; }
        public int SortOrder { get; set; }

        // Backward compat — old code uses .Name
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
        /// Load predefined columns filtered by project and DB type.
        /// </summary>
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
                                string rowDbType = reader["DB_TYPE"] == DBNull.Value ? "" : reader["DB_TYPE"]?.ToString()?.Trim() ?? "";

                                // DB_TYPE filter: match platform or empty (all platforms)
                                if (!string.IsNullOrEmpty(platformDbType) && !string.IsNullOrEmpty(rowDbType) &&
                                    !rowDbType.Equals(platformDbType, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                string colName = reader["COLUMN_NAME"]?.ToString()?.Trim() ?? "";
                                if (string.IsNullOrEmpty(colName)) continue;

                                _columns.Add(new PredefinedColumn
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ModelId = rowConfigId,
                                    ColumnName = colName,
                                    DataType = reader["DATA_TYPE"]?.ToString()?.Trim() ?? "",
                                    Nullable = reader["NULLABLE"] != DBNull.Value && Convert.ToBoolean(reader["NULLABLE"]),
                                    DefaultValue = reader["DEFAULT_VALUE"] == DBNull.Value ? "" : reader["DEFAULT_VALUE"]?.ToString() ?? "",
                                    DependsOnUdpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]),
                                    DependsOnUdpValue = reader["DEPENDS_ON_UDP_VALUE"] == DBNull.Value ? "" : reader["DEPENDS_ON_UDP_VALUE"]?.ToString()?.Trim() ?? "",
                                    DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? "",
                                    DbType = rowDbType,
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
                            pc.""DEFAULT_VALUE"", pc.""DEPENDS_ON_UDP_ID"", pc.""DEPENDS_ON_UDP_VALUE"",
                            pc.""DB_TYPE"", pc.""SORT_ORDER"",
                            udp.""NAME"" AS ""UDP_NAME""
                            FROM ""PREDEFINED_COLUMN"" pc
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON pc.""DEPENDS_ON_UDP_ID"" = udp.""ID""
                            WHERE pc.""CONFIG_ID"" = @cfgId
                            ORDER BY pc.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT pc.ID, pc.CONFIG_ID, pc.COLUMN_NAME, pc.DATA_TYPE, pc.NULLABLE,
                            pc.DEFAULT_VALUE, pc.DEPENDS_ON_UDP_ID, pc.DEPENDS_ON_UDP_VALUE,
                            pc.DB_TYPE, pc.SORT_ORDER,
                            udp.NAME AS UDP_NAME
                            FROM PREDEFINED_COLUMN pc
                            LEFT JOIN MC_UDP_DEFINITION udp ON pc.DEPENDS_ON_UDP_ID = udp.ID
                            WHERE pc.CONFIG_ID = :cfgId
                            ORDER BY pc.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT pc.[ID], pc.[CONFIG_ID], pc.[COLUMN_NAME], pc.[DATA_TYPE], pc.[NULLABLE],
                            pc.[DEFAULT_VALUE], pc.[DEPENDS_ON_UDP_ID], pc.[DEPENDS_ON_UDP_VALUE],
                            pc.[DB_TYPE], pc.[SORT_ORDER],
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
        /// Get all loaded predefined columns.
        /// </summary>
        public IEnumerable<PredefinedColumn> GetAll() => _columns;

        public void Reload(string platformDbType = null)
        {
            LoadPredefinedColumns(platformDbType);
        }
    }
}
