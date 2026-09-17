using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Utility;

namespace ErpWeb.EInvoiceLib.Model.Document
{
    public class TINInfo
    {
        public string tin { get; set; }
    }

    public class SearchTINInput
    {
        public string taxpayerName { get; set; }
        public string idType { get; set; }
        public string idValue { get; set; }

        public string getQueryString()
        {
            string query = "";

            if (!string.IsNullOrEmpty(taxpayerName))
            {
                query = query + "taxpayerName=" + taxpayerName + "&";
            }
            if (!string.IsNullOrEmpty(idType))
            {
                query = query + "idType=" + idType + "&";
            }

            if (!string.IsNullOrEmpty(idValue))
            {
                query = query + "idValue=" + idValue + "&";
            }

            if (query.Length > 0)
            {
                query = query.Substring(0, query.Length - 1);
            }
            return query;
        }
    }


    public class SearchDocumentInput
    {
        public int? pageNo { get; set; }

        //Optional: number of the documents to retrieve per page. Page size cannot exceed system configured maximum page size for this API	
        public int? pageSize { get; set; }
        //Unique ID of the document to retrieve.
        public string uuid { get; set; }
        //Optional: The start date and time when the document was submitted to the e-Invoice API, Time to be supplied in UTC timezone. Mandatory when ‘submissionDateTo’ is provided	2022-11-
        public DateTime? submissionDateFrom { get; set; }

        //Optional: The end date and time when the document was submitted to the e-Invoice API, Time to be supplied in UTC timezone. Mandatory when ‘submissionDateFrom’ is provided
        public DateTime? submissionDateTo { get; set; }

        //Optional: Token provided to navigate to the next page.Must be omitted or use an empty string when requesting the first page.
        public string continuationToken { get; set; }

        //Optional: number of the documents to retrieve per page. Page size cannot exceed system configured maximum page size for this API. Default is 100


        //Optional: The start date and time when the document was issued. Mandatory when ‘issueDateTo’ is provided
        public DateTime? issueDateFrom { get; set; }

        //Optional: The end date and time when the document was issued. Mandatory when ‘issueDateFrom’ is provided
        public DateTime? issueDateTo { get; set; }

        //Optional: direction of the document. Possible values: (Sent, Received)
        public string direction { get; set; }


        //Optional: status of the document. Possible values: (Valid, Invalid, Cancelled, Submitted)
        public string status { get; set; }

        //Optional: Document type code.  eg 01
        public string documentType { get; set; }

        //Optional: receiver registration number (can be national ID or foreigner ID).
        //BRN example: 201901234567 
        //NRIC example: 770625015324
        //Passport number example: A12345678
        //Army number example: 551587706543
        public string receiverId { get; set; }

        //Optional: Document recipient identifier type. Only can be used when ‘Direction’ filter is set to Sent. Possible values: (BRN, PASSPORT, NRIC, ARMY) This is mandatory in case the receiverId is provided
        public string receiverIdType { get; set; }

        //Optional: Document issuer identifier. Only can be used when ‘Direction’ filter is set to Received.
        public string issuerTin { get; set; }
        public string searchQuery { get; set; }


        public string getQueryString()
        {
            //GET / api / v1.0 / documents / search ?
            //&submissionDateFrom ={ submissionDateFrom}
            //&submissionDateTo ={ submissionDateTo}
            //&continuationToken ={ continuationToken}
            //&pageSize ={ pageSize}
            //&issueDateFrom ={ issueDateFrom}
            //&issueDateTo ={ issueDateTo}
            //&direction ={ direction}
            //&status ={ status}
            //&documentType ={ documentType}
            //&receiverId ={ receiverId}
            //&receiverIdType ={ receiverIdType}
            //&issuerTin ={ issuerTin}
            string query = "";

            if (!string.IsNullOrEmpty(uuid))
            {
                query = query + "uuid=" + uuid + "&";
            }
            if (pageNo.HasValue)
            {
                query = query + "pageNo=" + pageNo.Value.ToString() + "&";
            }
            if (submissionDateFrom.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDateTime(submissionDateFrom.Value);
                query = query + "submissionDateFrom=" + json + "&";
            }
            if (submissionDateTo.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDateTime(submissionDateTo.Value);
                query = query + "submissionDateTo=" + json + "&";
            }
            if (!string.IsNullOrEmpty(continuationToken))
            {
                query = query + "continuationToken=" + continuationToken + "&";
            }
            if (issueDateFrom.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDateTime(issueDateFrom.Value);
                query = query + "issueDateFrom=" + json + "&";
            }
            if (issueDateTo.HasValue)
            {
                var json = JsonDateTimeUtil.getJsonDateTime(issueDateTo.Value);
                query = query + "issueDateTo=" + json + "&";
            }
            if (!string.IsNullOrEmpty(direction))
            {
                query = query + "invoiceDirection=" + direction + "&";
            }
            if (!string.IsNullOrEmpty(status))
            {
                query = query + "status=" + status + "&";
            }
            if (!string.IsNullOrEmpty(documentType))
            {
                query = query + "documentType=" + documentType + "&";
            }
            if (!string.IsNullOrEmpty(receiverId))
            {
                query = query + "receiverId=" + receiverId + "&";
            }
            if (!string.IsNullOrEmpty(receiverIdType))
            {
                query = query + "receiverIdType=" + receiverIdType + "&";
            }

            if (!string.IsNullOrEmpty(issuerTin))
            {
                query = query + "issuerTin=" + issuerTin + "&";
            }
            if (!string.IsNullOrEmpty(searchQuery))
            {
                query = query + "searchQuery=" + searchQuery + "&";
            }
            if (query.Length > 0)
            {
                query = query.Substring(0, query.Length - 1);
            }

            return query;
        }
    }
}
