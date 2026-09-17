using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("ivENotificationDelivery", Schema = "dbo")]
    public partial class EInvNotificationDelivery
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string? notificationId { get; set; }

        public DateTime? attemptDateTime { get; set; }

        public string? status { get; set; }

        public string? statusDetails { get; set; }
    }
}
