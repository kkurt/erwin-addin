using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Determines active corporate and loads effective model IDs.
    /// All Corporates = ID 1 (hardcoded, always present).
    ///
    /// Flow:
    ///   1. DB has exactly 1 non-default corporate? -> auto-select it + All Corporates
    ///   2. Multiple corporates? -> use the one with IS_ACTIVE = true
    ///   3. No active corporate found -> error, extension closes
    /// </summary>
    public class CorporateContextService
    {
        private static CorporateContextService _instance;
        private static readonly object _lock = new object();

        private const int AllCorporatesId = 1;

        public int ActiveCorporateId { get; private set; }
        public string ActiveCorporateName { get; private set; }
        public List<int> EffectiveModelIds { get; private set; }
        public bool IsInitialized { get; private set; }
        public string LastError { get; private set; }

        public event Action<string> OnLog;

        public static CorporateContextService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                            _instance = new CorporateContextService();
                    }
                }
                return _instance;
            }
        }

        private CorporateContextService()
        {
            EffectiveModelIds = new List<int>();
        }

        public bool Initialize()
        {
            try
            {
                IsInitialized = false;
                LastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    LastError = "Database not configured. Please run Admin panel to configure the database connection.";
                    Log($"CorporateContext: {LastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();

                // Step 1: Get all non-default corporates from DB
                var corporates = GetNonDefaultCorporates(dbType);

                if (corporates.Count == 1)
                {
                    // Single corporate -> auto-select
                    ActiveCorporateId = corporates[0].id;
                    ActiveCorporateName = corporates[0].name;
                    Log($"CorporateContext: Auto-detected single corporate -> '{ActiveCorporateName}' (ID={ActiveCorporateId})");
                }
                else if (corporates.Count > 1)
                {
                    // Multiple corporates -> find the active one (IS_ACTIVE = true)
                    var activeCorp = GetActiveCorporate(dbType);
                    if (activeCorp != null)
                    {
                        ActiveCorporateId = activeCorp.Value.id;
                        ActiveCorporateName = activeCorp.Value.name;
                        Log($"CorporateContext: Active corporate from DB -> '{ActiveCorporateName}' (ID={ActiveCorporateId})");
                    }
                    else
                    {
                        string names = string.Join(", ", corporates.Select(c => $"'{c.name}' (ID={c.id})"));
                        LastError = $"Multiple corporates found but none is active: {names}. Please set IS_ACTIVE in Admin panel.";
                        Log($"CorporateContext: {LastError}");
                        return false;
                    }
                }
                else
                {
                    // No non-default corporates. Check if All Corporates (ID=1) exists.
                    string allCorpName = GetCorporateName(dbType, AllCorporatesId);
                    if (allCorpName != null)
                    {
                        ActiveCorporateId = AllCorporatesId;
                        ActiveCorporateName = allCorpName;
                        Log($"CorporateContext: Only All Corporates found -> using '{ActiveCorporateName}' (ID={AllCorporatesId})");
                    }
                    else
                    {
                        LastError = "No corporate found in database. Please configure a corporate in Admin panel.";
                        Log($"CorporateContext: {LastError}");
                        return false;
                    }
                }

                // Step 3: Load effective model IDs
                if (!LoadEffectiveModelIds(dbType))
                {
                    LastError = $"No models found for corporate '{ActiveCorporateName}' (ID={ActiveCorporateId}).";
                    Log($"CorporateContext: {LastError}");
                    return false;
                }

                IsInitialized = true;
                Log($"CorporateContext: Ready - '{ActiveCorporateName}' + All Corporates, {EffectiveModelIds.Count} model(s): [{string.Join(", ", EffectiveModelIds)}]");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log($"CorporateContext.Initialize error: {ex.Message}");
                return false;
            }
        }

        #region DB Queries

        private string GetCorporateName(string dbType, int corporateId)
        {
            try
            {
                string query = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => @"SELECT ""NAME"" FROM ""MC_CORPORATE"" WHERE ""ID"" = @id",
                    "ORACLE" => "SELECT NAME FROM MC_CORPORATE WHERE ID = :id",
                    _ => "SELECT [NAME] FROM [dbo].[MC_CORPORATE] WHERE [ID] = @id"
                };

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    {
                        var param = cmd.CreateParameter();
                        param.ParameterName = dbType == "ORACLE" ? ":id" : "@id";
                        param.Value = corporateId;
                        cmd.Parameters.Add(param);

                        var result = cmd.ExecuteScalar();
                        return result?.ToString()?.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CorporateContext: GetCorporateName error: {ex.Message}");
                return null;
            }
        }

        private List<(int id, string name)> GetNonDefaultCorporates(string dbType)
        {
            var result = new List<(int, string)>();
            try
            {
                // All Corporates = ID 1, everything else is a real corporate
                string query = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => @"SELECT ""ID"", ""NAME"" FROM ""MC_CORPORATE"" WHERE ""ID"" != 1 ORDER BY ""ID""",
                    "ORACLE" => "SELECT ID, NAME FROM MC_CORPORATE WHERE ID != 1 ORDER BY ID",
                    _ => "SELECT [ID], [NAME] FROM [dbo].[MC_CORPORATE] WHERE [ID] != 1 ORDER BY [ID]"
                };

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            result.Add((Convert.ToInt32(reader["ID"]), reader["NAME"]?.ToString()?.Trim() ?? ""));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CorporateContext: GetNonDefaultCorporates error: {ex.Message}");
            }
            return result;
        }

        private (int id, string name)? GetActiveCorporate(string dbType)
        {
            try
            {
                string query = dbType?.ToUpper() switch
                {
                    "POSTGRESQL" => @"SELECT ""ID"", ""NAME"" FROM ""MC_CORPORATE"" WHERE ""ID"" != 1 AND ""IS_ACTIVE"" = true",
                    "ORACLE" => "SELECT ID, NAME FROM MC_CORPORATE WHERE ID != 1 AND IS_ACTIVE = 1",
                    _ => "SELECT [ID], [NAME] FROM [dbo].[MC_CORPORATE] WHERE [ID] != 1 AND [IS_ACTIVE] = 1"
                };

                using (var conn = DatabaseService.Instance.CreateConnection())
                {
                    conn.Open();
                    using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            return (Convert.ToInt32(reader["ID"]), reader["NAME"]?.ToString()?.Trim() ?? "");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"CorporateContext: GetActiveCorporate error: {ex.Message}");
            }
            return null;
        }

        private bool LoadEffectiveModelIds(string dbType)
        {
            EffectiveModelIds.Clear();

            // Active corporate models + All Corporates (ID=1) models
            string query = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""ID"" FROM ""MODEL"" WHERE ""CORPORATE_ID"" = @corpId
                                UNION
                                SELECT ""ID"" FROM ""MODEL"" WHERE ""CORPORATE_ID"" = 1",
                "ORACLE" => @"SELECT ID FROM MODEL WHERE CORPORATE_ID = :corpId
                            UNION
                            SELECT ID FROM MODEL WHERE CORPORATE_ID = 1",
                _ => @"SELECT [ID] FROM [dbo].[MODEL] WHERE [CORPORATE_ID] = @corpId
                      UNION
                      SELECT [ID] FROM [dbo].[MODEL] WHERE [CORPORATE_ID] = 1"
            };

            using (var conn = DatabaseService.Instance.CreateConnection())
            {
                conn.Open();
                using (var cmd = DatabaseService.Instance.CreateCommand(query, conn))
                {
                    var param = cmd.CreateParameter();
                    param.ParameterName = dbType == "ORACLE" ? ":corpId" : "@corpId";
                    param.Value = ActiveCorporateId;
                    cmd.Parameters.Add(param);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            EffectiveModelIds.Add(Convert.ToInt32(reader["ID"]));
                        }
                    }
                }
            }

            return EffectiveModelIds.Count > 0;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Build parameterized IN clause for EffectiveModelIds.
        /// </summary>
        public string BuildModelInParams(string dbType, DbCommand command, string prefix = "mdl")
        {
            var paramNames = new List<string>();
            for (int i = 0; i < EffectiveModelIds.Count; i++)
            {
                string paramName = dbType == "ORACLE" ? $":{prefix}{i}" : $"@{prefix}{i}";
                paramNames.Add(paramName);

                var param = command.CreateParameter();
                param.ParameterName = paramName;
                param.Value = EffectiveModelIds[i];
                command.Parameters.Add(param);
            }
            return string.Join(", ", paramNames);
        }

        private void Log(string message)
        {
            OnLog?.Invoke(message);
            System.Diagnostics.Debug.WriteLine(message);
        }

        #endregion
    }
}
