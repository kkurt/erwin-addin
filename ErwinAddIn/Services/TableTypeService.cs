using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and caching TABLE_TYPE entries from database
    /// </summary>
    public class TableTypeService
    {
        private static TableTypeService _instance;
        private static readonly object _lock = new object();

        private readonly List<TableTypeEntry> _tableTypes;
        private bool _isLoaded;
        private string _lastError;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static TableTypeService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new TableTypeService();
                        }
                    }
                }
                return _instance;
            }
        }

        private TableTypeService()
        {
            _tableTypes = new List<TableTypeEntry>();
            _isLoaded = false;
        }

        /// <summary>
        /// Load table types from database using GlossaryService connection string
        /// </summary>
        public bool LoadTableTypes()
        {
            try
            {
                _tableTypes.Clear();
                _lastError = null;

                string connectionString = GlossaryService.Instance.ConnectionString;

                using (var connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    string query = "SELECT [ID], [NAME], [AFFIX], [NAME_EXTENSION_LOCATION] FROM [dbo].[TABLE_TYPE]";

                    using (var command = new SqlCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int id = reader.GetInt32(0);
                                string name = reader["NAME"]?.ToString()?.Trim() ?? "";
                                string affix = reader["AFFIX"]?.ToString()?.Trim() ?? "";
                                string nameExtLocation = reader["NAME_EXTENSION_LOCATION"]?.ToString()?.Trim() ?? "";

                                if (!string.IsNullOrEmpty(name))
                                {
                                    _tableTypes.Add(new TableTypeEntry
                                    {
                                        Id = id,
                                        Name = name,
                                        Affix = affix,
                                        NameExtensionLocation = nameExtLocation
                                    });
                                }
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"TableTypeService: Loaded {_tableTypes.Count} entries");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"TableTypeService.LoadTableTypes error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get all table type names as comma-separated string (for UDP List values)
        /// First item is placeholder to force user selection
        /// </summary>
        public string GetNamesAsCommaSeparated()
        {
            if (!_isLoaded || _tableTypes.Count == 0)
                return "(SELECT)";

            var names = new List<string> { "(SELECT)" };
            names.AddRange(_tableTypes.Select(t => t.Name));
            return string.Join(",", names);
        }

        /// <summary>
        /// Get table type entry by name
        /// </summary>
        public TableTypeEntry GetByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return _tableTypes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get all table type entries
        /// </summary>
        public IEnumerable<TableTypeEntry> GetAll()
        {
            return _tableTypes;
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _tableTypes.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Force reload
        /// </summary>
        public void Reload()
        {
            LoadTableTypes();
        }

        /// <summary>
        /// Remove all known affixes (prefix/suffix) from a table name
        /// This is used when changing TABLE_TYPE to first clean the name
        /// </summary>
        public string RemoveAllAffixes(string tableName)
        {
            if (string.IsNullOrEmpty(tableName) || !_isLoaded)
                return tableName;

            string result = tableName;

            // Try to remove affix from each known TABLE_TYPE
            foreach (var tableType in _tableTypes)
            {
                if (string.IsNullOrEmpty(tableType.Affix))
                    continue;

                string newResult = tableType.RemoveAffix(result);
                if (newResult != result)
                {
                    // Found and removed an affix
                    return newResult;
                }
            }

            return result;
        }

        /// <summary>
        /// Find which TABLE_TYPE's affix is currently applied to a table name
        /// </summary>
        public TableTypeEntry FindAppliedAffix(string tableName)
        {
            if (string.IsNullOrEmpty(tableName) || !_isLoaded)
                return null;

            foreach (var tableType in _tableTypes)
            {
                if (tableType.HasAffixApplied(tableName))
                {
                    return tableType;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Represents a TABLE_TYPE entry
    /// </summary>
    public class TableTypeEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Affix { get; set; }
        public string NameExtensionLocation { get; set; }

        /// <summary>
        /// Applies the affix to a table name based on NAME_EXTENSION_LOCATION
        /// </summary>
        /// <param name="tableName">Original table name</param>
        /// <returns>Table name with affix applied</returns>
        public string ApplyAffix(string tableName)
        {
            if (string.IsNullOrEmpty(Affix) || string.IsNullOrEmpty(tableName))
                return tableName;

            // Check if affix is already applied
            if (tableName.StartsWith(Affix + "_", StringComparison.OrdinalIgnoreCase) ||
                tableName.EndsWith("_" + Affix, StringComparison.OrdinalIgnoreCase))
            {
                return tableName;
            }

            // Apply based on NAME_EXTENSION_LOCATION
            // "PREFIX" or "P" = add to beginning
            // "SUFFIX" or "S" = add to end
            // Default is PREFIX
            string location = NameExtensionLocation?.ToUpperInvariant() ?? "PREFIX";

            if (location == "SUFFIX" || location == "S" || location == "END")
            {
                return $"{tableName}_{Affix}";
            }
            else
            {
                // PREFIX is default
                return $"{Affix}_{tableName}";
            }
        }

        /// <summary>
        /// Checks if a table name already has this affix applied
        /// </summary>
        public bool HasAffixApplied(string tableName)
        {
            if (string.IsNullOrEmpty(Affix) || string.IsNullOrEmpty(tableName))
                return false;

            string location = NameExtensionLocation?.ToUpperInvariant() ?? "PREFIX";

            if (location == "SUFFIX" || location == "S" || location == "END")
            {
                return tableName.EndsWith("_" + Affix, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                return tableName.StartsWith(Affix + "_", StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Removes this entry's affix from a table name
        /// </summary>
        public string RemoveAffix(string tableName)
        {
            if (string.IsNullOrEmpty(Affix) || string.IsNullOrEmpty(tableName))
                return tableName;

            string location = NameExtensionLocation?.ToUpperInvariant() ?? "PREFIX";

            if (location == "SUFFIX" || location == "S" || location == "END")
            {
                // Remove suffix
                string suffix = "_" + Affix;
                if (tableName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return tableName.Substring(0, tableName.Length - suffix.Length);
                }
            }
            else
            {
                // Remove prefix
                string prefix = Affix + "_";
                if (tableName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return tableName.Substring(prefix.Length);
                }
            }

            return tableName;
        }
    }
}
