using System;
using EliteSoft.Erwin.Shared.Models;
using EliteSoft.Erwin.Shared.Services;

namespace EliteSoft.Erwin.Shared.Data
{
    /// <summary>
    /// Factory for creating RepoDbContext instances using bootstrap configuration
    /// </summary>
    public class RepoDbContextFactory
    {
        private readonly BootstrapService _bootstrapService;

        public RepoDbContextFactory()
        {
            _bootstrapService = new BootstrapService();
        }

        public RepoDbContextFactory(BootstrapService bootstrapService)
        {
            _bootstrapService = bootstrapService ?? throw new ArgumentNullException(nameof(bootstrapService));
        }

        /// <summary>
        /// Creates a RepoDbContext using the stored bootstrap configuration
        /// </summary>
        /// <returns>RepoDbContext instance or null if not configured</returns>
        public RepoDbContext CreateContext()
        {
            var config = _bootstrapService.GetConfig();
            if (config == null || !config.IsConfigured)
            {
                return null;
            }

            return new RepoDbContext(config);
        }

        /// <summary>
        /// Creates a RepoDbContext with explicit connection parameters
        /// </summary>
        public RepoDbContext CreateContext(string dbType, string connectionString)
        {
            return new RepoDbContext(dbType, connectionString);
        }

        /// <summary>
        /// Creates a RepoDbContext from a BootstrapConfig
        /// </summary>
        public RepoDbContext CreateContext(BootstrapConfig config)
        {
            return new RepoDbContext(config);
        }

        /// <summary>
        /// Tests if the bootstrap configuration can connect to the database
        /// </summary>
        public bool TestConnection(out string errorMessage)
        {
            errorMessage = null;
            try
            {
                using (var context = CreateContext())
                {
                    if (context == null)
                    {
                        errorMessage = "Bootstrap configuration not found";
                        return false;
                    }

                    context.Database.CanConnect();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Tests connection with explicit parameters
        /// </summary>
        public bool TestConnection(string dbType, string connectionString, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                using (var context = new RepoDbContext(dbType, connectionString))
                {
                    context.Database.CanConnect();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Tests connection with BootstrapConfig
        /// </summary>
        public bool TestConnection(BootstrapConfig config, out string errorMessage)
        {
            errorMessage = null;
            try
            {
                using (var context = new RepoDbContext(config))
                {
                    context.Database.CanConnect();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
