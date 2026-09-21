using ErpWeb.EInvoiceLib.BL.Entity;

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

        // The four dbo.EInvDocSubmission methods were REMOVED here (plan Phase 4). ErpWeb owns that
        // table through AppDbContext + EInvoiceSubmissionWriter; keeping a second writer in this library
        // is exactly the drift this change removes. Token access above is unchanged.
    }
}