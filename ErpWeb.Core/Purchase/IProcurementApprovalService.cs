using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;

namespace ErpWeb.Core.Purchase;

public sealed class ProcurementApprovalResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public ProcurementApprovalInstanceDto? Instance { get; init; }
    public ProcurementApprovalPolicyDto? Policy { get; init; }
    public IReadOnlyList<ProcurementApprovalInboxRow>? Inbox { get; init; }
    public IReadOnlyList<ProcurementApprovalEventRow>? History { get; init; }
    public ProcurementApprovalPreview? Preview { get; init; }
    public ProcurementApprovalKpiSummary? Kpi { get; init; }
    public IReadOnlyList<ProcurementApprovalLookupRow>? Users { get; init; }
    public IReadOnlyList<ProcurementApprovalLookupRow>? Roles { get; init; }

    public static ProcurementApprovalResult Ok() => new() { Succeeded = true };
    public static ProcurementApprovalResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class ProcurementApprovalPolicyDto
{
    public long Uid { get; init; }
    public string DocType { get; init; } = string.Empty;
    public int Version { get; init; }
    public bool Enabled { get; set; }
    public string LegacyDraftMode { get; set; } = ProcurementApprovalLegacyModes.RequireGoingForward;
    public DateTime? CutoverAtUtc { get; init; }
    public byte[] RowVersion { get; set; } = [];
    public List<ProcurementApprovalBandDto> Bands { get; set; } = [];
}

public sealed class ProcurementApprovalBandDto
{
    public string Currency { get; set; } = "MYR";
    public decimal MinInclusive { get; set; }
    public decimal? MaxExclusive { get; set; }
    public bool NotRequired { get; set; }
    public List<ProcurementApprovalLevelDto> Levels { get; set; } = [];
}

public sealed class ProcurementApprovalLevelDto
{
    public int Sequence { get; set; }
    public string MatchMode { get; set; } = ProcurementApprovalMatchModes.Any;
    public List<ProcurementApprovalLevelAssigneeDto> Assignees { get; set; } = [];
}

public sealed class ProcurementApprovalLevelAssigneeDto
{
    public string Kind { get; set; } = ProcurementApprovalAssigneeKinds.User;
    public string RefCode { get; set; } = string.Empty;
}

public sealed class ProcurementApprovalInstanceDto
{
    public long Uid { get; init; }
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public short DocRev { get; init; }
    public int Cycle { get; init; }
    public string Status { get; init; } = string.Empty;
    public int CurrentLevel { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal Total { get; init; }
    public string? SubmittedBy { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public IReadOnlyList<string> CurrentAssignees { get; init; } = [];
    public byte[] RowVersion { get; init; } = [];
}

public sealed class ProcurementApprovalInboxRow
{
    public long InstanceId { get; init; }
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public short DocRev { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? SubmittedBy { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
    public int CurrentLevel { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal? Total { get; init; }
    public bool AmountHidden { get; init; }
    public int AgeDays { get; init; }
}

public sealed class ProcurementApprovalEventRow
{
    public DateTime OccurredAtUtc { get; init; }
    public string ActorUserId { get; init; } = string.Empty;
    public string EventType { get; init; } = string.Empty;
    public string? PriorStatus { get; init; }
    public string? NewStatus { get; init; }
    public int Cycle { get; init; }
    public int PolicyVersion { get; init; }
    public string? Reason { get; init; }
}

public sealed class ProcurementApprovalPreview
{
    public string DocType { get; init; } = string.Empty;
    public string DocNo { get; init; } = string.Empty;
    public short DocRev { get; init; }
    public string DocumentStatus { get; init; } = string.Empty;
    public string? VendorOrRequester { get; init; }
    public string Currency { get; init; } = string.Empty;
    public decimal? Total { get; init; }
    public bool AmountHidden { get; init; }
    public ProcurementApprovalInstanceDto? Instance { get; init; }
    public IReadOnlyList<ProcurementApprovalEventRow> History { get; init; } = [];
}

public sealed class ProcurementApprovalKpiSummary
{
    public int PendingPr { get; init; }
    public int PendingPo { get; init; }
    public int Overdue { get; init; }
    public int Approved { get; init; }
    public int Rejected { get; init; }
    public double? AverageHours { get; init; }
}

public sealed class ProcurementApprovalLookupRow
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string DisplayText =>
        string.IsNullOrWhiteSpace(Name) || string.Equals(Name, Code, StringComparison.Ordinal)
            ? Code
            : $"{Code} - {Name}";
}

public sealed class ProcurementApprovalSubmitRequest
{
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short DocRev { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class ProcurementApprovalDecisionRequest
{
    public long InstanceId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public string Decision { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? IdempotencyKey { get; set; }
}

public sealed class ProcurementApprovalReassignRequest
{
    public long InstanceId { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public int LevelSequence { get; set; }
    public string RemoveUserId { get; set; } = string.Empty;
    public string AddUserId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public interface IProcurementApprovalGuard
{
    Task<string?> AssertPrConvertibleAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default);

    Task<string?> AssertPoReceivableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default);

    Task<string?> AssertPoCloseableAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default);

    Task<string?> AssertDocumentMaterialEditAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string newFingerprint, CancellationToken cancellationToken = default);

    Task<string?> AssertAttachmentMutationAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        CancellationToken cancellationToken = default);

    Task<string?> ClosePendingOnDocumentCancelAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string actorUserId, CancellationToken cancellationToken = default);

    Task LockInstancesAsync(
        AppDbContext db, string companyCode, string branchCode, string docType,
        IEnumerable<(string DocNo, short DocRev)> keys, CancellationToken cancellationToken = default);

    Task<bool> IsPrVisibleForPoAsync(
        AppDbContext db, string companyCode, string branchCode, string prNo, CancellationToken cancellationToken = default);

    Task<bool> IsPoVisibleForGrAsync(
        AppDbContext db, string companyCode, string branchCode, string poNo, short poRelNo, CancellationToken cancellationToken = default);

    Task<bool> CanReadDocumentAsync(
        AppDbContext db, string companyCode, string branchCode, string docType, string docNo, short docRev,
        string userId, CancellationToken cancellationToken = default);
}

public interface IProcurementApprovalService : IProcurementApprovalGuard
{
    Task<ProcurementApprovalResult> GetPolicyAsync(string docType, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> SavePolicyAsync(ProcurementApprovalPolicyDto policy, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> SubmitAsync(ProcurementApprovalSubmitRequest request, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> DecideAsync(ProcurementApprovalDecisionRequest request, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> WithdrawAsync(ProcurementApprovalDecisionRequest request, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> ReassignAsync(ProcurementApprovalReassignRequest request, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> GetInboxAsync(
        bool allPending, bool completed, bool submittedByMe = false, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> GetHistoryAsync(string docType, string docNo, short docRev, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> GetPreviewAsync(string docType, string docNo, short docRev, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> GetKpiAsync(int overdueDays, CancellationToken cancellationToken = default);
    Task<ProcurementApprovalResult> GetLookupsAsync(CancellationToken cancellationToken = default);
    Task<ProcurementApprovalInstanceDto?> GetActiveAsync(string docType, string docNo, short docRev, CancellationToken cancellationToken = default);
}
