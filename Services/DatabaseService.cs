using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;
using Oracle.ManagedDataAccess.Client;
using EliteSoft.MetaAdmin.Shared.Models;
using EliteSoft.MetaAdmin.Shared.Services;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Database service for multi-provider support (MSSQL, PostgreSQL, Oracle).
    /// Reads bootstrap config via <see cref="HklmFirstBootstrapReader"/>: probes
    /// HKLM first (LocalMachine DPAPI), then HKCU (CurrentUser DPAPI). See
    /// docs/INSTALL.md for the load decision tree.
    /// </summary>
    public class DatabaseService
    {
        private static DatabaseService _instance;
        private static readonly object _lock = new object();

        private readonly IBootstrapService _bootstrapService;
        private BootstrapConfig _cachedConfig;

        /// <summary>
        /// Singleton instance
        /// </summary>
        public static DatabaseService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new DatabaseService();
                        }
                    }
                }
                return _instance;
            }
        }

        private DatabaseService()
        {
            // HklmFirstBootstrapReader probes HKLM first (LocalMachine DPAPI),
            // then HKCU (CurrentUser DPAPI). See docs/INSTALL.md for the full
            // decision tree and DPAPI scope rules. We no longer fall back to
            // bootstrap.json - the installer is the single source of truth and
            // it always writes the registry, never a JSON file. (The legacy
            // BootstrapService JSON path migrated to registry years ago via
            // RegistryBootstrapService.MigrateFromJsonIfNeeded; that migration
            // still runs in erwin-admin if a stale bootstrap.json lingers.)
            _bootstrapService = new HklmFirstBootstrapReader();
        }

        /// <summary>
        /// Gets the current bootstrap configuration
        /// </summary>
        public BootstrapConfig GetConfig()
        {
            if (_cachedConfig == null)
            {
                _cachedConfig = _bootstrapService.GetConfig();
            }
            return _cachedConfig;
        }

        /// <summary>
        /// Checks if the bootstrap configuration is valid
        /// </summary>
        public bool IsConfigured => _bootstrapService.IsConfigured();

        /// <summary>
        /// Gets the connection string based on current configuration
        /// </summary>
        public string GetConnectionString()
        {
            var config = GetConfig();
            return config?.GetConnectionString();
        }

        /// <summary>
        /// Gets the database type from configuration
        /// </summary>
        public string GetDbType()
        {
            var config = GetConfig();
            return config?.DbType?.ToUpper() ?? DbTypes.MSSQL;
        }

        /// <summary>
        /// Creates a new database connection based on configured database type
        /// </summary>
        public DbConnection CreateConnection()
        {
            var config = GetConfig();
            if (config == null)
            {
                throw new InvalidOperationException("Database configuration not found. Please configure the database connection in ErwinAdmin.");
            }

            string connectionString = config.GetConnectionString();
            string dbType = config.DbType?.ToUpper() ?? DbTypes.MSSQL;

            return CreateConnection(dbType, connectionString);
        }

        /// <summary>
        /// Creates a new database connection with specified type and connection string
        /// </summary>
        public DbConnection CreateConnection(string dbType, string connectionString)
        {
            switch (dbType?.ToUpper())
            {
                case "MSSQL":
                    return new SqlConnection(connectionString);

                case "POSTGRESQL":
                    return new NpgsqlConnection(connectionString);

                case "ORACLE":
                    return new OracleConnection(connectionString);

                default:
                    return new SqlConnection(connectionString);
            }
        }

        /// <summary>
        /// Creates a database command for the given connection
        /// </summary>
        public DbCommand CreateCommand(string query, DbConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = query;
            return command;
        }

        /// <summary>
        /// Tests the database connection
        /// </summary>
        public (bool Success, string Message) TestConnection()
        {
            try
            {
                using (var connection = CreateConnection())
                {
                    connection.Open();
                    return (true, "Connection successful");
                }
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Clears the cached configuration (forces reload)
        /// </summary>
        public void ClearCache()
        {
            _cachedConfig = null;
            _bootstrapService.ClearCache();
        }

        /// <summary>
        /// Gets the bootstrap service for direct access if needed
        /// </summary>
        public IBootstrapService BootstrapService => _bootstrapService;
    }
}
