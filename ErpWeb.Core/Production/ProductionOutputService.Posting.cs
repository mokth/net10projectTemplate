using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionOutputService
{
    public async Task<IvMasterOperationResult<ProductionOutputDetail>> PostAsync(
        long outputId, CancellationToken cancellationToken = default)
    {
        if (!await CanAccessAsync(PermissionCodes.Post, cancellationToken))
            return Fail("Access denied.", IvMasterErrorCode.AccessDenied);
        var scope = WriteScope();
        if (scope is null) return Fail("A company, branch and user scope is required.", IvMasterErrorCode.InvalidScope);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var output = await LockOutputAsync(db, scope.CompanyCode, scope.BranchCode!, outputId, cancellationToken);
            if (output is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
            if (output.Status == ProductionOutputStatuses.Posted)
                return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
            if (output.Status != ProductionOutputStatuses.New)
                return Fail("Only NEW drafts can be posted.");

            var link = await LockOutputPostLinkAsync(db, scope.CompanyCode, scope.BranchCode!, output.PostingRequestId, cancellationToken);
            if (link is null) return Fail("Posting link was not found.");
            if (link.Status == ProductionPostingLinkStatuses.Succeeded)
                return Ok(await MapDetailAsync(db, output.Uid, cancellationToken));
            if (link.Status != ProductionPostingLinkStatuses.Draft)
                return Fail("Posting link is not in DRAFT status.");

            var order = await LockWorkOrderAsync(db, output.WorkOrderId, cancellationToken);
            if (order is null) return Fail("Work Order was not found.", IvMasterErrorCode.NotFound);
            if (order.Status is not (ProductionWorkOrderStatuses.Released or ProductionWorkOrderStatuses.InProgress))
                return Fail("Work Order must be RELEASED or IN_PROGRESS.");
            if (order.SnapshotRevision != output.SnapshotRevision
                || !string.Equals(order.SnapshotHash, output.SnapshotHash, StringComparison.Ordinal))
                return Fail("Work Order snapshot changed; recreate the Daily Production draft.");
            if (order.SnapshotHashVersion < ProductionSnapshotHashVersions.Current)
                return Fail("Work Order snapshot hash version must be refreshed to V3 before Daily Production.");

            var routeStep = await LockRouteStepAsync(db, output.RouteStepId, cancellationToken);
            var operation = await LockOperationAsync(db, output.WorkOrderOperationId, cancellationToken);
            if (routeStep is null || operation is null)
                return Fail("Route step or operation was not found.");
            if (string.IsNullOrWhiteSpace(routeStep.OutputType))
                return Fail("Route OutputType is null; refresh the Work Order.");
            if (routeStep.YieldPercent is not null and not 100m)
                return Fail("YieldPercent must be 100 for this milestone.");

            var processed = IvQty.Round(output.GoodQty + output.ScrapQty + output.RejectQty + output.HoldQty);
            var operationRemaining = IvQty.Round(operation.PlannedOutputQty - operation.GoodQty);
            if (output.GoodQty > operationRemaining)
                return Fail($"Good qty {output.GoodQty} exceeds operation remaining {operationRemaining}.");

            var isFinalFg = operation.IsFinalOperation
                && string.Equals(routeStep.OutputType, PrRouteOutputTypes.FinishedGoods, StringComparison.OrdinalIgnoreCase);
            decimal goodForWo = 0m, scrapForWo = 0m, rejectForWo = 0m, holdForWo = 0m;
            if (isFinalFg)
            {
                if (!string.Equals(routeStep.OutputBaseUom, order.OutputUom, StringComparison.OrdinalIgnoreCase))
                    return Fail("Terminal FG OutputBaseUom must equal Work Order OutputUom.");
                var factor = routeStep.OutputConversionFactorToBase ?? 0m;
                if (factor <= 0m) return Fail("Route OutputConversionFactorToBase is missing.");
                goodForWo = IvQty.Round(output.GoodQty * factor);
                scrapForWo = IvQty.Round(output.ScrapQty * factor);
                rejectForWo = IvQty.Round(output.RejectQty * factor);
                holdForWo = IvQty.Round(output.HoldQty * factor);
                var woRemaining = ProductionWorkOrderCalc.OpenProductionQty(
                    order.PlannedQty, order.GoodQty, order.ApprovedVarianceQty);
                if (goodForWo > woRemaining)
                    return Fail($"Good qty for Work Order ({goodForWo}) exceeds remaining {woRemaining}.");
            }

            var materials = await db.ProductionWorkOrderMaterials
                .Where(x => x.WorkOrderOperationId == operation.Uid)
                .OrderBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            foreach (var materialId in materials.Select(x => x.Uid))
                await LockMaterialAsync(db, materialId, cancellationToken);

            var now = _clock.Now;
            var user = TruncateUser(scope.UserId);
            var consumeFacts = new List<(ProductionWorkOrderMaterial Material, ProductionBalLot Lot, decimal Qty, decimal BaseQty, decimal Cost)>();

            foreach (var material in materials)
            {
                var required = ProductionMaterialExecutionCalc.RequestedForProductionQty(
                    material.RequiredQty, operation.PlannedOutputQty, processed);
                if (required <= 0m) continue;

                if (string.Equals(material.IssueMethod, PrMaterialIssueMethods.Backflush, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(material.IssueMethod, PrMaterialIssueMethods.PickList, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(material.SupplySource, PrMaterialSupplySources.SeparateProductDefinition, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail($"{material.IssueMethod}/{material.SupplySource} is not supported in this milestone.");
                }

                var requiredBase = IvQty.Round(required * material.ConversionFactorToBase);
                List<ProductionBalLot> lots;
                if (string.Equals(material.SupplySource, PrMaterialSupplySources.InternalRouteWip, StringComparison.OrdinalIgnoreCase))
                {
                    if (material.ProducingRouteStepId is null)
                        return Fail($"Material {material.ComponentCode} has no producing route step.");
                    lots = await LockBalLotsAsync(db, scope.CompanyCode, scope.BranchCode!,
                        ProductionBalLotKinds.Wip, order.Uid, material.ProducingRouteStepId, material.ComponentCode,
                        cancellationToken);
                }
                else if (string.Equals(material.IssueMethod, PrMaterialIssueMethods.Manual, StringComparison.OrdinalIgnoreCase)
                         && (material.SupplySource is PrMaterialSupplySources.Purchased or PrMaterialSupplySources.ExternalSupply))
                {
                    lots = await LockBalLotsForMaterialAsync(db, material.Uid, cancellationToken);
                }
                else
                {
                    return Fail($"Unsupported supply for {material.ComponentCode}.");
                }

                lots = lots
                    .Where(x => x.BaseQty > 0m)
                    .OrderBy(x => x.LastMovementDate ?? DateTime.MinValue)
                    .ThenBy(x => x.LotNo, StringComparer.Ordinal)
                    .ThenBy(x => x.Uid)
                    .ToList();

                var remainingBase = requiredBase;
                foreach (var lot in lots)
                {
                    if (remainingBase <= 0m) break;
                    if (lot.LastMovementDate.HasValue && lot.LastMovementDate.Value > output.ProductionDate)
                        return Fail($"Lot {lot.LotNo} has a future LastMovementDate.");

                    var takeBase = Math.Min(lot.BaseQty, remainingBase);
                    var takeQty = material.ConversionFactorToBase > 0m
                        ? IvQty.Round(takeBase / material.ConversionFactorToBase)
                        : takeBase;
                    var takeCost = lot.BaseQty > 0m
                        ? IvQty.Round(lot.TotalCost * (takeBase / lot.BaseQty))
                        : 0m;

                    consumeFacts.Add((material, lot, takeQty, takeBase, takeCost));
                    remainingBase = IvQty.Round(remainingBase - takeBase);
                }

                if (remainingBase > 0m)
                    return Fail($"Insufficient production balance for {material.ComponentCode}.");
            }

            link.Status = ProductionPostingLinkStatuses.Pending;
            await db.SaveChangesAsync(cancellationToken);

            // Apply consumption
            foreach (var fact in consumeFacts.OrderBy(x => x.Lot.Uid))
            {
                var lot = fact.Lot;
                lot.Qty = IvQty.Round(lot.Qty - fact.Qty);
                lot.BaseQty = IvQty.Round(lot.BaseQty - fact.BaseQty);
                lot.TotalCost = IvQty.Round(lot.TotalCost - fact.Cost);
                lot.AverageUnitCost = lot.BaseQty > 0m ? IvQty.Round(lot.TotalCost / lot.BaseQty) : 0m;
                lot.LastMovementDate = output.ProductionDate;

                var balMov = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.Consume,
                    Qty = fact.Qty,
                    Uom = fact.Material.RequiredUom ?? lot.Uom,
                    BaseQty = fact.BaseQty,
                    BaseUom = lot.BaseUom,
                    UnitCost = fact.BaseQty > 0m ? IvQty.Round(fact.Cost / fact.BaseQty) : 0m,
                    TotalCost = fact.Cost,
                    WorkOrderId = order.Uid,
                    WorkOrderMaterialId = fact.Material.Uid,
                    WorkOrderOperationId = operation.Uid,
                    RouteStepId = routeStep.Uid,
                    ProductionOutputId = output.Uid,
                    PostingLinkId = link.Uid,
                    DocumentType = ProductionDocumentTypes.ProductionOutput,
                    DocumentNo = output.DocumentNo,
                    MovementDate = output.ProductionDate,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionBalLotMovements.Add(balMov);
                await db.SaveChangesAsync(cancellationToken);

                db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
                {
                    CompanyCode = scope.CompanyCode,
                    BranchCode = scope.BranchCode!,
                    WorkOrderId = order.Uid,
                    WorkOrderMaterialId = fact.Material.Uid,
                    WorkOrderOperationId = operation.Uid,
                    MovementType = ProductionMaterialMovementTypes.Consume,
                    MovementDate = output.ProductionDate,
                    ItemCode = fact.Material.ComponentCode,
                    Qty = fact.Qty,
                    Uom = fact.Material.RequiredUom ?? lot.Uom,
                    BaseQty = fact.BaseQty,
                    BaseUom = lot.BaseUom,
                    ConversionFactorToBase = fact.Material.ConversionFactorToBase,
                    WarehouseCode = lot.WarehouseCode ?? string.Empty,
                    LocationCode = lot.LocationCode ?? string.Empty,
                    LotNo = lot.LotNo ?? string.Empty,
                    FromBalLocId = lot.SourceIvBalLocId,
                    ItemStatus = string.Empty,
                    UnitCost = balMov.UnitCost,
                    TotalCost = fact.Cost,
                    PostingLinkId = link.Uid,
                    OriginalMovementId = lot.Kind == ProductionBalLotKinds.MaterialIn
                        ? lot.OriginalIssueMovementId
                        : null,
                    ProductionBalLotId = lot.Uid,
                    ProductionBalLotMovementId = balMov.Uid,
                    ProductionOutputId = output.Uid,
                    CreatedDate = now,
                    CreatedBy = user,
                });
            }

            // Produce WIP/FG staging when final + GoodQty > 0 + stocked/FG
            if (operation.IsFinalOperation
                && output.GoodQty > 0m
                && routeStep.OutputType is PrRouteOutputTypes.WipStocked or PrRouteOutputTypes.FinishedGoods)
            {
                var factor = routeStep.OutputConversionFactorToBase ?? 1m;
                var produceBase = IvQty.Round(output.GoodQty * factor);
                var wipLot = await LockOrCreateWipLotAsync(
                    db, scope, order, routeStep, operation, output, produceBase, now, user, cancellationToken);
                var produceMov = new ProductionBalLotMovement
                {
                    ProductionBalLotId = wipLot.Uid,
                    MovementType = ProductionBalLotMovementTypes.Produce,
                    Qty = output.GoodQty,
                    Uom = output.OutputUom,
                    BaseQty = produceBase,
                    BaseUom = routeStep.OutputBaseUom ?? output.OutputUom,
                    UnitCost = 0m,
                    TotalCost = 0m,
                    WorkOrderId = order.Uid,
                    WorkOrderOperationId = operation.Uid,
                    RouteStepId = routeStep.Uid,
                    ProductionOutputId = output.Uid,
                    PostingLinkId = link.Uid,
                    DocumentType = ProductionDocumentTypes.ProductionOutput,
                    DocumentNo = output.DocumentNo,
                    MovementDate = output.ProductionDate,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.ProductionBalLotMovements.Add(produceMov);
            }

            operation.GoodQty = IvQty.Round(operation.GoodQty + output.GoodQty);
            operation.ScrapQty = IvQty.Round(operation.ScrapQty + output.ScrapQty);
            operation.RejectQty = IvQty.Round(operation.RejectQty + output.RejectQty);
            operation.HoldQty = IvQty.Round(operation.HoldQty + output.HoldQty);
            operation.ProcessedQty = IvQty.Round(
                operation.GoodQty + operation.ScrapQty + operation.RejectQty + operation.HoldQty);
            operation.RemainingQty = IvQty.Round(Math.Max(operation.PlannedOutputQty - operation.GoodQty, 0m));

            foreach (var material in materials)
            {
                var facts = await db.ProductionMaterialMovements.AsNoTracking()
                    .Where(x => x.WorkOrderMaterialId == material.Uid)
                    .Select(x => new { x.MovementType, x.Qty })
                    .ToListAsync(cancellationToken);
                material.ConsumedQty = ProductionMaterialMovementTotals.EffectiveConsumed(
                    facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.Consume).Sum(x => x.Qty),
                    facts.Where(x => x.MovementType == ProductionMaterialMovementTypes.ConsumeReversal).Sum(x => x.Qty));
            }

            if (isFinalFg)
            {
                order.GoodQty = IvQty.Round(order.GoodQty + goodForWo);
                order.ScrapQty = IvQty.Round(order.ScrapQty + scrapForWo);
                order.RejectQty = IvQty.Round(order.RejectQty + rejectForWo);
                order.HoldQty = IvQty.Round(order.HoldQty + holdForWo);
                order.RemainingQty = ProductionWorkOrderCalc.OpenProductionQty(
                    order.PlannedQty, order.GoodQty, order.ApprovedVarianceQty);
            }

            var fromStatus = order.Status;
            if (order.Status == ProductionWorkOrderStatuses.Released)
                order.Status = ProductionWorkOrderStatuses.InProgress;

            output.Status = ProductionOutputStatuses.Posted;
            output.PostedDate = now;
            output.PostedBy = user;
            link.Status = ProductionPostingLinkStatuses.Succeeded;
            link.ResultCode = "OK";
            link.ResultMessage = $"Posted {output.DocumentNo}.";
            link.CompletedDate = now;

            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.OutputPosted,
                FromStatus = fromStatus,
                ToStatus = order.Status,
                SnapshotRevision = order.SnapshotRevision,
                Reason = $"Daily Production {output.DocumentNo}",
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
            return Fail("Posting conflicted with another change; reload and retry.", IvMasterErrorCode.Concurrency);
        }
    }

    private static async Task<ProductionBalLot> LockOrCreateWipLotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep routeStep,
        ProductionWorkOrderOperation operation,
        ProductionOutput output,
        decimal produceBase,
        DateTime now,
        string user,
        CancellationToken ct)
    {
        var existing = await db.ProductionBalLots
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Kind == ProductionBalLotKinds.Wip
                && x.WorkOrderId == order.Uid
                && x.ProducingRouteStepId == routeStep.Uid
                && x.ItemCode == output.OutputItemCode
                && x.LotNo == output.OutputLotNo)
            .OrderBy(x => x.Uid)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            if (db.Database.IsSqlServer())
            {
                existing = await db.ProductionBalLots
                    .FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK, HOLDLOCK) WHERE UID={existing.Uid}")
                    .SingleAsync(ct);
            }
            existing.Qty = IvQty.Round(existing.Qty + output.GoodQty);
            existing.BaseQty = IvQty.Round(existing.BaseQty + produceBase);
            existing.LastMovementDate = output.ProductionDate;
            return existing;
        }

        var lot = new ProductionBalLot
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = output.OutputItemCode,
            Qty = output.GoodQty,
            Uom = output.OutputUom,
            BaseQty = produceBase,
            BaseUom = routeStep.OutputBaseUom ?? output.OutputUom,
            ConversionFactorToBase = routeStep.OutputConversionFactorToBase ?? 1m,
            TotalCost = 0m,
            AverageUnitCost = 0m,
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            ProducingRouteStepId = routeStep.Uid,
            WorkOrderOperationId = operation.Uid,
            OutputType = routeStep.OutputType,
            WorkCentreCode = routeStep.WorkCentreCode,
            ProcessCode = operation.OperationCode,
            LotNo = output.OutputLotNo,
            LastMovementDate = output.ProductionDate,
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync(ct);
        return lot;
    }

    private static async Task<List<ProductionBalLot>> LockBalLotsForMaterialAsync(
        AppDbContext db, long materialId, CancellationToken ct)
    {
        var ids = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.Kind == ProductionBalLotKinds.MaterialIn && x.WorkOrderMaterialId == materialId && x.BaseQty > 0m)
            .OrderBy(x => x.Uid).Select(x => x.Uid).ToListAsync(ct);
        return await LockBalLotsByIdsAsync(db, ids, ct);
    }

    private static async Task<List<ProductionBalLot>> LockBalLotsAsync(
        AppDbContext db, string company, string branch, string kind, long workOrderId,
        long? producingRouteStepId, string itemCode, CancellationToken ct)
    {
        var q = db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && x.Kind == kind
                && x.WorkOrderId == workOrderId && x.ItemCode == itemCode && x.BaseQty > 0m);
        if (producingRouteStepId.HasValue)
            q = q.Where(x => x.ProducingRouteStepId == producingRouteStepId);
        var ids = await q.OrderBy(x => x.Uid).Select(x => x.Uid).ToListAsync(ct);
        return await LockBalLotsByIdsAsync(db, ids, ct);
    }

    private static async Task<List<ProductionBalLot>> LockBalLotsByIdsAsync(
        AppDbContext db, IReadOnlyList<long> ids, CancellationToken ct)
    {
        var result = new List<ProductionBalLot>();
        foreach (var id in ids.OrderBy(x => x))
        {
            ProductionBalLot? lot;
            if (db.Database.IsSqlServer())
            {
                lot = await db.ProductionBalLots
                    .FromSqlInterpolated($@"SELECT * FROM dbo.PrProductionBalLot WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}")
                    .SingleOrDefaultAsync(ct);
            }
            else
            {
                lot = await db.ProductionBalLots.SingleOrDefaultAsync(x => x.Uid == id, ct);
            }
            if (lot is not null) result.Add(lot);
        }
        return result;
    }

    private static async Task<ProductionOutput?> LockOutputAsync(
        AppDbContext db, string company, string branch, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionOutputs.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrProductionOutput WITH (UPDLOCK, HOLDLOCK) WHERE UID={id} AND CompanyCode={company} AND BranchCode={branch}")
                .SingleOrDefaultAsync(ct)
            : await db.ProductionOutputs.SingleOrDefaultAsync(x =>
                x.Uid == id && x.CompanyCode == company && x.BranchCode == branch, ct);

    private static async Task<ProductionPostingLink?> LockOutputPostLinkAsync(
        AppDbContext db, string company, string branch, string requestId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionPostingLinks.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrProductionPostingLink WITH (UPDLOCK, HOLDLOCK) WHERE CompanyCode={company} AND BranchCode={branch} AND CommandType={ProductionPostingCommandTypes.OutputPost} AND PostingRequestId={requestId}")
                .FirstOrDefaultAsync(ct)
            : await db.ProductionPostingLinks.FirstOrDefaultAsync(x =>
                x.CompanyCode == company && x.BranchCode == branch
                && x.CommandType == ProductionPostingCommandTypes.OutputPost
                && x.PostingRequestId == requestId, ct);

    private static async Task<ProductionWorkOrder?> LockWorkOrderAsync(AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrders.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static async Task<ProductionWorkOrderRouteStep?> LockRouteStepAsync(AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderRouteSteps.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrderRouteStep WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrderRouteSteps.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static async Task<ProductionWorkOrderOperation?> LockOperationAsync(AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderOperations.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrderOperation WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrderOperations.SingleOrDefaultAsync(x => x.Uid == id, ct);

    private static async Task<ProductionWorkOrderMaterial?> LockMaterialAsync(AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderMaterials.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrderMaterial WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrderMaterials.SingleOrDefaultAsync(x => x.Uid == id, ct);
}
