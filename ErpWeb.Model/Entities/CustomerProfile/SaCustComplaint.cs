using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ErpWeb.Model.Entities.CustomerProfile
{
    [Table("SaCustComplaint", Schema = "dbo")]
    public partial class SaCustComplaint
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        [Column("ID")]
        public int Id { get; set; }

        public DateTime? TrxDate { get; set; }

        public string Solution { get; set; }

        public double? ClaimAmt { get; set; }

        public double? TotalCost { get; set; }

        public double? ProdCost { get; set; }

        public double? PartCost { get; set; }

        public double? CourierCost { get; set; }

        public string CreatedBy { get; set; }

        public DateTime? CreatedOn { get; set; }

        public string UpdatedBy { get; set; }

        public DateTime? UpdatedOn { get; set; }
    }
}