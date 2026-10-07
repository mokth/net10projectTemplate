using ErpWeb.Core.Inventory;
using ErpWeb.Core.Transactions;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService
{
    public async Task<IvMasterOperationResult<ProductionOutputDetail>> RollbackAsync(
        ProductionOutputRollbackRequest request, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Rollback, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);
        var scope = WriteScope();
        if (scope is null) return Fail("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);

        if (request.OutputId <= 0) return Fail("Output is required.");
        if (!Guid.TryParseExact(request.PostingRequestId, "N", out _))
            return Fail("PostingRequestId must be a GUID in N format.");
        if (string.IsNullOrWhiteSpace(request.Reason))
            return Fail("Rollback reason is required.");

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await new ErpWeb.Core.StockLedger.BranchStockTransactionLock().AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, cancellationToken);
            var replay = await LockOutputRollbackLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.PostingRequestId, cancellationToken);
            if (replay is not null)
            {
                if (replay.Status == ProductionPostingLinkStatuses.Succeeded
                    && replay.ProductionDocumentLineId.HasValue)
                {
                    await tx.CommitAsync(cancellationToken);
                    return Ok(await MapDetailAsync(db, scope, replay.ProductionDocumentLineId.Value, cancellationToken));
                }

                return Fail("This rollback request is already being processed.", IvMasterErrorCode.Concurrency);
            }

            var output = await LockOutputAsync(db, scope.CompanyCode, scope.BranchCode!, request.OutputId, cancellationToken);
            if (output is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
            if (output.DeletedAtUtc is not null)
                return Fail(TransactionLifecycleGuard.ArchivedError(output.DeletedAtUtc, "This production output")!);
            if (output.Status == ProductionOutputStatuses.Reversed)
                return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
            if (output.Status != ProductionOutputStatuses.Posted)
                return Fail("Only POSTED Daily Production documents can be rolled back.");

            var postLink = await LockOutputPostLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, output.PostingRequestId, cancellationToken);
            if (postLink is null || postLink.Status != ProductionPostingLinkStatuses.Succeeded)
                return Fail("Original OUTPUT_POST link was not found or is not SUCCEEDED.");

            var order = await LockWorkOrderAsync(db, output.WorkOrderId, cancellationToken);
            if (order is null) return Fail("Work Order was not found.", IvMasterErrorCode.NotFound);
            var routeStep = await LockRouteStepAsync(db, output.RouteStepId, cancellationToken);
            var operation = await LockOperationAsync(db, output.WorkOrderOperationId, cancellationToken);
            if (routeStep is null || operation is null)
                return Fail("Route step or operation was not found.");

            // Match PostAsync lock ordering after the Work Order serialization root. The complete
            // graph remains stable while this rollback decides whether it would break a posted
            // downstream route history.
            await LockRouteStepsForWorkOrderAsync(db, order.Uid, cancellationToken);
            await LockOperationsForWorkOrderAsync(db, order.Uid, cancellationToken);

            var projectedOperationGood = IvQty.Round(operation.GoodQty - output.GoodQty);
            if (projectedOperationGood < 0m)
                return Fail("Reversing output would make an operation outcome quantity negative.");

            var projectedHasPostedGood = ProductionOperationSequenceGate.HasPostedGood(projectedOperationGood);
            var projectedComplete = ProductionOperationSequenceGate.IsOperationComplete(
                operation.PlannedOutputQty, projectedOperationGood);
            if (!projectedComplete)
            {
                var stage = await FindDownstreamStageExecutionAsync(
                    db, scope.CompanyCode, scope.BranchCode!, order.Uid, routeStep, cancellationToken);
                if (stage is not null)
                    return Fail(FormatDownstreamRollbackMessage(output.DocumentNo, stage));
            }

            if (!projectedHasPostedGood)
            {
                var process = await FindDownstreamProcessExecutionAsync(
                    db, scope.CompanyCode, scope.BranchCode!, order.Uid, routeStep, operation, cancellationToken);
                if (process is not null)
                    return Fail(FormatDownstreamRollbackMessage(output.DocumentNo, process));
            }

            var materials = await db.ProductionWorkOrderMaterials
                .Where(x => x.WorkOrderOperationId == operation.Uid)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            foreach (var materialId in materials.Select(x => x.Uid))
                await LockMaterialAsync(db, materialId, cancellationToken);

            var now = _clock.Now;
            var user = TruncateUser(scope.UserId);
            var rollbackLink = new ProductionPostingLink
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                CommandType = ProductionPostingCommandTypes.OutputRollback,
                PostingRequestId = request.PostingRequestId,
                WorkOrderId = order.Uid,
                ProductionDocumentType = ProductionDocumentTypes.ProductionOutput,
                ProductionDocumentNo = output.DocumentNo,
                ProductionDocumentLineId = output.Uid,
                OriginalPostingLinkId = postLink.Uid,
                SnapshotRevision = order.SnapshotRevision,
                SnapshotHash = order.SnapshotHash,
                Status = ProductionPostingLinkStatuses.Pending,
                CreatedDate = now,
                CreatedBy = user,
            };
            db.ProductionPostingLinks.Add(rollbackLink);
            await db.SaveChangesAsync(cancellationToken);

            StockPostingContext? ledgerContext = null;
            var originalPostingId = await db.ProductionBalLotMovements.AsNoTracking()
                .Where(x => x.ProductionOutputId == output.Uid && x.PostingLinkId == postLink.Uid && x.StockPostingId != null)
                .Select(x => x.StockPostingId)
                .FirstOrDefaultAsync(cancellationToken);
            var command = BuildOutputPostingCommand(
                output, reversal: true, _clock.Now, request.PostingRequestId, originalPostingId, rollbackLink.Uid);
            var ledger = await _stockCoordinator.BeginInTransactionAsync(db, command, cancellationToken);
            if (ledger.Error is not null)
                return Fail(ledger.Error.Message);
            ledgerContext = ledger.Context;

            // Prefix-balance guard for PRODUCE before mutating.
            var produceMovements = await db.ProductionBalLotMovements
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.MovementType == ProductionBalLotMovementTypes.Produce
                    && x.PostingLinkId == postLink.Uid)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);

            if (await ProductionPoolValuationService.HasActiveDependentsAsync(db, produceMovements.Select(x => x.Uid).ToArray(), cancellationToken))
                return Fail("Rollback is blocked by an active pooled-value dependency. Reverse downstream production/FG receipts first.");

            foreach (var produce in produceMovements)
            {
                var lotMovements = await db.ProductionBalLotMovements
                    .Where(x => x.ProductionBalLotId == produce.ProductionBalLotId)
                    .ToListAsync(cancellationToken);
                if (!ProductionBalLotSignedQty.CanRollbackProduce(lotMovements, produce.Uid))
                    return Fail("Produce rollback is blocked by later effective consumption on the WIP lot.");
            }

            // Reverse CONSUME first (restore piles), then PRODUCE.
            var consumeMaterial = await db.ProductionMaterialMovements
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.PostingLinkId == postLink.Uid
                    && x.MovementType == ProductionMaterialMovementTypes.Consume)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            var consumeBal = await db.ProductionBalLotMovements
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.PostingLinkId == postLink.Uid
                    && x.MovementType == ProductionBalLotMovementTypes.Consume)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);

            var balById = consumeBal.ToDictionary(x => x.Uid);
            var materialBalIds = consumeMaterial
                .Where(x => x.ProductionBalLotMovementId.HasValue)
                .Select(x => x.ProductionBalLotMovementId!.Value)
                .ToHashSet();

            foreach (var consume in consumeMaterial.OrderBy(x => x.ProductionBalLotId ?? 0).ThenBy(x => x.Uid))
            {
                if (consume.ProductionBalLotId is null || consume.ProductionBalLotMovementId is null)
                    return Fail("Consume fact is missing production balance lineage.");

                var lot = await LockBalLotByIdAsync(db, consume.ProductionBalLotId.Value, cancellationToken);
                if (lot is null) return Fail("Production balance lot was not found.");

                var originalBal = balById.GetValueOrDefault(consume.ProductionBalLotMovementId.Value);
                if (originalBal is null) return Fail("Bal-lot consume movement was not found.");

                lot.Qty = IvQty.Round(lot.Qty + originalBal.Qty);
                lot.BaseQty = IvQty.Round(lot.BaseQty + originalBal.BaseQty);
                lot.TotalCost = StockLedgerPrecision.Money(lot.TotalCost + originalBal.TotalCost);
                lot.AverageUnitCost = lot.BaseQty > 0m ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;

                var balRev = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.ConsumeReversal,
                    Qty = originalBal.Qty,
                    Uom = originalBal.Uom,
                    BaseQty = originalBal.BaseQty,
                    BaseUom = originalBal.BaseUom,
                    UnitCost = originalBal.UnitCost,
                    TotalCost = originalBal.TotalCost,
                    WorkOrderId = order.Uid,
                    WorkOrderMaterialId = consume.WorkOrderMaterialId,
                    WorkOrderOperationId = operation.Uid,
                    RouteStepId = routeStep.Uid,
                    ProductionOutputId = output.Uid,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = originalBal.Uid,
                    DocumentType = ProductionDocumentTypes.ProductionOutput,
                    DocumentNo = output.DocumentNo,
                    MovementDate = output.ProductionDate,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionBalLotMovements.Add(balRev);
                await db.SaveChangesAsync(cancellationToken);

                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
                {
                    CompanyCode = consume.CompanyCode,
                    BranchCode = consume.BranchCode,
                    WorkOrderId = consume.WorkOrderId,
                    WorkOrderMaterialId = consume.WorkOrderMaterialId,
                    WorkOrderOperationId = consume.WorkOrderOperationId,
                    MovementType = ProductionMaterialMovementTypes.ConsumeReversal,
                    MovementDate = output.ProductionDate,
                    ItemCode = consume.ItemCode,
                    Qty = consume.Qty,
                    Uom = consume.Uom,
                    BaseQty = consume.BaseQty,
                    BaseUom = consume.BaseUom,
                    ConversionFactorToBase = consume.ConversionFactorToBase,
                    WarehouseCode = consume.WarehouseCode,
                    LocationCode = consume.LocationCode,
                    LotNo = consume.LotNo,
                    FromBalLocId = consume.FromBalLocId,
                    ItemStatus = consume.ItemStatus,
                    UnitCost = consume.UnitCost,
                    TotalCost = consume.TotalCost,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = consume.Uid,
                    ProductionBalLotId = lot.Uid,
                    ProductionBalLotMovementId = balRev.Uid,
                    ProductionOutputId = output.Uid,
                    Reason = "ROLLBACK",
                    Remarks = Truncate(request.Reason, 250),
                    CreatedDate = now,
                    CreatedBy = user,
                });

                var allLotMovements = await db.ProductionBalLotMovements.AsNoTracking()
                    .Where(x => x.ProductionBalLotId == lot.Uid)
                    .ToListAsync(cancellationToken);
                lot.LastMovementDate = ProductionBalLotSignedQty.LatestEffectiveMovementDate(allLotMovements);
            }

            // Handoff consume: bal-lot CONSUME with no matching material movement.
            foreach (var handoffConsume in consumeBal
                         .Where(x => !materialBalIds.Contains(x.Uid))
                         .OrderBy(x => x.ProductionBalLotId)
                         .ThenBy(x => x.Uid))
            {
                var lot = await LockBalLotByIdAsync(db, handoffConsume.ProductionBalLotId, cancellationToken);
                if (lot is null) return Fail("Previous-process balance lot was not found.");

                lot.Qty = IvQty.Round(lot.Qty + handoffConsume.Qty);
                lot.BaseQty = IvQty.Round(lot.BaseQty + handoffConsume.BaseQty);
                lot.TotalCost = StockLedgerPrecision.Money(lot.TotalCost + handoffConsume.TotalCost);
                lot.AverageUnitCost = lot.BaseQty > 0m ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;

                var balRev = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.ConsumeReversal,
                    Qty = handoffConsume.Qty,
                    Uom = handoffConsume.Uom,
                    BaseQty = handoffConsume.BaseQty,
                    BaseUom = handoffConsume.BaseUom,
                    UnitCost = handoffConsume.UnitCost,
                    TotalCost = handoffConsume.TotalCost,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = operation.Uid,
                    RouteStepId = routeStep.Uid,
                    ProductionOutputId = output.Uid,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = handoffConsume.Uid,
                    DocumentType = ProductionDocumentTypes.ProductionOutput,
                    DocumentNo = output.DocumentNo,
                    MovementDate = output.ProductionDate,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionBalLotMovements.Add(balRev);
                await db.SaveChangesAsync(cancellationToken);

                var allLotMovements = await db.ProductionBalLotMovements.AsNoTracking()
                    .Where(x => x.ProductionBalLotId == lot.Uid)
                    .ToListAsync(cancellationToken);
                lot.LastMovementDate = ProductionBalLotSignedQty.LatestEffectiveMovementDate(allLotMovements);
            }

            foreach (var produce in produceMovements.OrderBy(x => x.ProductionBalLotId).ThenBy(x => x.Uid))
            {
                var lot = await LockBalLotByIdAsync(db, produce.ProductionBalLotId, cancellationToken);
                if (lot is null) return Fail("WIP production balance lot was not found.");

                var nextQty = IvQty.Round(lot.Qty - produce.Qty);
                var nextBaseQty = IvQty.Round(lot.BaseQty - produce.BaseQty);
                var nextCost = StockLedgerPrecision.Money(lot.TotalCost - produce.TotalCost);
                if (nextQty < 0m || nextBaseQty < 0m || nextCost < 0m)
                    return Fail($"Reversing output would make production balance lot {lot.Uid} negative.");
                lot.Qty = nextQty;
                lot.BaseQty = nextBaseQty;
                lot.TotalCost = nextCost;
                lot.AverageUnitCost = lot.BaseQty > 0m ? StockLedgerPrecision.Money(lot.TotalCost / lot.BaseQty) : 0m;

                var produceRev = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.ProduceReversal,
                    Qty = produce.Qty,
                    Uom = produce.Uom,
                    BaseQty = produce.BaseQty,
                    BaseUom = produce.BaseUom,
                    UnitCost = produce.BaseQty > 0m ? StockLedgerPrecision.Money(produce.TotalCost / produce.BaseQty) : 0m,
                    TotalCost = produce.TotalCost,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = operation.Uid,
                    RouteStepId = routeStep.Uid,
                    ProductionOutputId = output.Uid,
                    PostingLinkId = rollbackLink.Uid,
                    OriginalMovementId = produce.Uid,
                    DocumentType = ProductionDocumentTypes.ProductionOutput,
                    DocumentNo = output.DocumentNo,
                    MovementDate = output.ProductionDate,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionBalLotMovements.Add(produceRev);

                var allLotMovements = await db.ProductionBalLotMovements.AsNoTracking()
                    .Where(x => x.ProductionBalLotId == lot.Uid)
                    .ToListAsync(cancellationToken);
                allLotMovements.Add(produceRev);
                lot.LastMovementDate = ProductionBalLotSignedQty.LatestEffectiveMovementDate(allLotMovements);
            }

            var nextOperationGood = IvQty.Round(operation.GoodQty - output.GoodQty);
            var nextOperationScrap = IvQty.Round(operation.ScrapQty - output.ScrapQty);
            var nextOperationReject = IvQty.Round(operation.RejectQty - output.RejectQty);
            var nextOperationHold = IvQty.Round(operation.HoldQty - output.HoldQty);
            if (nextOperationGood < 0m || nextOperationScrap < 0m
                || nextOperationReject < 0m || nextOperationHold < 0m)
                return Fail("Reversing output would make an operation outcome quantity negative.");
            operation.GoodQty = nextOperationGood;
            operation.ScrapQty = nextOperationScrap;
            operation.RejectQty = nextOperationReject;
            operation.HoldQty = nextOperationHold;
            operation.ProcessedQty = IvQty.Round(
                operation.GoodQty + operation.ScrapQty + operation.RejectQty + operation.HoldQty);
            operation.RemainingQty = IvQty.Round(Math.Max(operation.PlannedOutputQty - operation.GoodQty, 0m));

            var isFinalFg = operation.IsFinalOperation
                && string.Equals(routeStep.OutputType, PrRouteOutputTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase);
            if (isFinalFg)
            {
                var factor = routeStep.OutputConversionFactorToBase ?? 1m;
                var nextOrderGood = IvQty.Round(order.GoodQty - IvQty.Round(output.GoodQty * factor));
                var nextOrderScrap = IvQty.Round(order.ScrapQty - IvQty.Round(output.ScrapQty * factor));
                var nextOrderReject = IvQty.Round(order.RejectQty - IvQty.Round(output.RejectQty * factor));
                var nextOrderHold = IvQty.Round(order.HoldQty - IvQty.Round(output.HoldQty * factor));
                if (nextOrderGood < 0m || nextOrderScrap < 0m
                    || nextOrderReject < 0m || nextOrderHold < 0m)
                    return Fail("Reversing output would make a Work Order outcome quantity negative.");
                order.GoodQty = nextOrderGood;
                order.ScrapQty = nextOrderScrap;
                order.RejectQty = nextOrderReject;
                order.HoldQty = nextOrderHold;
                order.RemainingQty = ProductionWorkOrderCalc.OpenProductionQty(
                    order.PlannedQty, order.GoodQty, order.ApprovedVarianceQty);
            }

            // Persist every reversal fact before deriving material aggregates. Mixing a database
            // total with the full pending list double-counts reversals flushed by earlier loops.
            await db.SaveChangesAsync(cancellationToken);

            await RebuildWorkOrderMaterialExecutionProjectionAsync(
                db, materials.Select(x => x.Uid).ToList(), output.Uid,
                ProductionOutputProjectionTransition.ExcludeCurrentRollback, cancellationToken);

            output.Status = ProductionOutputStatuses.Reversed;
            output.ReversedDate = now;
            output.ReversedBy = user;
            postLink.Status = ProductionPostingLinkStatuses.Reversed;
            postLink.ResultMessage = $"Reversed by {request.PostingRequestId}.";
            rollbackLink.Status = ProductionPostingLinkStatuses.Succeeded;
            rollbackLink.ResultCode = "OK";
            rollbackLink.ResultMessage = Truncate(request.Reason, 250);
            rollbackLink.CompletedDate = now;

            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.OutputRolledBack,
                FromStatus = order.Status,
                ToStatus = order.Status,
                SnapshotRevision = order.SnapshotRevision,
                Reason = Truncate(request.Reason, 250),
                OccurredDate = now,
                ActorUserId = user,
            });

            await db.SaveChangesAsync(cancellationToken);
            if (ledgerContext is not null)
            {
                await StampOutputLedgerFactsAsync(
                    ledgerContext, output.Uid, rollbackLink.Uid, order.WorkOrderNo, cancellationToken);
                await _stockCoordinator.CompleteInTransactionAsync(ledgerContext, cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail("Rollback conflicted with another change; retry with the same PostingRequestId.",
                IvMasterErrorCode.Concurrency);
        }
    }

    private static async Task<ProductionPostingLink?> LockOutputRollbackLinkAsync(
        AppDbContext db, string company, string branch, string requestId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.OutputRollback} AND PostingRequestId={requestId}")
                .FirstOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.FirstOrDefaultAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch
                && x.CommandType == ProductionPostingCommandTypes.OutputRollback
                && x.PostingRequestId == requestId, ct);

    private static async Task<ProductionBalLot?> LockBalLotByIdAsync(
        AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionBalLots.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}")
                .SingleOrDefaultAsync(ct)
            : await db.ProductionBalLots.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static async Task<DownstreamExecution?> FindDownstreamStageExecutionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        ProductionWorkOrderRouteStep currentRouteStep,
        CancellationToken cancellationToken)
    {
        var posted = await (
            from output in db.ProductionOutputs.AsNoTracking()
            join operation in db.ProductionWorkOrderOperations.AsNoTracking()
                on output.WorkOrderOperationId equals operation.Uid
            join routeStep in db.ProductionWorkOrderRouteSteps.AsNoTracking()
                on operation.RouteStepId equals (long?)routeStep.Uid
            where output.CompanyCode == companyCode
                && output.BranchCode == branchCode
                && output.WorkOrderId == workOrderId
                && output.Status == ProductionOutputStatuses.Posted
                && routeStep.StageSequence > currentRouteStep.StageSequence
            orderby routeStep.StageSequence, operation.ProcessSequence, output.ProductionDate, output.Uid
            select new DownstreamExecution(
                ProductionSequenceBlockingLevels.Stage,
                DownstreamExecutionKinds.DailyProduction,
                output.DocumentNo,
                routeStep.WorkCentreCode,
                operation.OperationCode))
            .FirstOrDefaultAsync(cancellationToken);
        if (posted is not null)
            return posted;

        return await (
            from material in db.ProductionWorkOrderMaterials.AsNoTracking()
            join operation in db.ProductionWorkOrderOperations.AsNoTracking()
                on material.WorkOrderOperationId equals operation.Uid
            join routeStep in db.ProductionWorkOrderRouteSteps.AsNoTracking()
                on operation.RouteStepId equals (long?)routeStep.Uid
            where material.WorkOrderId == workOrderId
                && material.IssuedQty - material.ReturnedQty > 0m
                && routeStep.StageSequence > currentRouteStep.StageSequence
            orderby routeStep.StageSequence, operation.ProcessSequence, material.Uid
            select new DownstreamExecution(
                ProductionSequenceBlockingLevels.Stage,
                DownstreamExecutionKinds.MaterialIssue,
                null,
                routeStep.WorkCentreCode,
                operation.OperationCode))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<DownstreamExecution?> FindDownstreamProcessExecutionAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        long workOrderId,
        ProductionWorkOrderRouteStep currentRouteStep,
        ProductionWorkOrderOperation currentOperation,
        CancellationToken cancellationToken)
    {
        var posted = await (
            from output in db.ProductionOutputs.AsNoTracking()
            join operation in db.ProductionWorkOrderOperations.AsNoTracking()
                on output.WorkOrderOperationId equals operation.Uid
            join routeStep in db.ProductionWorkOrderRouteSteps.AsNoTracking()
                on operation.RouteStepId equals (long?)routeStep.Uid
            where output.CompanyCode == companyCode
                && output.BranchCode == branchCode
                && output.WorkOrderId == workOrderId
                && output.Status == ProductionOutputStatuses.Posted
                && operation.RouteStepId == currentRouteStep.Uid
                && operation.ProcessSequence > currentOperation.ProcessSequence
            orderby operation.ProcessSequence, output.ProductionDate, output.Uid
            select new DownstreamExecution(
                ProductionSequenceBlockingLevels.Process,
                DownstreamExecutionKinds.DailyProduction,
                output.DocumentNo,
                routeStep.WorkCentreCode,
                operation.OperationCode))
            .FirstOrDefaultAsync(cancellationToken);
        if (posted is not null)
            return posted;

        return await (
            from material in db.ProductionWorkOrderMaterials.AsNoTracking()
            join operation in db.ProductionWorkOrderOperations.AsNoTracking()
                on material.WorkOrderOperationId equals operation.Uid
            join routeStep in db.ProductionWorkOrderRouteSteps.AsNoTracking()
                on operation.RouteStepId equals (long?)routeStep.Uid
            where material.WorkOrderId == workOrderId
                && material.IssuedQty - material.ReturnedQty > 0m
                && operation.RouteStepId == currentRouteStep.Uid
                && operation.ProcessSequence > currentOperation.ProcessSequence
            orderby operation.ProcessSequence, material.Uid
            select new DownstreamExecution(
                ProductionSequenceBlockingLevels.Process,
                DownstreamExecutionKinds.MaterialIssue,
                null,
                routeStep.WorkCentreCode,
                operation.OperationCode))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static string FormatDownstreamRollbackMessage(string documentNo, DownstreamExecution downstream)
    {
        if (downstream.ExecutionKind == DownstreamExecutionKinds.MaterialIssue)
        {
            return $"Cannot roll back {documentNo} because downstream Issue-to-Production "
                + $"remains active for {downstream.WorkCentreCode} / {downstream.OperationCode}.";
        }

        return $"Cannot roll back {documentNo} because downstream Daily Production "
            + $"{downstream.DocumentNo} is already POSTED for {downstream.WorkCentreCode} / "
            + $"{downstream.OperationCode}.";
    }

    private sealed record DownstreamExecution(
        string DependencyLevel,
        string ExecutionKind,
        string? DocumentNo,
        string WorkCentreCode,
        string OperationCode);

    private static class DownstreamExecutionKinds
    {
        public const string DailyProduction = "DAILY_PRODUCTION";
        public const string MaterialIssue = "MATERIAL_ISSUE";
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
