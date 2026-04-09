using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using Microsoft.Win32;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Determines active corporate and loads effective project IDs.
    /// All Corporates = ID 1 (hardcoded, always present).
    ///
    /// Flow:
    ///   1. Registry has ActiveCorporateId? → validate in DB → load its projects + All Corporates projects
    ///   2. Registry empty? → DB has exactly 1 non-default corporate? → use it + All Corporates
    ///   3. Otherwise → error, extension closes
    /// </summary>
    public class CorporateContextService
    {
        private static CorporateContextService _instance;
        private static readonly object _lock = new object();

        private const string RegistryPath = @"Software\EliteSoft\MetaRepo\Extension";
        private const int AllCorporatesId = 1;

        public int ActiveCorporateId { get; private set; }
        public string ActiveCorporateName { get; private set; }
        public List<int> EffectiveProjectIds { get; private set; }
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
            EffectiveProjectIds = new List<int>();
        }

        public bool Initialize()
        {
            try
            {
                IsInitialized = false;
                LastError = null;

                if (!DatabaseService.Instance.IsConfigured)
                {
                    LastError = "Database not configured. Please run Admin panel or install with -MetaRepo parameter.";
                    Log($"CorporateContext: {LastError}");
                    return false;
                }

                string dbType = DatabaseService.Instance.GetDbType();

                // Step 1: Try registry
                int registryCorporateId = ReadCorporateIdFromRegistry();

                if (registryCorporateId > 0 && registryCorporateId != AllCorporatesId)
                {
                    // Validate corporate exists in DB
                    string corpName = GetCorporateName(dbType, registryCorporateId);
                    if (corpName != null)
                    {
                        ActiveCorporateId = registryCorporateId;
                        ActiveCorporateName = corpName;
                        Log($"CorporateContext: From registry → '{corpName}' (ID={registryCorporateId})");
                    }
                    else
                    {
                        // Corporate not found in DB — clear invalid registry entry
                        Log($"CorporateContext: Registry corporate ID={registryCorporateId} not found in DB — clearing registry");
                        ClearCorporateRegistry();
                        registryCorporateId = 0;
                    }
                }

                // Step 2: No valid registry → auto-detect from DB
                if (ActiveCorporateId == 0)
                {
                    var corporates = GetNonDefaultCorporates(dbType);

                    if (corporates.Count == 1)
                    {
                        // Exactly 1 non-default corporate → auto-select
                        ActiveCorporateId = corporates[0].id;
                        ActiveCorporateName = corporates[0].name;
                        Log($"CorporateContext: Auto-detected single corporate -> '{ActiveCorporateName}' (ID={ActiveCorporateId})");
                    }
                    else if (corporates.Count > 1)
                    {
                        string names = string.Join(", ", corporates.Select(c => $"'{c.name}' (ID={c.id})"));
                        LastError = $"Multiple corporates found: {names}. Please run Admin panel to select active corporate.";
                        Log($"CorporateContext: {LastError}");
                        return false;
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
                }

                // Step 3: Load effective project IDs
                if (!LoadEffectiveProjectIds(dbType))
                {
                    LastError = $"No projects found for corporate '{ActiveCorporateName}' (ID={ActiveCorporateId}).";
                    Log($"CorporateContext: {LastError}");
                    return false;
                }

                IsInitialized = true;
                Log($"CorporateContext: Ready — '{ActiveCorporateName}' + All Corporates, {EffectiveProjectIds.Count} project(s): [{string.Join(", ", EffectiveProjectIds)}]");
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log($"CorporateContext.Initialize error: {ex.Message}");
                return false;
            }
        }

        #region Registry

        private int ReadCorporateIdFromRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath))
                {
                    if (key == null) return 0;
                    var val = key.GetValue("ActiveCorporateId")?.ToString();
                    return int.TryParse(val, out int id) && id > 0 ? id : 0;
                }
            }
            catch (Exception ex)
            {
                Log($"CorporateContext: Registry read error: {ex.Message}");
                return 0;
            }
        }

        private void ClearCorporateRegistry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true))
                {
                    if (key == null) return;
                    key.DeleteValue("ActiveCorporateId", throwOnMissingValue: false);
                    key.DeleteValue("ActiveCorporateCode", throwOnMissingValue: false);
                }
            }
            catch (Exception ex)
            {
                Log($"CorporateContext: Registry clear error: {ex.Message}");
            }
        }

        #endregion

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

        private bool LoadEffectiveProjectIds(string dbType)
        {
            EffectiveProjectIds.Clear();

            // Active corporate projects + All Corporates (ID=1) projects
            string query = dbType?.ToUpper() switch
            {
                "POSTGRESQL" => @"SELECT ""ID"" FROM ""PROJECT"" WHERE ""CORPORATE_ID"" = @corpId
                                UNION
                                SELECT ""ID"" FROM ""PROJECT"" WHERE ""CORPORATE_ID"" = 1",
                "ORACLE" => @"SELECT ID FROM PROJECT WHERE CORPORATE_ID = :corpId
                            UNION
                            SELECT ID FROM PROJECT WHERE CORPORATE_ID = 1",
                _ => @"SELECT [ID] FROM [dbo].[PROJECT] WHERE [CORPORATE_ID] = @corpId
                      UNION
                      SELECT [ID] FROM [dbo].[PROJECT] WHERE [CORPORATE_ID] = 1"
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
                            EffectiveProjectIds.Add(Convert.ToInt32(reader["ID"]));
                        }
                    }
                }
            }

            return EffectiveProjectIds.Count > 0;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Build parameterized IN clause for EffectiveProjectIds.
        /// </summary>
        public string BuildProjectInParams(string dbType, DbCommand command, string prefix = "prj")
        {
            var paramNames = new List<string>();
            for (int i = 0; i < EffectiveProjectIds.Count; i++)
            {
                string paramName = dbType == "ORACLE" ? $":{prefix}{i}" : $"@{prefix}{i}";
                paramNames.Add(paramName);

                var param = command.CreateParameter();
                param.ParameterName = paramName;
                param.Value = EffectiveProjectIds[i];
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
