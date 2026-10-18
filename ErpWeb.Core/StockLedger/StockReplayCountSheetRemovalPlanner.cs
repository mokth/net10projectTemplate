using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.StockLedger;

public sealed record StockReplayCountSheetRemovalLine(
    int CountLineId,
    int BalLocId,
    decimal PhysicalQty,
    byte[] CountLineRowVersion,
    StockReplayPhysicalRemovalPlan Physical);

public sealed record StockReplayCountSheetRemovalPlan(
    string CompanyCode,
    string BranchCode,
    int CountHeaderId,
    DateTime EffectiveAt,
    long AnchorPostingId,
    long AnchorPostingSequence,
    int AnchorDocumentRevision,
    long LedgerEpochId,
    long BranchWatermark,
    byte[] HeaderRowVersion,
    IReadOnlyList<StockReplayCountSheetRemovalLine> Lines,
    string PlanHash);

/// <summary>
/// Read-only physical removal of an entire posted count. A financial and
/// generation writer must consume this plan before rollback can be routed.
/// </summary>
public static class StockReplayCountSheetRemovalPlanner
{
    public static async Task<StockReplayCountSheetRemovalPlan> BuildAsync(
        AppDbContext db, string companyCode, string branchCode,
        int countHeaderId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        var header = await db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == countHeaderId && h.CompanyCode == companyCode
                && h.BranchCode == branchCode)
            .Select(h => new { h.CountNo, h.CountDate, h.Status,
                h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (header is null || header.Status != "POSTED"
            || header.CountDate != header.CountDate.Date)
            throw Invalid($"Count {countHeaderId} is not a posted sheet.");
        var lines = await db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == countHeaderId
                && l.PhysicalQty != null)
            .OrderBy(l => l.BalLocId).ThenBy(l => l.Id)
            .ToArrayAsync(cancellationToken);
        if (lines.Length == 0 || lines.Any(l => l.BalLocId <= 0
                || l.PhysicalQty is null || l.PhysicalQty < 0m
                || IvQty.Round(l.PhysicalQty.Value) != l.PhysicalQty)
            || lines.Select(l => l.BalLocId).Distinct().Count() != lines.Length)
            throw Invalid($"Count {countHeaderId} has an incomplete physical sheet.");
        var pileIds = lines.Select(l => l.BalLocId).ToArray();
        var piles = await db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == companyCode
                && b.BranchCode == branchCode
                && pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        if (piles.Count != lines.Length
            || lines.Any(l => !piles.TryGetValue(l.BalLocId, out var pile)
                || !StockReplayCountSheetPreviewReader.Matches(l, pile)))
            throw Invalid($"Count {countHeaderId} no longer matches its stock piles.");

        var active = await StockCountGenerationResolver.ResolveActiveAsync(
            db, companyCode, branchCode, lines.Select(l => l.Id).ToArray(),
            cancellationToken);
        var first = active[lines[0].Id];
        var effectiveAt = StockReplayEventKey.CountEndOfDay(header.CountDate);
        if (first.IsRemoval || first.AnchorPostingId <= 0
            || first.AnchorPostingSequence <= 0
            || first.EffectiveAt != effectiveAt
            || active.Values.Any(g => g.IsRemoval
                || g.AnchorPostingId != first.AnchorPostingId
                || g.AnchorPostingSequence != first.AnchorPostingSequence
                || g.EffectiveAt != effectiveAt))
            throw Invalid($"Count {countHeaderId} has inconsistent active anchors.");
        var anchor = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == first.AnchorPostingId
                && p.CompanyCode == companyCode
                && p.BranchCode == branchCode,
                cancellationToken);
        if (anchor is null || anchor.SealedAtUtc is null
            || anchor.SourceModule != "INVENTORY"
            || anchor.SourceDocumentType != "IV_STOCK_COUNT"
            || anchor.SourceDocumentId != countHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || anchor.SourceDocumentNo != header.CountNo
            || anchor.PostingRole != "PRIMARY"
            || anchor.PostingSequence != first.AnchorPostingSequence
            || anchor.EffectiveAt != effectiveAt
            || anchor.DocumentRevision < 0)
            throw Invalid($"Count {countHeaderId} has no sealed original anchor.");

