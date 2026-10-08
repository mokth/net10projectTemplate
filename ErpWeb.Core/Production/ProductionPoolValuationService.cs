using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Records provenance, FIFO genealogy and pooled dependencies in the caller's transaction.</summary>
public static class ProductionPoolValuationService
{
    public const string Verified = "VERIFIED";
    public const string Unvalued = "UNVALUED";

    public static async Task RecordAsync(StockPostingContext context, IReadOnlyList<ProductionBalLotMovement> movements,
        CancellationToken ct)
    {
        context.EnsureUnsealed();
        var db = context.Db;
        var registry = new StockMovementRegistry();
        var inputIds = movements.Where(x => x.MovementType == ProductionBalLotMovementTypes.Consume).Select(x => x.Uid).ToArray();
        var inputStatuses = new List<string>();
        foreach (var movement in movements.OrderBy(x => x.MovementType == ProductionBalLotMovementTypes.Produce ? 1 : 0).ThenBy(x => x.Uid))
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
            var original = movement.OriginalMovementId is long originalId
                ? await db.ProductionValuationEvidenceRows.SingleOrDefaultAsync(x => x.MovementId == originalId, ct) : null;
            var status = direction < 0 ? pool.Status : Unvalued;
            var basis = direction < 0 ? "POOLED_AVERAGE" : "MISSING_EVIDENCE";
            if (original is not null) { status = original.Status; basis = "REVERSAL"; }
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
                var allocations = await db.ProductionMovementAllocations.Where(x => x.OutboundMovementId == reversedId && x.ReversesAllocationId == null).ToListAsync(ct);
                foreach (var a in allocations)
                    db.ProductionMovementAllocations.Add(new() { CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                        StockPostingId = context.Posting.Id, ReceiptMovementId = a.ReceiptMovementId, OutboundMovementId = movement.Uid,
                        BaseQty = a.BaseQty, ReversesAllocationId = a.Id, CreatedAtUtc = context.Posting.PostedAtUtc });
                var edges = await db.ProductionPoolDependencyRows.Where(x => x.ConsumerMovementId == reversedId && x.ReversesDependencyId == null).ToListAsync(ct);
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
                var receiptIds = inbound.Where(x => x.OriginalMovementId == null).Select(x => x.Uid).ToArray();
                var used = await db.ProductionMovementAllocations.Where(x => receiptIds.Contains(x.ReceiptMovementId)).ToListAsync(ct);
                var sequenceIds = inbound.Where(x => x.StockPostingId.HasValue).Select(x => x.StockPostingId!.Value).ToArray();
                var sequences = await db.StockPostings.Where(x => sequenceIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.PostingSequence, ct);
                var candidates = inbound.Where(x => x.OriginalMovementId == null).Select(x => new ProductionContribution(lot.Uid, x.Uid,
                    x.MovementDate, sequences.GetValueOrDefault(x.StockPostingId ?? 0), x.PostingLineNo ?? 0,
                    x.BaseQty - contributors.Where(r => r.OriginalMovementId == x.Uid && registry.GetRequired(r.MovementType).Direction < 0).Sum(r => r.BaseQty) - used.Where(a => a.ReceiptMovementId == x.Uid).Sum(a => a.ReversesAllocationId == null ? a.BaseQty : -a.BaseQty))).ToArray();
                // Unvalued legacy consumption may lack a complete contribution ledger. FG never permits that state.
                if (candidates.Sum(x => x.AvailableBaseQty) >= movement.BaseQty)
                    foreach (var a in new ProductionContributionAllocator().BuildPlan(candidates, [new(movement.Uid.ToString(), movement.BaseQty)]))
                        db.ProductionMovementAllocations.Add(new() { CompanyCode = context.CompanyCode, BranchCode = context.BranchCode,
                            StockPostingId = context.Posting.Id, ReceiptMovementId = a.ReceiptMovementId, OutboundMovementId = movement.Uid,
                            BaseQty = a.BaseQty, CreatedAtUtc = context.Posting.PostedAtUtc });
                else if (movement.MovementType == ProductionBalLotMovementTypes.FgReceiptOut)
                    throw new InvalidOperationException("The source pool has incomplete quantity contribution evidence.");
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

    public static async Task<bool> HasActiveDependentsAsync(AppDbContext db, IReadOnlyCollection<long> movementIds, CancellationToken ct)
    {
        var visited = movementIds.ToHashSet();
        var frontier = movementIds.ToArray();
        while (frontier.Length > 0)
        {
            var edges = await db.ProductionPoolDependencyRows.Where(x => frontier.Contains(x.ContributorMovementId)).ToListAsync(ct);
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
