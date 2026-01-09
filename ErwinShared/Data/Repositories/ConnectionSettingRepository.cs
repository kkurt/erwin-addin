using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using EliteSoft.Erwin.Shared.Data.Entities;

namespace EliteSoft.Erwin.Shared.Data.Repositories
{
    /// <summary>
    /// Repository for managing ConnectionSetting entities
    /// </summary>
    public class ConnectionSettingRepository
    {
        private readonly RepoDbContextFactory _contextFactory;

        public ConnectionSettingRepository()
        {
            _contextFactory = new RepoDbContextFactory();
        }

        public ConnectionSettingRepository(RepoDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        /// <summary>
        /// Gets all connection settings
        /// </summary>
        public List<ConnectionSetting> GetAll()
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.ConnectionSettings.ToList();
            }
        }

        /// <summary>
        /// Gets active connection settings only
        /// </summary>
        public List<ConnectionSetting> GetActive()
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.ConnectionSettings.Where(c => c.IsActive).ToList();
            }
        }

        /// <summary>
        /// Gets a connection setting by name (e.g., "GLOSSARY")
        /// </summary>
        public ConnectionSetting GetByName(string connectionName)
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.ConnectionSettings
                    .FirstOrDefault(c => c.ConnectionName.ToUpper() == connectionName.ToUpper());
            }
        }

        /// <summary>
        /// Gets a connection setting by ID
        /// </summary>
        public ConnectionSetting GetById(int id)
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.ConnectionSettings.Find(id);
            }
        }

        /// <summary>
        /// Adds or updates a connection setting
        /// </summary>
        public ConnectionSetting Save(ConnectionSetting setting)
        {
            using (var context = _contextFactory.CreateContext())
            {
                var existing = context.ConnectionSettings
                    .FirstOrDefault(c => c.ConnectionName.ToUpper() == setting.ConnectionName.ToUpper());

                if (existing != null)
                {
                    // Update existing
                    existing.DbType = setting.DbType;
                    existing.Host = setting.Host;
                    existing.Port = setting.Port;
                    existing.DbSchema = setting.DbSchema;
                    existing.Username = setting.Username;
                    existing.Password = setting.Password;
                    existing.Description = setting.Description;
                    existing.IsActive = setting.IsActive;
                    existing.UpdatedDate = DateTime.Now;

                    context.SaveChanges();
                    return existing;
                }
                else
                {
                    // Add new
                    setting.CreatedDate = DateTime.Now;
                    context.ConnectionSettings.Add(setting);
                    context.SaveChanges();
                    return setting;
                }
            }
        }

        /// <summary>
        /// Deletes a connection setting by ID
        /// </summary>
        public bool Delete(int id)
        {
            using (var context = _contextFactory.CreateContext())
            {
                var setting = context.ConnectionSettings.Find(id);
                if (setting != null)
                {
                    context.ConnectionSettings.Remove(setting);
                    context.SaveChanges();
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Deletes a connection setting by name
        /// </summary>
        public bool DeleteByName(string connectionName)
        {
            using (var context = _contextFactory.CreateContext())
            {
                var setting = context.ConnectionSettings
                    .FirstOrDefault(c => c.ConnectionName.ToUpper() == connectionName.ToUpper());

                if (setting != null)
                {
                    context.ConnectionSettings.Remove(setting);
                    context.SaveChanges();
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Tests if a connection setting can connect to its target database
        /// </summary>
        public bool TestConnection(string connectionName, out string errorMessage)
        {
            errorMessage = null;
            var setting = GetByName(connectionName);

            if (setting == null)
            {
                errorMessage = $"Connection '{connectionName}' not found";
                return false;
            }

            return TestConnection(setting, out errorMessage);
        }

        /// <summary>
        /// Tests if a connection setting can connect to its target database
        /// </summary>
        public bool TestConnection(ConnectionSetting setting, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                var connectionString = setting.GetConnectionString();
                var factory = new RepoDbContextFactory();
                return factory.TestConnection(setting.DbType, connectionString, out errorMessage);
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
