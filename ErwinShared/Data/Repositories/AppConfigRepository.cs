using System;
using System.Collections.Generic;
using System.Linq;
using EliteSoft.Erwin.Shared.Data.Entities;

namespace EliteSoft.Erwin.Shared.Data.Repositories
{
    /// <summary>
    /// Repository for managing AppConfig entities
    /// </summary>
    public class AppConfigRepository
    {
        private readonly RepoDbContextFactory _contextFactory;

        public AppConfigRepository()
        {
            _contextFactory = new RepoDbContextFactory();
        }

        public AppConfigRepository(RepoDbContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        /// <summary>
        /// Gets all config settings
        /// </summary>
        public List<AppConfig> GetAll()
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.AppConfigs.ToList();
            }
        }

        /// <summary>
        /// Gets config settings by category
        /// </summary>
        public List<AppConfig> GetByCategory(string category)
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.AppConfigs
                    .Where(c => c.Category.ToUpper() == category.ToUpper())
                    .ToList();
            }
        }

        /// <summary>
        /// Gets a config value by key
        /// </summary>
        public AppConfig GetByKey(string configKey)
        {
            using (var context = _contextFactory.CreateContext())
            {
                return context.AppConfigs
                    .FirstOrDefault(c => c.ConfigKey.ToUpper() == configKey.ToUpper());
            }
        }

        /// <summary>
        /// Gets a string config value
        /// </summary>
        public string GetString(string configKey, string defaultValue = null)
        {
            var config = GetByKey(configKey);
            return config?.ConfigValue ?? defaultValue;
        }

        /// <summary>
        /// Gets an integer config value
        /// </summary>
        public int GetInt(string configKey, int defaultValue = 0)
        {
            var config = GetByKey(configKey);
            return config?.GetInt(defaultValue) ?? defaultValue;
        }

        /// <summary>
        /// Gets a boolean config value
        /// </summary>
        public bool GetBool(string configKey, bool defaultValue = false)
        {
            var config = GetByKey(configKey);
            return config?.GetBool(defaultValue) ?? defaultValue;
        }

        /// <summary>
        /// Sets a config value (creates or updates)
        /// </summary>
        public AppConfig Set(string configKey, string configValue, string valueType = "string", string category = null, string description = null)
        {
            using (var context = _contextFactory.CreateContext())
            {
                var existing = context.AppConfigs
                    .FirstOrDefault(c => c.ConfigKey.ToUpper() == configKey.ToUpper());

                if (existing != null)
                {
                    existing.ConfigValue = configValue;
                    existing.ValueType = valueType;
                    if (category != null) existing.Category = category;
                    if (description != null) existing.Description = description;
                    existing.UpdatedDate = DateTime.Now;

                    context.SaveChanges();
                    return existing;
                }
                else
                {
                    var config = new AppConfig
                    {
                        ConfigKey = configKey,
                        ConfigValue = configValue,
                        ValueType = valueType,
                        Category = category,
                        Description = description,
                        CreatedDate = DateTime.Now
                    };

                    context.AppConfigs.Add(config);
                    context.SaveChanges();
                    return config;
                }
            }
        }

        /// <summary>
        /// Sets a string config value
        /// </summary>
        public AppConfig SetString(string configKey, string value, string category = null, string description = null)
        {
            return Set(configKey, value, "string", category, description);
        }

        /// <summary>
        /// Sets an integer config value
        /// </summary>
        public AppConfig SetInt(string configKey, int value, string category = null, string description = null)
        {
            return Set(configKey, value.ToString(), "int", category, description);
        }

        /// <summary>
        /// Sets a boolean config value
        /// </summary>
        public AppConfig SetBool(string configKey, bool value, string category = null, string description = null)
        {
            return Set(configKey, value.ToString(), "bool", category, description);
        }

        /// <summary>
        /// Deletes a config by key
        /// </summary>
        public bool Delete(string configKey)
        {
            using (var context = _contextFactory.CreateContext())
            {
                var config = context.AppConfigs
                    .FirstOrDefault(c => c.ConfigKey.ToUpper() == configKey.ToUpper());

                if (config != null)
                {
                    context.AppConfigs.Remove(config);
                    context.SaveChanges();
                    return true;
                }
                return false;
            }
        }
    }
}
