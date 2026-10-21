namespace ErpWeb.Core.StockLedger;

/// <summary>
/// Evidence granted only after the prepared FIFO plan survives validation
/// inside its unsealed root transaction. It does not by itself persist a
/// correction or authorize bypassing dependent-module reconciliation.
/// </summary>
public sealed class StockReplayFifoWritePermit
{
    internal StockReplayFifoWritePermit(
        StockPostingContext context,
        StockReplayFifoInsertionPlan plan)
    {
        Operation = "INSERT";
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = plan.ItemCode;
        PlanHash = plan.PlanHash;
        SnapshotHash = plan.Snapshot.SnapshotHash;
        CurrentQuantity = plan.Snapshot.CurrentQuantity;
        CurrentValue = plan.Snapshot.CurrentValue;
        FinalQuantity = plan.Projection.FinalQuantity;
        FinalValue = plan.Projection.FinalValue;
        ChangedPileId = plan.PhysicalPlan.BalLocId;
        ChangedPileFinalQuantity = plan.PhysicalPlan.Projection.FinalQuantity;
    }

    internal StockReplayFifoWritePermit(
        StockPostingContext context,
        StockReplayFifoRemovalPlan plan)
    {
        Operation = "REMOVE";
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = plan.ItemCode;
        PlanHash = plan.PlanHash;
        SnapshotHash = plan.Snapshot.SnapshotHash;
        CurrentQuantity = plan.Snapshot.CurrentQuantity;
        CurrentValue = plan.Snapshot.CurrentValue;
        FinalQuantity = plan.Projection.FinalQuantity;
        FinalValue = plan.Projection.FinalValue;
        ChangedPileId = plan.PhysicalPlan.BalLocId;
        ChangedPileFinalQuantity = plan.PhysicalPlan.Projection.FinalQuantity;
        RemovedHistoryId = checked((int)plan.PhysicalPlan.RemovedActiveEvent.EventId);
    }

    internal StockReplayFifoWritePermit(
        StockPostingContext context,
        StockReplayFifoCountSheetInsertionPlan plan)
    {
        Operation = "COUNT";
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = plan.ItemCode;
        PlanHash = plan.PlanHash;
        SnapshotHash = plan.Snapshot.SnapshotHash;
        CurrentQuantity = plan.Snapshot.CurrentQuantity;
        CurrentValue = plan.Snapshot.CurrentValue;
        FinalQuantity = plan.Projection.FinalQuantity;
        FinalValue = plan.Projection.FinalValue;
        ChangedPileId = 0;
        ChangedPileFinalQuantity = 0m;
    }

    internal StockReplayFifoWritePermit(
        StockPostingContext context,
        StockReplayFifoCountSheetRemovalPlan plan,
        StockReplayFifoCountSheetRemovalItem item)
    {
        Operation = "COUNT_REMOVE";
        CompanyCode = context.CompanyCode;
        BranchCode = context.BranchCode;
        LedgerEpochId = context.Epoch.Id;
        RootPostingId = context.Posting.Id;
        RootPostingSequence = context.Posting.PostingSequence;
        ItemCode = item.ItemCode;
        PlanHash = plan.PlanHash;
        SnapshotHash = item.Snapshot.SnapshotHash;
        CurrentQuantity = item.Snapshot.CurrentQuantity;
        CurrentValue = item.Snapshot.CurrentValue;
        FinalQuantity = item.Projection!.FinalQuantity;
        FinalValue = item.Projection.FinalValue;
        ChangedPileId = 0;
        ChangedPileFinalQuantity = 0m;
    }

    public string Operation { get; }
    public string CompanyCode { get; }
    public string BranchCode { get; }
    public long LedgerEpochId { get; }
    public long RootPostingId { get; }
    public long RootPostingSequence { get; }
    public string ItemCode { get; }
    public string PlanHash { get; }
    public string SnapshotHash { get; }
    public decimal CurrentQuantity { get; }
    public decimal CurrentValue { get; }
    public decimal FinalQuantity { get; }
    public decimal FinalValue { get; }
    public int ChangedPileId { get; }
    public decimal ChangedPileFinalQuantity { get; }
    public int? RemovedHistoryId { get; }
}
