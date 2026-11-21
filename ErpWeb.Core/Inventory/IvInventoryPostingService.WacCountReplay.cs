using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private sealed record IssueCountProof(int HeaderId, int LineId, long GenerationId, string Fingerprint);

    // A single issue may alter one later count without changing the final
    // pool quantity or value. Removal must undo this issue's exact correction.
    private static async Task<IssueCountProof?> PlanSingleIssueCountAsync(
        AppDbContext db, string company, string branch, string item, int pileId,
        decimal issueQty, DateTime issueAt, IReadOnlyList<StockReplayPhysicalEvent> physical,
        IReadOnlyList<StockReplayWacEvent> financial, IReadOnlyList<StockReplayWacStep> steps,
        decimal currentQty, decimal currentValue, decimal finalQty, decimal finalValue,
        long? removingIssueOwnerId, CancellationToken ct)
    {
        if (issueQty <= 0m || physical.Count != 1 || financial.Count != 1 || steps.Count != 1
            || currentQty != finalQty || currentValue != finalValue)
            return null;
        var movement = physical[0];
        var value = financial[0];
        var step = steps[0];
        var removing = removingIssueOwnerId.HasValue;
        var expectedAdjustment = IvQty.Round(movement.SignedDelta
            + (removing ? -issueQty : issueQty));
        if (movement.EventId >= 0 || movement.EventId == long.MinValue
            || movement.AbsoluteCountQty is not > 0m
            || movement.Key.ClassPriority != StockReplayEventKey.CountClass
            || movement.Key.EffectiveAt.Year != issueAt.Year || movement.Key.EffectiveAt.Month != issueAt.Month
            || value.EventId != movement.EventId || value.Key != movement.Key
            || value.OldSignedQuantity != movement.SignedDelta || step.EventId != value.EventId
            || step.SignedQuantity != expectedAdjustment)
            return null;
        var generation = await db.StockCountReplayGenerations.AsNoTracking().SingleOrDefaultAsync(g =>
            g.Id == -movement.EventId && g.CompanyCode == company && g.BranchCode == branch, ct);
        if (generation is null || generation.IsRemoval || generation.SignedAdjustmentQty != movement.SignedDelta)
            return null;
        var active = await StockCountGenerationResolver.ResolveActiveAsync(db, company, branch,
            [generation.StockCountLineId], ct);
        if (!active.TryGetValue(generation.StockCountLineId, out var leaf) || leaf.Id != generation.Id)
            return null;
        var line = await db.IvStockCountLines.AsNoTracking().Include(l => l.Header)
            .SingleOrDefaultAsync(l => l.Id == generation.StockCountLineId, ct);
        if (line is null || line.Header.CompanyCode != company || line.Header.BranchCode != branch
            || line.Header.Status != IvStockCountStatuses.Posted || line.BalLocId != pileId
            || line.ICode != item || line.PhysicalQty != movement.AbsoluteCountQty
            || StockReplayEventKey.CountEndOfDay(line.Header.CountDate) != movement.Key.EffectiveAt)
            return null;
        if (removing)
        {
            var previous = generation.PreviousGenerationId is long previousId
                ? await db.StockCountReplayGenerations.AsNoTracking().SingleOrDefaultAsync(g => g.Id == previousId, ct)
                : null;
            if (generation.WritePostingId != removingIssueOwnerId || previous is null || previous.IsRemoval
                || previous.StockCountLineId != line.Id || previous.AnchorPostingId != generation.AnchorPostingId
                || previous.SignedAdjustmentQty != expectedAdjustment)
                return null;
        }
        var fingerprint = StockPostingFingerprint.Create(new { Kind = "MI_SINGLE_COUNT" }, new
        {
            line.Id, line.StockCountId, line.RowVersion, line.BalLocId, line.ICode, line.PhysicalQty,
            HeaderVersion = line.Header.RowVersion, line.Header.Status, line.Header.CountDate,
            GenerationId = generation.Id, generation.PreviousGenerationId, generation.AnchorPostingId,
            generation.AnchorPostingSequence, generation.WritePostingId, generation.SignedAdjustmentQty,
            issueQty, issueAt, removingIssueOwnerId
        }).SourceSnapshotHash;
        return new(line.StockCountId, line.Id, generation.Id, fingerprint);
    }

    private static async Task<bool> LockIssueCountSourceAsync(
        AppDbContext db, string company, string branch, IssueCountProof proof, CancellationToken ct)
    {
        if (!db.Database.IsSqlServer()) return true;
        if (db.Database.CurrentTransaction is null) return false;
        try
        {
            var headers = await db.IvStockCountHdrs.FromSqlInterpolated($@"SELECT * FROM dbo.IvStockCountHdr WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE ID = {proof.HeaderId} AND CompanyCode = {company} AND BranchCode = {branch}")
                .AsNoTracking().ToArrayAsync(ct);
            var lines = await db.IvStockCountLines.FromSqlInterpolated($@"SELECT * FROM dbo.IvStockCountLine WITH (UPDLOCK, HOLDLOCK, NOWAIT)
                WHERE ID = {proof.LineId} AND StockCountId = {proof.HeaderId}")
                .AsNoTracking().ToArrayAsync(ct);
            return headers.Length == 1 && lines.Length == 1;
        }
        catch (SqlException ex) when (ex.Number is 1205 or 1222)
        {
            return false;
        }
    }
}
