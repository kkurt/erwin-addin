using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing Approvement mechanism branch definitions
    /// </summary>
    [Table("APPROVEMENT_DEF")]
    public class ApprovementDef
    {
        [Key]
        [Column("ID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [Column("DEVELEOPMENT_BRANCH")]
        [StringLength(250)]
        public string DevelopmentBranch { get; set; }

        [Required]
        [Column("PROD_BRANCH")]
        [StringLength(250)]
        public string ProdBranch { get; set; }
    }
}
