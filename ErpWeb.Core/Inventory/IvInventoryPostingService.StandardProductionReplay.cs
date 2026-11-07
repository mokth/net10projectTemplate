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
    private sealed record StandardProductionReplayPlan(
        int HistoryId, ProductionLeafReplayPlan? Leaf, string StableHash,
        DateTime LatestEffectiveAt, int MovementCount,
        long? IssueMovementId = null, long? ConsumeMovementId = null,
        long? ProduceMovementId = null, bool OutputReversed = false,
        ActiveFinishedGoodReplayPlan? ActiveFinishedGood = null,
        CorrectedReversedFinishedGoodPlan? CorrectedReversedFinishedGood = null);

    private sealed record ActiveFinishedGoodReplayPlan(
        int BatchId, long MovementId, decimal Quantity, decimal BaseQuantity, decimal Value,
        ProductionEffectiveCost EffectiveCost,
        DateTime EffectiveAt, string StableHash);

    // Standard receipts may pass an already consumed issue only when every
    // affected quantity and cost remains unchanged. This bounded case admits
    // one output and its exact reversal, with no later WIP consumption.
    private static async Task<StandardProductionReplayPlan?> PlanStandardProductionAsync(
        AppDbContext db, string company, string branch, IReadOnlyList<int> historyIds,
        DateTime candidateDate, CancellationToken ct, bool allowRestatements = false,
        long? removingReceiptOwnerId = null)
    {
        var ids = await db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id) && h.TrxType == IvTrxTypes.IssueToProduction)
            .Select(h => h.Id).ToArrayAsync(ct);
        if (ids.Length == 0) return null;
        if (ids.Length != 1)
            throw ProductionReplayError("Standard receipt replay needs one Production issue.");

        var issues = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.CompanyCode == company && m.BranchCode == branch
                && m.InventoryHistoryId == ids[0]
                && m.MovementType == ProductionBalLotMovementTypes.Issue
                && m.OriginalMovementId == null).ToArrayAsync(ct);
        if (issues.Length != 1)
            throw ProductionReplayError("Production issue has no unique material-pool movement.");
        var issue = issues[0];
        var poolMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == issue.ProductionBalLotId)
            .OrderBy(m => m.Uid).ToArrayAsync(ct);
        if (poolMovements.Length == 1)
        {
            var leaf = await PlanProductionLeafAsync(db, company, branch, ids, ct);
            return new(ids[0], leaf!, string.Empty, leaf!.EffectiveAt, 1);
        }

        var authority = (await ProductionIssueEffectiveCostReader.ReadAsync(
            db, company, branch, [issue.Uid], ct))[issue.Uid];
        var issuedMaterial = await db.ProductionMaterialMovements.AsNoTracking()
            .SingleAsync(m => m.Uid == authority.MaterialMovementId, ct);
        var sourceLot = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == issue.ProductionBalLotId, ct);
        var sourcePool = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProductionBalLotId == issue.ProductionBalLotId, ct);
        var consumes = poolMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.Consume
            && m.OriginalMovementId is null).ToArray();
        if (consumes.Length != 1 || sourceLot is null || sourcePool is null)
            throw ProductionReplayError("Standard receipt needs one verified Production output consumption.");
        var consume = consumes[0];
        var consumeReversals = poolMovements.Where(m => m.OriginalMovementId == consume.Uid).ToArray();
        if (consumeReversals.Length > 1
            || poolMovements.Length != 2 + consumeReversals.Length
            || consume.ProductionOutputId is not long outputId
            || consume.StockPostingId is not long outputPostingId
            || consume.MovementDate.Year != candidateDate.Year
            || consume.MovementDate.Month != candidateDate.Month)
            throw ProductionReplayError("Production output consumption is outside the bounded open-month replay.");
        var reversed = consumeReversals.Length == 1;
        var output = await db.ProductionOutputs.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Uid == outputId, ct);
        var outputPosting = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == outputPostingId, ct);
        var issuePosting = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == issue.StockPostingId, ct);
        var link = await db.ProductionPostingLinks.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == consume.PostingLinkId, ct);
        if (output is null || outputPosting is null || issuePosting is null || link is null
            || output.CompanyCode != company || output.BranchCode != branch
            || output.PostingId != outputPostingId || output.DeletedAtUtc is not null
            || output.Status != (reversed ? ProductionOutputStatuses.New : ProductionOutputStatuses.Posted)
            || output.ProductionDate != consume.MovementDate
            || outputPosting.CompanyCode != company || outputPosting.BranchCode != branch
            || outputPosting.LedgerEpochId != issue.LedgerEpochId
            || outputPosting.SealedAtUtc is null || outputPosting.PostingRole != "PRIMARY"
            || outputPosting.SourceModule != "PRODUCTION"
            || outputPosting.SourceDocumentType != ProductionDocumentTypes.ProductionOutput
            || outputPosting.CommandType != "PRODUCTION_OUTPUT_POST"
            || outputPosting.SourceDocumentId != outputId.ToString(System.Globalization.CultureInfo.InvariantCulture)
            || outputPosting.SourceDocumentNo != output.DocumentNo
            || outputPosting.EffectiveAt != output.ProductionDate
            || outputPosting.PostingSequence <= issuePosting.PostingSequence
            || output.ProductionDate < issue.MovementDate
            || outputPosting.ProductionPostingLinkId != link.Uid
            || output.DocumentRevision != outputPosting.DocumentRevision + (reversed ? 1 : 0)
            || link.CompanyCode != company || link.BranchCode != branch
            || link.CommandType != ProductionPostingCommandTypes.OutputPost
            || link.Status != ProductionPostingLinkStatuses.Succeeded
            || link.ProductionDocumentLineId != outputId || link.WorkOrderId != output.WorkOrderId
            || link.DocumentRevision != outputPosting.DocumentRevision)
            throw ProductionReplayError("Production output has no exact sealed business owner.");

        var outputMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionOutputId == outputId && m.StockPostingId == outputPostingId
                && m.OriginalMovementId == null).OrderBy(m => m.Uid).ToArrayAsync(ct);
        var inputs = outputMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.Consume).ToArray();
        var produces = outputMovements.Where(m => m.MovementType == ProductionBalLotMovementTypes.Produce).ToArray();
        if (inputs.Length == 0 || produces.Length != 1
            || outputMovements.Length != inputs.Length + 1 || !inputs.Any(m => m.Uid == consume.Uid)
            || outputMovements.Any(m => m.CompanyCode != company || m.BranchCode != branch
                || m.LedgerVersion != 2 || m.LedgerEpochId != issue.LedgerEpochId
                || m.StockPostingId != outputPostingId || m.PostingLinkId != link.Uid
                || m.ProductionOutputId != outputId || m.MovementDate != output.ProductionDate
                || m.WorkOrderId != output.WorkOrderId || m.WorkOrderOperationId != output.WorkOrderOperationId
                || m.RouteStepId != output.RouteStepId || m.BaseQty <= 0m
                || m.TotalCost < 0m || m.UnitCost != StockLedgerPrecision.Money(m.TotalCost / m.BaseQty)
                || m.ValuationStatus != ProductionPoolValuationService.Verified))
            throw ProductionReplayError("Production output movements do not match their business owner.");
        var produce = produces[0];
        var allIds = outputMovements.Select(m => m.Uid).Append(issue.Uid).ToArray();
        var costs = await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, allIds, ct);
        if ((!allowRestatements && outputMovements.Append(issue).Any(m =>
                costs[m.Uid].RestatementFactId is not null
                || costs[m.Uid].TotalCost != m.TotalCost || costs[m.Uid].UnitCost != m.UnitCost))
            || authority.TotalCost != costs[issue.Uid].TotalCost
            || authority.UnitCost != costs[issue.Uid].UnitCost)
            throw ProductionReplayError("Standard receipt needs unchanged Production cost evidence.");
        var material = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotMovementId == consume.Uid
                && m.MovementType == ProductionMaterialMovementTypes.Consume)
            .ToArrayAsync(ct);
        if (material.Length != 1 || material[0].OriginalMovementId != authority.MaterialMovementId
            || material[0].CompanyCode != company || material[0].BranchCode != branch
            || material[0].ItemCode != issue.ItemCode || material[0].BaseUom != consume.BaseUom
            || material[0].ProductionBalLotId != sourceLot.Uid
            || material[0].ProductionOutputId != outputId
            || material[0].StockPostingId != outputPostingId || material[0].MovementDate != consume.MovementDate
            || material[0].PostingLinkId != link.Uid
            || material[0].WorkOrderId != consume.WorkOrderId
            || material[0].WorkOrderMaterialId != consume.WorkOrderMaterialId
            || consume.WorkOrderId != issue.WorkOrderId
            || consume.WorkOrderMaterialId != issue.WorkOrderMaterialId
            || consume.WorkOrderOperationId != issue.WorkOrderOperationId
            || issuedMaterial.WorkOrderMaterialId != issue.WorkOrderMaterialId
            || material[0].WorkOrderOperationId != consume.WorkOrderOperationId
            || material[0].BaseQty != consume.BaseQty || material[0].TotalCost != consume.TotalCost
            || material[0].UnitCost != consume.UnitCost)
            throw ProductionReplayError("Production consumption has inconsistent material ownership.");
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(e => allIds.Contains(e.MovementId)).ToArrayAsync(ct);
        if (evidence.Length != allIds.Length || outputMovements.Append(issue).Any(m =>
                evidence.Count(e => e.MovementId == m.Uid && e.ProductionBalLotId == m.ProductionBalLotId
                    && e.Status == ProductionPoolValuationService.Verified && e.Generation > 0
                    && e.Currency == "COMPANY_BASE" && e.Price == m.UnitCost
                    && e.ConversionFactor > 0m && e.ConversionFactor == m.ConversionFactorToBase) != 1))
            throw ProductionReplayError("Production output has incomplete verified valuation evidence.");
        var conversion = await db.ProductionConversionCostFacts.AsNoTracking()
            .Where(f => f.ProductionOutputId == outputId && f.ReversesFactId == null
                && f.StockPostingId == outputPostingId).OrderBy(f => f.Id).ToArrayAsync(ct);
        if (await db.ProductionConversionCostFacts.AsNoTracking().AnyAsync(f =>
                f.ProductionMovementId == produce.Uid
                && (f.StockPostingId != outputPostingId || f.ReversesFactId != null), ct)
            || conversion.Any(f => f.CompanyCode != company || f.BranchCode != branch
                || f.ProductionMovementId != produce.Uid || f.WorkOrderId != output.WorkOrderId
                || f.WorkOrderOperationId != output.WorkOrderOperationId
                || f.RouteStepId != output.RouteStepId || f.CostAmount < 0m)
            || costs[produce.Uid].TotalCost != StockLedgerPrecision.Money(
                inputs.Sum(m => costs[m.Uid].TotalCost) + conversion.Sum(f => f.CostAmount)))
            throw ProductionReplayError("Production output value does not reconcile to its frozen inputs and conversion cost.");

        var outputLot = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(l => l.Uid == produce.ProductionBalLotId, ct);
        var outputPool = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(p => p.ProductionBalLotId == produce.ProductionBalLotId, ct);
        var outputPoolMovements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotId == produce.ProductionBalLotId)
            .OrderBy(m => m.Uid).ToArrayAsync(ct);
        var downstreamMovements = outputPoolMovements.Where(m => m.Uid != produce.Uid
            && !(reversed && m.OriginalMovementId == produce.Uid
                && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal)).ToArray();
        string? reversedFinishedGoodHash = null;
        ActiveFinishedGoodReplayPlan? activeFinishedGood = null;
        CorrectedReversedFinishedGoodPlan? correctedReversedFinishedGood = null;
        if (downstreamMovements.Length != 0)
        {
            if (!allowRestatements)
                throw ProductionReplayError("Production output has unsupported downstream history.");
            if (!reversed && downstreamMovements.Length == 1
                && downstreamMovements[0].MovementType == ProductionBalLotMovementTypes.FgReceiptOut)
            {
                var issueMethods = await db.StockValuationFacts.AsNoTracking()
                    .Where(f => authority.ActiveValuationFactIds.Contains(f.Id))
                    .Select(f => f.CostMethod).Distinct().ToArrayAsync(ct);
                if (issueMethods.Length != 1)
                    throw ProductionReplayError("Production issue has inconsistent cost methods.");
                activeFinishedGood = await VerifyActiveFinishedGoodAsync(db, company, branch,
                    candidateDate, produce, costs[produce.Uid], downstreamMovements[0], issueMethods[0], ct);
            }
            else
            {
                var verified = await VerifyReversedFinishedGoodAsync(db, company, branch,
                    candidateDate, produce, downstreamMovements, removingReceiptOwnerId, ct);
                reversedFinishedGoodHash = verified.Hash;
                correctedReversedFinishedGood = verified.Corrected;
            }
        }
        if (reversed && correctedReversedFinishedGood is not null)
            throw ProductionReplayError("Corrected finished goods and their reversed Production output require a wider replay.");
        if (outputLot is null || outputPool is null || outputLot.Uid == sourceLot.Uid
            || outputPoolMovements.Length != (reversed ? 2 : 1) + downstreamMovements.Length
            || outputPoolMovements.Count(m => m.Uid == produce.Uid) != 1
            || sourceLot.CompanyCode != company || sourceLot.BranchCode != branch
            || outputLot.CompanyCode != company || outputLot.BranchCode != branch
            || sourceLot.Kind != ProductionBalLotKinds.MaterialIn
            || sourceLot.OriginalIssueMovementId != authority.MaterialMovementId
            || sourceLot.WorkOrderId != issue.WorkOrderId
            || sourceLot.WorkOrderMaterialId != issue.WorkOrderMaterialId
            || sourceLot.ItemCode != issue.ItemCode || sourcePool.Generation <= 0
            || outputPool.Generation <= 0
            || ProductionCostReadiness.PoolValuationError(sourceLot, sourcePool) is not null
            || ProductionCostReadiness.PoolValuationError(outputLot, outputPool) is not null
            || sourceLot.BaseQty != issue.BaseQty - (reversed ? 0m : consume.BaseQty)
            || sourceLot.Qty != issue.Qty - (reversed ? 0m : consume.Qty)
            || sourceLot.TotalCost != costs[issue.Uid].TotalCost - (reversed ? 0m : costs[consume.Uid].TotalCost)
            || consume.BaseQty > issue.BaseQty || consume.Qty > issue.Qty
            || !reversed && costs[consume.Uid].TotalCost != (consume.BaseQty == issue.BaseQty ? costs[issue.Uid].TotalCost
                : StockLedgerPrecision.Money(costs[issue.Uid].TotalCost * consume.BaseQty / issue.BaseQty))
            || sourceLot.AverageUnitCost != (sourceLot.BaseQty == 0m ? 0m
                : StockLedgerPrecision.Money(sourceLot.TotalCost / sourceLot.BaseQty))
            || outputLot.BaseQty != (reversed ? 0m : produce.BaseQty - (activeFinishedGood?.BaseQuantity ?? 0m))
            || outputLot.Qty != (reversed ? 0m : produce.Qty - (activeFinishedGood?.Quantity ?? 0m))
            || outputLot.TotalCost != (reversed ? 0m : costs[produce.Uid].TotalCost - (activeFinishedGood?.Value ?? 0m))
            || outputLot.AverageUnitCost != (outputLot.BaseQty == 0m ? 0m
                : StockLedgerPrecision.Money(outputLot.TotalCost / outputLot.BaseQty)))
            throw ProductionReplayError("Production issue or output pool does not reconcile.");

        var graph = await ProductionReplayDependencyPlanner.BuildAsync(db, company, branch, [issue.Uid], ct);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(a => a.ReceiptMovementId == issue.Uid || a.ReceiptMovementId == produce.Uid
                || a.OutboundMovementId == consume.Uid).ToArrayAsync(ct);
        if (!reversed && (graph.OrderedMovementIds.Count != (activeFinishedGood is null ? 3 : 4)
                || graph.Edges.Count != (activeFinishedGood is null ? 2 : 3)
                || !graph.Edges.Any(e => e.ContributorMovementId == issue.Uid && e.ConsumerMovementId == consume.Uid)
                || !graph.Edges.Any(e => e.ContributorMovementId == consume.Uid && e.ConsumerMovementId == produce.Uid)
                || activeFinishedGood is not null && !graph.Edges.Any(e =>
                    e.ContributorMovementId == produce.Uid && e.ConsumerMovementId == activeFinishedGood.MovementId)
                || allocations.Length != (activeFinishedGood is null ? 1 : 2)
                || allocations.Count(a => a.ReceiptMovementId == issue.Uid
                    && a.OutboundMovementId == consume.Uid && a.StockPostingId == outputPostingId
                    && a.BaseQty == consume.BaseQty) != 1
                || activeFinishedGood is not null && allocations.Count(a =>
                    a.ReceiptMovementId == produce.Uid && a.OutboundMovementId == activeFinishedGood.MovementId
                    && a.BaseQty == activeFinishedGood.BaseQuantity) != 1)
            || reversed && (graph.OrderedMovementIds.Count != 1 || graph.Edges.Count != 0
                || allocations.Length != 0))
            throw ProductionReplayError("Production output has an unsupported or inconsistent dependency.");

        var reversalIds = outputMovements.Select(m => m.Uid).ToArray();
        var reversals = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => m.OriginalMovementId.HasValue && reversalIds.Contains(m.OriginalMovementId.Value))
            .OrderBy(m => m.Uid).ToArrayAsync(ct);
        long? reversalPostingId = null;
        if (reversed)
        {
            reversalPostingId = output.ReversalPostingId;
            var reversalPosting = reversalPostingId is long rid
                ? await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(p => p.Id == rid, ct) : null;
            if (reversalPosting is null || reversalPosting.SealedAtUtc is null
                || reversalPosting.CompanyCode != company || reversalPosting.BranchCode != branch
                || reversalPosting.LedgerEpochId != issue.LedgerEpochId
                || reversalPosting.ReversesPostingId != outputPostingId
                || reversalPosting.PostingRole != "REVERSAL"
                || reversalPosting.SourceModule != "PRODUCTION"
                || reversalPosting.SourceDocumentType != ProductionDocumentTypes.ProductionOutput
                || reversalPosting.SourceDocumentId != outputPosting.SourceDocumentId
                || reversalPosting.SourceDocumentNo != outputPosting.SourceDocumentNo
                || reversalPosting.DocumentRevision != outputPosting.DocumentRevision
                || reversalPosting.CommandType != "PRODUCTION_OUTPUT_ROLLBACK"
                || reversals.Length != outputMovements.Length
                || outputMovements.Any(m => reversals.Count(r =>
                    r.OriginalMovementId == m.Uid && r.StockPostingId == reversalPostingId
                    && r.CompanyCode == company && r.BranchCode == branch
                    && r.LedgerVersion == 2 && r.LedgerEpochId == issue.LedgerEpochId
                    && r.ValuationStatus == ProductionPoolValuationService.Verified
                    && r.PostingLinkId == reversalPosting.ProductionPostingLinkId
                    && r.ProductionOutputId == outputId && r.MovementDate == m.MovementDate
                    && r.WorkOrderId == m.WorkOrderId && r.WorkOrderOperationId == m.WorkOrderOperationId
                    && r.WorkOrderMaterialId == m.WorkOrderMaterialId && r.RouteStepId == m.RouteStepId
                    && r.ProductionBalLotId == m.ProductionBalLotId
                    && r.MovementType == (m.MovementType == ProductionBalLotMovementTypes.Consume
                        ? ProductionBalLotMovementTypes.ConsumeReversal : ProductionBalLotMovementTypes.ProduceReversal)
                    && r.Qty == m.Qty && r.BaseQty == m.BaseQty && r.Uom == m.Uom && r.BaseUom == m.BaseUom
                    && r.TotalCost == costs[m.Uid].TotalCost && r.UnitCost == costs[m.Uid].UnitCost) != 1))
                throw ProductionReplayError("Production output reversal is not exact and sealed.");
        }
        else if (output.ReversalPostingId is not null || reversals.Length != 0)
            throw ProductionReplayError("Production output has an unexpected reversal.");

        var originalAllocations = await db.ProductionMovementAllocations.AsNoTracking()
            .Where(a => a.ReceiptMovementId == issue.Uid && a.OutboundMovementId == consume.Uid
                && a.ReversesAllocationId == null).ToArrayAsync(ct);
        var originalEdges = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => (e.ConsumerMovementId == consume.Uid || e.ConsumerMovementId == produce.Uid)
                && e.ReversesDependencyId == null).ToArrayAsync(ct);
        if (originalAllocations.Length != 1
            || originalAllocations[0].CompanyCode != company || originalAllocations[0].BranchCode != branch
            || originalAllocations[0].StockPostingId != outputPostingId
            || originalAllocations[0].BaseQty != consume.BaseQty
            || originalEdges.Length != inputs.Length + 1
            || originalEdges.Count(e => e.ContributorMovementId == issue.Uid
                && e.ConsumerMovementId == consume.Uid && e.StockPostingId == outputPostingId) != 1
            || inputs.Any(input => originalEdges.Count(e => e.ContributorMovementId == input.Uid
                && e.ConsumerMovementId == produce.Uid && e.StockPostingId == outputPostingId) != 1))
            throw ProductionReplayError("Production output has incomplete original allocation or dependency evidence.");
        var allocationId = originalAllocations[0].Id;
        var allocationReversals = await db.ProductionMovementAllocations.AsNoTracking()
            .Where(a => a.ReversesAllocationId == allocationId).ToArrayAsync(ct);
        var edgeIds = originalEdges.Select(e => e.Id).ToArray();
        var edgeReversals = await db.ProductionPoolDependencyRows.AsNoTracking()
            .Where(e => e.ReversesDependencyId.HasValue && edgeIds.Contains(e.ReversesDependencyId.Value))
            .ToArrayAsync(ct);
        var conversionIds = conversion.Select(f => f.Id).ToArray();
        var conversionReversals = await db.ProductionConversionCostFacts.AsNoTracking()
            .Where(f => f.ReversesFactId.HasValue && conversionIds.Contains(f.ReversesFactId.Value))
            .ToArrayAsync(ct);
        if (reversed)
        {
            var consumeReversal = reversals.Single(r => r.OriginalMovementId == consume.Uid);
            var produceReversal = reversals.Single(r => r.OriginalMovementId == produce.Uid);
            var materialReversals = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(m => m.OriginalMovementId == material[0].Uid
                    && m.MovementType == ProductionMaterialMovementTypes.ConsumeReversal)
                .ToArrayAsync(ct);
            if (materialReversals.Length != 1
                || materialReversals[0].ProductionBalLotMovementId != consumeReversal.Uid
                || materialReversals[0].ProductionOutputId != outputId
                || materialReversals[0].CompanyCode != company || materialReversals[0].BranchCode != branch
                || materialReversals[0].ItemCode != material[0].ItemCode
                || materialReversals[0].BaseUom != material[0].BaseUom
                || materialReversals[0].ProductionBalLotId != sourceLot.Uid
                || materialReversals[0].StockPostingId != reversalPostingId
                || materialReversals[0].WorkOrderId != material[0].WorkOrderId
                || materialReversals[0].WorkOrderMaterialId != material[0].WorkOrderMaterialId
                || materialReversals[0].WorkOrderOperationId != material[0].WorkOrderOperationId
                || materialReversals[0].PostingLinkId != consumeReversal.PostingLinkId
                || materialReversals[0].BaseQty != material[0].BaseQty
                || materialReversals[0].TotalCost != costs[consume.Uid].TotalCost
                || materialReversals[0].UnitCost != costs[consume.Uid].UnitCost
                || allocationReversals.Length != 1
                || allocationReversals[0].CompanyCode != company || allocationReversals[0].BranchCode != branch
                || allocationReversals[0].StockPostingId != reversalPostingId
                || allocationReversals[0].ReceiptMovementId != issue.Uid
                || allocationReversals[0].OutboundMovementId != consumeReversal.Uid
                || allocationReversals[0].BaseQty != consume.BaseQty
                || edgeReversals.Length != originalEdges.Length
                || originalEdges.Any(e => edgeReversals.Count(r =>
                    r.ReversesDependencyId == e.Id && r.StockPostingId == reversalPostingId
                    && r.ContributorMovementId == e.ContributorMovementId
                    && r.ConsumerMovementId == (e.ConsumerMovementId == consume.Uid
                        ? consumeReversal.Uid : produceReversal.Uid)) != 1)
                || conversionReversals.Length != conversion.Length
                || conversion.Any(f => conversionReversals.Count(r =>
                    r.ReversesFactId == f.Id && r.StockPostingId == reversalPostingId
                    && r.CompanyCode == company && r.BranchCode == branch
                    && r.WorkOrderId == f.WorkOrderId && r.WorkOrderOperationId == f.WorkOrderOperationId
                    && r.RouteStepId == f.RouteStepId && r.ProductionOutputId == outputId
                    && r.ProductionMovementId == produceReversal.Uid
                    && r.CostAmount == f.CostAmount && r.CostType == f.CostType
                    && r.SourceLineKey == f.SourceLineKey && r.BasisQty == f.BasisQty
                    && r.BasisUom == f.BasisUom && r.RatePerOutputUnit == f.RatePerOutputUnit) != 1))
                throw ProductionReplayError("Production output allocation, dependency or cost reversal is inconsistent.");
        }
        else if (allocationReversals.Length != 0 || edgeReversals.Length != 0 || conversionReversals.Length != 0)
            throw ProductionReplayError("Production output has unexpected reversed evidence.");

        var sourceEvidence = evidence.Single(e => e.MovementId == issue.Uid);
        var consumeEvidence = evidence.Single(e => e.MovementId == consume.Uid);
        var produceEvidence = evidence.Single(e => e.MovementId == produce.Uid);
        if (sourceEvidence.ProductionBalLotId != sourceLot.Uid || sourceEvidence.Generation != sourcePool.Generation
            || consumeEvidence.ProductionBalLotId != sourceLot.Uid || consumeEvidence.Generation != sourcePool.Generation
            || produceEvidence.ProductionBalLotId != outputLot.Uid || produceEvidence.Generation != outputPool.Generation)
            throw ProductionReplayError("Production valuation evidence refers to another pool generation.");
        var hash = StockPostingFingerprint.Create(new
        {
            authority.InventoryHistoryId, authority.MaterialMovementId, issue.Uid, issue.TotalCost, issue.UnitCost,
            EffectiveCosts = costs.Values.OrderBy(c => c.MovementId).ToArray(),
            IssueOwner = new { issue.WorkOrderId, issue.WorkOrderMaterialId, issue.WorkOrderOperationId,
                issue.PostingLinkId, issue.StockPostingId, issue.LedgerEpochId, issue.ItemCode },
            IssuedMaterial = new { issuedMaterial.Uid, issuedMaterial.WorkOrderId,
                issuedMaterial.WorkOrderMaterialId, issuedMaterial.WorkOrderOperationId,
                issuedMaterial.PostingLinkId, issuedMaterial.ItemCode },
            ConsumedMaterial = new { material[0].Uid, material[0].OriginalMovementId,
                material[0].WorkOrderId, material[0].WorkOrderMaterialId, material[0].WorkOrderOperationId,
                material[0].PostingLinkId, material[0].StockPostingId, material[0].ItemCode, material[0].BaseUom },
            OutputId = outputId, OutputPostingId = outputPostingId, output.DocumentRevision, output.Status,
            ReversedFinishedGood = reversedFinishedGoodHash,
            ActiveFinishedGood = activeFinishedGood,
            output.ReversalPostingId, OutputRowVersion = Convert.ToHexString(output.RowVersion ?? []),
            Movements = outputMovements.Select(m => new { m.Uid, m.ProductionBalLotId, m.BaseQty, m.Qty,
                m.TotalCost, m.UnitCost, m.PostingLinkId, m.StockPostingId, m.LedgerEpochId,
                m.WorkOrderId, m.WorkOrderMaterialId, m.WorkOrderOperationId, m.RouteStepId, m.ItemCode }).ToArray(),
            Reversals = reversals.Select(m => new { m.Uid, m.OriginalMovementId, m.StockPostingId,
                m.ProductionBalLotId, m.BaseQty, m.Qty, m.TotalCost, m.UnitCost }).ToArray(),
            Conversion = conversion.Select(f => new { f.Id, f.CostAmount, f.CostType, f.SourceLineKey,
                f.WorkOrderId, f.WorkOrderOperationId, f.RouteStepId, f.WorkOrderLabourId, f.WorkOrderMachineId,
                f.BasisQty, f.BasisUom, f.RatePerOutputUnit }).ToArray(),
            OriginalEdges = originalEdges.OrderBy(e => e.Id).Select(e => new { e.Id, e.ContributorMovementId,
                e.ConsumerMovementId, e.StockPostingId }).ToArray(),
            EdgeReversals = edgeReversals.OrderBy(e => e.Id).Select(e => new { e.Id, e.ReversesDependencyId,
                e.ContributorMovementId, e.ConsumerMovementId, e.StockPostingId }).ToArray(),
            OriginalAllocationId = allocationId,
            AllocationReversals = allocationReversals.Select(a => new { a.Id, a.StockPostingId,
                a.ReceiptMovementId, a.OutboundMovementId, a.BaseQty }).ToArray(),
            ConversionReversals = conversionReversals.OrderBy(f => f.Id).Select(f => new { f.Id,
                f.ReversesFactId, f.StockPostingId, f.ProductionMovementId, f.CostAmount }).ToArray(),
            Source = new { sourceLot.Uid, sourceLot.Qty, sourceLot.BaseQty, sourceLot.TotalCost,
                sourceLot.AverageUnitCost, sourceLot.WorkOrderId, sourceLot.WorkOrderMaterialId,
                sourcePool.Generation, sourcePool.Status,
                RowVersion = Convert.ToHexString(sourceLot.RowVersion ?? []) },
            Destination = new { outputLot.Uid, outputLot.Qty, outputLot.BaseQty, outputLot.TotalCost,
                outputLot.AverageUnitCost, outputPool.Generation, outputPool.Status,
                RowVersion = Convert.ToHexString(outputLot.RowVersion ?? []) },
            Graph = graph.Edges, Allocations = allocations.Select(a => new { a.Id, a.BaseQty }).ToArray()
        }, new { }).RequestFingerprint;
        return new(ids[0], null, hash,
            new[] { consume.MovementDate, activeFinishedGood?.EffectiveAt ?? consume.MovementDate,
                correctedReversedFinishedGood?.ReversalAt ?? consume.MovementDate }.Max(),
            outputMovements.Length + 1 + (activeFinishedGood is null ? 0 : 1)
                + (correctedReversedFinishedGood is null ? 0 : 2),
            issue.Uid, consume.Uid, produce.Uid, reversed, activeFinishedGood, correctedReversedFinishedGood);
    }

    private static async Task ApplyStandardProductionAsync(
        StockPostingContext context, StandardProductionReplayPlan plan,
        string inventoryPlanHash, long branchWatermark,
        IReadOnlyList<StockReplayStandardEvent> events, CancellationToken ct)
    {
        if (plan.Leaf is not null)
        {
            await ApplyProductionLeafAsync(context, plan.Leaf, inventoryPlanHash,
                branchWatermark, events.Max(e => e.Key.EffectiveAt), events.Count, ct);
            return;
        }
        var current = await PlanStandardProductionTailAsync(context.Db, context.CompanyCode,
            context.BranchCode, events, context.Posting.EffectiveAt, ct);
        if (current != plan)
            throw new StockLedgerException(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
                "Production output changed after receipt replay planning.", Retryable: true));
        if (await context.Db.StockReplayRuns.AsNoTracking().AnyAsync(r =>
                r.RootStockPostingId == context.Posting.Id, ct))
            throw ProductionReplayError("The receipt root already has a cost replay run.");
        context.Db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
            RootStockPostingId = context.Posting.Id, RootRequestId = context.Posting.RequestId,
            RootDocumentType = context.Posting.SourceDocumentType,
            RootDocumentId = context.Posting.SourceDocumentId,
            EarliestEffectiveAt = context.Posting.EffectiveAt,
            LatestEffectiveAt = events.Max(e => e.Key.EffectiveAt) > plan.LatestEffectiveAt
                ? events.Max(e => e.Key.EffectiveAt) : plan.LatestEffectiveAt,
            PlanWatermark = branchWatermark,
            PlanHash = StockPostingFingerprint.Create(new { inventoryPlanHash, production = plan },
                new { }).RequestFingerprint,
            AffectedHistoryCount = events.Count, AffectedItemCount = 1,
            AffectedProductionMovementCount = plan.MovementCount,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = context.UserId
        });
        await context.Db.SaveChangesAsync(ct);
    }
}
