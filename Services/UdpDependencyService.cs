using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Runtime model for a UDP dependency rule loaded from MC_UDP_DEPENDENCY.
    /// </summary>
    public class UdpDependencyRule
    {
        public int Id { get; set; }
        public string ParentUdpName { get; set; }
        public string ChildUdpName { get; set; }
        public string ConditionOperator { get; set; }
        public string ConditionValues { get; set; }
        public string ChildValue { get; set; }
        public int SortOrder { get; set; }
        public string Description { get; set; }
    }

    /// <summary>
    /// Service for loading and caching UDP dependency rules from MC_UDP_DEPENDENCY.
    /// Uses DatabaseService for multi-database support (MSSQL, PostgreSQL, Oracle).
    /// </summary>
    public class UdpDependencyService
    {
        private static UdpDependencyService _instance;
        private static readonly object _lock = new object();

        private List<UdpDependencyRule> _dependencies;
        private bool _isLoaded;
        private string _lastError;
        private string _loadedObjectType;

        public static UdpDependencyService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new UdpDependencyService();
                    }
                }
                return _instance;
            }
        }

        private UdpDependencyService()
        {
            _dependencies = new List<UdpDependencyRule>();
        }

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;

        /// <summary>
        /// Load active dependency rules. If objectType is null/empty, loads ALL.
        /// Joins MC_UDP_DEPENDENCY with MC_UDP_DEFINITION to resolve parent/child names.
        /// Only loads IS_ACTIVE = 1 rules, ordered by SORT_ORDER.
        /// </summary>
        public bool LoadDependencies(string objectType = null)
        {
            try
            {
                _dependencies.Clear();
                _lastError = null;
                _loadedObjectType = objectType;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"UdpDependencyService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                bool filterByType = !string.IsNullOrEmpty(objectType);
                string query = GetDependencyQuery(dbType, filterByType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        if (filterByType)
                        {
                            var param = command.CreateParameter();
                            param.ParameterName = dbType == "ORACLE" ? ":objectType" : "@objectType";
                            param.Value = objectType;
                            command.Parameters.Add(param);
                        }

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                _dependencies.Add(new UdpDependencyRule
                                {
                                    Id = Convert.ToInt32(reader["DEP_ID"]),
                                    ParentUdpName = reader["PARENT_NAME"]?.ToString()?.Trim() ?? "",
                                    ChildUdpName = reader["CHILD_NAME"]?.ToString()?.Trim() ?? "",
                                    ConditionOperator = reader["CONDITION_OPERATOR"]?.ToString()?.Trim() ?? "",
                                    ConditionValues = reader["CONDITION_VALUES"]?.ToString() ?? "",
                                    ChildValue = reader["CHILD_VALUE"]?.ToString() ?? "",
                                    SortOrder = reader["DEP_SORT"] == DBNull.Value ? 0 : Convert.ToInt32(reader["DEP_SORT"]),
                                    Description = reader["DEP_DESC"]?.ToString() ?? ""
                                });
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"UdpDependencyService: Loaded {_dependencies.Count} active rules");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"UdpDependencyService.LoadDependencies error: {ex.Message}");
                return false;
            }
        }

        private string GetDependencyQuery(string dbType, bool filterByType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                {
                    string where = filterByType
                        ? @"WHERE parent.""OBJECT_TYPE"" = @objectType AND dep.""IS_ACTIVE"" = true"
                        : @"WHERE dep.""IS_ACTIVE"" = true";
                    return $@"SELECT dep.""ID"" AS ""DEP_ID"", parent.""NAME"" AS ""PARENT_NAME"", child.""NAME"" AS ""CHILD_NAME"",
                            dep.""CONDITION_OPERATOR"", dep.""CONDITION_VALUES"", dep.""CHILD_VALUE"",
                            dep.""SORT_ORDER"" AS ""DEP_SORT"", dep.""DESCRIPTION"" AS ""DEP_DESC""
                            FROM ""MC_UDP_DEPENDENCY"" dep
                            JOIN ""MC_UDP_DEFINITION"" parent ON dep.""PARENT_UDP_ID"" = parent.""ID""
                            JOIN ""MC_UDP_DEFINITION"" child ON dep.""CHILD_UDP_ID"" = child.""ID""
                            {where}
                            ORDER BY dep.""SORT_ORDER""";
                }

                case "ORACLE":
                {
                    string where = filterByType
                        ? "WHERE parent.OBJECT_TYPE = :objectType AND dep.IS_ACTIVE = 1"
                        : "WHERE dep.IS_ACTIVE = 1";
                    return $@"SELECT dep.ID AS DEP_ID, parent.NAME AS PARENT_NAME, child.NAME AS CHILD_NAME,
                            dep.CONDITION_OPERATOR, dep.CONDITION_VALUES, dep.CHILD_VALUE,
                            dep.SORT_ORDER AS DEP_SORT, dep.DESCRIPTION AS DEP_DESC
                            FROM MC_UDP_DEPENDENCY dep
                            JOIN MC_UDP_DEFINITION parent ON dep.PARENT_UDP_ID = parent.ID
                            JOIN MC_UDP_DEFINITION child ON dep.CHILD_UDP_ID = child.ID
                            {where}
                            ORDER BY dep.SORT_ORDER";
                }

                case "MSSQL":
                default:
                {
                    string where = filterByType
                        ? "WHERE parent.[OBJECT_TYPE] = @objectType AND dep.[IS_ACTIVE] = 1"
                        : "WHERE dep.[IS_ACTIVE] = 1";
                    return $@"SELECT dep.[ID] AS [DEP_ID], parent.[NAME] AS [PARENT_NAME], child.[NAME] AS [CHILD_NAME],
                            dep.[CONDITION_OPERATOR], dep.[CONDITION_VALUES], dep.[CHILD_VALUE],
                            dep.[SORT_ORDER] AS [DEP_SORT], dep.[DESCRIPTION] AS [DEP_DESC]
                            FROM [dbo].[MC_UDP_DEPENDENCY] dep
                            JOIN [dbo].[MC_UDP_DEFINITION] parent ON dep.[PARENT_UDP_ID] = parent.[ID]
                            JOIN [dbo].[MC_UDP_DEFINITION] child ON dep.[CHILD_UDP_ID] = child.[ID]
                            {where}
                            ORDER BY dep.[SORT_ORDER]";
                }
            }
        }

        /// <summary>
        /// Get all dependency rules where the given UDP name is the parent.
        /// </summary>
        public IEnumerable<UdpDependencyRule> GetByParent(string parentUdpName)
        {
            if (string.IsNullOrEmpty(parentUdpName)) return Enumerable.Empty<UdpDependencyRule>();
            return _dependencies
                .Where(d => d.ParentUdpName.Equals(parentUdpName, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.SortOrder);
        }

        /// <summary>
        /// Get all loaded dependency rules.
        /// </summary>
        public IEnumerable<UdpDependencyRule> GetAll()
        {
            return _dependencies.OrderBy(d => d.SortOrder);
        }

        public int Count => _dependencies.Count;

        public void Reload()
        {
            LoadDependencies(_loadedObjectType);
        }
    }
}
