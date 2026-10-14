using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderTransferService
{
    public Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> RollbackAsync(
        ProductionWorkOrderTransferCommand request, CancellationToken ct = default)
        => ExecuteRollbackAsync(request, ct);

    private async Task<IvMasterOperationResult<ProductionWorkOrderTransferDocument>> ExecuteRollbackAsync(
        ProductionWorkOrderTransferCommand request, CancellationToken ct)
    {
        try
        {
            var scope = await ScopeAsync(PermissionCodes.Rollback, true, ct);
            if (request.RequestId == Guid.Empty || request.ExpectedVersion is null || request.ExpectedVersion.Length == 0)
                throw new TransferException("A stable request ID and document version are required.");
            var reason = request.Reason?.Trim();
            if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
                throw new TransferException("Enter a rollback reason of at most 500 characters.");

            const string commandType = ProductionPostingCommandTypes.WorkOrderTransferRollback;
            var semanticRequest = new
            {
                request.Id, Version = Convert.ToBase64String(request.ExpectedVersion), Command = commandType, Reason = reason
            };
            var requestFingerprint = StockPostingFingerprint.Create(semanticRequest, new { }).RequestFingerprint;
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            db.ProductionWorkOrderTransferWrite = true;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await branchLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, ct);

            var replay = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x =>
                x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == commandType && x.RequestId == request.RequestId, ct);
            if (replay is not null)
            {
                if (replay.RequestFingerprint != requestFingerprint)
                    throw new TransferException("The request ID was reused with different input.");
                if (replay.SealedAtUtc is null)
                    throw new TransferException("The existing posting is not sealed; reconciliation is required.");
                var replayTransfer = await LoadTransferAsync(db, scope, request.Id, false, ct);
                var replayDocument = await MapAsync(db, replayTransfer, scope, ct);
                await tx.CommitAsync(ct);
                return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(replayDocument);
            }

            var observed = await db.ProductionWorkOrderTransfers.AsNoTracking().SingleOrDefaultAsync(x => x.Uid == request.Id
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DeletedAtUtc == null, ct)
                ?? throw new TransferException("Work Order Transfer was not found.", IvMasterErrorCode.NotFound);
            var workOrderIds = new[] { observed.SourceWorkOrderId, observed.TargetWorkOrderId }.Distinct().Order().ToArray();
            foreach (var workOrderId in workOrderIds)
                await ProductionWorkOrderExecutionLock.AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, workOrderId, ct);

            var transfer = await LoadTransferAsync(db, scope, request.Id, true, ct);
            CheckVersion(transfer, request.ExpectedVersion);
            if (transfer.Status != ProductionWorkOrderTransferStatuses.Posted || transfer.DeletedAtUtc is not null)
                throw new TransferException("Only a POSTED Work Order Transfer can be rolled back.");
            if (transfer.SourceWorkOrderId != observed.SourceWorkOrderId || transfer.TargetWorkOrderId != observed.TargetWorkOrderId)
                throw new TransferException("Work Order Transfer ownership changed while locks were being acquired. Reload and retry.", IvMasterErrorCode.Concurrency);
            if (transfer.StockPostingId is not long primaryId || transfer.DestinationProductionBalLotId is not long destinationId
                || transfer.TransferOutMovementId is not long transferOutId || transfer.TransferInMovementId is not long transferInId
                || transfer.MovementGroupId is not Guid originalGroupId)
                throw new TransferException("This Work Order Transfer predates rollback evidence and cannot be reversed automatically. Run WOT reconciliation first.");
            if (await ProductionPostingGenerationGuard.ValidateRollbackTargetAsync(db, primaryId, scope.CompanyCode,
                    scope.BranchCode!, "PRODUCTION", ProductionDocumentTypes.WorkOrderTransfer,
                    transfer.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture), transfer.DocumentRevision, ct)
                is string generationError)
                throw new TransferException(generationError);

            var primary = await db.StockPostings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == primaryId
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.PostingRole == "PRIMARY" && x.SealedAtUtc != null, ct)
                ?? throw new TransferException("The sealed Work Order Transfer PRIMARY posting is missing.");
            var forwardFacts = await db.ProductionWorkOrderTransferFacts.Where(x => x.TransferId == transfer.Uid
                && x.DocumentRevision == transfer.DocumentRevision && x.StockPostingId == primaryId && x.ReversesFactId == null)
                .ToListAsync(ct);
            if (forwardFacts.Count != 1)
                throw new TransferException("This Work Order Transfer predates rollback evidence and cannot be reversed automatically. Run WOT reconciliation first.");
            var forwardFact = forwardFacts[0];
            if (forwardFact.SourceProductionBalLotId != transfer.SourceProductionBalLotId
                || forwardFact.DestinationProductionBalLotId != destinationId
                || forwardFact.SourceLegMovementId != transferOutId || forwardFact.TargetLegMovementId != transferInId
                || forwardFact.MovementGroupId != originalGroupId
                || forwardFact.DocumentRevision != transfer.DocumentRevision)
                throw new TransferException("Immutable Work Order Transfer evidence does not match the current document pointers.");

            var originalLink = await db.ProductionPostingLinks.SingleOrDefaultAsync(x => x.Uid == forwardFact.ProductionPostingLinkId
                && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode
                && x.CommandType == ProductionPostingCommandTypes.WorkOrderTransferPost
                && x.ProductionDocumentLineId == transfer.Uid && x.DocumentRevision == transfer.DocumentRevision
                && x.Status == ProductionPostingLinkStatuses.Succeeded, ct)
                ?? throw new TransferException("The forward Work Order Transfer posting link is missing or inconsistent.");
            var originalMovements = await db.ProductionBalLotMovements.Where(x => x.Uid == transferOutId || x.Uid == transferInId)
                .OrderBy(x => x.Uid).ToListAsync(ct);
            var originalOut = originalMovements.SingleOrDefault(x => x.Uid == transferOutId);
            var originalIn = originalMovements.SingleOrDefault(x => x.Uid == transferInId);
            if (originalMovements.Count != 2 || originalOut is null || originalIn is null
                || originalOut.MovementType != ProductionBalLotMovementTypes.TransferOut
                || originalIn.MovementType != ProductionBalLotMovementTypes.TransferIn
                || originalOut.ProductionBalLotId != forwardFact.SourceProductionBalLotId
                || originalIn.ProductionBalLotId != forwardFact.DestinationProductionBalLotId
                || originalOut.StockPostingId != primaryId || originalIn.StockPostingId != primaryId
                || originalOut.PostingLinkId != originalLink.Uid || originalIn.PostingLinkId != originalLink.Uid
                || originalOut.MovementGroupId != originalGroupId || originalIn.MovementGroupId != originalGroupId
                || originalOut.OriginalMovementId is not null || originalIn.OriginalMovementId is not null
                || originalOut.Qty != forwardFact.Qty || originalIn.Qty != forwardFact.Qty
                || originalOut.BaseQty != forwardFact.BaseQty || originalIn.BaseQty != forwardFact.BaseQty
                || originalOut.BaseUom != forwardFact.BaseUom || originalIn.BaseUom != forwardFact.BaseUom
                || originalOut.TotalCost != forwardFact.TotalValue || originalIn.TotalCost != forwardFact.TotalValue
                || originalOut.UnitCost != forwardFact.UnitCost || originalIn.UnitCost != forwardFact.UnitCost
                || originalOut.DocumentType != ProductionDocumentTypes.WorkOrderTransfer
                || originalIn.DocumentType != ProductionDocumentTypes.WorkOrderTransfer)
                throw new TransferException("The original Work Order Transfer movement evidence does not match its immutable fact.");

            var lockedOrders = new Dictionary<long, ProductionWorkOrder>();
            foreach (var workOrderId in workOrderIds)
                lockedOrders[workOrderId] = await LockWorkOrderAsync(db, scope, workOrderId, ct);
            var source = await LoadSourceLotAsync(db, scope, transfer.SourceProductionBalLotId, true, ct);
            var target = await LockPoolByIdAsync(db, scope, destinationId, ct);
            ValidateRollbackPoolIdentity(transfer, source, target);
            if (IvQty.Round(source.BaseQty) != IvQty.Round(forwardFact.SourceBaseAfter)
                || StockLedgerPrecision.Money(source.TotalCost) != StockLedgerPrecision.Money(forwardFact.SourceValueAfter)
                || IvQty.Round(target.BaseQty) != IvQty.Round(forwardFact.TargetBaseAfter)
                || StockLedgerPrecision.Money(target.TotalCost) != StockLedgerPrecision.Money(forwardFact.TargetValueAfter))
                throw new TransferException("The WIP balance no longer matches the posted Work Order Transfer. Reconcile or reverse later activity first.");

            if (await ProductionCostReadiness.PoolValuationError(db, source, ct) is { } sourceCostError)
                throw new TransferException(sourceCostError);
            if (await ProductionCostReadiness.PoolValuationError(db, target, ct) is { } targetCostError)
                throw new TransferException(targetCostError);
            var sourceValuation = await db.ProductionPoolValuationRows.AsNoTracking()
                .SingleAsync(x => x.ProductionBalLotId == source.Uid, ct);
            var targetValuation = await db.ProductionPoolValuationRows.AsNoTracking()
                .SingleAsync(x => x.ProductionBalLotId == target.Uid, ct);
            await ValidateCompletePoolContributionsAsync(db, source, ct, requireActivePositiveReceipt: false);
            await ValidateCompletePoolContributionsAsync(db, target, ct);

            var targetTransferContribution = (await ProductionContributionAvailabilityService.ReadAsync(
                db, target.Uid, targetValuation.Generation, ct)).SingleOrDefault(x => x.Movement.Uid == transferInId);
            if (targetTransferContribution is null
                || targetTransferContribution.Evidence.Status != ProductionPoolValuationService.Verified
                || IvQty.Round(targetTransferContribution.Contribution.AvailableBaseQty) != IvQty.Round(originalIn.BaseQty))
                throw new TransferException("The transferred WIP has been partially or fully consumed and cannot be rolled back.");

            var laterActivity = await (from movement in db.ProductionBalLotMovements
                join posting in db.StockPostings on movement.StockPostingId equals posting.Id
                where (movement.ProductionBalLotId == source.Uid || movement.ProductionBalLotId == target.Uid)
                    && posting.PostingSequence > primary.PostingSequence
                    && movement.OriginalMovementId == null && posting.SealedAtUtc != null
                    && !db.StockPostings.Any(reversal => reversal.ReversesPostingId == posting.Id && reversal.SealedAtUtc != null)
                select movement.Uid).AnyAsync(ct);
            if (laterActivity)
                throw new TransferException("Reverse later active Production movements on this WIP pool first.");
            if (await ProductionPoolValuationService.HasActiveDependentsAsync(db, [transferInId], ct))
                throw new TransferException("Rollback is blocked because downstream Production movements depend on the transferred WIP. Reverse downstream production first.");

            var effectiveAt = clock.Now;
            var sourceBaseBefore = IvQty.Round(source.BaseQty);
            var sourceValueBefore = StockLedgerPrecision.Money(source.TotalCost);
            var targetBaseBefore = IvQty.Round(target.BaseQty);
            var targetValueBefore = StockLedgerPrecision.Money(target.TotalCost);
            var sourceGenerationBefore = sourceValuation.Generation;
            var targetGenerationBefore = targetValuation.Generation;
            var groupId = Guid.NewGuid();
            var postingLink = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = transfer.TargetWorkOrderId,
                CommandType = commandType, PostingRequestId = request.RequestId.ToString("N"),
                DocumentRevision = transfer.DocumentRevision, ProductionDocumentType = ProductionDocumentTypes.WorkOrderTransfer,
                ProductionDocumentNo = transfer.TransferNo, ProductionDocumentLineId = transfer.Uid,
                SnapshotRevision = transfer.TargetSnapshotRevision, SnapshotHash = transfer.TargetSnapshotHash,
                OriginalPostingLinkId = originalLink.Uid, Status = ProductionPostingLinkStatuses.Pending,
                CreatedDate = effectiveAt, CreatedBy = User(scope)
            };
            db.ProductionPostingLinks.Add(postingLink);
            await db.SaveChangesAsync(ct);

            var evidence = StockPostingFingerprint.Create(semanticRequest, new
            {
                transfer.Uid, transfer.TransferNo, transfer.DocumentRevision, PrimaryPostingId = primary.Id,
                ForwardFactId = forwardFact.Id, OriginalOutMovementId = originalOut.Uid, OriginalInMovementId = originalIn.Uid,
                SourceBefore = new { source.Uid, source.Qty, source.BaseQty, source.TotalCost, source.RowVersion },
                TargetBefore = new { target.Uid, target.Qty, target.BaseQty, target.TotalCost, target.RowVersion },
                sourceGenerationBefore, targetGenerationBefore, EffectiveAt = effectiveAt, Reason = reason
            });
            var postCommand = new StockPostingCommand
            {
                RequestId = request.RequestId, CommandType = commandType, SourceModule = "PRODUCTION",
                SourceDocumentType = ProductionDocumentTypes.WorkOrderTransfer,
                SourceDocumentId = transfer.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                SourceDocumentNo = transfer.TransferNo, DocumentRevision = transfer.DocumentRevision,
                PostingRole = "REVERSAL", EffectiveAt = effectiveAt, Evidence = evidence,
                ReversesPostingId = primaryId, ReasonText = reason, ProductionPostingLinkId = postingLink.Uid
            };
            var begin = await coordinator.BeginInTransactionAsync(db, postCommand, ct);
            if (begin.Error is not null) throw new TransferException(begin.Error.Message);
            if (!begin.LedgerEnabled || begin.Context is null)
                throw new TransferException("An active V2 stock ledger is required. No legacy Work Order Transfer rollback is permitted.");
            if (begin.WasReplay)
                throw new TransferException("A prior Work Order Transfer rollback exists without matching NEW document evidence. Reconcile before retrying.");
            var context = begin.Context;
            await using var postingScope = new StockPostingAbortScope(coordinator, context);
            context.Posting.ProductionPostingLinkId = postingLink.Uid;

            var legs = new[]
            {
                new ProductionStockLeg(target, ProductionBalLotMovementTypes.TransferOut,
                    originalIn.Qty, originalIn.BaseQty, transfer.TargetWorkOrderId, originalIn.WorkOrderMaterialId,
                    originalIn.WorkOrderOperationId, originalIn.RouteStepId, postingLink.Uid,
                    ProductionDocumentTypes.WorkOrderTransfer, transfer.TransferNo, $"{transfer.Uid}:ROLLBACK:OUT", 0,
                    OriginalMovementId: originalIn.Uid, ExactTotalValue: originalIn.TotalCost,
                    ValuationStatus: ProductionPoolValuationService.Verified),
                new ProductionStockLeg(source, ProductionBalLotMovementTypes.TransferIn,
                    originalOut.Qty, originalOut.BaseQty, transfer.SourceWorkOrderId, originalOut.WorkOrderMaterialId,
                    originalOut.WorkOrderOperationId, originalOut.RouteStepId, postingLink.Uid,
                    ProductionDocumentTypes.WorkOrderTransfer, transfer.TransferNo, $"{transfer.Uid}:ROLLBACK:IN", 0,
                    OriginalMovementId: originalOut.Uid, ExactTotalValue: originalOut.TotalCost,
                    ValuationStatus: ProductionPoolValuationService.Verified)
            };
            var movements = await productionStock.ApplyAsync(context, legs, ct);
            if (movements.Count != 2) throw new TransferException("Work Order Transfer rollback did not create exactly two production movements.");
            var targetOut = movements.Single(x => x.MovementType == ProductionBalLotMovementTypes.TransferOut);
            var sourceIn = movements.Single(x => x.MovementType == ProductionBalLotMovementTypes.TransferIn);
            StampTransferMovement(targetOut, target.WorkCentreCode, target.ProcessCode,
                target.ProductionLocation?.Code, groupId);
            StampTransferMovement(sourceIn, source.WorkCentreCode, source.ProcessCode,
                source.ProductionLocation?.Code, groupId);
            AssertReversalPair(targetOut, sourceIn, originalIn, originalOut, primaryId, groupId);
            await db.SaveChangesAsync(ct);
            await ProductionPoolValuationService.RecordAsync(context, movements, ct);
            if (targetOut.ValuationStatus != ProductionPoolValuationService.Verified
                || sourceIn.ValuationStatus != ProductionPoolValuationService.Verified)
                throw new TransferException("Work Order Transfer rollback valuation evidence is not VERIFIED.");

            var sourceBaseAfter = IvQty.Round(source.BaseQty);
            var sourceValueAfter = StockLedgerPrecision.Money(source.TotalCost);
            var targetBaseAfter = IvQty.Round(target.BaseQty);
            var targetValueAfter = StockLedgerPrecision.Money(target.TotalCost);
            if (sourceBaseAfter != IvQty.Round(forwardFact.SourceBaseBefore)
                || sourceValueAfter != StockLedgerPrecision.Money(forwardFact.SourceValueBefore)
                || targetBaseAfter != IvQty.Round(forwardFact.TargetBaseBefore)
                || targetValueAfter != StockLedgerPrecision.Money(forwardFact.TargetValueBefore)
                || IvQty.Round(source.BaseQty / source.ConversionFactorToBase) != IvQty.Round(source.Qty)
                || IvQty.Round(target.BaseQty / target.ConversionFactorToBase) != IvQty.Round(target.Qty)
                || source.AverageUnitCost != (source.BaseQty > 0m ? StockLedgerPrecision.Money(source.TotalCost / source.BaseQty) : 0m)
                || target.AverageUnitCost != (target.BaseQty > 0m ? StockLedgerPrecision.Money(target.TotalCost / target.BaseQty) : 0m))
                throw new TransferException("Work Order Transfer rollback did not restore the exact original WIP balances.");

            var sourceValuationAfter = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == source.Uid, ct);
            var targetValuationAfter = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == target.Uid, ct);
            if (sourceValuationAfter.Status != ProductionPoolValuationService.Verified
                || sourceValuationAfter.TrackedBaseQty != source.BaseQty || sourceValuationAfter.TrackedValue != source.TotalCost
                || targetValuationAfter.Status != ProductionPoolValuationService.Verified
                || targetValuationAfter.TrackedBaseQty != target.BaseQty || targetValuationAfter.TrackedValue != target.TotalCost)
                throw new TransferException("Work Order Transfer rollback valuation does not match the restored live balances.");
            await ValidateCompletePoolContributionsAsync(db, source, ct, requireActivePositiveReceipt: source.BaseQty > 0m);
            await ValidateCompletePoolContributionsAsync(db, target, ct, requireActivePositiveReceipt: target.BaseQty > 0m);
            var targetAvailabilityAfter = await ProductionContributionAvailabilityService.ReadAsync(db, target.Uid,
                targetValuationAfter.Generation, ct);
            var restoredTransferReceipt = targetAvailabilityAfter.SingleOrDefault(x => x.Movement.Uid == transferInId);
            if (restoredTransferReceipt is null || IvQty.Round(restoredTransferReceipt.Contribution.AvailableBaseQty) != 0m)
                throw new TransferException("The reversed target transfer contribution still has available quantity.");

            db.ProductionWorkOrderTransferFacts.Add(new ProductionWorkOrderTransferFact
            {
                TransferId = transfer.Uid, DocumentRevision = transfer.DocumentRevision,
                StockPostingId = context.Posting.Id, ProductionPostingLinkId = postingLink.Uid,
                SourceProductionBalLotId = source.Uid, DestinationProductionBalLotId = target.Uid,
                SourceLegMovementId = sourceIn.Uid, TargetLegMovementId = targetOut.Uid, MovementGroupId = groupId,
                Qty = sourceIn.Qty, BaseQty = sourceIn.BaseQty, BaseUom = sourceIn.BaseUom,
                TotalValue = sourceIn.TotalCost, UnitCost = sourceIn.UnitCost,
                SourceBaseBefore = sourceBaseBefore, SourceValueBefore = sourceValueBefore,
                SourceBaseAfter = sourceBaseAfter, SourceValueAfter = sourceValueAfter,
                TargetBaseBefore = targetBaseBefore, TargetValueBefore = targetValueBefore,
                TargetBaseAfter = targetBaseAfter, TargetValueAfter = targetValueAfter,
                SourceGenerationBefore = sourceGenerationBefore, SourceGenerationAfter = sourceValuationAfter.Generation,
                TargetGenerationBefore = targetGenerationBefore, TargetGenerationAfter = targetValuationAfter.Generation,
                ReversesFactId = forwardFact.Id, CreatedAtUtc = DateTime.UtcNow
            });

            transfer.Status = ProductionWorkOrderTransferStatuses.New;
            transfer.ReversalPostingId = context.Posting.Id;
            transfer.ReversalReason = reason;
            transfer.RollbackDate = effectiveAt;
            transfer.RollbackBy = User(scope);
            transfer.RollbackCount = checked(transfer.RollbackCount + 1);
            transfer.RollbackOperationId = request.RequestId;
            transfer.DocumentRevision = checked(transfer.DocumentRevision + 1);
            transfer.Qty = source.Qty;
            transfer.BaseQty = source.BaseQty;
            transfer.TransferTotalCost = source.TotalCost;
            transfer.SourceBalanceRowVersion = source.RowVersion.ToArray();
            transfer.SourceLastMovementDate = source.LastMovementDate;
            transfer.SourceLastStockEventEffectiveAt = source.LastStockEventEffectiveAt;
            if (HasCurrentSnapshot(source.WorkOrder))
            {
                transfer.SourceSnapshotRevision = source.WorkOrder!.SnapshotRevision;
                transfer.SourceSnapshotHash = source.WorkOrder.SnapshotHash;
            }

            var nextDraftLink = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, WorkOrderId = transfer.TargetWorkOrderId,
                CommandType = ProductionPostingCommandTypes.WorkOrderTransferPost,
                PostingRequestId = Guid.NewGuid().ToString("N"), DocumentRevision = transfer.DocumentRevision,
                ProductionDocumentType = ProductionDocumentTypes.WorkOrderTransfer,
                ProductionDocumentNo = transfer.TransferNo, ProductionDocumentLineId = transfer.Uid,
                SnapshotRevision = transfer.TargetSnapshotRevision, SnapshotHash = transfer.TargetSnapshotHash,
                Status = ProductionPostingLinkStatuses.Draft, CreatedDate = effectiveAt, CreatedBy = User(scope)
            };
            db.ProductionPostingLinks.Add(nextDraftLink);
            await db.SaveChangesAsync(ct);
            postingLink.Status = ProductionPostingLinkStatuses.Succeeded;
            postingLink.ResultCode = "ROLLED_BACK";
            postingLink.ResultMessage = reason;
            postingLink.CompletedDate = effectiveAt;
            transfer.ProductionPostingLinkId = nextDraftLink.Uid;
            transfer.PostingRequestId = nextDraftLink.PostingRequestId;
            await db.SaveChangesAsync(ct);
            await coordinator.CompleteInTransactionAsync(context, ct);
            postingScope.MarkCompleted();
            if (context.Posting.SealedAtUtc is null)
                throw new TransferException("The Work Order Transfer reversal was not sealed.");
            var document = await MapAsync(db, transfer, scope, ct);
            await tx.CommitAsync(ct);
            return IvMasterOperationResult<ProductionWorkOrderTransferDocument>.Ok(document);
        }
        catch (TransferException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, ex.Code); }
        catch (StockLedgerException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Error.Message); }
        catch (ProductionWorkOrderExecutionLockException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message, IvMasterErrorCode.Concurrency); }
        catch (DbUpdateConcurrencyException) { return Fail<ProductionWorkOrderTransferDocument>("Stock or Work Order Transfer changed. Reload and retry.", IvMasterErrorCode.Concurrency); }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex)) { return Fail<ProductionWorkOrderTransferDocument>("This Work Order Transfer generation already has a rollback or posting. Reload the document.", IvMasterErrorCode.Concurrency); }
        catch (InvalidOperationException ex) { return Fail<ProductionWorkOrderTransferDocument>(ex.Message); }
    }

    private static async Task<ProductionBalLot> LockPoolByIdAsync(AppDbContext db, InventoryTenantScope scope,
        long id, CancellationToken ct)
    {
        IQueryable<ProductionBalLot> query = db.ProductionBalLots.Where(x => x.Uid == id
            && x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);
        if (db.Database.IsSqlServer())
            query = db.ProductionBalLots.FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK,HOLDLOCK)
