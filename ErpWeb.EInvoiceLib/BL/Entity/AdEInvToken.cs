using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.BL.Entity
{
    [Table("EInvToken", Schema = "dbo")]
    public partial class EInvToken
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string? access_token { get; set; }

        public string? token_type { get; set; }

        public int? expires_in { get; set; }

        public string? scope { get; set; }

        public DateTime? created { get; set; }
    }

    [Table("EInvTokenOwn", Schema = "dbo")]
    public partial class EInvTokenOwn
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int ID { get; set; }

        public string? access_token { get; set; }

        public string? token_type { get; set; }

        public int? expires_in { get; set; }

        public string? scope { get; set; }

        public DateTime? created { get; set; }
    }
}
