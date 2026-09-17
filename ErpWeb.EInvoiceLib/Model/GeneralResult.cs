using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class GeneralResult<T>
    {
        public bool IsSuccess { get; set; }
        public string error { get; set; }
        public string errorCode { get; set; }
        public T result { get; set; }
    }
}
