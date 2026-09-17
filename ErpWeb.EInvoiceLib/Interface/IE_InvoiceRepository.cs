using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.EInvoiceLib.Interface
{
    public interface IE_InvoiceRepository
    {
        Task<GeneralResult<CancelRespone>> CancelDocument(CancelDocument doc, List<string> uuid);
        Task<GeneralResult<AllDocumentType>> getAllDocumentType();
        Task<GeneralResult<DocumentInfo>> getDocument(string uuid);
        Task<GeneralResult<DocumentValidatation>> getDocumentDetail(string uuid);
        Task<GeneralResult<DocumentTypeInfo>> getDocumentType(int docID);
        Task<GeneralResult<DocumentTypeVersion>> getDocumenTypeVersion(int docID, int vid);
        Task<GeneralResult<NotificationResp>> getNotification(NotificationInput query);
        Task<GeneralResult<RecentDocument>> getRecentDocument(RecentDocumentInput query);
        Task<GeneralResult<Submission>> getSubmission(string submissionUid);
        Task<string> GetToken();
        Task<GeneralResult<string>> Login();
        Task<GeneralResult<string>> Login(string onbehalfof);
        Task<GeneralResult<CancelRespone>> RejectDocument(CancelDocument doc, List<string> uuid);
        Task<GeneralResult<RecentDocument>> searchDocument(SearchDocumentInput query);
        Task<GeneralResult<SuccessSubmit>> SubmitDocument(SubmitDocument doc);
        Task<GeneralResult<bool>> ValidateTin(string tin, string idType, string idValue);
        Task<GeneralResult<List<Model.Document.TINInfo>>> SearchTin(Model.Document.SearchTINInput query);
        Task<GeneralResult<SuccessSubmit>> SubmitDocumentWithTin(SubmitDocument doc, string TINNO);
    }
}