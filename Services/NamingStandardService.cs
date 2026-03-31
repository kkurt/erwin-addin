using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Represents a naming standard rule loaded from MC_NAMING_STANDARD.
    /// </summary>
    public class NamingStandardRule
    {
        public int Id { get; set; }
        public string ObjectType { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public string LengthOperator { get; set; }
        public int? LengthValue { get; set; }
        public string RegexpPattern { get; set; }
        public string ErrorMessage { get; set; }
        public bool AutoApply { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Service for loading and caching naming standard rules from MC_NAMING_STANDARD.
    /// Uses DatabaseService for multi-database support (MSSQL, PostgreSQL, Oracle).
    /// </summary>
    public class NamingStandardService
    {
        private static NamingStandardService _instance;
        private static readonly object _lock = new object();

        private List<NamingStandardRule> _allRules;
        private Dictionary<string, List<NamingStandardRule>> _byObjectType;
        private bool _isLoaded;
        private string _lastError;

        public static NamingStandardService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new NamingStandardService();
                    }
                }
                return _instance;
            }
        }

        private NamingStandardService()
        {
            _allRules = new List<NamingStandardRule>();
            _byObjectType = new Dictionary<string, List<NamingStandardRule>>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;
        public int Count => _allRules.Count;

        /// <summary>
        /// Load all active naming standard rules from MC_NAMING_STANDARD.
        /// </summary>
        public bool LoadStandards()
        {
            try
            {
                _allRules.Clear();
                _byObjectType.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"NamingStandardService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var rule = new NamingStandardRule
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ObjectType = reader["OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                                    Prefix = reader["PREFIX"] == DBNull.Value ? "" : reader["PREFIX"]?.ToString()?.Trim() ?? "",
                                    Suffix = reader["SUFFIX"] == DBNull.Value ? "" : reader["SUFFIX"]?.ToString()?.Trim() ?? "",
                                    LengthOperator = reader["LENGTH_OPERATOR"] == DBNull.Value ? "" : reader["LENGTH_OPERATOR"]?.ToString()?.Trim() ?? "",
                                    LengthValue = reader["LENGTH_VALUE"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["LENGTH_VALUE"]),
                                    RegexpPattern = reader["REGEXP_PATTERN"] == DBNull.Value ? "" : reader["REGEXP_PATTERN"]?.ToString() ?? "",
                                    ErrorMessage = reader["ERROR_MESSAGE"] == DBNull.Value ? "" : reader["ERROR_MESSAGE"]?.ToString() ?? "",
                                    AutoApply = reader["AUTO_APPLY"] != DBNull.Value && Convert.ToBoolean(reader["AUTO_APPLY"]),
                                    IsActive = Convert.ToBoolean(reader["IS_ACTIVE"]),
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"])
                                };

                                if (!rule.IsActive) continue;

                                _allRules.Add(rule);

                                if (!_byObjectType.ContainsKey(rule.ObjectType))
                                    _byObjectType[rule.ObjectType] = new List<NamingStandardRule>();
                                _byObjectType[rule.ObjectType].Add(rule);
                            }
                        }
                    }
                }

                _isLoaded = true;
                var typeSummary = string.Join(", ", _byObjectType.Select(kv => $"{kv.Key}={kv.Value.Count}"));
                System.Diagnostics.Debug.WriteLine($"NamingStandardService: Loaded {_allRules.Count} active rules ({typeSummary})");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"NamingStandardService.LoadStandards error: {ex.Message}");
                return false;
            }
        }

        private string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""ID"", ""OBJECT_TYPE"", ""PREFIX"", ""SUFFIX"", ""LENGTH_OPERATOR"", ""LENGTH_VALUE"",
                            ""REGEXP_PATTERN"", ""ERROR_MESSAGE"", ""AUTO_APPLY"", ""IS_ACTIVE"", ""SORT_ORDER""
                            FROM ""MC_NAMING_STANDARD""
                            WHERE ""IS_ACTIVE"" = true
                            ORDER BY ""OBJECT_TYPE"", ""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT ID, OBJECT_TYPE, PREFIX, SUFFIX, LENGTH_OPERATOR, LENGTH_VALUE,
                            REGEXP_PATTERN, ERROR_MESSAGE, AUTO_APPLY, IS_ACTIVE, SORT_ORDER
                            FROM MC_NAMING_STANDARD
                            WHERE IS_ACTIVE = 1
                            ORDER BY OBJECT_TYPE, SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT [ID], [OBJECT_TYPE], [PREFIX], [SUFFIX], [LENGTH_OPERATOR], [LENGTH_VALUE],
                            [REGEXP_PATTERN], [ERROR_MESSAGE], [AUTO_APPLY], [IS_ACTIVE], [SORT_ORDER]
                            FROM [dbo].[MC_NAMING_STANDARD]
                            WHERE [IS_ACTIVE] = 1
                            ORDER BY [OBJECT_TYPE], [SORT_ORDER]";
            }
        }

        /// <summary>
        /// Get active rules for a specific object type (e.g. "Table", "Column").
        /// </summary>
        public IEnumerable<NamingStandardRule> GetByObjectType(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return Enumerable.Empty<NamingStandardRule>();
            if (_byObjectType.TryGetValue(objectType, out var list))
                return list.OrderBy(r => r.SortOrder);
            return Enumerable.Empty<NamingStandardRule>();
        }

        public void Reload()
        {
            LoadStandards();
        }
    }
}
