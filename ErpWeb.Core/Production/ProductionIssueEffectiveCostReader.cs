using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionIssueCostAuthority(
    long MovementId,
    long MaterialMovementId,
    int InventoryHistoryId,
    decimal BaseQty,
    decimal TotalCost,
    decimal UnitCost,
    IReadOnlyList<long> ActiveValuationFactIds);

/// <summary>
/// Resolves the Inventory value that a posted Production ISSUE must inherit.
/// An active FIFO issue can own several splits; the complete active generation
/// is the authority after an Inventory replay correction.
/// </summary>
internal static class ProductionIssueEffectiveCostReader
{
    public static async Task<IReadOnlyDictionary<long, ProductionIssueCostAuthority>> ReadAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyCollection<long> movementIds,
        CancellationToken cancellationToken = default)
        => await ReadCoreAsync(db, companyCode, branchCode, movementIds,
            null, cancellationToken);

    /// <summary>Reads saved Inventory corrections before their replay root is sealed.</summary>
    internal static async Task<IReadOnlyDictionary<long, ProductionIssueCostAuthority>>
        ReadWithinRootAsync(StockPostingContext context,
            IReadOnlyCollection<long> movementIds,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        if (context.Posting.Id <= 0 || context.Posting.RequestId == Guid.Empty
            || context.Posting.LedgerEpochId != context.Epoch.Id
            || context.Posting.PostingSequence <= 0)
            throw Invalid("Production issue cost authority requires a valid replay root.");
        return await ReadCoreAsync(context.Db, context.CompanyCode,
            context.BranchCode, movementIds, context.Posting,
            cancellationToken);
    }

    private static async Task<IReadOnlyDictionary<long, ProductionIssueCostAuthority>>
        ReadCoreAsync(AppDbContext db, string companyCode, string branchCode,
            IReadOnlyCollection<long> movementIds, StockPosting? unsealedRoot,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(movementIds);
        if (string.IsNullOrWhiteSpace(companyCode) || string.IsNullOrWhiteSpace(branchCode)
            || movementIds.Any(id => id <= 0))
            throw Invalid("Production issue cost authority requires a tenant and positive movement IDs.");
        if (movementIds.Count == 0)
            return new Dictionary<long, ProductionIssueCostAuthority>();
        var company = companyCode.Trim();
        var branch = branchCode.Trim();
        var ids = movementIds.Distinct().ToArray();
        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(m => ids.Contains(m.Uid))
            .ToDictionaryAsync(m => m.Uid, cancellationToken);
        if (movements.Count != ids.Length || movements.Values.Any(m =>
                m.CompanyCode != company || m.BranchCode != branch
                || m.LedgerVersion != 2 || m.LedgerEpochId is null
                || m.StockPostingId is null || m.OriginalMovementId is not null
                || m.MovementType != ProductionBalLotMovementTypes.Issue
                || m.InventoryHistoryId is null || m.BaseQty <= 0m
                || m.ValuationStatus != ProductionPoolValuationService.Verified))
            throw Invalid("Production issue has no verified V2 movement identity.");
        var historyIds = movements.Values.Select(m => m.InventoryHistoryId!.Value).Distinct().ToArray();
        if (historyIds.Length != movements.Count)
            throw Invalid("Production issues share one Inventory history.");
        var materials = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(m => m.ProductionBalLotMovementId.HasValue
                && ids.Contains(m.ProductionBalLotMovementId.Value))
            .ToArrayAsync(cancellationToken);
        if (materials.Length != movements.Count
            || materials.GroupBy(m => m.ProductionBalLotMovementId).Any(g => g.Count() != 1))
            throw Invalid("Production issue has no unique material movement identity.");
        var materialByMovement = materials.ToDictionary(
            m => m.ProductionBalLotMovementId!.Value);
        var histories = await db.IvTrxHistories.AsNoTracking()
            .Where(h => historyIds.Contains(h.Id))
            .ToDictionaryAsync(h => h.Id, cancellationToken);
        if (histories.Count != historyIds.Length
            || await db.IvTrxHistories.AsNoTracking()
                .AnyAsync(h => h.ReversesHistoryId != null
                    && historyIds.Contains(h.ReversesHistoryId.Value), cancellationToken))
            throw Invalid("Production issue Inventory history is missing or reversed.");
        var generations = await StockReplayValuationGenerationResolver.ResolveAsync(
            db, company, branch, histories.Values.ToArray(), cancellationToken);
        var postingIds = movements.Values.Select(m => m.StockPostingId!.Value)
            .Concat(generations.Values.SelectMany(facts => facts.Select(f => f.StockPostingId)))
            .Concat(unsealedRoot is null ? Array.Empty<long>() : [unsealedRoot.Id])
            .Distinct().ToArray();
        var postings = await db.StockPostings.AsNoTracking()
            .Where(p => postingIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        if (postings.Count != postingIds.Length || postings.Values.Any(p =>
                p.CompanyCode != company || p.BranchCode != branch
                || p.SealedAtUtc is null && p.Id != unsealedRoot?.Id
                || p.PostingSequence <= 0)
            || unsealedRoot is not null
                && (postings[unsealedRoot.Id].RequestId != unsealedRoot.RequestId
                    || postings[unsealedRoot.Id].SealedAtUtc is not null
                    || postings[unsealedRoot.Id].LedgerEpochId != unsealedRoot.LedgerEpochId)
            || generations.Values.SelectMany(facts => facts).Any(f =>
                f.LedgerEpochId != postings[f.StockPostingId].LedgerEpochId)
            || movements.Values.Any(m =>
                postings[m.StockPostingId!.Value].LedgerEpochId != m.LedgerEpochId
                || postings[m.StockPostingId.Value].PostingRole != "PRIMARY"
                || postings[m.StockPostingId.Value].EffectiveAt != m.MovementDate
                || generations[m.InventoryHistoryId!.Value].Any(f =>
                    postings[f.StockPostingId].PostingSequence
                        < postings[m.StockPostingId.Value].PostingSequence))
            || await db.StockPostings.AsNoTracking().AnyAsync(p =>
                p.ReversesPostingId.HasValue
                && postingIds.Contains(p.ReversesPostingId.Value)
                && p.SealedAtUtc != null, cancellationToken))
            throw Invalid("Production issue valuation has no active sealed posting owner.");
        return movements.Values.ToDictionary(m => m.Uid, m => FromActiveGeneration(
            m, materialByMovement[m.Uid], histories[m.InventoryHistoryId!.Value],
            generations[m.InventoryHistoryId.Value]));
    }

    internal static ProductionIssueCostAuthority FromActiveGeneration(
        ProductionBalLotMovement movement,
        ProductionMaterialMovement material,
        IvTrxHistory history,
        IReadOnlyList<StockValuationFact> activeFacts)
    {
        ArgumentNullException.ThrowIfNull(movement);
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(activeFacts);
        if (movement.Uid <= 0 || movement.MovementType != ProductionBalLotMovementTypes.Issue
            || movement.LedgerVersion != 2 || movement.LedgerEpochId is null
            || movement.StockPostingId is null || movement.OriginalMovementId is not null
            || movement.ValuationStatus != ProductionPoolValuationService.Verified
            || movement.InventoryHistoryId != history.Id
            || material.Uid <= 0
            || material.ProductionBalLotMovementId != movement.Uid
            || material.MovementType != ProductionMaterialMovementTypes.Issue
            || material.OriginalMovementId is not null
            || material.CompanyCode != movement.CompanyCode
            || material.BranchCode != movement.BranchCode
            || material.ItemCode != movement.ItemCode
            || material.WorkOrderId != movement.WorkOrderId
            || material.WorkOrderOperationId != movement.WorkOrderOperationId
            || material.PostingLinkId != movement.PostingLinkId
            || material.InventoryHistoryId != history.Id
            || material.StockPostingId != movement.StockPostingId
            || material.MovementDate != movement.MovementDate
            || StockLedgerPrecision.Quantity(material.BaseQty)
                != StockLedgerPrecision.Quantity(movement.BaseQty)
            || movement.CompanyCode != history.CompanyCode
            || movement.BranchCode != history.BranchCode
            || movement.LedgerEpochId != history.LedgerEpochId
            || movement.StockPostingId != history.StockPostingId
            || movement.ItemCode != history.ICode
            || movement.MovementDate != history.TrxDtTime
            || history.TrxType != IvTrxTypes.IssueToProduction
            || movement.BaseQty <= 0m
            || StockLedgerPrecision.Quantity(history.FrStdQty ?? 0m)
                != StockLedgerPrecision.Quantity(movement.BaseQty)
            || StockLedgerPrecision.Quantity(history.ToStdQty ?? 0m) != 0m
            || history.LedgerVersion != 2 || history.EntryRole != "ORIGINAL"
            || activeFacts.Count == 0 || activeFacts.Any(f =>
                f.InventoryHistoryId != history.Id
                || f.CompanyCode != movement.CompanyCode
                || f.BranchCode != movement.BranchCode
                || f.LedgerEpochId != movement.LedgerEpochId
                || f.ItemCode != movement.ItemCode
                || f.EffectiveAt != movement.MovementDate
                || f.BaseUom != movement.BaseUom
                || f.Direction != -1
                || f.MovementCode != "PRODUCTION_MATERIAL_OUT"
                || f.ValuationStatus != StockValuationStatuses.Valued
                || f.BaseQty <= 0m || f.CostAmount < 0m
                || f.ProductionMovementId != material.Uid
                || f.WorkOrderId != material.WorkOrderId
                || f.WorkOrderOperationId != material.WorkOrderOperationId
                || f.ProductionPostingLinkId != material.PostingLinkId)
            || StockLedgerPrecision.Quantity(activeFacts.Sum(f => f.BaseQty))
                != StockLedgerPrecision.Quantity(movement.BaseQty))
            throw Invalid($"Production ISSUE movement {movement.Uid} does not match its active Inventory valuation generation.");
        var total = StockLedgerPrecision.Money(activeFacts.Sum(f => f.CostAmount));
        return new ProductionIssueCostAuthority(movement.Uid, material.Uid, history.Id,
            StockLedgerPrecision.Quantity(movement.BaseQty), total,
            StockLedgerPrecision.Money(total / movement.BaseQty),
            activeFacts.Select(f => f.Id).Order().ToArray());
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
