using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing general application configuration key-value pairs
    /// </summary>
    [Table("ERWIN_APP_CONFIG")]
    public class AppConfig
    {
        [Key]
        [Column("ID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Configuration key (e.g., "DEFAULT_TABLE_TYPE", "VALIDATION_ENABLED")
        /// </summary>
        [Required]
        [Column("CONFIG_KEY")]
        [StringLength(100)]
        public string ConfigKey { get; set; }

        /// <summary>
        /// Configuration value
        /// </summary>
        [Column("CONFIG_VALUE")]
        [StringLength(500)]
        public string ConfigValue { get; set; }

        /// <summary>
        /// Value type hint (string, int, bool, json)
        /// </summary>
        [Column("VALUE_TYPE")]
        [StringLength(50)]
        public string ValueType { get; set; } = "string";

        /// <summary>
        /// Optional description of this config setting
        /// </summary>
        [Column("DESCRIPTION")]
        [StringLength(500)]
        public string Description { get; set; }

        /// <summary>
        /// Category for grouping configs (e.g., "VALIDATION", "UI", "GENERAL")
        /// </summary>
        [Column("CATEGORY")]
        [StringLength(100)]
        public string Category { get; set; }

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

        #region Helper Methods

        public string GetString() => ConfigValue;

        public int GetInt(int defaultValue = 0)
        {
            return int.TryParse(ConfigValue, out var result) ? result : defaultValue;
        }

        public bool GetBool(bool defaultValue = false)
        {
            return bool.TryParse(ConfigValue, out var result) ? result : defaultValue;
        }

        #endregion
    }
}
