using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using EliteSoft.MetaAdmin.Shared.Data;
using EliteSoft.MetaAdmin.Shared.Data.Entities;
using EliteSoft.MetaAdmin.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Runtime service for dependency sets.
    /// Loads sets/mappings/relations from DB, fetches external table data,
    /// and evaluates cascading dependencies when UDP values change.
    /// Replaces the old MC_UDP_DEPENDENCY system.
    /// </summary>
    public class DependencySetRuntimeService
    {
        private List<DependencySet> _sets;
        private readonly Dictionary<int, List<Dictionary<string, string>>> _tableDataCache; // connectionId+table -> rows
        private readonly Dictionary<string, string> _connectionStringCache; // connectionDefId -> connStr+dbType
        private bool _isLoaded;
        private string _lastError;

        public event Action<string> OnLog;

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;
        public int SetCount => _sets?.Count ?? 0;
        public int MappingCount => _sets?.Sum(s => s.Mappings?.Count ?? 0) ?? 0;

        public DependencySetRuntimeService()
        {
            _sets = new List<DependencySet>();
            _tableDataCache = new Dictionary<int, List<Dictionary<string, string>>>();
            _connectionStringCache = new Dictionary<string, string>();
        }

        /// <summary>
        /// Load dependency sets, mappings, and relations from DB for effective models.
        /// Also pre-fetches external table data for TABLE source mappings.
        /// </summary>
        public bool Load()
        {
            try
            {
                _sets.Clear();
                _tableDataCache.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    return false;
                }

                var bootstrapService = new RegistryBootstrapService();
                var config = bootstrapService.GetConfig();
                if (config == null || !config.IsConfigured) return false;

                // Load sets with mappings and relations for effective models
                var effectiveModelIds = CorporateContextService.Instance.IsInitialized
                    ? CorporateContextService.Instance.EffectiveModelIds
                    : null;

                using (var context = new RepoDbContext(config))
                {
                    var query = context.DependencySets
                        .Include(s => s.Mappings)
                            .ThenInclude(m => m.SourceUdp)
                        .Include(s => s.Mappings)
                            .ThenInclude(m => m.TargetUdp)
                        .Include(s => s.Relations)
                        .AsQueryable();

                    if (effectiveModelIds != null && effectiveModelIds.Count > 0)
                        query = query.Where(s => effectiveModelIds.Contains(s.ModelId));

                    _sets = query.OrderBy(s => s.Name).ToList();
                }

                Log($"DependencySetRuntime: Loaded {_sets.Count} set(s), {MappingCount} mapping(s)");

                // Pre-fetch external table data for TABLE source mappings
                PreFetchTableData(config);

                _isLoaded = true;
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                Log($"DependencySetRuntime.Load error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Evaluate dependencies when a UDP value changes.
        /// Returns a dictionary of child UDP names and their new values/list options.
        /// </summary>
        public List<DependencyUpdate> EvaluateUdpChange(string changedUdpName, string newValue, Dictionary<string, string> currentUdpValues)
        {
            var updates = new List<DependencyUpdate>();
            if (!_isLoaded || _sets == null) return updates;

            foreach (var set in _sets)
            {
                // Find mappings where changed UDP is the source
                var sourceMappings = set.Mappings?
                    .Where(m => m.SourceType == "UDP" && m.SourceUdp != null
                        && m.SourceUdp.Name.Equals(changedUdpName, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.SortOrder)
                    .ToList();

                if (sourceMappings == null || sourceMappings.Count == 0) continue;

                foreach (var mapping in sourceMappings)
                {
                    if (mapping.TargetType == "UDP" && mapping.TargetUdp != null)
                    {
                        // UDP -> UDP: direct value mapping (like old system)
                        updates.Add(new DependencyUpdate
                        {
                            UdpName = mapping.TargetUdp.Name,
                            UpdateType = DependencyUpdateType.SetValue,
                            Value = newValue,
                            SetName = set.Name
                        });
                    }
                    else if (mapping.TargetType == "TABLE" && !string.IsNullOrEmpty(mapping.TargetTable))
                    {
                        // UDP -> TABLE: filter table by UDP value, then cascade to related mappings
                        var cascadeUpdates = EvaluateCascade(set, mapping, newValue);
                        updates.AddRange(cascadeUpdates);
                    }
                }

                // Also check TABLE -> UDP mappings where a relation connects through the changed UDP
                var tableToUdpUpdates = EvaluateTableToUdpCascade(set, changedUdpName, newValue, currentUdpValues);
                updates.AddRange(tableToUdpUpdates);
            }

            if (updates.Count > 0)
            {
                Log($"DependencySetRuntime: UDP '{changedUdpName}'='{newValue}' triggered {updates.Count} update(s)");
            }

            return updates;
        }

        /// <summary>
        /// Get List UDP options from a TABLE source mapping.
        /// Applies cascade filtering through relations if a parent filter is active.
        /// </summary>
        public List<string> GetListUdpOptions(string udpName, Dictionary<string, string> currentUdpValues = null)
        {
            if (!_isLoaded || _sets == null) return null;

            foreach (var set in _sets)
            {
                var mapping = set.Mappings?.FirstOrDefault(m =>
                    m.TargetType == "UDP" && m.TargetUdp != null
                    && m.TargetUdp.Name.Equals(udpName, StringComparison.OrdinalIgnoreCase)
                    && m.SourceType == "TABLE");

                if (mapping == null) continue;

                var tableData = GetCachedTableData(mapping.SourceConnectionId, mapping.SourceTable);
                if (tableData == null) continue;

                // Check if this table has a parent relation (cascade filter)
                var filteredRows = ApplyRelationFilter(set, mapping.SourceTable, tableData, currentUdpValues);

                var column = mapping.SourceColumn;
                return filteredRows
                    .Select(row => row.ContainsKey(column) ? row[column] : null)
                    .Where(v => !string.IsNullOrEmpty(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v)
                    .ToList();
            }

            return null;
        }

        /// <summary>
        /// Apply relation-based cascade filter on table data.
        /// If this table is a child in a relation (TABLE_B), find the parent value from UDPs
        /// and filter rows by FK.
        /// </summary>
        private List<Dictionary<string, string>> ApplyRelationFilter(
            DependencySet set,
            string tableName,
            List<Dictionary<string, string>> tableData,
            Dictionary<string, string> currentUdpValues)
        {
            if (currentUdpValues == null || currentUdpValues.Count == 0)
                return tableData;

            // Find relations where this table is the child (TABLE_B)
            var parentRelations = set.Relations?
                .Where(r => r.TableB.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (parentRelations == null || parentRelations.Count == 0)
                return tableData;

            foreach (var relation in parentRelations)
            {
                // Find the mapping that links the parent table to a UDP
                var parentMapping = set.Mappings?.FirstOrDefault(m =>
                    m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                    && m.SourceTable != null
                    && m.SourceTable.Equals(relation.TableA, StringComparison.OrdinalIgnoreCase));

                if (parentMapping == null) continue;

                // Get current value of the parent UDP
                string parentUdpName = parentMapping.TargetUdp.Name;
                if (!currentUdpValues.TryGetValue(parentUdpName, out var parentUdpValue))
                    continue;
                if (string.IsNullOrEmpty(parentUdpValue)) continue;

                // Get parent table data and find PK(s) matching the UDP value
                var parentData = GetCachedTableData(parentMapping.SourceConnectionId, parentMapping.SourceTable);
                if (parentData == null) continue;

                var matchingPks = parentData
                    .Where(row => row.ContainsKey(parentMapping.SourceColumn)
                        && row[parentMapping.SourceColumn].Equals(parentUdpValue, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.ContainsKey(relation.ColumnA) ? row[relation.ColumnA] : null)
                    .Where(pk => !string.IsNullOrEmpty(pk))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (matchingPks.Count == 0) continue;

                // Filter child table by FK
                tableData = tableData
                    .Where(row => row.ContainsKey(relation.ColumnB) && matchingPks.Contains(row[relation.ColumnB]))
                    .ToList();

                Log($"DependencySetRuntime: Cascade filter '{relation.TableA}.{parentMapping.SourceColumn}'='{parentUdpValue}' -> {tableName} filtered to {tableData.Count} rows");
            }

            return tableData;
        }

        /// <summary>
        /// Refresh external table data cache for a specific connection.
        /// Called when external data might have changed.
        /// </summary>
        public void RefreshTableData()
        {
            try
            {
                _tableDataCache.Clear();
                var config = new RegistryBootstrapService().GetConfig();
                if (config != null) PreFetchTableData(config);
            }
            catch (Exception ex)
            {
                Log($"DependencySetRuntime.RefreshTableData error: {ex.Message}");
            }
        }

        #region Cascade Evaluation

        private List<DependencyUpdate> EvaluateCascade(DependencySet set, DependencyMapping sourceMapping, string filterValue)
        {
            var updates = new List<DependencyUpdate>();

            // Get source table data filtered by the UDP value
            var sourceData = GetCachedTableData(sourceMapping.TargetConnectionId, sourceMapping.TargetTable);
            if (sourceData == null || sourceData.Count == 0) return updates;

            // Find relations from the target table to other tables
            var relations = set.Relations?
                .Where(r => r.TableA.Equals(sourceMapping.TargetTable, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (relations == null || relations.Count == 0) return updates;

            // Get PK values from filtered source data
            foreach (var relation in relations)
            {
                var filteredPks = sourceData
                    .Where(row => row.ContainsKey(sourceMapping.SourceColumn ?? "") &&
                                  row[sourceMapping.SourceColumn ?? ""].Equals(filterValue, StringComparison.OrdinalIgnoreCase))
                    .Select(row => row.ContainsKey(relation.ColumnA) ? row[relation.ColumnA] : null)
                    .Where(pk => !string.IsNullOrEmpty(pk))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (filteredPks.Count == 0) continue;

                // Find child table data filtered by FK
                var childData = GetCachedTableData(relation.ConnectionId, relation.TableB);
                if (childData == null) continue;

                var filteredChildRows = childData
                    .Where(row => row.ContainsKey(relation.ColumnB) && filteredPks.Contains(row[relation.ColumnB]))
                    .ToList();

                // Find mappings from child table to UDPs
                var childMappings = set.Mappings?
                    .Where(m => m.SourceType == "TABLE"
                        && m.SourceTable != null
                        && m.SourceTable.Equals(relation.TableB, StringComparison.OrdinalIgnoreCase)
                        && m.TargetType == "UDP" && m.TargetUdp != null)
                    .ToList();

                if (childMappings == null) continue;

                foreach (var childMapping in childMappings)
                {
                    var col = childMapping.SourceColumn;
                    var options = filteredChildRows
                        .Select(row => row.ContainsKey(col) ? row[col] : null)
                        .Where(v => !string.IsNullOrEmpty(v))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(v => v)
                        .ToList();

                    updates.Add(new DependencyUpdate
                    {
                        UdpName = childMapping.TargetUdp.Name,
                        UpdateType = DependencyUpdateType.SetListOptions,
                        ListOptions = options,
                        SetName = set.Name
                    });
                }
            }

            return updates;
        }

        private List<DependencyUpdate> EvaluateTableToUdpCascade(DependencySet set, string changedUdpName, string newValue, Dictionary<string, string> currentUdpValues)
        {
            var updates = new List<DependencyUpdate>();

            // Find TABLE->UDP mappings where this UDP is the source
            // Pattern: TABLE.column -> UDP (source), and the table is related to another table via relation
            var tableSourceMappings = set.Mappings?
                .Where(m => m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                    && m.TargetUdp.Name.Equals(changedUdpName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (tableSourceMappings == null || tableSourceMappings.Count == 0) return updates;

            foreach (var mapping in tableSourceMappings)
            {
                // This UDP gets its value from TABLE.column. When value changes,
                // find rows in the table that match, get PK, cascade via relations
                var tableData = GetCachedTableData(mapping.SourceConnectionId, mapping.SourceTable);
                if (tableData == null) continue;

                var matchingRows = tableData
                    .Where(row => row.ContainsKey(mapping.SourceColumn) &&
                                  row[mapping.SourceColumn].Equals(newValue, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Find relations from this source table
                var relations = set.Relations?
                    .Where(r => r.TableA.Equals(mapping.SourceTable, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (relations == null) continue;

                foreach (var relation in relations)
                {
                    var pks = matchingRows
                        .Select(r => r.ContainsKey(relation.ColumnA) ? r[relation.ColumnA] : null)
                        .Where(pk => !string.IsNullOrEmpty(pk))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (pks.Count == 0) continue;

                    var childData = GetCachedTableData(relation.ConnectionId, relation.TableB);
                    if (childData == null) continue;

                    var filteredRows = childData
                        .Where(row => row.ContainsKey(relation.ColumnB) && pks.Contains(row[relation.ColumnB]))
                        .ToList();

                    // Find UDP targets from child table
                    var childUdpMappings = set.Mappings?
                        .Where(m => m.SourceType == "TABLE"
                            && m.SourceTable != null
                            && m.SourceTable.Equals(relation.TableB, StringComparison.OrdinalIgnoreCase)
                            && m.TargetType == "UDP" && m.TargetUdp != null)
                        .ToList();

                    if (childUdpMappings == null) continue;

                    foreach (var childMapping in childUdpMappings)
                    {
                        var col = childMapping.SourceColumn;
                        var options = filteredRows
                            .Select(row => row.ContainsKey(col) ? row[col] : null)
                            .Where(v => !string.IsNullOrEmpty(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(v => v)
                            .ToList();

                        updates.Add(new DependencyUpdate
                        {
                            UdpName = childMapping.TargetUdp.Name,
                            UpdateType = DependencyUpdateType.SetListOptions,
                            ListOptions = options,
                            SetName = set.Name
                        });
                    }
                }
            }

            return updates;
        }

        #endregion

        #region External Table Data

        private void PreFetchTableData(BootstrapConfig config)
        {
            if (_sets == null) return;

            // Collect unique table sources that need fetching
            var tableSources = _sets
                .SelectMany(s => s.Mappings ?? Enumerable.Empty<DependencyMapping>())
                .Where(m => m.SourceType == "TABLE" && !string.IsNullOrEmpty(m.SourceTable))
                .Select(m => new { ConnId = m.SourceConnectionId, Table = m.SourceTable })
                .Union(
                    _sets.SelectMany(s => s.Mappings ?? Enumerable.Empty<DependencyMapping>())
                    .Where(m => m.TargetType == "TABLE" && !string.IsNullOrEmpty(m.TargetTable))
                    .Select(m => new { ConnId = m.TargetConnectionId, Table = m.TargetTable })
                )
                .Union(
                    _sets.SelectMany(s => s.Relations ?? Enumerable.Empty<DependencyRelation>())
                    .SelectMany(r => new[]
                    {
                        new { ConnId = r.ConnectionId, Table = r.TableA },
                        new { ConnId = r.ConnectionId, Table = r.TableB }
                    })
                )
                .Distinct()
                .ToList();

            foreach (var source in tableSources)
            {
                try
                {
                    FetchAndCacheTableData(config, source.ConnId, source.Table);
                }
                catch (Exception ex)
                {
                    Log($"DependencySetRuntime: Failed to fetch {source.Table}: {ex.Message}");
                }
            }
        }

        private void FetchAndCacheTableData(BootstrapConfig config, int? connectionId, string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return;

            int cacheKey = GetTableCacheKey(connectionId, tableName);
            if (_tableDataCache.ContainsKey(cacheKey)) return;

            string connStr = null;
            string dbType = null;

            if (connectionId.HasValue)
            {
                var connInfo = GetConnectionInfo(config, connectionId.Value);
                if (connInfo == null)
                {
                    Log($"DependencySetRuntime: CONNECTION_DEF ID={connectionId} not found");
                    return;
                }
                connStr = connInfo.Value.connStr;
                dbType = connInfo.Value.dbType;
                Log($"DependencySetRuntime: Fetching '{tableName}' from CONNECTION_DEF ID={connectionId} ({dbType}, {connInfo.Value.host}/{connInfo.Value.database})");
            }
            else
            {
                connStr = config.GetConnectionString();
                dbType = config.DbType;
                Log($"DependencySetRuntime: Fetching '{tableName}' from repo DB ({dbType})");
            }

            var rows = new List<Dictionary<string, string>>();
            try
            {
                using (var conn = DatabaseService.Instance.CreateConnection(dbType, connStr))
                {
                    conn.Open();
                    string quotedTable = QuoteTable(dbType, tableName);
                    string query = $"SELECT * FROM {quotedTable}";

                    Log($"DependencySetRuntime: Query = {query}");
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = query;
                        cmd.CommandTimeout = 10;
                        using (var reader = cmd.ExecuteReader())
                        {
                            var columns = Enumerable.Range(0, reader.FieldCount)
                                .Select(i => reader.GetName(i))
                                .ToList();
                            Log($"DependencySetRuntime: Columns = [{string.Join(", ", columns)}]");

                            while (reader.Read())
                            {
                                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var col in columns)
                                {
                                    var val = reader[col];
                                    row[col] = val == DBNull.Value ? "" : val.ToString().Trim();
                                }
                                rows.Add(row);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DependencySetRuntime: ERROR fetching '{tableName}': {ex.Message}");
            }

            _tableDataCache[cacheKey] = rows;
            Log($"DependencySetRuntime: Cached {rows.Count} rows from {tableName}");
        }

        private List<Dictionary<string, string>> GetCachedTableData(int? connectionId, string tableName)
        {
            if (string.IsNullOrEmpty(tableName)) return null;
            int key = GetTableCacheKey(connectionId, tableName);
            return _tableDataCache.TryGetValue(key, out var data) ? data : null;
        }

        private int GetTableCacheKey(int? connectionId, string tableName)
        {
            return HashCode.Combine(connectionId ?? 0, tableName?.ToUpperInvariant() ?? "");
        }

        private (string connStr, string dbType, string host, string database)? GetConnectionInfo(BootstrapConfig config, int connectionDefId)
        {
            string repoDbType = config.DbType;

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                string query = repoDbType?.ToUpper() switch
                {
                    "POSTGRESQL" => @"SELECT ""DB_TYPE"", ""HOST"", ""PORT"", ""DB_SCHEMA"", ""USERNAME"", ""PASSWORD"" FROM ""CONNECTION_DEF"" WHERE ""ID"" = @id",
                    "ORACLE" => "SELECT DB_TYPE, HOST, PORT, DB_SCHEMA, USERNAME, PASSWORD FROM CONNECTION_DEF WHERE ID = :id",
                    _ => "SELECT [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD] FROM [dbo].[CONNECTION_DEF] WHERE [ID] = @id"
                };

                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                {
                    var param = cmd.CreateParameter();
                    param.ParameterName = repoDbType == "ORACLE" ? ":id" : "@id";
                    param.Value = connectionDefId;
                    cmd.Parameters.Add(param);

                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) return null;

                        string dbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL";
                        string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                        string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                        string schema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "";
                        string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                        string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                        string username = PasswordEncryptionService.Decrypt(encUser);
                        string password = PasswordEncryptionService.Decrypt(encPass);

                        // Fallback to bootstrap credentials if DPAPI fails
                        if (string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser))
                        {
                            var bootstrapConfig = DatabaseService.Instance.GetConfig();
                            if (bootstrapConfig != null)
                            {
                                username = bootstrapConfig.Username;
                                password = bootstrapConfig.Password;
                            }
                        }

                        string connStr = BuildConnectionString(dbType, host, port, schema, username, password);
                        return (connStr, dbType, host, schema);
                    }
                }
            }
        }

        private string BuildConnectionString(string dbType, string host, string port, string schema, string username, string password)
        {
            switch (dbType?.ToUpper())
            {
                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={schema})));User Id={username};Password={password};Connection Timeout=10;";
                case "POSTGRESQL":
                    return $"Host={host};Port={port};Database={schema};Username={username};Password={password};Timeout=10;";
                default:
                    return $"Server={host},{port};Database={schema};User Id={username};Password={password};TrustServerCertificate=True;Connection Timeout=10;";
            }
        }

        private string QuoteTable(string dbType, string tableName)
        {
            return dbType?.ToUpper() switch
            {
                "POSTGRESQL" => $"\"{tableName}\"",
                "ORACLE" => tableName,
                _ => $"[{tableName}]"
            };
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    public enum DependencyUpdateType
    {
        SetValue,       // Set a specific value to the UDP
        SetListOptions  // Replace the List UDP's available options
    }

    public class DependencyUpdate
    {
        public string UdpName { get; set; }
        public DependencyUpdateType UpdateType { get; set; }
        public string Value { get; set; }           // For SetValue
        public List<string> ListOptions { get; set; } // For SetListOptions
        public string SetName { get; set; }          // Which set triggered this
    }
}
