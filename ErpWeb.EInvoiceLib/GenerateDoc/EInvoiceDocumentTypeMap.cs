using System;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.EInvoiceLib.GenerateDoc
{
    /// <summary>
    /// Additive mapping between the ERP document type enum and the LHDN MyInvois document
    /// type code / UBL namespace it must be emitted with.
    /// <para>
    /// This type only supplies constant lookup values; it does not change any existing
    /// generation, hashing or signing behaviour. Invoice type 01 continues to be produced
    /// by <see cref="GenerateInvoice"/> exactly as before.
    /// </para>
    /// </summary>
    public static class EInvoiceDocumentTypeMap
    {
        public const string Invoice = "01";
        public const string CreditNote = "02";
        public const string DebitNote = "03";
        public const string RefundNote = "04";
        public const string SelfBilledInvoice = "11";
        public const string SelfBilledCreditNote = "12";
        public const string SelfBilledDebitNote = "13";
        public const string SelfBilledRefundNote = "14";

        public const string InvoiceNamespace = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
        public const string CreditNoteNamespace = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
        public const string DebitNoteNamespace = "urn:oasis:names:specification:ubl:schema:xsd:DebitNote-2";
        public const string RefundNoteNamespace = "urn:oasis:names:specification:ubl:schema:xsd:RefundNote-2";

        /// <summary>
        /// LHDN document type code for the ERP document type.
        /// </summary>
        public static string GetDocumentTypeCode(EInvoiceDocumentType docType)
        {
            switch (docType)
            {
                case EInvoiceDocumentType.invoice:
                    return Invoice;
                case EInvoiceDocumentType.creditnote:
                    return CreditNote;
                case EInvoiceDocumentType.debitnote:
                    return DebitNote;
                case EInvoiceDocumentType.sb_invoice:
                    return SelfBilledInvoice;
                case EInvoiceDocumentType.sb_creditnote:
                    return SelfBilledCreditNote;
                case EInvoiceDocumentType.sb_debitnote:
                    return SelfBilledDebitNote;
                default:
                    throw new ArgumentOutOfRangeException(nameof(docType), docType, "Unsupported e-Invoice document type.");
            }
        }

        /// <summary>
        /// UBL root namespace that must wrap the document for the given type code.
        /// </summary>
        public static string GetDocumentNamespace(string documentTypeCode)
        {
            switch (documentTypeCode)
            {
                case Invoice:
                case SelfBilledInvoice:
                    return InvoiceNamespace;
                case CreditNote:
                case SelfBilledCreditNote:
                    return CreditNoteNamespace;
                case DebitNote:
                case SelfBilledDebitNote:
                    return DebitNoteNamespace;
                case RefundNote:
                case SelfBilledRefundNote:
                    return RefundNoteNamespace;
                default:
                    throw new ArgumentOutOfRangeException(nameof(documentTypeCode), documentTypeCode, "Unsupported e-Invoice document type code.");
            }
        }

        /// <summary>
        /// XAdES signature identifier UBL expects for the given type code.
        /// </summary>
        public static string GetSignatureId(string documentTypeCode)
        {
            switch (documentTypeCode)
            {
                case CreditNote:
                case SelfBilledCreditNote:
                    return "urn:oasis:names:specification:ubl:signature:CreditNote";
                case DebitNote:
                case SelfBilledDebitNote:
                    return "urn:oasis:names:specification:ubl:signature:DebitNote";
                case RefundNote:
                case SelfBilledRefundNote:
                    return "urn:oasis:names:specification:ubl:signature:RefundNote";
                default:
                    return "urn:oasis:names:specification:ubl:signature:Invoice";
            }
        }

        /// <summary>
        /// True when the document type is a credit/debit/refund note (i.e. requires an
        /// <c>BillingReference</c> to the original invoice).
        /// </summary>
        public static bool IsCreditOrDebitNote(string documentTypeCode)
        {
            return documentTypeCode == CreditNote
                || documentTypeCode == DebitNote
                || documentTypeCode == RefundNote
                || documentTypeCode == SelfBilledCreditNote
                || documentTypeCode == SelfBilledDebitNote
                || documentTypeCode == SelfBilledRefundNote;
        }

        /// <summary>
        /// True when the document type is a self-billed document (types 11-14).
        /// </summary>
        public static bool IsSelfBilled(string documentTypeCode)
        {
            return documentTypeCode == SelfBilledInvoice
                || documentTypeCode == SelfBilledCreditNote
                || documentTypeCode == SelfBilledDebitNote
                || documentTypeCode == SelfBilledRefundNote;
        }
    }
}
