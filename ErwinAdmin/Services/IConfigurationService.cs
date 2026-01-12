using System.Collections.Generic;
using System.Threading.Tasks;
using EliteSoft.Erwin.Shared.Data.Entities;
using EliteSoft.Erwin.Shared.Models;

namespace EliteSoft.Erwin.Admin.Services
{
    /// <summary>
    /// Service interface for managing application configuration
    /// </summary>
    public interface IConfigurationService
    {
        #region Bootstrap Configuration

        /// <summary>
        /// Gets the current bootstrap configuration
        /// </summary>
        BootstrapConfig GetBootstrapConfig();

        /// <summary>
        /// Saves the bootstrap configuration
        /// </summary>
        void SaveBootstrapConfig(BootstrapConfig config);

        /// <summary>
        /// Tests a database connection
        /// </summary>
        /// <param name="config">Configuration to test</param>
        /// <param name="errorMessage">Error message if connection fails</param>
        /// <returns>True if connection successful</returns>
        bool TestDatabaseConnection(BootstrapConfig config, out string errorMessage);

        #endregion

        #region Project Properties

        /// <summary>
        /// Gets a project property value
        /// </summary>
        string GetProjectProperty(string key);

        /// <summary>
        /// Gets a project property as boolean
        /// </summary>
        bool GetProjectPropertyBool(string key, bool defaultValue = false);

        /// <summary>
        /// Sets a project property value
        /// </summary>
        void SetProjectProperty(string key, string value);

        /// <summary>
        /// Sets a project property as boolean
        /// </summary>
        void SetProjectPropertyBool(string key, bool value);

        #endregion

        #region Table Types

        /// <summary>
        /// Gets all table types
        /// </summary>
        List<TableType> GetTableTypes();

        /// <summary>
        /// Saves table types (add/update/delete)
        /// </summary>
        /// <returns>Tuple of (inserted, updated, deleted) counts</returns>
        (int inserted, int updated, int deleted) SaveTableTypes(List<TableType> tableTypes);

        /// <summary>
        /// Clears all table types
        /// </summary>
        void ClearTableTypes();

        #endregion

        #region Glossary Connection

        /// <summary>
        /// Gets the glossary connection definition
        /// </summary>
        GlossaryConnectionDef GetGlossaryConnection();

        /// <summary>
        /// Saves the glossary connection definition
        /// </summary>
        void SaveGlossaryConnection(GlossaryConnectionDef connection);

        /// <summary>
        /// Tests a glossary database connection
        /// </summary>
        bool TestGlossaryConnection(GlossaryConnectionDef connection, out string errorMessage);

        #endregion

        #region Approvement Mechanism

        /// <summary>
        /// Gets the approvement definition
        /// </summary>
        ApprovementDef GetApprovementDef();

        /// <summary>
        /// Saves the approvement definition
        /// </summary>
        void SaveApprovementDef(ApprovementDef approvement);

        #endregion

        #region Database Initialization

        /// <summary>
        /// Ensures all required database tables are created
        /// </summary>
        void EnsureDatabaseInitialized();

        #endregion
    }
}
