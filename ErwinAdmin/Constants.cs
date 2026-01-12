namespace EliteSoft.Erwin.Admin
{
    /// <summary>
    /// Application-wide constants
    /// </summary>
    public static class Constants
    {
        /// <summary>
        /// Project property keys stored in database
        /// </summary>
        public static class ProjectPropertyKeys
        {
            public const string UseExternalGlossary = "USE_EXTERNAL_GLOSSARY";
            public const string UseTableTypes = "USE_TABLE_TYPES";
            public const string UseApprovementMechanism = "USE_APPROVEMENT_MECHANISM";
        }

        /// <summary>
        /// Tree node tag prefixes
        /// </summary>
        public static class NodeTags
        {
            public const string Library = "Library:";
            public const string Category = "Category:";
            public const string Model = "MODEL:";
            public const string Root = "ROOT";
            public const string Placeholder = "PLACEHOLDER";
            public const string Mart = "MART:";
        }

        /// <summary>
        /// Default values
        /// </summary>
        public static class Defaults
        {
            public const string MartPort = "18170";
            public const string MssqlPort = "1433";
            public const string PostgresPort = "5432";
            public const string OraclePort = "1521";
            public const string DefaultHost = "localhost";
        }
    }
}
