using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing project-level configuration properties.
    /// Simple key-value store for settings like USE_EXTERNAL_GLOSSARY, USE_TABLE_TYPES etc.
    /// </summary>
    [Table("PROJECT_PROPERTY")]
    public class ProjectProperty
    {
        [Key]
        [Column("Id")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        /// <summary>
        /// Property key (e.g., "USE_EXTERNAL_GLOSSARY", "USE_TABLE_TYPES")
        /// </summary>
        [Required]
        [Column("KEY")]
        [StringLength(50)]
        public string Key { get; set; }

        /// <summary>
        /// Property value
        /// </summary>
        [Required]
        [Column("VALUE")]
        [StringLength(50)]
        public string Value { get; set; }

        #region Helper Methods

        public bool GetBool(bool defaultValue = false)
        {
            if (string.IsNullOrEmpty(Value)) return defaultValue;
            return bool.TryParse(Value, out var result) ? result : defaultValue;
        }

        public int GetInt(int defaultValue = 0)
        {
            if (string.IsNullOrEmpty(Value)) return defaultValue;
            return int.TryParse(Value, out var result) ? result : defaultValue;
        }

        #endregion
    }
}
