using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Document
{
    public enum DocumentStatus
    {
        Submitted = 0,
        Valid,
        Invalid,
        Cancelled
    }

    public enum IDType
    {
        NRIC,
        BRN,
        PASSPORT,
        ARMY
    }
}
