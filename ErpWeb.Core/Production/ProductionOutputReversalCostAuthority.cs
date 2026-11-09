using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>Checks the cost that an output reversal captured at its posting sequence.</summary>
internal static class ProductionOutputReversalCostAuthority
{
    public static async Task ValidateBaselineAsync(AppDbContext db, string company, string branch,
        IReadOnlyCollection<ProductionBalLotMovement> originals,
        IReadOnlyCollection<ProductionBalLotMovement> reversals, CancellationToken ct)
    {
        var ids = originals.Select(m => m.Uid).ToArray();
        if (ids.Length == 0 || ids.Distinct().Count() != ids.Length
            || reversals.Count != ids.Length || originals.Any(m =>
                m.OriginalMovementId is not null || m.StockPostingId is not > 0
                || m.CompanyCode != company || m.BranchCode != branch)
            || reversals.Any(r => r.OriginalMovementId is not long id || !ids.Contains(id)
                || r.StockPostingId is not > 0 || r.CompanyCode != company || r.BranchCode != branch)
            || reversals.GroupBy(r => r.OriginalMovementId).Any(g => g.Count() != 1))
            throw Invalid("Production output reversal has an incomplete cost pair.");

        // This checks the ownership and sealing of every original correction.
        // Originals have no parent, so this cannot recurse into this authority.
        await ProductionReplayEffectiveCostReader.ReadAsync(db, company, branch, ids, ct);
        var facts = await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(f => ids.Contains(f.ProductionBalLotMovementId)).ToArrayAsync(ct);
        var rootIds = facts.Select(f => f.ReplayStockPostingId).Distinct().ToArray();
        var ownerIds = reversals.Select(r => r.StockPostingId!.Value).Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => rootIds.Contains(p.Id) || ownerIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, ct);
        if (rootIds.Any(id => !postings.ContainsKey(id)) || ownerIds.Any(id => !postings.ContainsKey(id)))
            throw Invalid("Production output reversal cost ownership is missing.");
        foreach (var original in originals)
        {
            var reversal = reversals.Single(r => r.OriginalMovementId == original.Uid);
            var owner = postings[reversal.StockPostingId!.Value];
            if (owner.CompanyCode != company || owner.BranchCode != branch || owner.SealedAtUtc is null
                || owner.PostingRole != "REVERSAL" || owner.SourceModule != "PRODUCTION"
                || owner.SourceDocumentType != ProductionDocumentTypes.ProductionOutput
                || owner.CommandType != "PRODUCTION_OUTPUT_ROLLBACK"
                || owner.ReversesPostingId != original.StockPostingId
                || owner.LedgerEpochId != original.LedgerEpochId
                || reversal.LedgerEpochId != original.LedgerEpochId)
                throw Invalid("Production output reversal has no sealed cost owner.");
            var before = facts.Where(f => f.ProductionBalLotMovementId == original.Uid
                && postings[f.ReplayStockPostingId].PostingSequence < owner.PostingSequence).ToArray();
            var atReversal = ProductionEffectiveCostResolver.Resolve(
                [new ProductionEffectiveCost(original.Uid, original.TotalCost, original.UnitCost, null)],
                before)[original.Uid];
            if (reversal.TotalCost != atReversal.TotalCost || reversal.UnitCost != atReversal.UnitCost)
                throw Invalid("Production output reversal did not capture its original effective cost.");
        }
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