        var plans = new List<StockReplayCountSheetRemovalLine>(lines.Length);
        foreach (var line in lines)
        {
            var generation = active[line.Id];
            var target = new StockReplayPhysicalEvent(-generation.Id,
                new StockReplayEventKey(effectiveAt,
                    StockReplayEventKey.CountClass,
                    generation.AnchorPostingSequence, $"C{line.Id:D10}", 0),
                generation.SignedAdjustmentQty, line.PhysicalQty);
            var physical = await StockReplayPhysicalPlanner.BuildRemovalAsync(
                db, companyCode, branchCode, line.BalLocId, target,
                cancellationToken);
            plans.Add(new(line.Id, line.BalLocId, line.PhysicalQty!.Value,
                line.RowVersion.ToArray(), physical));
        }
        var physicalFirst = plans[0].Physical;
        if (plans.Any(p => p.Physical.LedgerEpochId
                    != physicalFirst.LedgerEpochId
                || p.Physical.BranchWatermark
                    != physicalFirst.BranchWatermark))
            throw Stale($"Count {countHeaderId} changed during removal planning.");
        var plan = new StockReplayCountSheetRemovalPlan(companyCode, branchCode,
            countHeaderId, effectiveAt, first.AnchorPostingId,
            first.AnchorPostingSequence, anchor.DocumentRevision,
            physicalFirst.LedgerEpochId,
            physicalFirst.BranchWatermark, header.RowVersion.ToArray(),
            plans.ToArray(), string.Empty);
        plan = plan with { PlanHash = Hash(plan) };
        ValidatePreparedPlan(plan);
        return plan;
    }

    public static void ValidatePreparedPlan(
        StockReplayCountSheetRemovalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.CountHeaderId <= 0 || plan.AnchorPostingId <= 0
            || plan.AnchorPostingSequence <= 0
            || plan.AnchorDocumentRevision < 0
            || plan.EffectiveAt.Date.AddDays(1).AddTicks(-1)
                != plan.EffectiveAt
            || plan.Lines.Count == 0
            || plan.Lines.Select(l => l.CountLineId).Distinct().Count()
                != plan.Lines.Count
            || plan.Lines.Select(l => l.BalLocId).Distinct().Count()
                != plan.Lines.Count
            || plan.Lines.Any(l => l.CountLineId <= 0 || l.BalLocId <= 0
                || l.PhysicalQty < 0m
                || IvQty.Round(l.PhysicalQty) != l.PhysicalQty
                || l.Physical.CompanyCode != plan.CompanyCode
                || l.Physical.BranchCode != plan.BranchCode
                || l.Physical.BalLocId != l.BalLocId
                || l.Physical.LedgerEpochId != plan.LedgerEpochId
                || l.Physical.BranchWatermark != plan.BranchWatermark
                || l.Physical.RemovedActiveEvent.EventId >= 0
                || l.Physical.RemovedActiveEvent.Key
                    != new StockReplayEventKey(plan.EffectiveAt,
                        StockReplayEventKey.CountClass,
                        plan.AnchorPostingSequence,
                        $"C{l.CountLineId:D10}", 0)
                || l.Physical.RemovedActiveEvent.AbsoluteCountQty
                    != l.PhysicalQty)
            || !StringComparer.Ordinal.Equals(plan.PlanHash, Hash(plan)))
            throw Stale("The prepared count removal sheet changed.");
    }

    public static async Task ValidateWithinRootAsync(
        StockPostingContext context,
        StockReplayCountSheetRemovalPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidatePreparedPlan(plan);
        context.EnsureUnsealed();
        if (context.CompanyCode != plan.CompanyCode
            || context.BranchCode != plan.BranchCode
            || context.Epoch.Id != plan.LedgerEpochId
            || context.Posting.SourceModule != "INVENTORY"
            || context.Posting.SourceDocumentType != "IV_STOCK_COUNT"
            || context.Posting.SourceDocumentId != plan.CountHeaderId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
            || context.Posting.PostingRole != "REVERSAL"
            || context.Posting.ReversesPostingId != plan.AnchorPostingId
            || context.Posting.DocumentRevision
                != plan.AnchorDocumentRevision
            || context.Posting.EffectiveAt != plan.EffectiveAt
            || context.Posting.PostingSequence != plan.BranchWatermark + 1)
            throw Stale("The count removal plan belongs to another root.");

        var header = await context.Db.IvStockCountHdrs.AsNoTracking()
            .Where(h => h.Id == plan.CountHeaderId
                && h.CompanyCode == plan.CompanyCode
                && h.BranchCode == plan.BranchCode)
            .Select(h => new { h.CountNo, h.Status, h.CountDate,
                h.RowVersion })
            .SingleOrDefaultAsync(cancellationToken);
        var lines = await context.Db.IvStockCountLines.AsNoTracking()
            .Where(l => l.StockCountId == plan.CountHeaderId
                && l.PhysicalQty != null)
            .ToArrayAsync(cancellationToken);
        var pileIds = plan.Lines.Select(l => l.BalLocId).ToArray();
        var piles = await context.Db.IvBalLocs.AsNoTracking()
            .Where(b => b.CompanyCode == plan.CompanyCode
                && b.BranchCode == plan.BranchCode
                && pileIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, cancellationToken);
        var planned = plan.Lines.ToDictionary(l => l.CountLineId);
        if (header is null || header.Status != "POSTED"
            || header.CountDate != header.CountDate.Date
            || header.CountNo != context.Posting.SourceDocumentNo
            || StockReplayEventKey.CountEndOfDay(header.CountDate)
                != plan.EffectiveAt
            || !header.RowVersion.SequenceEqual(plan.HeaderRowVersion)
            || lines.Length != plan.Lines.Count
            || lines.Any(l => !planned.TryGetValue(l.Id, out var p)
                || l.BalLocId != p.BalLocId
                || l.PhysicalQty != p.PhysicalQty
                || !l.RowVersion.SequenceEqual(p.CountLineRowVersion)
                || !piles.TryGetValue(l.BalLocId, out var pile)
                || !StockReplayCountSheetPreviewReader.Matches(l, pile)))
            throw Stale($"Count {plan.CountHeaderId} changed after removal planning.");
        foreach (var line in plan.Lines)
            await StockReplayPhysicalPlanner.ValidateWithinRootAsync(context,
                line.Physical, cancellationToken);
    }

    private static string Hash(StockReplayCountSheetRemovalPlan plan) =>
        StockPostingFingerprint.Create(new
        {
            plan.CompanyCode, plan.BranchCode, plan.CountHeaderId,
            plan.EffectiveAt, plan.AnchorPostingId,
            plan.AnchorPostingSequence, plan.AnchorDocumentRevision,
            plan.LedgerEpochId,
            plan.BranchWatermark, plan.HeaderRowVersion,
            lines = plan.Lines.Select(l => new
            {
                l.CountLineId, l.BalLocId, l.PhysicalQty,
                l.CountLineRowVersion, l.Physical.PlanHash
            }).ToArray()
        }, new { }).RequestFingerprint;

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));

    private static StockLedgerException Stale(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.StaleDocument,
            message, Retryable: true));
}
