namespace EliteSoft.Erwin.Shared.Models
{
    /// <summary>
    /// Supported database types
    /// </summary>
    public static class DbTypes
    {
        public const string MSSQL = "MSSQL";
        public const string PostgreSQL = "PostgreSQL";
        public const string Oracle = "Oracle";

        public static readonly string[] All = { MSSQL, PostgreSQL, Oracle };

        public static string GetDefaultPort(string dbType)
        {
            switch (dbType?.ToUpper())
            {
                case "MSSQL": return "1433";
                case "POSTGRESQL": return "5432";
                case "ORACLE": return "1521";
                default: return "1433";
            }
        }
    }
}
