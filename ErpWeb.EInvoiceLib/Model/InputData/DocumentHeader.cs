using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model.InputData
{

    public enum EInvoiceDocumentType
    {
        invoice,
        creditnote,
        debitnote,
        sb_invoice,
        sb_creditnote,
        sb_debitnote,
    }

    public enum SubmissionFormat
    {
        JSON,
        XML
    }

    public enum InvoicePeriodEnum
    {
        Daily, Weekly, Biweekly, Monthly, Bimonthly, Quarterly, Half_Yearly, Yearly, Others, Not_Applicable
    }

    public enum TINRegistrationType
    {
        NRIC, PASSPORT, BRN, ARMY
    }

    //public class DocumentHeader
    //{
    //    public string DocumentVersion { get; set; }
    //    /// <summary>
    //    /// Invoice No, Credit or Debit No number
    //    /// </summary>
    //    public string DocumentNo { get; set; }
    //    /// <summary>
    //    /// For Credit Or Debit Note, this is the Origin Invoice No, For Inovice it may be PO or DO number
    //    /// </summary>
    //    public string RefDocumentNo { get; set; }
    //    /// <summary>
    //    /// For Credit Or Debit Note only, this is the Origin Invoice UUID
    //    /// </summary>
    //    public string OriginInvoiceUUID { get; set; }
    //    /// <summary>
    //    /// Date time when submitting
    //    /// </summary>
    //    public DateTime? IssueDate { get; set; }
    //    /// <summary>
    //    /// invoice, creditnote or debitnote        
    //    /// </summary>
    //    public EInvoiceDocumentType docType { get; set; }

    //    public InvoicePeriodEnum invoicePeriod { get; set; }
    //    /// <summary>
    //    /// Curreny code follow LDHN, 3 char
    //    /// </summary>
    //    public string Currency { get; set; }
    //    /// <summary>
    //    /// Curreny code follow LDHN, 3 char
    //    /// </summary>
    //    public string ForeignCurrency { get; set; }
    //    /// <summary>
    //    /// Only if Foreign Currency
    //    /// </summary>
    //    public double? ExchangeRate { get; set; }
    //    /// <summary>
    //    /// the Tax payer info, the party who submit the invoice
    //    /// </summary>
    //    public PartyInfo Supplier { get; set; }
    //    /// <summary>
    //    /// the Customer info
    //    /// </summary>
    //    public PartyInfo Customer { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable (inclusive of applicable discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? AmountIncTax { get; set; }
    //    /// <summary>
    //    /// Total Tax Amount at header level
    //    /// </summary>
    //    public double? TaxAmount { get; set; }
    //    /// <summary>
    //    /// Total amount of tax payable
    //    /// </summary>
    //    public double? DiscountAmount { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable inclusive of total taxes chargeable (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? AmountExlTax { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable (inclusive of total taxes chargeable and any rounding adjustment) excluding any amount paid in advance.
    //    /// </summary>
    //    public double? TotalPayableAmount { get; set; }
    //    /// <summary>
    //    /// Sum of total amount payable (inclusive of applicable line item and invoice level discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? TotalNetAmount { get; set; }
    //    /// <summary>
    //    /// Item Lines
    //    /// </summary>
    //    public List<DocumentDetail> documentDetails { get; set; }
    //    /// <summary>
    //    /// Tax info, for Consolidation usage
    //    /// </summary>
    //    public List<DocumentDetailTax> DetailTaxs { get; set; }
    //}

    public class DocumentHeader
    {
        public string DocumentVersion { get; set; }
        /// <summary>
        /// Invoice No, Credit or Debit No number
        /// </summary>
        public string DocumentNo { get; set; }
        /// <summary>
        /// For Credit Or Debit Note, this is the Origin Invoice No, For Inovice it may be PO or DO number
        /// </summary>
        public string RefDocumentNo { get; set; }
        /// <summary>
        /// For Credit Or Debit Note only, this is the Origin Invoice UUID
        /// </summary>
        public string OriginInvoiceUUID { get; set; }
        /// <summary>
        /// Date time when submitting
        /// </summary>
        public DateTime? IssueDate { get; set; }
        /// <summary>
        /// invoice, creditnote or debitnote        
        /// </summary>
        public EInvoiceDocumentType docType { get; set; }
        /// <summary>
        /// Curreny code follow LDHN, 3 char
        /// </summary>
        public string Currency { get; set; }
        /// <summary>
        /// Curreny code follow LDHN, 3 char
        /// </summary>
        public string ForeignCurrency { get; set; }
        /// <summary>
        /// Only if Foreign Currency
        /// </summary>
        public double? ExchangeRate { get; set; }
        /// <summary>
        /// the Tax payer info, the party who submit the invoice
        /// </summary>
        public PartyInfo Supplier { get; set; }
        /// <summary>
        /// the Customer info
        /// </summary>
        public PartyInfo Customer { get; set; }
        /// <summary>
        /// Sum of amount payable (inclusive of applicable discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
        /// </summary>
        public double? AmountIncTax { get; set; }
        /// <summary>
        /// Total Tax Amount at header level
        /// </summary>
        public double? TaxAmount { get; set; }
        /// <summary>
        /// Total amount of tax payable
        /// </summary>
        public double? DiscountAmount { get; set; }
        /// <summary>
        /// Sum of amount payable inclusive of total taxes chargeable (e.g., sales tax, service tax).
        /// </summary>
        public double? AmountExlTax { get; set; }
        /// <summary>
        /// Sum of amount payable (inclusive of total taxes chargeable and any rounding adjustment) excluding any amount paid in advance.
        /// </summary>
        public double? TotalPayableAmount { get; set; }
        /// <summary>
        /// Sum of total amount payable (inclusive of applicable line item and invoice level discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
        /// </summary>
        public double? TotalNetAmount { get; set; }

        public double? AdditionalDiscountAmount { get; set; }
        /// <summary>
        /// Item Lines
        /// </summary>
        public List<DocumentDetail> documentDetails { get; set; }
        /// <summary>
        /// Tax info, for Consolidation usage
        /// </summary>
        public List<DocumentDetailTax> DetailTaxs { get; set; }

        public string InvoicePeriodStartDate { get; set; }
        public string InvoicePeriodEndDate { get; set; }
        public string FrequencyOfBilling { get; set; } // Daily, Weekly, Biweekly, Monthly, Bimonthly, Quarterly, Half-yearly, Yearly, Others 
    }

    public class PartyInfo
    {
        public string IndustryClassificationCode { get; set; }
        public string BizDesciption { get; set; }
        public string CompanyName { get; set; }
        public string TinNo { get; set; }

        /// <summary>
        // NRIC,PASSPORT,BRN,ARMY
        /// </summary>
        public TINRegistrationType RegType { get; set; }

        //NRIC,PASSPORT,BRN,ARMY number
        public string RegNo { get; set; }
        public string SSTNo { get; set; }
        public string Addr1 { get; set; }
        public string Addr2 { get; set; }
        public string Addr3 { get; set; }
        public string Addr4 { get; set; }
        public string CityName { get; set; }
        /// <summary>
        /// CountryCode follow LHDN country code
        /// </summary>
        public string CountryCode { get; set; }
        /// <summary>
        /// StateCode follow LHDN state code
        /// </summary>
        public string StateCode { get; set; }
        public string PostalCode { get; set; }
        public string Email { get; set; }
        public string PhoneNo { get; set; }
    }

    //public class DocumentDetail
    //{

    //    /// <summary>
    //    /// Line no
    //    /// When doing consolidation, the line is the invoice number
    //    /// </summary>
    //    public string Line { get; set; }
    //    public double? Qty { get; set; }
    //    /// <summary>
    //    /// No use in submission, only for display error messgae 
    //    /// </summary>
    //    public string ItemCode { get; set; }
    //    /// <summary>
    //    /// Item Description
    //    /// </summary>
    //    public string ItemDesc { get; set; }
    //    /// <summary>
    //    /// UOM follow LHDN UOM code
    //    /// </summary>
    //    public string UOM { get; set; }
    //    /// <summary>
    //    /// Item Unit price
    //    /// When doing consolidation, the Price is the total Amount of that invoice
    //    /// </summary>
    //    public double? UnitPrice { get; set; }
    //    /// <summary>
    //    /// Amount Include tax at item level
    //    /// </summary>
    //    public double? AmountIncTax { get; set; }
    //    /// <summary>
    //    /// Amount Exclude tax at item level
    //    /// </summary>
    //    public double? AmountExclTax { get; set; }
    //    /// <summary>
    //    /// Discount Amount at item level
    //    /// </summary>
    //    public double? DiscountAmount { get; set; }
    //    /// <summary>
    //    /// Tax Amount at item level
    //    /// </summary>
    //    public double? TaxAmount { get; set; }
    //    /// <summary>
    //    /// Tax Type ,LHDN Tax Type
    //    /// </summary>
    //    public string TaxType { get; set; }
    //    public string TaxExemptedReason { get; set; }
    //    /// <summary>
    //    /// Tax Percent
    //    /// </summary>
    //    public double? TaxPerCent { get; set; }

    //    /// <summary>
    //    ///  Amount of each individual item / service within the invoice, excluding any taxes, charges or discounts
    //    /// </summary>
    //    public double? ItemPriceExtensionAmount { get; set; }
    //    /// <summary>
    //    /// Category of products or services being billed as a result of a commercial transaction
    //    /// </summary>
    //    public string ClassificationCode { get; set; }

    //    // public string ClassificationDesc { get; set; }

    //    /// <summary>
    //    /// Tax details, for Consolidate usage
    //    /// </summary>
    //    public List<DocumentDetailTax> DetailTaxs { get; set; }

    //}

    public class DocumentDetail
    {

        /// <summary>
        /// Line no
        /// When doing consolidation, the line is the invoice number
        /// </summary>
        public string Line { get; set; }
        public double? Qty { get; set; }
        /// <summary>
        /// No use in submission, only for display error messgae 
        /// </summary>
        public string ItemCode { get; set; }
        /// <summary>
        /// Item Description
        /// </summary>
        public string ItemDesc { get; set; }
        /// <summary>
        /// UOM follow LHDN UOM code
        /// </summary>
        public string UOM { get; set; }
        /// <summary>
        /// Item Unit price
        /// When doing consolidation, the Price is the total Amount of that invoice
        /// </summary>
        public double? UnitPrice { get; set; }
        /// <summary>
        /// Amount of each individual item / service within the invoice, excluding any taxes, charges or discounts
        /// </summary>
        public double? GrossAmount { get; set; }
        /// <summary>
        /// Amount Include tax at item level
        /// </summary>
        public double? AmountIncTax { get; set; }
        /// <summary>
        /// Amount Exclude tax at item level
        /// </summary>
        public double? AmountExclTax { get; set; }
        /// <summary>
        /// Discount Amount at item level
        /// </summary>
        public double? DiscountAmount { get; set; }
        /// <summary>
        /// Tax Amount at item level
        /// </summary>
        public double? TaxAmount { get; set; }
        /// <summary>
        /// Tax Type ,LHDN Tax Type
        /// </summary>
        public string TaxType { get; set; }
        /// <summary>
        /// Tax Percent
        /// </summary>
        public double? TaxPerCent { get; set; }
        /// <summary>
        /// Category of products or services being billed as a result of a commercial transaction
        /// </summary>
        public string ClassificationCode { get; set; }

        // public string ClassificationDesc { get; set; }

        /// <summary>
        /// Tax details, for Consolidate usage
        /// </summary>
        public List<DocumentDetailTax> DetailTaxs { get; set; }

    }


    public class DocumentDetailTax
    {
        /// <summary>
        /// Amount use for the Tax Calculation 
        /// </summary>
        public double? TaxAbleAmount { get; set; }
        /// <summary>
        /// Tax amount
        /// </summary>
        public double? TaxAmount { get; set; }
        /// <summary>
        /// Tax Type ,LHDN Tax Type
        /// </summary>
        public string TaxType { get; set; }
        /// <summary>
        /// Tax percent
        /// </summary>
        public double? TaxPerCent { get; set; }

        public string TaxExemptedReason { get; set; }

    }


    //public enum EInvoiceDocumentType
    //{
    //    invoice,
    //    creditnote,
    //    debitnote
    //}

    //public enum TINRegistrationType
    //{
    //    NRIC, PASSPORT, BRN, ARMY
    //}

    //public class DocumentHeader
    //{
    //    public string DocumentVersion { get; set; }
    //    /// <summary>
    //    /// Invoice No, Credit or Debit No number
    //    /// </summary>
    //    public string DocumentNo { get; set; }
    //    /// <summary>
    //    /// For Credit Or Debit Note, this is the Origin Invoice No, For Inovice it may be PO or DO number
    //    /// </summary>
    //    public string RefDocumentNo { get; set; }
    //    /// <summary>
    //    /// Date time when submitting
    //    /// </summary>
    //    public DateTime? IssueDate { get; set; }
    //    public string InvoicePeriodStartDate { get; set; }
    //    public string InvoicePeriodEndDate { get; set; }
    //    /// <summary>
    //    /// invoice, creditnote or debitnote        
    //    /// </summary>
    //    public EInvoiceDocumentType docType { get; set; }
    //    /// <summary>
    //    /// Curreny code follow LDHN, 3 char
    //    /// </summary>
    //    public string Currency { get; set; }
    //    /// <summary>
    //    /// Curreny code follow LDHN, 3 char
    //    /// </summary>
    //    public string ForeignCurrency { get; set; }
    //    /// <summary>
    //    /// Only if Foreign Currency
    //    /// </summary>
    //    public double? ExchangeRate { get; set; }
    //    /// <summary>
    //    /// the Tax payer info, the party who submit the invoice
    //    /// </summary>
    //    public PartyInfo Supplier { get; set; }
    //    /// <summary>
    //    /// the Customer info
    //    /// </summary>
    //    public PartyInfo Customer { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable (inclusive of applicable discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? AmountIncTax { get; set; }
    //    /// <summary>
    //    /// Total Tax Amount at header level
    //    /// </summary>
    //    public double? TaxAmount { get; set; }
    //    /// <summary>
    //    /// Total amount of tax payable
    //    /// </summary>
    //    public double? DiscountAmount { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable inclusive of total taxes chargeable (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? AmountExlTax { get; set; }
    //    /// <summary>
    //    /// Sum of amount payable (inclusive of total taxes chargeable and any rounding adjustment) excluding any amount paid in advance.
    //    /// </summary>
    //    public double? TotalPayableAmount { get; set; }
    //    /// <summary>
    //    /// Sum of total amount payable (inclusive of applicable line item and invoice level discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax).
    //    /// </summary>
    //    public double? TotalNetAmount { get; set; }
    //    /// <summary>
    //    /// Item Lines
    //    /// </summary>
    //    public List<DocumentDetail> documentDetails { get; set; }
    //    /// <summary>
    //    /// Tax info, for Consolidation usage
    //    /// </summary>
    //    public List<DocumentDetailTax> DetailTaxs { get; set; }
    //}

    //public class PartyInfo
    //{
    //    public string IndustryClassificationCode { get; set; }
    //    public string BizDesciption { get; set; }
    //    public string CompanyName { get; set; }
    //    public string TinNo { get; set; }

    //    /// <summary>
    //    // NRIC,PASSPORT,BRN,ARMY
    //    /// </summary>
    //    public TINRegistrationType RegType { get; set; }

    //    //NRIC,PASSPORT,BRN,ARMY number
    //    public string RegNo { get; set; }
    //    public string SSTNo { get; set; }
    //    public string Addr1 { get; set; }
    //    public string Addr2 { get; set; }
    //    public string Addr3 { get; set; }
    //    public string Addr4 { get; set; }
    //    public string CityName { get; set; }
    //    /// <summary>
    //    /// CountryCode follow LHDN country code
    //    /// </summary>
    //    public string CountryCode { get; set; }
    //    /// <summary>
    //    /// StateCode follow LHDN state code
    //    /// </summary>
    //    public string StateCode { get; set; }
    //    public string PostalCode { get; set; }
    //    public string Email { get; set; }
    //    public string PhoneNo { get; set; }
    //}

    //public class DocumentDetail
    //{

    //    /// <summary>
    //    /// Line no
    //    /// When doing consolidation, the line is the invoice number
    //    /// </summary>
    //    public string Line { get; set; }
    //    public double? Qty { get; set; }
    //    /// <summary>
    //    /// No use in submission, only for display error messgae 
    //    /// </summary>
    //    public string ItemCode { get; set; }
    //    /// <summary>
    //    /// Item Description
    //    /// </summary>
    //    public string ItemDesc { get; set; }
    //    /// <summary>
    //    /// UOM follow LHDN UOM code
    //    /// </summary>
    //    public string UOM { get; set; }
    //    /// <summary>
    //    /// Item Unit price
    //    /// When doing consolidation, the Price is the total Amount of that invoice
    //    /// </summary>
    //    public double? UnitPrice { get; set; }
    //    /// <summary>
    //    /// Amount Include tax at item level
    //    /// </summary>
    //    public double? GrossAmount { get; set; }
    //    /// <summary>
    //    /// Amount Include tax at item level
    //    /// </summary>
    //    public double? AmountIncTax { get; set; }
    //    /// <summary>
    //    /// Amount Exclude tax at item level
    //    /// </summary>
    //    public double? AmountExclTax { get; set; }
    //    /// <summary>
    //    /// Discount Amount at item level
    //    /// </summary>
    //    public double? DiscountAmount { get; set; }
    //    /// <summary>
    //    /// Tax Amount at item level
    //    /// </summary>
    //    public double? TaxAmount { get; set; }
    //    /// <summary>
    //    /// Tax Type ,LHDN Tax Type
    //    /// </summary>
    //    public string TaxType { get; set; }
    //    /// <summary>
    //    /// Tax Percent
    //    /// </summary>
    //    public double? TaxPerCent { get; set; }
    //    /// <summary>
    //    /// Category of products or services being billed as a result of a commercial transaction
    //    /// </summary>
    //    public string ClassificationCode { get; set; }

    //    // public string ClassificationDesc { get; set; }

    //    /// <summary>
    //    /// Tax details, for Consolidate usage
    //    /// </summary>
    //    public List<DocumentDetailTax> DetailTaxs { get; set; }

    //}

    //public class DocumentDetailTax
    //{
    //    /// <summary>
    //    /// Amount use for the Tax Calculation 
    //    /// </summary>
    //    public double? TaxAbleAmount { get; set; }
    //    /// <summary>
    //    /// Tax amount
    //    /// </summary>
    //    public double? TaxAmount { get; set; }
    //    /// <summary>
    //    /// Tax Type ,LHDN Tax Type
    //    /// </summary>
    //    public string TaxType { get; set; }
    //    /// <summary>
    //    /// Tax percent
    //    /// </summary>
    //    public double? TaxPerCent { get; set; }
    //}
}
