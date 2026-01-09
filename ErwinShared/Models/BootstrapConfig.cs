namespace EliteSoft.Erwin.Shared.Models
{
    /// <summary>
    /// Local bootstrap configuration for Repository DB connection.
    /// This is stored locally on each machine (Extension/Admin) as JSON.
    /// </summary>
    public class BootstrapConfig
    {
        /// <summary>
        /// Database type: MSSQL, PostgreSQL, Oracle
        /// </summary>
        public string DbType { get; set; } = "MSSQL";

        /// <summary>
        /// Database server host
        /// </summary>
        public string Host { get; set; } = "localhost";

        /// <summary>
        /// Database server port
        /// </summary>
        public string Port { get; set; } = "1433";

        /// <summary>
        /// Database name / Schema
        /// </summary>
        public string Database { get; set; } = "";

        /// <summary>
        /// Database username
        /// </summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Database password (encrypted)
        /// </summary>
        public string Password { get; set; } = "";

        /// <summary>
        /// Whether the bootstrap config has been configured
        /// </summary>
        public bool IsConfigured { get; set; } = false;

        /// <summary>
        /// Builds the connection string based on DbType
        /// </summary>
        public string GetConnectionString()
        {
            switch (DbType?.ToUpper())
            {
                case "MSSQL":
                    return $"Server={Host},{Port};Database={Database};User Id={Username};Password={Password};TrustServerCertificate=True;";

                case "POSTGRESQL":
                    return $"Host={Host};Port={Port};Database={Database};Username={Username};Password={Password};";

                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={Port}))(CONNECT_DATA=(SERVICE_NAME={Database})));User Id={Username};Password={Password};";

                default:
                    return $"Server={Host},{Port};Database={Database};User Id={Username};Password={Password};TrustServerCertificate=True;";
            }
        }
    }
}
