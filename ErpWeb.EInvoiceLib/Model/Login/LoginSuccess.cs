using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.Login
{ /// <summary>
  /// Author     : TH MOK
  /// Created On : 2024-Feb-20
  /// Company    : Wincom IT Solutions SDN BHD.
  /// Description: Inital the API project for Mock test purpose
  /// </summary>
  /// 
    public class LoginSuccess
    {
        //Encoded JWT token structure that contains the fields of the issued token, token protection attributes.
        //type JWT token
        public string access_token { get; set; }
        //Solution in this case returns only Bearer authentication tokens
        public string token_type { get; set; }
        //The lifetime of the access token defined in seconds
        //3600 (means it is valid for one hour)
        public int expires_in { get; set; }
        //Optional if matches the requested scope. Otherwise contains information on scope granted to token. This defines the APIs that client will have access to use this token.
        public string scope { get; set; }
    }
}
