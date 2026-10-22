using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed record ProductionInboundValuationAuthority(
    string CostBasisCode, decimal UnitCost, decimal TotalValue,
    long? OriginalIssueMovementId = null, long? StandardCostRevisionId = null,
    long? ApprovedAdjustmentLineId = null);

/// <summary>Optional strict lineage and inbound value evidence. Null preserves legacy callers.</summary>
public sealed class ProductionPoolValuationDirectives
{
    public IReadOnlyDictionary<long, long> RequiredReceiptMovementByOutboundMovementId { get; init; }
        = new Dictionary<long, long>();
    public IReadOnlyDictionary<long, ProductionInboundValuationAuthority> InboundValuationAuthorityByMovementId { get; init; }
        = new Dictionary<long, ProductionInboundValuationAuthority>();
}

/// <summary>Records provenance, FIFO genealogy and pooled dependencies in the caller's transaction.</summary>
public static class ProductionPoolValuationService
{
    public const string Verified = "VERIFIED";
    public const string Unvalued = "UNVALUED";

    public static async Task RecordAsync(StockPostingContext context, IReadOnlyList<ProductionBalLotMovement> movements,
        CancellationToken ct)
        => await RecordAsync(context, movements, null, ct);

    public static async Task RecordAsync(StockPostingContext context, IReadOnlyList<ProductionBalLotMovement> movements,
        ProductionPoolValuationDirectives? directives, CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        var registry = new StockMovementRegistry();
        IReadOnlyList<WorkOrderTransferPair> transferPairs = context.Posting.CommandType == ProductionPostingCommandTypes.WorkOrderTransferRollback
            ? Array.Empty<WorkOrderTransferPair>()
            : ValidateWorkOrderTransferPairs(movements, context.Posting.Id);
        var transferInPairByMovement = transferPairs.ToDictionary(x => x.Inbound.Uid, x => x);
        var inputIds = movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.Consume).Select(x => x.Uid).ToArray();
        var inputStatuses = new List<string>();
        var orderedMovements = movements.OrderBy(x => x.MovementType == ProductionBalLotMovementTypes.Produce ? 1 : 0)
            .ThenBy(x => x.Uid).ToList();
        foreach (var pair in transferPairs)
        {
            orderedMovements.Remove(pair.Inbound);
            var outboundIndex = orderedMovements.IndexOf(pair.Outbound);
            if (outboundIndex < 0)
                throw new InvalidOperationException("Work Order Transfer OUT movement is missing from its posting set.");
            orderedMovements.Insert(outboundIndex + 1, pair.Inbound);
        }
        foreach (var movement in orderedMovements)
        {
            if (await db.ProductionValuationEvidenceRows.AnyAsync(x => x.MovementId == movement.Uid, ct))
                throw new InvalidOperationException("Valuation evidence is immutable and has already been recorded.");
            var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == movement.ProductionBalLotId, ct);
            var pool = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(x => x.ProductionBalLotId == lot.Uid, ct);
            if (pool is null)
            {
                // Existing numeric balances have no trustworthy cost evidence. Never backfill them as verified.
                var samePool = movements.Where(x => x.ProductionBalLotId == lot.Uid).ToArray();
                pool = new ProductionPoolValuation
                {
                    ProductionBalLotId = lot.Uid,
                    TrackedBaseQty = IvQty.Round(lot.BaseQty - samePool.Sum(x => registry.GetRequired(x.MovementType).Direction * x.BaseQty)),
                    TrackedValue = StockLedgerPrecision.Money(lot.TotalCost - samePool.Sum(x => registry.GetRequired(x.MovementType).Direction * x.TotalCost))
                };
                db.ProductionPoolValuationRows.Add(pool);
            }
            if (pool.TrackedBaseQty == 0 && pool.TrackedValue != 0)
                throw new InvalidOperationException($"Production pool {lot.Uid} has stranded value.");
            var direction = registry.GetRequired(movement.MovementType).Direction;
            var hasInboundAuthority = directives is not null
                && directives.InboundValuationAuthorityByMovementId.TryGetValue(movement.Uid, out _);
            var inboundAuthority = hasInboundAuthority
                ? directives!.InboundValuationAuthorityByMovementId[movement.Uid]
                : null;
            if (hasInboundAuthority)
                await ValidateInboundAuthorityAsync(context, lot, pool, movement, inboundAuthority!, ct);
            var original = movement.OriginalMovementId is long originalId
                ? await db.ProductionValuationEvidenceRows.SingleOrDefaultAsync(x => x.MovementId == originalId, ct) : null;
            var status = direction < 0 ? pool.Status : Unvalued;
            var basis = direction < 0 ? "POOLED_AVERAGE" : "MISSING_EVIDENCE";
            if (original is not null) { status = original.Status; basis = "REVERSAL"; }
            if (transferInPairByMovement.TryGetValue(movement.Uid, out var transferPair))
            {
                var outboundEvidence = await db.ProductionValuationEvidenceRows
                    .SingleOrDefaultAsync(x => x.MovementId == transferPair.Outbound.Uid, ct);
                if (outboundEvidence?.Status != Verified)
                    throw new InvalidOperationException("Work Order Transfer IN requires its paired OUT to be VERIFIED first.");
                if (pool.TrackedBaseQty == 0m && pool.TrackedValue == 0m)
                {
                    // An empty reusable destination identity may start a new verified generation.
                }
                else if (pool.TrackedBaseQty <= 0m || pool.Status != Verified)
                {
                    throw new InvalidOperationException("A nonempty Work Order Transfer destination pool must already be VERIFIED.");
                }
                status = Verified;
                basis = $"WORK_ORDER_TRANSFER:{transferPair.Outbound.Uid}";
            }
            if (hasInboundAuthority)
            {
                status = Verified;
                basis = inboundAuthority!.CostBasisCode;
            }
            if (movement.MovementType == ProductionBalLotMovementTypes.Produce)
            {
                var conversionFacts = movement.OriginalMovementId is null
                    ? await db.ProductionConversionCostFacts
                        .Where(x => x.ProductionMovementId == movement.Uid && x.ReversesFactId == null)
                        .ToListAsync(ct)
                    : new List<ProductionConversionCostFact>();
                var consumedValue = StockLedgerPrecision.Money(
                    movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.Consume)
                        .Sum(x => x.TotalCost));
                var conversionValue = StockLedgerPrecision.Money(conversionFacts.Sum(x => x.CostAmount));
                var hasConsumedInputs = inputStatuses.Count > 0;
                var hasConversion = conversionValue > 0m;
                var hasAuthority = hasConsumedInputs || hasConversion;
                var expectedProduceValue = StockLedgerPrecision.Money(consumedValue + conversionValue);
                if (movement.OriginalMovementId is null && movement.TotalCost != expectedProduceValue)
                {
                    throw new InvalidOperationException(
                        $"Production PRODUCE movement {movement.Uid} does not reconcile to consumed inputs plus absorbed conversion facts.");
                }

