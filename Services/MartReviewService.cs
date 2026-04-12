using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Service for comparing the currently open model (in-memory via SCAPI)
    /// with its last saved version in the Mart (ErwinRepository database).
    /// </summary>
    public class MartReviewService
    {
        private readonly dynamic _session;
        private readonly dynamic _scapi;

        public event Action<string> OnLog;

        // ErwinRepository Mart DB object type constants
        private const int ENTITY_TYPE = 1075838979;
        private const int ATTRIBUTE_TYPE = 1075838981;

        public MartReviewService(dynamic session, dynamic scapi)
        {
            _session = session;
            _scapi = scapi;
        }

        /// <summary>
        /// Run review: compare current model (SCAPI) vs Mart (DB).
        /// Returns list of differences.
        /// </summary>
        public List<ReviewDifference> RunReview()
        {
            var differences = new List<ReviewDifference>();

            try
            {
                // 1. Get Mart DB connection from CONNECTION_DEF
                string martConnStr = GetMartDbConnectionString();
                if (string.IsNullOrEmpty(martConnStr))
                {
                    Log("Review: Mart DB connection not found in CONNECTION_DEF");
                    return differences;
                }

                // 2. Get model name from current PU
                string modelName = GetCurrentModelName();
                if (string.IsNullOrEmpty(modelName))
                {
                    Log("Review: Could not determine current model name");
                    return differences;
                }
                Log($"Review: Model name = '{modelName}'");

                // 3. Find model's CatalogId in Mart DB
                int catalogId = FindCatalogId(martConnStr, modelName);
                if (catalogId == 0)
                {
                    Log($"Review: Model '{modelName}' not found in Mart database");
                    return differences;
                }
                Log($"Review: Mart CatalogId = {catalogId}");

                // 4. Get local snapshot (from SCAPI session)
                var localSnapshot = GetLocalSnapshot();
                Log($"Review: Local snapshot: {localSnapshot.Count} entities");

                // 5. Get Mart snapshot (from ErwinRepository DB)
                var martSnapshot = GetMartSnapshot(martConnStr, catalogId);
                Log($"Review: Mart snapshot: {martSnapshot.Count} entities");

                // 6. Compare
                differences = CompareSnapshots(localSnapshot, martSnapshot);
                Log($"Review: Found {differences.Count} difference(s)");
            }
            catch (Exception ex)
            {
                Log($"Review error: {ex.Message}");
            }

            return differences;
        }

        /// <summary>
        /// Check if the current model is from Mart (has a matching catalog entry).
        /// </summary>
        public bool IsModelFromMart()
        {
            try
            {
                string martConnStr = GetMartDbConnectionString();
                if (string.IsNullOrEmpty(martConnStr)) return false;

                string modelName = GetCurrentModelName();
                if (string.IsNullOrEmpty(modelName)) return false;

                int catalogId = FindCatalogId(martConnStr, modelName);
                return catalogId > 0;
            }
            catch
            {
                return false;
            }
        }

        #region Local Snapshot (SCAPI)

        private Dictionary<string, LocalEntity> GetLocalSnapshot()
        {
            var entities = new Dictionary<string, LocalEntity>(StringComparer.OrdinalIgnoreCase);

            try
            {
                dynamic modelObjects = _session.ModelObjects;
                dynamic root = modelObjects.Root;
                if (root == null) return entities;

                dynamic allEntities = modelObjects.Collect(root, "Entity");
                if (allEntities == null) return entities;

                foreach (dynamic entity in allEntities)
                {
                    if (entity == null) continue;
                    try
                    {
                        string name = entity.Name ?? "";
                        string physName = "";
                        try { physName = entity.Properties("Physical_Name").Value?.ToString() ?? ""; } catch { }
                        if (string.IsNullOrEmpty(physName)) physName = name;

                        var localEntity = new LocalEntity
                        {
                            Name = name,
                            PhysicalName = physName
                        };

                        // Get attributes
                        try
                        {
                            dynamic attrs = modelObjects.Collect(entity, "Attribute");
                            if (attrs != null)
                            {
                                foreach (dynamic attr in attrs)
                                {
                                    if (attr == null) continue;
                                    try
                                    {
                                        string attrName = attr.Name ?? "";
                                        string attrPhys = "";
                                        try { attrPhys = attr.Properties("Physical_Name").Value?.ToString() ?? ""; } catch { }
                                        if (string.IsNullOrEmpty(attrPhys)) attrPhys = attrName;

                                        string dataType = "";
                                        try { dataType = attr.Properties("Physical_Data_Type").Value?.ToString() ?? ""; } catch { }

                                        bool nullable = true;
                                        try { nullable = attr.Properties("Null_Option_Type").Value?.ToString() != "1"; } catch { }

                                        localEntity.Attributes[attrPhys] = new LocalAttribute
                                        {
                                            Name = attrName,
                                            PhysicalName = attrPhys,
                                            DataType = dataType,
                                            IsNullable = nullable
                                        };
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }

                        entities[physName] = localEntity;
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log($"Review: Local snapshot error: {ex.Message}");
            }

            return entities;
        }

        #endregion

        #region Mart Snapshot (DB)

        private Dictionary<string, MartEntity> GetMartSnapshot(string connStr, int catalogId)
        {
            var entities = new Dictionary<string, MartEntity>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (var conn = DatabaseService.Instance.CreateConnection("MSSQL", connStr))
                {
                    conn.Open();

                    // Get entities
                    string entityQuery = @"
                        SELECT DISTINCT O_Id, CONVERT(VARCHAR(500), O_Name) AS O_Name
                        FROM m9Object
                        WHERE C_Id = @CatalogId AND O_Type = @EntityType AND O_EndVersion = 999999999
                        ORDER BY O_Name";

                    var entityMap = new Dictionary<int, MartEntity>();

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = entityQuery;
                        AddParam(cmd, "@CatalogId", catalogId);
                        AddParam(cmd, "@EntityType", ENTITY_TYPE);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int oId = Convert.ToInt32(reader["O_Id"]);
                                string name = reader["O_Name"]?.ToString()?.Trim() ?? "";

                                if (string.IsNullOrEmpty(name)) continue;

                                if (!entities.ContainsKey(name))
                                {
                                    var entity = new MartEntity { Name = name, PhysicalName = name };
                                    entities[name] = entity;
                                    entityMap[oId] = entity;
                                }
                                else
                                {
                                    entityMap[oId] = entities[name];
                                }
                            }
                        }
                    }

                    // Get attributes
                    string attrQuery = @"
                        SELECT DISTINCT O_Id, O_ParentId, CONVERT(VARCHAR(500), O_Name) AS O_Name
                        FROM m9Object
                        WHERE C_Id = @CatalogId AND O_Type = @AttrType AND O_EndVersion = 999999999
                        ORDER BY O_ParentId, O_Name";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = attrQuery;
                        AddParam(cmd, "@CatalogId", catalogId);
                        AddParam(cmd, "@AttrType", ATTRIBUTE_TYPE);

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                int parentId = reader["O_ParentId"] == DBNull.Value ? 0 : Convert.ToInt32(reader["O_ParentId"]);
                                string name = reader["O_Name"]?.ToString()?.Trim() ?? "";

                                if (entityMap.TryGetValue(parentId, out var parentEntity) && !string.IsNullOrEmpty(name))
                                {
                                    if (!parentEntity.Attributes.ContainsKey(name))
                                    {
                                        parentEntity.Attributes[name] = new MartAttribute
                                        {
                                            Name = name,
                                            PhysicalName = name
                                        };
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Review: Mart snapshot error: {ex.Message}");
            }

            return entities;
        }

        private int FindCatalogId(string connStr, string modelName)
        {
            try
            {
                using (var conn = DatabaseService.Instance.CreateConnection("MSSQL", connStr))
                {
                    conn.Open();

                    // Search by model name in catalog
                    string query = @"
                        SELECT TOP 1 C_Id
                        FROM m9Catalog
                        WHERE C_Name = @ModelName
                        ORDER BY C_Id DESC";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = query;
                        AddParam(cmd, "@ModelName", modelName);

                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                            return Convert.ToInt32(result);
                    }

                    // Fallback: search by name pattern (with path)
                    string likeQuery = @"
                        SELECT TOP 1 C_Id
                        FROM m9Catalog
                        WHERE CAST(C_Path AS VARCHAR(1000)) LIKE @Pattern
                        ORDER BY C_Id DESC";

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = likeQuery;
                        AddParam(cmd, "@Pattern", "%" + modelName);

                        var result = cmd.ExecuteScalar();
                        if (result != null && result != DBNull.Value)
                            return Convert.ToInt32(result);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Review: FindCatalogId error: {ex.Message}");
            }

            return 0;
        }

        #endregion

        #region Comparison

        private List<ReviewDifference> CompareSnapshots(
            Dictionary<string, LocalEntity> local,
            Dictionary<string, MartEntity> mart)
        {
            var diffs = new List<ReviewDifference>();

            // Added entities (in local but not in mart)
            foreach (var kvp in local)
            {
                if (!mart.ContainsKey(kvp.Key))
                {
                    diffs.Add(new ReviewDifference
                    {
                        ObjectType = "Entity",
                        ObjectName = kvp.Value.PhysicalName,
                        ChangeType = "Added",
                        Detail = $"{kvp.Value.Attributes.Count} column(s)"
                    });
                }
            }

            // Deleted entities (in mart but not in local)
            foreach (var kvp in mart)
            {
                if (!local.ContainsKey(kvp.Key))
                {
                    diffs.Add(new ReviewDifference
                    {
                        ObjectType = "Entity",
                        ObjectName = kvp.Value.PhysicalName,
                        ChangeType = "Deleted",
                        Detail = $"{kvp.Value.Attributes.Count} column(s)"
                    });
                }
            }

            // Modified entities (in both, compare attributes)
            foreach (var kvp in local)
            {
                if (mart.TryGetValue(kvp.Key, out var martEntity))
                {
                    var localEntity = kvp.Value;

                    // Added columns
                    foreach (var attr in localEntity.Attributes)
                    {
                        if (!martEntity.Attributes.ContainsKey(attr.Key))
                        {
                            diffs.Add(new ReviewDifference
                            {
                                ObjectType = "Column",
                                ObjectName = $"{localEntity.PhysicalName}.{attr.Value.PhysicalName}",
                                ChangeType = "Added",
                                Detail = attr.Value.DataType
                            });
                        }
                    }

                    // Deleted columns
                    foreach (var attr in martEntity.Attributes)
                    {
                        if (!localEntity.Attributes.ContainsKey(attr.Key))
                        {
                            diffs.Add(new ReviewDifference
                            {
                                ObjectType = "Column",
                                ObjectName = $"{martEntity.PhysicalName}.{attr.Value.PhysicalName}",
                                ChangeType = "Deleted",
                                Detail = ""
                            });
                        }
                    }
                }
            }

            return diffs;
        }

        #endregion

        #region Helpers

        private string GetCurrentModelName()
        {
            try
            {
                dynamic pu = _scapi.PersistenceUnits.Item(0);
                return pu.Name ?? "";
            }
            catch { return ""; }
        }

        private string GetMartDbConnectionString()
        {
            try
            {
                if (!DatabaseService.Instance.IsConfigured) return null;

                var config = new RegistryBootstrapService().GetConfig();
                if (config == null) return null;

                string repoDbType = config.DbType;

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();

                    // Find ErwinRepository connection (DB_TYPE contains 'MSSQL' and DB_SCHEMA is ErwinRepository-like)
                    // or NAME = 'Mart Server' / 'ErwinRepository'
                    string query = repoDbType?.ToUpper() switch
                    {
                        "POSTGRESQL" => @"SELECT ""ID"", ""DB_TYPE"", ""HOST"", ""PORT"", ""DB_SCHEMA"", ""USERNAME"", ""PASSWORD""
                                         FROM ""CONNECTION_DEF"" WHERE ""DB_SCHEMA"" LIKE '%Repository%' OR ""DB_SCHEMA"" LIKE '%Erwin%'
                                         ORDER BY ""ID"" LIMIT 1",
                        _ => @"SELECT TOP 1 [ID], [DB_TYPE], [HOST], [PORT], [DB_SCHEMA], [USERNAME], [PASSWORD]
                               FROM [dbo].[CONNECTION_DEF] WHERE [DB_SCHEMA] LIKE '%Repository%' OR [DB_SCHEMA] LIKE '%Erwin%'
                               ORDER BY [ID]"
                    };

                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            string dbType = reader["DB_TYPE"]?.ToString()?.Trim() ?? "MSSQL";
                            string host = reader["HOST"]?.ToString()?.Trim() ?? "";
                            string port = reader["PORT"]?.ToString()?.Trim() ?? "";
                            string schema = reader["DB_SCHEMA"]?.ToString()?.Trim() ?? "";
                            string encUser = reader["USERNAME"]?.ToString()?.Trim() ?? "";
                            string encPass = reader["PASSWORD"]?.ToString()?.Trim() ?? "";

                            string username = PasswordEncryptionService.Decrypt(encUser);
                            string password = PasswordEncryptionService.Decrypt(encPass);

                            if (string.IsNullOrEmpty(username) || (username.Length > 50 && username == encUser))
                            {
                                username = config.Username;
                                password = config.Password;
                            }

                            string connStr = $"Server={host},{port};Database={schema};User Id={username};Password={password};TrustServerCertificate=True;Connection Timeout=10;";
                            Log($"Review: Mart DB = {host}/{schema}");
                            return connStr;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Review: GetMartDbConnectionString error: {ex.Message}");
            }

            return null;
        }

        private void AddParam(DbCommand cmd, string name, object value)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = name;
            param.Value = value;
            cmd.Parameters.Add(param);
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        #endregion
    }

    #region Models

    public class ReviewDifference
    {
        public string ObjectType { get; set; }    // Entity, Column
        public string ObjectName { get; set; }    // Table name or Table.Column
        public string ChangeType { get; set; }    // Added, Deleted, Modified
        public string Detail { get; set; }        // Extra info (data type, column count)
    }

    public class LocalEntity
    {
        public string Name { get; set; }
        public string PhysicalName { get; set; }
        public Dictionary<string, LocalAttribute> Attributes { get; set; } = new Dictionary<string, LocalAttribute>(StringComparer.OrdinalIgnoreCase);
    }

    public class LocalAttribute
    {
        public string Name { get; set; }
        public string PhysicalName { get; set; }
        public string DataType { get; set; }
        public bool IsNullable { get; set; }
    }

    public class MartEntity
    {
        public string Name { get; set; }
        public string PhysicalName { get; set; }
        public Dictionary<string, MartAttribute> Attributes { get; set; } = new Dictionary<string, MartAttribute>(StringComparer.OrdinalIgnoreCase);
    }

    public class MartAttribute
    {
        public string Name { get; set; }
        public string PhysicalName { get; set; }
    }

    #endregion
}
