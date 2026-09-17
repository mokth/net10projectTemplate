using ErpWeb.EInvoiceLib.BL.Entity;
using ErpWeb.EInvoiceLib.Model.Document;

namespace ErpWeb.EInvoiceLib.Interface
{
    public interface IEInvoiceRepository
    {
        void Dispose();
        Task<EInvToken?> GetAdTokenAsync();
        Task<string> GetTokenAsync();
        Task InsertAdTokenAsync(EInvToken token);

        Task<EInvTokenOwn?> GetAdTokenOwnAsync();
        Task<string> GetTokenOwnAsync();
        Task InsertAdTokenOwnAsync(EInvTokenOwn token);
        Task AddSubmissionAsync(SuccessSubmit submit);
        Task<EInvDocSubmission?> getSubmissionAsync(string submissionID, string uuid);
        Task<EInvDocSubmission?> getSubmissionByCompIdAsync(string companyID, string documentNo, int docID);
        Task UpdateSubmissionAsync(Submission submit);
    }
}