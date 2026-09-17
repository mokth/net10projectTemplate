using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using ErpWeb.EInvoiceLib.Model;

using ErpWeb.EInvoiceLib.Interface;
using System.Globalization;
using System.Numerics;
using ErpWeb.EInvoiceLib.Store;
using EInvoiceAPI.Utility;
using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.GenerateDoc;
using Object = ErpWeb.EInvoiceLib.Document.Object;

namespace ErpWeb.EInvoiceLib.Utility
{
    public class EInvSignatureHelper
    {
        string _certPath;
        X509Certificate2 cert;
        SignatureData signData;
        IClientSecretStore clientSecretStore;
        public EInvSignatureHelper(IClientSecretStore clientSecret)
        {
            clientSecretStore = clientSecret;
            string certPath = clientSecretStore.getCertPath();
            string certPass = clientSecretStore.getCertPass();
            signData = new SignatureData();
            //cert = new X509Certificate2(File.ReadAllBytes(certPath), certPass);
            cert = new X509Certificate2(File.ReadAllBytes(certPath), certPass, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);

            //cert.Import(File.ReadAllBytes(certPath), certPass, X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
        }


        public Root StartProcess(Root root, DateTime signTime)
        {
            // Step 1: Transform the document
            string jsonString = HashUtility.SerializeJson(root);
            // Step 2. Calculate the document digest
            byte[] docHash = HashUtility.Sha256Hash(jsonString);
            // Step 3: Sign the document digest using the certificate
            byte[] sign = HashUtility.SignData(docHash, cert);
            // Step 4: Calculate the certificate digest

            // byte[] rawcertbytes = cert.RawData;
            // byte[] certbytes = HashUtility.Sha256HashBytes(rawcertbytes);
            var rsaPublickey = cert.GetRSAPublicKey().ExportParameters(false);

            signData.RSAKey_Modulus = Convert.ToBase64String(rsaPublickey.Modulus);
            signData.RSAKey_Exponent = Convert.ToBase64String(rsaPublickey.Exponent);

            signData.CertDigest = HashUtility.GetCertHash(cert);// Convert.ToBase64String(certbytes);
            signData.DocDigest = Convert.ToBase64String(docHash);
            signData.SignatureValue = Convert.ToBase64String(sign);
            signData.X509Certificate = HashUtility.GetX509Certificate(cert);// Convert.ToBase64String(rawcertbytes); //cert.GetRawCertDataString();
            signData.X509IssuerName = cert.Issuer;
            signData.X509SerialNumber = HashUtility.GetCertSerialNumber(cert);// Int64.Parse(cert.SerialNumber, NumberStyles.HexNumber);
            signData.X509SubjectName = cert.Subject;
            signData.SigningTime = signTime.ToString("yyyy-MM-ddTHH:mm:ssZ");


            //Step 5: Populate the signed properties section
            var doc = root.Invoice[0];
            CreateUBLExtentions(doc);
            //Step 6: Calculate the signed properties section digest
            var SignedProperties = doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].
                                    UBLDocumentSignatures[0].SignatureInformation[0].
                                    Signature[0].Object[0].QualifyingProperties[0];//.SignedProperties[0];
            var jsonSignedProperties = HashUtility.SerializeJson(SignedProperties);
            var signProFileName = Path.Combine(clientSecretStore.getJSonFilePath(), "jsonSignedProperties.txt");
            File.WriteAllText(signProFileName, jsonSignedProperties);
            byte[] propCert = HashUtility.Sha256Hash(jsonSignedProperties);
            string PropsDigest = Convert.ToBase64String(propCert);
            var reference = doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].
                UBLDocumentSignatures[0].SignatureInformation[0].
                Signature[0].SignedInfo[0].Reference;
            var signRef = reference.Where(x => x.URI == "#id-xades-signed-props").FirstOrDefault();
            signRef.DigestValue[0]._ = PropsDigest;
            //doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].UBLDocumentSignatures[0].SignatureInformation[0]
            //   .Signature[0].Object[0].QualifyingProperties[0].SignedProperties[0].Id = "id-xades-signed-props";
            //Id = "id-xades-signed-props",    
            //if (signRef != null)
            //{
            //    signRef.DigestValue[0]._ = PropsDigest;
            //}
            //else
            //{
            //    doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].
            //        UBLDocumentSignatures[0].SignatureInformation[0].
            //        Signature[0].SignedInfo[0].Reference[0].DigestValue[0]._ = PropsDigest;
            //}

