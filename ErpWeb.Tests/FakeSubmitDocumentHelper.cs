using ErpWeb.EInvoiceLib.Document;
using ErpWeb.EInvoiceLib.Interface;
using ErpWeb.EInvoiceLib.Model;
using ErpWeb.EInvoiceLib.Model.Document;
using ErpWeb.EInvoiceLib.Model.InputData;

namespace ErpWeb.Tests;

/// <summary>
/// Scriptable stand-in for the MyInvois helper.
///
/// <para>
/// Every call is recorded in <see cref="Calls"/>. That is the point of the fake: the production rules
/// under test are mostly about what must <b>not</b> reach MyInvois (a duplicate submit, a retry with an
/// unknown outcome, a CN whose origin is not valid), so the assertions are on the call log as much as
/// on the returned status.
/// </para>
/// </summary>
internal sealed class FakeSubmitDocumentHelper : ISubmitDocumentHelper
{
    /// <summary>Method names in call order, e.g. <c>SubmitInvoices</c>, <c>GetSubmission</c>.</summary>
    public List<string> Calls { get; } = [];

    public List<DocumentHeader> Submitted { get; } = [];

    /// <summary>
    /// Raised while a submit is "at MyInvois", so a test can simulate another session changing the row
    /// under the in-flight submission.
    /// </summary>
    public Action? OnSubmitCalled { get; set; }

    public string? LastOnBehalfTin { get; private set; }

    public Func<List<DocumentHeader>, GeneralResult<SuccessSubmit>> SubmitHandler { get; set; } = DefaultSubmit;

    public Func<List<DocumentHeader>, GeneralResult<SuccessSubmit>> NoteSubmitHandler { get; set; } = DefaultSubmit;

    public Func<List<DocumentHeader>, GeneralResult<SuccessSubmit>> SubmitWithTinHandler { get; set; } = DefaultSubmit;

    public Func<string, GeneralResult<Submission>> SubmissionHandler { get; set; } =
        _ => Success(new Submission { submissionUid = "SUB-1", documentSummary = [] });

    public Func<SearchDocumentInput, GeneralResult<RecentDocument>> SearchHandler { get; set; } =
        _ => Success(new RecentDocument { result = [] });

    public Func<string, GeneralResult<DocumentValidatation>> DocumentDetailHandler { get; set; } =
        _ => Success(new DocumentValidatation { status = "Valid", dateTimeValidated = DateTime.UtcNow });

    public Func<CancelDocument, GeneralResult<CancelRespone>> CancelHandler { get; set; } =
        _ => Success(new CancelRespone { status = "cancelled" });

    public Func<string, IDType, string, GeneralResult<bool>> ValidateTinHandler { get; set; } =
        (_, _, _) => Success(true);

    public Func<SearchTINInput, GeneralResult<List<TINInfo>>> SearchTinHandler { get; set; } =
        _ => Success<List<TINInfo>>([]);

    public string? LastValidatedTin { get; private set; }

    public string? LastValidatedIdType { get; private set; }

    public SearchTINInput? LastSearchTinQuery { get; private set; }

    public int SubmitCallCount =>
        Calls.Count(x => x is "SubmitInvoices" or "SubmitCreditDebitNotes" or "SubmitInvoicesWithTIN");

    // ─────────────────────────── myInvois response builders ───────────────────────────

    public static GeneralResult<SuccessSubmit> Accepted(string codeNumber, string uuid, string submissionUid = "SUB-1") =>
        new()
        {
            IsSuccess = true,
            result = new SuccessSubmit
            {
                submissionUID = submissionUid,
                acceptedDocuments = [new AcceptedDocuments { invoiceCodeNumber = codeNumber, uuid = uuid }]
            }
        };

    public static GeneralResult<SuccessSubmit> Rejected(string codeNumber, string message = "Classification code is not valid.") =>
        new()
        {
            IsSuccess = false,
            result = new SuccessSubmit
            {
                submissionUID = "SUB-REJ",
                rejectedDocuments =
                [
                    new RejectedDocuments
                    {
                        invoiceCodeNumber = codeNumber,
                        error = new ErrorRespone { code = "400", message = message }
                    }
                ]
            }
        };

    /// <summary>A blank token: the request never left the process.</summary>
    public static GeneralResult<SuccessSubmit> NeverSent() =>
        new() { IsSuccess = false, error = "Invalid JWT Token! Token is blank." };

    /// <summary>A submitted document that MyInvois later validated.</summary>
    public static GeneralResult<Submission> SubmissionWith(string documentNo, string status, string? uuid = null) =>
        new()
        {
            IsSuccess = true,
            result = new Submission
            {
                submissionUid = "SUB-1",
                overallStatus = "valid",
                documentSummary =
                [
                    new DocumentSummary
                    {
                        internalId = documentNo,
                        submissionUid = "SUB-1",
                        uuid = uuid ?? "UUID-" + documentNo,
                        status = status,
                        dateTimeValidated = DateTime.UtcNow
                    }
                ]
            }
        };

