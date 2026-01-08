using System;
using System.Collections.Generic;
using System.Data.SqlClient;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and querying the GLOSSARY table from database
    /// </summary>
    public class GlossaryService
    {
        private static GlossaryService _instance;
        private static readonly object _lock = new object();

        private readonly Dictionary<string, GlossaryEntry> _glossary;
        private bool _isLoaded;
        private string _lastError;

        // Connection string
        private const string ConnectionString = "Server=localhost,1433;Database=Fiba;User Id=sa;Password=Elite12345;Connection Timeout=5;";

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
        /// Load glossary from database
        /// </summary>
        public bool LoadGlossary()
        {
            try
            {
                _glossary.Clear();
                _lastError = null;

                using (var connection = new SqlConnection(ConnectionString))
                {
                    connection.Open();

                    string query = "SELECT [NAME], [DATA_TYPE], [OWNER] FROM [dbo].[GLOSSARY]";

                    using (var command = new SqlCommand(query, connection))
                    {
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
                System.Diagnostics.Debug.WriteLine($"GlossaryService: Loaded {_glossary.Count} entries");
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
