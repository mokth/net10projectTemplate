namespace ErpWeb.Core.Costing;

public static class CostingRepairOwnerTypes
{
    public const string MiscReceipt = "INV_MISC_RECEIPT";
    public const string CustomerReturn = "INV_CUSTOMER_RETURN";
    public const string MiscIssue = "INV_MISC_ISSUE";
    public const string Scrap = "INV_SCRAP";
    public const string VendorReturn = "INV_VENDOR_RETURN";
    public const string Transfer = "INV_TRANSFER";
    public const string Adjustment = "INV_ADJUSTMENT";
    public const string PurchaseGoodsReceipt = "PO_GOODS_RECEIPT";
    public const string PurchaseCreditNote = "PO_CDN";
    public const string SalesInvoice = "SA_INVOICE";
    public const string SalesDeliveryOrder = "SA_DO";
    public const string SalesCreditNote = "SA_CDN";
    public const string ProductionMaterialIssue = "PR_MATERIAL_ISSUE";
    public const string ProductionOutput = "PR_OUTPUT";
    public const string ProductionFinishedGood = "PR_FINISHED_GOOD";
}

public sealed record CostingRepairOwner(
    string OwnerType,
    string OwnerDocumentNo,
    string PhysicalSourceDocumentType,
    string PhysicalSourceDocumentId,
    string PhysicalSourceDocumentNo,
    long StockPostingId);

public sealed record CostingRepairOwnershipResult(
    bool IsProven,
    string? BlockingReason,
    CostingRepairOwner? Owner);

public sealed record CostingRepairEvidence(
    string CompanyCode,
    string BranchCode,
    long StockPostingId,
    string PhysicalSourceDocumentType,
    string PhysicalSourceDocumentId,
    string PhysicalSourceDocumentNo,
    string? DoNo,
    string? InvNo,
    bool ForceClosed,
    bool SalesCreditNoteProven,
    string? SalesCreditNoteNo,
    bool PurchaseCreditNoteProven,
    string? PurchaseCreditNoteNo,
    bool ProductionLinkProven,
    string? ProductionDocumentType = null,
    string? ProductionDocumentNo = null);

public sealed record CostingRepairNode(
    long StockPostingId,
    string PhysicalSourceDocumentType,
    string PhysicalSourceDocumentId,
    string PhysicalSourceDocumentNo);

public interface ICostingRepairOwnershipResolver
{
    Task<CostingRepairOwnershipResult> ResolveAsync(
        CostingRepairNode node,
        CancellationToken cancellationToken = default);
}

public sealed record CostingRepairCapability(bool CanReverse, string? BlockingReason);

public interface ICostingRepairAdapter
{
    IReadOnlySet<string> OwnerTypes { get; }

    Task<CostingRepairCapability> CanHandleAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CancellationToken cancellationToken);

    Task<CostingRepairStepResult> ReverseAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CostingRepairExecutionContext context,
        CancellationToken cancellationToken);

    Task<CostingRepairStepResult> RepostAsync(
        CostingRepairNode node,
        CostingRepairOwner owner,
        CostingRepairExecutionContext context,
        CancellationToken cancellationToken);
}

public sealed record CostingRepairExecutionContext(
    Guid RepairRequestId,
    string StableStepId,
    string ExpectedLifecycle,
    string Reason);

public sealed record CostingRepairStepResult(
    bool Succeeded,
    bool Stale,
    string? Message,
    long? ResultStockPostingId);

public sealed record CostingRepairPoolImpact(
    string ItemCode,
    string CostMethod,
    string BaseUom,
    decimal BeforeQty,
    decimal AfterQty,
    decimal BeforeValue,
    decimal ExpectedValue,
    decimal DeltaValue);

public sealed record CostingRepairStep(
    string StableStepId,
    string PhysicalSourceDocumentType,
    string PhysicalSourceDocumentId,
    string PhysicalSourceDocumentNo,
    string OwnerType,
    string OwnerDocumentNo,
    string AdapterName,
    string Action);

public sealed record CostingRepairPlan(
    bool CanRepair,
    string Strategy,
    string? BlockingReason,
    IReadOnlyList<CostingRepairStep> Steps,
    IReadOnlyList<string> AffectedItems,
    IReadOnlyList<string> AffectedDocuments,
    DateTime? EarliestEffectiveAt,
    DateTime? LatestEffectiveAt,
    int PostingCount,
    int ValuationFactCount,
    IReadOnlyList<CostingRepairPoolImpact> PoolImpacts,
    string PreviewHash);

public enum CostingRepairTargetKind
{
    Posting,
    CostState,
    DiagnosticOnly
}

public sealed record CostingRepairTarget(
    CostingRepairTargetKind Kind,
    long? StockPostingId,
    string? ItemCode,
    string? CostMethod,
    string? FindingCode);

public interface ICostingRepairPlanner
{
    Task<CostingRepairPlan> PlanAsync(long stockPostingId, CancellationToken cancellationToken = default);

    Task<CostingRepairPlan> PlanAsync(CostingRepairTarget target, CancellationToken cancellationToken = default);
}

public interface ICostingRepairService
{
    Task<CostingRepairExecutionResult> ExecuteReverseAsync(
        long stockPostingId,
        string previewHash,
        string reason,
        CancellationToken cancellationToken = default);

    Task<CostingRepairExecutionResult> ExecuteStateRebuildAsync(
        string itemCode,
        string costMethod,
        string previewHash,
        string reason,
        CancellationToken cancellationToken = default);
}

public sealed class CostingRepairExecutionResult
{
    public bool Succeeded { get; init; }
    public bool Stale { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? Message { get; init; }
    public Guid? RepairRequestId { get; init; }
}
