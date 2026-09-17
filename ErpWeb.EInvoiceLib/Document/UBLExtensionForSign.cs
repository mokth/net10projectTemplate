using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Document
{
    public class UBLExtensionForSign
    {
        public List<UBLExtension> UBLExtension { get; set; }
    }

    public class AnExtension
    {
        public string _ { get; set; }
    }

    public class AnotherExtension
    {
        public string _ { get; set; }
    }

    public class CanonicalizationMethod
    {
        public string Algorithm { get; set; }
        public string _ { get; set; }
    }

    public class Cert
    {
        public List<CertDigest> CertDigest { get; set; }
        public List<IssuerSerial> IssuerSerial { get; set; }
    }

    public class CertDigest
    {
        public List<DigestMethod> DigestMethod { get; set; }
        public List<DigestValue> DigestValue { get; set; }
    }

    public class DigestMethod
    {
        public string _ { get; set; }
        public string Algorithm { get; set; }
    }

    public class DigestValue
    {
        public string _ { get; set; }
    }

    public class Exponent
    {
        public string _ { get; set; }
    }

    public class ExtensionContent
    {
        //public List<AnExtension> AnExtension { get; set; }
        //public List<AnotherExtension> AnotherExtension { get; set; }
        public List<UBLDocumentSignature> UBLDocumentSignatures { get; set; }
    }

    public class ExtensionURI
    {
        public string _ { get; set; }
    }

    public class IssuerSerial
    {
        public List<X509IssuerName> X509IssuerName { get; set; }
        public List<X509SerialNumber> X509SerialNumber { get; set; }
    }

    public class KeyInfo
    {
        public List<KeyValue> KeyValue { get; set; }
        public List<X509Datum> X509Data { get; set; }
    }

    public class KeyValue
    {
        public List<RSAKeyValue> RSAKeyValue { get; set; }
    }

    public class Modulu
    {
        public string _ { get; set; }
    }

    public class Object
    {
        public List<QualifyingProperty> QualifyingProperties { get; set; }
    }

    public class QualifyingProperty
    {
        public string Target { get; set; }
        public List<SignedProperty> SignedProperties { get; set; }
        public List<UnsignedProperty> UnsignedProperties { get; set; }
    }

    public class Reference
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string URI { get; set; }

        public List<TransformRoot> Transforms { get; set; }
        public List<DigestMethod> DigestMethod { get; set; }
        public List<DigestValue> DigestValue { get; set; }
    }

    public class ReferencedSignatureID
    {

        public string _ { get; set; }

    }

    public class RSAKeyValue
    {
        public List<Modulu> Modulus { get; set; }
        public List<Exponent> Exponent { get; set; }
    }

    public class Signature
    {
        public string Id { get; set; } = "signature";
        public List<SignedInfo> SignedInfo { get; set; }
        public List<SignatureValue> SignatureValue { get; set; }
        public List<KeyInfo> KeyInfo { get; set; }
        public List<Object> Object { get; set; }
    }

    public class SignatureInformation
    {
        public List<ID> ID { get; set; }
        public List<ReferencedSignatureID> ReferencedSignatureID { get; set; }
        public List<Signature> Signature { get; set; }
    }

    public class SignatureMethod
    {
        public string _ { get; set; }
        public string Algorithm { get; set; } = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";

    }

    public class SignatureTimeStamp
    {
        public List<XMLTimeStamp> XMLTimeStamp { get; set; }
    }

    public class SignatureValue
    {
        public string _ { get; set; }
    }

    public class SignedInfo
    {
        public List<CanonicalizationMethod> CanonicalizationMethod { get; set; }
        public List<SignatureMethod> SignatureMethod { get; set; }

        public List<Reference> Reference { get; set; }


    }

    public class SignedProperty
    {
        public string Id { get; set; }
        public List<SignedSignatureProperty> SignedSignatureProperties { get; set; }
    }

    public class SignedSignatureProperty
    {
        public List<SigningTime> SigningTime { get; set; }
        public List<SigningCertificate> SigningCertificate { get; set; }
    }

    public class SigningCertificate
    {
        public List<Cert> Cert { get; set; }
    }

    public class SigningTime
    {
        public string _ { get; set; }
    }

    public class TransformRoot
    {
        public List<Transform> Transforms { get; set; }
    }

    public class Transform
    {
        public string Algorithm { get; set; }
        public List<XPath> XPath { get; set; }
    }

    public class UBLDocumentSignature
    {
        public List<SignatureInformation> SignatureInformation { get; set; }
    }

    //public class UBLExtensionRoot
    //{
    //    public List<UBLExtension> UBLExtensions { get; set; }
    //}

    public class UBLExtension
    {
        public List<ExtensionURI> ExtensionURI { get; set; }
        public List<ExtensionContent> ExtensionContent { get; set; }
    }

    public class UnsignedProperty
    {
        public List<UnsignedSignatureProperty> UnsignedSignatureProperties { get; set; }
    }

    public class UnsignedSignatureProperty
    {
        public List<SignatureTimeStamp> SignatureTimeStamp { get; set; }
    }

    public class UBLX509Certificate
    {
        public string _ { get; set; }
    }

    public class X509Datum
    {
        public List<UBLX509Certificate> X509Certificate { get; set; }
        public List<X509SubjectName> X509SubjectName { get; set; }
        public List<X509IssuerSerial> X509IssuerSerial { get; set; }
    }

    public class X509IssuerName
    {
        public string _ { get; set; }
    }

    public class X509IssuerSerial
    {
        public List<X509IssuerName> X509IssuerName { get; set; }
        public List<X509SerialNumber> X509SerialNumber { get; set; }
    }

    public class X509SerialNumber
    {
        public string _ { get; set; }
    }

    public class X509SubjectName
    {
        public string _ { get; set; }
    }

    public class XMLTimeStamp
    {
        public string _ { get; set; }
    }

    public class XPath
    {
        public string _ { get; set; }
    }
}