            // Complete the (previously empty) non-invoice branches. Type 01 resolves to the same
            // identifier as before, so the proven invoice signing output is unchanged.
            string SignatureID = EInvoiceDocumentTypeMap.GetSignatureId(doc.InvoiceTypeCode[0]._);

            root.Invoice[0].Signature = new List<SignatureInv>()
            {
                new SignatureInv()
                {
                     ID = new List<ID>()
                     {
                         new ID()
                         {
                              _ = SignatureID ,
                         }
                     }
                     , SignatureMethod = new List<SignatureMethodInv>()
                     {
                         new SignatureMethodInv()
                         {
                              _= "urn:oasis:names:specification:ubl:dsig:enveloped:xades"
                         }
                     },
                      //SignatoryParty = new List<SignatoryParty>()
                      //{
                      //    new SignatoryParty()
                      //    {
                      //         PartyIdentification = new List<PartyIdentification>()
                      //         {
                      //             new PartyIdentification()
                      //             {
                      //                  ID = new List<ID>()
                      //                  {
                      //                      new ID()
                      //                      {
                      //                          _= "MyParty"
                      //                      }
                      //                  }
                      //             }
                      //         }
                      //    }
                      //}
                }
            };

            return root;
        }

        public void CreateUBLExtentions(InvoiceJson doc)
        {

            var ublExtension = new List<UBLExtension>()
                    {
                        new UBLExtension()
                        {
                             ExtensionURI = new List<ExtensionURI>()
                             {
                                 new ExtensionURI()
                                 {
                                      _ ="urn:oasis:names:specification:ubl:dsig:enveloped:xades"

                                 }
                             },
                             ExtensionContent = new List<ExtensionContent>()
                             {
                                 new ExtensionContent()
                                 {
                                      UBLDocumentSignatures = new List<UBLDocumentSignature>()
                                      {
                                          new UBLDocumentSignature()
                                          {
                                               SignatureInformation= new List<SignatureInformation>()
                                               {
                                                   new SignatureInformation()
                                                   {
                                                        ID = new List<ID>()
                                                        {
                                                            new ID()
                                                            {
                                                                 _ = "urn:oasis:names:specification:ubl:signature:1"
                                                            }
                                                        },
                                                         ReferencedSignatureID = new List<ReferencedSignatureID>()
                                                         {
                                                              new ReferencedSignatureID()
                                                              {
                                                                   _ = "urn:oasis:names:specification:ubl:signature:Invoice"
                                                              }
                                                         },
                                                        Signature = new List<Signature>()
                                                        {
                                                             new Signature()
                                                             {
                                                                   Id = "signature",
                                                                   SignedInfo = new List<SignedInfo>()
                                                                   {
                                                                       new SignedInfo()
                                                                       {
                                                                            //only for XML
                                                                            //CanonicalizationMethod= new List<CanonicalizationMethod>()
                                                                            //{
                                                                            //    new CanonicalizationMethod()
                                                                            //    {
                                                                            //         Algorithm ="http://www.w3.org/2006/12/xml-c14n11",

                                                                            //    }
                                                                            //},
                                                                             SignatureMethod= new List<SignatureMethod>()
                                                                             {
                                                                                 new SignatureMethod()
                                                                                 {
                                                                                      _="",
                                                                                      Algorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"
                                                                                 }
                                                                             },
                                                                             Reference = new List<Reference>()
                                                                             {
                                                                                 new Reference()
                                                                                 {
                                                                                     Id = "id-doc-signed-data",
                                                                                     URI="",
                                                                                     Type="",

                                                                                     DigestMethod = new List<DigestMethod>()
                                                                                      {
                                                                                          new DigestMethod()
                                                                                          {
                                                                                               _="",
                                                                                               Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
                                                                                          }
                                                                                      },
                                                                                     DigestValue = new List<DigestValue>()
                                                                                     {
                                                                                          new DigestValue()
                                                                                          {
                                                                                               _= signData.DocDigest //"Doc Digest here"
                                                                                          }
                                                                                     }
                                                                                 }
                                                                                 ,
                                                                                 new Reference()
                                                                                 {
                                                                                     Type= "http://uri.etsi.org/01903/v1.3.2#SignedProperties",
                                                                                     URI = "#id-xades-signed-props",
                                                                                     Id = "id-xades-signed-props",
                                                                                     DigestMethod = new List<DigestMethod>()
                                                                                      {
                                                                                          new DigestMethod()
                                                                                          {
                                                                                               _="",
                                                                                              Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
                                                                                          }
                                                                                      },
                                                                                     DigestValue = new List<DigestValue>()
                                                                                     {
                                                                                          new DigestValue()
                                                                                          {
                                                                                               _= ""// "PropsDigest Digest here,at first it blank"
                                                                                          }
                                                                                     }
                                                                                 },


                                                                             },

                                                                       }
                                                                   },
                                                                   SignatureValue = new List<SignatureValue>()
                                                                   {
                                                                         new SignatureValue()
                                                                         {
                                                                              _ = signData.SignatureValue //"SignatureValue here"
                                                                         }
                                                                   },
                                                                   KeyInfo = new List<KeyInfo>()
                                                                   {
                                                                       new KeyInfo()
                                                                       {
                                                                            KeyValue = new List<KeyValue>()
                                                                            {
                                                                                new KeyValue()
                                                                                {
                                                                                     RSAKeyValue = new List<RSAKeyValue>()
                                                                                     {
                                                                                         new RSAKeyValue()
                                                                                         {
                                                                                              Modulus = new List<Modulu>()
                                                                                              {
                                                                                                  new Modulu()
                                                                                                  {
                                                                                                       _ = signData.RSAKey_Modulus // "modulu here"
                                                                                                  }
                                                                                              },
                                                                                              Exponent = new List<Exponent>()
                                                                                              {
                                                                                                 new Exponent()
                                                                                                 {
                                                                                                      _ = signData.RSAKey_Exponent// "Exponnet here"
                                                                                                 }
                                                                                              }
                                                                                         }
                                                                                     }
                                                                                }
                                                                            },
                                                                             X509Data = new List<X509Datum>()
                                                                             {
                                                                                  new X509Datum()
                                                                                  {
                                                                                       X509Certificate = new List<UBLX509Certificate>()
                                                                                       {
                                                                                           new UBLX509Certificate()
                                                                                           {
                                                                                                _ = signData.X509Certificate //"cert hash"
                                                                                           }
                                                                                       },
                                                                                        X509SubjectName = new List<X509SubjectName>()
                                                                                        {
                                                                                            new X509SubjectName()
                                                                                            {
                                                                                                 _= signData.X509SubjectName // " cert subject "
                                                                                            }
                                                                                        },
                                                                                         X509IssuerSerial = new List<X509IssuerSerial>()
                                                                                         {
                                                                                             new X509IssuerSerial()
                                                                                             {
                                                                                                  X509IssuerName = new List<X509IssuerName>()
                                                                                                  {
                                                                                                      new X509IssuerName()
                                                                                                      {
                                                                                                           _= signData.X509IssuerName //"Issue name"
                                                                                                      }
                                                                                                  },
                                                                                                   X509SerialNumber = new List<X509SerialNumber>()
                                                                                                   {
                                                                                                       new X509SerialNumber()
                                                                                                       {
                                                                                                            _ = signData.X509SerialNumber.ToString() //"serailnumber"
                                                                                                       }
                                                                                                   }
                                                                                             }
                                                                                         }

                                                                                  }
                                                                             }
                                                                       }
                                                                   }
                                                                   , Object = new List<Object>()
                                                                   {
                                                                       new Object()
                                                                       {
                                                                            QualifyingProperties = new List<QualifyingProperty>()
                                                                            {

                                                                                new QualifyingProperty()
                                                                                {
                                                                                    Target = "signature",

                                                                                     SignedProperties = new List<SignedProperty>()
                                                                                     {

                                                                                         new SignedProperty()
                                                                                         {
                                                                                           Id = "id-xades-signed-props",
                                                                                           SignedSignatureProperties = new List<SignedSignatureProperty>()
                                                                                              {
                                                                                                  new SignedSignatureProperty()
                                                                                                  {

                                                                                                       SigningTime = new List<SigningTime>()
                                                                                                       {
                                                                                                           new SigningTime()
                                                                                                           {
                                                                                                               _ = signData.SigningTime // "10-11-26T18:00:00Z"
                                                                                                           }
                                                                                                       },
                                                                                                        SigningCertificate = new List<SigningCertificate>()
                                                                                                        {
                                                                                                            new SigningCertificate()
                                                                                                            {
                                                                                                                 Cert = new List<Cert>()
                                                                                                                 {
                                                                                                                     new Cert()
                                                                                                                     {
                                                                                                                          CertDigest= new List<CertDigest>()
                                                                                                                          {
                                                                                                                              new CertDigest()
                                                                                                                              {
                                                                                                                                   DigestMethod = new List<DigestMethod>()
                                                                                                                                   {
                                                                                                                                       new DigestMethod()
                                                                                                                                       {
                                                                                                                                            _="",
                                                                                                                                            Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
                                                                                                                                       }
                                                                                                                                   },
                                                                                                                                    DigestValue = new List<DigestValue>()
                                                                                                                                    {
                                                                                                                                        new DigestValue()
                                                                                                                                        {
                                                                                                                                           _= signData.CertDigest // "CertDigest here"
                                                                                                                                        }
                                                                                                                                    }
                                                                                                                              }
                                                                                                                          }, IssuerSerial = new List<IssuerSerial>()
                                                                                                                          {
                                                                                                                              new IssuerSerial()
                                                                                                                              {
                                                                                                                                   X509IssuerName= new List<X509IssuerName>()
                                                                                                                                   {
                                                                                                                                        new X509IssuerName()
                                                                                                                                        {
                                                                                                                                             _ = signData.X509IssuerName
                                                                                                                                        }
                                                                                                                                   },
                                                                                                                                    X509SerialNumber = new List<X509SerialNumber>()
                                                                                                                                    {
                                                                                                                                        new X509SerialNumber()
                                                                                                                                        {
                                                                                                                                             _ =  signData.X509SerialNumber.ToString()
                                                                                                                                        }
                                                                                                                                    }
                                                                                                                              }
                                                                                                                          }
                                                                                                                     }
                                                                                                                 }

                                                                                                            }
                                                                                                        }
                                                                                                  }
                                                                                              }
                                                                                         }

                                                                                     }, 
                                                                                    //UnsignedProperties = new List<UnsignedProperty>()
                                                                                    // {
                                                                                    //     new UnsignedProperty()
                                                                                    //     {
                                                                                    //          UnsignedSignatureProperties = new List<UnsignedSignatureProperty>()
                                                                                    //          {
                                                                                    //              new UnsignedSignatureProperty()
                                                                                    //              {
                                                                                    //                   SignatureTimeStamp = new List<SignatureTimeStamp>()
                                                                                    //                   {
                                                                                    //                       new SignatureTimeStamp()
                                                                                    //                       {
                                                                                    //                            XMLTimeStamp = new List<XMLTimeStamp>()
                                                                                    //                            {
                                                                                    //                                new XMLTimeStamp()
                                                                                    //                                {
                                                                                    //                                     _ = signData.SigningTime//  "2010-11-26T18:00:00Z"
                                                                                    //                                }
                                                                                    //                            }
                                                                                    //                       }
                                                                                    //                   }
                                                                                    //              }
                                                                                    //          }
                                                                                    //     }
                                                                                    // }
                                                                                }
                                                                            }
                                                                       }
                                                                   }

                                                             }
                                                        }
                                                   }
                                               }
                                          }
                                      }
                                 }
                             }

                        }
                    };

            doc.UBLExtensions = new List<UBLExtensionForSign>()
            {
                 new UBLExtensionForSign()
                 {
                      UBLExtension =ublExtension
                 }
            };

        }

        //public static BigInteger GetCertSerialNumber(X509Certificate2 cert)
        //{
        //    return BigInteger.Parse(cert.SerialNumber, NumberStyles.HexNumber);

        //}

        //public Root StartProcess(Root root, DateTime signTime)
        //{
        //    string jsonString = SerializeJson(root);
        //    byte[] docHash = Sha256Hash(jsonString);
        //    byte[] sign = SignData(docHash);
        //    byte[] certbytes = Sha256Hash(cert.GetRawCertDataString());
        //    var rsaPublickey = cert.GetRSAPublicKey().ExportParameters(false);

        //    signData.RSAKey_Modulus = Convert.ToBase64String(rsaPublickey.Modulus);
        //    signData.RSAKey_Exponent = Convert.ToBase64String(rsaPublickey.Exponent);

        //    signData.CertDigest = Convert.ToBase64String(certbytes);
        //    signData.DocDigest = Convert.ToBase64String(docHash);
        //    signData.SignatureValue = Convert.ToBase64String(sign);
        //    signData.X509Certificate = cert.GetRawCertDataString();
        //    signData.X509IssuerName = cert.Issuer;
        //    signData.X509SerialNumber = GetCertSerialNumber(cert);
        //    signData.X509SubjectName = cert.Subject;
        //    signData.SigningTime = signTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
        //    var doc = root.Invoice[0];
        //    CreateUBLExtentions(doc);
        //    var SignedProperties = doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].UBLDocumentSignatures[0].SignatureInformation[0].Signature[0].Object[0].QualifyingProperties[0].SignedProperties;
        //    var jsonSignedProperties = SerializeJson(SignedProperties);
        //    byte[] propCert = Sha256Hash(jsonSignedProperties);
        //    string PropsDigest = Convert.ToBase64String(propCert);
        //    doc.UBLExtensions[0].UBLExtension[0].ExtensionContent[0].
        //        UBLDocumentSignatures[0].SignatureInformation[0].
        //        Signature[0].SignedInfo[0].Reference[1].DigestValue[0]._ = PropsDigest;

        //    string SignatureID = "urn:oasis:names:specification:ubl:signature:Invoice";
        //    if (doc.InvoiceTypeCode[0]._ == "01")
        //    {
        //        SignatureID = "urn:oasis:names:specification:ubl:signature:Invoice";
        //    }
        //    else if (doc.InvoiceTypeCode[0]._ == "02")
        //    {

        //    }
        //    else if (doc.InvoiceTypeCode[0]._ == "03")
        //    {

        //    }

        //    root.Invoice[0].Signature = new List<SignatureInv>()
        //    {
        //        new SignatureInv()
        //        {
        //             ID = new List<ID>()
        //             {
        //                 new ID()
        //                 {
        //                      _ = SignatureID ,
        //                 }
        //             }
        //             , SignatureMethod = new List<SignatureMethodInv>()
        //             {
        //                 new SignatureMethodInv()
        //                 {
        //                      _= "urn:oasis:names:specification:ubl:dsig:enveloped:xades"
        //                 }
        //             },
        //              //SignatoryParty = new List<SignatoryParty>()
        //              //{
        //              //    new SignatoryParty()
        //              //    {
        //              //         PartyIdentification = new List<PartyIdentification>()
        //              //         {
        //              //             new PartyIdentification()
        //              //             {
        //              //                  ID = new List<ID>()
        //              //                  {
        //              //                      new ID()
        //              //                      {
        //              //                          _= "MyParty"
        //              //                      }
        //              //                  }
        //              //             }
        //              //         }
        //              //    }
        //              //}
        //        }
        //    };

        //    return root;
        //}

        //public string SerializeJson(object doc)
        //{
        //    var settings = new JsonSerializerSettings
        //    {
        //        //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
        //        DateFormatString = "yyyy-MM-ddTH:mmZ",
        //        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        //        NullValueHandling = NullValueHandling.Ignore,
        //    };
        //    var jsonString = JsonConvert.SerializeObject(doc, settings);

        //    return jsonString;
        //}

        //public string SerializeJsonIndented(object doc)
        //{
        //    var settings = new JsonSerializerSettings
        //    {
        //        //DateFormatString = "yyyy-MM-ddTH:mm:ss.fffZ",
        //        DateFormatString = "yyyy-MM-ddTH:mmZ",
        //        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        //        NullValueHandling = NullValueHandling.Ignore,
        //        Formatting = Formatting.Indented
        //    };
        //    var jsonString = JsonConvert.SerializeObject(doc, settings);

        //    return jsonString;
        //}


        //public byte[] Sha256Hash(string text)
        //{
        //    SHA256 sha256 = SHA256.Create();
        //    byte[] byteData = Encoding.UTF8.GetBytes(text);
        //    var hashBytes = sha256.ComputeHash(byteData);

        //    return hashBytes;
        //}

        //public byte[] SignData(byte[] hashdata)
        //{
        //    byte[] signedData = null;
        //    //var hashdata= Sha256Hash(text);
        //    using (RSA rsa = cert.GetRSAPrivateKey())
        //    {
        //        try
        //        {
        //            signedData = rsa.SignData(
        //                hashdata,
        //                HashAlgorithmName.SHA256,
        //                RSASignaturePadding.Pkcs1);
        //        }
        //        catch (CryptographicException)
        //        {
        //            try
        //            {
        //                using (RSA rsaCng = new RSACng())
        //                {
        //                    rsaCng.ImportParameters(rsa.ExportParameters(true));

        //                    signedData = rsaCng.SignData(
        //                        hashdata,
        //                         HashAlgorithmName.SHA256,
        //                        RSASignaturePadding.Pkcs1);
        //                }
        //            }
        //            catch
        //            {
        //            }

        //            if (signedData == null)
        //            {
        //                // Let the original exception continue
        //                throw;
        //            }
        //        }
        //    }

        //    return signedData;
        //}


        //public void CreateUBLExtentions(InvoiceJson doc)
        //{

        //    var ublExtension = new List<UBLExtension>()
        //            {
        //                new UBLExtension()
        //                {
        //                     ExtensionURI = new List<ExtensionURI>()
        //                     {
        //                         new ExtensionURI()
        //                         {
        //                              _ ="urn:oasis:names:specification:ubl:dsig:enveloped:xades"

        //                         }
        //                     },
        //                     ExtensionContent = new List<ExtensionContent>()
        //                     {
        //                         new ExtensionContent()
        //                         {
        //                              UBLDocumentSignatures = new List<UBLDocumentSignature>()
        //                              {
        //                                  new UBLDocumentSignature()
        //                                  {
        //                                       SignatureInformation= new List<SignatureInformation>()
        //                                       {
        //                                           new SignatureInformation()
        //                                           {
        //                                                ID = new List<ID>()
        //                                                {
        //                                                    new ID()
        //                                                    {
        //                                                         _ = "urn:oasis:names:specification:ubl:signature:1"
        //                                                    }
        //                                                },
        //                                                 ReferencedSignatureID = new List<ReferencedSignatureID>()
        //                                                 {
        //                                                      new ReferencedSignatureID()
        //                                                      {
        //                                                           _ = "urn:oasis:names:specification:ubl:signature:Invoice"
        //                                                      }
        //                                                 },
        //                                                Signature = new List<Signature>()
        //                                                {
        //                                                     new Signature()
        //                                                     {
        //                                                           Id = "signature",
        //                                                           SignedInfo = new List<SignedInfo>()
        //                                                           {
        //                                                               new SignedInfo()
        //                                                               {
        //                                                                    //only for XML
        //                                                                    //CanonicalizationMethod= new List<CanonicalizationMethod>()
        //                                                                    //{
        //                                                                    //    new CanonicalizationMethod()
        //                                                                    //    {
        //                                                                    //         _=""
        //                                                                    //    }
        //                                                                    //},
        //                                                                     SignatureMethod= new List<SignatureMethod>()
        //                                                                     {
        //                                                                         new SignatureMethod()
        //                                                                         {
        //                                                                              _="",
        //                                                                              Algorithm = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256"
        //                                                                         }
        //                                                                     },
        //                                                                     Reference = new List<Reference>()
        //                                                                     {
        //                                                                         new Reference()
        //                                                                         {
        //                                                                             Id = "id-doc-signed-data",
        //                                                                             //only for xml
        //                                                                             //Transforms = new List<TransformRoot>()
        //                                                                             //{
        //                                                                             //    new TransformRoot()
        //                                                                             //    {
        //                                                                             //        Transforms = new List<Transform>()
        //                                                                             //        {
        //                                                                             //            new Transform()
        //                                                                             //            {
        //                                                                             //                 XPath = new List<XPath>()
        //                                                                             //                 {
        //                                                                             //                     new XPath()
        //                                                                             //                     {
        //                                                                             //                          _ ="\n            count(ancestor-or-self::sig:UBLDocumentSignatures |\n                  here()/ancestor::sig:UBLDocumentSignatures[1]) >\n            count(ancestor-or-self::sig:UBLDocumentSignatures)\n          "
        //                                                                             //                     }
        //                                                                             //                 }
        //                                                                             //            }
        //                                                                             //        }
        //                                                                             //    }
        //                                                                             //},
        //                                                                             DigestMethod = new List<DigestMethod>()
        //                                                                              {
        //                                                                                  new DigestMethod()
        //                                                                                  {
        //                                                                                       _="",
        //                                                                                       Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
        //                                                                                  }
        //                                                                              },
        //                                                                             DigestValue = new List<DigestValue>()
        //                                                                             {
        //                                                                                  new DigestValue()
        //                                                                                  {
        //                                                                                       _= signData.DocDigest //"Doc Digest here"
        //                                                                                  }
        //                                                                             }
        //                                                                         },
        //                                                                         new Reference()
        //                                                                         {
        //                                                                             Type= "http://uri.etsi.org/01903/v1.3.2#SignedProperties",
        //                                                                             URI ="#id-xades-signed-props",
        //                                                                             DigestMethod = new List<DigestMethod>()
        //                                                                              {
        //                                                                                  new DigestMethod()
        //                                                                                  {
        //                                                                                       _="",
        //                                                                                      Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
        //                                                                                  }
        //                                                                              },
        //                                                                             DigestValue = new List<DigestValue>()
        //                                                                             {
        //                                                                                  new DigestValue()
        //                                                                                  {
        //                                                                                       _= ""// "PropsDigest Digest here,at first it blank"
        //                                                                                  }
        //                                                                             }
        //                                                                         }

        //                                                                     }

        //                                                               }
        //                                                           },
        //                                                           SignatureValue = new List<SignatureValue>()
        //                                                           {
        //                                                                 new SignatureValue()
        //                                                                 {
        //                                                                      _ = signData.SignatureValue //"SignatureValue here"
        //                                                                 }
        //                                                           },
        //                                                           KeyInfo = new List<KeyInfo>()
        //                                                           {
        //                                                               new KeyInfo()
        //                                                               {
        //                                                                    KeyValue = new List<KeyValue>()
        //                                                                    {
        //                                                                        new KeyValue()
        //                                                                        {
        //                                                                             RSAKeyValue = new List<RSAKeyValue>()
        //                                                                             {
        //                                                                                 new RSAKeyValue()
        //                                                                                 {
        //                                                                                      Modulus = new List<Modulu>()
        //                                                                                      {
        //                                                                                          new Modulu()
        //                                                                                          {
        //                                                                                               _ = signData.RSAKey_Modulus // "modulu here"
        //                                                                                          }
        //                                                                                      },
        //                                                                                      Exponent = new List<Exponent>()
        //                                                                                      {
        //                                                                                         new Exponent()
        //                                                                                         {
        //                                                                                              _ = signData.RSAKey_Exponent// "Exponnet here"
        //                                                                                         }
        //                                                                                      }
        //                                                                                 }
        //                                                                             }
        //                                                                        }
        //                                                                    },
        //                                                                     X509Data = new List<X509Datum>()
        //                                                                     {
        //                                                                          new X509Datum()
        //                                                                          {
        //                                                                               X509Certificate = new List<UBLX509Certificate>()
        //                                                                               {
        //                                                                                   new UBLX509Certificate()
        //                                                                                   {
        //                                                                                        _ = signData.X509Certificate //"cert hash"
        //                                                                                   }
        //                                                                               },
        //                                                                                X509SubjectName = new List<X509SubjectName>()
        //                                                                                {
        //                                                                                    new X509SubjectName()
        //                                                                                    {
        //                                                                                         _= signData.X509SubjectName // " cert subject "
        //                                                                                    }
        //                                                                                },
        //                                                                                 X509IssuerSerial = new List<X509IssuerSerial>()
        //                                                                                 {
        //                                                                                     new X509IssuerSerial()
        //                                                                                     {
        //                                                                                          X509IssuerName = new List<X509IssuerName>()
        //                                                                                          {
        //                                                                                              new X509IssuerName()
        //                                                                                              {
        //                                                                                                   _= signData.X509IssuerName //"Issue name"
        //                                                                                              }
        //                                                                                          },
        //                                                                                           X509SerialNumber = new List<X509SerialNumber>()
        //                                                                                           {
        //                                                                                               new X509SerialNumber()
        //                                                                                               {
        //                                                                                                    _ = signData.X509SerialNumber.ToString() //"serailnumber"
        //                                                                                               }
        //                                                                                           }
        //                                                                                     }
        //                                                                                 }

        //                                                                          }
        //                                                                     }
        //                                                               }
        //                                                           }
        //                                                           , Object = new List<Object>()
        //                                                           {
        //                                                               new Object()
        //                                                               {
        //                                                                    QualifyingProperties = new List<QualifyingProperty>()
        //                                                                    {

        //                                                                        new QualifyingProperty()
        //                                                                        {
        //                                                                            Target = "signature",
        //                                                                             SignedProperties = new List<SignedProperty>()
        //                                                                             {
        //                                                                                 new SignedProperty()
        //                                                                                 {
        //                                                                                      SignedSignatureProperties = new List<SignedSignatureProperty>()
        //                                                                                      {
        //                                                                                          new SignedSignatureProperty()
        //                                                                                          {
        //                                                                                               SigningTime = new List<SigningTime>()
        //                                                                                               {
        //                                                                                                   new SigningTime()
        //                                                                                                   {
        //                                                                                                       _ = signData.SigningTime // "10-11-26T18:00:00Z"
        //                                                                                                   }
        //                                                                                               },
        //                                                                                                SigningCertificate = new List<SigningCertificate>()
        //                                                                                                {
        //                                                                                                    new SigningCertificate()
        //                                                                                                    {
        //                                                                                                         Cert = new List<Cert>()
        //                                                                                                         {
        //                                                                                                             new Cert()
        //                                                                                                             {
        //                                                                                                                  CertDigest= new List<CertDigest>()
        //                                                                                                                  {
        //                                                                                                                      new CertDigest()
        //                                                                                                                      {
        //                                                                                                                           DigestMethod = new List<DigestMethod>()
        //                                                                                                                           {
        //                                                                                                                               new DigestMethod()
        //                                                                                                                               {
        //                                                                                                                                    _="",
        //                                                                                                                                    Algorithm = "http://www.w3.org/2001/04/xmlenc#sha256"
        //                                                                                                                               }
        //                                                                                                                           },
        //                                                                                                                            DigestValue = new List<DigestValue>()
        //                                                                                                                            {
        //                                                                                                                                new DigestValue()
        //                                                                                                                                {
        //                                                                                                                                   _= signData.CertDigest // "CertDigest here"
        //                                                                                                                                }
        //                                                                                                                            }
        //                                                                                                                      }
        //                                                                                                                  }, IssuerSerial = new List<IssuerSerial>()
        //                                                                                                                  {
        //                                                                                                                      new IssuerSerial()
        //                                                                                                                      {
        //                                                                                                                           X509IssuerName= new List<X509IssuerName>()
        //                                                                                                                           {
        //                                                                                                                                new X509IssuerName()
        //                                                                                                                                {
        //                                                                                                                                     _ = signData.X509IssuerName
        //                                                                                                                                }
        //                                                                                                                           },
        //                                                                                                                            X509SerialNumber = new List<X509SerialNumber>()
        //                                                                                                                            {
        //                                                                                                                                new X509SerialNumber()
        //                                                                                                                                {
        //                                                                                                                                     _ = signData.X509SerialNumber.ToString()
        //                                                                                                                                }
        //                                                                                                                            }
        //                                                                                                                      }
        //                                                                                                                  }
        //                                                                                                             }
        //                                                                                                         }

        //                                                                                                    }
        //                                                                                                }
        //                                                                                          }
        //                                                                                      }
        //                                                                                 }

        //                                                                             }, UnsignedProperties = new List<UnsignedProperty>()
        //                                                                             {
        //                                                                                 new UnsignedProperty()
        //                                                                                 {
        //                                                                                      UnsignedSignatureProperties = new List<UnsignedSignatureProperty>()
        //                                                                                      {
        //                                                                                          new UnsignedSignatureProperty()
        //                                                                                          {
        //                                                                                               SignatureTimeStamp = new List<SignatureTimeStamp>()
        //                                                                                               {
        //                                                                                                   new SignatureTimeStamp()
        //                                                                                                   {
        //                                                                                                        XMLTimeStamp = new List<XMLTimeStamp>()
        //                                                                                                        {
        //                                                                                                            new XMLTimeStamp()
        //                                                                                                            {
        //                                                                                                                 _ = signData.SigningTime//  "2010-11-26T18:00:00Z"
        //                                                                                                            }
        //                                                                                                        }
        //                                                                                                   }
        //                                                                                               }
        //                                                                                          }
        //                                                                                      }
        //                                                                                 }
        //                                                                             }
        //                                                                        }
        //                                                                    }
        //                                                               }
        //                                                           }

        //                                                     }
        //                                                }
        //                                           }
        //                                       }
        //                                  }
        //                              }
        //                         }
        //                     }

        //                }
        //            };

        //    doc.UBLExtensions = new List<UBLExtensionForSign>()
        //    {
        //         new UBLExtensionForSign()
        //         {
        //              UBLExtension =ublExtension
        //         }
        //    };

        //}

    }
}
