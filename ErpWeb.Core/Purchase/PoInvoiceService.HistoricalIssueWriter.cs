using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed partial class PoInvoiceService
{
    // The invoice root writes its own value fact and the issue's exact
    // reversal/replacement before the projected WAC state is installed.
    private static async Task<IReadOnlyList<int>> WriteInvoiceIssueReplayAsync(
        StockPostingContext context, string planHash, long watermark, InvoiceIssueReplay replay,
        PurchaseCostAdjustmentRequest request, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var value = request.ValueAdjustment;
        if (context.Db.Database.CurrentTransaction is null || context.CostMethod != StockCostMethods.MovingAverage
            || context.Posting.SourceModule != "PROCUREMENT" || context.Posting.SourceDocumentType != "PO_INVOICE"
            || context.Posting.PostingRole is not ("PRIMARY" or "REVERSAL")
            || (context.Posting.PostingRole == "PRIMARY") != (context.Posting.ReversesPostingId is null)
            || context.Posting.PostingSequence != checked(watermark + 1)
            || context.Posting.EffectiveAt > replay.Issue.Key.EffectiveAt
            || context.Posting.EffectiveAt.Year != replay.Issue.Key.EffectiveAt.Year
            || context.Posting.EffectiveAt.Month != replay.Issue.Key.EffectiveAt.Month
            || planHash.Length != 64 || value is null || value.PostingLineNo != request.SourceDocumentLine
            || value.SplitOrdinal != 0 || value.ItemCode != request.ItemCode
            || value.Amount != Math.Abs(request.InventoryAdjustmentAmount)
            || value.Direction is not (-1 or 1)
            || value.Direction * value.Amount != request.InventoryAdjustmentAmount
            || value.OriginalValuationFactId is not > 0
            || (context.Posting.PostingRole == "PRIMARY") != (value.ReversesValuationFactId is null)
            || (context.Posting.PostingRole == "PRIMARY") != (request.ReversesAdjustmentId is null)
            || context.Posting.PostingRole == "REVERSAL"
                && value.OriginalValuationFactId != value.ReversesValuationFactId
            || request.CostMethod != StockCostMethods.MovingAverage
            || request.SourceDocumentType != "PO_INVOICE" || request.SourceDocumentNo != context.Posting.SourceDocumentNo
            || request.SourceCostingRevision != context.Posting.DocumentRevision
            || request.InventoryAdjustmentAmount != replay.InventoryVariance
            || request.ConsumedVarianceAmount != replay.ConsumedVariance
            || StockLedgerPrecision.Money(request.InventoryAdjustmentAmount + request.ConsumedVarianceAmount)
                != request.TotalAdjustmentAmount
            || replay.Issue.EventId is not (> 0 and <= int.MaxValue)
            || replay.IssueStep.EventId != replay.Issue.EventId || replay.Issue.OldSignedQuantity >= 0m
            || replay.IssueStep.SignedQuantity != replay.Issue.OldSignedQuantity
            || replay.IssueStep.SignedValue == replay.Issue.OldSignedValue)
            throw InvoiceReplayInvalid("The purchase invoice has no matching locked issue replay authority.");

        var db = context.Db;
        var issue = await db.IvTrxHistories.AsNoTracking().SingleOrDefaultAsync(h =>
            h.Id == (int)replay.Issue.EventId && h.CompanyCode == context.CompanyCode
            && h.BranchCode == context.BranchCode, ct);
        if (issue is null || issue.StockPostingId != replay.IssueOwnerId || issue.FromBalLocId != replay.BalanceId)
            throw InvoiceReplayInvalid("The later issue changed after invoice replay planning.");
        var active = await StockReplayValuationGenerationResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode, [issue], ct);
        if (active[issue.Id].Count != 1 || active[issue.Id][0].Id != replay.PriorFactId)
            throw InvoiceReplayInvalid("The later issue cost changed after invoice replay planning.");

        var fact = new StockValuationFact
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, LedgerEpochId = context.Epoch.Id,
            StockPostingId = context.Posting.Id, PostingLineNo = value.PostingLineNo, SplitOrdinal = value.SplitOrdinal,
            SourceLineId = $"{context.Posting.SourceDocumentType}:{context.Posting.SourceDocumentId}:{value.PostingLineNo}:{value.SplitOrdinal}",
            SourceDocumentType = context.Posting.SourceDocumentType, SourceDocumentId = context.Posting.SourceDocumentId,
            SourceDocumentNo = context.Posting.SourceDocumentNo, SourceDocumentLine = value.SourceDocumentLine,
            EffectiveAt = context.Posting.EffectiveAt, BusinessDate = context.Posting.BusinessDate,
            PeriodKey = context.Posting.PeriodKey, ItemCode = value.ItemCode, BaseUom = value.BaseUom,
            MovementCode = value.MovementCode, Direction = value.Direction, BaseQty = 0m,
            CostMethod = StockCostMethods.MovingAverage, UnitCost = 0m, CostAmount = value.Amount,
            BaseCostAmount = value.Amount, ValuationSource = value.ValuationSource,
            ValuationStatus = value.ReversesValuationFactId is null ? StockValuationStatuses.Valued : StockValuationStatuses.Reversed,
            ValuationVersion = 1, OriginalValuationFactId = value.OriginalValuationFactId,
            ReversesValuationFactId = value.ReversesValuationFactId, CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        };
        db.StockValuationFacts.Add(fact);
        db.PurchaseCostAdjustments.Add(new PurchaseCostAdjustment
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, AdjustmentType = request.AdjustmentType,
            SourceDocumentType = request.SourceDocumentType, SourceDocumentNo = request.SourceDocumentNo,
            SourceDocumentLine = request.SourceDocumentLine, SourceCostingRevision = request.SourceCostingRevision,
            PoNo = request.PoNo, PoRelNo = request.PoRelNo, PoLineNo = request.PoLineNo, ItemCode = request.ItemCode,
            CostMethod = request.CostMethod, BaseQty = request.BaseQty, ActualBaseAmount = request.ActualBaseAmount,
            ReferenceBaseAmount = request.ReferenceBaseAmount, CommercialReferenceAmount = request.CommercialReferenceAmount,
            TotalAdjustmentAmount = request.TotalAdjustmentAmount, InventoryAdjustmentAmount = request.InventoryAdjustmentAmount,
            ConsumedVarianceAmount = request.ConsumedVarianceAmount, StockPostingId = context.Posting.Id,
            InventoryAdjustmentFact = fact, ReversesAdjustmentId = request.ReversesAdjustmentId,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        });
        var replacements = await StockReplayWacIssueRepricingWriter.AppendAsync(context, issue.Id,
            replay.Issue, replay.IssueStep, 2, 3, ct);
        if (replacements.Count != 1)
            throw InvoiceReplayInvalid("The later issue has no unique replacement valuation.");
        await db.SaveChangesAsync(ct);
        var reversal = await db.StockValuationFacts.AsNoTracking().SingleAsync(f =>
            f.StockPostingId == context.Posting.Id && f.InventoryHistoryId == issue.Id
            && f.ReversesValuationFactId == replay.PriorFactId, ct);
        var run = new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType, RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt, LatestEffectiveAt = replay.Issue.Key.EffectiveAt,
            PlanWatermark = watermark, PlanHash = planHash, AffectedHistoryCount = 1,
            AffectedItemCount = 1, AffectedProductionMovementCount = 0,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        };
        db.StockReplayRuns.Add(run);
        await db.SaveChangesAsync(ct);
        foreach (var changedFact in new[] { reversal.Id, replacements[0].Id })
            db.StockReplayEffects.Add(new StockReplayEffect
            {
                CompanyCode = context.CompanyCode, BranchCode = context.BranchCode, ReplayRunId = run.Id,
                RootStockPostingId = context.Posting.Id, BusinessOwnerPostingId = replay.IssueOwnerId,
                EffectKind = StockReplayInvoiceIssueAudit.EffectKind, AffectedEntityType = StockReplayEffectTypes.StockValuationFact,
                AffectedEntityId = changedFact, OriginalEntityId = replay.PriorFactId, CreatedAtUtc = DateTime.UtcNow
            });
        var permit = new StockReplayWacWritePermit(context, request.ItemCode, planHash,
            replay.CurrentQuantity, replay.CurrentValue, replay.FinalQuantity, replay.FinalValue,
            replay.BalanceId, replay.BalanceQty);
        await StockReplayWacCostStateWriter.ApplyAsync(context, permit, ct);
        return [issue.Id];
    }

    private static StockLedgerException InvoiceReplayInvalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
