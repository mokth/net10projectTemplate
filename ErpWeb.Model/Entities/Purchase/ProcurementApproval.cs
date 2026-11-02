namespace ErpWeb.Model.Entities.Purchase;

public static class ProcurementApprovalDocTypes
{
    public const string Pr = "PR";
    public const string Po = "PO";
}

public static class ProcurementApprovalStatuses
{
    public const string Pending = "PENDING";
    public const string Approved = "APPROVED";
    public const string Rejected = "REJECTED";
    public const string Returned = "RETURNED";
    public const string Withdrawn = "WITHDRAWN";
    public const string NotRequired = "NOT_REQUIRED";
    public const string Cancelled = "CANCELLED";
    public const string Superseded = "SUPERSEDED";
}

public static class ProcurementApprovalDecisions
{
    public const string Approve = "APPROVE";
    public const string Reject = "REJECT";
    public const string Return = "RETURN";
}

public static class ProcurementApprovalAssigneeKinds
{
    public const string User = "USER";
    public const string Role = "ROLE";
}

public static class ProcurementApprovalMatchModes
{
    public const string Any = "ANY";
    public const string All = "ALL";
}

public static class ProcurementApprovalLegacyModes
{
    public const string RequireGoingForward = "REQUIRE_GOING_FORWARD";
    public const string LegacyExempt = "LEGACY_EXEMPT";
}

public static class ProcurementApprovalEventTypes
{
    public const string Submitted = "SUBMITTED";
    public const string NotRequired = "NOT_REQUIRED";
    public const string Approved = "APPROVED";
    public const string LevelAdvanced = "LEVEL_ADVANCED";
    public const string Rejected = "REJECTED";
    public const string Returned = "RETURNED";
    public const string Withdrawn = "WITHDRAWN";
    public const string Cancelled = "CANCELLED";
    public const string Reassigned = "REASSIGNED";
    public const string Superseded = "SUPERSEDED";
    public const string CutoverExempted = "CUTOVER_EXEMPTED";
    public const string PolicyEnabled = "POLICY_ENABLED";
    public const string PolicyDisabled = "POLICY_DISABLED";
    public const string Recovered = "RECOVERED";
}

public class ProcurementApprovalPolicy
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool Enabled { get; set; }
    public bool IsPublished { get; set; }
    public string LegacyDraftMode { get; set; } = ProcurementApprovalLegacyModes.RequireGoingForward;
    public DateTime? CutoverAtUtc { get; set; }
    public DateTime? EffectiveAtUtc { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProcurementApprovalBand> Bands { get; set; } = new List<ProcurementApprovalBand>();
}

public class ProcurementApprovalBand
{
    public long Uid { get; set; }
    public long PolicyId { get; set; }
    public ProcurementApprovalPolicy? Policy { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal MinInclusive { get; set; }
    public decimal? MaxExclusive { get; set; }
    public bool NotRequired { get; set; }
    public int Sequence { get; set; }

    public ICollection<ProcurementApprovalLevel> Levels { get; set; } = new List<ProcurementApprovalLevel>();
}

public class ProcurementApprovalLevel
{
    public long Uid { get; set; }
    public long BandId { get; set; }
    public ProcurementApprovalBand? Band { get; set; }
    public int Sequence { get; set; }
    public string MatchMode { get; set; } = ProcurementApprovalMatchModes.Any;

    public ICollection<ProcurementApprovalLevelAssignee> Assignees { get; set; } = new List<ProcurementApprovalLevelAssignee>();
}

public class ProcurementApprovalLevelAssignee
{
    public long Uid { get; set; }
    public long LevelId { get; set; }
    public ProcurementApprovalLevel? Level { get; set; }
    public string Kind { get; set; } = ProcurementApprovalAssigneeKinds.User;
    public string RefCode { get; set; } = string.Empty;
}

public class ProcurementApprovalInstance
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short DocRev { get; set; }
    public int Cycle { get; set; }
    public long PolicyId { get; set; }
    public int PolicyVersion { get; set; }
    public long? BandId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int CurrentLevel { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Gross { get; set; }
    public decimal Taxes { get; set; }
    public decimal Total { get; set; }
    public string MaterialFingerprint { get; set; } = string.Empty;
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTime CreatedDate { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<ProcurementApprovalAssignee> Assignees { get; set; } = new List<ProcurementApprovalAssignee>();
    public ICollection<ProcurementApprovalDecision> Decisions { get; set; } = new List<ProcurementApprovalDecision>();
    public ICollection<ProcurementApprovalEvent> Events { get; set; } = new List<ProcurementApprovalEvent>();
}

public class ProcurementApprovalAssignee
{
    public long Uid { get; set; }
    public long InstanceId { get; set; }
    public ProcurementApprovalInstance? Instance { get; set; }
    public int LevelSequence { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string MatchMode { get; set; } = ProcurementApprovalMatchModes.Any;
}

public class ProcurementApprovalDecision
{
    public long Uid { get; set; }
    public long InstanceId { get; set; }
    public ProcurementApprovalInstance? Instance { get; set; }
    public int LevelSequence { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTime DecidedAtUtc { get; set; }
}

public class ProcurementApprovalEvent
{
    public long Uid { get; set; }
    public long? InstanceId { get; set; }
    public ProcurementApprovalInstance? Instance { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short DocRev { get; set; }
    public int Cycle { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? PriorStatus { get; set; }
    public string? NewStatus { get; set; }
    public int PolicyVersion { get; set; }
    public string? Reason { get; set; }
    public string? IdempotencyKey { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public DateTime OccurredAtUtc { get; set; }
}

public class ProcurementApprovalCutoverExemption
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocType { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public short DocRev { get; set; }
    public long PolicyId { get; set; }
    public DateTime CutoverAtUtc { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string ActorUserId { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
}
