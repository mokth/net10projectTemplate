using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ErpWeb.EInvoiceLib.Model
{
    public class AllDocumentType
    {
        List<DocumentTypeInfo> result { get; set; }
    }
    public class DocumentTypeInfo
    {
        //Unique identifier of the document type
        public int id { get; set; }
        //The document type code from the possible values: 01,02,03,04,11,12,13,14
        public int invoiceTypeCode { get; set; }
        //Description of the document type
        public string description { get; set; }
        //Date when the activity of the document type started eg 2015-02-13T13:15Z
        public DateTime? activeFrom { get; set; }
        //Optional: date when the activity of the document type ends
        public DateTime? activeTo { get; set; }

        //Array of document type version summary objects
        public List<DocumentTypeVersions> documentTypeVersions { get; set; }
        public List<WorkflowParameter> workflowParameters { get; set; }
    }


    public class WorkflowParameter
    {
        //Unique id of the parameter for this document type
        public int id { get; set; }

        //Name of the parameter. Can be submissionDuration or cancellationDuration or rejectionDuration
        //eg Rejection time limit in hours
        public string parameter { get; set; }

        //Parameter value
        public int value { get; set; }

        //Date when the activity of the document type started eg 2015-02-13T13:15Z
        public DateTime? activeFrom { get; set; }
        //Optional: date when the activity of the document type ends
        public DateTime? activeTo { get; set; }
    }

    public class DocumentTypeVersions
    {
        //Unique identifier of the document type version
        public int id { get; set; }
        //Unique name of the document type version
        public string name { get; set; }
        //Description of the document type version
        public string description { get; set; }
        //Date when the activity of the document type started eg 2015-02-13T13:15Z
        public DateTime? activeFrom { get; set; }
        //Optional: date when the activity of the document type ends
        public DateTime? activeTo { get; set; }
        //Unique version number of the document type version eg 1.1
        public decimal? versionNumber { get; set; }
        //Status of the document type version - draft, published, deactivated
        public string status { get; set; }
    }


    public class DocumentTypeVersion
    {

        //The document type code from the possible values: 01,02,03,04,11,12,13,14
        public int invoiceTypeCode { get; set; }
        //Name of the document type version within the document type that can be used in document submission to identify document type version being submitted
        public string name { get; set; }
        //Description of the document type
        public string description { get; set; }
        //Unique version number of the document type version eg 1.1
        public decimal? versionNumber { get; set; }
        //Status of the document type version - draft, published, deactivated
        public string status { get; set; }
        //Date when the activity of the document type started eg 2015-02-13T13:15Z
        public DateTime? activeFrom { get; set; }
        //Optional: date when the activity of the document type ends
        public DateTime? activeTo { get; set; }
        //Base64 encoded JSON schema definition defining the technical structure of the JSON representation of the document type in this version. System will support UBL 2.1 standard schemas.
        public string jsonSchema { get; set; }
        //Base64 encoded XML schema definition defining the technical structure of the XML representation of the document type in this version. System will support UBL 2.1 standard schemas.
        public string xmlSchema { get; set; }
    }
}
