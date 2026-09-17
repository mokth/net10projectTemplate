using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.Document
{
    /// <summary>
    /// Author     : TH MOK
    /// Created On : 2024-Feb-20
    /// Company    : Wincom IT Solutions SDN BHD.
    /// Description: Inital the API project for Mock test purpose
    /// </summary>
    public class CancelDocument
    {
        //Desired status for the document. Must be cancelled to cancel previously issued document.	
        public string status { get; set; }

        //Reason for cancelling the document.
        //eg Customer cancelled the order. Reasons to be used as follows Wrong buyer details or Wrong invoice details
        public string reason { get; set; }


        //this now use in E-Invoice
        [JsonIgnore]
        public string docType { get; set; }
    }

    public class CancelRespone
    {
        //Unique ID of the document
        public string uuid { get; set; }

        //Status if document has been cancelled eg Cancelled
        public string status { get; set; }

        //Error if cancellion of document failed.
        public ErrorRespone error { get; set; }
    }
}
