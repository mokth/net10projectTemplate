using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{

    internal sealed record WacReversedOutputProof(
        long ConsumeMovementId, long ConsumeReversalMovementId,
        long ProduceMovementId, long ProduceReversalMovementId, ProductionEffectiveCost ConsumeCost,
        ProductionEffectiveCost ProduceCost, decimal ConsumedQuantity,
        decimal UnchangedInputAndConversionValue, long? ActiveCorrectionRootId,
        DateTime LatestEffectiveAt, string EvidenceHash);

    // This proof admits one issue, one reversed output and no
    // downstream movements. The standard output planner verifies the original
    // production facts; here we additionally prove the reversed issue and the
    // entire correction history belong to this receipt.
    private static async Task<WacReversedOutputProof?> PlanIsolatedReversedOutputAsync(
        AppDbContext db, string company, string branch, ProductionBalLotMovement issue,
        ProductionBalLotMovement issueReverse, ProductionMaterialMovement issuedMaterial,
        ProductionMaterialMovement consumedMaterial, StockPosting issueOwner, StockPosting issueReverseOwner,
        long[] receiptOwnerIds, DateTime receiptDate, CancellationToken ct)
    {
        try
        {
            if (issue.InventoryHistoryId is not int historyId
                || consumedMaterial.ProductionBalLotMovementId is not long consumeId)
                return null;
            var issueCosts = await ProductionReplayEffectiveCostReader.ReadAsync(
                db, company, branch, [issue.Uid, issueReverse.Uid], ct);
            if (issueCosts[issue.Uid].TotalCost != issueCosts[issueReverse.Uid].TotalCost
                || issueCosts[issue.Uid].UnitCost != issueCosts[issueReverse.Uid].UnitCost)
                return null;
            var authority = new ProductionIssueCostAuthority(issue.Uid, issuedMaterial.Uid,
                historyId, issue.BaseQty, issueCosts[issue.Uid].TotalCost,
                issueCosts[issue.Uid].UnitCost, []);
            var plan = await PlanStandardProductionAsync(db, company, branch, [historyId],
                receiptDate, ct, allowRestatements: true, reversedIssueAuthority: authority);
            if (plan is null || !plan.OutputReversed || plan.Leaf is not null
                || plan.IssueMovementId != issue.Uid || plan.ConsumeMovementId != consumeId
                || plan.ProduceMovementId is not long produceId
                || plan.ActiveFinishedGood is not null || plan.CorrectedReversedFinishedGood is not null)
                return null;
            var originals = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(m => m.Uid == consumeId || m.Uid == produceId).ToArrayAsync(ct);
            if (originals.Length != 2) return null;
            var consume = originals.Single(m => m.Uid == consumeId);
            var produce = originals.Single(m => m.Uid == produceId);
            if (consume.ProductionOutputId is not long outputId || consume.StockPostingId is not long outputOwnerId)
                return null;
            var output = await db.ProductionOutputs.AsNoTracking().SingleOrDefaultAsync(o => o.Uid == outputId, ct);
            var outputOwner = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == outputOwnerId, ct);
            var outputReverseOwner = output?.ReversalPostingId is long id
                ? await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct) : null;
            if (outputOwner is null || outputReverseOwner is null
                || outputOwner.PostingSequence <= issueOwner.PostingSequence
                || outputReverseOwner.PostingSequence <= outputOwner.PostingSequence
                || issueReverseOwner.PostingSequence <= outputReverseOwner.PostingSequence
                || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, outputOwner.EffectiveAt, ct) is not null
                || await IvPeriodCloseGuard.EnsureOpenAsync(db, company, branch, outputReverseOwner.EffectiveAt, ct) is not null)
                return null;
            var reverseLink = await db.ProductionPostingLinks.AsNoTracking().SingleOrDefaultAsync(l =>
                l.Uid == outputReverseOwner.ProductionPostingLinkId, ct);
            if (reverseLink is null || reverseLink.CompanyCode != company || reverseLink.BranchCode != branch
                || reverseLink.CommandType != ProductionPostingCommandTypes.OutputRollback
                || reverseLink.Status != ProductionPostingLinkStatuses.Succeeded
                || reverseLink.OriginalPostingLinkId != outputOwner.ProductionPostingLinkId
                || reverseLink.ProductionDocumentLineId != outputId
                || reverseLink.WorkOrderId != issue.WorkOrderId
                || reverseLink.DocumentRevision != outputOwner.DocumentRevision)
                return null;
            var outputMovements = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(m => m.ProductionOutputId == outputId).ToArrayAsync(ct);
            var inputs = outputMovements.Where(m => m.OriginalMovementId is null
                && m.MovementType == ProductionBalLotMovementTypes.Consume).ToArray();
            if (inputs.Length is < 1 or > 2 || outputMovements.Length != 2 * (inputs.Length + 1)
                || outputMovements.Count(m => m.Uid == consumeId) != 1
                || outputMovements.Count(m => m.Uid == produceId) != 1)
                return null;
            var consumeReversals = outputMovements.Where(m => m.OriginalMovementId == consumeId).ToArray();
            var produceReversals = outputMovements.Where(m => m.OriginalMovementId == produceId).ToArray();
            if (consumeReversals.Length != 1 || produceReversals.Length != 1) return null;
            var consumeReverse = consumeReversals[0];
            var produceReverse = produceReversals[0];
            if (await db.ProductionBalLotMovements.AsNoTracking().AnyAsync(m =>
                    m.ProductionBalLotId == produce.ProductionBalLotId
                    && m.Uid != produce.Uid && m.Uid != produceReverse.Uid, ct))
                return null;
            var relevantIds = outputMovements.Select(m => m.Uid).Append(issue.Uid).Append(issueReverse.Uid).ToArray();
            var movementIds = new[] { issue.Uid, issueReverse.Uid, consumeId, consumeReverse.Uid, produceId, produceReverse.Uid };
            var edges = await db.ProductionPoolDependencyRows.AsNoTracking()
                .Where(e => relevantIds.Contains(e.ContributorMovementId) || relevantIds.Contains(e.ConsumerMovementId))
                .OrderBy(e => e.Id).ToArrayAsync(ct);
            var allocations = await db.ProductionMovementAllocations.AsNoTracking()
                .Where(a => relevantIds.Contains(a.ReceiptMovementId) || relevantIds.Contains(a.OutboundMovementId))
                .OrderBy(a => a.Id).ToArrayAsync(ct);
            var conversions = await db.ProductionConversionCostFacts.AsNoTracking()
                .Where(f => f.ProductionOutputId == outputId).OrderBy(f => f.Id).ToArrayAsync(ct);
            if (edges.Length != 4 * inputs.Length || allocations.Length != 2 * inputs.Length
                || conversions.Count(f => f.ReversesFactId is null) * 2 != conversions.Length
                || conversions.Any(f => f.CompanyCode != company || f.BranchCode != branch))
                return null;
            foreach (var input in inputs)
            {
                var inputReversals = outputMovements.Where(m => m.OriginalMovementId == input.Uid).ToArray();
                var originalAllocations = allocations.Where(a => a.OutboundMovementId == input.Uid
                    && a.ReversesAllocationId is null).ToArray();
                if (inputReversals.Length != 1 || originalAllocations.Length != 1
                    || allocations.Count(a => a.OutboundMovementId == inputReversals[0].Uid
                        && a.ReversesAllocationId == originalAllocations[0].Id
                        && a.ReceiptMovementId == originalAllocations[0].ReceiptMovementId
                        && a.BaseQty == originalAllocations[0].BaseQty
                        && a.StockPostingId == outputReverseOwner.Id
                        && a.CompanyCode == company && a.BranchCode == branch) != 1
                    || originalAllocations[0].CompanyCode != company || originalAllocations[0].BranchCode != branch
                    || originalAllocations[0].StockPostingId != outputOwner.Id
                    || originalAllocations[0].BaseQty != input.BaseQty)
                    return null;
                var originalEdges = edges.Where(e => e.ReversesDependencyId is null
                    && e.ContributorMovementId == originalAllocations[0].ReceiptMovementId
                    && e.ConsumerMovementId == input.Uid && e.StockPostingId == outputOwner.Id).ToArray();
                if (originalEdges.Length != 1 || edges.Count(e => e.ReversesDependencyId == originalEdges[0].Id
                        && e.ContributorMovementId == originalEdges[0].ContributorMovementId
                        && e.ConsumerMovementId == inputReversals[0].Uid
                        && e.StockPostingId == outputReverseOwner.Id) != 1)
                    return null;
            }
            var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, relevantIds, ct);
            if (inputs.Any(input =>
                {
                    var reverse = outputMovements.Single(m => m.OriginalMovementId == input.Uid);
                    return costs[input.Uid].TotalCost != costs[reverse.Uid].TotalCost
                        || costs[input.Uid].UnitCost != costs[reverse.Uid].UnitCost;
                })) return null;
            var conversion = StockLedgerPrecision.Money(conversions.Where(f => f.ReversesFactId is null)
                .Sum(f => f.CostAmount) + inputs.Where(i => i.Uid != consumeId).Sum(i => costs[i.Uid].TotalCost));
            if (costs[consumeId].TotalCost != ReversedOutputConsumeValue(
                    costs[issue.Uid].TotalCost, consume.BaseQty, issue.BaseQty)
                || costs[consumeReverse.Uid].TotalCost != ReversedOutputConsumeValue(
                    costs[issueReverse.Uid].TotalCost, consume.BaseQty, issue.BaseQty)
                || costs[produceId].TotalCost != StockLedgerPrecision.Money(costs[consumeId].TotalCost + conversion)
                || costs[produceReverse.Uid].TotalCost != costs[produceId].TotalCost)
                return null;

            // Each corrected output leg must have exactly the same receipt roots,
            // prior values and reversal lineage as its corresponding issue leg.
            var facts = await db.ProductionCostRestatementFacts.AsNoTracking()
                .Where(f => movementIds.Contains(f.ProductionBalLotMovementId)).ToArrayAsync(ct);
            if (facts.Any(f => !receiptOwnerIds.Contains(f.ReplayStockPostingId))) return null;
            var issueFacts = facts.Where(f => f.ProductionBalLotMovementId == issue.Uid).ToArray();
            var reverseFacts = facts.Where(f => f.ProductionBalLotMovementId == issueReverse.Uid).ToArray();
            if (!SameHistory(issueFacts, consumeId, 0m) || !SameHistory(issueFacts, produceId, conversion)
                || !SameHistory(reverseFacts, consumeReverse.Uid, 0m)
                || !SameHistory(reverseFacts, produceReverse.Uid, conversion))
                return null;
            var activeIds = new[] { costs[consumeId].RestatementFactId, costs[consumeReverse.Uid].RestatementFactId,
                costs[produceId].RestatementFactId, costs[produceReverse.Uid].RestatementFactId };
            var activeRoots = facts.Where(f => activeIds.Contains(f.Id)).Select(f => f.ReplayStockPostingId).Distinct().ToArray();
            if (activeIds.Any(id => id.HasValue) && (activeIds.Any(id => !id.HasValue) || activeRoots.Length != 1))
                return null;
            var latest = new[] { issueReverseOwner.EffectiveAt, outputOwner.EffectiveAt, outputReverseOwner.EffectiveAt }.Max();
            var hash = StockPostingFingerprint.Create(new
            {
                plan.StableHash, OutputReverseOwner = outputReverseOwner.Id, outputReverseOwner.PostingSequence,
                outputReverseOwner.EffectiveAt, ReverseLink = reverseLink.Uid,
                Edges = edges.Select(e => new { e.Id, e.StockPostingId, e.ContributorMovementId, e.ConsumerMovementId, e.ReversesDependencyId }).ToArray(),
                Allocations = allocations.Select(a => new { a.Id, a.StockPostingId, a.ReceiptMovementId, a.OutboundMovementId, a.BaseQty, a.ReversesAllocationId }).ToArray(),
                Facts = facts.OrderBy(f => f.Id).Select(f => new { f.Id, f.ReplayStockPostingId, f.ProductionBalLotMovementId,
                    f.PriorEffectiveTotalCost, f.CorrectedEffectiveTotalCost, f.ReversesRestatementFactId }).ToArray()
            }, new { }).RequestFingerprint;
            return new(consumeId, consumeReverse.Uid, produceId, produceReverse.Uid,
                costs[consumeId], costs[produceId], consume.BaseQty, conversion,
                activeRoots.Length == 0 ? null : activeRoots[0], latest, hash);

            bool SameHistory(ProductionCostRestatementFact[] source, long outputMovementId, decimal extra)
            {
                var target = facts.Where(f => f.ProductionBalLotMovementId == outputMovementId).ToArray();
                if (source.Length != target.Length
                    || source.GroupBy(f => (f.ReplayStockPostingId, f.ReversesRestatementFactId.HasValue)).Any(g => g.Count() != 1)
                    || target.GroupBy(f => (f.ReplayStockPostingId, f.ReversesRestatementFactId.HasValue)).Any(g => g.Count() != 1)) return false;
                foreach (var s in source)
                {
                    var matches = target.Where(t => t.ReplayStockPostingId == s.ReplayStockPostingId
                        && t.ReversesRestatementFactId.HasValue == s.ReversesRestatementFactId.HasValue).ToArray();
                    if (matches.Length != 1) return false;
                    var t = matches[0];
                    if (t.PriorEffectiveTotalCost != StockLedgerPrecision.Money(
                            ReversedOutputConsumeValue(s.PriorEffectiveTotalCost, consume.BaseQty, issue.BaseQty) + extra)
                        || t.CorrectedEffectiveTotalCost != StockLedgerPrecision.Money(
                            ReversedOutputConsumeValue(s.CorrectedEffectiveTotalCost, consume.BaseQty, issue.BaseQty) + extra)
                        || t.ReversesRestatementFactId.HasValue != s.ReversesRestatementFactId.HasValue) return false;
                    if (s.ReversesRestatementFactId is long reversed)
                    {
                        var sourceOriginal = source.SingleOrDefault(f => f.Id == reversed);
                        var targetOriginal = target.SingleOrDefault(f => f.Id == t.ReversesRestatementFactId);
                        if (sourceOriginal is null || targetOriginal is null
                            || sourceOriginal.ReplayStockPostingId != targetOriginal.ReplayStockPostingId) return false;
                    }
                }
                return true;
            }
        }
        catch (StockLedgerException)
        {
            return null;
        }
    }

    private static decimal ReversedOutputConsumeValue(decimal issueValue, decimal consumedQuantity, decimal issueQuantity) =>
        consumedQuantity == issueQuantity ? issueValue
            : StockLedgerPrecision.Money(issueValue * consumedQuantity / issueQuantity);
}
