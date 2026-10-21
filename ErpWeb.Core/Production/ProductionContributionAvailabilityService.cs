using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>One receipt contribution formula is shared by outbound allocation and return eligibility.</summary>
public sealed record ProductionReceiptAvailability(ProductionContribution Contribution,
    ProductionBalLotMovement Movement, ProductionValuationEvidence Evidence);

public static class ProductionContributionAvailabilityService
{
    public static decimal CalculateAvailableBaseQty(long receiptMovementId, decimal receiptBaseQty,
        IEnumerable<ProductionBalLotMovement> poolMovements,
        IEnumerable<ProductionMovementAllocation> receiptAllocations)
    {
        var movements = poolMovements as IReadOnlyCollection<ProductionBalLotMovement> ?? poolMovements.ToArray();
        var allocations = receiptAllocations as IReadOnlyCollection<ProductionMovementAllocation> ?? receiptAllocations.ToArray();
        var registry = new StockMovementRegistry();
        var reversedOut = movements.Where(x => x.OriginalMovementId == receiptMovementId
                && registry.GetRequired(x.MovementType).Direction < 0)
            .Sum(x => x.BaseQty);
        var used = allocations.Where(x => x.ReceiptMovementId == receiptMovementId)
            .Sum(x => x.ReversesAllocationId is null ? x.BaseQty : -x.BaseQty);
        return IvQty.Round(receiptBaseQty - reversedOut - used);
    }

    public static async Task<IReadOnlyList<ProductionReceiptAvailability>> ReadAsync(
        AppDbContext db, long productionBalLotId, int? generation, CancellationToken ct)
    {
        var rows = await (from evidence in db.ProductionValuationEvidenceRows.AsNoTracking()
            join movement in db.ProductionBalLotMovements.AsNoTracking() on evidence.MovementId equals movement.Uid
            where evidence.ProductionBalLotId == productionBalLotId
                && (!generation.HasValue || evidence.Generation == generation.Value)
            select new { Evidence = evidence, Movement = movement }).ToListAsync(ct);
        var movements = rows.Select(x => x.Movement).ToArray();
        var receiptIds = rows.Where(x => x.Movement.OriginalMovementId is null
                && new StockMovementRegistry().GetRequired(x.Movement.MovementType).Direction > 0)
            .Select(x => x.Movement.Uid).ToArray();
        if (receiptIds.Length == 0) return [];
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(x => receiptIds.Contains(x.ReceiptMovementId)).ToListAsync(ct);
        var sequenceIds = rows.Where(x => x.Movement.StockPostingId.HasValue)
            .Select(x => x.Movement.StockPostingId!.Value).Distinct().ToArray();
        var sequences = sequenceIds.Length == 0 ? new Dictionary<long, long>()
            : await db.StockPostings.AsNoTracking().Where(x => sequenceIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.PostingSequence, ct);
        var registry = new StockMovementRegistry();
        var result = new List<ProductionReceiptAvailability>(receiptIds.Length);
        foreach (var row in rows.Where(x => receiptIds.Contains(x.Movement.Uid)))
        {
            var receipt = row.Movement;
            var available = CalculateAvailableBaseQty(receipt.Uid, receipt.BaseQty, movements, allocations);
            result.Add(new(new ProductionContribution(productionBalLotId, receipt.Uid, receipt.MovementDate,
                sequences.GetValueOrDefault(receipt.StockPostingId ?? 0), receipt.PostingLineNo ?? 0, available),
                receipt, row.Evidence));
        }
        return result;
    }

    public static async Task<ProductionReceiptAvailability> RequireCurrentIssueAsync(
        AppDbContext db, ProductionBalLot pool, long issueMovementId, CancellationToken ct)
    {
        var valuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == pool.Uid, ct)
            ?? throw new InvalidOperationException("Production pool valuation is missing.");
        var receipt = (await ReadAsync(db, pool.Uid, valuation.Generation, ct))
            .SingleOrDefault(x => x.Movement.Uid == issueMovementId)
            ?? throw new InvalidOperationException("The original ISSUE receipt is outside the current Production valuation generation.");
        if (receipt.Movement.MovementType != ProductionBalLotMovementTypes.Issue
            || receipt.Movement.OriginalMovementId is not null
            || receipt.Evidence.Status != ProductionPoolValuationService.Verified)
            throw new InvalidOperationException("Only a VERIFIED original ISSUE receipt in the current generation can be returned.");
        if (receipt.Contribution.AvailableBaseQty < 0m || receipt.Contribution.AvailableBaseQty > pool.BaseQty)
            throw new InvalidOperationException("Original ISSUE contribution availability does not reconcile to the Production pool.");
        return receipt;
    }
}
