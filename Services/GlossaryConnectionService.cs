using System;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for loading Glossary connection definition from GLOSSARY_CONNECTION_DEF table in repository database.
    /// This defines where the GLOSSARY table resides (may be different from repo DB).
    /// </summary>
    public class GlossaryConnectionService
    {
        private static GlossaryConnectionService _instance;
        private static readonly object _lock = new object();

        private GlossaryConnectionDef _connectionDef;
        private bool _isLoaded;
        private string _lastError;

        /// <summary>
        /// Singleton instance
        /// </summary>
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
        /// Loads the glossary connection definition from GLOSSARY_CONNECTION_DEF table
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

                string dbType = DatabaseService.Instance.GetDbType();
                string query = GetQuery(dbType);

                using (var connection = DatabaseService.Instance.CreateConnection())
                {
                    connection.Open();

                    using (var command = DatabaseService.Instance.CreateCommand(query, connection))
                    {
                        using (var reader = command.ExecuteReader())
                        {
                            if (reader.Read())
                            {
                                _connectionDef = new GlossaryConnectionDef
                                {
                                    Id = Convert.ToInt32(reader["ID"]),
                                    Host = reader["HOST"]?.ToString()?.Trim() ?? "",
                                    Port = reader["PORT"]?.ToString()?.Trim() ?? "",
                                    DbSchema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "",
                                    Username = reader["USERNAME"]?.ToString()?.Trim() ?? "",
                                    Password = reader["PASSWORD"]?.ToString()?.Trim() ?? ""
                                };
                            }
                        }
                    }
                }

                if (_connectionDef == null)
                {
                    _lastError = "No glossary connection definition found in GLOSSARY_CONNECTION_DEF table.";
                    _isLoaded = false;
                    return false;
                }

                _isLoaded = true;
                System.Diagnostics.Debug.WriteLine($"GlossaryConnectionService: Loaded connection def - Host: {_connectionDef.Host}, DB: {_connectionDef.DbSchema}");
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
        /// Gets the appropriate SQL query for the database type
        /// </summary>
        private string GetQuery(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return "SELECT \"ID\", \"HOST\", \"PORT\", \"DB_SCHEMA\", \"USERNAME\", \"PASSWORD\" FROM \"GLOSSARY_CONNECTION_DEF\" LIMIT 1";

                case "ORACLE":
                    return "SELECT ID, HOST, PORT, DB_SCHEMA, USERNAME, PASSWORD FROM GLOSSARY_CONNECTION_DEF WHERE ROWNUM = 1";

                case "MSSQL":
                default:
                    return "SELECT TOP 1 [ID], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD] FROM [dbo].[GLOSSARY_CONNECTION_DEF]";
            }
        }

        /// <summary>
        /// Builds connection string for the glossary database
        /// </summary>
        public string GetGlossaryConnectionString()
        {
            if (_connectionDef == null)
                return null;

            // Glossary connection is always MSSQL based on the schema (using standard SQL Server connection)
            string dbType = DatabaseService.Instance.GetDbType();

            switch (dbType?.ToUpper())
            {
                case "POSTGRESQL":
                    return $"Host={_connectionDef.Host};Port={_connectionDef.Port};Database={_connectionDef.DbSchema};Username={_connectionDef.Username};Password={_connectionDef.Password};";

                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={_connectionDef.Host})(PORT={_connectionDef.Port}))(CONNECT_DATA=(SERVICE_NAME={_connectionDef.DbSchema})));User Id={_connectionDef.Username};Password={_connectionDef.Password};";

                case "MSSQL":
                default:
                    return $"Server={_connectionDef.Host},{_connectionDef.Port};Database={_connectionDef.DbSchema};User Id={_connectionDef.Username};Password={_connectionDef.Password};TrustServerCertificate=True;Connection Timeout=5;";
            }
        }

        /// <summary>
        /// Gets the loaded connection definition
        /// </summary>
        public GlossaryConnectionDef ConnectionDef => _connectionDef;

        public bool IsLoaded => _isLoaded;
        public string LastError => _lastError;

        /// <summary>
        /// Force reload
        /// </summary>
        public void Reload()
        {
            LoadConnectionDef();
        }

        /// <summary>
        /// Clear cached data
        /// </summary>
        public void ClearCache()
        {
            _connectionDef = null;
            _isLoaded = false;
        }
    }

    /// <summary>
    /// Represents a glossary connection definition
    /// </summary>
    public class GlossaryConnectionDef
    {
        public int Id { get; set; }
        public string Host { get; set; }
        public string Port { get; set; }
        public string DbSchema { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
}
