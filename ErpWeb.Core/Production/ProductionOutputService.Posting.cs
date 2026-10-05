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
            await new ErpWeb.Core.StockLedger.BranchStockTransactionLock().AcquireAsync(db, scope.CompanyCode, scope.BranchCode!, cancellationToken);
            var output = await LockOutputAsync(db, scope.CompanyCode, scope.BranchCode!, outputId, cancellationToken);
            if (output is null) return Fail("Production output was not found.", IvMasterErrorCode.NotFound);
            if (output.Status == ProductionOutputStatuses.Posted)
                return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
            if (output.Status != ProductionOutputStatuses.New)
                return Fail("Only NEW drafts can be posted.");
            StockPostingContext? ledgerContext = null;

            var link = await LockOutputPostLinkAsync(db, scope.CompanyCode, scope.BranchCode!, output.PostingRequestId, cancellationToken);
            if (link is null) return Fail("Posting link was not found.");
            if (link.Status == ProductionPostingLinkStatuses.Succeeded)
                return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
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

            // The Work Order lock serializes Daily Production posts and rollbacks for this order.
            // Lock the complete execution graph in deterministic routing order before evaluating it,
            // so a predecessor rollback cannot invalidate this decision before commit.
            var sequenceRouteSteps = await LockRouteStepsForWorkOrderAsync(db, order.Uid, cancellationToken);
            var sequenceOperations = await LockOperationsForWorkOrderAsync(db, order.Uid, cancellationToken);
            var sequence = _operationEligibility.Evaluate(
                operation, sequenceRouteSteps, sequenceOperations);
            if (!sequence.IsEligible)
                return Fail(sequence.BlockingReason!);

            if (_stockCoordinator is not null)
            {
                var command = BuildOutputPostingCommand(
                    output, reversal: false, output.ProductionDate, null, null);
                var ledger = await _stockCoordinator.BeginInTransactionAsync(db, command, cancellationToken);
                if (ledger.Error is not null) return Fail(ledger.Error.Message);
                ledgerContext = ledger.Context;
            }

            var siblings = await db.ProductionWorkOrderOperations
                .Where(x => x.RouteStepId == routeStep.Uid)
                .OrderBy(x => x.ProcessSequence)
                .ThenBy(x => x.Uid)
                .ToListAsync(cancellationToken);
            if (ProductionProcessHandoff.ValidateFinalIsLast(siblings) is { } finalError)
                return Fail(finalError);

            var processed = IvQty.Round(output.GoodQty + output.ScrapQty + output.RejectQty + output.HoldQty);
            var operationRemaining = IvQty.Round(operation.PlannedOutputQty - operation.GoodQty);
            if (output.GoodQty > operationRemaining)
                return Fail($"Good qty {output.GoodQty} exceeds operation remaining {operationRemaining}.");

            var priorError = ProductionProcessHandoff.TryGetImmediatePrior(siblings, operation, out var priorOperation);
            if (priorError is not null) return Fail(priorError);
            var nextError = ProductionProcessHandoff.TryGetImmediateNext(siblings, operation, out var nextOperation);
            if (nextError is not null) return Fail(nextError);

            var producesHandoff = ProductionProcessHandoff.IsHandoffProducer(operation, nextOperation);
            ProductionProcessHandoff.QtyContract? producerContract = null;
            if (producesHandoff && output.GoodQty > 0m)
            {
                if (ProductionProcessHandoff.ResolveProducerContract(operation, routeStep, out producerContract) is { } producerUomError)
                    return Fail(producerUomError);
            }

            ProductionProcessHandoff.QtyContract? consumerContract = null;
            ProductionBalLot? handoffLot = null;
            decimal handoffConsumeQty = 0m;
            decimal handoffConsumeBase = 0m;
            if (priorOperation is not null)
            {
                var priorContractError = ProductionProcessHandoff.ResolveProducerContract(
                    priorOperation, routeStep, out var priorContract);
                if (priorContractError is not null) return Fail(priorContractError);
                if (priorContract is null) return Fail("Previous-process handoff UOM could not be resolved.");

                if (ProductionProcessHandoff.ResolveConsumerContract(
                        operation, routeStep, priorContract.Uom, out consumerContract) is { } consumerUomError)
                    return Fail(consumerUomError);
                if (consumerContract is null) return Fail("Process handoff UOM could not be resolved.");

                handoffConsumeQty = processed;
                if (handoffConsumeQty > 0m)
                {
                    handoffConsumeBase = ProductionProcessHandoff.ToBaseQty(
                        handoffConsumeQty, consumerContract.ConversionFactorToBase);
                    handoffLot = await LockHandoffLotAsync(
                        db, scope.CompanyCode, scope.BranchCode!, order.Uid, priorOperation.Uid,
                        routeStep.OutputItemCode, cancellationToken);
                    if (handoffLot is null || handoffLot.BaseQty <= 0m)
                        return Fail($"Insufficient previous-process balance for {routeStep.OutputItemCode}.");
                    if (handoffLot.LastMovementDate.HasValue
                        && handoffLot.LastMovementDate.Value > output.ProductionDate)
                        return Fail($"Lot {handoffLot.LotNo} has a future LastMovementDate.");
                    if (handoffConsumeBase > handoffLot.BaseQty)
                        return Fail($"Insufficient previous-process balance for {routeStep.OutputItemCode}.");
                }
            }

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

            var outputFacts = await db.ProductionOutputMaterials
                .Where(x => x.ProductionOutputId == output.Uid)
                .ToListAsync(cancellationToken);
            foreach (var material in materials)
            {
                var standard = ProductionMaterialExecutionCalc.DailyProductionStandardQty(
                    material.RequiredQty, operation.PlannedOutputQty, processed);
                var max = ProductionMaterialExecutionCalc.DailyProductionMaxQty(standard, material.Tolerance);
                var fact = outputFacts.SingleOrDefault(x => x.WorkOrderMaterialId == material.Uid);
                if (fact is null)
                    return Fail($"Daily Production material fact is missing for {material.ComponentCode}.");
                if (fact.StandardQty != standard)
                    return Fail($"Saved standard for {material.ComponentCode} no longer matches the Work Order snapshot.");
                var support = ProductionOutputMaterialSupport.BlockingReason(material);
                if (standard > 0m && support is not null)
                    return Fail(support);
                var consumeError = ProductionOutputMaterialFacts.ValidateConsume(
                    fact.ConsumeQty, standard, max, fact.VarianceReasonCode, fact.VarianceReasonText, interactive: false);
                if (consumeError is not null)
                    return Fail($"{material.ComponentCode}: {consumeError}");
            }

            var now = _clock.Now;
            var user = TruncateUser(scope.UserId);
            var consumeFacts = new List<(
                ProductionWorkOrderMaterial Material,
                ProductionBalLot Lot,
                decimal MaterialQty,
                decimal LotQty,
                decimal BaseQty)>();
            var remainingBaseByLot = new Dictionary<long, decimal>();

            foreach (var material in materials)
            {
                var fact = outputFacts.Single(x => x.WorkOrderMaterialId == material.Uid);
                var required = fact.ConsumeQty;
                if (required <= 0m) continue;

                var support = ProductionOutputMaterialSupport.BlockingReason(material);
                if (support is not null)
                    return Fail(support);

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

                    var availableBase = remainingBaseByLot.GetValueOrDefault(lot.Uid, lot.BaseQty);
                    if (availableBase <= 0m) continue;
                    var takeBase = Math.Min(availableBase, remainingBase);
                    if (IvQty.Round(takeBase) <= 0m) continue;
                    var materialQty = material.ConversionFactorToBase > 0m
                        ? IvQty.Round(takeBase / material.ConversionFactorToBase)
                        : takeBase;
                    var lotQty = lot.ConversionFactorToBase > 0m
                        ? IvQty.Round(takeBase / lot.ConversionFactorToBase)
                        : takeBase;
                    if (materialQty <= 0m || lotQty <= 0m)
                        return Fail($"The allocation for {material.ComponentCode} rounds to zero in its source UOM.");
                    consumeFacts.Add((material, lot, materialQty, lotQty, takeBase));
                    remainingBaseByLot[lot.Uid] = IvQty.Round(availableBase - takeBase);
                    remainingBase = IvQty.Round(remainingBase - takeBase);
                }

                if (remainingBase > 0m)
                    return Fail($"Insufficient production balance for {material.ComponentCode}.");
            }

            link.Status = ProductionPostingLinkStatuses.Pending;
            await db.SaveChangesAsync(cancellationToken);

            // Apply consumption
            var totalConsumedCost = 0m;
            foreach (var fact in consumeFacts.OrderBy(x => x.Lot.Uid))
            {
                var lot = fact.Lot;
                // A final depletion receives the exact remaining cost. This prevents rounded
                // proportional splits from leaving value stranded in an empty production pile.
                var takeCost = IvQty.Round(lot.BaseQty - fact.BaseQty) <= 0m
                    ? lot.TotalCost
                    : IvQty.Round(lot.TotalCost * (fact.BaseQty / lot.BaseQty));
                lot.Qty = IvQty.Round(lot.Qty - fact.LotQty);
                lot.BaseQty = IvQty.Round(lot.BaseQty - fact.BaseQty);
                lot.TotalCost = IvQty.Round(lot.TotalCost - takeCost);
                lot.AverageUnitCost = lot.BaseQty > 0m ? IvQty.Round(lot.TotalCost / lot.BaseQty) : 0m;
                lot.LastMovementDate = output.ProductionDate;
                totalConsumedCost = IvQty.Round(totalConsumedCost + takeCost);

                var balMov = new ProductionBalLotMovement
                {
                    ProductionBalLotId = lot.Uid,
                    MovementType = ProductionBalLotMovementTypes.Consume,
                    Qty = fact.LotQty,
                    Uom = lot.Uom,
                    BaseQty = fact.BaseQty,
                    BaseUom = lot.BaseUom,
                    UnitCost = fact.BaseQty > 0m ? IvQty.Round(takeCost / fact.BaseQty) : 0m,
                    TotalCost = takeCost,
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
                    Qty = fact.MaterialQty,
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
                    TotalCost = takeCost,
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

            if (handoffLot is not null && handoffConsumeQty > 0m && consumerContract is not null)
            {
                var handoffCost = IvQty.Round(handoffLot.BaseQty - handoffConsumeBase) <= 0m
                    ? handoffLot.TotalCost
                    : IvQty.Round(handoffLot.TotalCost * (handoffConsumeBase / handoffLot.BaseQty));
                handoffLot.Qty = IvQty.Round(handoffLot.Qty - handoffConsumeQty);
                handoffLot.BaseQty = IvQty.Round(handoffLot.BaseQty - handoffConsumeBase);
                handoffLot.TotalCost = IvQty.Round(handoffLot.TotalCost - handoffCost);
                handoffLot.AverageUnitCost = handoffLot.BaseQty > 0m
                    ? IvQty.Round(handoffLot.TotalCost / handoffLot.BaseQty)
                    : 0m;
                handoffLot.LastMovementDate = output.ProductionDate;
                totalConsumedCost = IvQty.Round(totalConsumedCost + handoffCost);

                db.ProductionBalLotMovements.Add(new ProductionBalLotMovement
                {
                    ProductionBalLotId = handoffLot.Uid,
                    MovementType = ProductionBalLotMovementTypes.Consume,
                    Qty = handoffConsumeQty,
                    Uom = consumerContract.Uom,
                    BaseQty = handoffConsumeBase,
                    BaseUom = consumerContract.BaseUom,
                    UnitCost = handoffConsumeBase > 0m ? IvQty.Round(handoffCost / handoffConsumeBase) : 0m,
                    TotalCost = handoffCost,
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
                });
            }

            if (producesHandoff && output.GoodQty > 0m && producerContract is not null)
            {
                var produceBase = ProductionProcessHandoff.ToBaseQty(
                    output.GoodQty, producerContract.ConversionFactorToBase);
                var createdOrUpdated = await LockOrCreateHandoffLotAsync(
                    db, scope, order, routeStep, operation, output, producerContract, produceBase,
                    totalConsumedCost, cancellationToken);
                if (createdOrUpdated.Error is not null)
                    return Fail(createdOrUpdated.Error);

                db.ProductionBalLotMovements.Add(new ProductionBalLotMovement
                {
                    ProductionBalLotId = createdOrUpdated.Lot!.Uid,
                    MovementType = ProductionBalLotMovementTypes.Produce,
                    Qty = output.GoodQty,
                    Uom = producerContract.Uom,
                    BaseQty = produceBase,
                    BaseUom = producerContract.BaseUom,
                    UnitCost = produceBase > 0m ? IvQty.Round(totalConsumedCost / produceBase) : 0m,
                    TotalCost = totalConsumedCost,
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
                    db, scope, order, routeStep, operation, output, produceBase, totalConsumedCost, now, user, cancellationToken);
                var produceMov = new ProductionBalLotMovement
                {
                    ProductionBalLotId = wipLot.Uid,
                    MovementType = ProductionBalLotMovementTypes.Produce,
                    Qty = output.GoodQty,
                    Uom = output.OutputUom,
                    BaseQty = produceBase,
                    BaseUom = routeStep.OutputBaseUom ?? output.OutputUom,
                    UnitCost = produceBase > 0m ? IvQty.Round(totalConsumedCost / produceBase) : 0m,
                    TotalCost = totalConsumedCost,
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

            // Material and balance movement facts must form one complete persisted set before
            // execution aggregates are projected from the database.
            await db.SaveChangesAsync(cancellationToken);

            operation.GoodQty = IvQty.Round(operation.GoodQty + output.GoodQty);
            operation.ScrapQty = IvQty.Round(operation.ScrapQty + output.ScrapQty);
            operation.RejectQty = IvQty.Round(operation.RejectQty + output.RejectQty);
            operation.HoldQty = IvQty.Round(operation.HoldQty + output.HoldQty);
            operation.ProcessedQty = IvQty.Round(
                operation.GoodQty + operation.ScrapQty + operation.RejectQty + operation.HoldQty);
            operation.RemainingQty = IvQty.Round(Math.Max(operation.PlannedOutputQty - operation.GoodQty, 0m));

            await RebuildWorkOrderMaterialExecutionProjectionAsync(
                db, materials.Select(x => x.Uid).ToList(), output.Uid,
                ProductionOutputProjectionTransition.IncludeCurrentPost, cancellationToken);

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
            if (ledgerContext is not null)
            {
                await StampOutputLedgerFactsAsync(
                    ledgerContext, output.Uid, link.Uid, order.WorkOrderNo, cancellationToken);
                await _stockCoordinator!.CompleteInTransactionAsync(ledgerContext, cancellationToken);
            }
            await tx.CommitAsync(cancellationToken);
            return Ok(await MapDetailAsync(db, scope, output.Uid, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail("Posting conflicted with another change; reload and retry.", IvMasterErrorCode.Concurrency);
        }
    }

    private static StockPostingCommand BuildOutputPostingCommand(
        ProductionOutput output,
        bool reversal,
        DateTime effectiveAt,
        string? reversalRequestId,
        long? reversesPostingId)
    {
        var requestId = Guid.ParseExact(
            reversal ? reversalRequestId! : output.PostingRequestId, "N");
        var evidence = StockPostingFingerprint.Create(
            new
            {
                output.Uid, output.DocumentNo, output.SnapshotRevision,
                output.GoodQty, output.ScrapQty, output.RejectQty, output.HoldQty,
                output.OutputItemCode, output.OutputUom, output.OutputLotNo,
                effectiveAt, reversal, reversesPostingId
            },
            new { output.WorkOrderId, output.WorkOrderOperationId, output.RouteStepId });
        return new StockPostingCommand
        {
            RequestId = requestId,
            CommandType = reversal ? "PRODUCTION_OUTPUT_ROLLBACK" : "PRODUCTION_OUTPUT_POST",
            SourceModule = "PRODUCTION",
            SourceDocumentType = ProductionDocumentTypes.ProductionOutput,
            SourceDocumentId = output.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceDocumentNo = output.DocumentNo,
            DocumentRevision = output.SnapshotRevision,
            PostingRole = reversal ? "REVERSAL" : "PRIMARY",
            EffectiveAt = effectiveAt,
            Evidence = evidence,
            ReversesPostingId = reversesPostingId,
        };
    }

    private static async Task StampOutputLedgerFactsAsync(
        StockPostingContext context,
        long outputId,
        long postingLinkId,
        string workOrderNo,
        CancellationToken cancellationToken)
    {
        var movements = await context.Db.ProductionBalLotMovements
            .Where(x => x.ProductionOutputId == outputId && x.PostingLinkId == postingLinkId)
            .OrderBy(x => x.Uid).ToListAsync(cancellationToken);
        var lots = await context.Db.ProductionBalLots
            .Where(x => movements.Select(m => m.ProductionBalLotId).Contains(x.Uid))
            .ToDictionaryAsync(x => x.Uid, cancellationToken);
        var line = 0;
        foreach (var movement in movements)
        {
            var lot = lots[movement.ProductionBalLotId];
            movement.LedgerVersion = 2;
            movement.LedgerEpochId = context.Epoch.Id;
            movement.StockPostingId = context.Posting.Id;
            movement.PostingLineNo = ++line;
            movement.CompanyCode = context.CompanyCode;
            movement.BranchCode = context.BranchCode;
            movement.ItemCode = lot.ItemCode;
            movement.ItemDescription = lot.Description;
            movement.BalanceStage = lot.BalanceStage ?? "PROCESS_WIP";
            movement.ProductionLocationId = lot.ProductionLocationId;
            movement.StockStatusCode = lot.StockStatusCode ?? "AVAILABLE";
            movement.WorkOrderNo = workOrderNo;
            movement.LotIdentity = lot.PoolCode ?? lot.LotNo;
            movement.PhysicalLotNo = lot.PhysicalLotNo;
            movement.ConversionFactorToBase = lot.ConversionFactorToBase;
            movement.SourceLineId = movement.WorkOrderMaterialId?.ToString()
                ?? movement.WorkOrderOperationId?.ToString()
                ?? movement.Uid.ToString();
            movement.SplitOrdinal = movements.Count(x => x.SourceLineId == movement.SourceLineId && x.Uid < movement.Uid);
            movement.ValuationStatus = "UNVALUED";
            lot.LastStockEventEffectiveAt = context.Posting.EffectiveAt;
        }
        var material = await context.Db.ProductionMaterialMovements
            .Where(x => x.ProductionOutputId == outputId && x.PostingLinkId == postingLinkId)
            .ToListAsync(cancellationToken);
        foreach (var movement in material)
        {
            movement.StockPostingId = context.Posting.Id;
            movement.SourceLineId = movement.WorkOrderMaterialId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            movement.SplitOrdinal = material.Count(x => x.WorkOrderMaterialId == movement.WorkOrderMaterialId
                && x.MovementType == movement.MovementType && x.Uid < movement.Uid);
            movement.MovementDate = context.Posting.EffectiveAt;
        }
        await context.Db.SaveChangesAsync(cancellationToken);
        await ProductionPoolValuationService.RecordAsync(context, movements, cancellationToken);
    }

    private static async Task<long> ResolveProductionLocationAsync(AppDbContext db, InventoryTenantScope scope, string workCentre, CancellationToken ct)
    {
        var code = workCentre.Length > 20 ? workCentre[..20] : workCentre;
        var location = await db.ProductionLocations.SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.Code == code, ct);
        if (location is null)
        {
            location = new ProductionLocation { CompanyCode = scope.CompanyCode, BranchCode = scope.BranchCode!, Code = code, Description = workCentre, WorkCentreCode = workCentre };
            db.ProductionLocations.Add(location); await db.SaveChangesAsync(ct);
        }
        if (!location.IsActive) throw new InvalidOperationException("Production location is inactive.");
        return location.Id;
    }

    private static async Task<(ProductionBalLot? Lot, string? Error)> LockOrCreateHandoffLotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep routeStep,
        ProductionWorkOrderOperation operation,
        ProductionOutput output,
        ProductionProcessHandoff.QtyContract contract,
        decimal produceBase,
        decimal producedCost,
        CancellationToken ct)
    {
        var lotNo = ProductionProcessHandoff.HandoffLotNo(operation.Uid);
        var existing = await db.ProductionBalLots
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Kind == ProductionBalLotKinds.Wip
                && x.WorkOrderId == order.Uid
                && x.ProducingRouteStepId == null
                && x.WorkOrderOperationId == operation.Uid
                && x.ItemCode == routeStep.OutputItemCode
                && x.LotNo == lotNo)
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

            if (existing.LastMovementDate.HasValue
                && output.ProductionDate < existing.LastMovementDate.Value)
            {
                return (null, $"Lot {existing.LotNo} has a future LastMovementDate.");
            }

            existing.Qty = IvQty.Round(existing.Qty + output.GoodQty);
            existing.BaseQty = IvQty.Round(existing.BaseQty + produceBase);
            existing.TotalCost = IvQty.Round(existing.TotalCost + producedCost);
            existing.AverageUnitCost = existing.BaseQty > 0m ? IvQty.Round(existing.TotalCost / existing.BaseQty) : 0m;
            existing.LastMovementDate = output.ProductionDate;
            return (existing, null);
        }

        var lot = new ProductionBalLot
        {
            CompanyCode = scope.CompanyCode,
            BranchCode = scope.BranchCode!,
            Kind = ProductionBalLotKinds.Wip,
            ItemCode = routeStep.OutputItemCode,
            Qty = output.GoodQty,
            Uom = contract.Uom,
            BaseQty = produceBase,
            BaseUom = contract.BaseUom,
            ConversionFactorToBase = contract.ConversionFactorToBase,
            TotalCost = producedCost,
            AverageUnitCost = produceBase > 0m ? IvQty.Round(producedCost / produceBase) : 0m,
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            ProducingRouteStepId = null,
            WorkOrderOperationId = operation.Uid,
            OutputType = routeStep.OutputType,
            WorkCentreCode = routeStep.WorkCentreCode,
            ProcessCode = operation.OperationCode,
            LotNo = lotNo,
            LastMovementDate = output.ProductionDate,
        };
        db.ProductionBalLots.Add(lot);
        await db.SaveChangesAsync(ct);
        return (lot, null);
    }

    private static async Task<ProductionBalLot?> LockHandoffLotAsync(
        AppDbContext db,
        string company,
        string branch,
        long workOrderId,
        long producingOperationId,
        string itemCode,
        CancellationToken ct)
    {
        var lotNo = ProductionProcessHandoff.HandoffLotNo(producingOperationId);
        var existing = await db.ProductionBalLots.AsNoTracking()
            .Where(x => x.CompanyCode == company
                && x.BranchCode == branch
                && x.Kind == ProductionBalLotKinds.Wip
                && x.WorkOrderId == workOrderId
                && x.ProducingRouteStepId == null
                && x.WorkOrderOperationId == producingOperationId
                && x.ItemCode == itemCode
                && x.LotNo == lotNo)
            .OrderBy(x => x.Uid)
            .Select(x => x.Uid)
            .FirstOrDefaultAsync(ct);
        if (existing <= 0) return null;

        var lots = await LockBalLotsByIdsAsync(db, [existing], ct);
        return lots.FirstOrDefault();
    }

    private static async Task<ProductionBalLot> LockOrCreateWipLotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep routeStep,
        ProductionWorkOrderOperation operation,
        ProductionOutput output,
        decimal produceBase,
        decimal producedCost,
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
            existing.TotalCost = IvQty.Round(existing.TotalCost + producedCost);
            existing.AverageUnitCost = existing.BaseQty > 0m ? IvQty.Round(existing.TotalCost / existing.BaseQty) : 0m;
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
            TotalCost = producedCost,
            AverageUnitCost = produceBase > 0m ? IvQty.Round(producedCost / produceBase) : 0m,
            WorkOrderId = order.Uid,
            WorkOrderNo = order.WorkOrderNo,
            BalanceStage = routeStep.OutputType == PrRouteOutputTypes.FinishedGoods ? "FG_STAGING" : "PROCESS_WIP",
            StockStatusCode = "AVAILABLE",
            OriginType = "PRODUCED",
            PhysicalLotNo = output.OutputLotNo,
            ProductionLocationId = await ResolveProductionLocationAsync(db, scope, routeStep.WorkCentreCode, ct),
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

    private static async Task<List<ProductionWorkOrderRouteStep>> LockRouteStepsForWorkOrderAsync(
        AppDbContext db, long workOrderId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderRouteSteps.FromSqlInterpolated(
                    $@"SELECT * FROM dbo.PrWorkOrderRouteStep WITH (UPDLOCK, HOLDLOCK) WHERE WorkOrderID={workOrderId} ORDER BY StageSequence, UID")
                .ToListAsync(ct)
            : await db.ProductionWorkOrderRouteSteps
                .Where(x => x.WorkOrderId == workOrderId)
                .OrderBy(x => x.StageSequence)
                .ThenBy(x => x.Uid)
                .ToListAsync(ct);

    private static async Task<List<ProductionWorkOrderOperation>> LockOperationsForWorkOrderAsync(
        AppDbContext db, long workOrderId, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderOperations.FromSqlInterpolated(
                    $@"SELECT * FROM dbo.PrWorkOrderOperation WITH (UPDLOCK, HOLDLOCK) WHERE WorkOrderID={workOrderId} ORDER BY RouteStepID, ProcessSequence, UID")
                .ToListAsync(ct)
            : await db.ProductionWorkOrderOperations
                .Where(x => x.WorkOrderId == workOrderId)
                .OrderBy(x => x.RouteStepId)
                .ThenBy(x => x.ProcessSequence)
                .ThenBy(x => x.Uid)
                .ToListAsync(ct);

    private static async Task<ProductionWorkOrderMaterial?> LockMaterialAsync(AppDbContext db, long id, CancellationToken ct) =>
        db.Database.IsSqlServer()
            ? await db.ProductionWorkOrderMaterials.FromSqlInterpolated(
                $@"SELECT * FROM dbo.PrWorkOrderMaterial WITH (UPDLOCK, HOLDLOCK) WHERE UID={id}").SingleOrDefaultAsync(ct)
            : await db.ProductionWorkOrderMaterials.SingleOrDefaultAsync(x => x.Uid == id, ct);
}
