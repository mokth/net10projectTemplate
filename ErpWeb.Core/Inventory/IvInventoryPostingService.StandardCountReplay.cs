using ErpWeb.Core.Numbering;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed partial class IvInventoryPostingService
{
    private static Task<bool> HasLaterStandardCountAsync(
        AppDbContext db, string companyCode, string branchCode,
        string itemCode, DateTime effectiveAt,
        CancellationToken cancellationToken) =>
        db.StockCountReplayGenerations.AsNoTracking().AnyAsync(g =>
            g.CompanyCode == companyCode
            && g.BranchCode == branchCode
            && g.EffectiveAt > effectiveAt
            && !g.IsRemoval
            && db.IvStockCountLines.Any(l =>
                l.Id == g.StockCountLineId
                && l.ICode == itemCode
                && l.Header.CompanyCode == companyCode
                && l.Header.BranchCode == branchCode
                && l.Header.Status == IvStockCountStatuses.Posted),
            cancellationToken);

    private static async Task<CountReplayProof?> PlanSingleStandardCountReplayAsync(
        AppDbContext db, string company, string branch, string item, int pileId,
        decimal quantity, DateTime movementAt,
        IReadOnlyList<StockReplayPhysicalEvent> physical,
        IReadOnlyList<StockReplayStandardEvent> financial,
        IReadOnlyList<StockReplayStandardStep> steps,
        decimal currentQty, decimal currentValue,
        decimal finalQty, decimal finalValue,
        decimal candidateSignedValue, long? removingOwnerId, bool receipt,
        CancellationToken ct)
    {
        if (quantity <= 0m || physical.Count != 1 || financial.Count != 1 || steps.Count != 1
            || currentQty != finalQty)
            return null;
        var movement = physical[0];
        var value = financial[0];
        var step = steps[0];
        var removing = removingOwnerId.HasValue;
        var signedQuantity = receipt ? quantity : -quantity;
        var expectedAdjustment = IvQty.Round(movement.SignedDelta
            + (removing ? signedQuantity : -signedQuantity));
        var expectedFinalValue = StockLedgerPrecision.Money(
            currentValue + (removing ? -candidateSignedValue : candidateSignedValue)
            + (step.SignedValue - value.OldSignedValue));
        if (movement.EventId >= 0 || movement.EventId == long.MinValue
            || movement.AbsoluteCountQty is not >= 0m
            || movement.Key.ClassPriority != StockReplayEventKey.CountClass
            || movement.Key.EffectiveAt.Year != movementAt.Year
            || movement.Key.EffectiveAt.Month != movementAt.Month
            || value.Kind != StockReplayStandardEventKinds.Count
            || value.EventId != movement.EventId || value.Key != movement.Key
            || value.OldSignedQuantity != movement.SignedDelta
            || step.EventId != value.EventId
            || step.SignedQuantity != expectedAdjustment
            || finalValue != expectedFinalValue)
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
            if (generation.WritePostingId != removingOwnerId || previous is null || previous.IsRemoval
                || previous.StockCountLineId != line.Id || previous.AnchorPostingId != generation.AnchorPostingId
                || previous.SignedAdjustmentQty != expectedAdjustment)
                return null;
        }
        var fingerprint = StockPostingFingerprint.Create(new { Kind = receipt ? "STD_MR_SINGLE_COUNT" : "STD_MI_SINGLE_COUNT" }, new
        {
            line.Id, line.StockCountId, line.RowVersion, line.BalLocId, line.ICode, line.PhysicalQty,
            HeaderVersion = line.Header.RowVersion, line.Header.Status, line.Header.CountDate,
            GenerationId = generation.Id, generation.PreviousGenerationId, generation.AnchorPostingId,
            generation.AnchorPostingSequence, generation.WritePostingId, generation.SignedAdjustmentQty,
            quantity, movementAt, removingOwnerId, value.StandardCostRevisionId, value.UnitCost
        }).SourceSnapshotHash;
        return new(line.StockCountId, line.Id, generation.Id, fingerprint);
    }

    private static async Task<IReadOnlyList<int>> ApplySingleStandardCountInsertionAsync(
        StockPostingContext root,
        StockReplayStandardWritePermit permit,
        StockReplayPhysicalPlan physical,
        StockReplayStandardFinancialTail tail,
        StockReplayStandardResult projection,
        string planHash,
        StockCountCorrectionWritePermit countPermit,
        CancellationToken cancellationToken)
    {
        if (countPermit.ReversedCandidateHistoryId is not null
            || countPermit.Lines.Length != 1
            || tail.LaterEvents.Count != 1
            || tail.LaterEvents[0].EventId != -countPermit.Lines[0].PreviousGenerationId)
            throw InvalidStandardTransfer("The Standard movement has no unique prepared count correction.");
        var corrected = await StockCountCorrectionHistoryWriter.AppendAsync(
            root, countPermit,
            [new StockCountCorrectionPostingLines(countPermit.Lines[0].LineId, 2, 3)],
            new RunningNumberService(), cancellationToken);
        await StockReplayStandardCountCorrectionValuationWriter.AppendMovementInsertionAsync(
            root, permit, physical, tail, projection, planHash, countPermit,
            countPermit.Lines[0].LineId, 2, 3, cancellationToken);
        return corrected.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).Distinct().ToArray();
    }

    private static async Task<IReadOnlyList<int>> ApplySingleStandardCountRemovalAsync(
        StockPostingContext root,
        StockReplayStandardWritePermit permit,
        StockReplayPhysicalRemovalPlan physical,
        StockReplayStandardFinancialTail tail,
        StockReplayStandardRemovalResult projection,
        string planHash,
        StockCountCorrectionWritePermit countPermit,
        CancellationToken cancellationToken)
    {
        if (countPermit.ReversedCandidateHistoryId != physical.RemovedActiveEvent.EventId
            || countPermit.Lines.Length != 1
            || tail.LaterEvents.Count != 1
            || tail.LaterEvents[0].EventId != -countPermit.Lines[0].PreviousGenerationId)
            throw InvalidStandardTransfer("The Standard rollback has no unique prepared count correction.");
        var corrected = await StockCountCorrectionHistoryWriter.AppendAsync(
            root, countPermit,
            [new StockCountCorrectionPostingLines(countPermit.Lines[0].LineId, 2, 3)],
            new RunningNumberService(), cancellationToken);
        await StockReplayStandardCountCorrectionValuationWriter.AppendMovementRemovalAsync(
            root, permit, physical, tail, projection, planHash, countPermit,
            countPermit.Lines[0].LineId, 2, 3, cancellationToken);
        return corrected.Where(g => g.AdjustmentHistoryId.HasValue)
            .Select(g => g.AdjustmentHistoryId!.Value).Distinct().ToArray();
    }
}
