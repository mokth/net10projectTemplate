using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    public async Task<LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>>
        SearchEligibleDeliveryRequestsAsync(
            LargeLookupSearchRequest request,
            CancellationToken cancellationToken = default)
    {
        request ??= new LargeLookupSearchRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: false, cancellationToken);
        if (auth.Error is not null)
        {
            return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Fail(auth.Error.Value.Message);
        }

        if (_deliveryRequestFulfilment is null)
        {
            return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Fail(
                "Delivery Request fulfilment facts are unavailable for production selection.");
        }

        try
        {
            var scope = auth.Scope!;
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var candidates = db.SaDeliveryRequests.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && (x.Status == SaDeliveryRequestStatuses.Released
                        || x.Status == SaDeliveryRequestStatuses.InProduction));

            var term = request.SearchText?.Trim();
            if (!string.IsNullOrWhiteSpace(term))
            {
                candidates = candidates.Where(x =>
                    x.DeliveryRequestNo.Contains(term)
                    || x.ProductCode.Contains(term)
                    || (x.ProductDescription != null && x.ProductDescription.Contains(term))
                    || db.SaDeliveryRequestSources.Any(source =>
                        source.DeliveryRequestId == x.Uid
                        && source.CompanyCode == scope.CompanyCode
                        && source.BranchCode == scope.BranchCode
                        && source.IsActive
                        && (source.SoNo.Contains(term)
                            || (source.CustomerCode != null && source.CustomerCode.Contains(term)))));
            }

            var ordered = candidates
                .OrderBy(x => x.RequiredDate)
                .ThenBy(x => x.DeliveryRequestNo)
                .ThenBy(x => x.Uid);
            const int scanChunkSize = 200;
            var skip = request.NormalizedSkip;
            var take = request.NormalizedTake;
            var eligibleCount = 0;
            var offset = 0;
            var selected = new List<(SaDeliveryRequest Header, SaDeliveryRequestFulfilmentFacts Facts)>(take);

            while (true)
            {
                var headers = await ordered
                    .Skip(offset)
                    .Take(scanChunkSize)
                    .ToListAsync(cancellationToken);
                if (headers.Count == 0)
                {
                    break;
                }

                var factsResult = await _deliveryRequestFulfilment.GetFactsBatchAsync(
                    headers.Select(x => x.Uid).ToArray(), cancellationToken);
                if (!factsResult.Succeeded || factsResult.Data is null)
                {
                    return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Fail(
                        factsResult.Message ?? "Delivery Request fulfilment facts could not be loaded.");
                }

                foreach (var header in headers)
                {
                    if (!factsResult.Data.TryGetValue(header.Uid, out var facts))
                    {
                        return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Fail(
                            "Delivery Request fulfilment facts were incomplete. Refresh and try again.");
                    }

                    if (facts.ProductionUnplannedQty <= 0.0001m)
                    {
                        continue;
                    }

                    if (eligibleCount >= skip && selected.Count < take)
                    {
                        selected.Add((header, facts));
                    }

                    eligibleCount++;
                }

                offset += headers.Count;
                if (headers.Count < scanChunkSize)
                {
                    break;
                }
            }

            if (selected.Count == 0)
            {
                return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Ok([], eligibleCount);
            }

            var selectedIds = selected.Select(x => x.Header.Uid).ToArray();
            var sources = await db.SaDeliveryRequestSources.AsNoTracking()
                .Where(x => selectedIds.Contains(x.DeliveryRequestId)
                    && x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.IsActive)
                .Select(x => new
                {
                    x.DeliveryRequestId,
                    x.SoNo,
                    x.CustomerCode
                })
                .ToListAsync(cancellationToken);
            var sourceSummaryByRequest = sources
                .GroupBy(x => x.DeliveryRequestId)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var soNumbers = group
                            .Select(x => x.SoNo)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        var customers = group
                            .Select(x => x.CustomerCode)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Select(x => x!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        return (SoNumbers: soNumbers, Customers: customers);
                    });

            var rows = selected.Select(item =>
            {
                var header = item.Header;
                var facts = item.Facts;
                var summary = sourceSummaryByRequest.TryGetValue(header.Uid, out var foundSummary)
                    ? foundSummary
                    : (SoNumbers: new List<string>(), Customers: new List<string>());
                return new ProductionWorkOrderDeliveryRequestLookupRow
                {
                    DeliveryRequestId = header.Uid,
                    DeliveryRequestNo = header.DeliveryRequestNo,
                    ProductCode = header.ProductCode,
                    ProductDescription = header.ProductDescription,
                    ProductionUom = header.ProductionUom,
                    RequestedQty = header.RequestedQty,
                    WoAllocatedQty = facts.ActiveWoAllocatedQty,
                    ProductionUnplannedQty = facts.ProductionUnplannedQty,
                    RequiredDate = header.RequiredDate,
                    DefinitionCode = header.DefinitionCode,
                    WarehouseCode = header.WarehouseCode,
                    ProjectCode = header.ProjectCode,
                    Priority = header.Priority,
                    Remark = header.Remark,
                    PrimarySoNo = summary.SoNumbers.FirstOrDefault(),
                    ActiveSoCount = summary.SoNumbers.Count,
                    PrimaryCustomerCode = summary.Customers.FirstOrDefault(),
                    ActiveCustomerCount = summary.Customers.Count,
                    RowVersion = header.RowVersion.ToArray()
                };
            }).ToList();

            return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Ok(rows, eligibleCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return LargeLookupPage<ProductionWorkOrderDeliveryRequestLookupRow>.Fail(
                "Unable to load eligible Delivery Requests.");
        }
    }

    public async Task<IvMasterOperationResult<ProductionWorkOrderPreview>> PreviewFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderDeliveryRequestRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            decimal? reconciledUnplannedQty = null;
            if (_deliveryRequestFulfilment is not null)
            {
                var reconcile = await _deliveryRequestFulfilment.ReconcileAsync(
                    request.DeliveryRequestId, cancellationToken);
                if (!reconcile.Succeeded || reconcile.Data is null)
                {
                    return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(
                        reconcile.ErrorCode,
                        reconcile.Message ?? "Delivery Request fulfilment reconciliation failed.");
                }

                reconciledUnplannedQty = reconcile.Data.ProductionUnplannedQty;
            }
            var prepared = await BuildDeliveryRequestSnapshotAsync(
                db,
                auth.Scope!,
                request,
                cancellationToken,
                reconciledUnplannedQty);
            if (prepared.Error is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(IvMasterErrorCode.Validation, prepared.Error);
            }

            return IvMasterOperationResult<ProductionWorkOrderPreview>.Ok(
                MapDeliveryRequestPreview(prepared));
        }
        catch (WorkOrderCommandException ex)
        {
            return IvMasterOperationResult<ProductionWorkOrderPreview>.Fail(ex.Code, ex.Message);
        }
    }

    public Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateDraftFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default) =>
        CreateFromDeliveryRequestCoreAsync(request, release: false, cancellationToken);

    public Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateAndReleaseFromDeliveryRequestAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken = default) =>
        CreateFromDeliveryRequestCoreAsync(request, release: true, cancellationToken);

    private async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> CreateFromDeliveryRequestCoreAsync(
        ProductionWorkOrderDeliveryRequestRequest request,
        bool release,
        CancellationToken cancellationToken)
    {
        request ??= new ProductionWorkOrderDeliveryRequestRequest();
        var add = await AuthorizeAsync(PermissionCodes.Add, requireWriteScope: true, cancellationToken);
        if (add.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(add.Error.Value.Code, add.Error.Value.Message);
        }

        if (release)
        {
            var approve = await AuthorizeAsync(ProductionPermissionCodes.Release, requireWriteScope: true, cancellationToken);
            if (approve.Error is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(approve.Error.Value.Code, approve.Error.Value.Message);
            }
        }

        if (request.DeliveryRequestId <= 0)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Delivery Request is required.", "DeliveryRequestId");
        }

        var plannedQty = IvQty.Round(request.PlannedQty);
        if (plannedQty <= 0m)
        {
            return ValidationFailure<ProductionWorkOrderDetail>("Planned quantity must be positive.", "PlannedQty");
        }

        var scope = add.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
                db, scope, request.DeliveryRequestId, cancellationToken)
                ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "Delivery Request not found.");
            if (request.DeliveryRequestRowVersion is { Length: > 0 }
                && !deliveryRequest.RowVersion.SequenceEqual(request.DeliveryRequestRowVersion))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Concurrency,
                    "The Delivery Request changed by another user. Reload it before creating a Work Order.");
            }

            if (deliveryRequest.Status is not (SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction))
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    "Only a Released or In Production Delivery Request can create a Work Order.");
            }

            var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
                db, scope, deliveryRequest.Uid, cancellationToken);
            var activeAllocated = allocations.Where(x => x.IsActive).Sum(x => x.AllocatedQty);
            var fulfilment = _deliveryRequestFulfilment is null
                ? null
                : await _deliveryRequestFulfilment.ReconcileInTransactionAsync(
                    db, deliveryRequest.Uid, scope, cancellationToken);
            var unplanned = fulfilment?.ProductionUnplannedQty
                ?? Math.Max(deliveryRequest.RequestedQty - activeAllocated, 0m);
            if (plannedQty - unplanned > 0.0001m)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    $"Planned quantity exceeds the Delivery Request unplanned quantity ({unplanned:N4}).");
            }

            var prepared = await BuildDeliveryRequestSnapshotAsync(
                db,
                scope,
                CopyRequestWithPlannedQty(request, plannedQty),
                cancellationToken,
                reconciledUnplannedQty: unplanned);
            if (prepared.Error is not null || prepared.Snapshot?.WorkOrder is null)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    prepared.Error ?? "The Work Order snapshot could not be built.");
            }

            var entity = prepared.Snapshot.WorkOrder;
            var now = DateTime.UtcNow;
            entity.WorkOrderNo = await AllocateWorkOrderNoAsync(db, scope.CompanyCode, cancellationToken);
            entity.CreatedDate = now;
            entity.CreatedBy = scope.UserId;
            entity.ModifiedDate = now;
            entity.ModifiedBy = scope.UserId;
            entity.SourceType = ProductionSourceTypes.DeliveryRequest;
            entity.SourceReference = deliveryRequest.DeliveryRequestNo;
            entity.AuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrder = entity,
                EventType = ProductionAuditEventTypes.Created,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = entity.SnapshotRevision,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    entity.ProductCode,
                    entity.SnapshotHash,
                    entity.SourceBomVersion,
                    DeliveryRequestId = deliveryRequest.Uid,
                    DeliveryRequestNo = deliveryRequest.DeliveryRequestNo,
                    AllocatedQty = plannedQty
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });

            var allocation = new PrWorkOrderDemandAllocation
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                WorkOrder = entity,
                DeliveryRequest = deliveryRequest,
                AllocatedQty = plannedQty,
                IsActive = true,
                CreatedDate = now,
                CreatedBy = scope.UserId
            };
            db.ProductionWorkOrders.Add(entity);
            db.PrWorkOrderDemandAllocations.Add(allocation);
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);

            if (release)
            {
                await ValidateAndApplyCurrentReleaseAsync(db, entity, scope, cancellationToken);
                await MarkDeliveryRequestWorkOrderReleasedAsync(
                    db,
                    entity,
                    scope,
                    deliveryRequest.Uid,
                    "Work Order released from Delivery Request.",
                    cancellationToken);
            }

            db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
            {
                DeliveryRequestId = deliveryRequest.Uid,
                EventType = SaDeliveryRequestAuditEventTypes.WorkOrderCreated,
                WorkOrderId = entity.Uid,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                {
                    entity.WorkOrderNo,
                    AllocatedQty = plannedQty,
                    Released = release
                }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
            {
                DeliveryRequestId = deliveryRequest.Uid,
                EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
                WorkOrderId = entity.Uid,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { AllocatedQty = plannedQty, IsActive = true }),
                OccurredDate = now,
                ActorUserId = scope.UserId
            });
            TouchSqliteRowVersions(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(entity));
        }
        catch (WorkOrderCommandException ex)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Delivery Request or Work Order changed by another user. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "The Work Order demand allocation already exists. Reload the Delivery Request and try again.");
        }
    }

    private async Task<DeliveryRequestSnapshotBuild> BuildDeliveryRequestSnapshotAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        ProductionWorkOrderDeliveryRequestRequest request,
        CancellationToken cancellationToken,
        decimal? reconciledUnplannedQty = null)
    {
        var deliveryRequest = await db.SaDeliveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == request.DeliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode, cancellationToken);
        if (deliveryRequest is null)
        {
            return DeliveryRequestSnapshotBuild.Fail("Delivery Request not found.");
        }

        if (deliveryRequest.Status is not (SaDeliveryRequestStatuses.Released or SaDeliveryRequestStatuses.InProduction))
        {
            return DeliveryRequestSnapshotBuild.Fail(
                "Only a Released or In Production Delivery Request can create a Work Order.");
        }

        var allocations = await db.PrWorkOrderDemandAllocations.AsNoTracking()
            .Where(x => x.DeliveryRequestId == deliveryRequest.Uid
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .ToListAsync(cancellationToken);
        var activeAllocated = allocations.Where(x => x.IsActive).Sum(x => x.AllocatedQty);
        var unplanned = reconciledUnplannedQty
            ?? Math.Max(deliveryRequest.RequestedQty - activeAllocated, 0m);
        var plannedQty = IvQty.Round(request.PlannedQty);
        if (plannedQty <= 0m)
        {
            return DeliveryRequestSnapshotBuild.Fail("Planned quantity must be positive.");
        }
        if (plannedQty - unplanned > 0.0001m)
        {
            return DeliveryRequestSnapshotBuild.Fail(
                $"Planned quantity exceeds the Delivery Request unplanned quantity ({unplanned:N4}).");
        }

        var direction = string.IsNullOrWhiteSpace(request.SchedulingDirection)
            ? ProductionSchedulingDirections.Forward
            : Normalize(request.SchedulingDirection);
        if (!ProductionSchedulingDirections.IsKnown(direction))
        {
            return DeliveryRequestSnapshotBuild.Fail("Schedule direction must be FORWARD or BACKWARD.");
        }

        var start = request.PlannedStartDate == default ? deliveryRequest.RequiredDate.Date : request.PlannedStartDate.Date;
        var completion = request.PlannedCompletionDate == default ? start : request.PlannedCompletionDate.Date;
        if (completion < start)
        {
            return DeliveryRequestSnapshotBuild.Fail("Planned completion cannot be before planned start.");
        }

        var draft = new ProductionWorkOrderDraftRequest
        {
            ProductCode = deliveryRequest.ProductCode,
            DefinitionCode = Normalize(request.DefinitionCode ?? deliveryRequest.DefinitionCode ?? PrProductDefinitionCodes.Standard),
            PlannedQty = plannedQty,
            PlannedStartDate = start,
            PlannedCompletionDate = completion,
            SchedulingDirection = direction,
            SourceType = ProductionSourceTypes.DeliveryRequest,
            SourceReference = deliveryRequest.DeliveryRequestNo,
            Remark = request.Remark ?? deliveryRequest.Remark
        };
        var snapshot = await BuildCurrentSnapshotAsync(
            scope,
            draft,
            snapshotRevision: 1,
            explicitScheduleAnchor: null,
            cancellationToken);
        if (snapshot.WorkOrder is null)
        {
            return DeliveryRequestSnapshotBuild.Fail(snapshot.Error ?? "The Work Order snapshot could not be built.");
        }

        if (!string.Equals(
                Normalize(snapshot.WorkOrder.OutputUom),
                Normalize(deliveryRequest.ProductionUom),
                StringComparison.OrdinalIgnoreCase))
        {
            return DeliveryRequestSnapshotBuild.Fail(
                $"The Product Definition output UOM ({snapshot.WorkOrder.OutputUom}) is incompatible with the Delivery Request production UOM ({deliveryRequest.ProductionUom}).");
        }

        return new DeliveryRequestSnapshotBuild(
            deliveryRequest,
            activeAllocated,
            unplanned,
            snapshot,
            null);
    }

    private static ProductionWorkOrderPreview MapDeliveryRequestPreview(DeliveryRequestSnapshotBuild prepared)
    {
        var detail = MapDetail(prepared.Snapshot!.WorkOrder!);
        return new ProductionWorkOrderPreview
        {
            DeliveryRequestId = prepared.DeliveryRequest!.Uid,
            DeliveryRequestNo = prepared.DeliveryRequest.DeliveryRequestNo,
            DeliveryRequestUnplannedQty = prepared.UnplannedQty,
            ProductCode = detail.ProductCode,
            ProductDescription = detail.ProductDescription,
            OutputUom = detail.OutputUom,
            SourceDefinitionCode = detail.SourceDefinitionCode,
            SourceDefinitionName = detail.SourceDefinitionName,
            SourceBomHdrId = detail.SourceBomHdrId,
            SourceBomVersion = detail.SourceBomVersion,
            BomBaseQty = detail.BomBaseQty,
            BomBaseUom = detail.BomBaseUom,
            PlannedQty = detail.PlannedQty,
            PlannedStartDate = detail.PlannedStartDate,
            PlannedCompletionDate = detail.PlannedCompletionDate,
            SchedulingDirection = detail.SchedulingDirection,
            ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime
                ?? ProductionSchedulingDirections.NormalizePlannerDateAnchor(detail.PlannedStartDate, detail.SchedulingDirection),
            SnapshotHash = detail.SnapshotHash,
            SourceProductDefinitionRevisionId = detail.SourceProductDefinitionRevisionId,
            RouteSteps = detail.RouteSteps,
            Materials = detail.Materials,
            Operations = detail.Operations,
            Warnings = prepared.Snapshot.Warnings
        };
    }

    private static ProductionWorkOrderDeliveryRequestRequest CopyRequestWithPlannedQty(
        ProductionWorkOrderDeliveryRequestRequest request,
        decimal plannedQty) => new()
        {
            DeliveryRequestId = request.DeliveryRequestId,
            PlannedQty = plannedQty,
            DefinitionCode = request.DefinitionCode,
            PlannedStartDate = request.PlannedStartDate,
            PlannedCompletionDate = request.PlannedCompletionDate,
            SchedulingDirection = request.SchedulingDirection,
            Remark = request.Remark,
            DeliveryRequestRowVersion = request.DeliveryRequestRowVersion
        };

    private async Task<long> AdjustDeliveryRequestAllocationAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        decimal plannedQty,
        CancellationToken cancellationToken)
    {
        var relatedAllocations = entity.DemandAllocations
            .Where(x => x.WorkOrderId == entity.Uid)
            .ToList();
        if (relatedAllocations.Count != 1 || !relatedAllocations[0].IsActive)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "This Delivery Request Work Order must have exactly one active demand allocation and cannot be resized.");
        }

        var allocation = relatedAllocations[0];

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, allocation.DeliveryRequestId, cancellationToken)
            ?? throw new WorkOrderCommandException(IvMasterErrorCode.NotFound, "The Delivery Request source was not found.");
        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var currentAllocations = allocations
            .Where(x => x.WorkOrderId == entity.Uid && x.IsActive)
            .ToList();
        if (currentAllocations.Count != 1)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order must have exactly one active demand allocation.");
        }

        var currentAllocation = currentAllocations[0];

        decimal availableForThisWorkOrder;
        if (_deliveryRequestFulfilment is not null)
        {
            var facts = await _deliveryRequestFulfilment.ReconcileInTransactionAsync(
                db, deliveryRequest.Uid, scope, cancellationToken);
            var otherOutstandingSupply = Math.Max(
                facts.OutstandingWoSupplyQty - currentAllocation.AllocatedQty,
                0m);
            availableForThisWorkOrder = IvQty.Round(Math.Max(
                facts.ProductionRequiredQty - otherOutstandingSupply,
                0m));
        }
        else
        {
            var otherActive = allocations
                .Where(x => x.IsActive && x.WorkOrderId != entity.Uid)
                .Sum(x => x.AllocatedQty);
            availableForThisWorkOrder = IvQty.Round(Math.Max(
                deliveryRequest.RequestedQty - otherActive,
                0m));
        }

        plannedQty = IvQty.Round(plannedQty);
        if (plannedQty - availableForThisWorkOrder > 0.0001m)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.Validation,
                $"Planned quantity exceeds the Delivery Request quantity available to this Work Order ({availableForThisWorkOrder:N4}).");
        }

        currentAllocation.AllocatedQty = plannedQty;
        currentAllocation.ReleaseReason = "Work Order Draft quantity changed.";
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { AllocatedQty = plannedQty }),
            OccurredDate = DateTime.UtcNow,
            ActorUserId = scope.UserId
        });
        TouchSqliteRowVersions(db, entity);
        return deliveryRequest.Uid;
    }

    /// <summary>
    /// Locks the demand side before a normal Work Order lifecycle command locks the
    /// Work Order aggregate. Call this before LoadAggregateAsync/RequireDraftAsync
    /// whenever the Work Order may carry a Delivery Request allocation.
    /// </summary>
    private async Task LockDemandBridgeBeforeWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string workOrderNo,
        CancellationToken cancellationToken)
    {
        var identity = await db.ProductionWorkOrders
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.WorkOrderNo == workOrderNo)
            .Select(x => new { x.Uid, x.SourceType })
            .SingleOrDefaultAsync(cancellationToken);
        if (identity is null || !IsDeliveryRequestWorkOrder(identity.SourceType))
        {
            return;
        }

        var bridgeRows = await (
            from allocation in db.PrWorkOrderDemandAllocations.AsNoTracking()
            where allocation.WorkOrderId == identity.Uid
                && allocation.CompanyCode == scope.CompanyCode
                && allocation.BranchCode == scope.BranchCode
            select new { allocation.DeliveryRequestId })
            .ToListAsync(cancellationToken);
        if (bridgeRows.Count != 1 || bridgeRows[0].DeliveryRequestId <= 0)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order must have exactly one valid demand allocation.");
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, bridgeRows[0].DeliveryRequestId, cancellationToken);
        if (deliveryRequest is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Work Order demand source could not be loaded.");
        }

        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        if (allocations.Count(x => x.WorkOrderId == identity.Uid) != 1)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order must have exactly one valid demand allocation.");
        }
    }

    private async Task MarkDeliveryRequestWorkOrderReleasedAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        long? deliveryRequestId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!IsDeliveryRequestWorkOrder(entity.SourceType))
        {
            return;
        }

        var (deliveryRequest, allocation) = await LockRequiredDeliveryRequestAllocationAsync(
            db, entity, scope, deliveryRequestId, cancellationToken);

        if (_deliveryRequestFulfilment is not null)
        {
            var facts = await _deliveryRequestFulfilment.ReconcileInTransactionAsync(
                db, deliveryRequest.Uid, scope, cancellationToken);
            var otherOutstandingSupply = Math.Max(
                facts.OutstandingWoSupplyQty - allocation.AllocatedQty,
                0m);
            var maxAllowedForThisWorkOrder = IvQty.Round(Math.Max(
                facts.ProductionRequiredQty - otherOutstandingSupply,
                0m));
            if (allocation.AllocatedQty - maxAllowedForThisWorkOrder > 0.0001m)
            {
                throw new WorkOrderCommandException(
                    IvMasterErrorCode.Validation,
                    $"The Delivery Request demand now supports only {maxAllowedForThisWorkOrder:N4} for this Work Order. Reduce the Draft quantity before release.");
            }
        }

        var now = entity.ReleasedDate ?? DateTime.UtcNow;
        allocation.IsActive = true;
        allocation.ReleasedDate = now;
        allocation.ReleasedBy = entity.ReleasedBy ?? scope.UserId;
        allocation.ReleaseReason = reason;
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                allocation.AllocatedQty,
                allocation.IsActive,
                allocation.ReleasedDate,
                Released = true
            }),
            Reason = reason,
            OccurredDate = now,
            ActorUserId = scope.UserId
        });

        await ReconcileDeliveryRequestStatusAsync(
            db, deliveryRequest, scope, cancellationToken);
    }

    private async Task DeactivateDeliveryRequestAllocationAsync(
        AppDbContext db,
        ProductionWorkOrder entity,
        InventoryTenantScope scope,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!IsDeliveryRequestWorkOrder(entity.SourceType))
        {
            return;
        }

        var (deliveryRequest, allocation) = await LockRequiredDeliveryRequestAllocationAsync(
            db, entity, scope, deliveryRequestId: null, cancellationToken);

        if (!allocation.IsActive)
        {
            await ReconcileDeliveryRequestStatusAsync(
                db, deliveryRequest, scope, cancellationToken);
            return;
        }

        var now = DateTime.UtcNow;
        allocation.IsActive = false;
        allocation.ReleasedDate = now;
        allocation.ReleasedBy = scope.UserId;
        allocation.ReleaseReason = reason;
        db.SaDeliveryRequestAuditEvents.Add(new SaDeliveryRequestAuditEvent
        {
            DeliveryRequestId = deliveryRequest.Uid,
            EventType = SaDeliveryRequestAuditEventTypes.AllocationChanged,
            WorkOrderId = entity.Uid,
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                allocation.AllocatedQty,
                allocation.IsActive
            }),
            Reason = reason,
            OccurredDate = now,
            ActorUserId = scope.UserId
        });

        await ReconcileDeliveryRequestStatusAsync(
            db, deliveryRequest, scope, cancellationToken);
    }

    private async Task<(SaDeliveryRequest DeliveryRequest, PrWorkOrderDemandAllocation Allocation)>
        LockRequiredDeliveryRequestAllocationAsync(
            AppDbContext db,
            ProductionWorkOrder entity,
            InventoryTenantScope scope,
            long? deliveryRequestId,
            CancellationToken cancellationToken)
    {
        var relatedAllocations = entity.DemandAllocations
            .Where(x => x.WorkOrderId == entity.Uid)
            .ToList();
        if (relatedAllocations.Count > 1)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order has duplicate demand allocations.");
        }

        var relatedRequestId = relatedAllocations.Count == 1
            ? relatedAllocations[0].DeliveryRequestId
            : deliveryRequestId;
        if (relatedRequestId is not > 0
            || relatedAllocations.Count == 1
                && deliveryRequestId is > 0
                && relatedAllocations[0].DeliveryRequestId != deliveryRequestId.Value)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order has no valid relational demand identity.");
        }

        var deliveryRequest = await LockDeliveryRequestForWorkOrderAsync(
            db, scope, relatedRequestId.Value, cancellationToken);
        if (deliveryRequest is null)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request source could not be loaded for the current company and branch.");
        }

        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var matching = allocations.Where(x => x.WorkOrderId == entity.Uid).ToList();
        if (matching.Count != 1 || matching[0].DeliveryRequestId != deliveryRequest.Uid)
        {
            throw new WorkOrderCommandException(
                IvMasterErrorCode.InUse,
                "The Delivery Request Work Order must have exactly one valid demand allocation.");
        }

        return (deliveryRequest, matching[0]);
    }

    private async Task ReconcileDeliveryRequestStatusAsync(
        AppDbContext db,
        SaDeliveryRequest deliveryRequest,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                deliveryRequest.Status,
                SaDeliveryRequestStatuses.Cancelled,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var allocations = await LockDeliveryRequestAllocationsForWorkOrderAsync(
            db, scope, deliveryRequest.Uid, cancellationToken);
        var activeAllocations = allocations.Where(x => x.IsActive).ToList();
        var workOrderIds = activeAllocations
            .Select(x => x.WorkOrderId)
            .Distinct()
            .ToList();
        var workOrders = workOrderIds.Count == 0
            ? new List<ProductionWorkOrder>()
            : await db.ProductionWorkOrders
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && workOrderIds.Contains(x.Uid))
                .ToListAsync(cancellationToken);
        var workOrderById = workOrders.ToDictionary(x => x.Uid);
        var hasProductionActiveWorkOrder = activeAllocations.Any(allocation =>
            workOrderById.TryGetValue(allocation.WorkOrderId, out var workOrder)
            && (workOrder.Status is ProductionWorkOrderStatuses.Released
                or ProductionWorkOrderStatuses.InProgress
                or ProductionWorkOrderStatuses.Completed
                or ProductionWorkOrderStatuses.Closed
                || workOrder.GoodQty > 0m));
        var nextStatus = hasProductionActiveWorkOrder
            ? SaDeliveryRequestStatuses.InProduction
            : SaDeliveryRequestStatuses.Released;
        if (string.Equals(deliveryRequest.Status, nextStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        deliveryRequest.Status = nextStatus;
        deliveryRequest.ModifiedDate = DateTime.UtcNow;
        deliveryRequest.ModifiedBy = scope.UserId;
    }

    private static bool IsDeliveryRequestWorkOrder(string? sourceType) =>
        string.Equals(sourceType, ProductionSourceTypes.DeliveryRequest, StringComparison.OrdinalIgnoreCase);

    private async Task<SaDeliveryRequest?> LockDeliveryRequestForWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long uid,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.SaDeliveryRequests.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequest WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE UID = {uid} AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SaDeliveryRequests.SingleOrDefaultAsync(x => x.Uid == uid
            && x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode, cancellationToken);
    }

    private static async Task<List<PrWorkOrderDemandAllocation>> LockDeliveryRequestAllocationsForWorkOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long deliveryRequestId,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.PrWorkOrderDemandAllocations.FromSqlInterpolated($@"
SELECT * FROM dbo.PrWorkOrderDemandAllocation WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE DeliveryRequestID = {deliveryRequestId}
  AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
ORDER BY WorkOrderID")
                .AsTracking()
                .ToListAsync(cancellationToken);
        }

        return await db.PrWorkOrderDemandAllocations
            .Where(x => x.DeliveryRequestId == deliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.WorkOrderId)
            .ToListAsync(cancellationToken);
    }

    private static bool IsSqlServer(AppDbContext db) =>
        db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private sealed record DeliveryRequestSnapshotBuild(
        SaDeliveryRequest? DeliveryRequest,
        decimal ActiveAllocatedQty,
        decimal UnplannedQty,
        BuiltSnapshot? Snapshot,
        string? Error)
    {
        public static DeliveryRequestSnapshotBuild Fail(string message) =>
            new(null, 0m, 0m, null, message);
    }
}
