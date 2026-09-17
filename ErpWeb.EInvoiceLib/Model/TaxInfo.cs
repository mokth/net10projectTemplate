using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class TaxInfo
    {
        public string TaxCode { get; set; }

        //Taxable Types
        //https://sdk.myinvois.hasil.gov.my/codes/tax-types/
        public string TaxType { get; set; }
        public double TaxAmount { get; set; }
        public double TaxableAmount { get; set; }
        public double TaxPercent { get; set; }
        public bool IsInclusive { get; set; }
    }
}
