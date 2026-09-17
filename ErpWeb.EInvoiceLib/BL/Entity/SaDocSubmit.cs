using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("SaDocSubmit", Schema = "dbo")]
    public partial class SaDocSubmit
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string? uuid { get; set; }

        public string? submissionUUID { get; set; }

        public string? longId { get; set; }

        public string? internalId { get; set; }

        public string? typeName { get; set; }

        public string? typeVersionName { get; set; }

        public string? issuerTin { get; set; }

        public string? issuerName { get; set; }

        public string? receiverId { get; set; }

        public string? receiverName { get; set; }

        public DateTime? dateTimeIssued { get; set; }

        public DateTime? dateTimeReceived { get; set; }

        public DateTime? dateTimeValidated { get; set; }

        public decimal? totalSales { get; set; }

        public decimal? totalDiscount { get; set; }

        public decimal? netAmount { get; set; }

        public decimal? total { get; set; }

        public string? status { get; set; }

        public DateTime? cancelDateTime { get; set; }

        public DateTime? rejectRequestDateTime { get; set; }

        public string? documentStatusReason { get; set; }

        public string? createdByUserId { get; set; }

        public string? document { get; set; }
    }
}
