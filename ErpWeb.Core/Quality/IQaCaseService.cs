namespace ErpWeb.Core.Quality;

public interface IQaCaseService
{
    Task<QaResult> SearchAsync(QaListQuery query, CancellationToken cancellationToken = default);

    Task<QaResult> GetInquiryAsync(QaInquiryQuery query, CancellationToken cancellationToken = default);

    Task<QaResult> GetAsync(string caseNo, CancellationToken cancellationToken = default);

    Task<QaResult> SaveDraftAsync(QaCaseSaveCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> SubmitAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> StartInspectionAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> SaveInspectionAsync(QaInspectionSaveCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> SubmitForReviewAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> ApproveAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> RejectAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> CloseAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> ReleaseHoldAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> CancelAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> DeleteDraftAsync(QaReviewCommand command, CancellationToken cancellationToken = default);

    Task<QaResult> SearchSourcesAsync(QaSourceQuery query, CancellationToken cancellationToken = default);
}
