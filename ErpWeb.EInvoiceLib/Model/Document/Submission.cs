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

    /// <summary>
    /// This API allows caller to get details of a single submission to check its processing status after initially submitting it and getting back unique submission identifier.
    // This API is available to submitter only as it might contain documents issued to multiple receivers.
    /// </summary>
    public class Submission
    {
        public string submissionUid { get; set; }
        public int? documentCount { get; set; }
        public DateTime? dateTimeReceived { get; set; }
        public string overallStatus { get; set; }
        public List<DocumentSummary> documentSummary { get; set; }
    }

    public class DocumentSummary
    {

        //Total sales amount of the document in MYR.
        public decimal? totalExcludingTax { get; set; }
        //Total discount amount of the document in MYR.
        public decimal? totalDiscount { get; set; }

        //Total net amount of the document in MYR.
        //totalNetAmount from Json
        public decimal? totalNetAmount { get; set; }
        //Total amount of the document in MYR.
        public decimal? totalPayableAmount { get; set; }
        //Unique document ID in e-Invoice
        public string? uuid { get; set; }

        //Unique ID of the submission the document was part of
        public string? submissionUid { get; set; }

        //Unique long temporary Id that can be used to query document data anonymously. The long id will be returned only for valid documents
        public string? longId { get; set; }

        //Internal ID used in submission for the document
        public string? internalId { get; set; }

        //Unique name of the document type that can be used in submission of the documents. eg invoice
        public string? typeName { get; set; }

        //Name of the document type version within the document type that can be used in document submission to identify document type version being submitted
        public string? typeVersionName { get; set; }
        public string? supplierTIN { get; set; }
        public string? supplierName { get; set; }
        public string? buyerName { get; set; }
        public string? buyerTIN { get; set; }
        //Optional: receiver registration number (can be national ID or foreigner ID).
        public string? receiverId { get; set; }
        public string? receiverTIN { get; set; }
        //Optional: receiver name (can be company name or person’s name)
        public string? receiverName { get; set; }
        //Document recipient identifier type. Only can be used when ‘Direction’ filter is set to Sent. Possible values: (BRN, PASSPORT, NRIC, ARMY,) This is mandatory in case the receiverId is provided
        public string? receiverIdType { get; set; }
        //The date and time when the document was issued.
        public DateTime? dateTimeIssued { get; set; }

        //The date and time when the document was submitted.
        public DateTime? dateTimeReceived { get; set; }
        public string? status { get; set; }
        //Refer to the document cancellation that has been initiated by the taxpayer “issuer” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? cancelDateTime { get; set; }

        //Refer to the document rejection request that has been initiated by the taxpayer “receiver” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? rejectRequestDateTime { get; set; }
        //Mandatory: Reason of the cancellation or rejection of the document.
        public string? documentStatusReason { get; set; }
        public string? submissionChannel { get; set; }
        public string? intermediaryName { get; set; }
        public string? intermediaryTIN { get; set; }
        public string? intermediaryROB { get; set; }
        public string? createdByUserId { get; set; }
        //TIN of issuer
        public string? issuerTin { get; set; }
        //Issuer company name
        public string? issuerID { get; set; }
        public string? issuerName { get; set; }
        public string? issuerIDType { get; set; }
        //The date and time when the document passed all validations and moved to the valid state.
        public DateTime? dateTimeValidated { get; set; }
       
        public string? documentCurrency { get; set; }



        //Total sales amount of the document in MYR.
        public decimal? totalSales { get; set; }
        //not use
        public decimal? netAmount { get; set; }      
        
        //not use
        public decimal? total { get; set; }
        //Total discount amount of the document in Original currency.
        public decimal? totalOriginalDiscount { get; set; }

        //Total sales amount of the document in Original currency.
        public decimal? totalOriginalSales { get; set; }

        //Total net amount of the document in Original currency.
        public decimal? netOriginalAmount { get; set; }
        //Total amount of the document in Original currency.
        public decimal? totalOriginal { get; set; }
        //Status of the document - Submitted, Valid, Invalid, Cancelled
       
        //User created the document. Can be ERP ID or User Email
      
        public string? document { get; set; }

    }

    public class DocumentInfo : DocumentSummary
    {

        //Document object containing the original submitted raw document. Document with ‘invalid’ status with not be returned. Details of the invalid document can be fetched by Get Document Details API.
        //public string document { get; set; }
    }

    public class DocumentFullInfo : DocumentSummary
    {

        //Document object containing the original submitted raw document. Document with ‘invalid’ status with not be returned. Details of the invalid document can be fetched by Get Document Details API.
        public string document { get; set; }
    }

    [Serializable]
    public class DocumentValidatation
    {
        //Unique document ID in e-Invoice
        public string uuid { get; set; }

        //Unique ID of the submission the document was part of
        public string submissionUid { get; set; }

        //Unique long temporary Id that can be used to query document data anonymously. The long id will be returned only for valid documents
        public string longId { get; set; }
        public string internalId { get; set; }

        //Unique name of the document type that can be used in submission of the documents. eg invoice
        public string typeName { get; set; }

        //Name of the document type version within the document type that can be used in document submission to identify document type version being submitted
        public string typeVersionName { get; set; }

        //TIN of issuer
        public string issuerTin { get; set; }

        //Issuer company name
        public string issuerName { get; set; }

        //Optional: receiver registration number (can be national ID or foreigner ID).
        public string receiverId { get; set; }

        //Optional: receiver name (can be company name or person’s name)
        public string receiverName { get; set; }

        //The date and time when the document was issued.
        public DateTime? dateTimeIssued { get; set; }

        //The date and time when the document was submitted.
        public DateTime? dateTimeReceived { get; set; }

        //The date and time when the document passed all validations and moved to the valid state.
        public DateTime? dateTimeValidated { get; set; }
        //Total sales amount of the document in MYR.
        public decimal? totalExcludingTax { get; set; }

        //Total discount amount of the document in MYR.
        public decimal? totalDiscount { get; set; }

        //Total net amount of the document in MYR.
        public decimal? totalNetAmount { get; set; }

        //Total amount of the document in MYR.
        public decimal? totalPayableAmount { get; set; }
        //Status of the document - Submitted, Valid, Invalid, Cancelled
        public string status { get; set; }

        public string createdByUserId { get; set; }
        //Mandatory: Reason of the cancellation or rejection of the document.
        public string documentStatusReason { get; set; }
        //Refer to the document cancellation that has been initiated by the taxpayer “issuer” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? cancelDateTime { get; set; }

        //Refer to the document rejection request that has been initiated by the taxpayer “receiver” of the document on the system, will be in UTC format
        //2021-02-25T01:59:10.2095172Z
        public DateTime? rejectRequestDateTime { get; set; }

        public ValidationResults validationResults { get; set; }

    }

    public class ValidationResults
    {
        public string status { get; set; }
        //public DocErrorDetail error { get; set; }
        public List<ValidationStep> validationSteps { get; set; }
    }

    public class ValidationStep
    {
        public string name { get; set; }
        public string status { get; set; }
        //public DocErrorDetail error { get; set; }
        public string error { get; set; }
    }
}
