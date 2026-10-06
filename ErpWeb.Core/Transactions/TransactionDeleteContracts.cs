using ErpWeb.Model.Data;

namespace ErpWeb.Core.Transactions;

public static class TransactionDeleteOwnerTypes
{
    public const string InventoryBatch = "INVENTORY_BATCH";
    public const string SalesInvoice = "SA_INVOICE";
    public const string SalesDeliveryOrder = "SA_DO";
    public const string SalesCdn = "SA_CDN";
    public const string PurchaseInvoice = "PO_INVOICE";
    public const string PurchaseCdn = "PO_CDN";
    public const string ProductionMaterialIssue = "PRODUCTION_MATERIAL_ISSUE";
    public const string ProductionOutput = "PRODUCTION_OUTPUT";
    public const string ProductionFinishedGood = "PRODUCTION_FINISHED_GOOD";
}

public sealed record TransactionDeleteSubject(
    string CompanyCode,
    string BranchCode,
    string OwnerType,
    string OwnerDocumentId,
    string OwnerDocumentNo);

internal sealed record TransactionExecutionSource(
    string SourceModule,
    string SourceDocumentType,
    string SourceDocumentId,
    string SourceDocumentNo,
    int? PhysicalBatchNo = null);

public enum TransactionDeleteMode
{
    HardDeleteDraft,
    ArchiveHistorical,
    Block
}

public sealed record TransactionDeleteDecision(
    TransactionDeleteMode Mode,
    bool HasHistoricalPosting,
    bool HasActivePosting,
    bool HasUnsealedPosting,
    bool HasHistoricalMovement,
    bool HasDownstreamDependency,
    string? BlockingReason);

public interface ITransactionDeletePolicyService
{
    Task<TransactionDeleteDecision> EvaluateAsync(
        AppDbContext db,
        TransactionDeleteSubject subject,
        CancellationToken cancellationToken = default);
}

public static class TransactionDeleteMessages
{
    public const string IncompletePosting =
        "This document has an incomplete stock/costing posting. Use Costing Center reconciliation before deleting it.";

    public const string RollbackFirst =
        "This document is still posted. Roll it back before deleting it.";

    public const string ForceClosed =
        "This document was force-closed and cannot be deleted.";

    public const string Closed =
        "This document is closed and cannot be deleted.";

    public const string NotDeletableStatus =
        "This document cannot be deleted in its current status.";

    public const string NotFound =
        "The document was not found.";

    public const string HistoricalBatch =
        "This stock batch has historical execution evidence. Archive the owning document instead of deleting its posting history.";

    public const string DefaultArchiveReason = "Deleted after rollback";

    public const int MaxReasonLength = 250;
}
