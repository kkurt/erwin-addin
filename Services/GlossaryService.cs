using System;
using System.Collections.Generic;
using System.Data.Common;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and querying the GLOSSARY table from database.
    /// Uses GlossaryConnectionService to get connection info from GLOSSARY_CONNECTION_DEF table.
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
                string query = GetGlossaryQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection(dbType, connectionString))
                {
                    connection.Open();

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

                                if (!string.IsNullOrEmpty(name) && !_glossary.ContainsKey(name))
                                {
                                    _glossary[name] = new GlossaryEntry
                                    {
                                        Name = name,
                                        DataType = dataType,
                                        Owner = owner
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

        /// <summary>
        /// Gets the appropriate SQL query for the database type
        /// </summary>
        private string GetGlossaryQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return "SELECT \"NAME\", \"DATA_TYPE\", \"OWNER\" FROM \"GLOSSARY\"";

                case "ORACLE":
                    return "SELECT NAME, DATA_TYPE, OWNER FROM GLOSSARY";

                case "MSSQL":
                default:
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
        /// Gets the current database type
        /// </summary>
        public string DbType => DatabaseService.Instance.GetDbType();

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
    /// Represents a glossary entry
    /// </summary>
    public class GlossaryEntry
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Owner { get; set; }
    }
}
