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
        private readonly Dictionary<int, List<Dictionary<string, string>>> _tableDataCache;
        private readonly Dictionary<string, string> _connectionStringCache;
        // Cache: parent UDP name -> list of affected child UDPs with their PT paths (built once at load)
        private Dictionary<string, List<CascadeTarget>> _cascadeMap;
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
            _cascadeMap = new Dictionary<string, List<CascadeTarget>>(StringComparer.OrdinalIgnoreCase);
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

                // Single config scope (one mart path -> one config row)
                var ctx = ConfigContextService.Instance;
                if (!ctx.IsInitialized)
                {
                    _lastError = "ConfigContext not initialized.";
                    _isLoaded = false;
                    return false;
                }
                int cfgId = ctx.ActiveConfigId;

                using (var context = new RepoDbContext(config))
                {
                    _sets = context.DependencySets
                        .Include(s => s.Mappings)
                            .ThenInclude(m => m.SourceUdp)
                        .Include(s => s.Mappings)
                            .ThenInclude(m => m.TargetUdp)
                        .Include(s => s.Relations)
                        .Where(s => s.ConfigId == cfgId)
                        .OrderBy(s => s.Name)
                        .ToList();
                }

                Log($"DependencySetRuntime: Loaded {_sets.Count} set(s), {MappingCount} mapping(s)");

                // Pre-fetch external table data for TABLE source mappings
                PreFetchTableData(config);

                // Build cascade dependency map (parent UDP -> affected child UDPs)
                BuildCascadeMap();

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
                // 1. UDP -> UDP mappings: source UDP's linked table column -> target UDP value
                var udpToUdpMappings = set.Mappings?
                    .Where(m => m.SourceType == "UDP" && m.SourceUdp != null
                        && m.SourceUdp.Name.Equals(changedUdpName, StringComparison.OrdinalIgnoreCase)
                        && m.TargetType == "UDP" && m.TargetUdp != null)
                    .OrderBy(m => m.SortOrder)
                    .ToList();

                if (udpToUdpMappings != null && udpToUdpMappings.Count > 0)
                {
                    // Find the TABLE->UDP mapping that populates the source UDP (to find its table)
                    var sourceTableMapping = set.Mappings?.FirstOrDefault(m =>
                        m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                        && m.TargetUdp.Name.Equals(changedUdpName, StringComparison.OrdinalIgnoreCase));

                    if (sourceTableMapping != null)
                    {
                        // Get the table data and find the row matching the selected value
                        var tableData = GetCachedTableData(sourceTableMapping.SourceConnectionId, sourceTableMapping.SourceTable);
                        var matchColumn = sourceTableMapping.SourceColumn;

                        var matchingRow = tableData?.FirstOrDefault(row =>
                            row.ContainsKey(matchColumn) &&
                            row[matchColumn].Equals(newValue, StringComparison.OrdinalIgnoreCase));

                        foreach (var mapping in udpToUdpMappings)
                        {
                            string columnToRead = mapping.SourceColumn; // e.g. "KVKK"
                            string targetValue = "";

                            if (matchingRow != null && !string.IsNullOrEmpty(columnToRead) && matchingRow.ContainsKey(columnToRead))
                            {
                                targetValue = matchingRow[columnToRead];
                            }

                            updates.Add(new DependencyUpdate
                            {
                                UdpName = mapping.TargetUdp.Name,
                                UpdateType = DependencyUpdateType.SetValue,
                                Value = targetValue,
                                SetName = set.Name
                            });

                            Log($"DependencySetRuntime: UDP->UDP: {changedUdpName}='{newValue}' -> {sourceTableMapping.SourceTable}.{columnToRead}='{targetValue}' -> {mapping.TargetUdp.Name}");
                        }
                    }
                }

                // 2. TABLE -> UDP cascade (existing: model-level filter through relations)
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
            // Find relations where this table is the child (TABLE_B)
            var parentRelations = set.Relations?
                .Where(r => r.TableB.Equals(tableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // No parent relation = standalone table, return all rows
            if (parentRelations == null || parentRelations.Count == 0)
                return tableData;

            // This table IS a child in a relation. Parent must be selected, otherwise return empty.
            foreach (var relation in parentRelations)
            {
                var parentMapping = set.Mappings?.FirstOrDefault(m =>
                    m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                    && m.SourceTable != null
                    && m.SourceTable.Equals(relation.TableA, StringComparison.OrdinalIgnoreCase));

                if (parentMapping == null) continue;

                string parentUdpName = parentMapping.TargetUdp.Name;

                // Parent UDP not set -> return empty (dependent UDPs stay empty until parent is selected)
                if (currentUdpValues == null || !currentUdpValues.TryGetValue(parentUdpName, out var parentUdpValue)
                    || string.IsNullOrEmpty(parentUdpValue))
                {
                    return new List<Dictionary<string, string>>();
                }

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
        /// For UDP->UDP mapping: get the value to set on the target UDP
        /// by looking up the source UDP's selected value in its linked table.
        /// </summary>
        public string GetUdpToUdpValue(string sourceUdpName, string sourceValue, string targetUdpName)
        {
            if (!_isLoaded || _sets == null) return null;

            foreach (var set in _sets)
            {
                // Find the UDP->UDP mapping
                var udpMapping = set.Mappings?.FirstOrDefault(m =>
                    m.SourceType == "UDP" && m.SourceUdp != null
                    && m.SourceUdp.Name.Equals(sourceUdpName, StringComparison.OrdinalIgnoreCase)
                    && m.TargetType == "UDP" && m.TargetUdp != null
                    && m.TargetUdp.Name.Equals(targetUdpName, StringComparison.OrdinalIgnoreCase));

                if (udpMapping == null) continue;

                // Find the TABLE->UDP mapping that feeds the source UDP (to get its table)
                var tableMapping = set.Mappings?.FirstOrDefault(m =>
                    m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                    && m.TargetUdp.Name.Equals(sourceUdpName, StringComparison.OrdinalIgnoreCase));

                if (tableMapping == null) continue;

                var tableData = GetCachedTableData(tableMapping.SourceConnectionId, tableMapping.SourceTable);
                if (tableData == null) continue;

                // Find the row where the source column matches the selected value
                var matchingRow = tableData.FirstOrDefault(row =>
                    row.ContainsKey(tableMapping.SourceColumn) &&
                    row[tableMapping.SourceColumn].Equals(sourceValue, StringComparison.OrdinalIgnoreCase));

                if (matchingRow == null) return "";

                // Read the target column value from that row
                string columnToRead = udpMapping.SourceColumn;
                if (!string.IsNullOrEmpty(columnToRead) && matchingRow.ContainsKey(columnToRead))
                    return matchingRow[columnToRead];

                return "";
            }

            return null;
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

        /// <summary>
        /// Get cascade targets affected by a change in the given parent UDP.
        /// </summary>
        /// <summary>
        /// Get all UDP names that are cascade sources (have dependent UDPs).
        /// Used to efficiently check only relevant column UDPs for changes.
        /// </summary>
        public List<string> GetAllCascadeSourceUdps()
        {
            if (_cascadeMap == null) return new List<string>();
            return _cascadeMap.Keys.ToList();
        }

        /// <summary>
        /// Check for unset parent UDPs that have dependents.
        /// Returns list of messages like "Select 'ASSET' on Model to populate PROCESS, DATA_CATEGORY"
        /// </summary>
        public List<string> GetMissingParentWarnings(Dictionary<string, string> modelUdpValues)
        {
            var warnings = new List<string>();
            if (_cascadeMap == null) return warnings;

            foreach (var kvp in _cascadeMap)
            {
                string parentUdpName = kvp.Key;
                var children = kvp.Value;

                // Check if parent is set
                bool parentSet = modelUdpValues != null
                    && modelUdpValues.TryGetValue(parentUdpName, out var val)
                    && !string.IsNullOrEmpty(val);

                if (!parentSet)
                {
                    // Determine parent level
                    var parentDef = UdpDefinitionService.Instance.GetAll()
                        .FirstOrDefault(d => d.Name.Equals(parentUdpName, StringComparison.OrdinalIgnoreCase));
                    string level = (parentDef?.ObjectType ?? "Model") switch
                    {
                        "Model" => "Model properties",
                        "Table" => "Table UDP",
                        "Column" => "Column UDP",
                        _ => "Model properties"
                    };

                    var childNames = string.Join(", ", children.Select(c => c.UdpName));
                    warnings.Add($"'{parentUdpName}' ({level}) is not set. Dependent UDPs will be empty: {childNames}");
                }
            }

            return warnings;
        }

        public List<CascadeTarget> GetAffectedUdps(string parentUdpName)
        {
            if (_cascadeMap != null && _cascadeMap.TryGetValue(parentUdpName, out var affected))
                return affected;
            return new List<CascadeTarget>();
        }

        /// <summary>
        /// Build cascade map at load time. Each entry contains the child UDP name
        /// and its PT path suffix for fast metamodel lookup.
        /// </summary>
        private void BuildCascadeMap()
        {
            _cascadeMap = new Dictionary<string, List<CascadeTarget>>(StringComparer.OrdinalIgnoreCase);

            foreach (var set in _sets)
            {
                if (set.Mappings == null || set.Relations == null) continue;

                foreach (var parentMapping in set.Mappings.Where(m =>
                    m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null))
                {
                    string parentTable = parentMapping.SourceTable;
                    string parentUdpName = parentMapping.TargetUdp.Name;

                    var childRelations = set.Relations
                        .Where(r => r.TableA.Equals(parentTable, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (childRelations.Count == 0) continue;

                    foreach (var relation in childRelations)
                    {
                        var childMappings = set.Mappings
                            .Where(m => m.SourceType == "TABLE" && m.TargetType == "UDP" && m.TargetUdp != null
                                && m.SourceTable != null
                                && m.SourceTable.Equals(relation.TableB, StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        foreach (var childMapping in childMappings)
                        {
                            if (!_cascadeMap.ContainsKey(parentUdpName))
                                _cascadeMap[parentUdpName] = new List<CascadeTarget>();

                            string childUdpName = childMapping.TargetUdp.Name;
                            if (_cascadeMap[parentUdpName].Any(t => t.UdpName.Equals(childUdpName, StringComparison.OrdinalIgnoreCase)))
                                continue;

                            // Resolve object type -> erwin owner class
                            var childDef = UdpDefinitionService.Instance.GetAll()
                                .FirstOrDefault(d => d.Name.Equals(childUdpName, StringComparison.OrdinalIgnoreCase));
                            string ownerClass = (childDef?.ObjectType ?? "Table") switch
                            {
                                "Table" => "Entity",
                                "Column" => "Attribute",
                                "Model" => "Model",
                                _ => "Entity"
                            };

                            _cascadeMap[parentUdpName].Add(new CascadeTarget
                            {
                                UdpName = childUdpName,
                                PtPathSuffix = $".Physical.{childUdpName}",
                                OwnerClass = ownerClass
                            });
                        }
                    }
                }

                // UDP -> UDP mappings: source UDP changes -> target UDP gets value from table column
                foreach (var udpMapping in set.Mappings.Where(m =>
                    m.SourceType == "UDP" && m.SourceUdp != null
                    && m.TargetType == "UDP" && m.TargetUdp != null))
                {
                    string srcUdpName = udpMapping.SourceUdp.Name;
                    string tgtUdpName = udpMapping.TargetUdp.Name;

                    if (!_cascadeMap.ContainsKey(srcUdpName))
                        _cascadeMap[srcUdpName] = new List<CascadeTarget>();

                    if (_cascadeMap[srcUdpName].Any(t => t.UdpName.Equals(tgtUdpName, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var childDef = UdpDefinitionService.Instance.GetAll()
                        .FirstOrDefault(d => d.Name.Equals(tgtUdpName, StringComparison.OrdinalIgnoreCase));
                    string ownerClass = (childDef?.ObjectType ?? "Column") switch
                    {
                        "Table" => "Entity",
                        "Column" => "Attribute",
                        "Model" => "Model",
                        _ => "Attribute"
                    };

                    _cascadeMap[srcUdpName].Add(new CascadeTarget
                    {
                        UdpName = tgtUdpName,
                        PtPathSuffix = $".Physical.{tgtUdpName}",
                        OwnerClass = ownerClass,
                        IsValueMapping = true // UDP->UDP: set value, not list options
                    });
                }
            }

            foreach (var kvp in _cascadeMap)
            {
                var names = string.Join(", ", kvp.Value.Select(t => $"{t.UdpName}{(t.IsValueMapping ? "(val)" : "(list)")}"));
                Log($"DependencySetRuntime: Cascade map: '{kvp.Key}' -> [{names}]");
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
        public string Value { get; set; }
        public List<string> ListOptions { get; set; }
        public string SetName { get; set; }
    }

    /// <summary>
    /// Pre-computed cascade target: which child UDP to update and how to find it in metamodel.
    /// </summary>
    public class CascadeTarget
    {
        public string UdpName { get; set; }       // e.g. "PROCESS"
        public string PtPathSuffix { get; set; }  // e.g. ".Physical.PROCESS"
        public string OwnerClass { get; set; }    // e.g. "Entity"
        public bool IsValueMapping { get; set; }  // true=UDP->UDP (set value), false=TABLE->UDP (set list)
    }
}
