using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed class ProductionPostingInvariantException : InvalidOperationException
{
    public ProductionPostingInvariantException(string detail)
        : base($"Production valuation evidence is incomplete; posting was rolled back. {detail}")
    {
    }
}

/// <summary>Proves V2 movement, immutable evidence, and reconciled pool state before a Production post is sealed.</summary>
public static class ProductionPostingInvariant
{
    public static async Task AssertIssueVerifiedAsync(
        StockPostingContext context,
        long postingLinkId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var movements = await context.Db.ProductionBalLotMovements
            .Where(x => x.PostingLinkId == postingLinkId && x.MovementType == ProductionBalLotMovementTypes.Issue)
            .OrderBy(x => x.Uid)
            .ToListAsync(ct);
        if (movements.Count == 0)
            throw new ProductionPostingInvariantException("No ISSUE movement was recorded for the posting link.");

        await AssertMovementsAsync(context, movements, requireIssueHistory: true, ct);
        await AssertTouchedPoolsAsync(context, movements, ct);
    }

    public static async Task AssertOutputVerifiedAsync(
        StockPostingContext context,
        long outputId,
        long postingLinkId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        var movements = await context.Db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == outputId
                && x.PostingLinkId == postingLinkId
                && (x.MovementType == ProductionBalLotMovementTypes.Consume
                    || x.MovementType == ProductionBalLotMovementTypes.Produce))
            .OrderBy(x => x.Uid)
            .ToListAsync(ct);
        if (movements.Count == 0)
            return;

        await AssertMovementsAsync(context, movements, requireIssueHistory: false, ct);
        await AssertTouchedPoolsAsync(context, movements, ct);
    }

    public static void AssertSealed(StockPostingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Posting.SealedAtUtc is null)
            throw new ProductionPostingInvariantException("The V2 StockPosting was not sealed.");
    }

    private static async Task AssertMovementsAsync(
        StockPostingContext context,
        IReadOnlyList<ProductionBalLotMovement> movements,
        bool requireIssueHistory,
        CancellationToken ct)
    {
        foreach (var movement in movements)
        {
            if (movement.LedgerVersion != 2)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not ledger version 2.");
            if (movement.LedgerEpochId != context.Epoch.Id)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not on the current ledger epoch.");
            if (movement.StockPostingId != context.Posting.Id)
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not on the current StockPosting.");
            if (!string.Equals(movement.ValuationStatus, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Movement {movement.Uid} is not VERIFIED.");

            var evidence = await context.Db.ProductionValuationEvidenceRows
                .SingleOrDefaultAsync(x => x.MovementId == movement.Uid, ct)
                ?? throw new ProductionPostingInvariantException($"Movement {movement.Uid} has no valuation evidence.");
            if (evidence.ProductionBalLotId != movement.ProductionBalLotId)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is on a different pool.");
            if (evidence.Generation <= 0)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} has an invalid generation.");
            if (!string.Equals(evidence.Status, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is not VERIFIED.");
            if (string.IsNullOrWhiteSpace(evidence.Basis))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a basis.");
            if (!string.Equals(evidence.Currency, "COMPANY_BASE", StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is not in company base currency.");
            if (evidence.Price is null)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a price.");
            if (evidence.ConversionFactor is null or <= 0m)
                throw new ProductionPostingInvariantException($"Evidence for movement {movement.Uid} is missing a conversion factor.");

            if (requireIssueHistory || movement.MovementType == ProductionBalLotMovementTypes.Issue)
            {
                if (evidence.InventoryHistoryId is null)
                    throw new ProductionPostingInvariantException($"ISSUE evidence for movement {movement.Uid} is missing inventory history.");
                var history = await context.Db.IvTrxHistories
                    .SingleOrDefaultAsync(x => x.Id == evidence.InventoryHistoryId.Value, ct)
                    ?? throw new ProductionPostingInvariantException($"ISSUE evidence for movement {movement.Uid} points to missing inventory history.");
                if (history.StockPostingId is not null && history.StockPostingId != context.Posting.Id)
                    throw new ProductionPostingInvariantException($"ISSUE history for movement {movement.Uid} is not on the current StockPosting.");
            }
        }
    }

    private static async Task AssertTouchedPoolsAsync(
        StockPostingContext context,
        IReadOnlyList<ProductionBalLotMovement> movements,
        CancellationToken ct)
    {
        foreach (var lotId in movements.Select(x => x.ProductionBalLotId).Distinct().OrderBy(x => x))
        {
            var lot = await context.Db.ProductionBalLots.SingleAsync(x => x.Uid == lotId, ct);
            var pool = await context.Db.ProductionPoolValuationRows
                .SingleOrDefaultAsync(x => x.ProductionBalLotId == lotId, ct)
                ?? throw new ProductionPostingInvariantException($"Production pool {lotId} has no valuation projection.");
            if (pool.TrackedBaseQty < 0m || pool.TrackedValue < 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} has a negative tracked quantity or value.");
            if (pool.TrackedBaseQty != lot.BaseQty || pool.TrackedValue != lot.TotalCost)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");
            if (pool.TrackedBaseQty == 0m && pool.TrackedValue != 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");
            if (lot.BaseQty == 0m && lot.TotalCost != 0m)
                throw new ProductionPostingInvariantException($"Production pool {lotId} quantity/value reconciliation failed: tracked quantity/value does not match the production balance.");

            var nonempty = lot.BaseQty != 0m || lot.TotalCost != 0m;
            if (nonempty && !string.Equals(pool.Status, ProductionPoolValuationService.Verified, StringComparison.Ordinal))
                throw new ProductionPostingInvariantException($"Production pool {lotId} is not VERIFIED.");
        }
    }
}
