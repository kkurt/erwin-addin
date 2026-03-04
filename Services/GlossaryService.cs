using System;
using System.Collections.Generic;
using System.Data.Common;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and querying the GLOSSARY table from database.
    /// Uses GlossaryConnectionService to get connection info from GLOSSARY_CONNECTION_DEF table.
    ///
    /// GLOSSARY Table Schema:
    ///   ID            int           (PK)
    ///   NAME          varchar(50)   - Column name
    ///   DATA_TYPE     varchar(50)   - Physical data type
    ///   OWNER         varchar(50)   - Owner
    ///   DB_TYPE       varchar(50)   - Database type
    ///   KVKK          bit           - KVKK flag
    ///   PCIDSS        bit           - PCI-DSS flag
    ///   CLASSIFICATION varchar(50)   - Classification
    ///   COMMENT       varchar(500)  - Column comment/definition
    /// </summary>
    public class GlossaryService
    {
        private static GlossaryService _instance;
        private static readonly object _lock = new object();

        private readonly Dictionary<string, GlossaryEntry> _glossary;
        private bool _isLoaded;
        private string _lastError;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static GlossaryService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new GlossaryService();
                        }
                    }
                }
                return _instance;
            }
        }

        private GlossaryService()
        {
            _glossary = new Dictionary<string, GlossaryEntry>(StringComparer.OrdinalIgnoreCase);
            _isLoaded = false;
        }

        /// <summary>
        /// Load glossary from database using connection info from GLOSSARY_CONNECTION_DEF table
        /// </summary>
        public bool LoadGlossary()
        {
            try
            {
                _glossary.Clear();
                _lastError = null;

                // First check if repo database is configured
                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Repository database not configured. Please configure in ErwinAdmin.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"GlossaryService: {_lastError}");
                    return false;
                }

                // Load glossary connection definition from GLOSSARY_CONNECTION_DEF table
                var glossaryConnService = GlossaryConnectionService.Instance;
                if (!glossaryConnService.IsLoaded)
                {
                    glossaryConnService.LoadConnectionDef();
                }

                if (!glossaryConnService.IsLoaded || glossaryConnService.ConnectionDef == null)
                {
                    _lastError = glossaryConnService.LastError ?? "Glossary connection definition not found.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"GlossaryService: {_lastError}");
                    return false;
                }

                // Get glossary connection string from CONNECTION_DEF (ID=4)
                string connectionString = glossaryConnService.GetGlossaryConnectionString();
                string dbType = glossaryConnService.GetGlossaryDbType();

                using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
                {
                    connection.Open();

                    // 3-tier fallback: full (with COMMENT) → extended (without COMMENT) → basic
                    bool hasExtendedColumns = false;
                    bool hasCommentColumn = false;
                    string query;

                    // Tier 1: Try full query with COMMENT
                    query = GetGlossaryQuery(dbType, "full");
                    if (TestQuery(connection, query))
                    {
                        hasExtendedColumns = true;
                        hasCommentColumn = true;
                    }
                    else
                    {
                        // Tier 2: Try extended without COMMENT
                        query = GetGlossaryQuery(dbType, "extended");
                        if (TestQuery(connection, query))
                        {
                            hasExtendedColumns = true;
                            System.Diagnostics.Debug.WriteLine("GlossaryService: COMMENT column not available");
                        }
                        else
                        {
                            // Tier 3: Basic only
                            query = GetGlossaryQuery(dbType, "basic");
                            System.Diagnostics.Debug.WriteLine("GlossaryService: Extended columns not available, using basic query");
                        }
                    }

                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = query;
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                string name = reader["NAME"]?.ToString()?.Trim() ?? "";
                                string dataType = reader["DATA_TYPE"]?.ToString()?.Trim() ?? "";
                                string owner = reader["OWNER"]?.ToString()?.Trim() ?? "";

                                string dbTypeVal = "";
                                bool kvkk = false;
                                bool pcidss = false;
                                string classification = "";
                                string comment = "";

                                if (hasExtendedColumns)
                                {
                                    dbTypeVal = SafeReadString(reader, "DB_TYPE");
                                    kvkk = SafeReadBool(reader, "KVKK");
                                    pcidss = SafeReadBool(reader, "PCIDSS");
                                    classification = SafeReadString(reader, "CLASSIFICATION");
                                }

                                if (hasCommentColumn)
                                {
                                    comment = SafeReadString(reader, "COMMENT");
                                }

                                if (!string.IsNullOrEmpty(name) && !_glossary.ContainsKey(name))
                                {
                                    _glossary[name] = new GlossaryEntry
                                    {
                                        Name = name,
                                        DataType = dataType,
                                        Owner = owner,
                                        DbType = dbTypeVal,
                                        Kvkk = kvkk,
                                        Pcidss = pcidss,
                                        Classification = classification,
                                        Comment = comment
                                    };
                                }
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"GlossaryService: Loaded {_glossary.Count} entries from {glossaryConnService.ConnectionDef.Host}/{glossaryConnService.ConnectionDef.DbSchema}");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"GlossaryService.LoadGlossary error: {ex.Message}");
                return false;
            }
        }

        private string SafeReadString(DbDataReader reader, string columnName)
        {
            try
            {
                var value = reader[columnName];
                return value == null || value == DBNull.Value ? "" : value.ToString().Trim();
            }
            catch { return ""; }
        }

        private bool SafeReadBool(DbDataReader reader, string columnName)
        {
            try
            {
                var value = reader[columnName];
                if (value == null || value == DBNull.Value) return false;
                if (value is bool b) return b;
                return Convert.ToBoolean(value);
            }
            catch { return false; }
        }

        private bool TestQuery(DbConnection connection, string query)
        {
            try
            {
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = query;
                    using (cmd.ExecuteReader()) { }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Gets the appropriate SQL query for the database type.
        /// tier: "full" (all columns + COMMENT), "extended" (without COMMENT), "basic" (NAME, DATA_TYPE, OWNER)
        /// </summary>
        private string GetGlossaryQuery(string dbType, string tier)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    if (tier == "full")
                        return "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\", \"DB_TYPE\", \"KVKK\", \"PCIDSS\", \"CLASSIFICATION\", \"COMMENT\" FROM \"GLOSSARY\"";
                    if (tier == "extended")
                        return "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\", \"DB_TYPE\", \"KVKK\", \"PCIDSS\", \"CLASSIFICATION\" FROM \"GLOSSARY\"";
                    return "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\" FROM \"GLOSSARY\"";

                case "ORACLE":
                    if (tier == "full")
                        return "SELECT NAME, DATA_TYPE, OWNER, DB_TYPE, KVKK, PCIDSS, CLASSIFICATION, \"COMMENT\" FROM GLOSSARY";
                    if (tier == "extended")
                        return "SELECT NAME, DATA_TYPE, OWNER, DB_TYPE, KVKK, PCIDSS, CLASSIFICATION FROM GLOSSARY";
                    return "SELECT NAME, DATA_TYPE, OWNER FROM GLOSSARY";

                case "MSSQL":
                default:
                    if (tier == "full")
                        return "SELECT [NAME], [DATA_TYPE], [OWNER], [DB_TYPE], [KVKK], [PCIDSS], [CLASSIFICATION], [COMMENT] FROM [dbo].[GLOSSARY]";
                    if (tier == "extended")
                        return "SELECT [NAME], [DATA_TYPE], [OWNER], [DB_TYPE], [KVKK], [PCIDSS], [CLASSIFICATION] FROM [dbo].[GLOSSARY]";
                    return "SELECT [NAME], [DATA_TYPE], [OWNER] FROM [dbo].[GLOSSARY]";
            }
        }

        /// <summary>
        /// Check if a column name exists in the glossary
        /// </summary>
        public bool Exists(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return false;
            return _glossary.ContainsKey(columnName);
        }

        /// <summary>
        /// Get glossary entry for a column name
        /// </summary>
        public GlossaryEntry GetEntry(string columnName)
        {
            if (string.IsNullOrEmpty(columnName)) return null;
            _glossary.TryGetValue(columnName, out var entry);
            return entry;
        }

        /// <summary>
        /// Get all glossary entries
        /// </summary>
        public IEnumerable<GlossaryEntry> GetAll()
        {
            return _glossary.Values;
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _glossary.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Force reload
        /// </summary>
        public void Reload()
        {
            LoadGlossary();
        }

        /// <summary>
        /// Get current connection string from GlossaryConnectionService
        /// </summary>
        public string ConnectionString => GlossaryConnectionService.Instance.GetGlossaryConnectionString();

        /// <summary>
        /// Checks if the database is configured
        /// </summary>
        public bool IsConfigured => DatabaseService.Instance.IsConfigured;

        /// <summary>
        /// Gets the glossary connection definition
        /// </summary>
        public GlossaryConnectionDef ConnectionDef => GlossaryConnectionService.Instance.ConnectionDef;
    }

    /// <summary>
    /// Represents a glossary entry matching the GLOSSARY table schema
    /// </summary>
    public class GlossaryEntry
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Owner { get; set; }
        public string DbType { get; set; }
        public bool Kvkk { get; set; }
        public bool Pcidss { get; set; }
        public string Classification { get; set; }
        public string Comment { get; set; }
    }
}
