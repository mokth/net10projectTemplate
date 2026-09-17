using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("ivEDocumentTypeVersion", Schema = "dbo")]
    public partial class ivEDocumentTypeVersion
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int id { get; set; }

        public string? docID { get; set; }

        public string? docTypeVersion { get; set; }

        public string? name { get; set; }

        public string? description { get; set; }

        public DateTime? activeFrom { get; set; }

        public DateTime? activeTo { get; set; }

        public string? versionNumber { get; set; }

        public string? status { get; set; }
    }
}
