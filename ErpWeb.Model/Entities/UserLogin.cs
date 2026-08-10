using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ErpWeb.Model.Entities;

[Table("userlogin")]
public class UserLogin
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int uid { get; set; }

    [Required]
    [MaxLength(10)]
    public string id { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string name { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string password { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? email { get; set; }

    [MaxLength(20)]
    public string? mobileno { get; set; }

    public bool? active { get; set; }

    [MaxLength(20)]
    public string? userlevel { get; set; }

    public DateTime? Created { get; set; }

    public DateTime? Updated { get; set; }

    [MaxLength(10)]
    public string? UserID { get; set; }

    [MaxLength(10)]
    public string? UpdatedUID { get; set; }

    [Required]
    [MaxLength(5)]
    public string CompanyCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(5)]
    public string BranchCode { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string LocationCode { get; set; } = string.Empty;

    public bool changepass { get; set; }

    [MaxLength(100)]
    public string? ImagePath { get; set; }
}
