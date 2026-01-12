using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using EliteSoft.Erwin.Shared.Data;
using EliteSoft.Erwin.Shared.Data.Entities;
using EliteSoft.Erwin.Shared.Models;
using EliteSoft.Erwin.Shared.Services;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service implementation for managing application configuration
    /// </summary>
    public class ConfigurationService : IConfigurationService
    {
        private readonly BootstrapService _bootstrapService;

        public ConfigurationService()
        {
            _bootstrapService = new BootstrapService();
        }

        public ConfigurationService(BootstrapService bootstrapService)
        {
            _bootstrapService = bootstrapService ?? throw new ArgumentNullException(nameof(bootstrapService));
        }

        #region Bootstrap Configuration

        public BootstrapConfig GetBootstrapConfig()
        {
            return _bootstrapService.GetConfig();
        }

        public void SaveBootstrapConfig(BootstrapConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _bootstrapService.SaveConfig(config);
        }

        public bool TestDatabaseConnection(BootstrapConfig config, out string errorMessage)
        {
            if (config == null)
            {
                errorMessage = "Configuration is null";
                return false;
            }

            var factory = new RepoDbContextFactory();
            return factory.TestConnection(config, out errorMessage);
        }

        #endregion

        #region Project Properties

        public string GetProjectProperty(string key)
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return null;

            try
            {
                using (var context = new RepoDbContext(config))
                {
                    var prop = context.ProjectProperties.FirstOrDefault(p => p.Key == key);
                    return prop?.Value;
                }
            }
            catch
            {
                return null;
            }
        }

        public bool GetProjectPropertyBool(string key, bool defaultValue = false)
        {
            var value = GetProjectProperty(key);
            if (string.IsNullOrEmpty(value))
                return defaultValue;

            return bool.TryParse(value, out var result) ? result : defaultValue;
        }

        public void SetProjectProperty(string key, string value)
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return;

            try
            {
                using (var context = new RepoDbContext(config))
                {
                    context.EnsureTablesCreated();

                    var prop = context.ProjectProperties.FirstOrDefault(p => p.Key == key);
                    if (prop != null)
                    {
                        prop.Value = value;
                    }
                    else
                    {
                        context.ProjectProperties.Add(new ProjectProperty
                        {
                            Key = key,
                            Value = value
                        });
                    }
                    context.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConfigService] Failed to set property {key}: {ex.Message}");
            }
        }

        public void SetProjectPropertyBool(string key, bool value)
        {
            SetProjectProperty(key, value.ToString());
        }

        #endregion

        #region Table Types

        public List<TableType> GetTableTypes()
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return new List<TableType>();

            try
            {
                using (var context = new RepoDbContext(config))
                {
                    return context.TableTypes.ToList();
                }
            }
            catch
            {
                return new List<TableType>();
            }
        }

        public (int inserted, int updated, int deleted) SaveTableTypes(List<TableType> tableTypes)
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                throw new InvalidOperationException("Database not configured");

            using (var context = new RepoDbContext(config))
            {
                context.EnsureTablesCreated();

                var existingTypes = context.TableTypes.ToList();
                var inputNames = tableTypes.Select(t => t.Name).ToHashSet();

                // Delete types not in input
                var toDelete = existingTypes.Where(t => !inputNames.Contains(t.Name)).ToList();
                foreach (var item in toDelete)
                {
                    context.TableTypes.Remove(item);
                }

                int insertedCount = 0;
                int updatedCount = 0;

                foreach (var tt in tableTypes)
                {
                    var existing = existingTypes.FirstOrDefault(t => t.Name == tt.Name);
                    if (existing != null)
                    {
                        if (existing.Affix != tt.Affix || existing.NameExtensionLocation != tt.NameExtensionLocation)
                        {
                            existing.Affix = tt.Affix;
                            existing.NameExtensionLocation = tt.NameExtensionLocation;
                            updatedCount++;
                        }
                    }
                    else
                    {
                        context.TableTypes.Add(new TableType
                        {
                            Name = tt.Name,
                            Affix = tt.Affix,
                            NameExtensionLocation = tt.NameExtensionLocation
                        });
                        insertedCount++;
                    }
                }

                context.SaveChanges();
                return (insertedCount, updatedCount, toDelete.Count);
            }
        }

        public void ClearTableTypes()
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return;

            using (var context = new RepoDbContext(config))
            {
                var allTypes = context.TableTypes.ToList();
                foreach (var tt in allTypes)
                {
                    context.TableTypes.Remove(tt);
                }
                context.SaveChanges();
            }
        }

        #endregion

        #region Glossary Connection

        public GlossaryConnectionDef GetGlossaryConnection()
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return null;

            try
            {
                using (var context = new RepoDbContext(config))
                {
                    return context.GlossaryConnectionDefs.FirstOrDefault();
                }
            }
            catch
            {
                return null;
            }
        }

        public void SaveGlossaryConnection(GlossaryConnectionDef connection)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));

            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                throw new InvalidOperationException("Database not configured");

            using (var context = new RepoDbContext(config))
            {
                context.EnsureTablesCreated();

                var existing = context.GlossaryConnectionDefs.FirstOrDefault();
                if (existing != null)
                {
                    existing.Host = connection.Host;
                    existing.Port = connection.Port;
                    existing.DbSchema = connection.DbSchema;
                    existing.Username = connection.Username;
                    existing.Password = connection.Password;
                }
                else
                {
                    context.GlossaryConnectionDefs.Add(connection);
                }

                context.SaveChanges();
            }
        }

        public bool TestGlossaryConnection(GlossaryConnectionDef connection, out string errorMessage)
        {
            if (connection == null)
            {
                errorMessage = "Connection is null";
                return false;
            }

            try
            {
                var connectionString = $"Server={connection.Host},{connection.Port};Database={connection.DbSchema};User Id={connection.Username};Password={connection.Password};TrustServerCertificate=True;";

                using (var conn = new SqlConnection(connectionString))
                {
                    conn.Open();
                    errorMessage = null;
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        #endregion

        #region Approvement Mechanism

        public ApprovementDef GetApprovementDef()
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return null;

            try
            {
                using (var context = new RepoDbContext(config))
                {
                    return context.ApprovementDefs.FirstOrDefault();
                }
            }
            catch
            {
                return null;
            }
        }

        public void SaveApprovementDef(ApprovementDef approvement)
        {
            if (approvement == null)
                throw new ArgumentNullException(nameof(approvement));

            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                throw new InvalidOperationException("Database not configured");

            using (var context = new RepoDbContext(config))
            {
                context.EnsureTablesCreated();

                var existing = context.ApprovementDefs.FirstOrDefault();
                if (existing != null)
                {
                    existing.DevelopmentBranch = approvement.DevelopmentBranch;
                    existing.ProdBranch = approvement.ProdBranch;
                }
                else
                {
                    context.ApprovementDefs.Add(approvement);
                }

                context.SaveChanges();
            }
        }

        #endregion

        #region Database Initialization

        public void EnsureDatabaseInitialized()
        {
            var config = GetBootstrapConfig();
            if (config == null || !config.IsConfigured)
                return;

            using (var context = new RepoDbContext(config))
            {
                context.EnsureTablesCreated();
            }
        }

        #endregion
    }
}
