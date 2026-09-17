using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.Model.InputData;
using ErpWeb.EInvoiceLib.Store;

namespace ErpWeb.EInvoiceLib.GenerateDoc
{
    public class GenerateInvoice
    {

        DocumentHeader doc;

        public Root InvoiceRoot;

        public GenerateInvoice()
        {

        }

        public GenerateInvoice(DocumentHeader document)
        {
            doc = document;
        }


        //Generate single invoice only
        public string Generate()
        {
            InvoiceJson invoice = createInvoiceContent();
            InvoiceRoot = new Root()
            {
                _D = EInvoiceDocumentTypeMap.GetDocumentNamespace(
                    EInvoiceDocumentTypeMap.GetDocumentTypeCode(doc.docType)),
                _A = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
                _B = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
                // _E = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2",
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
                //FloatParseHandling = FloatParseHandling.Decimal

            };
            var json = JsonConvert.SerializeObject(InvoiceRoot, settings);
            // Console.WriteLine(json);
            //File.WriteAllText("c:\\test\\Invoince.json", json);
            return json;

        }

        public string GenerateMultipleInvoice(List<InvoiceJson> invoices)
        {

            InvoiceRoot = new Root()
            {
                _D = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2",
                _A = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2",
                _B = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2",
                //_E = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2"

            };
            InvoiceRoot.Invoice = invoices.ToList();


            var settings = new JsonSerializerSettings
            {
                //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
                DateFormatString = "yyyy-MM-ddTH:mmZ",
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                NullValueHandling = NullValueHandling.Ignore,
                // FloatParseHandling = FloatParseHandling.Decimal

            };
            var json = JsonConvert.SerializeObject(InvoiceRoot, settings);
            // Console.WriteLine(json);
            //  File.WriteAllText("c:\\test\\Invoicemulti.json", json);
            return json;

        }

        public InvoiceJson createInvoiceContent()
        {
            InvoiceJson invoice = new InvoiceJson();
            string currCode = doc.Currency ?? "";// Convert2NumTool<string>.ConvertVal(drInv["Currency"]);


            invoice.InvoiceTypeCode = new List<InvoiceTypeCode>()
            {
                new InvoiceTypeCode()
                {
                     // 01 for a normal invoice; 11 for a self-billed invoice. 01 is unchanged.
                     _ = EInvoiceDocumentTypeMap.GetDocumentTypeCode(doc.docType),
                     listVersionID = doc.DocumentVersion
                }
            };

            invoice.ID = new List<ID>()
            {
                new ID()
                {
                    _ = doc.DocumentNo??""// Convert2NumTool<string>.ConvertVal(drInv["InvNo"])
                }
            };

            //DateTime invDate = Convert2NumTool<DateTime>.ConvertVal(drInv["InvDate"]);
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

            invoice.InvoicePeriod = new List<InvoicePeriod>()
            {
                new InvoicePeriod()
                {
                     StartDate = new List<StartDate>()
                     {
                          new StartDate()
                          {
                               //_ =   _= eInvDate.ToUniversalTime().ToString("yyyy-MM-dd") //"2024-03-01"
                               _ =  doc.InvoicePeriodStartDate
                          }
                     },
                     EndDate = new List<EndDate>()
                     {
                          new EndDate()
                          {
                              // _ =   _= eInvDate.ToUniversalTime().ToString("yyyy-MM-dd") //"2024-03-01"
                               _ =   doc.InvoicePeriodEndDate
                          }
                     },
                     Description = new List<Description>()
                     {
                         new Description()
                         {
                              _ ="Daily"
                         }
                     }
                }
            };

            invoice.DocumentCurrencyCode = new List<DocumentCurrencyCode>()
            {
                 new DocumentCurrencyCode()
                 {
                       _= currCode
                 }
            };

            //foreign currency
            if (currCode.ToUpper() != "MYR")
            {
                invoice.TaxExchangeRate = new List<TaxExchangeRate>()
                {
                    new TaxExchangeRate()
                    {
                         CalculationRate= new List<CalculationRate>()
                         {
                              new CalculationRate()
                              {
                                   _ = Math.Round(doc.ExchangeRate??0,4) //  Math.Round(Convert2NumTool<double>.ConvertVal( drInv["CurrRate"]), 4)
                              }

                         },
                          SourceCurrencyCode = new List<DocumentCurrencyCode>()
                          {
                              new DocumentCurrencyCode()
                              {
                                   _ = currCode
                              }
                          },
                           TargetCurrencyCode = new List<DocumentCurrencyCode>()
                           {
                               new DocumentCurrencyCode()
                               {
                                    _ ="MYR"
                               }
                           }
                    },

                };

            }

            invoice.BillingReference = GenerateDocHelper.getBillingReference(doc);

            invoice.LegalMonetaryTotal = GenerateDocHelper.getLegalMonetaryTotal(doc);

            invoice.TaxTotal = GenerateDocHelper.getHeaderTaxTotal(doc);

            //Name of business or individual who will be the issuer of the e-Invoice in a commercial transaction
            invoice.AccountingSupplierParty = GenerateDocHelper.getAccountingSupplierParty(doc);

            //Customer / Buyer
            invoice.AccountingCustomerParty = GenerateDocHelper.getAccountingCustomerParty(doc);

            //invoice.InvoiceLine = GenerateDocHelper.getInvoiceLine(currCode, dtInvDtl, dtTaxGrp, dtIvMas);
            invoice.InvoiceLine = GenerateDocHelper.getInvoiceLine(doc);

            //additional Invoice discount
            if (doc.AdditionalDiscountAmount.HasValue && doc.AdditionalDiscountAmount > 0)
            {
                invoice.AllowanceCharge = new List<AllowanceCharge>()
            {
                new AllowanceCharge()
                {
                     ChargeIndicator = new List<ChargeIndicator>()
                     {
                          new ChargeIndicator()
                          {
                               _ = false
                          }
                     },
                     AllowanceChargeReason = new List<AllowanceChargeReason>()
                     {
                          new AllowanceChargeReason()
                          {
                               _ = "Promotion Discount"
                          }
                     },
                     Amount = new List<Amount>()
                     {
                          new Amount()
                          {
                               currencyID = currCode,
                               _ = doc.AdditionalDiscountAmount ?? 0 // Math.Round(Convert2NumTool<double>.ConvertVal(drInv["AllowanceTotalAmount"]), 2)
                          }
                     }

                }
            };
            }
            return invoice;
        }
    }
}