                status = inputStatuses.All(x => x == Verified) && hasAuthority ? Verified : Unvalued;
                basis = hasConsumedInputs && hasConversion
                    ? "CONSUMED_INPUTS+ABSORBED_CONVERSION"
                    : hasConversion ? "ABSORBED_CONVERSION" : "CONSUMED_INPUTS";
            }
            var history = movement.InventoryHistoryId is int historyId
                ? await db.IvTrxHistories.SingleOrDefaultAsync(x => x.Id == historyId, ct) : null;
            if (movement.MovementType == ProductionBalLotMovementTypes.Issue)
            {
                var valuationFacts = await db.StockValuationFacts
                    .Where(x => x.StockPostingId == context.Posting.Id
                        && x.InventoryHistoryId == movement.InventoryHistoryId
                        && x.Direction < 0
                        && x.MovementCode == "PRODUCTION_MATERIAL_OUT"
                        && x.ValuationStatus == StockValuationStatuses.Valued)
                    .ToListAsync(ct);
                var factQty = IvQty.Round(valuationFacts.Sum(x => x.BaseQty));
                var factValue = StockLedgerPrecision.Money(valuationFacts.Sum(x => x.CostAmount));
                if (movement.InventoryHistoryId is null
                    || valuationFacts.Count == 0
                    || factQty != IvQty.Round(movement.BaseQty)
                    || factValue != StockLedgerPrecision.Money(movement.TotalCost))
                {
                    throw new InvalidOperationException(
                        $"Production ISSUE movement {movement.Uid} does not reconcile to V2 inventory valuation facts.");
                }

                status = Verified;
                basis = $"STOCK_VALUATION:{context.CostMethod}";
            }
            if (direction > 0)
            {
                if (pool.TrackedBaseQty == 0 && pool.TrackedValue == 0)
                {
                    pool.Generation = original?.Generation ?? pool.Generation + 1;
                    pool.Status = status;
                }
                else if (status != Verified) pool.Status = Unvalued;
                else if (hasInboundAuthority) pool.Status = Verified;
                // Verified receipts cannot cleanse a contaminated nonempty pool.
            }
            var evidence = new ProductionValuationEvidence
            {
                MovementId = movement.Uid, ProductionBalLotId = lot.Uid, Generation = pool.Generation,
                Status = status, Basis = basis, Currency = status == Verified ? "COMPANY_BASE" : null,
                PriceUom = movement.BaseUom,
                Price = status == Verified
                    ? StockLedgerPrecision.Money(movement.BaseQty > 0m
                        ? movement.TotalCost / movement.BaseQty
                        : movement.UnitCost)
                    : null,
                ConversionFactor = movement.ConversionFactorToBase, InventoryHistoryId = history?.Id,
                OriginalMovementId = movement.OriginalMovementId
            };
            db.ProductionValuationEvidenceRows.Add(evidence);
            movement.ValuationStatus = status;
            if (movement.MovementType == ProductionBalLotMovementTypes.Consume) inputStatuses.Add(status);

            if (movement.OriginalMovementId is long reversedId)
            {
                var allocations = await ProductionActiveEdgeResolver.Allocations(db)
                    .Where(x => x.OutboundMovementId == reversedId).ToListAsync(ct);
                foreach (var a in allocations)
                    db.ProductionMovementAllocations.Add(new() { CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                        StockPostingId = context.Posting.Id, ReceiptMovementId = a.ReceiptMovementId, OutboundMovementId = movement.Uid,
                        BaseQty = a.BaseQty, ReversesAllocationId = a.Id, CreatedAtUtc = context.Posting.PostedAtUtc });
                var edges = await ProductionActiveEdgeResolver.Dependencies(db)
                    .Where(x => x.ConsumerMovementId == reversedId).ToListAsync(ct);
                foreach (var edge in edges)
                {
                    db.ProductionPoolDependencyRows.Add(new() { ContributorMovementId = edge.ContributorMovementId,
                        ConsumerMovementId = movement.Uid, StockPostingId = context.Posting.Id, ReversesDependencyId = edge.Id });
                    // The restored receipt remembers all original contributors, including those without FIFO allocations.
                }
            }
            else if (direction < 0)
            {
                var contributors = await (from e in db.ProductionValuationEvidenceRows
                    join m in db.ProductionBalLotMovements on e.MovementId equals m.Uid
                    where e.ProductionBalLotId == lot.Uid
                    select m).ToListAsync(ct);
                var inbound = contributors.Where(x => registry.GetRequired(x.MovementType).Direction > 0).ToArray();
                var currentIds = await db.ProductionValuationEvidenceRows.Where(x => x.ProductionBalLotId == lot.Uid && x.Generation == pool.Generation).Select(x => x.MovementId).ToListAsync(ct);
                foreach (var contributor in inbound.Where(x => currentIds.Contains(x.Uid)))
                    db.ProductionPoolDependencyRows.Add(new() { ContributorMovementId = contributor.Uid, ConsumerMovementId = movement.Uid, StockPostingId = context.Posting.Id });
                var availability = await ProductionContributionAvailabilityService.ReadAsync(db, lot.Uid, pool.Generation, ct);
                var candidates = availability.Select(x => x.Contribution).ToArray();
                if (directives is not null
                    && directives.RequiredReceiptMovementByOutboundMovementId.TryGetValue(movement.Uid, out var receiptId))
                {
                    var receipt = availability.SingleOrDefault(x => x.Movement.Uid == receiptId);
                    var validPair = movement.MovementType == ProductionBalLotMovementTypes.Return
                        && receipt?.Movement.MovementType == ProductionBalLotMovementTypes.Issue
                        || movement.MovementType == ProductionBalLotMovementTypes.AdjustOut
                        && receipt?.Movement.MovementType == ProductionBalLotMovementTypes.AdjustIn;
                    if (!validPair || receipt is null || receipt.Evidence.Status != Verified
                        || receipt.Contribution.AvailableBaseQty < movement.BaseQty)
                        throw new InvalidOperationException("Production outbound quantity is not available from its exact current-generation VERIFIED receipt contribution.");
                    candidates = [receipt.Contribution];
                }
                // Unvalued legacy consumption may lack a complete contribution ledger. FG never permits that state.
                if (candidates.Sum(x => x.AvailableBaseQty) >= movement.BaseQty)
                    foreach (var a in new ProductionContributionAllocator().BuildPlan(candidates, [new(movement.Uid.ToString(), movement.BaseQty)]))
                        db.ProductionMovementAllocations.Add(new() { CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                            StockPostingId = context.Posting.Id, ReceiptMovementId = a.ReceiptMovementId, OutboundMovementId = movement.Uid,
                            BaseQty = a.BaseQty, CreatedAtUtc = context.Posting.PostedAtUtc });
                else if (movement.MovementType == ProductionBalLotMovementTypes.FgReceiptOut)
                    throw new InvalidOperationException("The source pool has incomplete quantity contribution evidence.");
            }
            if (movement.MovementType == ProductionBalLotMovementTypes.AdjustIn && hasInboundAuthority)
            {
                var sourceIds = inboundAuthority!.CostBasisCode switch
                {
                    ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost =>
                        await db.ProductionValuationEvidenceRows.Where(x => x.ProductionBalLotId == lot.Uid
                                && x.Generation == pool.Generation && x.MovementId != movement.Uid)
                            .Select(x => x.MovementId).ToListAsync(ct),
                    ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost => inboundAuthority.OriginalIssueMovementId is long sourceId
                        ? await db.ProductionMaterialMovements.Where(x => x.Uid == sourceId && x.ProductionBalLotMovementId != null)
                            .Select(x => x.ProductionBalLotMovementId!.Value).ToListAsync(ct)
                        : [],
                    _ => []
                };
                foreach (var sourceId in sourceIds.Distinct().Where(x => x != movement.Uid))
                    db.ProductionPoolDependencyRows.Add(new() { ContributorMovementId = sourceId,
                        ConsumerMovementId = movement.Uid, StockPostingId = context.Posting.Id });
            }
            if (transferInPairByMovement.TryGetValue(movement.Uid, out var completedTransferPair))
            {
                db.ProductionPoolDependencyRows.Add(new()
                {
                    ContributorMovementId = completedTransferPair.Outbound.Uid,
                    ConsumerMovementId = movement.Uid,
                    StockPostingId = context.Posting.Id
                });
            }
            if (movement.MovementType == ProductionBalLotMovementTypes.Produce)
                foreach (var inputId in inputIds)
                    db.ProductionPoolDependencyRows.Add(new() { ContributorMovementId = inputId, ConsumerMovementId = movement.Uid, StockPostingId = context.Posting.Id });
            pool.TrackedBaseQty = IvQty.Round(pool.TrackedBaseQty + direction * movement.BaseQty);
            pool.TrackedValue = StockLedgerPrecision.Money(pool.TrackedValue + direction * movement.TotalCost);
            if (pool.TrackedBaseQty < 0 || pool.TrackedValue < 0 || (pool.TrackedBaseQty == 0 && pool.TrackedValue != 0))
                throw new InvalidOperationException("Production quantity/value reconciliation failed.");
            await db.SaveChangesAsync(ct);
        }
    }

    private sealed record WorkOrderTransferPair(ProductionBalLotMovement Outbound, ProductionBalLotMovement Inbound);

    private static IReadOnlyList<WorkOrderTransferPair> ValidateWorkOrderTransferPairs(
        IReadOnlyList<ProductionBalLotMovement> movements, long stockPostingId)
    {
        var transferDocuments = movements.Where(x =>
            string.Equals(x.DocumentType, ProductionDocumentTypes.WorkOrderTransfer, StringComparison.Ordinal)).ToArray();
        if (transferDocuments.Length == 0) return [];
        if (transferDocuments.Any(x => x.MovementType is not (ProductionBalLotMovementTypes.TransferOut or ProductionBalLotMovementTypes.TransferIn)))
            throw new InvalidOperationException("Work Order Transfer posting contains a non-transfer movement.");
        if (transferDocuments.Length != 2 || transferDocuments.Any(x => x.MovementGroupId is null))
            throw new InvalidOperationException("A Work Order Transfer posting must contain one grouped OUT/IN movement pair.");
        var groups = transferDocuments.GroupBy(x => x.MovementGroupId).ToArray();
        if (groups.Length != 1) throw new InvalidOperationException("A Work Order Transfer posting must contain exactly one movement group.");
        var group = groups[0].ToArray();
        var outboundRows = group.Where(x => x.MovementType == ProductionBalLotMovementTypes.TransferOut).ToArray();
        var inboundRows = group.Where(x => x.MovementType == ProductionBalLotMovementTypes.TransferIn).ToArray();
        if (group.Length != 2 || outboundRows.Length != 1 || inboundRows.Length != 1)
            throw new InvalidOperationException("A Work Order Transfer movement group must contain exactly one OUT and one IN.");
        var outbound = outboundRows[0];
        var inbound = inboundRows[0];
        if (outbound.StockPostingId != stockPostingId || inbound.StockPostingId != stockPostingId
            || outbound.OriginalMovementId is not null || inbound.OriginalMovementId is not null
            || outbound.ProductionBalLotId == inbound.ProductionBalLotId || outbound.WorkOrderId == inbound.WorkOrderId
            || outbound.ItemCode != inbound.ItemCode || outbound.Qty != inbound.Qty
            || outbound.Uom != inbound.Uom || outbound.BaseQty != inbound.BaseQty
            || outbound.BaseUom != inbound.BaseUom || outbound.ConversionFactorToBase != inbound.ConversionFactorToBase
            || outbound.TotalCost != inbound.TotalCost || outbound.UnitCost != inbound.UnitCost
            || outbound.DocumentNo != inbound.DocumentNo || outbound.CompanyCode != inbound.CompanyCode
            || outbound.BranchCode != inbound.BranchCode
            || outbound.ValuationStatus != Verified || inbound.ValuationStatus != Verified)
            throw new InvalidOperationException("Work Order Transfer OUT and IN movement evidence does not match.");
        return [new(outbound, inbound)];
    }

    private static async Task ValidateInboundAuthorityAsync(StockPostingContext context, ProductionBalLot lot,
        ProductionPoolValuation pool, ProductionBalLotMovement movement,
        ProductionInboundValuationAuthority authority, CancellationToken ct)
    {
        if (movement.MovementType != ProductionBalLotMovementTypes.AdjustIn || movement.OriginalMovementId is not null
            || authority.TotalValue < 0m || authority.UnitCost < 0m
            || StockLedgerPrecision.Money(authority.TotalValue) != movement.TotalCost
            || StockLedgerPrecision.Money(authority.UnitCost) != movement.UnitCost
            || StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty) != movement.UnitCost)
            throw new InvalidOperationException("ADJUST_IN does not match its six-decimal Production cost authority.");
        switch (authority.CostBasisCode)
        {
            case ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost:
                if (pool.Status != Verified || pool.TrackedBaseQty <= 0m || pool.TrackedValue < 0m
                    || authority.OriginalIssueMovementId is not null || authority.StandardCostRevisionId is not null
                    || StockLedgerPrecision.Money(pool.TrackedValue * movement.BaseQty / pool.TrackedBaseQty) != movement.TotalCost
                    || StockLedgerPrecision.Money(movement.TotalCost / movement.BaseQty) != movement.UnitCost)
                    throw new InvalidOperationException("A nonempty pool ADJUST_IN must use its exact current verified Production pool value.");
                break;
            case ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost:
                if (pool.TrackedBaseQty != 0m || pool.TrackedValue != 0m || authority.OriginalIssueMovementId is not long issueId
                    || authority.StandardCostRevisionId is not null)
                    throw new InvalidOperationException("ORIGINAL_ISSUE_COST applies only to an empty MATERIAL_IN Production generation.");
                var materialIssue = await context.Db.ProductionMaterialMovements.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Uid == issueId && x.MovementType == ProductionMaterialMovementTypes.Issue
                        && x.ProductionBalLotId == lot.Uid && x.WorkOrderId == lot.WorkOrderId
                        && x.WorkOrderMaterialId == lot.WorkOrderMaterialId, ct)
                    ?? throw new InvalidOperationException("The exact original MATERIAL_IN ISSUE cost source is unavailable.");
                var issueMovement = materialIssue.ProductionBalLotMovementId is long issueBalanceMovementId
                    ? await context.Db.ProductionBalLotMovements.SingleAsync(x => x.Uid == issueBalanceMovementId, ct) : null;
                var issueEvidence = issueMovement is null ? null : await context.Db.ProductionValuationEvidenceRows
                    .SingleOrDefaultAsync(x => x.MovementId == issueMovement.Uid, ct);
                if (issueMovement is null || issueEvidence?.Status != Verified
                    || materialIssue.BaseQty <= 0m
                    || StockLedgerPrecision.Money(issueMovement.TotalCost * movement.BaseQty / materialIssue.BaseQty) != movement.TotalCost)
                    throw new InvalidOperationException("ORIGINAL_ISSUE_COST does not match the exact VERIFIED source ISSUE cost.");
                break;
            case ProductionStockAdjustmentCostBasisCodes.StandardCost:
                if (authority.OriginalIssueMovementId is not null || authority.StandardCostRevisionId is not long standardId)
                    throw new InvalidOperationException("STANDARD_COST requires its approved effective revision ID.");
                var standard = await context.Db.ItemStandardCostRevisions.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.Id == standardId && x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode && x.ItemCode == lot.ItemCode
                        && x.Status == ItemStandardCostRevisionStatuses.Approved && x.EffectiveFrom <= context.Posting.EffectiveAt.Date
                        && (x.EffectiveTo == null || x.EffectiveTo > context.Posting.EffectiveAt.Date), ct)
                    ?? throw new InvalidOperationException("STANDARD_COST does not identify one approved revision effective on the adjustment date.");
                if (StockLedgerPrecision.Money(standard.TotalStandardCost * movement.BaseQty) != movement.TotalCost)
                    throw new InvalidOperationException("STANDARD_COST total value does not match its approved revision.");
                break;
            case ProductionStockAdjustmentCostBasisCodes.ManualApproved:
            case ProductionStockAdjustmentCostBasisCodes.ZeroCostApproved:
                var line = authority.ApprovedAdjustmentLineId is long lineId
                    ? await context.Db.ProductionStockAdjustmentLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == lineId
                        && x.BatchId == int.Parse(context.Posting.SourceDocumentId), ct) : null;
                if (line is null || line.CostEvidenceType != authority.CostBasisCode
                    || StockLedgerPrecision.Money(line.ProposedUnitCost.GetValueOrDefault() * movement.BaseQty) != movement.TotalCost
                    || string.IsNullOrWhiteSpace(line.CostOverrideReason)
                    || string.IsNullOrWhiteSpace(line.CostApprovedBy) || line.CostApprovedAtUtc is null
                    || StockLedgerPrecision.Money(authority.UnitCost * movement.BaseQty) != movement.TotalCost
                    || authority.CostBasisCode == ProductionStockAdjustmentCostBasisCodes.ManualApproved && movement.UnitCost <= 0m
                    || authority.CostBasisCode == ProductionStockAdjustmentCostBasisCodes.ZeroCostApproved && movement.UnitCost != 0m)
                    throw new InvalidOperationException("PADJ override has no complete server-approved six-decimal sidecar evidence.");
                break;
            default:
                throw new InvalidOperationException("Unknown Production Stock Adjustment valuation authority.");
        }
        if (authority.CostBasisCode != ProductionStockAdjustmentCostBasisCodes.CurrentProductionPoolCost
            && authority.CostBasisCode != ProductionStockAdjustmentCostBasisCodes.OriginalIssueCost
            && pool.TrackedBaseQty != 0m && pool.Status != Verified)
            throw new InvalidOperationException("A nonempty UNVALUED Production pool cannot accept ADJUST_IN.");
    }

    public static async Task<bool> HasActiveDependentsAsync(AppDbContext db, IReadOnlyCollection<long> movementIds, CancellationToken ct)
    {
        var visited = movementIds.ToHashSet();
        var frontier = movementIds.ToArray();
        while (frontier.Length > 0)
        {
            var edges = await ProductionActiveEdgeResolver.Dependencies(db)
                .Where(x => frontier.Contains(x.ContributorMovementId)).ToListAsync(ct);
            var consumers = edges.Select(x => x.ConsumerMovementId).Where(x => !visited.Contains(x)).Distinct().ToArray();
            if (consumers.Length == 0) break;
            var active = await (from m in db.ProductionBalLotMovements join p in db.StockPostings on m.StockPostingId equals p.Id
                where consumers.Contains(m.Uid) && m.OriginalMovementId == null && p.SealedAtUtc != null
                    && !db.StockPostings.Any(r => r.ReversesPostingId == p.Id && r.SealedAtUtc != null)
                select m.Uid).AnyAsync(ct);
            if (active) return true;
            visited.UnionWith(consumers); frontier = consumers;
        }
        return false;
    }
}
