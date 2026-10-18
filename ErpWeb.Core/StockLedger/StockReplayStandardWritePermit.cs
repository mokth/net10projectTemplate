using System.Collections.Immutable;

namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Evidence issued only after a Standard count sheet survives validation under
/// its replay root. Writers use it in the same transaction.
/// </summary>
public sealed class StockReplayStandardWritePermit
{
    internal StockReplayStandardWritePermit(
        StockPostingContext context,
        StockReplayStandardCountSheetInsertionPlan plan)
    {
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = plan.ItemCode;
        PlanHash = plan.PlanHash;
        CurrentQuantity = plan.Tail.CurrentQuantity;
        CurrentValue = plan.Tail.CurrentValue;
        FinalQuantity = plan.Projection.FinalQuantity;
        FinalValue = plan.Projection.FinalValue;
        ChangedPiles = plan.CountedPiles.ToImmutableDictionary(
            p => p.BalLocId, p => p.Projection.FinalQuantity);
    }

    internal StockReplayStandardWritePermit(
        StockPostingContext context, string itemCode, string planHash,
        decimal currentQuantity, decimal currentValue,
        decimal finalQuantity, decimal finalValue,
        ImmutableDictionary<int, decimal> changedPiles)
    {
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = itemCode;
        PlanHash = planHash;
        CurrentQuantity = currentQuantity;
        CurrentValue = currentValue;
        FinalQuantity = finalQuantity;
        FinalValue = finalValue;
        ChangedPiles = changedPiles;
    }

    public string CompanyCode { get; }
    public string BranchCode { get; }
    public long LedgerEpochId { get; }
    public long RootPostingId { get; }
    public long RootPostingSequence { get; }
    public string ItemCode { get; }
    public string PlanHash { get; }
    public decimal CurrentQuantity { get; }
    public decimal CurrentValue { get; }
    public decimal FinalQuantity { get; }
    public decimal FinalValue { get; }
    public ImmutableDictionary<int, decimal> ChangedPiles { get; }
}
