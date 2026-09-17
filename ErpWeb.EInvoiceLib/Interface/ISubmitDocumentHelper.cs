using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.EInvoiceLib.Interface
{
    public interface ISubmitDocumentHelper
    {
        public void SetOnBehalfTin(string onBehalf);
        Task<GeneralResult<CancelRespone>> CancelDocument(CancelDocument doc, List<string> uuid);
        Task<GeneralResult<Submission>> GetCDNSubmission(string submissionUid);
        Task<GeneralResult<DocumentInfo>> GetDocument(string uuid);
        Task<GeneralResult<DocumentValidatation>> GetDocumentDetail(string uuid);
        Task<GeneralResult<NotificationResp>> GetNotification(NotificationInput query);
        Task<GeneralResult<RecentDocument>> GetRecentDocument(RecentDocumentInput query);
        Task<GeneralResult<Submission>> GetSubmission(string submissionUid);
        Task<GeneralResult<CancelRespone>> RejectDocument(CancelDocument doc, List<string> uuid);
        Task<GeneralResult<RecentDocument>> SearchDocument(SearchDocumentInput query);
        void StoreJSonFile(string content, string docType, string docNo);
        Task<GeneralResult<SuccessSubmit>> SubmitCreditDebitNotes(List<DocumentHeader> infos);
        Task<GeneralResult<SuccessSubmit>> SubmitInvoices(List<DocumentHeader> infos);
        Task<GeneralResult<bool>> ValidateTin(string tin, IDType idType, string idValue);
        Task<GeneralResult<List<TINInfo>>> SearchTin(SearchTINInput query);
        Task<GeneralResult<SuccessSubmit>> SubmitInvoicesWithTIN(List<DocumentHeader> infos, string TINNO);
    }
}