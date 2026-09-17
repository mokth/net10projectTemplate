using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("ivEDocumentFlow", Schema = "dbo")]
    public partial class IvEDocumentFlow
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int id { get; set; }

        public string? docID { get; set; }

        public string? parameter { get; set; }

        public int? value { get; set; }

        public DateTime? activeFrom { get; set; }

        public DateTime? activeTo { get; set; }
    }
}