    /// <summary>The submission finished and this document was not part of it.</summary>
    public static GeneralResult<Submission> SubmissionWithout(string documentNo) =>
        new()
        {
            IsSuccess = true,
            result = new Submission
            {
                submissionUid = "SUB-1",
                overallStatus = "valid",
                documentSummary =
                [
                    new DocumentSummary { internalId = documentNo + "-OTHER", uuid = "UUID-OTHER", status = "Valid" }
                ]
            }
        };

    public static GeneralResult<RecentDocument> SearchFound(string documentNo, string status, string? uuid = null) =>
        Success(new RecentDocument
        {
            result =
            [
                new DocumentSummary
                {
                    internalId = documentNo,
                    submissionUid = "SUB-1",
                    uuid = uuid ?? "UUID-" + documentNo,
                    status = status,
                    dateTimeValidated = DateTime.UtcNow
                }
            ]
        });

    public static GeneralResult<RecentDocument> SearchEmpty() => Success(new RecentDocument { result = [] });

    public static GeneralResult<T> Success<T>(T value) => new() { IsSuccess = true, result = value };

    public static GeneralResult<T> Failure<T>(string error, string? errorCode = null) =>
        new() { IsSuccess = false, error = error, errorCode = errorCode };

    // ─────────────────────────── ISubmitDocumentHelper ───────────────────────────

    public void SetOnBehalfTin(string onBehalf) => LastOnBehalfTin = onBehalf;

    public void StoreJSonFile(string content, string docType, string docNo)
    {
    }

    public Task<GeneralResult<SuccessSubmit>> SubmitInvoices(List<DocumentHeader> infos)
    {
        Calls.Add(nameof(SubmitInvoices));
        Submitted.AddRange(infos);
        OnSubmitCalled?.Invoke();
        return Task.FromResult(SubmitHandler(infos));
    }

    public Task<GeneralResult<SuccessSubmit>> SubmitCreditDebitNotes(List<DocumentHeader> infos)
    {
        Calls.Add(nameof(SubmitCreditDebitNotes));
        Submitted.AddRange(infos);
        OnSubmitCalled?.Invoke();
        return Task.FromResult(NoteSubmitHandler(infos));
    }

    public Task<GeneralResult<SuccessSubmit>> SubmitInvoicesWithTIN(List<DocumentHeader> infos, string TINNO)
    {
        Calls.Add(nameof(SubmitInvoicesWithTIN));
        Submitted.AddRange(infos);
        OnSubmitCalled?.Invoke();
        return Task.FromResult(SubmitWithTinHandler(infos));
    }

    public Task<GeneralResult<CancelRespone>> CancelDocument(CancelDocument doc, List<string> uuid)
    {
        Calls.Add(nameof(CancelDocument));
        return Task.FromResult(CancelHandler(doc));
    }

    public Task<GeneralResult<CancelRespone>> RejectDocument(CancelDocument doc, List<string> uuid)
    {
        Calls.Add(nameof(RejectDocument));
        return Task.FromResult(CancelHandler(doc));
    }

    public Task<GeneralResult<Submission>> GetSubmission(string submissionUid)
    {
        Calls.Add(nameof(GetSubmission));
        return Task.FromResult(SubmissionHandler(submissionUid));
    }

    public Task<GeneralResult<Submission>> GetCDNSubmission(string submissionUid)
    {
        Calls.Add(nameof(GetCDNSubmission));
        return Task.FromResult(SubmissionHandler(submissionUid));
    }

    public Task<GeneralResult<DocumentInfo>> GetDocument(string uuid)
    {
        Calls.Add(nameof(GetDocument));
        return Task.FromResult(Success(new DocumentInfo { uuid = uuid }));
    }

    public Task<GeneralResult<DocumentValidatation>> GetDocumentDetail(string uuid)
    {
        Calls.Add(nameof(GetDocumentDetail));
        return Task.FromResult(DocumentDetailHandler(uuid));
    }

    public Task<GeneralResult<NotificationResp>> GetNotification(NotificationInput query)
    {
        Calls.Add(nameof(GetNotification));
        return Task.FromResult(Success(new NotificationResp()));
    }

    public Task<GeneralResult<RecentDocument>> GetRecentDocument(RecentDocumentInput query)
    {
        Calls.Add(nameof(GetRecentDocument));
        return Task.FromResult(SearchHandler(new SearchDocumentInput()));
    }

    public Task<GeneralResult<RecentDocument>> SearchDocument(SearchDocumentInput query)
    {
        Calls.Add(nameof(SearchDocument));
        return Task.FromResult(SearchHandler(query));
    }

    public Task<GeneralResult<bool>> ValidateTin(string tin, IDType idType, string idValue)
    {
        Calls.Add(nameof(ValidateTin));
        LastValidatedTin = tin;
        LastValidatedIdType = idType.ToString();
        return Task.FromResult(ValidateTinHandler(tin, idType, idValue));
    }

    public Task<GeneralResult<List<TINInfo>>> SearchTin(SearchTINInput query)
    {
        Calls.Add(nameof(SearchTin));
        LastSearchTinQuery = query;
        return Task.FromResult(SearchTinHandler(query));
    }

    private static GeneralResult<SuccessSubmit> DefaultSubmit(List<DocumentHeader> infos) =>
        Accepted(infos[0].DocumentNo, "UUID-" + infos[0].DocumentNo);
}
