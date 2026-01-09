using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EliteSoft.Erwin.Shared.Data.Entities
{
    /// <summary>
    /// Entity for storing Glossary database connection definition
    /// </summary>
    [Table("GLOSSARY_CONNECTION_DEF")]
    public class GlossaryConnectionDef
    {
        [Key]
        [Column("ID")]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }

        [Required]
        [Column("HOST")]
        [StringLength(250)]
        public string Host { get; set; }

        [Required]
        [Column("PORT")]
        [StringLength(50)]
        public string Port { get; set; }

        [Required]
        [Column("DB_SCHEMA")]
        [StringLength(50)]
        public string DbSchema { get; set; }

        [Required]
        [Column("USERNAME")]
        [StringLength(50)]
        public string Username { get; set; }

        [Required]
        [Column("PASSWORD")]
        [StringLength(50)]
        public string Password { get; set; }
    }
}
