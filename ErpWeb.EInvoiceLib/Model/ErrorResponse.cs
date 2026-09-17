using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class ErrorSubmit
    {
        public ErrorRespone error { get; set; }
    }

    public class ErrorRespone
    {
        //Error code that client must be able to handle.
        public string code { get; set; }

        //Human readable error message. If available, message is returned in user’s preferred language.
        public string message { get; set; }

        //Optional: the target/subject of the error, e.g., parameter that caused the error. eg passwor        
        public string target { get; set; }

        //Optional: list of multiple errors detected that caused overall API call to fail. Used to return all errors in one go so that they all can be corrected by the caller before calling again. Each Error object is of the same structure as defined in this table that allows composition of multiple errors received.
        public List<ErrorDetail> details { get; set; }

    }

    public class ErrorDetail
    {
        public object code { get; set; }
        public string message { get; set; }
        public string target { get; set; }
        public object propertyPath { get; set; }
        public object details { get; set; }
    }

    public class InnerErrorMsg
    {
        public string PropertyName { get; set; }
        public string PropertyPath { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string ErrorMs { get; set; }
        public string MetaData { get; set; }
        public object InnerError { get; set; }
    }

    public class DocErrorDetail
    {
        public string PropertyName { get; set; }
        public string PropertyPath { get; set; }
        public string ErrorCode { get; set; }
        public string Error { get; set; }
        public string ErrorMs { get; set; }
        public string MetaData { get; set; }
        public List<InnerErrorMsg> InnerError { get; set; }
    }
}
