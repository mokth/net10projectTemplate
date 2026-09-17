using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class SignatureData
    {
        public string SignatureValue { get; set; }
        public string PropsDigest { get; set; }
        public string DocDigest { get; set; }
        public string CertDigest { get; set; }
        public string SigningTime { get; set; }
        public string X509Certificate { get; set; }
        public string X509IssuerName { get; set; }
        public BigInteger X509SerialNumber { get; set; }
        public string X509SubjectName { get; set; }
        public string RSAKey_Modulus { get; set; }
        public string RSAKey_Exponent { get; set; }
    }
}
