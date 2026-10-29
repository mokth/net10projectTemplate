using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
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
        ErpWeb.Core.StockLedger.StockPostingContext? activePosting = null;
        var postingCompleted = false;
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

            var batch = await _stockPosting.LockBatchForUpdateAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo, cancellationToken);
            if (batch is null || batch.TrxType != IvTrxTypes.IssueToProduction)
                return RollbackFail(IvMasterErrorCode.NotFound, "Issue to Production was not found.");
            if (batch.BatchStatus != IvBatchStatuses.Posted)
                return RollbackFail(IvMasterErrorCode.Validation, "Only POSTED Issue to Production documents can be rolled back.");
            var currentRevision = await db.IvTrxBatchDetails
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                    && x.BatchId == batch.Id && x.TrxType == IvTrxTypes.IssueToProduction)
                .Select(x => (int?)x.DocumentRevision).MaxAsync(cancellationToken);
            if (currentRevision is null or <= 0)
                return RollbackFail(IvMasterErrorCode.Validation, "The current inventory issue revision was not found.");
            var originalLink = await LockOriginalIssueLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo, currentRevision.Value, cancellationToken);
            if (originalLink is null || originalLink.Status != ProductionPostingLinkStatuses.Succeeded)
                return RollbackFail(IvMasterErrorCode.Validation, "The current Issue to Production generation has no successful POST link.");
            var originalPostingId = await db.StockPostings.AsNoTracking()
                .Where(x => x.ProductionPostingLinkId == originalLink.Uid
                    && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                    && x.SourceModule == "INVENTORY" && x.SourceDocumentType == batch.TrxType
                    && x.SourceDocumentId == batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    && x.DocumentRevision == currentRevision.Value && x.PostingRole == "PRIMARY")
                .Select(x => (long?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (originalPostingId is null)
                return RollbackFail(IvMasterErrorCode.Validation, "The sealed V2 PRIMARY for this Issue to Production generation was not found.");
            if (await ProductionPostingGenerationGuard.ValidateRollbackTargetAsync(
                    db, originalPostingId.Value, scope.CompanyCode, scope.BranchCode!, "INVENTORY", batch.TrxType,
                    batch.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), currentRevision.Value, cancellationToken)
                is string generationError)
                return RollbackFail(IvMasterErrorCode.Validation, generationError);
            var effectiveAt = await ErpWeb.Core.StockLedger.StockBusinessTime.NowAsync(
                db, scope.CompanyCode, cancellationToken);
            var ledger = await _inventoryPosting.BeginPostingInTransactionAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.InventoryBatchNo,
                true, effectiveAt, cancellationToken);
            if (ledger.Error is not null)
                return RollbackFail(IvMasterErrorCode.Validation, ledger.Error.Message,
                    ProductionOperatorErrorLog.Ledger(_logger, null, ledger.Error, ErpDocumentKind.MaterialIssue,
                        request.InventoryBatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ErpErrorAction.Rollback, "Material Issue rollback begin"));
            if (!ledger.LedgerEnabled || ledger.Context is null)
                return RollbackFail(IvMasterErrorCode.Validation, "Issue to Production rollback requires an active V2 stock ledger for this company/branch.");
            if (ledger.WasReplay)
                return RollbackFail(IvMasterErrorCode.Concurrency, "A sealed V2 rollback already exists for this generation, but the document is still POSTED. Run posting reconciliation.");
            var ledgerContext = ledger.Context;
            activePosting = ledgerContext;

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

            // Inventory replay can change an issue's effective value without
            // changing its sealed Production material movement. Reverse the
            // current value, and verify it still matches the active Inventory
            // generation before changing either ledger.
            var effectiveCosts = await ProductionEffectiveCostResolver.ResolveAsync(
                db, scope.CompanyCode, scope.BranchCode!, originalBalanceMovements, cancellationToken);
            var activeIssues = await ProductionIssueEffectiveCostReader.ReadAsync(
                db, scope.CompanyCode, scope.BranchCode!, originalBalanceMovements, cancellationToken);
            if (originalBalanceMovements.Any(id => activeIssues[id].TotalCost != effectiveCosts[id].TotalCost
                || activeIssues[id].UnitCost != effectiveCosts[id].UnitCost))
                return RollbackFail(IvMasterErrorCode.Validation,
                    "Production issue cost does not match its active Inventory valuation.");

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
                if (pile is not null && original.ProductionBalLotMovementId is long movementId
                    && effectiveCosts.TryGetValue(movementId, out var cost)
                    && cost.RestatementFactId is not null)
                {
                    var pool = await db.ProductionPoolValuationRows.AsNoTracking()
                        .SingleOrDefaultAsync(p => p.ProductionBalLotId == pile.Uid, cancellationToken);
                    if (pool is null || pool.Status != ProductionPoolValuationService.Verified
                        || pool.TrackedBaseQty != pile.BaseQty || pool.TrackedValue != cost.TotalCost
                        || pile.TotalCost != cost.TotalCost || pile.AverageUnitCost != cost.UnitCost)
                        return RollbackFail(IvMasterErrorCode.Validation,
                            "The corrected Production material pool does not match the issue cost.");
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
                DocumentRevision = currentRevision.Value,
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
                ledgerContext,
                db, scope.CompanyCode, scope.BranchCode!, user, request.InventoryBatchNo,
                IvTrxTypes.IssueToProduction, cancellationToken);
            if (!rolledBack.Succeeded)
                return RollbackFail(IvMasterErrorCode.Validation,
                    rolledBack.ErrorMessage ?? "Inventory rollback failed.",
                    rolledBack.OperatorError);

            // Inventory MI rollback restores BalLoc and returns the batch to NEW for editing and reposting.
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
                var effective = original.ProductionBalLotMovementId is long movementId
                    && effectiveCosts.TryGetValue(movementId, out var cost) ? cost : null;
                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
                {
                    CompanyCode = original.CompanyCode,
                    BranchCode = original.BranchCode,
                    WorkOrderId = original.WorkOrderId,
                    WorkOrderMaterialId = original.WorkOrderMaterialId,
                    WorkOrderOperationId = original.WorkOrderOperationId,
                    MovementType = ProductionMaterialMovementTypes.IssueReversal,
                    MovementDate = ledgerContext.Posting.EffectiveAt,
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
                    UnitCost = effective?.UnitCost ?? original.UnitCost,
                    TotalCost = effective?.TotalCost ?? original.TotalCost,
                    PostingLinkId = rollbackLink.Uid,
                    StockPostingId = ledgerContext.Posting.Id,
                    OriginalMovementId = original.Uid,
                    Reason = "ROLLBACK",
                    Remarks = Truncate(request.Reason, 250),
                    CreatedDate = now,
                    CreatedBy = user
                });

            }

            var facts = await ProductionMaterialExecutionFacts.ReadAsync(db, scope.CompanyCode, scope.BranchCode!, materialIds, cancellationToken);
            var reversedByMaterial = originalMovements.GroupBy(x => x.WorkOrderMaterialId)
                .ToDictionary(x => x.Key, x => IvQty.Round(x.Sum(y => y.Qty)));
            foreach (var material in materials.Values)
            {
                var execution = facts.GetValueOrDefault(material.Uid, ProductionMaterialExecutionTotals.Empty);
                material.IssuedQty = IvQty.Round(execution.EffectiveIssuedQty - reversedByMaterial.GetValueOrDefault(material.Uid));
                material.ReturnedQty = execution.EffectiveReturnedQty;
                material.ConsumedQty = execution.EffectiveConsumedQty;
                material.ModifiedDate = now;
                material.ModifiedBy = user;
            }

            foreach (var original in originalMovements)
            {
                var pile = await db.ProductionBalLots
                    .FirstOrDefaultAsync(x => x.OriginalIssueMovementId == original.Uid, cancellationToken);
                if (pile is null)
                    continue;
                var effective = original.ProductionBalLotMovementId is long movementId
                    && effectiveCosts.TryGetValue(movementId, out var cost) ? cost : null;
                db.ProductionBalLotMovements.Add(new ProductionBalLotMovement
                {
                    ProductionBalLotId = pile.Uid,
                    MovementType = ProductionBalLotMovementTypes.IssueReversal,
                    Qty = original.Qty,
                    Uom = original.Uom,
                    BaseQty = original.BaseQty,
                    BaseUom = original.BaseUom,
                    UnitCost = effective?.UnitCost ?? original.UnitCost,
                    TotalCost = effective?.TotalCost ?? original.TotalCost,
                    WorkOrderId = original.WorkOrderId,
                    WorkOrderMaterialId = original.WorkOrderMaterialId,
                    WorkOrderOperationId = original.WorkOrderOperationId,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = original.ProductionBalLotMovementId,
                    DocumentType = ProductionDocumentTypes.MaterialIssue,
                    DocumentNo = request.InventoryBatchNo.ToString(),
                    MovementDate = ledgerContext.Posting.EffectiveAt,
                    CreatedDate = now,
                    CreatedBy = user,
                });
                var reversal = db.ChangeTracker.Entries<ProductionBalLotMovement>()
                    .Where(x => x.State == EntityState.Added)
                    .Select(x => x.Entity)
                    .Last();
                reversal.LedgerVersion = 2;
                reversal.LedgerEpochId = ledgerContext.Epoch.Id;
                reversal.StockPostingId = ledgerContext.Posting.Id;
                reversal.PostingLineNo = db.ChangeTracker.Entries<ProductionBalLotMovement>()
                    .Count(x => x.State == EntityState.Added && x.Entity.StockPostingId == ledgerContext.Posting.Id);
                reversal.CompanyCode = original.CompanyCode;
                reversal.BranchCode = original.BranchCode;
                reversal.ItemCode = original.ItemCode;
                reversal.WorkOrderNo = order.WorkOrderNo;
                reversal.ConversionFactorToBase = original.ConversionFactorToBase;
                reversal.SourceLineId = original.InventoryTrxLineNo?.ToString() ?? original.Uid.ToString();
                reversal.SplitOrdinal = 0;
                reversal.ValuationStatus = "UNVALUED";
                pile.Qty = 0m;
                pile.BaseQty = 0m;
                pile.TotalCost = 0m;
                pile.AverageUnitCost = 0m;
                pile.LastMovementDate = original.MovementDate;
            }

            // Inventory rollback advances detail revisions on tracked entities. Persist them
            // inside this root transaction before querying the next generation; an EF SQL
            // aggregate otherwise reads the old database revision and falsely rejects rollback.
            await db.SaveChangesAsync(cancellationToken);
            var nextRevision = await db.IvTrxBatchDetails
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                    && x.BatchId == batch.Id && x.TrxType == IvTrxTypes.IssueToProduction)
                .Select(x => (int?)x.DocumentRevision).MaxAsync(cancellationToken);
            if (nextRevision != checked(currentRevision.Value + 1))
                return RollbackFail(IvMasterErrorCode.Validation, "Inventory rollback did not advance the issue detail generation exactly once.");
            var activeMaps = await db.ProductionMaterialIssueLines
                .Where(x => x.PostingLinkId == originalLink.Uid && x.DocumentRevision == currentRevision.Value)
                .ToListAsync(cancellationToken);
            if (activeMaps.Count == 0 || activeMaps.Any(x => x.DocumentRevision != currentRevision.Value))
                return RollbackFail(IvMasterErrorCode.Validation, "The current production issue allocation map is incomplete.");
            var nextPostLink = new ProductionPostingLink
            {
                CompanyCode = originalLink.CompanyCode,
                BranchCode = originalLink.BranchCode,
                CommandType = ProductionPostingCommandTypes.MaterialIssuePost,
                PostingRequestId = Guid.NewGuid().ToString("N"),
                DocumentRevision = nextRevision.Value,
                WorkOrderId = originalLink.WorkOrderId,
                ProductionDocumentType = originalLink.ProductionDocumentType,
                ProductionDocumentNo = originalLink.ProductionDocumentNo,
                InventoryBatchNo = originalLink.InventoryBatchNo,
                SnapshotRevision = originalLink.SnapshotRevision,
                SnapshotHash = originalLink.SnapshotHash,
                ProductionQtyThisIssue = originalLink.ProductionQtyThisIssue,
                Status = ProductionPostingLinkStatuses.Draft,
                CreatedDate = now,
                CreatedBy = user
            };
            db.ProductionPostingLinks.Add(nextPostLink);
            await db.SaveChangesAsync(cancellationToken);
            foreach (var map in activeMaps)
            {
                var detailRevision = await db.IvTrxBatchDetails.AsNoTracking()
                    .Where(x => x.Id == map.InventoryBatchDetailId)
                    .Select(x => (int?)x.DocumentRevision).SingleOrDefaultAsync(cancellationToken);
                if (detailRevision != nextRevision)
                    return RollbackFail(IvMasterErrorCode.Validation, "An inventory detail does not match the new issue generation.");
                map.PostingLinkId = nextPostLink.Uid;
                map.DocumentRevision = nextRevision.Value;
            }
            rollbackLink.PostingOperationId = rolledBack.OperationId?.ToString("N");
            rollbackLink.Status = ProductionPostingLinkStatuses.Succeeded;
            rollbackLink.ResultCode = "OK";
            rollbackLink.ResultMessage = $"Rolled back IP batch {request.InventoryBatchNo}; generation {nextRevision.Value} is ready for editing and reposting.";
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
            var balanceReversals = await db.ProductionBalLotMovements.Where(x => x.StockPostingId == ledgerContext.Posting.Id).ToListAsync(cancellationToken);
            await ProductionPoolValuationService.RecordAsync(ledgerContext, balanceReversals, cancellationToken);
            ledgerContext.Posting.ProductionPostingLinkId = rollbackLink.Uid;
            await _inventoryPosting.CompletePostingInTransactionAsync(ledgerContext, cancellationToken);
            postingCompleted = true;
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Ok(
                await BuildRollbackResultAsync(db, rollbackLink, cancellationToken));
        }
        catch (StockLedgerException ex)
        {
            return RollbackFail(IvMasterErrorCode.Validation, ex.Error.Message,
                ProductionOperatorErrorLog.Ledger(_logger, ex, ex.Error, ErpDocumentKind.MaterialIssue,
                    request.InventoryBatchNo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ErpErrorAction.Rollback, "Material Issue rollback"));
        }
        catch (DbUpdateException)
        {
            return RollbackFail(IvMasterErrorCode.Concurrency, "The rollback conflicted with another request; retry with the same PostingRequestId.");
        }
        finally
        {
            if (!postingCompleted)
                await _inventoryPosting.AbortPostingInTransactionAsync(activePosting, CancellationToken.None);
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
        AppDbContext db, string company, string branch, int batchNo, int documentRevision, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.MaterialIssuePost} AND InventoryBatchNo={batchNo} AND DocumentRevision={documentRevision}").SingleOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.CompanyCode == company && x.BranchCode == branch && x.CommandType == ProductionPostingCommandTypes.MaterialIssuePost && x.InventoryBatchNo == batchNo && x.DocumentRevision == documentRevision, ct);

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
        var totals = await ProductionMaterialExecutionFacts.ReadAsync(db, link.CompanyCode, link.BranchCode, materialIds, ct);
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
                NetActiveAdjustmentQty = totals.GetValueOrDefault(x.Uid, ProductionMaterialExecutionTotals.Empty).NetActiveAdjustmentQty,
                EffectiveSupplyQty = totals.GetValueOrDefault(x.Uid, ProductionMaterialExecutionTotals.Empty).EffectiveSupplyQty,
                OutstandingQty = totals.GetValueOrDefault(x.Uid, ProductionMaterialExecutionTotals.Empty).OutstandingQty(x.RequiredQty)
            }).ToList()
        };
    }

    private static IvMasterOperationResult<ProductionMaterialIssueRollbackResult> RollbackFail(
        IvMasterErrorCode code, string message,
        ErpWeb.Core.Services.ErpOperatorError? operatorError = null) =>
        IvMasterOperationResult<ProductionMaterialIssueRollbackResult>.Fail(
            code, message, operatorError: operatorError);
}
