using ErpWeb.Core.Inventory;
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
            var replay = await LockOutputRollbackLinkAsync(
                db, scope.CompanyCode, scope.BranchCode!, request.PostingRequestId, cancellationToken);
            if (replay is not null)
            {
                if (replay.Status == ProductionPostingLinkStatuses.Succeeded
                    && replay.ProductionDocumentLineId.HasValue)
                {
                    await tx.CommitAsync(cancellationToken);
                    return Ok(await MapDetailAsync(db, replay.ProductionDocumentLineId.Value, cancellationToken));
                }

                return Fail("This rollback request is already being processed.", IvMasterErrorCode.Concurrency);
            }

            var output = await LockOutputAsync(db, scope.CompanyCode, scope.BranchCode!, request.OutputId, cancellationToken);
            if (output is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
            if (output.Status == ProductionOutputStatuses.Reversed)
                return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
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

            // Prefix-balance guard for PRODUCE before mutating.
            var produceMovements = await db.ProductionBalLotMovements
                .Where(x => x.ProductionOutputId == output.Uid
                    && x.MovementType == ProductionBalLotMovementTypes.Produce
                    && x.PostingLinkId == postLink.Uid)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);

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
            foreach (var consume in consumeMaterial.OrderBy(x => x.ProductionBalLotId ?? 0).ThenBy(x => x.Uid))
            {
                if (consume.ProductionBalLotId is null || consume.ProductionBalLotMovementId is null)
                    return Fail("Consume fact is missing production balance lineage.");

                var lot = await LockBalLotByIdAsync(db, consume.ProductionBalLotId.Value, cancellationToken);
                if (lot is null) return Fail("Production balance lot was not found.");

                var originalBal = balById.GetValueOrDefault(consume.ProductionBalLotMovementId.Value);
                if (originalBal is null) return Fail("Bal-lot consume movement was not found.");

                lot.Qty = IvQty.Round(lot.Qty + consume.Qty);
                lot.BaseQty = IvQty.Round(lot.BaseQty + consume.BaseQty);
                lot.TotalCost = IvQty.Round(lot.TotalCost + consume.TotalCost);
                lot.AverageUnitCost = lot.BaseQty > 0m ? IvQty.Round(lot.TotalCost / lot.BaseQty) : 0m;

                var balRev = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.ConsumeReversal,
                    Qty = consume.Qty,
                    Uom = consume.Uom,
                    BaseQty = consume.BaseQty,
                    BaseUom = consume.BaseUom,
                    UnitCost = consume.UnitCost,
                    TotalCost = consume.TotalCost,
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
                // Include the pending reversal in-memory for LastMovementDate.
                allLotMovements.Add(balRev);
                lot.LastMovementDate = ProductionBalLotSignedQty.LatestEffectiveMovementDate(allLotMovements);
            }

            foreach (var produce in produceMovements.OrderBy(x => x.ProductionBalLotId).ThenBy(x => x.Uid))
            {
                var lot = await LockBalLotByIdAsync(db, produce.ProductionBalLotId, cancellationToken);
                if (lot is null) return Fail("WIP production balance lot was not found.");

                lot.Qty = IvQty.Round(Math.Max(lot.Qty - produce.Qty, 0m));
                lot.BaseQty = IvQty.Round(Math.Max(lot.BaseQty - produce.BaseQty, 0m));
                lot.TotalCost = 0m;
                lot.AverageUnitCost = 0m;

                var produceRev = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.ProduceReversal,
                    Qty = produce.Qty,
                    Uom = produce.Uom,
                    BaseQty = produce.BaseQty,
                    BaseUom = produce.BaseUom,
                    UnitCost = 0m,
                    TotalCost = 0m,
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

            operation.GoodQty = IvQty.Round(Math.Max(operation.GoodQty - output.GoodQty, 0m));
            operation.ScrapQty = IvQty.Round(Math.Max(operation.ScrapQty - output.ScrapQty, 0m));
            operation.RejectQty = IvQty.Round(Math.Max(operation.RejectQty - output.RejectQty, 0m));
            operation.HoldQty = IvQty.Round(Math.Max(operation.HoldQty - output.HoldQty, 0m));
            operation.ProcessedQty = IvQty.Round(
                operation.GoodQty + operation.ScrapQty + operation.RejectQty + operation.HoldQty);
            operation.RemainingQty = IvQty.Round(Math.Max(operation.PlannedOutputQty - operation.GoodQty, 0m));

            var isFinalFg = operation.IsFinalOperation
                && string.Equals(routeStep.OutputType, PrRouteOutputTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase);
            if (isFinalFg)
            {
                var factor = routeStep.OutputConversionFactorToBase ?? 1m;
                order.GoodQty = IvQty.Round(Math.Max(order.GoodQty - IvQty.Round(output.GoodQty * factor), 0m));
                order.ScrapQty = IvQty.Round(Math.Max(order.ScrapQty - IvQty.Round(output.ScrapQty * factor), 0m));
                order.RejectQty = IvQty.Round(Math.Max(order.RejectQty - IvQty.Round(output.RejectQty * factor), 0m));
                order.HoldQty = IvQty.Round(Math.Max(order.HoldQty - IvQty.Round(output.HoldQty * factor), 0m));
                order.RemainingQty = ProductionWorkOrderCalc.OpenProductionQty(
                    order.PlannedQty, order.GoodQty, order.ApprovedVarianceQty);
            }

            foreach (var material in materials)
            {
                var facts = await db.ProductionMaterialMovements.AsNoTracking()
                    .Where(x => x.WorkOrderMaterialId == material.Uid)
                    .Select(x => new { x.MovementType, x.Qty })
                    .ToListAsync(cancellationToken);
                var pendingConsumeReversal = consumeMaterial
                    .Where(x => x.WorkOrderMaterialId == material.Uid)
                    .Sum(x => x.Qty);
                material.ConsumedQty = ProductionMaterialMovementTotals.EffectiveConsumed(
                    facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty),
                    facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal).Sum(x => x.Qty)
                        + pendingConsumeReversal);
            }

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
            await tx.CommitAsync(cancellationToken);
            return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
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

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max];
}
