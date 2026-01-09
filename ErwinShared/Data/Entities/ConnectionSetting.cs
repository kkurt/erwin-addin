using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing database connection settings (e.g., Glossary DB connection)
    /// </summary>
    [Table("ERWIN_CONNECTION_SETTINGS")]
    public class ConnectionSetting
    {
        [Key]
        [Column("ID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Connection name/identifier (e.g., "GLOSSARY", "TABLE_TYPE")
        /// </summary>
        [Required]
        [Column("CONNECTION_NAME")]
        [StringLength(100)]
        public string ConnectionName { get; set; }

        /// <summary>
        /// Database type: MSSQL, PostgreSQL, Oracle
        /// </summary>
        [Required]
        [Column("DB_TYPE")]
        [StringLength(50)]
        public string DbType { get; set; }

        /// <summary>
        /// Database server host
        /// </summary>
        [Required]
        [Column("HOST")]
        [StringLength(250)]
        public string Host { get; set; }

        /// <summary>
        /// Database server port
        /// </summary>
        [Column("PORT")]
        [StringLength(50)]
        public string Port { get; set; }

        /// <summary>
        /// Database name or schema
        /// </summary>
        [Column("DB_SCHEMA")]
        [StringLength(100)]
        public string DbSchema { get; set; }

        /// <summary>
        /// Database username
        /// </summary>
        [Column("USERNAME")]
        [StringLength(100)]
        public string Username { get; set; }

        /// <summary>
        /// Database password (should be encrypted in production)
        /// </summary>
        [Column("PASSWORD")]
        [StringLength(250)]
        public string Password { get; set; }

        /// <summary>
        /// Optional description
        /// </summary>
        [Column("DESCRIPTION")]
        [StringLength(500)]
        public string Description { get; set; }

        /// <summary>
        /// Whether this connection is active
        /// </summary>
        [Column("IS_ACTIVE")]
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Record creation date
        /// </summary>
        [Column("CREATED_DATE")]
        public DateTime CreatedDate { get; set; } = DateTime.Now;

        /// <summary>
        /// Last update date
        /// </summary>
        [Column("UPDATED_DATE")]
        public DateTime? UpdatedDate { get; set; }

        /// <summary>
        /// Builds the connection string based on DbType
        /// </summary>
        public string GetConnectionString()
        {
            switch (DbType?.ToUpper())
            {
                case "MSSQL":
                    return $"Server={Host},{Port};Database={DbSchema};User Id={Username};Password={Password};TrustServerCertificate=True;";

                case "POSTGRESQL":
                    return $"Host={Host};Port={Port};Database={DbSchema};Username={Username};Password={Password};";

                case "ORACLE":
                    return $"Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST={Host})(PORT={Port}))(CONNECT_DATA=(SERVICE_NAME={DbSchema})));User Id={Username};Password={Password};";

                default:
                    return $"Server={Host},{Port};Database={DbSchema};User Id={Username};Password={Password};TrustServerCertificate=True;";
            }
        }
    }
}
