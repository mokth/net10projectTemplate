using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    /// <summary>
    /// LEGACY schema reference. Deliberately <b>not mapped by any DbContext</b> any more (plan Phase 4):
    /// ErpWeb owns dbo.EInvDocSubmission through
    /// <c>ErpWeb.Model/Entities/Sales/EInvDocSubmission.cs</c> + <c>AppDbContext</c>.
    /// <para>
    /// Kept only because it is the in-repo record of the original column names. It is also <b>narrower
    /// than the live table</b>: Phase 0 found 17 further payload-capture columns (supplierTIN, buyerTIN,
    /// direction, ...) that this class never mapped. Do not re-register it - a second EF owner for the
    /// same table is the drift this consolidation removed.
    /// </para>
    /// </summary>
    [Table("EInvDocSubmission", Schema = "dbo")]
    public partial class EInvDocSubmission
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

        public string? companyID { get; set; }
        public string? documentNo { get; set; }
        public int? documentID { get; set; }
        public string? documentType { get; set; }

    }
}
