using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class SubmitInfo
    {
        public DateTime submitOn { get; set; }
        public string docNo { get; set; }
        public string docType { get; set; }
        public string custCode { get; set; }
        public string userId { get; set; }
        public string compCode { get; set; }
        public string branchCode { get; set; }
        public string locCode { get; set; }
        public string oriInvNo { get; set; }
        public string oriInvUUID { get; set; }
    }
}