WHERE UID={id} AND CompanyCode={scope.CompanyCode} AND BranchCode={scope.BranchCode}");
        return await query.Include(x => x.ProductionLocation).Include(x => x.WorkOrderOperation)
            .SingleOrDefaultAsync(ct) ?? throw new TransferException("Target WIP pool was not found.");
    }

    private static void ValidateRollbackPoolIdentity(ProductionWorkOrderTransfer transfer,
        ProductionBalLot source, ProductionBalLot target)
    {
        if (source.Kind != ProductionBalLotKinds.Wip || source.WorkOrderId != transfer.SourceWorkOrderId
            || source.ProducingRouteStepId != transfer.SourceProducingRouteStepId
            || source.WorkOrderOperationId != transfer.SourceProducerOperationId || source.ItemCode != transfer.ItemCode
            || source.LotNo != transfer.LotNo || source.Uom != transfer.Uom || source.BaseUom != transfer.BaseUom
            || source.ConversionFactorToBase != transfer.ConversionFactorToBase
            || (string.IsNullOrWhiteSpace(source.PhysicalLotNo) ? transfer.LotNo : source.PhysicalLotNo) != transfer.LotNo
            || source.ProductionLocationId != transfer.SourceProductionLocationId
            || source.BalanceStage != "PROCESS_WIP" || source.OutputType != PrRouteOutputTypes.WipStocked
            || source.StockStatusCode != "AVAILABLE" || source.OriginType != "PRODUCED"
            || target.Kind != ProductionBalLotKinds.Wip || target.WorkOrderId != transfer.TargetWorkOrderId
            || target.ProducingRouteStepId != transfer.TargetProducingRouteStepId
            || target.WorkOrderOperationId != transfer.TargetProducerOperationId || target.WorkOrderMaterialId is not null
            || target.ItemCode != transfer.ItemCode || target.LotNo != transfer.LotNo
            || target.Uom != transfer.Uom || target.BaseUom != transfer.BaseUom
            || target.ConversionFactorToBase != transfer.ConversionFactorToBase
            || !string.IsNullOrWhiteSpace(target.PhysicalLotNo) && target.PhysicalLotNo != transfer.LotNo
            || target.BalanceStage != "PROCESS_WIP" || target.OutputType != PrRouteOutputTypes.WipStocked
            || target.StockStatusCode != "AVAILABLE" || target.OriginType != "PRODUCED")
            throw new TransferException("Work Order Transfer WIP identity changed after posting; reconcile the pools before rollback.");
    }

    private static void AssertReversalPair(ProductionBalLotMovement targetOut, ProductionBalLotMovement sourceIn,
        ProductionBalLotMovement originalIn, ProductionBalLotMovement originalOut, long primaryId, Guid reversalGroupId)
    {
        if (targetOut.MovementType != ProductionBalLotMovementTypes.TransferOut
            || sourceIn.MovementType != ProductionBalLotMovementTypes.TransferIn
            || targetOut.OriginalMovementId != originalIn.Uid || sourceIn.OriginalMovementId != originalOut.Uid
            || targetOut.StockPostingId != sourceIn.StockPostingId || targetOut.StockPostingId == primaryId
            || targetOut.MovementGroupId != reversalGroupId || sourceIn.MovementGroupId != reversalGroupId
            || targetOut.ProductionBalLotId != originalIn.ProductionBalLotId
            || sourceIn.ProductionBalLotId != originalOut.ProductionBalLotId
            || targetOut.Qty != originalIn.Qty || sourceIn.Qty != originalOut.Qty
            || targetOut.BaseQty != originalIn.BaseQty || sourceIn.BaseQty != originalOut.BaseQty
            || targetOut.BaseUom != originalIn.BaseUom || sourceIn.BaseUom != originalOut.BaseUom
            || targetOut.TotalCost != originalIn.TotalCost || sourceIn.TotalCost != originalOut.TotalCost
            || targetOut.UnitCost != originalIn.UnitCost || sourceIn.UnitCost != originalOut.UnitCost
            || targetOut.ItemCode != originalIn.ItemCode || sourceIn.ItemCode != originalOut.ItemCode)
            throw new TransferException("Work Order Transfer reversal legs do not exactly mirror the original movement pair.");
    }

    private static bool HasCurrentSnapshot(ProductionWorkOrder? order) => order is not null
        && order.SnapshotRevision > 0 && order.SnapshotHash?.Length == 64
        && ProductionSnapshotFormatVersions.IsFullHierarchy(order.SnapshotFormatVersion)
        && !order.IsLegacySnapshot && ProductionSnapshotHashVersions.SupportsDailyProduction(order.SnapshotHashVersion);
}
