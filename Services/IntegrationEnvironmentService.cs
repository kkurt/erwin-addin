#nullable enable

using System;
using System.Collections.Generic;
using System.Data.Common;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// A deployment environment defined by the admin side for a CONFIG
    /// (e.g. Dev / Test / Prod). Read-only contract consumed by the
    /// Integrate tab; the add-in never writes ENVIRONMENT in this iteration.
    /// </summary>
    /// <param name="Id">ENVIRONMENT.ID primary key.</param>
    /// <param name="ConfigId">Owning CONFIG.ID.</param>
    /// <param name="Name">Environment name; also the Mart folder segment used for current-env detection.</param>
    /// <param name="SortOrder">1-based ordering; lower is earlier in the pipeline (Dev before Prod).</param>
    /// <param name="Description">Optional free text; nullable in the schema.</param>
    /// <param name="ColorHex">Optional "#RRGGBB" badge color; nullable in the schema.</param>
    public sealed record IntegrationEnvironment(
        int Id,
        int ConfigId,
        string Name,
        int SortOrder,
        string? Description,
        string? ColorHex);

    /// <summary>
    /// A directed promotion transition FROM one environment TO another, as
    /// defined by the admin side. Adjacency is not required (Dev can jump to
    /// Prod); forward and back are separate rows. Read-only in this iteration.
    /// </summary>
    /// <param name="Id">ENVIRONMENT_RELATION.ID primary key.</param>
    /// <param name="ConfigId">Owning CONFIG.ID.</param>
    /// <param name="FromEnvironmentId">Source ENVIRONMENT.ID.</param>
    /// <param name="ToEnvironmentId">Target ENVIRONMENT.ID.</param>
    /// <param name="RequiresApproval">When true the promotion goes through approval and is not executed directly.</param>
    public sealed record IntegrationRelation(
        int Id,
        int ConfigId,
        int FromEnvironmentId,
        int ToEnvironmentId,
        bool RequiresApproval);

    /// <summary>
    /// Reads the admin-defined Integrate contract (ENVIRONMENT and
    /// ENVIRONMENT_RELATION) for a CONFIG straight from the MetaRepo via raw
    /// ADO.NET, mirroring <see cref="ConfigContextService"/>'s MODEL_CONFIG_MAPPING
    /// read. These tables have no EF entity in the add-in's referenced
    /// MetaRepo assembly, so the dialect-aware ADO path keeps the add-in
    /// decoupled from the admin EF model and from its schema version.
    ///
    /// By design these methods do NOT swallow exceptions: a MetaRepo read
    /// failure must surface to the user (the Integrate tab renders it), never
    /// degrade silently to an empty list.
    /// </summary>
    public static class IntegrationEnvironmentService
    {
        /// <summary>
        /// Returns every environment of <paramref name="configId"/> ordered by
        /// SORT_ORDER (pipeline order). Empty when the config has no environments.
        /// </summary>
        /// <param name="configId">CONFIG.ID whose environments to read.</param>
        public static IReadOnlyList<IntegrationEnvironment> GetEnvironments(int configId)
        {
            string dbType = DatabaseService.Instance.GetDbType();
            string query = GetEnvironmentsQuery(dbType);

            var result = new List<IntegrationEnvironment>();
            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                {
                    AddParam(cmd, dbType, "configId", configId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new IntegrationEnvironment(
                                Id: ToInt(reader["ID"]),
                                ConfigId: ToInt(reader["CONFIG_ID"]),
                                Name: ToStr(reader["NAME"]) ?? string.Empty,
                                SortOrder: ToInt(reader["SORT_ORDER"]),
                                Description: ToStr(reader["DESCRIPTION"]),
                                ColorHex: ToStr(reader["COLOR_HEX"])));
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Returns every promotion transition defined for <paramref name="configId"/>
        /// (the full directed topology). Each row carries its REQUIRES_APPROVAL flag.
        /// The Integrate tab draws all of these and derives the actionable targets
        /// from the current environment by filtering on FROM_ENVIRONMENT_ID in memory.
        /// </summary>
        /// <param name="configId">CONFIG.ID scope.</param>
        public static IReadOnlyList<IntegrationRelation> GetRelations(int configId)
        {
            string dbType = DatabaseService.Instance.GetDbType();
            string query = GetRelationsQuery(dbType);

            var result = new List<IntegrationRelation>();
            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                {
                    AddParam(cmd, dbType, "configId", configId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add(new IntegrationRelation(
                                Id: ToInt(reader["ID"]),
                                ConfigId: ToInt(reader["CONFIG_ID"]),
                                FromEnvironmentId: ToInt(reader["FROM_ENVIRONMENT_ID"]),
                                ToEnvironmentId: ToInt(reader["TO_ENVIRONMENT_ID"]),
                                RequiresApproval: ToBool(reader["REQUIRES_APPROVAL"])));
                        }
                    }
                }
            }
            return result;
        }

        // NAME is a reserved word on SQL Server; bracket every column to match
        // the MODEL_CONFIG_MAPPING idiom and stay safe across the schema.
        private static string GetEnvironmentsQuery(string dbType) => dbType switch
        {
            "POSTGRESQL" => @"SELECT ""ID"",""CONFIG_ID"",""NAME"",""SORT_ORDER"",""DESCRIPTION"",""COLOR_HEX"" FROM ""ENVIRONMENT"" WHERE ""CONFIG_ID"" = @configId ORDER BY ""SORT_ORDER""",
            "ORACLE"     => @"SELECT ID, CONFIG_ID, NAME, SORT_ORDER, DESCRIPTION, COLOR_HEX FROM ENVIRONMENT WHERE CONFIG_ID = :configId ORDER BY SORT_ORDER",
            _            => @"SELECT [ID],[CONFIG_ID],[NAME],[SORT_ORDER],[DESCRIPTION],[COLOR_HEX] FROM [dbo].[ENVIRONMENT] WHERE [CONFIG_ID] = @configId ORDER BY [SORT_ORDER]"
        };

        private static string GetRelationsQuery(string dbType) => dbType switch
        {
            "POSTGRESQL" => @"SELECT ""ID"",""CONFIG_ID"",""FROM_ENVIRONMENT_ID"",""TO_ENVIRONMENT_ID"",""REQUIRES_APPROVAL"" FROM ""ENVIRONMENT_RELATION"" WHERE ""CONFIG_ID"" = @configId",
            "ORACLE"     => @"SELECT ID, CONFIG_ID, FROM_ENVIRONMENT_ID, TO_ENVIRONMENT_ID, REQUIRES_APPROVAL FROM ENVIRONMENT_RELATION WHERE CONFIG_ID = :configId",
            _            => @"SELECT [ID],[CONFIG_ID],[FROM_ENVIRONMENT_ID],[TO_ENVIRONMENT_ID],[REQUIRES_APPROVAL] FROM [dbo].[ENVIRONMENT_RELATION] WHERE [CONFIG_ID] = @configId"
        };

        private static void AddParam(DbCommand cmd, string dbType, string name, object value)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = SqlDialect.Param(dbType, name);
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        private static int ToInt(object value) => Convert.ToInt32(value);

        private static bool ToBool(object value) =>
            value != DBNull.Value && value != null && Convert.ToBoolean(value);

        private static string? ToStr(object value)
        {
            if (value == DBNull.Value || value == null) return null;
            string s = value.ToString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }
    }
}
