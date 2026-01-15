using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading and caching DOMAIN_DEF entries from database.
    /// Uses DatabaseService for multi-database support (MSSQL, PostgreSQL, Oracle).
    /// </summary>
    public class DomainDefService
    {
        private static DomainDefService _instance;
        private static readonly object _lock = new object();

        private readonly Dictionary<string, DomainDefEntry> _domainDefs;
        private bool _isLoaded;
        private string _lastError;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static DomainDefService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DomainDefService();
                        }
                    }
                }
                return _instance;
            }
        }

        private DomainDefService()
        {
            _domainDefs = new Dictionary<string, DomainDefEntry>(StringComparer.OrdinalIgnoreCase);
            _isLoaded = false;
        }

        /// <summary>
        /// Load domain definitions from database using DatabaseService configuration
        /// </summary>
        public bool LoadDomainDefs()
        {
            try
            {
                _domainDefs.Clear();
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Database not configured. Please configure the database connection in ErwinAdmin.";
                    _isLoaded = false;
                    System.Diagnostics.Debug.WriteLine($"DomainDefService: {_lastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetDomainDefQuery(dbType);

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
                                string name = reader["NAME"]?.ToString()?.Trim() ?? "";
                                string description = reader["DESCRIPTION"]?.ToString()?.Trim() ?? "";
                                string regexp = reader["REGEXP"]?.ToString()?.Trim() ?? "";
                                string dataType = reader["DATA_TYPE"]?.ToString()?.Trim() ?? "";

                                if (!string.IsNullOrEmpty(name) && !_domainDefs.ContainsKey(name))
                                {
                                    _domainDefs[name] = new DomainDefEntry
                                    {
                                        Id = id,
                                        Name = name,
                                        Description = description,
                                        Regexp = regexp,
                                        DataType = dataType
                                    };
                                }
                            }
                        }
                    }
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"DomainDefService: Loaded {_domainDefs.Count} entries");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"DomainDefService.LoadDomainDefs error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the appropriate SQL query for the database type
        /// </summary>
        private string GetDomainDefQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return "SELECT \"ID\", \"NAME\", \"DESCRIPTION\", \"REGEXP\", \"DATA_TYPE\" FROM \"DOMAIN_DEF\"";

                case "ORACLE":
                    return "SELECT ID, NAME, DESCRIPTION, REGEXP, DATA_TYPE FROM DOMAIN_DEF";

                case "MSSQL":
                default:
                    return "SELECT [ID], [NAME], [DESCRIPTION], [REGEXP], [DATA_TYPE] FROM [dbo].[DOMAIN_DEF]";
            }
        }

        /// <summary>
        /// Check if a domain name exists in the cache
        /// </summary>
        public bool Exists(string domainName)
        {
            if (string.IsNullOrEmpty(domainName)) return false;
            return _domainDefs.ContainsKey(domainName);
        }

        /// <summary>
        /// Get domain definition entry by name (case-insensitive)
        /// </summary>
        public DomainDefEntry GetByName(string domainName)
        {
            if (string.IsNullOrEmpty(domainName)) return null;
            _domainDefs.TryGetValue(domainName, out var entry);
            return entry;
        }

        /// <summary>
        /// Get all domain definition entries
        /// </summary>
        public IEnumerable<DomainDefEntry> GetAll()
        {
            return _domainDefs.Values;
        }

        /// <summary>
        /// Get all domain names as comma-separated string (for UDP List values)
        /// First item is placeholder to force user selection
        /// </summary>
        public string GetNamesAsCommaSeparated()
        {
            if (!_isLoaded || _domainDefs.Count == 0)
                return "(SELECT)";

            var names = new List<string> { "(SELECT)" };
            names.AddRange(_domainDefs.Keys);
            return string.Join(",", names);
        }

        /// <summary>
        /// Validate a column name against a domain's regexp pattern
        /// </summary>
        /// <param name="domainName">The domain name to look up</param>
        /// <param name="columnPhysicalName">The column physical name to validate</param>
        /// <returns>Validation result with match status and domain entry</returns>
        public DomainValidationResult ValidateColumnName(string domainName, string columnPhysicalName)
        {
            if (string.IsNullOrEmpty(domainName))
            {
                return DomainValidationResult.Invalid("Domain name is empty", null);
            }

            if (string.IsNullOrEmpty(columnPhysicalName))
            {
                return DomainValidationResult.Invalid("Column name is empty", null);
            }

            // Try exact match first
            var domainEntry = GetByName(domainName);

            // If not found, try normalizing: "ADRES TERM" -> "ADRES_TERM"
            if (domainEntry == null)
            {
                string normalizedName = domainName.Replace(" ", "_");
                domainEntry = GetByName(normalizedName);

                if (domainEntry != null)
                {
                    System.Diagnostics.Debug.WriteLine($"DomainDefService: Matched '{domainName}' as '{normalizedName}'");
                }
            }

            if (domainEntry == null)
            {
                return DomainValidationResult.Invalid($"Domain '{domainName}' not found in cache", null);
            }

            if (string.IsNullOrEmpty(domainEntry.Regexp))
            {
                // No regexp defined - validation passes by default
                return DomainValidationResult.Valid(domainEntry);
            }

            try
            {
                var regex = new Regex(domainEntry.Regexp, RegexOptions.IgnoreCase);
                bool isMatch = regex.IsMatch(columnPhysicalName);

                if (isMatch)
                {
                    return DomainValidationResult.Valid(domainEntry);
                }
                else
                {
                    return DomainValidationResult.Invalid(
                        $"Column name '{columnPhysicalName}' does not match domain pattern '{domainEntry.Regexp}'",
                        domainEntry);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DomainDefService.ValidateColumnName regex error: {ex.Message}");
                return DomainValidationResult.Invalid($"Invalid regexp pattern: {ex.Message}", domainEntry);
            }
        }

        public bool IsLoaded => _isLoaded;
        public int Count => _domainDefs.Count;
        public string LastError => _lastError;

        /// <summary>
        /// Force reload
        /// </summary>
        public void Reload()
        {
            LoadDomainDefs();
        }
    }

    /// <summary>
    /// Represents a DOMAIN_DEF entry
    /// </summary>
    public class DomainDefEntry
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Regexp { get; set; }
        public string DataType { get; set; }
    }

    /// <summary>
    /// Result of domain validation
    /// </summary>
    public class DomainValidationResult
    {
        public bool IsValid { get; set; }
        public string Message { get; set; }
        public DomainDefEntry DomainEntry { get; set; }

        public static DomainValidationResult Valid(DomainDefEntry entry) =>
            new DomainValidationResult { IsValid = true, DomainEntry = entry };

        public static DomainValidationResult Invalid(string message, DomainDefEntry entry) =>
            new DomainValidationResult { IsValid = false, Message = message, DomainEntry = entry };
    }
}
