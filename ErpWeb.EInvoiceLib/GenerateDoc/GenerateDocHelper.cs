using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.EInvoiceLib.GenerateDoc
{
    public class GenerateDocHelper
    {
        public static List<AccountingSupplierParty> getAccountingSupplierParty(DocumentHeader doc)
        {
            List<AccountingSupplierParty> party = new List<AccountingSupplierParty>();

            string misccode = doc.Supplier.IndustryClassificationCode ?? ""; //Convert2NumTool<string>.ConvertVal(drRow["MISCCode"]);
            string bizDesc = doc.Supplier.BizDesciption ?? "";//  Convert2NumTool<string>.ConvertVal(drRow["BizDesc"]);
            string tinno = doc.Supplier.TinNo ?? "";// Convert2NumTool<string>.ConvertVal(drRow["TINNo"]);
            string brnNo = doc.Supplier.RegNo ?? "";// Convert2NumTool<string>.ConvertVal(drRow["Com_Reg_No"]);
            string gstNo = doc.Supplier.SSTNo ?? "";// Convert2NumTool<string>.ConvertVal(drAccBrn["GSTNumber"]);

            string addr1 = doc.Supplier.Addr1 ?? "";// Convert2NumTool<string>.ConvertVal(drRow["Address1"]);
            string addr2 = doc.Supplier.Addr2 ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Address2"]);
            string addr3 = doc.Supplier.Addr3 ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Address3"]);

            string city = doc.Supplier.CityName ?? "";//Convert2NumTool<string>.ConvertVal(drRow["City"]);
            string postalCode = doc.Supplier.PostalCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Postal"]);
            string stateCode = doc.Supplier.StateCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["StateCode"]);
            string countryCode = doc.Supplier.CountryCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["CountryCode"]);
            string companyName = doc.Supplier.CompanyName ?? "";//Convert2NumTool<string>.ConvertVal(drAccBrn["CompanyName"]);



            string telno = doc.Supplier.PhoneNo ?? "";// Convert2NumTool<string>.ConvertVal(drRow["Tel"]);
            string email = doc.Supplier.Email ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Email"]);

            if (string.IsNullOrEmpty(misccode))
            {
                throw new Exception("AccountingSupplierParty MISCCode is blank");
            }
            if (string.IsNullOrEmpty(bizDesc))
            {
                throw new Exception("AccountingSupplierParty biz desc is blank");
            }
            if (string.IsNullOrEmpty(addr1))
            {
                throw new Exception("AccountingSupplierParty address line 1 is blank");
            }
            if (string.IsNullOrEmpty(addr1))
            {
                throw new Exception("AccountingSupplierParty address line 1 is blank");
            }
            if (string.IsNullOrEmpty(city))
            {
                throw new Exception("AccountingSupplierParty city name is blank");
            }
            if (string.IsNullOrEmpty(stateCode))
            {
                throw new Exception("AccountingSupplierParty state Code name is blank");
            }
            if (string.IsNullOrEmpty(countryCode))
            {
                throw new Exception("AccountingSupplierParty country Code name is blank");
            }
            if (string.IsNullOrEmpty(companyName))
            {
                throw new Exception("AccountingSupplierParty customer Name  is blank");
            }
            if (string.IsNullOrEmpty(tinno))
            {
                throw new Exception("AccountingSupplierParty TIN NO is blank");
            }
            if (string.IsNullOrEmpty(brnNo))
            {
                throw new Exception("AccountingSupplierParty Biz Registration number is blank");
            }
            if (string.IsNullOrEmpty(telno))
            {
                throw new Exception("AccountingSupplierParty Tel number is blank");
            }

            List<string> addressList = new List<string>();
            addressList.AddRange(new List<string>()
            {
                addr1,addr2,addr3
            });

            List<AddressLine> address = new List<AddressLine>();
            foreach (string addr in addressList)
            {
                if (!string.IsNullOrEmpty(addr))
                {
                    var addrline = new AddressLine()
                    {
                        Line = new List<Line>()
                        {
                          new Line()
                          {
                            _ = addr
                          }
                        }
                    };
                    address.Add(addrline);
                }
            }

            var postalAddress = new List<PostalAddress>()
                             {
                                 new PostalAddress()
                                 {
                                      CityName = new List<CityName>()
                                       {
                                           new CityName()
                                           {
                                                  _ = city,
                                           }
                                       },

                                      CountrySubentityCode = new List<CountrySubentityCode>()
                                       {
                                           new CountrySubentityCode()
                                           {
                                                 _ = stateCode,
                                           }
                                       },
                                      AddressLine = address.ToList(),

                                       Country = new List<Country>()
                                       {
                                            new Country()
                                            {
                                                 IdentificationCode= new List<IdentificationCode>()
                                                 {
                                                      new IdentificationCode()
                                                      {
                                                           _= countryCode,
                                                          listID="ISO3166-1",
                                                          listAgencyID ="6"
                                                      }
                                                 }
                                            }
                                       },
                                 }
                             };

            if (string.IsNullOrEmpty(postalCode))
            {
                var postalZone = new List<PostalZone>()
                                      {
                                          new PostalZone()
                                          {
                                               _ = postalCode,
                                          }
                                      };
                postalAddress[0].PostalZone = postalZone.ToList();
            }

            var partyIdentification = new List<PartyIdentification>()
                              {
                                     new PartyIdentification()
                                     {
                                          ID= new List<ID>()
                                          {
                                               new ID()
                                               {
                                                    _= tinno,
                                                    schemeID="TIN"
                                               }
                                          }
                                     },
                                     new PartyIdentification()
                                     {
                                          ID= new List<ID>()
                                          {
                                               new ID()
                                               {
                                                    _= brnNo,
                                                    schemeID= doc.Supplier.RegType.ToString()
                                               }
                                          }
                                     },

            };

            // SST registration number of the Supplier
            // *This is not applicable to Supplier that are not SST - registered.
            // The input of special characters is not allowed, except for dash(-).
            // Supplier to input “NA” if supplier is not registered for SST.
            if (!string.IsNullOrEmpty(gstNo))
            {
                gstNo = "NA";
            }

            var sst = new PartyIdentification()
            {
                ID = new List<ID>()
                    {
                        new ID()
                         {
                            _= gstNo,
                           schemeID="SST"
                        }
                    }
            };
            partyIdentification.Add(sst);

            var contact = new List<Contact>()
                              {
                                  new Contact()
                                  {
                                       Telephone = new List<Telephone>()
                                       {
                                           new Telephone()
                                           {
                                                _= telno,
                                           }
                                       },


                                  }
                              };


            if (!string.IsNullOrEmpty(email))
            {
                var electronicMail = new List<ElectronicMail>()
                                       {
                                           new ElectronicMail()
                                           {
                                                _ =email,
                                           }
                                       };
                contact[0].ElectronicMail = electronicMail.ToList();
            }


            party = new List<AccountingSupplierParty>()
            {
                new AccountingSupplierParty()
                {
                     Party = new List<Party>()
                     {
                         new Party()
                         {
                              IndustryClassificationCode =  new List<IndustryClassificationCode>()
                             {
                                 new IndustryClassificationCode()
                                 {
                                     // need to add at AdCompany table                                      
                                     // Supplier’s Malaysia Standard Industrial Classification (MSIC) Code
                                     // Supplier’s Business Activity Description
                                     _ = misccode,
                                     name= bizDesc,
                                 }
                             },
                             PartyIdentification = partyIdentification.ToList(),
                             PostalAddress = postalAddress.ToList(),
                             PartyLegalEntity= new List<PartyLegalEntity>()
                              {
                                    new PartyLegalEntity()
                                    {
                                         RegistrationName = new List<RegistrationName>()
                                         {
                                              new RegistrationName()
                                              {
                                                   _= companyName,
                                              }
                                         }
                                    }
                              },

                             Contact = contact

                         },

                     }
                }
            };
            return party;
        }

        public static List<AccountingCustomerParty> getAccountingCustomerParty(DocumentHeader doc)
        {
            List<AccountingCustomerParty> party = new List<AccountingCustomerParty>();

            string addr1 = doc.Customer.Addr1 ?? "";// Convert2NumTool<string>.ConvertVal(drRow["Address1"]);
            string addr2 = doc.Customer.Addr2 ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Address2"]);
            string addr3 = doc.Customer.Addr3 ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Address3"]);
            string addr4 = doc.Customer.Addr4 ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Address4"]);
            string city = doc.Customer.CityName ?? "";//Convert2NumTool<string>.ConvertVal(drRow["City"]);
            string postalCode = doc.Customer.PostalCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["PostalCode"]);
            string stateCode = doc.Customer.StateCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["StateCode"]);
            string countryCode = doc.Customer.CountryCode ?? "";//Convert2NumTool<string>.ConvertVal(drRow["CountryCode"]);
            string custName = doc.Customer.CompanyName ?? "";//Convert2NumTool<string>.ConvertVal(drRow["CustName"]);
            string tinno = doc.Customer.TinNo ?? "";//Convert2NumTool<string>.ConvertVal(drRow["TINNo"]);
            string brnNo = doc.Customer.RegNo ?? "";//Convert2NumTool<string>.ConvertVal(drRow["CustomerBRN"]);
            string gstNo = doc.Customer.SSTNo ?? "";//Convert2NumTool<string>.ConvertVal(drRow["GSTRegNo"]);
            string telno = doc.Customer.PhoneNo ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Tel"]);
            string email = doc.Customer.Email ?? "";//Convert2NumTool<string>.ConvertVal(drRow["Email"]);

            if (string.IsNullOrEmpty(addr1))
            {
                throw new Exception("AccountingCustomerParty address line 1 is blank");
            }
            if (string.IsNullOrEmpty(city))
            {
                throw new Exception("AccountingCustomerParty city name is blank");
            }
            if (string.IsNullOrEmpty(stateCode))
            {
                throw new Exception("AccountingCustomerParty state Code name is blank");
            }
            if (string.IsNullOrEmpty(countryCode))
            {
                throw new Exception("AccountingCustomerParty country Code name is blank");
            }
            if (string.IsNullOrEmpty(custName))
            {
                throw new Exception("AccountingCustomerParty customer Name  is blank");
            }
            if (string.IsNullOrEmpty(tinno))
            {
                throw new Exception("AccountingCustomerParty TIN NO is blank");
            }
            if (string.IsNullOrEmpty(brnNo))
            {
                throw new Exception("AccountingCustomerParty Biz Registration number is blank");
            }
            if (string.IsNullOrEmpty(telno))
            {
                throw new Exception("AccountingCustomerParty Tel number is blank");
            }

            List<string> addressList = new List<string>();
            addressList.AddRange(new List<string>()
            {
                addr1,addr2,addr3,addr4
            });

            List<AddressLine> address = new List<AddressLine>();
            foreach (string addr in addressList)
            {
                if (!string.IsNullOrEmpty(addr))
                {
                    var addrline = new AddressLine()
                    {
                        Line = new List<Line>()
                        {
                          new Line()
                          {
                            _ = addr
                          }
                        }
                    };
                    address.Add(addrline);
                }
            }

            var postalAddress = new List<PostalAddress>()
                             {
                                 new PostalAddress()
                                 {
                                      CityName = new List<CityName>()
                                       {
                                           new CityName()
                                           {
                                                  _ = city,
                                           }
                                       },

                                      CountrySubentityCode = new List<CountrySubentityCode>()
                                       {
                                           new CountrySubentityCode()
                                           {
                                                 _ = stateCode,
                                           }
                                       },
                                      AddressLine = address.ToList(),

                                       Country = new List<Country>()
                                       {
                                            new Country()
                                            {
                                                 IdentificationCode= new List<IdentificationCode>()
                                                 {
                                                      new IdentificationCode()
                                                      {
                                                           _= countryCode,
                                                          listID="ISO3166-1",
                                                          listAgencyID ="6"
                                                      }
                                                 }
                                            }
                                       },
                                 }
                             };

            if (string.IsNullOrEmpty(postalCode))
            {
                var postalZone = new List<PostalZone>()
                                      {
                                          new PostalZone()
                                          {
                                               _ = postalCode,
                                          }
                                      };
                postalAddress[0].PostalZone = postalZone.ToList();
            }

            var partyIdentification = new List<PartyIdentification>()
                              {
                                     new PartyIdentification()
                                     {
                                          ID= new List<ID>()
                                          {
                                               new ID()
                                               {
                                                    _= tinno,
                                                    schemeID="TIN"
                                               }
                                          }
                                     },
                                     new PartyIdentification()
                                     {
                                          ID= new List<ID>()
                                          {
                                               new ID()
                                               {
                                                    _= brnNo,
                                                     schemeID= doc.Customer.RegType.ToString()
                                               }
                                          }
                                     },

            };

            if (!string.IsNullOrEmpty(gstNo))
            {
                var sst = new PartyIdentification()
                {
                    ID = new List<ID>()
                    {
                        new ID()
                         {
                            _= gstNo,
                           schemeID="SST"
                        }
                    }
                };
                partyIdentification.Add(sst);
            }

            var contact = new List<Contact>()
                              {
                                  new Contact()
                                  {
                                       Telephone = new List<Telephone>()
                                       {
                                           new Telephone()
                                           {
                                                _= telno,
                                           }
                                       },


                                  }
                              };


            if (!string.IsNullOrEmpty(email))
            {
                var electronicMail = new List<ElectronicMail>()
                                       {
                                           new ElectronicMail()
                                           {
                                                _ =email,
                                           }
                                       };
                contact[0].ElectronicMail = electronicMail.ToList();
            }


            party = new List<AccountingCustomerParty>()
            {
                new AccountingCustomerParty()
                {
                     Party = new List<Party>()
                     {
                         new Party()
                         {
                             PostalAddress = postalAddress.ToList(),
                             PartyLegalEntity= new List<PartyLegalEntity>()
                              {
                                    new PartyLegalEntity()
                                    {
                                         RegistrationName = new List<RegistrationName>()
                                         {
                                              new RegistrationName()
                                              {
                                                   _= custName,
                                              }
                                         }
                                    }
                              },
                             PartyIdentification = partyIdentification.ToList(),
                             Contact = contact

                         },

                     }
                }
            };
            return party;
        }

        public static List<BillingReference> getBillingReference(DocumentHeader doc)
        {
            List<BillingReference> billingReference = new List<BillingReference>();
            var billref = new BillingReference()
            {
                AdditionalDocumentReference = new List<AdditionalDocumentReference>()
                      {
                           new AdditionalDocumentReference()
                           {
                                ID = new List<ID>()
                                {
                                    new ID()
                                    {
                                         _= doc.RefDocumentNo??"" // Convert2NumTool<string>.ConvertVal(drRow["DONO"]) //use do no
                                    }
                                }
                           }
                      }
            };

            billingReference.Add(billref);
            return billingReference;
        }

        public static List<LegalMonetaryTotal> getLegalMonetaryTotal(DocumentHeader doc)
        {
            double grossAmt = Math.Round(doc.AmountExlTax ?? 0, 2);// Convert2NumTool<double>.ConvertVal(drRow["GrossAmnt"]), 2);
            double totAmt = Math.Round(doc.AmountIncTax ?? 0, 2); //Convert2NumTool<double>.ConvertVal(drRow["TotAmnt"]), 2);
            string currencyID = doc.Currency ?? "";// Convert2NumTool<string>.ConvertVal(drRow["Currency"]);

            if (string.IsNullOrEmpty(currencyID))
            {
                throw new Exception("Currency code is blank");
            }

            var legalMonetaryTotal = new List<LegalMonetaryTotal>()
            {
                new LegalMonetaryTotal()
                {
                    LineExtensionAmount = new List<LineExtensionAmount>()
                    {
                        new LineExtensionAmount()
                        {
                             _= Math.Round(doc.TotalNetAmount ?? 0,2),
                             currencyID = currencyID
                        }
                    },
                   // Total excluding Tax
                   TaxExclusiveAmount= new List<TaxExclusiveAmount>()
                     {
                         new TaxExclusiveAmount()
                         {
                              _= Math.Round(doc.AmountExlTax ?? 0,2),
                              currencyID = currencyID
                         }
                     },
                    //Total Including Tax
                    TaxInclusiveAmount= new List<TaxInclusiveAmount>()
                     {
                         new TaxInclusiveAmount()
                         {
                              _= Math.Round(doc.AmountIncTax ?? 0,2),
                               currencyID = currencyID
                         }
                     },
                    //Total Payable Amount
                    PayableAmount= new List<PayableAmount>()
                     {
                         new PayableAmount()
                         {
                             _= Math.Round(doc.TotalPayableAmount ?? 0,2),
                                currencyID = currencyID
                         }
                     },
                      AllowanceTotalAmount = new List<AllowanceTotalAmount>()
                    {
                         new AllowanceTotalAmount()
                         {
                                _ =  Math.Round(doc.DiscountAmount??0,2),
                               currencyID = currencyID
                         }
                    }
                },

            };

            return legalMonetaryTotal;
        }

        public static List<TaxTotal> getHeaderTaxTotal(DocumentHeader doc)
        {
            var taxInfo = new List<TaxInfo>();
            List<TaxTotal> taxTotals = new List<TaxTotal>();
            taxInfo = calculateTax(doc);

            double totalTaxAmt = taxInfo.Sum((x) => x.TaxAmount);

            var taxSubtotals = new List<TaxSubtotal>();
            foreach (var tax in taxInfo)
            {
                var taxSubTotal = new TaxSubtotal()
                {
                    TaxableAmount = new List<TaxableAmount>()
                             {
                                 new TaxableAmount()
                                 {
                                      _= tax.TaxableAmount,
                                      currencyID =doc.Currency??""// currencyCode,
                                 }
                             },
                    TaxAmount = new List<TaxAmount>()
                              {
                                  new TaxAmount()
                                  {
                                        _= tax.TaxAmount,
                                      currencyID =doc.Currency??""// currencyCode,
                                  }
                              },
                    Percent = new List<Percent>()
                                    {
                                        new Percent()
                                        {
                                            _ = tax.TaxPercent
                                        }
                                    },
                    TaxCategory = new List<TaxCategory>()
                            {
                                new TaxCategory()
                                {
                                     ID = new List<ID>()
                                     {
                                         new ID()
                                         {
                                              _= tax.TaxType
                                         }
                                     },
                                 
                                    TaxScheme = new List<TaxScheme>()
                                    {
                                        new TaxScheme()
                                        {
                                             ID = new List<ID>()
                                             {
                                                new ID()
                                                {
                                                     _="OTH",
                                                     schemeID = "UN/ECE 5153",
                                                     schemeAgencyID= "6",
                                                 }
                                             }
                                        }
                                    }

                                }
                            }
                };
                taxSubtotals.Add(taxSubTotal);
            }



            var TaxTotal = new List<TaxTotal>()
            {
                new TaxTotal()
                {
                     TaxAmount = new List<TaxAmount>()
                     {
                          new TaxAmount()
                          {
                               _ = totalTaxAmt,
                               currencyID = doc.Currency??""//currencyCode,
                          }
                     }
                }
            };
            if (totalTaxAmt > 0)
            {
                TaxTotal[0].TaxSubtotal = taxSubtotals.ToList();
            }
            else
            {
                var taxSubTotal = new TaxSubtotal()
                {
                    TaxableAmount = new List<TaxableAmount>()
                             {
                                 new TaxableAmount()
                                 {
                                       _= doc.AmountExlTax??0,
                                      currencyID =doc.Currency??""// currencyCode,
                                 }
                             },
                    TaxAmount = new List<TaxAmount>()
                              {
                                  new TaxAmount()
                                  {
                                        _= 0,
                                      currencyID =doc.Currency??""//currencyCode,
                                  }
                              },
                    TaxCategory = new List<TaxCategory>()
                            {
                                new TaxCategory()
                                {
                                     ID = new List<ID>()
                                     {
                                         new ID()
                                         {
                                              _= "06"
                                         }
                                     },

                                    TaxScheme = new List<TaxScheme>()
                                    {
                                        new TaxScheme()
                                        {
                                             ID = new List<ID>()
                                             {
                                                new ID()
                                                {
                                                     _="OTH",
                                                     schemeID = "UN/ECE 5153",
                                                     schemeAgencyID= "6",
                                                 }
                                             }
                                        }
                                    }

                                }
                            }
                };

                TaxTotal[0].TaxSubtotal = new List<TaxSubtotal>()
                {
                    taxSubTotal
                };
            }

            return TaxTotal;
        }

        private static List<TaxInfo> calculateTax(DocumentHeader doc)
        {
            var groupedTax = doc.documentDetails
                              .GroupBy(b => new
                              {
                                  tax = b.TaxType

                              })
                              .Select(g => new
                              {
                                  TaxGroup = g.Key.tax,
                                  TaxPercent = g.Max(x => x.TaxPerCent),
                                  TaxAmt = g.Sum(x => x.TaxAmount), //tax amt
                                  Amount = g.Sum(x => x.AmountExclTax) //taxable amt
                              }).ToList();

            List<TaxInfo> taxs = new List<TaxInfo>();
            foreach (var item in groupedTax)
            {
                TaxInfo tax = new TaxInfo();
                tax.TaxAmount = Math.Round(item.TaxAmt ?? 0, 2);
                tax.TaxCode = item.TaxGroup;
                tax.TaxableAmount = Math.Round(item.Amount ?? 0, 2);
                tax.TaxPercent = item.TaxPercent ?? 0;
                tax.TaxType = item.TaxGroup;
                taxs.Add(tax);
            }

            //taxs.GroupBy(x => x.TaxType)
            //    .Select(g => new TaxInfo
            //    {
            //        TaxType = g.Key,
            //        TaxPercent = g.Max(x => x.TaxPercent),
            //        TaxAmount = g.Sum(x => x.TaxAmount),
            //    });
            //var taxInfo = taxs.ToList();

            return taxs;
        }

        public static List<InvoiceLine> getInvoiceLine(DocumentHeader doc)
        {
            string currencyCode = doc.Currency ?? "";// data.currCode;
            //DataTable dtInvDtl = data.tbInvDetail;
            //DataTable dtTaxGrp = data.tbTax;
            //DataTable dtIvMas = data.tbIvMas;

            List<InvoiceLine> items = new List<InvoiceLine>();

            foreach (var drItem in doc.documentDetails)
            {
                TaxInfo itemTax = new TaxInfo();
                string taxCode = drItem.TaxType ?? "";// Convert2NumTool<string>.ConvertVal(drItem["TaxGroup"]);
                //string icode = Convert2NumTool<string>.ConvertVal(drItem["ICode"]);
                //string stdUOM = Convert2NumTool<string>.ConvertVal(drItem["StdUOM"]);
                //DataRow[] drUOMs = data.tbUOM.Select("UOMCode ='" + stdUOM + "'");
                string UNECE_UOM = drItem.UOM ?? "";// stdUOM.ToUpper();
                                                    //if (drUOMs.Length > 0)
                                                    // {
                                                    //    UNECE_UOM = Convert2NumTool<string>.ConvertVal(drUOMs[0]["UNECE_UOM"]);
                                                    // }


                //DataRow[] drTaxes = dtTaxGrp.Select("TaxGrCode='" + taxCode + "'");
                //DataRow[] drIvMas = dtIvMas.Select("ICode = '" + icode + "'");
                string classCode = drItem.ClassificationCode ?? "";

                double taxAmt = drItem.TaxAmount ?? 0;// Convert2NumTool<double>.ConvertVal(drItem["TaxAmt"]);
                double taxAbleAmt = drItem.AmountExclTax ?? 0;// Convert2NumTool<double>.ConvertVal(drItem["Amount"]);

                //if (drIvMas.Length > 0)
                //{
                //    classCode = Convert2NumTool<string>.ConvertVal(drIvMas[0]["Classification"]);
                //}
                if (string.IsNullOrEmpty(classCode))
                {
                    throw new Exception("Item Classification Code for " + drItem.ItemCode + " is not define yet.");
                }


                itemTax.TaxCode = taxCode;
                itemTax.TaxType = drItem.TaxType ?? "";//  Convert2NumTool<string>.ConvertVal(drTaxes[0]["TaxType"]);
                itemTax.TaxPercent = drItem.TaxPerCent ?? 0;//  Convert2NumTool<double>.ConvertVal(drTaxes[0]["Percentage"]);


                var taxs = new List<TaxTotal>()
                {
                     new TaxTotal()
                     {
                          TaxAmount = new List<TaxAmount>()
                                 {
                                     new TaxAmount()
                                     {
                                          _ = taxAmt,
                                           currencyID= currencyCode,
                                     }
                                 }
                     }
                };

                //if (taxAmt > 0)
                // {
                var taxSubtotal = new List<TaxSubtotal>()
                                  {
                                      new TaxSubtotal()
                                      {
                                           TaxableAmount = new List<TaxableAmount>()
                                           {
                                                new TaxableAmount()
                                                {
                                                      _ =  taxAbleAmt,
                                                     currencyID =currencyCode
                                                }
                                           },
                                           TaxAmount = new List<TaxAmount>()
                                           {
                                               new TaxAmount()
                                               {
                                                    _ = taxAmt,
                                                    currencyID =currencyCode,
                                               }
                                           },
                                            Percent = new List<Percent>()
                                                    {
                                                        new Percent()
                                                        {
                                                            _ =  itemTax.TaxPercent
                                                        }
                                                    },
                                           TaxCategory = new List<TaxCategory>()
                                           {
                                               new TaxCategory()
                                               {
                                                    ID = new List<ID>()
                                                    {
                                                        new ID()
                                                        {
                                                             _= itemTax.TaxType
                                                        }
                                                    },
                                                   

                                                     TaxScheme = new List<TaxScheme>()
                                                     {
                                                         new TaxScheme(){
                                                           ID = new List<ID>()
                                                           {
                                                              new ID()
                                                                {
                                                                   _ ="OTH",
                                                                   schemeID = "UN/ECE 5153",
                                                                   schemeAgencyID = "6"
                                                                }
                                                           }
                                                         }


                                                     }

                                               }
                                           },

                                      }
                                  };
                taxs[0].TaxSubtotal = taxSubtotal.ToList();
                //}

                var invItem = new InvoiceLine()
                {
                    ID = new List<ID>()
                    {
                        new ID()
                        {
                            _ = drItem.Line.ToString() //Convert2NumTool<int>.ConvertVal(drItem["Line"]).ToString()
                        }
                    },
                    InvoicedQuantity = new List<InvoicedQuantity>()
                    {
                        new InvoicedQuantity()
                        {
                            _ =  drItem.Qty?? 0, // Convert2NumTool<double>.ConvertVal(drItem["StdQty"]),
                            unitCode =  UNECE_UOM // Convert2NumTool<string>.ConvertVal(drItem["StdUOM"]),
                                                         
                        }
                    },
                    //AllowanceCharge = new List<AllowanceCharge>()
                    //{
                    //    new AllowanceCharge()
                    //    {
                    //        ChargeIndicator = new List<ChargeIndicator>()
                    //        {
                    //            new ChargeIndicator()
                    //            {
                    //                _ = false
                    //            }
                    //        },
                    //        MultiplierFactorNumeric = new List<MultiplierFactorNumeric>()
                    //        {
                    //            new MultiplierFactorNumeric()
                    //            {
                    //                _ = 0
                    //            }
                    //        },
                    //        Amount = new List<Amount>()
                    //        {
                    //            new Amount()
                    //            {
                    //                _ = 0,
                    //                currencyID = currencyCode,

                    //            }
                    //        }

                    //    }
                    //},

                    //Sum of amount payable (inclusive of applicable discounts and charges), excluding any applicable taxes (e.g., sales tax, service tax)”.                    
                    LineExtensionAmount = new List<LineExtensionAmount>()
                    {
                        new LineExtensionAmount()
                        {
                            _ = drItem.AmountIncTax??0, //Convert2NumTool<double>.ConvertVal(drItem["NetAmount"]),    //Amount                        
                            currencyID = currencyCode,
                        }
                    },
                    TaxTotal = taxs.ToList(),


                    Item = new List<Item>()
                    {
                        new Item()
                        {
                            CommodityClassification = new List<CommodityClassification>()
                            {
                                new CommodityClassification()
                                {
                                    ItemClassificationCode = new List<ItemClassificationCode>()
                                    {
                                        //from Item Master
                                        new ItemClassificationCode()
                                        {
                                            _ = classCode,
                                            listID = "CLASS"

                                        }
                                    }
                                }
                            },
                            Description = new List<Description>()
                            {
                                new Description()
                                {
                                    _ =  drItem.ItemDesc??"" //Convert2NumTool<string>.ConvertVal(drItem["IDesc"]).Replace("\n", " ").Replace("\r", " "),
                                }
                            }
                        }
                    },

                    Price = new List<Price>()
                    {
                        new Price()
                        {
                            PriceAmount = new List<PriceAmount>()
                            {
                                new PriceAmount()
                                {
                                    _ =  Math.Round(drItem.UnitPrice??0,2) ,// Convert2NumTool<double>.ConvertVal(drItem["UnitPrice"]), 2),
                                    currencyID = currencyCode,
                                }
                            },
                             //BaseQuantity = new List<BaseQuantity>()
                             //{
                             //     new BaseQuantity()
                             //     {
                             //            _ =  Convert2NumTool<double>.ConvertVal(drItem["StdQty"]),
                             //           unitCode = "C62"// Convert2NumTool<string>.ConvertVal(drItem["StdUOM"]),
                             //     }
                             //}
                        }
                    },


                    //Amount of each individual item / service within the invoice, excluding any taxes, charges or discounts
                    ItemPriceExtension = new List<ItemPriceExtension>()
                        {
                            new ItemPriceExtension()
                            {
                                 Amount = new List<Amount>()
                                 {
                                     new Amount()
                                     {
                                           _= drItem.AmountExclTax??0,// Convert2NumTool<double>.ConvertVal(drItem["Amount"]), //unit price * qty
                                           currencyID=  currencyCode,
                                     }
                                 }
                            }
                        },
                };

                if (drItem.DiscountAmount.HasValue && drItem.DiscountAmount > 0)
                {
                    invItem.AllowanceCharge = new List<AllowanceCharge>()
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
                                      _ ="Item Discount"
                                 }
                             },
                             MultiplierFactorNumeric = new List<MultiplierFactorNumeric>()
                             {
                                 new MultiplierFactorNumeric()
                                 {
                                      _= 1
                                 }
                             },
                             Amount = new List<Amount>()
                             {
                                   new Amount()
                                   {
                                       _ = drItem.DiscountAmount.Value,
                                       currencyID=  currencyCode,
                                   }
                             }
                        }
                    };
                }

                items.Add(invItem);
            }

            return items;
        }
    }
}
