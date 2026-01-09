using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing table type definitions with affix settings
    /// </summary>
    [Table("TABLE_TYPE")]
    public class TableType
    {
        [Key]
        [Column("ID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [Column("NAME")]
        [StringLength(50)]
        public string Name { get; set; }

        [Required]
        [Column("AFFIX")]
        [StringLength(50)]
        public string Affix { get; set; }

        [Required]
        [Column("NAME_EXTENSION_LOCATION")]
        [StringLength(50)]
        public string NameExtensionLocation { get; set; }
    }

    /// <summary>
    /// Default table type values
    /// </summary>
    public static class TableTypeDefaults
    {
        public static TableType[] GetDefaults()
        {
            return new[]
            {
                new TableType { Name = "LOG", Affix = "LOG", NameExtensionLocation = "PREFIX" },
                new TableType { Name = "PARAMETER", Affix = "PRM", NameExtensionLocation = "PREFIX" },
                new TableType { Name = "TRANSACTION", Affix = "TRX", NameExtensionLocation = "PREFIX" },
                new TableType { Name = "HISTORY", Affix = "HST", NameExtensionLocation = "SUFFIX" }
            };
        }
    }
}
