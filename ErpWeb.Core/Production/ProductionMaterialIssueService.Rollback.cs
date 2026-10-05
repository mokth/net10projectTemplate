using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionMaterialIssueService
{
    public async Task<IvMasterOperationResult<ProductionMaterialIssueRollbackResult>> RollbackAsync(
        ProductionMaterialIssueRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_stockPosting is null || _inventoryPosting is null)
            return RollbackFail(IvMasterErrorCode.Validation, "Material issue rollback dependencies are unavailable.");
        if (!await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, cancellationToken)
            || !await _access.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Rollback, cancellationToken))
            return RollbackFail(IvMasterErrorCode.AccessDenied, "Access denied.");

        var validation = ValidateRollbackRequest(request);
        if (validation.Count > 0)
            return IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Fail(
                IvMasterErrorCode.Validation, "The rollback request is invalid.", validation);
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
            return RollbackFail(IvMasterErrorCode.InvalidScope, "A company, branch and user scope is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await new ErpWeb.Core.StockLedger.BranchStockTransactionLock().AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, cancellationToken);
            var replay = await LockRollbackLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.PostingRequestId, cancellationToken);
            if (replay is not null)
            {
                if (replay.Status == ProductionPostingLinkStatuses.Succeeded && replay.InventoryBatchNo.HasValue)
                {
                    var replayResult = await BuildRollbackResultAsync(db, replay, cancellationToken);
                    await tx.CommitAsync(cancellationToken);
                    return IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Ok(replayResult);
                }

                return RollbackFail(IvMasterErrorCode.Concurrency, "This rollback request is already being processed.");
            }

            var originalLink = await LockOriginalIssueLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo, cancellationToken);
            if (originalLink is null)
                return RollbackFail(IvMasterErrorCode.NotFound, "Issue to Production was not found.");
            if (originalLink.Status != ProductionPostingLinkStatuses.Succeeded)
                return RollbackFail(IvMasterErrorCode.Validation, "Only a successfully posted Issue to Production can be rolled back.");

            var batch = await _stockPosting.LockBatchForUpdateAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo, cancellationToken);
            if (batch is null || batch.TrxType != IvTrxTypes.IssueToProduction)
                return RollbackFail(IvMasterErrorCode.NotFound, "Issue to Production was not found.");
            if (batch.BatchStatus != IvBatchStatuses.Posted)
                return RollbackFail(IvMasterErrorCode.Validation, "Only POSTED Issue to Production documents can be rolled back.");
            var effectiveAt = await ErpWeb.Core.StockLedger.StockBusinessTime.NowAsync(
                db, scope.CompanyCode, cancellationToken);
            var ledger = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo,
                true, effectiveAt, cancellationToken);
            if (ledger.Error is not null)
                return RollbackFail(IvMasterErrorCode.Validation, ledger.Error.Message);

            var order = await LockWorkOrderByIdAsync(db, originalLink.WorkOrderId, cancellationToken);
            if (order is null)
                return RollbackFail(IvMasterErrorCode.NotFound, "Work Order was not found.");
            var originalMovements = await db.ProductionMaterialMovements
                .Where(x => x.PostingLinkId == originalLink.Uid && x.MovementType == ProductionMaterialMovementTypes.Issue)
                .OrderBy(x => x.WorkOrderMaterialId).ThenBy(x => x.InventoryTrxLineNo)
                .ToListAsync(cancellationToken);
            if (originalMovements.Count == 0)
                return RollbackFail(IvMasterErrorCode.Validation, "The original production issue facts were not found.");

            var originalIds = originalMovements.Select(x => x.Uid).ToList();
            var alreadyReversedIds = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.OriginalMovementId.HasValue
                    && originalIds.Contains(x.OriginalMovementId.Value)
                    && x.MovementType == ProductionMaterialMovementTypes.IssueReversal)
                .Select(x => x.OriginalMovementId!.Value)
                .ToListAsync(cancellationToken);
            originalMovements = originalMovements
                .Where(x => !alreadyReversedIds.Contains(x.Uid))
                .ToList();
            if (originalMovements.Count == 0)
                return RollbackFail(IvMasterErrorCode.Validation, "This Issue to Production has already been rolled back.");

            originalIds = originalMovements.Select(x => x.Uid).ToList();
            var dependentMovements = await LoadDependencyGraphAsync(
                db, originalIds, cancellationToken);
            if (ProductionMaterialMovementTotals.HasBlockingDownstreamDependency(dependentMovements))
                return RollbackFail(IvMasterErrorCode.InUse, "Rollback is blocked because later production movements depend on this issue.");

            var originalBalanceMovements = originalMovements.Where(x => x.ProductionBalLotMovementId.HasValue).Select(x => x.ProductionBalLotMovementId!.Value).ToArray();
            if (await ProductionPoolValuationService.HasActiveDependentsAsync(db, originalBalanceMovements, cancellationToken))
                return RollbackFail(IvMasterErrorCode.InUse, "Rollback is blocked by an active pooled-value dependency.");

            // MATERIAL_IN piles must still hold the full original issue base qty.
            foreach (var original in originalMovements)
            {
                var pile = await db.ProductionBalLots
                    .FirstOrDefaultAsync(x => x.OriginalIssueMovementId == original.Uid, cancellationToken);
                if (pile is not null
                    && IvQty.Round(pile.BaseQty) != IvQty.Round(original.BaseQty))
                {
                    return RollbackFail(IvMasterErrorCode.InUse,
                        $"Production balance lot for {original.ItemCode} no longer holds the full issued quantity.");
                }
            }
            var now = _clock.Now;
            var user = scope.UserId.Length > 10 ? scope.UserId[..10] : scope.UserId;
            var rollbackLink = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.MaterialIssueRollback,
                PostingRequestId = request.PostingRequestId,
                WorkOrderId = order.Uid,
                ProductionDocumentType = ProductionDocumentTypes.MaterialIssue,
                ProductionDocumentNo = request.InventoryBatchNo.ToString(),
                InventoryBatchNo = request.InventoryBatchNo,
                OriginalPostingLinkId = originalLink.Uid,
                Status = ProductionPostingLinkStatuses.Pending,
                CreatedDate = now,
                CreatedBy = user
            };
            db.ProductionPostingLinks.Add(rollbackLink);
            await db.SaveChangesAsync(cancellationToken);

            var materialIds = originalMovements.Select(x => x.WorkOrderMaterialId).Distinct().OrderBy(x => x).ToList();
            var materials = new Dictionary<long, ProductionWorkOrderMaterial>();
            foreach (var materialId in materialIds)
            {
                var material = await LockMaterialAsync(db, materialId, cancellationToken);
                if (material is null || material.WorkOrderId != order.Uid)
                    return RollbackFail(IvMasterErrorCode.NotFound, "A Work Order material was not found.");
                materials[materialId] = material;
            }

            var rolledBack = await _inventoryPosting.RollBackStockOutInTransactionAsync(
                ledger.Context,
                db, scope.CompanyCode, scope.BranchCode!, user, request.InventoryBatchNo,
                IvTrxTypes.IssueToProduction, cancellationToken);
            if (!rolledBack.Succeeded)
                return RollbackFail(IvMasterErrorCode.Validation, rolledBack.ErrorMessage ?? "Inventory rollback failed.");

            // Inventory MI rollback restores BalLoc and returns the batch to NEW for correction/re-post.
            // Keep that status (do not cancel) so users can adjust qty/lot and post again.
            batch.BatchStatus = IvBatchStatuses.New;

            // When this IP was the only history on a BalLoc, inventory rollback clears TransDate.
            // Piles with quantity still need a stock date so the reopened draft can be re-posted.
            foreach (var balLocId in originalMovements
                .Where(x => x.FromBalLocId.HasValue)
                .Select(x => x.FromBalLocId!.Value)
                .Distinct()
                .OrderBy(x => x))
            {
                var locked = await _stockPosting!.LockBalLocByIdForTenantAsync(
                    db, balLocId, scope.CompanyCode, scope.BranchCode!, cancellationToken);
                if (locked is null)
                    return RollbackFail(IvMasterErrorCode.NotFound, $"Stock balance {balLocId} was not found after rollback.");
                if (locked.StdQty > 0m && locked.TransDate is null)
                {
                    var stockDate = originalMovements
                        .Where(x => x.FromBalLocId == balLocId)
                        .Min(x => x.MovementDate);
                    await _stockPosting.SetBalLocTransDateAsync(
                        db, balLocId, scope.CompanyCode, scope.BranchCode!, stockDate, cancellationToken);
                }
            }

            foreach (var original in originalMovements)
            {
                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
                {
                    CompanyCode = original.CompanyCode,
                    BranchCode = original.BranchCode,
                    WorkOrderId = original.WorkOrderId,
                    WorkOrderMaterialId = original.WorkOrderMaterialId,
                    WorkOrderOperationId = original.WorkOrderOperationId,
                    MovementType = ProductionMaterialMovementTypes.IssueReversal,
                    MovementDate = ledger.Context?.Posting.EffectiveAt ?? now,
                    ItemCode = original.ItemCode,
                    Qty = original.Qty,
                    Uom = original.Uom,
                    BaseQty = original.BaseQty,
                    BaseUom = original.BaseUom,
                    ConversionFactorToBase = original.ConversionFactorToBase,
                    WarehouseCode = original.WarehouseCode,
                    LocationCode = original.LocationCode,
                    LotNo = original.LotNo,
                    LotId = original.LotId,
                    FromBalLocId = original.FromBalLocId,
                    ItemStatus = original.ItemStatus,
                    InventoryBatchId = original.InventoryBatchId,
                    InventoryBatchNo = original.InventoryBatchNo,
                    InventoryBatchDetailId = original.InventoryBatchDetailId,
                    InventoryTrxLineNo = original.InventoryTrxLineNo,
                    InventoryHistoryId = null,
                    InventoryPostingOperationId = rolledBack.OperationId?.ToString("N"),
                    UnitCost = original.UnitCost,
                    TotalCost = original.TotalCost,
                    PostingLinkId = rollbackLink.Uid,
                    StockPostingId = ledger.Context?.Posting.Id,
                    OriginalMovementId = original.Uid,
                    Reason = "ROLLBACK",
                    Remarks = Truncate(request.Reason, 250),
                    CreatedDate = now,
                    CreatedBy = user
                });

                // Archive the reversed ISSUE under the rollback link so the post link can accept a new
                // ISSUE row on the same InventoryBatchDetailId after the draft is corrected and re-posted.
                if (ledger.Context is null)
                {
                    original.PostingLinkId = rollbackLink.Uid;
                    original.InventoryHistoryId = null;
                }
            }

            var facts = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => materialIds.Contains(x.WorkOrderMaterialId))
                .Select(x => new { x.WorkOrderMaterialId, x.MovementType, x.Qty })
                .ToListAsync(cancellationToken);
            var reversedByMaterial = originalMovements.GroupBy(x => x.WorkOrderMaterialId)
                .ToDictionary(x => x.Key, x => IvQty.Round(x.Sum(y => y.Qty)));
            foreach (var material in materials.Values)
            {
                var materialFacts = facts.Where(x => x.WorkOrderMaterialId == material.Uid);
                material.IssuedQty = IvQty.Round(
                    materialFacts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue).Sum(x => x.Qty)
                    - materialFacts.Where(x => x.MovementType == ProductionMaterialMovementTypes.IssueReversal).Sum(x => x.Qty)
                    - reversedByMaterial.GetValueOrDefault(material.Uid));
                material.ReturnedQty = IvQty.Round(materialFacts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Return).Sum(x => x.Qty));
                material.ConsumedQty = ProductionMaterialMovementTotals.EffectiveConsumed(
                    materialFacts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty),
                    materialFacts.Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal).Sum(x => x.Qty));
                material.ModifiedDate = now;
                material.ModifiedBy = user;
            }

            foreach (var original in originalMovements)
            {
                var pile = await db.ProductionBalLots
                    .FirstOrDefaultAsync(x => x.OriginalIssueMovementId == original.Uid, cancellationToken);
                if (pile is null)
                    continue;
                db.ProductionBalLotMovements.Add(new ProductionBalLotMovement
                {
                    ProductionBalLotId = pile.Uid,
                    MovementType = ProductionBalLotMovementTypes.IssueReversal,
                    Qty = original.Qty,
                    Uom = original.Uom,
                    BaseQty = original.BaseQty,
                    BaseUom = original.BaseUom,
                    UnitCost = original.UnitCost,
                    TotalCost = original.TotalCost,
                    WorkOrderId = original.WorkOrderId,
                    WorkOrderMaterialId = original.WorkOrderMaterialId,
                    WorkOrderOperationId = original.WorkOrderOperationId,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = original.ProductionBalLotMovementId,
                    DocumentType = ProductionDocumentTypes.MaterialIssue,
                    DocumentNo = request.InventoryBatchNo.ToString(),
                    MovementDate = ledger.Context?.Posting.EffectiveAt ?? original.MovementDate,
                    CreatedDate = now,
                    CreatedBy = user,
                });
                if (ledger.Context is not null)
                {
                    var reversal = db.ChangeTracker.Entries<ProductionBalLotMovement>()
                        .Where(x => x.State == EntityState.Added)
                        .Select(x => x.Entity)
                        .Last();
                    reversal.LedgerVersion = 2;
                    reversal.LedgerEpochId = ledger.Context.Epoch.Id;
                    reversal.StockPostingId = ledger.Context.Posting.Id;
                    reversal.PostingLineNo = db.ChangeTracker.Entries<ProductionBalLotMovement>()
                        .Count(x => x.State == EntityState.Added && x.Entity.StockPostingId == ledger.Context.Posting.Id);
                    reversal.CompanyCode = original.CompanyCode;
                    reversal.BranchCode = original.BranchCode;
                    reversal.ItemCode = original.ItemCode;
                    reversal.WorkOrderNo = order.WorkOrderNo;
                    reversal.ConversionFactorToBase = original.ConversionFactorToBase;
                    reversal.SourceLineId = original.InventoryTrxLineNo?.ToString() ?? original.Uid.ToString();
                    reversal.SplitOrdinal = 0;
                    reversal.ValuationStatus = "UNVALUED";
                }
                pile.Qty = 0m;
                pile.BaseQty = 0m;
                pile.TotalCost = 0m;
                pile.AverageUnitCost = 0m;
                pile.LastMovementDate = original.MovementDate;
            }

            // Reopen the same post link as a NEW draft so the user can edit qty/lot and re-post.
            originalLink.Status = ProductionPostingLinkStatuses.Draft;
            originalLink.PostingOperationId = null;
            originalLink.ResultCode = null;
            originalLink.ResultMessage = $"Reopened after rollback {request.PostingRequestId}.";
            originalLink.CompletedDate = null;
            rollbackLink.PostingOperationId = rolledBack.OperationId?.ToString("N");
            rollbackLink.Status = ProductionPostingLinkStatuses.Succeeded;
            rollbackLink.ResultCode = "OK";
            rollbackLink.ResultMessage = $"Rolled back IP batch {request.InventoryBatchNo}; draft reopened for correction.";
            rollbackLink.CompletedDate = now;
            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.MaterialIssueRolledBack,
                FromStatus = order.Status,
                ToStatus = order.Status,
                SnapshotRevision = order.SnapshotRevision,
                Reason = Truncate(request.Reason, 250),
                OccurredDate = now,
                ActorUserId = user
            });

            await db.SaveChangesAsync(cancellationToken);
            if (ledger.Context is not null)
            {
                var balanceReversals = await db.ProductionBalLotMovements.Where(x => x.StockPostingId == ledger.Context.Posting.Id).ToListAsync(cancellationToken);
                await ProductionPoolValuationService.RecordAsync(ledger.Context, balanceReversals, cancellationToken);
                await _inventoryPosting.CompletePostingInTransactionAsync(ledger.Context, cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Ok(
                await BuildRollbackResultAsync(db, rollbackLink, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            return RollbackFail(IvMasterErrorCode.Concurrency, "The rollback conflicted with another request; retry with the same PostingRequestId.");
        }
    }

    private static Dictionary<string, string> ValidateRollbackRequest(ProductionMaterialIssueRollbackRequest request)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Guid.TryParseExact(request?.PostingRequestId, "N", out _)) errors[nameof(request.PostingRequestId)] = "PostingRequestId must be a GUID in N format.";
        if (request?.InventoryBatchNo is null or <= 0) errors[nameof(request.InventoryBatchNo)] = "Inventory batch number is required.";
        if (string.IsNullOrWhiteSpace(request?.Reason)) errors[nameof(request.Reason)] = "Rollback reason is required.";
        else if (request.Reason.Trim().Length > 250) errors[nameof(request.Reason)] = "Rollback reason cannot exceed 250 characters.";
        return errors;
    }

    private static async Task<ProductionPostingLink?> LockRollbackLinkAsync(
        AppDbContext db, string company, string branch, string requestId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssueRollback} AND PostingRequestId={requestId}").FirstOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.FirstOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssueRollback && x.PostingRequestId == requestId, ct);

    private static async Task<List<ProductionMaterialMovement>> LoadDependencyGraphAsync(
        AppDbContext db,
        IReadOnlyCollection<long> originalIds,
        CancellationToken ct)
    {
        var visited = originalIds.ToHashSet();
        var frontier = originalIds.ToList();
        var dependencies = new List<ProductionMaterialMovement>();

        while (frontier.Count > 0)
        {
            var children = await db.ProductionMaterialMovements.AsNoTracking()
                .Where(x => x.OriginalMovementId.HasValue
                    && frontier.Contains(x.OriginalMovementId.Value))
                .OrderBy(x => x.Uid)
                .ToListAsync(ct);

            frontier = [];
            foreach (var child in children)
            {
                if (!visited.Add(child.Uid))
                    continue;
                dependencies.Add(child);
                frontier.Add(child.Uid);
            }
        }

        return dependencies;
    }

    private static async Task<ProductionPostingLink?> LockOriginalIssueLinkAsync(
        AppDbContext db, string company, string branch, int batchNo, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssuePost} AND InventoryBatchNo={batchNo}").SingleOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost && x.InventoryBatchNo == batchNo, ct);

    private static async Task<ProductionWorkOrder?> LockWorkOrderByIdAsync(
        AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders.FromSqlInterpolated($@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static async Task<ProductionMaterialIssueRollbackResult> BuildRollbackResultAsync(
        AppDbContext db, ProductionPostingLink link, CancellationToken ct)
    {
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == link.WorkOrderId, ct);
        var materialIds = await db.ProductionMaterialMovements.AsNoTracking()
            .Where(x => x.PostingLinkId == link.Uid && x.MovementType == ProductionMaterialMovementTypes.IssueReversal)
            .Select(x => x.WorkOrderMaterialId).Distinct().ToListAsync(ct);
        var materials = await db.ProductionWorkOrderMaterials.AsNoTracking()
            .Where(x => materialIds.Contains(x.Uid)).OrderBy(x => x.Uid).ToListAsync(ct);
        return new ProductionMaterialIssueRollbackResult
        {
            PostingRequestId = link.PostingRequestId,
            BatchNo = link.InventoryBatchNo ?? 0,
            RollbackOperationId = link.PostingOperationId,
            WorkOrderNo = order.WorkOrderNo,
            WorkOrderStatus = order.Status,
            RollbackDate = link.CompletedDate ?? link.CreatedDate,
            Materials = materials.Select(x => new ProductionMaterialIssuePostedMaterial
            {
                WorkOrderMaterialId = x.Uid,
                IssuedQty = x.IssuedQty,
                ReturnedQty = x.ReturnedQty,
                NetIssuedQty = ProductionWorkOrderCalc.NetIssuedQty(x.IssuedQty, x.ReturnedQty),
                OutstandingQty = ProductionWorkOrderCalc.OpenRequirementQty(x.RequiredQty, x.IssuedQty, x.ReturnedQty)
            }).ToList()
        };
    }

    private static IvMasterOperationResult<ProductionMaterialIssueRollbackResult> RollbackFail(
        IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Fail(code, message);
}
