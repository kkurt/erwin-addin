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
    ///   CLASSIFICATON varchar(50)   - Classification
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

                // Get glossary connection string from GLOSSARY_CONNECTION_DEF
                string connectionString = glossaryConnService.GetGlossaryConnectionString();
                string dbType = DatabaseService.Instance.GetDbType();

                using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
                {
                    connection.Open();

                    // Try full query first, fall back to basic if extended columns don't exist
                    bool hasExtendedColumns = true;
                    string query = GetGlossaryQuery(dbType, true);

                    try
                    {
                        using (var testCmd = connection.CreateCommand())
                        {
                            testCmd.CommandText = query;
                            using (testCmd.ExecuteReader()) { }
                        }
                    }
                    catch
                    {
                        hasExtendedColumns = false;
                        query = GetGlossaryQuery(dbType, false);
                        System.Diagnostics.Debug.WriteLine("GlossaryService: Extended columns not available, using basic query");
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

                                if (hasExtendedColumns)
                                {
                                    dbTypeVal = SafeReadString(reader, "DB_TYPE");
                                    kvkk = SafeReadBool(reader, "KVKK");
                                    pcidss = SafeReadBool(reader, "PCIDSS");
                                    classification = SafeReadString(reader, "CLASSIFICATON");
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
                                        Classification = classification
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

        /// <summary>
        /// Gets the appropriate SQL query for the database type
        /// </summary>
        private string GetGlossaryQuery(string dbType, bool includeExtended)
        {
            // Base columns: NAME, DATA_TYPE, OWNER (always exist)
            // Extended columns: DB_TYPE, KVKK, PCIDSS, CLASSIFICATON
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return includeExtended
                        ? "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\", \"DB_TYPE\", \"KVKK\", \"PCIDSS\", \"CLASSIFICATON\" FROM \"GLOSSARY\""
                        : "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\" FROM \"GLOSSARY\"";

                case "ORACLE":
                    return includeExtended
                        ? "SELECT NAME, DATA_TYPE, OWNER, DB_TYPE, KVKK, PCIDSS, CLASSIFICATON FROM GLOSSARY"
                        : "SELECT NAME, DATA_TYPE, OWNER FROM GLOSSARY";

                case "MSSQL":
                default:
                    return includeExtended
                        ? "SELECT [NAME], [DATA_TYPE], [OWNER], [DB_TYPE], [KVKK], [PCIDSS], [CLASSIFICATON] FROM [dbo].[GLOSSARY]"
                        : "SELECT [NAME], [DATA_TYPE], [OWNER] FROM [dbo].[GLOSSARY]";
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
    }
}
