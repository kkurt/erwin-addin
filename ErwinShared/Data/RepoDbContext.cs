using System;
using Microsoft.EntityFrameworkCore;
using EliteSoft.Erwin.Shared.Data.Entities;
using EliteSoft.Erwin.Shared.Models;

namespace EliteSoft.Erwin.Shared.Data
{
    /// <summary>
    /// Repository Database Context with multi-provider support (MSSQL, PostgreSQL, Oracle)
    /// </summary>
    public class RepoDbContext : DbContext
    {
        private readonly string _connectionString;
        private readonly string _dbType;

        public DbSet<ConnectionSetting> ConnectionSettings { get; set; }
        public DbSet<AppConfig> AppConfigs { get; set; }
        public DbSet<TableType> TableTypes { get; set; }
        public DbSet<GlossaryConnectionDef> GlossaryConnectionDefs { get; set; }

        /// <summary>
        /// Creates a new RepoDbContext with the specified connection settings
        /// </summary>
        /// <param name="dbType">Database type: MSSQL, PostgreSQL, Oracle</param>
        /// <param name="connectionString">Connection string for the database</param>
        public RepoDbContext(string dbType, string connectionString)
        {
            _dbType = dbType?.ToUpper() ?? DbTypes.MSSQL;
            _connectionString = connectionString;
        }

        /// <summary>
        /// Creates a new RepoDbContext from BootstrapConfig
        /// </summary>
        public RepoDbContext(BootstrapConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _dbType = config.DbType?.ToUpper() ?? DbTypes.MSSQL;
            _connectionString = config.GetConnectionString();
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (optionsBuilder.IsConfigured)
                return;

            switch (_dbType)
            {
                case "MSSQL":
                    optionsBuilder.UseSqlServer(_connectionString);
                    break;

                case "POSTGRESQL":
                    optionsBuilder.UseNpgsql(_connectionString);
                    break;

                case "ORACLE":
                    optionsBuilder.UseOracle(_connectionString);
                    break;

                default:
                    optionsBuilder.UseSqlServer(_connectionString);
                    break;
            }
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // ConnectionSetting configuration
            modelBuilder.Entity<ConnectionSetting>(entity =>
            {
                entity.HasIndex(e => e.ConnectionName).IsUnique();
            });

            // AppConfig configuration
            modelBuilder.Entity<AppConfig>(entity =>
            {
                entity.HasIndex(e => e.ConfigKey).IsUnique();
            });

            // TableType configuration
            modelBuilder.Entity<TableType>(entity =>
            {
                entity.HasIndex(e => e.Name).IsUnique();
            });
        }

        /// <summary>
        /// Ensures the database schema is created. Call this on first run.
        /// </summary>
        public void EnsureTablesCreated()
        {
            Database.EnsureCreated();
        }
    }
}
