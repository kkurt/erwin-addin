using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Represents a naming standard rule loaded from MC_NAMING_STANDARD.
    /// Schema (post 2026-05-04 admin refactor):
    ///   OBJECT_TYPE_ID  -> MC_OBJECT_TYPE.ID  (resolved into ObjectType string)
    ///   PROPERTY_DEF_ID -> MC_PROPERTY_DEF.ID (resolved into PropertyCode string)
    /// Rules are now keyed on (ObjectType, PropertyCode) so admins can author a
    /// distinct rule per property of the same object (e.g. Table.Physical_Name
    /// vs Table.Logical_Name). The addin currently only validates Physical_Name
    /// equivalents for every object type.
    /// </summary>
    public class NamingStandardRule
    {
        public int Id { get; set; }
        public string ObjectType { get; set; }      // From MC_OBJECT_TYPE.NAME (e.g. "TABLE", "COLUMN")
        public string PropertyCode { get; set; }    // From MC_PROPERTY_DEF.PROPERTY_CODE (e.g. "Physical_Name")
        public int PropertyDefId { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }
        public string LengthOperator { get; set; }
        public int? LengthValue { get; set; }
        public string RegexpPattern { get; set; }
        public string ErrorMessage { get; set; }
        public bool AutoApply { get; set; }
        public bool IsActive { get; set; }
        public int SortOrder { get; set; }
        public int ConfigId { get; set; }
        public int? DependsOnUdpId { get; set; }
        public string DependsOnUdpValue { get; set; }
        public string DependsOnUdpName { get; set; }  // Resolved UDP name from JOIN
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
        // Keyed on (ObjectType, PropertyCode) - case-insensitive on both parts.
        private Dictionary<(string objectType, string propertyCode), List<NamingStandardRule>> _byKey;
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
            _byKey = new Dictionary<(string, string), List<NamingStandardRule>>(KeyComparer.Instance);
        }

        // Composite (objectType, propertyCode) comparer that ignores case on both
        // parts so callers can pass "Table" / "Column" / "Physical_Name" without
        // worrying about the exact DB casing.
        private sealed class KeyComparer : IEqualityComparer<(string objectType, string propertyCode)>
        {
            public static readonly KeyComparer Instance = new KeyComparer();
            public bool Equals((string objectType, string propertyCode) x, (string objectType, string propertyCode) y) =>
                StringComparer.OrdinalIgnoreCase.Equals(x.objectType, y.objectType) &&
                StringComparer.OrdinalIgnoreCase.Equals(x.propertyCode, y.propertyCode);
            public int GetHashCode((string objectType, string propertyCode) obj) =>
                HashCode.Combine(
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.objectType ?? ""),
                    StringComparer.OrdinalIgnoreCase.GetHashCode(obj.propertyCode ?? ""));
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
                _byKey.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"NamingStandardService: {_lastError}");
                    return false;
                }

                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
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
                        var pCfg = command.CreateParameter();
                        pCfg.ParameterName = dbType == "ORACLE" ? ":cfgId" : "@cfgId";
                        pCfg.Value = ctx.ActiveConfigId;
                        command.Parameters.Add(pCfg);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var rule = new NamingStandardRule
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    ObjectType = reader["OBJECT_TYPE"]?.ToString()?.Trim() ?? "",
                                    PropertyCode = reader["PROPERTY_CODE"]?.ToString()?.Trim() ?? "",
                                    PropertyDefId = Convert.ToInt32(reader["PROPERTY_DEF_ID"]),
                                    Prefix = reader["PREFIX"] == DBNull.Value ? "" : reader["PREFIX"]?.ToString()?.Trim() ?? "",
                                    Suffix = reader["SUFFIX"] == DBNull.Value ? "" : reader["SUFFIX"]?.ToString()?.Trim() ?? "",
                                    LengthOperator = reader["LENGTH_OPERATOR"] == DBNull.Value ? "" : reader["LENGTH_OPERATOR"]?.ToString()?.Trim() ?? "",
                                    LengthValue = reader["LENGTH_VALUE"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["LENGTH_VALUE"]),
                                    RegexpPattern = reader["REGEXP_PATTERN"] == DBNull.Value ? "" : reader["REGEXP_PATTERN"]?.ToString() ?? "",
                                    ErrorMessage = reader["ERROR_MESSAGE"] == DBNull.Value ? "" : reader["ERROR_MESSAGE"]?.ToString() ?? "",
                                    AutoApply = reader["AUTO_APPLY"] != DBNull.Value && Convert.ToBoolean(reader["AUTO_APPLY"]),
                                    IsActive = Convert.ToBoolean(reader["IS_ACTIVE"]),
                                    SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"]),
                                    ConfigId = Convert.ToInt32(reader["CONFIG_ID"]),
                                    DependsOnUdpId = reader["DEPENDS_ON_UDP_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["DEPENDS_ON_UDP_ID"]),
                                    DependsOnUdpValue = reader["DEPENDS_ON_UDP_VALUE"] == DBNull.Value ? "" : reader["DEPENDS_ON_UDP_VALUE"]?.ToString()?.Trim() ?? "",
                                    DependsOnUdpName = reader["UDP_NAME"] == DBNull.Value ? "" : reader["UDP_NAME"]?.ToString()?.Trim() ?? ""
                                };

                                if (!rule.IsActive) continue;

                                _allRules.Add(rule);

                                var key = (rule.ObjectType, rule.PropertyCode);
                                if (!_byKey.TryGetValue(key, out var bucket))
                                {
                                    bucket = new List<NamingStandardRule>();
                                    _byKey[key] = bucket;
                                }
                                bucket.Add(rule);
                            }
                        }
                    }
                }

                _isLoaded = true;
                var typeSummary = string.Join(", ", _byKey.Select(kv => $"{kv.Key.objectType}.{kv.Key.propertyCode}={kv.Value.Count}"));
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

        // Schema (post 2026-05-04 admin refactor):
        //   MC_NAMING_STANDARD.OBJECT_TYPE_ID  -> MC_OBJECT_TYPE.ID
        //   MC_NAMING_STANDARD.PROPERTY_DEF_ID -> MC_PROPERTY_DEF.ID
        // Filter: pd.DBMS_VERSION_ID IS NULL means "erwin built-in property" -
        // those are the only properties the addin validates today (e.g.
        // Physical_Name, Logical_Name). DBMS-version-specific PropertyDef rows
        // (DDL-only DDLA fields, dialect quirks) are intentionally excluded
        // because they don't correspond to anything the addin reads via SCAPI.
        private static string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ns.""ID"", ot.""NAME"" AS ""OBJECT_TYPE"", pd.""PROPERTY_CODE"",
                            ns.""PROPERTY_DEF_ID"", ns.""PREFIX"", ns.""SUFFIX"", ns.""LENGTH_OPERATOR"", ns.""LENGTH_VALUE"",
                            ns.""REGEXP_PATTERN"", ns.""ERROR_MESSAGE"", ns.""AUTO_APPLY"", ns.""IS_ACTIVE"", ns.""SORT_ORDER"",
                            ns.""CONFIG_ID"", ns.""DEPENDS_ON_UDP_ID"", ns.""DEPENDS_ON_UDP_VALUE"",
                            udp.""NAME"" AS ""UDP_NAME""
                            FROM ""MC_NAMING_STANDARD"" ns
                            JOIN ""MC_OBJECT_TYPE""  ot ON ot.""ID"" = ns.""OBJECT_TYPE_ID""
                            JOIN ""MC_PROPERTY_DEF"" pd ON pd.""ID"" = ns.""PROPERTY_DEF_ID""
                            LEFT JOIN ""MC_UDP_DEFINITION"" udp ON udp.""ID"" = ns.""DEPENDS_ON_UDP_ID""
                            WHERE ns.""IS_ACTIVE"" = true
                              AND ns.""CONFIG_ID"" = @cfgId
                              AND pd.""DBMS_VERSION_ID"" IS NULL
                            ORDER BY ot.""NAME"", pd.""PROPERTY_CODE"", ns.""SORT_ORDER""";

                case "ORACLE":
                    return @"SELECT ns.ID, ot.NAME AS OBJECT_TYPE, pd.PROPERTY_CODE,
                            ns.PROPERTY_DEF_ID, ns.PREFIX, ns.SUFFIX, ns.LENGTH_OPERATOR, ns.LENGTH_VALUE,
                            ns.REGEXP_PATTERN, ns.ERROR_MESSAGE, ns.AUTO_APPLY, ns.IS_ACTIVE, ns.SORT_ORDER,
                            ns.CONFIG_ID, ns.DEPENDS_ON_UDP_ID, ns.DEPENDS_ON_UDP_VALUE,
                            udp.NAME AS UDP_NAME
                            FROM MC_NAMING_STANDARD ns
                            JOIN MC_OBJECT_TYPE  ot ON ot.ID = ns.OBJECT_TYPE_ID
                            JOIN MC_PROPERTY_DEF pd ON pd.ID = ns.PROPERTY_DEF_ID
                            LEFT JOIN MC_UDP_DEFINITION udp ON udp.ID = ns.DEPENDS_ON_UDP_ID
                            WHERE ns.IS_ACTIVE = 1
                              AND ns.CONFIG_ID = :cfgId
                              AND pd.DBMS_VERSION_ID IS NULL
                            ORDER BY ot.NAME, pd.PROPERTY_CODE, ns.SORT_ORDER";

                case "MSSQL":
                default:
                    return @"SELECT ns.[ID], ot.[NAME] AS [OBJECT_TYPE], pd.[PROPERTY_CODE],
                            ns.[PROPERTY_DEF_ID], ns.[PREFIX], ns.[SUFFIX], ns.[LENGTH_OPERATOR], ns.[LENGTH_VALUE],
                            ns.[REGEXP_PATTERN], ns.[ERROR_MESSAGE], ns.[AUTO_APPLY], ns.[IS_ACTIVE], ns.[SORT_ORDER],
                            ns.[CONFIG_ID], ns.[DEPENDS_ON_UDP_ID], ns.[DEPENDS_ON_UDP_VALUE],
                            udp.[NAME] AS [UDP_NAME]
                            FROM [dbo].[MC_NAMING_STANDARD] ns
                            JOIN [dbo].[MC_OBJECT_TYPE]  ot ON ot.[ID] = ns.[OBJECT_TYPE_ID]
                            JOIN [dbo].[MC_PROPERTY_DEF] pd ON pd.[ID] = ns.[PROPERTY_DEF_ID]
                            LEFT JOIN [dbo].[MC_UDP_DEFINITION] udp ON udp.[ID] = ns.[DEPENDS_ON_UDP_ID]
                            WHERE ns.[IS_ACTIVE] = 1
                              AND ns.[CONFIG_ID] = @cfgId
                              AND pd.[DBMS_VERSION_ID] IS NULL
                            ORDER BY ot.[NAME], pd.[PROPERTY_CODE], ns.[SORT_ORDER]";
            }
        }

        /// <summary>
        /// Get active rules for a specific (object type, property code) pair.
        /// Both arguments are case-insensitive. The DB stores object types in
        /// upper case ("TABLE"/"COLUMN"/...) but callers may pass "Table"/"Column".
        /// Property codes follow erwin's PascalCase ("Physical_Name", "Logical_Name").
        /// </summary>
        public IEnumerable<NamingStandardRule> GetByObjectTypeAndProperty(string objectType, string propertyCode)
        {
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(propertyCode))
                return Enumerable.Empty<NamingStandardRule>();
            if (_byKey.TryGetValue((objectType, propertyCode), out var list))
                return list.OrderBy(r => r.SortOrder);
            return Enumerable.Empty<NamingStandardRule>();
        }

        /// <summary>
        /// Distinct UDP names referenced by any active rule's
        /// <c>DEPENDS_ON_UDP_NAME</c> condition. Used by the live Entity
        /// Editor watcher to know which UDPs to snapshot/diff each tick;
        /// returns an empty list if no conditional rules are loaded so
        /// the watcher can short-circuit cheaply.
        /// </summary>
        public IReadOnlyList<string> GetRelevantUdpNames()
        {
            return _allRules
                .Where(r => !string.IsNullOrEmpty(r.DependsOnUdpName))
                .Select(r => r.DependsOnUdpName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Reload()
        {
            LoadStandards();
        }
    }
}
