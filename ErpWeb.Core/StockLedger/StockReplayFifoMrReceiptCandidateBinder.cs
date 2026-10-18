using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayFifoBoundReceiptCandidate(
    int HistoryId, StockReplayEventKey Key,
    StockReplayPhysicalResult PhysicalProjection,
    StockReplayFifoTailProjection FinancialProjection,
    long RootPostingId, string PreparedPlanHash);

/// <summary>
/// Binds the saved MR original to the provisional receipt key after the
/// physical write and before creating its valuation fact or FIFO layer.
/// </summary>
public static class StockReplayFifoMrReceiptCandidateBinder
{
    public static async Task<StockReplayFifoBoundReceiptCandidate> BindAsync(
        StockPostingContext context, StockReplayFifoInsertionPlan plan,
        StockReplayFifoWritePermit permit, int historyId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(permit);
        context.EnsureUnsealed();
        StockReplayFifoInsertionPlanner.ValidatePreparedPlan(plan);
        var physical = plan.PhysicalPlan;
        var candidate = physical.Candidate;
        if (historyId <= 0 || candidate.SignedDelta <= 0m
            || candidate.EventId <= 0
            || candidate.Key.AttachedSuborder != 0
            || permit.Operation != "INSERT"
            || permit.RootPostingId != context.Posting.Id
            || permit.RootPostingSequence != context.Posting.PostingSequence
            || permit.LedgerEpochId != context.Epoch.Id
            || permit.CompanyCode != context.CompanyCode
            || permit.BranchCode != context.BranchCode
            || permit.ItemCode != plan.ItemCode
            || permit.PlanHash != plan.PlanHash
            || permit.SnapshotHash != plan.Snapshot.SnapshotHash
            || permit.ChangedPileId != physical.BalLocId
            || permit.ChangedPileFinalQuantity
                != physical.Projection.FinalQuantity
            || permit.FinalQuantity != plan.Projection.FinalQuantity
            || permit.FinalValue != plan.Projection.FinalValue
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType
                != IvTrxTypes.MiscellaneousReceipt
            || context.Posting.PostingRole != "PRIMARY"
            || context.Posting.EffectiveAt != candidate.Key.EffectiveAt)
            throw Invalid("FIFO MR candidate binding needs its prepared root permit.");
        var db = context.Db;
        var rootHistories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => h.CompanyCode == context.CompanyCode
                && h.BranchCode == context.BranchCode
                && h.StockPostingId == context.Posting.Id)
            .ToArrayAsync(cancellationToken);
        if (rootHistories.Length != 1 || rootHistories[0].Id != historyId)
            throw Invalid("FIFO MR binding requires one saved root history.");
        var history = rootHistories[0];
        var evidence = InventoryCostEvidenceTypes.Normalize(
            history.CostEvidenceType);
        var expectedPriceEvidence = evidence switch
        {
            InventoryCostEvidenceTypes.ManualApproved =>
                "EXPLICIT_COMPANY_BASE_PRICE",
            InventoryCostEvidenceTypes.ZeroCostApproved =>
                InventoryCostEvidenceTypes.ZeroCostApproved,
            InventoryCostEvidenceTypes.OpeningApproved =>
                InventoryCostEvidenceTypes.OpeningApproved,
            _ => null
        };
        if (history.LedgerVersion != 2
            || history.LedgerEpochId != context.Epoch.Id
            || history.DocumentRevision != context.Posting.DocumentRevision
            || history.PostingLineNo != 1
            || history.EntryRole != "ORIGINAL"
            || history.ReversesHistoryId is not null
            || history.BatchStatus != IvBatchStatuses.Posted
            || history.TrxType != IvTrxTypes.MiscellaneousReceipt
            || history.ICode != plan.ItemCode
            || history.TrxDtTime != candidate.Key.EffectiveAt
            || history.ToBalLocId != physical.BalLocId
            || history.ToStdQty != candidate.SignedDelta
            || history.ToStdUom != plan.ReceiptBaseUom
            || history.FromBalLocId is not null
            || history.FrStdQty is not null
            || history.ValuationStatus is not null
            || history.UnitPrice is not decimal unit
            || StockLedgerPrecision.Money(unit * candidate.SignedDelta)
                != plan.ReceiptValue
            || history.BaseUnitPrices is not null
            || history.Cost is not null || history.CostPrice is not null)
            throw Invalid("The saved MR history differs from its approved receipt.");
        if (expectedPriceEvidence is null
            || history.PriceEvidence != expectedPriceEvidence
            || string.IsNullOrWhiteSpace(history.CostApprovedBy)
            || history.CostApprovedAtUtc is null
            || string.IsNullOrWhiteSpace(history.CostOverrideReason)
            || history.UnitPrice is not decimal approvedUnit
            || approvedUnit < 0m
            || evidence == InventoryCostEvidenceTypes.ManualApproved
                && approvedUnit <= 0m
            || evidence == InventoryCostEvidenceTypes.ZeroCostApproved
                && approvedUnit != 0m)
            throw Invalid("The posted FIFO MR lost its approved cost evidence.");
        var batches = await db.IvTrxBatches.AsNoTracking()
            .Where(b => b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode
                && b.BatchNo == history.BatchNo)
            .ToArrayAsync(cancellationToken);
        if (batches.Length != 1
            || batches[0].BatchStatus != IvBatchStatuses.Posted
            || batches[0].TrxType != history.TrxType
            || batches[0].TrxDtTime != history.TrxDtTime
            || context.Posting.SourceDocumentId != batches[0].Id.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.SourceDocumentNo != history.BatchNo.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw Invalid("FIFO MR candidate has no matching posted batch.");
        var details = await db.IvTrxBatchDetails.AsNoTracking()
            .Where(d => d.BatchId == batches[0].Id)
            .ToArrayAsync(cancellationToken);
        db.ChangeTracker.DetectChanges();
        if (details.Length != 1
            || db.ChangeTracker.Entries<IvTrxHistory>().Any(e =>
                e.Entity.Id == historyId
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatch>().Any(e =>
                e.Entity.Id == batches[0].Id
                && e.State != EntityState.Unchanged)
            || db.ChangeTracker.Entries<IvTrxBatchDetail>().Any(e =>
                e.Entity.BatchId == batches[0].Id
                && e.State != EntityState.Unchanged)
            || details[0].TrxLineNo != history.TrxLineNo
            || details[0].DocumentRevision != context.Posting.DocumentRevision
            || details[0].ICode != plan.ItemCode
            || details[0].ToBalLocId != physical.BalLocId
            || details[0].ToStdQty != candidate.SignedDelta
            || details[0].ToStdUom != plan.ReceiptBaseUom
            || details[0].UnitPrice != history.UnitPrice
            || details[0].CostEvidenceType != history.CostEvidenceType
            || details[0].PriceEvidence != history.PriceEvidence
            || details[0].CostApprovedBy != history.CostApprovedBy
            || details[0].CostApprovedAtUtc != history.CostApprovedAtUtc
            || details[0].CostOverrideReason != history.CostOverrideReason
            || string.IsNullOrWhiteSpace(details[0].Reason)
            || details[0].Cost is not null
            || details[0].CostPrice is not null
            || details[0].BaseUnitPrices is not null)
            throw Invalid("The saved MR history differs from its staged approval.");
        var pileQuantity = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.Id == physical.BalLocId
                && b.CompanyCode == context.CompanyCode
                && b.BranchCode == context.BranchCode)
            .Select(b => (decimal?)b.StdQty)
            .SingleOrDefaultAsync(cancellationToken);
        if (pileQuantity != physical.Projection.FinalQuantity)
            throw Invalid("FIFO MR pile does not hold its projected final quantity.");

        var key = candidate.Key with { LineIdentity = $"H{historyId:D10}" };
        var boundEvent = candidate with { EventId = historyId, Key = key };
        StockReplayPhysicalResult boundPhysical;
        StockReplayFifoTailProjection boundFinancial;
        try
        {
            boundPhysical = StockReplayPhysicalSimulator.Simulate(
                physical.CurrentQuantity, boundEvent, physical.LaterEvents);
            boundFinancial = StockReplayFifoTailProjector.InsertReceipt(
                plan.Snapshot, key, candidate.SignedDelta,
                plan.ReceiptValue!.Value, plan.ReceiptBaseUom!);
        }
        catch (Exception ex) when (ex is ArgumentException or StockLedgerException)
        {
            throw Invalid("The saved FIFO MR changes the prepared event order.");
        }
        if (boundPhysical.HistoricalQuantityBeforeCandidate
                != physical.Projection.HistoricalQuantityBeforeCandidate
            || boundPhysical.Candidate with { EventId = candidate.EventId }
                != physical.Projection.Candidate
            || !boundPhysical.LaterSteps.SequenceEqual(physical.Projection.LaterSteps)
            || boundPhysical.FinalQuantity != physical.Projection.FinalQuantity
            || boundFinancial.FinalQuantity != plan.Projection.FinalQuantity
            || boundFinancial.FinalValue != plan.Projection.FinalValue
            || boundFinancial.Issues.Count != plan.Projection.Issues.Count
            || !boundFinancial.Issues.Zip(plan.Projection.Issues).All(pair =>
                pair.First.Key == pair.Second.Key
                && pair.First.Quantity == pair.Second.Quantity
                && pair.First.Value == pair.Second.Value
                && pair.First.Allocations.SequenceEqual(pair.Second.Allocations))
            || boundFinancial.FinalLayers.Count
                != plan.Projection.FinalLayers.Count
            || !boundFinancial.FinalLayers.Zip(plan.Projection.FinalLayers)
                .All(pair => pair.First.LayerId == pair.Second.LayerId
                    && pair.First.RemainingQuantity
                        == pair.Second.RemainingQuantity
                    && pair.First.RemainingValue == pair.Second.RemainingValue))
            throw Invalid("The saved FIFO MR changes its prepared replay projection.");
        return new(historyId, key, boundPhysical, boundFinancial,
            context.Posting.Id, plan.PlanHash);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
