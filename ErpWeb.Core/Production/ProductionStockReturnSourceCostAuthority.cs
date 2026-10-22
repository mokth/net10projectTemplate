using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

internal sealed record ProductionStockReturnSourceCostProjection(
    long SourceIssueMovementId,
    long ReturnMovementId,
    long ReturnFactId,
    int ReturnHistoryId,
    decimal CorrectedSourceValue,
    decimal CorrectedReturnValue,
    decimal ProjectedRemainingValue);

/// <summary>
/// Resolves the corrected value of one return from an already corrected ISSUE.
/// The caller must propagate the result through Production and Inventory before
/// sealing the replay root. Other movements in the source pool are excluded.
/// </summary>
internal static class ProductionStockReturnSourceCostAuthority
{
    public static async Task<ProductionStockReturnSourceCostProjection> ReadWithinRootAsync(
        StockPostingContext context, int returnBatchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EnsureUnsealed();
        var db = context.Db;
        if (db.Database.CurrentTransaction is null || returnBatchId <= 0)
            throw Invalid("Return source cost authority requires a replay transaction and return ID.");

        var document = await db.ProductionStockReturns.AsNoTracking()
            .Include(x => x.Batch).Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.BatchId == returnBatchId
                && x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode,
                cancellationToken);
        if (document is null || document.Batch.BatchStatus != IvBatchStatuses.Posted
            || document.ReversalPostingId is not null
            || document.PostingId is not long returnPostingId)
            throw Invalid("Return source cost authority needs an active posted return.");
        var lines = document.Lines.Where(x => x.DocumentRevision == document.DocumentRevision).ToArray();
        var facts = await db.ProductionStockReturnFacts.AsNoTracking()
            .Where(x => x.BatchId == returnBatchId && x.StockPostingId == returnPostingId
                && x.ReversesFactId == null).ToArrayAsync(cancellationToken);
        if (lines.Length != 1 || facts.Length != 1 || facts[0].ReturnLineId != lines[0].Id)
            throw Invalid("Return source cost authority supports one active return line.");
        var line = lines[0];
        var fact = facts[0];
        var issueMaterial = await db.ProductionMaterialMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == fact.SourceIssueMovementId,
                cancellationToken);
        var returnMaterial = await db.ProductionMaterialMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == fact.ProductionMaterialMovementId,
                cancellationToken);
        var issue = issueMaterial?.ProductionBalLotMovementId is long issueMovementId
            ? await db.ProductionBalLotMovements.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Uid == issueMovementId, cancellationToken) : null;
        var returned = await db.ProductionBalLotMovements.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == fact.ProductionMovementId,
                cancellationToken);
        var pool = await db.ProductionBalLots.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == line.ProductionBalLotId,
                cancellationToken);
        var valuation = await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductionBalLotId == line.ProductionBalLotId,
                cancellationToken);
        var issueOwner = issue?.StockPostingId is long issuePostingId
            ? await db.StockPostings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == issuePostingId, cancellationToken) : null;
        var returnOwner = await db.StockPostings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == returnPostingId, cancellationToken);
        var returnHistory = await db.IvTrxHistories.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == fact.InventoryHistoryId,
                cancellationToken);
        if (issueMaterial is null || returnMaterial is null || issue is null
            || returned is null || pool is null || valuation is null
            || issueOwner is null || returnOwner is null || returnHistory is null
            || issueMaterial.MovementType != ProductionMaterialMovementTypes.Issue
            || issueMaterial.OriginalMovementId is not null
            || issueMaterial.ReversesMaterialMovementId is not null
            || issueMaterial.CompanyCode != context.CompanyCode
            || issueMaterial.BranchCode != context.BranchCode
            || issueMaterial.WorkOrderId != document.WorkOrderId
            || issueMaterial.ItemCode != issue.ItemCode
            || issueMaterial.BaseQty != issue.BaseQty
            || issueMaterial.BaseUom != issue.BaseUom
            || issueMaterial.MovementDate != issue.MovementDate
            || issueMaterial.InventoryHistoryId != issue.InventoryHistoryId
            || issueMaterial.ProductionBalLotId != pool.Uid
            || issueMaterial.StockPostingId != issueOwner.Id
            || issueMaterial.TotalCost != issue.TotalCost
            || returnMaterial.MovementType != ProductionMaterialMovementTypes.Return
            || returnMaterial.ReversesMaterialMovementId is not null
            || returnMaterial.CompanyCode != context.CompanyCode
            || returnMaterial.BranchCode != context.BranchCode
            || returnMaterial.WorkOrderId != document.WorkOrderId
            || returnMaterial.ItemCode != returned.ItemCode
            || returnMaterial.BaseQty != returned.BaseQty
            || returnMaterial.BaseUom != returned.BaseUom
            || returnMaterial.MovementDate != returned.MovementDate
            || returnMaterial.InventoryHistoryId != fact.InventoryHistoryId
            || returnMaterial.ProductionBalLotId != pool.Uid
            || returnMaterial.OriginalMovementId != issueMaterial.Uid
            || returnMaterial.SourceIssueMovementId != issueMaterial.Uid
            || returnMaterial.ProductionBalLotMovementId != returned.Uid
            || returnMaterial.StockPostingId != returnOwner.Id
            || returnMaterial.TotalCost != fact.TotalValue
            || issue.MovementType != ProductionBalLotMovementTypes.Issue
            || returned.MovementType != ProductionBalLotMovementTypes.Return
            || issue.OriginalMovementId is not null || returned.OriginalMovementId is not null
            || issue.ProductionBalLotId != pool.Uid || returned.ProductionBalLotId != pool.Uid
            || issue.WorkOrderId != document.WorkOrderId
            || returned.WorkOrderId != document.WorkOrderId
            || issue.LedgerVersion != 2 || returned.LedgerVersion != 2
            || issue.LedgerEpochId != context.Epoch.Id
            || returned.LedgerEpochId != context.Epoch.Id
            || issue.ValuationStatus != ProductionPoolValuationService.Verified
            || returned.ValuationStatus != ProductionPoolValuationService.Verified
            || issueOwner.CompanyCode != context.CompanyCode
            || issueOwner.BranchCode != context.BranchCode
            || issueOwner.LedgerEpochId != context.Epoch.Id
            || issueOwner.SealedAtUtc is null || issueOwner.PostingRole != "PRIMARY"
            || returnOwner.CompanyCode != context.CompanyCode
            || returnOwner.BranchCode != context.BranchCode
            || returnOwner.LedgerEpochId != context.Epoch.Id
            || returnOwner.SealedAtUtc is null || returnOwner.PostingRole != "PRIMARY"
            || issueOwner.PostingSequence >= returnOwner.PostingSequence
            || issue.MovementDate > returned.MovementDate
            || issue.MovementDate != issueOwner.EffectiveAt
            || returned.MovementDate != returnOwner.EffectiveAt
            || pool.CompanyCode != context.CompanyCode || pool.BranchCode != context.BranchCode
            || pool.Kind != ProductionBalLotKinds.MaterialIn
            || pool.OriginalIssueMovementId != issueMaterial.Uid
            || pool.WorkOrderId != document.WorkOrderId
            || pool.ItemCode != issue.ItemCode || pool.ItemCode != returned.ItemCode
            || pool.BaseUom != issue.BaseUom || pool.BaseUom != returned.BaseUom
            || issue.BaseQty <= 0m || returned.BaseQty <= 0m
            || returned.BaseQty > issue.BaseQty
            || fact.SourceIssueMovementId != line.SourceIssueMovementId
            || fact.BaseQty != line.BaseQty || fact.BaseQty != returned.BaseQty
            || fact.TotalValue != returned.TotalCost
            || fact.InventoryHistoryId != returned.InventoryHistoryId
            || returnHistory.CompanyCode != context.CompanyCode
            || returnHistory.BranchCode != context.BranchCode
            || returnHistory.StockPostingId != returnOwner.Id
            || returnHistory.LedgerVersion != 2
            || returnHistory.EntryRole != "ORIGINAL"
            || returnHistory.TrxType != IvTrxTypes.ProductionStockReturn
            || returnHistory.ValuationStatus != StockValuationStatuses.Valued
            || StockLedgerPrecision.Quantity(returnHistory.ToStdQty ?? 0m)
                != StockLedgerPrecision.Quantity(fact.BaseQty)
            || returnHistory.FrStdQty is > 0m
            || valuation.Status != ProductionPoolValuationService.Verified
            || valuation.Generation <= 0
            || valuation.TrackedBaseQty != pool.BaseQty
            || valuation.TrackedValue != pool.TotalCost
            || await db.IvTrxHistories.AsNoTracking().AnyAsync(x =>
                x.ReversesHistoryId == fact.InventoryHistoryId, cancellationToken)
            || await db.ProductionStockReturnFacts.AsNoTracking().AnyAsync(x =>
                x.ReversesFactId == fact.Id, cancellationToken)
            || await db.StockPostings.AsNoTracking().AnyAsync(x =>
                (x.ReversesPostingId == issueOwner.Id
                    || x.ReversesPostingId == returnOwner.Id)
                && x.SealedAtUtc != null, cancellationToken))
            throw Invalid("Return ISSUE, posting, pool, or cross-ledger fact identity is incomplete.");

        var movements = await db.ProductionBalLotMovements.AsNoTracking()
            .Where(x => x.ProductionBalLotId == pool.Uid).ToArrayAsync(cancellationToken);
        var evidence = await db.ProductionValuationEvidenceRows.AsNoTracking()
            .Where(x => x.ProductionBalLotId == pool.Uid).ToArrayAsync(cancellationToken);
        var allocations = await ProductionActiveEdgeResolver.Allocations(db).AsNoTracking()
            .Where(x => x.OutboundMovementId == returned.Uid).ToArrayAsync(cancellationToken);
        var dependencies = await ProductionActiveEdgeResolver.Dependencies(db).AsNoTracking()
            .Where(x => x.ConsumerMovementId == returned.Uid).ToArrayAsync(cancellationToken);
        if (movements.Length != 2 || movements.All(x => x.Uid != issue.Uid)
            || movements.All(x => x.Uid != returned.Uid)
            || evidence.Length != 2
            || evidence.Any(x => x.Generation != valuation.Generation
                || x.Status != ProductionPoolValuationService.Verified)
            || evidence.Count(x => x.MovementId == issue.Uid) != 1
            || evidence.Count(x => x.MovementId == returned.Uid) != 1
            || allocations.Length != 1
            || allocations[0].CompanyCode != context.CompanyCode
            || allocations[0].BranchCode != context.BranchCode
            || allocations[0].ReceiptMovementId != issue.Uid
            || allocations[0].StockPostingId != returnOwner.Id
            || allocations[0].BaseQty != returned.BaseQty
            || dependencies.Length != 1
            || dependencies[0].ContributorMovementId != issue.Uid
            || dependencies[0].StockPostingId != returnOwner.Id
            || pool.BaseQty != issue.BaseQty - returned.BaseQty)
            throw Invalid("Return is not an isolated exact ISSUE contribution.");

        var effective = await ProductionEffectiveCostResolver.ResolveAsync(db,
            context.CompanyCode, context.BranchCode,
            [issue.Uid, returned.Uid], cancellationToken);
        var inventoryIssue = (await ProductionIssueEffectiveCostReader.ReadWithinRootAsync(
            context, [issue.Uid], cancellationToken))[issue.Uid];
        if (inventoryIssue.TotalCost != effective[issue.Uid].TotalCost
            || inventoryIssue.BaseQty != issue.BaseQty)
            throw Invalid("Corrected Production ISSUE cost differs from its active Inventory valuation.");
        var correctedSource = effective[issue.Uid].TotalCost;
        var sequence = ProductionStockReturnSequenceCostProjection.Project(
            issue.BaseQty, issue.TotalCost, correctedSource,
            [new ProductionStockReturnSequenceStep(returned.Uid, returned.BaseQty,
                fact.TotalValue, effective[returned.Uid].TotalCost)], returned.Uid);
        var correctedReturn = sequence.CorrectedTargetValue;
        var correctedRemaining = sequence.ProjectedRemainingValue;
        if (effective[returned.Uid].RestatementFactId is long activeRestatementId)
        {
            var activeRestatement = await db.ProductionCostRestatementFacts.AsNoTracking()
                .SingleAsync(x => x.Id == activeRestatementId, cancellationToken);
            if (activeRestatement.ReplayStockPostingId != context.Posting.Id
                || effective[returned.Uid].TotalCost != correctedReturn
                || pool.TotalCost != correctedRemaining)
                throw Invalid("Return has a different active cost correction or pool value.");
        }
        else if (effective[returned.Uid].TotalCost != fact.TotalValue
            || pool.TotalCost != StockLedgerPrecision.Money(
                issue.TotalCost - fact.TotalValue)
                && !(correctedReturn == fact.TotalValue
                    && pool.TotalCost == correctedRemaining))
            throw Invalid("Return original cost or source pool value is inconsistent.");
        return new(issue.Uid, returned.Uid, fact.Id, fact.InventoryHistoryId,
            correctedSource, correctedReturn,
            correctedRemaining);
    }

    private static StockLedgerException Invalid(string message) =>
        new(new StockLedgerError(StockLedgerErrorCodes.LedgerMismatch, message));
}
