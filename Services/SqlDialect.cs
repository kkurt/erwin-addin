namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Small per-dialect SQL helpers shared by the admin-DB access services.
    /// Today it covers only the ADO parameter-name prefix: Oracle binds with
    /// ':name', SQL Server and PostgreSQL with '@name'.
    /// </summary>
    internal static class SqlDialect
    {
        /// <summary>
        /// The prefixed ADO parameter name for the given DBMS: <c>:name</c> for
        /// Oracle, <c>@name</c> otherwise. The comparison is case-sensitive against
        /// "ORACLE" to reproduce every existing call site exactly - DatabaseService.GetDbType()
        /// already upper-cases the value, so callers pass an upper-cased dbType (or an
        /// explicit ToUpperInvariant()), and this preserves their prior behaviour byte-for-byte.
        /// </summary>
        public static string Param(string dbType, string name)
            => dbType == "ORACLE" ? ":" + name : "@" + name;
    }
}
