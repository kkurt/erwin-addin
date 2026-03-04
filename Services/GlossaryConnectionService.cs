using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading Glossary connection definition from CONNECTION_DEF table (ID=4) in repository database.
    /// This defines where the GLOSSARY table resides (may be different DB type/server than repo DB).
    /// </summary>
    public class GlossaryConnectionService
    {
        private static GlossaryConnectionService _instance;
        private static readonly object _lock = new object();

        private GlossaryConnectionDef _connectionDef;
        private bool _isLoaded;
        private string _lastError;

        public static GlossaryConnectionService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new GlossaryConnectionService();
                        }
                    }
                }
                return _instance;
            }
        }

        private GlossaryConnectionService()
        {
            _isLoaded = false;
        }

        /// <summary>
        /// Loads the glossary connection definition from CONNECTION_DEF table (ID=4)
        /// </summary>
        public bool LoadConnectionDef()
        {
            try
            {
                _connectionDef = null;
                _lastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    _lastError = "Repository database not configured. Please configure in ErwinAdmin.";
                    _isLoaded = false;
                    return false;
                }

                string repoDbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(repoDbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                var encryptedUsername = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                                var encryptedPassword = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                                _connectionDef = new GlossaryConnectionDef
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    DbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL",
                                    Host = reader["HOST"]?.ToString()?.Trim() ?? "",
                                    Port = reader["PORT"]?.ToString()?.Trim() ?? "",
                                    DbSchema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "",
                                    Username = PasswordEncryptionService.Decrypt(encryptedUsername) ?? encryptedUsername,
                                    Password = PasswordEncryptionService.Decrypt(encryptedPassword) ?? encryptedPassword
                                };
                            }
                        }
                    }
                }

                if (_connectionDef == null)
                {
                    _lastError = "No glossary connection definition found in CONNECTION_DEF table (ID=4).";
                    _isLoaded = false;
                    return false;
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"GlossaryConnectionService: Loaded connection def - DbType: {_connectionDef.DbType}, Host: {_connectionDef.Host}, DB: {_connectionDef.DbSchema}");
                return true;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _isLoaded = false;
                System.Diagnostics.Debug.WriteLine($"GlossaryConnectionService.LoadConnectionDef error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets the appropriate SQL query for the repository database type.
        /// Queries CONNECTION_DEF table with ID=4 (Glossary).
        /// </summary>
        private string GetQuery(string repoDbType)
        {
            switch (repoDbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return "SELECT \"ID\", \"DB_TYPE\", \"HOST\", \"PORT\", \"DB_SCHEMA\", \"USERNAME\", \"PASSWORD\" FROM \"connection_def\" WHERE \"ID\" = 4";

                case "ORACLE":
                    return "SELECT ID, DB_TYPE, HOST, PORT, DB_SCHEMA, USERNAME, PASSWORD FROM CONNECTION_DEF WHERE ID = 4";

                case "MSSQL":
                default:
                    return "SELECT [ID], [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD] FROM [dbo].[CONNECTION_DEF] WHERE [ID] = 4";
            }
        }

        /// <summary>
        /// Builds connection string for the glossary database using the DB_TYPE from CONNECTION_DEF
        /// </summary>
        public string GetGlossaryConnectionString()
        {
            if (_connectionDef == null)
                return null;

            // Use the glossary's own DB_TYPE (not the repo DB type)
            switch (_connectionDef.DbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $"Host={_connectionDef.Host};Port={_connectionDef.Port};Database={_connectionDef.DbSchema};Username={_connectionDef.Username};Password={_connectionDef.Password};";

                case "ORACLE":
                    return $"Data Source={_connectionDef.Host}:{_connectionDef.Port}/{_connectionDef.DbSchema};User Id={_connectionDef.Username};Password={_connectionDef.Password};";

                case "MSSQL":
                default:
                    return $"Server={_connectionDef.Host},{_connectionDef.Port};Database={_connectionDef.DbSchema};User Id={_connectionDef.Username};Password={_connectionDef.Password};TrustServerCertificate=True;Connection Timeout=5;";
            }
        }

        /// <summary>
        /// Gets the glossary database type (from CONNECTION_DEF.DB_TYPE, not from repo DB)
        /// </summary>
        public string GetGlossaryDbType()
        {
            return _connectionDef?.DbType ?? "MSSQL";
        }

        public GlossaryConnectionDef ConnectionDef => _connectionDef;
        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;

        public void Reload()
        {
            LoadConnectionDef();
        }

        public void ClearCache()
        {
            _connectionDef = null;
            _isLoaded = false;
        }
    }

    /// <summary>
    /// Represents a glossary connection definition from CONNECTION_DEF table (ID=4)
    /// </summary>
    public class GlossaryConnectionDef
    {
        public int Id { get; set; }
        public string DbType { get; set; }
        public string Host { get; set; }
        public string Port { get; set; }
        public string DbSchema { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
