using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Utility;

namespace ErpWeb.EInvoiceLib.Model.Document
{
    public class RecentDocument
    {
        //public List<RecentDocumentInfo> result { get; set; }
        public List<DocumentSummary> result { get; set; }
        public Metadata metadata { get; set; }
    }
    public class Metadata
    {
        public int totalPages { get; set; }
        public int totalCount { get; set; }
    }
    public class RecentDocumentInfo
    {
        //Unique document ID in e-Invoice
        public string uuid { get; set; }

        //Unique ID of the submission that document was part of
        public string submissionUUID { get; set; }

        //Unique long temporary Id that can be used to query document data anonymously	
        public string longId { get; set; }

        //Internal ID used in submission for the document
        public string internalId { get; set; }

        //Unique name of the document type that can be used in submission of the documents.	
        public string typeName { get; set; }

        //Name of the document type version within the document type that can be used in document submission to identify document type version being submitted	1
        public string typeVersionName { get; set; }

        //TIN of issuer
        public string issuerTin { get; set; }

        //Issuer company name
        public string issuerName { get; set; }

        //Optional: receiver registration number (can be national ID or foreigner ID).
        //BRN example: 201901234567 
        //NRIC example: 770625015324
        //Passport number example: A12345678
        //Army number example: 551587706543
        public string receiverId { get; set; }

        //Optional: receiver name (can be company name or person’s name)
        public string receiverName { get; set; }

        //The date and time when the document was issued. 2015-02-13T14:20Z
        public DateTime? dateTimeIssued { get; set; }

        //The date and time when the document was submitted.  2015-02-13T14:20Z
        public DateTime? dateTimeReceived { get; set; }

        //The date and time when the document passed all validations and moved to the valid state. 2015-02-13T14:20Z
        public DateTime? dateTimeValidated { get; set; }

        //Total sales amount of the document in MYR.
        public decimal? totalSales { get; set; }

        //Total discount amount of the document in MYR.
        public decimal? totalDiscount { get; set; }

        //Total net amount of the document in MYR.
        public decimal? netAmount { get; set; }

        //Total amount of the document in MYR.
        public decimal? total { get; set; }

        //Status of the document - Submitted, Valid, Invalid, Cancelled
        public string status { get; set; }

        //Refer to the document cancellation that has been initiated by the taxpayer “issuer” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? cancelDateTime { get; set; }

        //Refer to the document rejection request that has been initiated by the taxpayer “receiver” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? rejectRequestDateTime { get; set; }

        //Mandatory: Reason of the cancellation or rejection of the document.
        public string documentStatusReason { get; set; }

        //User created the document. Can be ERP ID or User Email
        public string createdByUserId { get; set; }

        //Optional: Document recipient identifier type. Only can be used when ‘Direction’ filter is set to Sent. Possible values: (BRN, PASSPORT, NRIC, ARMY,) This is mandatory in case the receiverId is provided	
        public string receiverIdType { get; set; }

    }

    public class FoundDocument : RecentDocument
    {
        //Optional: Document recipient identifier type. Only can be used when ‘Direction’ filter is set to Sent. Possible values: (BRN, PASSPORT, NRIC, ARMY,) This is mandatory in case the receiverId is provided
        public string receiverIdType { get; set; }
    }


    public class RecentDocumentInput
    {
        //Optional: number of the page to retrieve. Typically this parameter value is derived from initial parameter less call when caller learns total amount of page of certain size
        public int? pageNo { get; set; }

        //Optional: number of the documents to retrieve per page. Page size cannot exceed system configured maximum page size for this API	
        public int? pageSize { get; set; }

        //Optional: The start date and time when the document was submitted to the e-Invoice API, Time to be supplied in UTC timezone. Mandatory when ‘submissionDateTo’ is provided	2022-11-
        public DateTime? submissionDateFrom { get; set; }

        //Optional: The end date and time when the document was submitted to the e-Invoice API, Time to be supplied in UTC timezone. Mandatory when ‘submissionDateFrom’ is provided
        public DateTime? submissionDateTo { get; set; }

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

        //Optional: Document recipient TIN. Only can be used when ‘Direction’ filter is set to Sent.
        public string receiverTin { get; set; }

        //Optional: Document issuer identifier. Only can be used when ‘Direction’ filter is set to Received.
        public string issuerTin { get; set; }

        public string getQueryString()
        {
            //GET / api / v1.0 / documents / recent ?
            //pageNo ={ pageNo}
            //&pageSize ={ pageSize}
            //&submissionDateFrom ={ submissionDateFrom}
            //&submissionDateTo ={ submissionDateTo}
            //&issueDateFrom ={ issueDateFrom}
            //&issueDateTo ={ IssueDateTo}
            //&direction ={ direction}
            //&status ={ status}
            //&documentType ={ documentType}
            //&receiverIdType ={ receiverIdType}
            //&receiverId ={ receiverId}
            //&issuerIdType ={ issuerIdType}
            //&receiverTin ={ receiverTin}
            //&issuerTin ={ issuerTin}
            string query = "";
            if (pageNo.HasValue)
            {
                query = query + "pageNo=" + pageNo.Value.ToString() + "&";
            }
            if (pageSize.HasValue)
            {
                query = query + "pageNo=" + pageSize.Value.ToString() + "&";
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
                query = query + "direction=" + direction + "&";
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
            if (!string.IsNullOrEmpty(receiverTin))
            {
                query = query + "receiverTin=" + receiverTin + "&";
            }
            if (!string.IsNullOrEmpty(issuerTin))
            {
                query = query + "issuerTin=" + issuerTin + "&";
            }

            if (query.Length > 0)
            {
                query = query.Substring(0, query.Length - 1);
            }

            return query;
        }
    }
}
