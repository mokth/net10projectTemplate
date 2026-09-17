using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.Login
{
    public class JWTTokenInfo
    {
        //for now unsure yet
        public string id { get; set; }
        public string auth_token { get; set; }
        public double expires_in { get; set; }
    }
}
