using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and caching PREDEFINED_COLUMN entries from database.
    /// These columns are automatically added to tables when a TABLE_TYPE is selected.
    /// </summary>
    public class PredefinedColumnService
    {
        private static PredefinedColumnService _instance;
        private static readonly object _lock = new object();

        private readonly List<PredefinedColumn> _columns;
        private bool _isLoaded;
        private string _lastError;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static PredefinedColumnService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new PredefinedColumnService();
                        }
                    }
                }
                return _instance;
            }
        }

        private PredefinedColumnService()
        {
            _columns = new List<PredefinedColumn>();
            _isLoaded = false;
        }

        /// <summary>
        /// Load predefined columns from database
        /// </summary>
        public bool LoadPredefinedColumns()
        {
            try
            {
                _columns.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured.";
                    _isLoaded = false;
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int id = Convert.ToInt32(reader["ID"]);
                                int tableTypeId = Convert.ToInt32(reader["TABLE_TYPE_ID"]);
                                string name = reader["NAME"]?.ToString()?.Trim() ?? "";
                                string dataType = reader["TYPE"]?.ToString()?.Trim() ?? "";
                                bool nullable = Convert.ToBoolean(reader["NULLABLE"]);

                                if (!string.IsNullOrEmpty(name))
                                {
                                    _columns.Add(new PredefinedColumn
                                    {
                                        Id = id,
                                        TableTypeId = tableTypeId,
                                        Name = name,
                                        DataType = dataType,
                                        Nullable = nullable
                                    });
                                }
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"PredefinedColumnService: Loaded {_columns.Count} entries");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"PredefinedColumnService.LoadPredefinedColumns error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the appropriate SQL query for the database type
        /// </summary>
        private string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return "SELECT \"ID\", \"TABLE_TYPE_ID\", \"NAME\", \"TYPE\", \"NULLABLE\" FROM \"PREDEFINED_COLUMN\"";

                case "ORACLE":
                    return "SELECT ID, TABLE_TYPE_ID, NAME, TYPE, NULLABLE FROM PREDEFINED_COLUMN";

                case "MSSQL":
                default:
                    return "SELECT [ID], [TABLE_TYPE_ID], [NAME], [TYPE], [NULLABLE] FROM [dbo].[PREDEFINED_COLUMN]";
            }
        }

        /// <summary>
        /// Get predefined columns for a specific TABLE_TYPE ID
        /// </summary>
        public IEnumerable<PredefinedColumn> GetByTableTypeId(int tableTypeId)
        {
            if (!_isLoaded)
                return Enumerable.Empty<PredefinedColumn>();

            return _columns.Where(c => c.TableTypeId == tableTypeId);
        }

        /// <summary>
        /// Get all predefined columns
        /// </summary>
        public IEnumerable<PredefinedColumn> GetAll()
        {
            return _columns;
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _columns.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Force reload
        /// </summary>
        public void Reload()
        {
            LoadPredefinedColumns();
        }
    }

    /// <summary>
    /// Represents a PREDEFINED_COLUMN entry
    /// </summary>
    public class PredefinedColumn
    {
        public int Id { get; set; }
        public int TableTypeId { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public bool Nullable { get; set; }
    }
}
