using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("IvEDocumentType", Schema = "dbo")]
    public partial class ivEDocumentType
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string docID { get; set; }

        public string invoiceTypeCode { get; set; }

        public string description { get; set; }

        public DateTime? activeFrom { get; set; }

        public DateTime? activeTo { get; set; }
    }
}
