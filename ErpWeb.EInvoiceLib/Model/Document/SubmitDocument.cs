using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.Document
{
    /// <summary>
    /// Author     : TH MOK
    /// Created On : 2024-Feb-20
    /// Company    : Wincom IT Solutions SDN BHD.
    /// Description: Inital the API project for Mock test purpose
    /// </summary>

    public class SubmitDocument
    {
        public List<Documents> documents { get; set; }
    }

    public class Documents
    {
        //XML, JSON
        public string format { get; set; }
        //The base64 of the document JSON or XML
        public string document { get; set; }
        //The hash value of the document being submitted
        public string documentHash { get; set; }
        //Document reference number used by Supplier for internal tracking purpose
        //eg INV12345, CN23456, DN34567
        public string codeNumber { get; set; }

    }

    public class SuccessSubmit
    {
        public string submissionUID { get; set; }

        public string? companyID { get; set; }
        public string? documentNo { get; set; }
        public int? documentID { get; set; }

        public List<AcceptedDocuments> acceptedDocuments { get; set; }

        public List<RejectedDocuments> rejectedDocuments { get; set; }
    }

    public class AcceptedDocuments
    {
        //Unique document ID assigned by e-Invoice. 26 Latin alphanumeric symbols.
        public string uuid { get; set; }

        //Document reference number used by Supplier for internal tracking purpose
        //eg INV12345, CN23456, DN34567
        public string invoiceCodeNumber { get; set; }
    }

    public class RejectedDocuments
    {
        //Document reference number used by Supplier for internal tracking purpose
        //eg INV12345, CN23456, DN34567
        public string invoiceCodeNumber { get; set; }
        public ErrorRespone error { get; set; }
    }
}
