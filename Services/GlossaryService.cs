using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Glossary service with dynamic mapping support.
    /// Reads glossary config from DG_TABLE_MAPPING (MAPPING_CODE='GLOSSARY') + DG_TABLE_MAPPING_COLUMN.
    /// Cache: Dictionary&lt;matchValue, Dictionary&lt;targetUdp, value&gt;&gt;
    /// </summary>
    public class GlossaryService
    {
        private static GlossaryService _instance;
        private static readonly object _lock = new object();

        // Dynamic cache: matchValue → { targetUdp → value }
        private Dictionary<string, Dictionary<string, string>> _glossaryCache;
        private bool _isLoaded;
        private string _lastError;

        // Mapping metadata (for logging/debugging)
        private string _matchSourceColumn;
        private List<(string sourceCol, string targetType, string targetField)> _valueMappings;
        private string _tableName;

        public event Action<string> OnLog;

        public static GlossaryService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new GlossaryService();
                    }
                }
                return _instance;
            }
        }

        private GlossaryService()
        {
            _glossaryCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            _valueMappings = new List<(string, string, string)>();
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _glossaryCache.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Load glossary using DG_TABLE_MAPPING config.
        /// </summary>
        public bool LoadGlossary()
        {
            try
            {
                _glossaryCache.Clear();
                _lastError = null;
                _matchSourceColumn = null;
                _valueMappings.Clear();
                _tableName = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Repository database not configured.";
                    _isLoaded = false;
                    Log($"GlossaryService: {_lastError}");
                    return false;
                }

                string repoDbType = DatabaseService.Instance.GetDbType();

                // Step 1: Read DG_TABLE_MAPPING (MAPPING_CODE='GLOSSARY')
                int? mappingId = null;
                int? connectionDefId = null;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();

                    // Try DG_TABLE_MAPPING first (new dynamic mapping)
                    try
                    {
                        // Use corporate effective project IDs if available
                        var corpContext = CorporateContextService.Instance;
                        bool hasCorporateFilter = corpContext.IsInitialized && corpContext.EffectiveModelIds.Count > 0;
                        string effectiveIds = hasCorporateFilter
                            ? string.Join(",", corpContext.EffectiveModelIds)
                            : null;

                        string mappingQuery = GetMappingQuery(repoDbType, hasCorporateFilter, effectiveIds);
                        using (var cmd = DatabaseService.Instance.CreateCommand(mappingQuery, conn))
                        {

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    mappingId = Convert.ToInt32(reader["ID"]);
                                    connectionDefId = reader["CONNECTION_DEF_ID"] == DBNull.Value ? (int?)null : Convert.ToInt32(reader["CONNECTION_DEF_ID"]);
                                    _tableName = reader["TABLE_NAME"]?.ToString()?.Trim() ?? "";
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"GlossaryService: DG_TABLE_MAPPING not available ({ex.Message}), trying legacy CONNECTION_DEF...");
                    }

                    if (mappingId == null)
                    {
                        _lastError = "Glossary not configured. Please configure GLOSSARY mapping in Admin > Data Governance.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    Log($"GlossaryService: Found mapping ID={mappingId}, table='{_tableName}', connDefId={connectionDefId}");

                    // Step 2: Read DG_TABLE_MAPPING_COLUMN
                    string colQuery = GetMappingColumnQuery(repoDbType);
                    using (var cmd2 = DatabaseService.Instance.CreateCommand(colQuery, conn))
                    {
                        var param = cmd2.CreateParameter();
                        param.ParameterName = repoDbType == "ORACLE" ? ":mappingId" : "@mappingId";
                        param.Value = mappingId.Value;
                        cmd2.Parameters.Add(param);

                        using (var reader = cmd2.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string sourceCol = reader["SOURCE_COLUMN"]?.ToString()?.Trim() ?? "";
                                string targetField = reader["TARGET_FIELD"]?.ToString()?.Trim() ?? "";
                                string targetType = "";
                                try { targetType = reader["TARGET_TYPE"]?.ToString()?.Trim() ?? ""; }
                                catch { targetType = ""; } // Column may not exist in older schemas

                                if (string.IsNullOrEmpty(sourceCol)) continue;

                                if (targetField == "_MATCH_")
                                    _matchSourceColumn = sourceCol;
                                else if (!string.IsNullOrEmpty(targetField))
                                    _valueMappings.Add((sourceCol, targetType, targetField));
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(_matchSourceColumn))
                    {
                        _lastError = "No _MATCH_ column defined in DG_TABLE_MAPPING_COLUMN.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    Log($"GlossaryService: Match column='{_matchSourceColumn}', {_valueMappings.Count} value mapping(s): [{string.Join(", ", _valueMappings.Select(m => $"{m.sourceCol}→{m.targetType}:{m.targetField}"))}]");

                    // Step 3: Read CONNECTION_DEF for glossary DB connection
                    if (connectionDefId == null)
                    {
                        _lastError = "No CONNECTION_DEF_ID in GLOSSARY mapping.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    string glossaryConnStr = null;
                    string glossaryDbType = null;

                    string connQuery = GetConnectionDefQuery(repoDbType);
                    using (var cmd3 = DatabaseService.Instance.CreateCommand(connQuery, conn))
                    {
                        var param = cmd3.CreateParameter();
                        param.ParameterName = repoDbType == "ORACLE" ? ":connId" : "@connId";
                        param.Value = connectionDefId.Value;
                        cmd3.Parameters.Add(param);

                        using (var reader = cmd3.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                glossaryDbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL";
                                string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                                string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                                string dbSchema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "";
                                string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                                string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                                string username = PasswordEncryptionService.Decrypt(encUser);
                                string password = PasswordEncryptionService.Decrypt(encPass);

                                // If DPAPI decrypt failed (credentials encrypted by different user),
                                // fall back to Bootstrap credentials (encrypted by install script for this user)
                                bool decryptFailed = string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser);
                                if (decryptFailed)
                                {
                                    Log("GlossaryService: CONNECTION_DEF credentials not decryptable, using Bootstrap credentials");
                                    var bootstrapConfig = DatabaseService.Instance.GetConfig();
                                    if (bootstrapConfig != null)
                                    {
                                        username = bootstrapConfig.Username;
                                        password = bootstrapConfig.Password;
                                    }
                                }

                                glossaryConnStr = BuildConnectionString(glossaryDbType, host, port, dbSchema, username, password);
                                Log($"GlossaryService: Connection = {glossaryDbType}, {host}/{dbSchema}");
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(glossaryConnStr))
                    {
                        _lastError = $"CONNECTION_DEF ID={connectionDefId} not found or invalid.";
                        _isLoaded = false;
                        Log($"GlossaryService: {_lastError}");
                        return false;
                    }

                    // Step 4: Load glossary data from external DB
                    LoadGlossaryData(glossaryDbType, glossaryConnStr);
                }

                _isLoaded = true;
                Log($"GlossaryService: Loaded {_glossaryCache.Count} entries (table='{_tableName}', match='{_matchSourceColumn}')");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                Log($"GlossaryService.LoadGlossary error: {ex.Message}");
                return false;
            }
        }

        private void LoadGlossaryData(string dbType, string connectionString)
        {
            // Build dynamic SELECT with only needed columns
            var allCols = new List<string> { _matchSourceColumn };
            allCols.AddRange(_valueMappings.Select(m => m.sourceCol));
            var distinctCols = allCols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            string selectCols = string.Join(", ", distinctCols.Select(c => QuoteColumn(dbType, c)));
            string fromTable = QuoteTable(dbType, _tableName);
            string query = $"SELECT {selectCols} FROM {fromTable}";

            using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
            {
                connection.Open();

                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = query;
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string matchValue = "";
                            try { matchValue = reader[_matchSourceColumn]?.ToString()?.Trim() ?? ""; }
                            catch (Exception ex) { Log($"GlossaryService: Match column read error: {ex.Message}"); continue; }

                            if (string.IsNullOrEmpty(matchValue)) continue;

                            var udpValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var (sourceCol, targetType, targetField) in _valueMappings)
                            {
                                try
                                {
                                    string val = reader[sourceCol]?.ToString()?.Trim() ?? "";
                                    udpValues[targetField] = val;
                                }
                                catch (Exception ex)
                                {
                                    Log($"GlossaryService: Column '{sourceCol}' read error: {ex.Message}");
                                    udpValues[targetField] = "";
                                }
                            }

                            if (!_glossaryCache.ContainsKey(matchValue))
                                _glossaryCache[matchValue] = udpValues;
                        }
                    }
                }
            }
        }

        #region Public API

        /// <summary>
        /// Check if a column name exists in the glossary.
        /// </summary>
        public bool HasEntry(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return false;
            return _glossaryCache.ContainsKey(columnName);
        }

        /// <summary>
        /// Alias for HasEntry (backward compatibility).
        /// </summary>
        public bool Exists(string columnName) => HasEntry(columnName);

        /// <summary>
        /// Get UDP values for a matched column name.
        /// Returns null if not found.
        /// </summary>
        public Dictionary<string, string> GetUdpValues(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return null;
            _glossaryCache.TryGetValue(columnName, out var values);
            return values;
        }

        /// <summary>
        /// Get the target type for a mapping target field (UDP, ERWIN_PROPERTY, DB_PROPERTY).
        /// </summary>
        public string GetTargetType(string targetField)
        {
            var mapping = _valueMappings.FirstOrDefault(m => m.targetField.Equals(targetField, StringComparison.OrdinalIgnoreCase));
            return mapping.targetType ?? "";
        }

        /// <summary>
        /// Reload with last used modelId.
        /// </summary>
        public void Reload()
        {
            LoadGlossary();
        }

        public bool IsConfigured => DatabaseService.Instance.IsConfigured;

        #endregion

        #region SQL Helpers

        private string GetMappingQuery(string dbType, bool hasCorporateFilter, string effectiveIds)
        {
            // effectiveIds is a safe comma-separated list of integer IDs (no SQL injection risk)
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                {
                    string where = hasCorporateFilter && !string.IsNullOrEmpty(effectiveIds)
                        ? $@"""MAPPING_CODE"" = 'GLOSSARY' AND ""MODEL_ID"" IN ({effectiveIds})"
                        : @"""MAPPING_CODE"" = 'GLOSSARY'";
                    return $@"SELECT ""ID"", ""CONNECTION_DEF_ID"", ""TABLE_NAME"" FROM ""DG_TABLE_MAPPING""
                            WHERE {where}
                            ORDER BY ""MODEL_ID"" DESC NULLS LAST LIMIT 1";
                }
                case "ORACLE":
                {
                    string where = hasCorporateFilter && !string.IsNullOrEmpty(effectiveIds)
                        ? $"MAPPING_CODE = 'GLOSSARY' AND MODEL_ID IN ({effectiveIds})"
                        : "MAPPING_CODE = 'GLOSSARY'";
                    return $@"SELECT ID, CONNECTION_DEF_ID, TABLE_NAME FROM DG_TABLE_MAPPING
                            WHERE {where}
                            ORDER BY MODEL_ID DESC NULLS LAST FETCH FIRST 1 ROWS ONLY";
                }
                case "MSSQL":
                default:
                {
                    string where = hasCorporateFilter && !string.IsNullOrEmpty(effectiveIds)
                        ? $"[MAPPING_CODE] = 'GLOSSARY' AND [MODEL_ID] IN ({effectiveIds})"
                        : "[MAPPING_CODE] = 'GLOSSARY'";
                    return $@"SELECT TOP 1 [ID], [CONNECTION_DEF_ID], [TABLE_NAME] FROM [dbo].[DG_TABLE_MAPPING]
                            WHERE {where}
                            ORDER BY [MODEL_ID] DESC";
                }
            }
        }

        private string GetMappingColumnQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""SOURCE_COLUMN"", ""TARGET_TYPE"", ""TARGET_FIELD"" FROM ""DG_TABLE_MAPPING_COLUMN""
                            WHERE ""TABLE_MAPPING_ID"" = @mappingId ORDER BY ""SORT_ORDER""";
                case "ORACLE":
                    return @"SELECT SOURCE_COLUMN, TARGET_TYPE, TARGET_FIELD FROM DG_TABLE_MAPPING_COLUMN
                            WHERE TABLE_MAPPING_ID = :mappingId ORDER BY SORT_ORDER";
                case "MSSQL":
                default:
                    return @"SELECT [SOURCE_COLUMN], [TARGET_TYPE], [TARGET_FIELD] FROM [dbo].[DG_TABLE_MAPPING_COLUMN]
                            WHERE [TABLE_MAPPING_ID] = @mappingId ORDER BY [SORT_ORDER]";
            }
        }

        private string GetConnectionDefQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return @"SELECT ""DB_TYPE"", ""HOST"", ""PORT"", ""DB_SCHEMA"", ""USERNAME"", ""PASSWORD""
                            FROM ""CONNECTION_DEF"" WHERE ""ID"" = @connId";
                case "ORACLE":
                    return @"SELECT DB_TYPE, HOST, PORT, DB_SCHEMA, USERNAME, PASSWORD
                            FROM CONNECTION_DEF WHERE ID = :connId";
                case "MSSQL":
                default:
                    return @"SELECT [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD]
                            FROM [dbo].[CONNECTION_DEF] WHERE [ID] = @connId";
            }
        }

        private string BuildConnectionString(string dbType, string host, string port, string database, string username, string password)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $"Host={host};Port={port};Database={database};Username={username};Password={password};";
                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={host})(PORT={port}))(CONNECT_DATA=(SERVICE_NAME={database})));User Id={username};Password={password};";
                case "MSSQL":
                default:
                    return $"Server={host},{port};Database={database};User Id={username};Password={password};TrustServerCertificate=True;";
            }
        }

        private string QuoteColumn(string dbType, string col)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL": return $"\"{col}\"";
                case "ORACLE": return col;
                case "MSSQL":
                default: return $"[{col}]";
            }
        }

        private string QuoteTable(string dbType, string table)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL": return $"\"{table}\"";
                case "ORACLE": return table;
                case "MSSQL":
                default: return $"[dbo].[{table}]";
            }
        }

        #endregion

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }
    }
}
