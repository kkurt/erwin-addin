using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Runtime model for a UDP definition loaded from MC_UDP_DEFINITION + MC_UDP_LIST_OPTION.
    /// </summary>
    public class UdpDefinitionRuntime
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ObjectType { get; set; }
        public string UdpType { get; set; }
        public string DefaultValue { get; set; }
        public bool IsRequired { get; set; }
        public int? MinValue { get; set; }
        public int? MaxValue { get; set; }
        public int? MaxLength { get; set; }
        public string ValidationOperator { get; set; }
        public string ValidationValue { get; set; }
        public string ErrorMessage { get; set; }
        public string ApplyOn { get; set; }
        public int SortOrder { get; set; }
        public List<UdpListOption> ListOptions { get; set; } = new List<UdpListOption>();
    }

    /// <summary>
    /// List option for a List-type UDP.
    /// </summary>
    public class UdpListOption
    {
        public string Value { get; set; }
        public string DisplayText { get; set; }
        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Service for loading and caching UDP definitions from MC_UDP_DEFINITION and MC_UDP_LIST_OPTION.
    /// Uses DatabaseService for multi-database support (MSSQL, PostgreSQL, Oracle).
    /// </summary>
    public class UdpDefinitionService
    {
        private static UdpDefinitionService _instance;
        private static readonly object _lock = new object();

        // All definitions keyed by UDP name (across all object types)
        private Dictionary<string, UdpDefinitionRuntime> _definitions;
        // Definitions grouped by object type for filtered access
        private Dictionary<string, List<UdpDefinitionRuntime>> _byObjectType;
        private bool _isLoaded;
        private string _lastError;

        public static UdpDefinitionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new UdpDefinitionService();
                    }
                }
                return _instance;
            }
        }

        private UdpDefinitionService()
        {
            _definitions = new Dictionary<string, UdpDefinitionRuntime>(StringComparer.OrdinalIgnoreCase);
            _byObjectType = new Dictionary<string, List<UdpDefinitionRuntime>>(StringComparer.OrdinalIgnoreCase);
        }

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;

        /// <summary>
        /// Load all UDP definitions from MC_UDP_DEFINITION + MC_UDP_LIST_OPTION.
        /// If objectType is null or empty, loads ALL object types.
        /// </summary>
        public bool LoadDefinitions(string objectType = null)
        {
            try
            {
                _definitions.Clear();
                _byObjectType.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"UdpDefinitionService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                bool filterByType = !string.IsNullOrEmpty(objectType);
                string query = GetDefinitionQuery(dbType, filterByType);

                // Effective project IDs for corporate filtering (applied in-memory)
                var effectiveProjectIds = CorporateContextService.Instance.IsInitialized
                    ? new HashSet<int>(CorporateContextService.Instance.EffectiveProjectIds)
                    : null;

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
                                // Corporate scope filter (in-memory)
                                if (effectiveProjectIds != null)
                                {
                                    int rowProjectId = reader["PROJECT_ID"] == DBNull.Value ? 0 : Convert.ToInt32(reader["PROJECT_ID"]);
                                    if (rowProjectId > 0 && !effectiveProjectIds.Contains(rowProjectId))
                                        continue;
                                }

                                int defId = Convert.ToInt32(reader["DEF_ID"]);
                                string name = reader["NAME"]?.ToString()?.Trim() ?? "";
                                string objType = reader["OBJECT_TYPE"]?.ToString()?.Trim() ?? "";

                                if (string.IsNullOrEmpty(name)) continue;

                                // Key = "ObjectType:Name" to support same UDP name across different object types
                                string key = $"{objType}:{name}";

                                if (!_definitions.TryGetValue(key, out var def))
                                {
                                    def = new UdpDefinitionRuntime
                                    {
                                        Id = defId,
                                        Name = name,
                                        Description = reader["DESCRIPTION"]?.ToString() ?? "",
                                        ObjectType = objType,
                                        UdpType = reader["UDP_TYPE"]?.ToString()?.Trim() ?? "",
                                        DefaultValue = reader["DEFAULT_VALUE"]?.ToString() ?? "",
                                        IsRequired = Convert.ToBoolean(reader["IS_REQUIRED"]),
                                        MinValue = reader["MIN_VALUE"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MIN_VALUE"]),
                                        MaxValue = reader["MAX_VALUE"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MAX_VALUE"]),
                                        MaxLength = reader["MAX_LENGTH"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["MAX_LENGTH"]),
                                        ValidationOperator = reader["VALIDATION_OPERATOR"]?.ToString() ?? "",
                                        ValidationValue = reader["VALIDATION_VALUE"]?.ToString() ?? "",
                                        ErrorMessage = reader["ERROR_MESSAGE"]?.ToString() ?? "",
                                        ApplyOn = reader["APPLY_ON"]?.ToString()?.Trim() ?? "Both",
                                        SortOrder = reader["SORT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["SORT_ORDER"])
                                    };
                                    _definitions[key] = def;
                                }

                                // Add list option if present (uses DEF_ID to match — same row = same definition)
                                if (reader["OPT_VALUE"] != DBNull.Value)
                                {
                                    string optValue = reader["OPT_VALUE"]?.ToString() ?? "";
                                    if (!string.IsNullOrEmpty(optValue))
                                    {
                                        def.ListOptions.Add(new UdpListOption
                                        {
                                            Value = optValue,
                                            DisplayText = reader["OPT_DISPLAY"]?.ToString() ?? optValue,
                                            SortOrder = reader["OPT_ORDER"] == DBNull.Value ? 0 : Convert.ToInt32(reader["OPT_ORDER"])
                                        });
                                    }
                                }
                            }
                        }
                    }
                }

                // Sort list options and group by object type
                foreach (var def in _definitions.Values)
                {
                    def.ListOptions = def.ListOptions.OrderBy(o => o.SortOrder).ToList();

                    string ot = def.ObjectType ?? "";
                    if (!_byObjectType.ContainsKey(ot))
                        _byObjectType[ot] = new List<UdpDefinitionRuntime>();
                    _byObjectType[ot].Add(def);
                }

                _isLoaded = true;
                var typeSummary = string.Join(", ", _byObjectType.Select(kv => $"{kv.Key}={kv.Value.Count}"));
                System.Diagnostics.Debug.WriteLine($"UdpDefinitionService: Loaded {_definitions.Count} definitions ({typeSummary})");

                // Log List UDPs with their options for debugging
                foreach (var def in _definitions.Values.Where(d => d.UdpType?.Equals("List", StringComparison.OrdinalIgnoreCase) == true))
                {
                    string opts = def.ListOptions.Count > 0
                        ? string.Join(",", def.ListOptions.Select(o => o.Value))
                        : "(none)";
                    System.Diagnostics.Debug.WriteLine($"  List UDP '{def.Name}' ({def.ObjectType}): options=[{opts}]");
                }
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"UdpDefinitionService.LoadDefinitions error: {ex.Message}");
                return false;
            }
        }

        private string GetDefinitionQuery(string dbType, bool filterByType)
        {
            string whereClause;
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    whereClause = filterByType ? @"WHERE d.""OBJECT_TYPE"" = @objectType" : "";
                    return $@"SELECT d.""ID"" AS ""DEF_ID"", d.""NAME"", d.""DESCRIPTION"", d.""OBJECT_TYPE"", d.""UDP_TYPE"",
                            d.""DEFAULT_VALUE"", d.""IS_REQUIRED"", d.""MIN_VALUE"", d.""MAX_VALUE"", d.""MAX_LENGTH"",
                            d.""VALIDATION_OPERATOR"", d.""VALIDATION_VALUE"", d.""ERROR_MESSAGE"", d.""APPLY_ON"", d.""SORT_ORDER"",
                            d.""PROJECT_ID"",

                            o.""VALUE"" AS ""OPT_VALUE"", o.""DISPLAY_TEXT"" AS ""OPT_DISPLAY"", o.""SORT_ORDER"" AS ""OPT_ORDER""
                            FROM ""MC_UDP_DEFINITION"" d
                            LEFT JOIN ""MC_UDP_LIST_OPTION"" o ON o.""UDP_DEFINITION_ID"" = d.""ID""
                            {whereClause}
                            ORDER BY d.""SORT_ORDER"", o.""SORT_ORDER""";

                case "ORACLE":
                    whereClause = filterByType ? "WHERE d.OBJECT_TYPE = :objectType" : "";
                    return $@"SELECT d.ID AS DEF_ID, d.NAME, d.DESCRIPTION, d.OBJECT_TYPE, d.UDP_TYPE,
                            d.DEFAULT_VALUE, d.IS_REQUIRED, d.MIN_VALUE, d.MAX_VALUE, d.MAX_LENGTH,
                            d.VALIDATION_OPERATOR, d.VALIDATION_VALUE, d.ERROR_MESSAGE, d.APPLY_ON, d.SORT_ORDER,
                            d.PROJECT_ID,

                            o.VALUE AS OPT_VALUE, o.DISPLAY_TEXT AS OPT_DISPLAY, o.SORT_ORDER AS OPT_ORDER
                            FROM MC_UDP_DEFINITION d
                            LEFT JOIN MC_UDP_LIST_OPTION o ON o.UDP_DEFINITION_ID = d.ID
                            {whereClause}
                            ORDER BY d.SORT_ORDER, o.SORT_ORDER";

                case "MSSQL":
                default:
                    whereClause = filterByType ? "WHERE d.[OBJECT_TYPE] = @objectType" : "";
                    return $@"SELECT d.[ID] AS [DEF_ID], d.[NAME], d.[DESCRIPTION], d.[OBJECT_TYPE], d.[UDP_TYPE],
                            d.[DEFAULT_VALUE], d.[IS_REQUIRED], d.[MIN_VALUE], d.[MAX_VALUE], d.[MAX_LENGTH],
                            d.[VALIDATION_OPERATOR], d.[VALIDATION_VALUE], d.[ERROR_MESSAGE], d.[APPLY_ON], d.[SORT_ORDER],
                            d.[PROJECT_ID],

                            o.[VALUE] AS [OPT_VALUE], o.[DISPLAY_TEXT] AS [OPT_DISPLAY], o.[SORT_ORDER] AS [OPT_ORDER]
                            FROM [dbo].[MC_UDP_DEFINITION] d
                            LEFT JOIN [dbo].[MC_UDP_LIST_OPTION] o ON o.[UDP_DEFINITION_ID] = d.[ID]
                            {whereClause}
                            ORDER BY d.[SORT_ORDER], o.[SORT_ORDER]";
            }
        }

        /// <summary>
        /// Get a UDP definition by object type and name.
        /// </summary>
        public UdpDefinitionRuntime GetByName(string objectType, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string key = $"{objectType}:{name}";
            _definitions.TryGetValue(key, out var def);
            return def;
        }

        /// <summary>
        /// Get all loaded definitions ordered by SortOrder.
        /// </summary>
        public IEnumerable<UdpDefinitionRuntime> GetAll()
        {
            return _definitions.Values.OrderBy(d => d.SortOrder);
        }

        /// <summary>
        /// Get definitions for a specific object type (e.g. "Table", "Column").
        /// </summary>
        public IEnumerable<UdpDefinitionRuntime> GetByObjectType(string objectType)
        {
            if (string.IsNullOrEmpty(objectType)) return GetAll();
            if (_byObjectType.TryGetValue(objectType, out var list))
                return list.OrderBy(d => d.SortOrder);
            return Enumerable.Empty<UdpDefinitionRuntime>();
        }

        /// <summary>
        /// Get all distinct object types that have definitions loaded.
        /// </summary>
        public IEnumerable<string> GetLoadedObjectTypes()
        {
            return _byObjectType.Keys;
        }

        public int Count => _definitions.Count;

        public void Reload()
        {
            LoadDefinitions();
        }
    }
}
