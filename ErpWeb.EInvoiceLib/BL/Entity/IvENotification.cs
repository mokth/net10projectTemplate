using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("EInvNotification", Schema = "dbo")]
    public partial class EInvNotification
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string? notificationId { get; set; }

        public DateTime? receivedDateTime { get; set; }

        public DateTime? deliveredDateTime { get; set; }

        public string? typeId { get; set; }

        public string? typeName { get; set; }

        public string? finalMessage { get; set; }

        public string? channel { get; set; }

        public string? address { get; set; }

        public string? language { get; set; }

        public string? status { get; set; }

        public int? totalPages { get; set; }

        public int? totalCount { get; set; }
    }
}
