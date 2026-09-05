using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ErpWeb.Model.Entities.CustomerProfile
{
    [Table("SaCustomerType", Schema = "dbo")]
    public partial class SaCustomerType
    {
        [Key]
        [Required]
        public string CustTypeCode { get; set; }

        public string CustTypeDesc { get; set; }

        public bool? Active { get; set; }

        public DateTime? Created { get; set; }

        public DateTime? Updated { get; set; }

        [Column("UserID")]
        public string UserId { get; set; }
    }
}