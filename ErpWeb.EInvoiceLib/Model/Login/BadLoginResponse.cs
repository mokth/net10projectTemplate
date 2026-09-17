using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.Login
{
    public class BadLoginResponse
    {
        //Possible values: invalid_request, invalid_client, invalid_grant, unauthorized_client, unsupported_grant_type, invalid_scope
        public string error { get; set; }
        //Optional human readable error message containing more details about error encountered.
        public string error_description { get; set; }
        //Optional URI containing more information about the error. Not used in MyInvois System
        public string error_uri { get; set; }
    }
}
