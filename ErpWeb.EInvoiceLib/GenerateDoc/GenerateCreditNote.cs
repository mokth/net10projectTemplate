using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.EInvoiceLib.GenerateDoc
{
    public class GenerateCreditNote
    {


        string DocumentType = "";
        string _oriInvUUID;
        public Root InvoiceRoot;
        DocumentHeader doc;

        public GenerateCreditNote(DocumentHeader document)
        {
            doc = document;
        }


        public string Generate()
        {
            string currCode = doc.Currency ?? "";// Convert2NumTool<string>.ConvertVal(drInv["Currency"]);

            // Resolve the LHDN document type code and UBL namespace from the ERP document type.
            // Credit note -> 02, debit note -> 03 (self-billed CN/DN -> 12/13).
            DocumentType = EInvoiceDocumentTypeMap.GetDocumentTypeCode(doc.docType);
            if (!EInvoiceDocumentTypeMap.IsCreditOrDebitNote(DocumentType))
            {
                throw new Exception("GenerateCreditNote only supports credit note or debit note document types.");
            }

            // The original invoice reference must be supplied by the caller; never fall back to a
            // placeholder UUID because MyInvois rejects a credit/debit note that does not reference
            // a real, valid invoice.
            _oriInvUUID = doc.OriginInvoiceUUID;
            if (string.IsNullOrWhiteSpace(_oriInvUUID))
            {
                throw new Exception("OriginInvoiceUUID is required for credit/debit note submission.");
            }

            string documentVersion = string.IsNullOrWhiteSpace(doc.DocumentVersion) ? "1.0" : doc.DocumentVersion;

            InvoiceJson invoice = new InvoiceJson();
            //calculateTax();

            invoice.InvoiceTypeCode = new List<InvoiceTypeCode>()
            {
                new InvoiceTypeCode()
                {
                     _= DocumentType, // Invoice
                     listVersionID = documentVersion
                }
            };

            invoice.ID = new List<ID>()
            {
                new ID()
                {
                    _ = doc.DocumentNo??""//Convert2NumTool<string>.ConvertVal(drInv["DocNo"])
                }
            };

            //DateTime invDate = Convert2NumTool<DateTime>.ConvertVal(drInv["DocDate"]);
            DateTime eInvDate = DateTime.Now;
            invoice.IssueDate = new List<IssueDate>()
            {
                 new IssueDate()
                 {
                      _= eInvDate.ToUniversalTime().ToString("yyyy-MM-dd") //"2024-03-01"
                 }
            };

            invoice.IssueTime = new List<IssueTime>()
            {
                //When “Z” (Zulu) is tacked on the end of a time, it indicates that that time is UTC,
                //so really the literal Z is part of the time. What is T between date and time? The T is just a literal to separate the date from the time,
                //and the Z means “zero hour offset” also known as “Zulu time” (UTC).
                 new IssueTime()
                 {
                      _= eInvDate.ToUniversalTime().ToString("HH:mm:00Z")// "15:30:00.0Z"
                 }
            };

            invoice.DocumentCurrencyCode = new List<DocumentCurrencyCode>()
            {
                 new DocumentCurrencyCode()
                 {
                       _= currCode
                 }
            };

            invoice.TaxCurrencyCode = new List<DocumentCurrencyCode>()
            {
                 new DocumentCurrencyCode()
                 {
                        _= currCode
                 }
            };



            //credit note
            invoice.BillingReference = new List<BillingReference>()
            {
                new BillingReference()
                {
                     InvoiceDocumentReference = new List<InvoiceDocumentReference>()
                     {
                         new InvoiceDocumentReference()
                         {
                              UUID = new List<ID>()
                              {
                                  new ID()
                                  {
                                       _ = _oriInvUUID
                                  }
                              },
                              ID= new List<ID>()
                              {
                                  new ID()
                                  {
                                       _ = doc.RefDocumentNo??""//Convert2NumTool<string>.ConvertVal(drInv["InvNo"])
                                  }
                              }

                              //invUUID //get from SaInvoice
                         },



                     }
                }
            };

            invoice.LegalMonetaryTotal = GenerateDocHelper.getLegalMonetaryTotal(doc);
            invoice.TaxTotal = GenerateDocHelper.getHeaderTaxTotal(doc);
            invoice.AccountingSupplierParty = GenerateDocHelper.getAccountingSupplierParty(doc);
            invoice.AccountingCustomerParty = GenerateDocHelper.getAccountingCustomerParty(doc);
            //invoice.InvoiceLine = GenerateDocHelper.getInvoiceLine(currCode, dtInvDtl, dtTaxGrp, dtIvMas);
            invoice.InvoiceLine = GenerateDocHelper.getInvoiceLine(doc);

            string D = EInvoiceDocumentTypeMap.GetDocumentNamespace(DocumentType);

            InvoiceRoot = new Root()
            {
                _D = D,
                _A = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
                _B = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",

                Invoice = new List<InvoiceJson>()
                 {
                      invoice
                 }
            };

            var settings = new JsonSerializerSettings
            {
                //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
                DateFormatString = "yyyy-MM-ddTH:mmZ",
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                NullValueHandling = NullValueHandling.Ignore
            };
            var json = JsonConvert.SerializeObject(InvoiceRoot, settings);

            return json;

        }
    }
}
