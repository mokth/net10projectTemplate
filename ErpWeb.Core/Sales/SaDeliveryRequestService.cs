using System.Text.Json;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Owns Sales Order -> Delivery Request demand. It deliberately stops at the relational
/// handoff: Work Order snapshot and release authority remains in ProductionWorkOrderService.
/// </summary>
public sealed class SaDeliveryRequestService : ISaDeliveryRequestService
{
    private const string NumberingModule = "DR";
    private const decimal QuantityTolerance = 0.0001m;
    private const int MaxRetries = 3;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;

    public SaDeliveryRequestService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
    }

    public async Task<IvMasterOperationResult<SaDeliveryRequestListPage>> SearchAsync(
        SaDeliveryRequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Access, write: false, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<SaDeliveryRequestListPage>(auth.Error.Value);
        }

        query ??= new SaDeliveryRequestListQuery();
        var skip = Math.Max(query.Skip, 0);
        var take = query.Take <= 0 ? 20 : Math.Min(query.Take, 500);
        var scope = auth.Scope!;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var headers = db.SaDeliveryRequests.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode);

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var term = query.SearchText.Trim();
            headers = headers.Where(x => x.DeliveryRequestNo.Contains(term)
                || x.ProductCode.Contains(term)
                || (x.ProductDescription != null && x.ProductDescription.Contains(term)));
        }

        var statusFilter = NullIfEmpty(query.Status);
        if (statusFilter is not null)
        {
            statusFilter = Normalize(statusFilter);
            var completedRequestIds =
                from header in db.SaDeliveryRequests.AsNoTracking()
                join allocation in db.PrWorkOrderDemandAllocations.AsNoTracking()
                    on header.Uid equals allocation.DeliveryRequestId
                join workOrder in db.ProductionWorkOrders.AsNoTracking()
                    on allocation.WorkOrderId equals workOrder.Uid
                where header.CompanyCode == scope.CompanyCode
                    && header.BranchCode == scope.BranchCode
                    && workOrder.Status != ProductionWorkOrderStatuses.Cancelled
                group workOrder by new { header.Uid, header.RequestedQty } into grouped
                where grouped.Sum(x => x.GoodQty) + QuantityTolerance >= grouped.Key.RequestedQty
                select grouped.Key.Uid;

            headers = statusFilter switch
            {
                SaDeliveryRequestStatuses.Completed => headers
                    .Where(x => x.Status != SaDeliveryRequestStatuses.Cancelled
                        && completedRequestIds.Contains(x.Uid)),
                SaDeliveryRequestStatuses.InProduction => headers
                    .Where(x => x.Status == SaDeliveryRequestStatuses.InProduction
                        && !completedRequestIds.Contains(x.Uid)),
                SaDeliveryRequestStatuses.Draft => headers.Where(x => x.Status == SaDeliveryRequestStatuses.Draft),
                SaDeliveryRequestStatuses.Released => headers.Where(x => x.Status == SaDeliveryRequestStatuses.Released),
                SaDeliveryRequestStatuses.Cancelled => headers.Where(x => x.Status == SaDeliveryRequestStatuses.Cancelled),
                _ => headers.Where(x => x.Status == statusFilter)
            };
        }

        if (!string.IsNullOrWhiteSpace(query.ProductCode))
        {
            var product = Normalize(query.ProductCode);
            headers = headers.Where(x => x.ProductCode == product);
        }

        if (query.RequiredDateFrom is DateTime from)
        {
            headers = headers.Where(x => x.RequiredDate >= from.Date);
        }

        if (query.RequiredDateTo is DateTime to)
        {
            headers = headers.Where(x => x.RequiredDate < to.Date.AddDays(1));
        }

        var total = await headers.CountAsync(cancellationToken);
        var page = await headers
            .OrderByDescending(x => x.RequiredDate)
            .ThenByDescending(x => x.Uid)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var ids = page.Select(x => x.Uid).ToList();
        var sourceCounts = ids.Count == 0
            ? []
            : await db.SaDeliveryRequestSources.AsNoTracking()
                .Where(x => ids.Contains(x.DeliveryRequestId))
                .GroupBy(x => x.DeliveryRequestId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
        var allocations = ids.Count == 0
            ? []
            : await db.PrWorkOrderDemandAllocations.AsNoTracking()
                .Where(x => ids.Contains(x.DeliveryRequestId))
                .Join(db.ProductionWorkOrders.AsNoTracking(),
                    allocation => allocation.WorkOrderId,
                    workOrder => workOrder.Uid,
                    (allocation, workOrder) => new { allocation, workOrder })
                .Select(x => new
                {
                    x.allocation.DeliveryRequestId,
                    x.allocation.AllocatedQty,
                    x.allocation.IsActive,
                    x.workOrder.Status,
                    x.workOrder.GoodQty,
                    x.workOrder.PlannedQty
                })
                .ToListAsync(cancellationToken);

        var sourceCountById = sourceCounts.ToDictionary(x => x.Id, x => x.Count);
        var allocationById = allocations.GroupBy(x => x.DeliveryRequestId)
            .ToDictionary(g => g.Key, g => new ProgressFacts(
                g.Where(x => x.IsActive).Sum(x => x.AllocatedQty),
                g.Where(x => !string.Equals(x.Status, ProductionWorkOrderStatuses.Cancelled, StringComparison.Ordinal))
                    .Sum(x => x.GoodQty),
                g.Count(),
                g.Any(x => x.IsActive && (x.GoodQty > QuantityTolerance
                    || x.Status is ProductionWorkOrderStatuses.InProgress
                        or ProductionWorkOrderStatuses.Completed
                        or ProductionWorkOrderStatuses.Closed))));

        var rows = page.Select(header =>
        {
            allocationById.TryGetValue(header.Uid, out var facts);
            facts ??= ProgressFacts.Empty;
            return new SaDeliveryRequestListRow
            {
                Uid = header.Uid,
                DeliveryRequestNo = header.DeliveryRequestNo,
                ProductCode = header.ProductCode,
                ProductDescription = header.ProductDescription,
                ProductionUom = header.ProductionUom,
                RequestedQty = header.RequestedQty,
                WoAllocatedQty = facts.AllocatedQty,
                UnplannedQty = Unplanned(header.RequestedQty, facts.AllocatedQty),
                ProducedQty = facts.ProducedQty,
                RequiredDate = header.RequiredDate,
                Status = DerivedStatus(header.Status, header.RequestedQty, facts),
                SourceCount = sourceCountById.GetValueOrDefault(header.Uid),
                WorkOrderCount = facts.WorkOrderCount,
                CreatedDate = header.CreatedDate,
                CreatedBy = header.CreatedBy,
                RowVersion = header.RowVersion ?? []
            };
        }).ToList();

        return IvMasterOperationResult<SaDeliveryRequestListPage>.Ok(new SaDeliveryRequestListPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<SaDeliveryRequestDetail>> GetAsync(
        long uid,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Access, write: false, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<SaDeliveryRequestDetail>(auth.Error.Value);
        }

        if (uid <= 0)
        {
            return Invalid<SaDeliveryRequestDetail>("Delivery Request is required.", "Uid");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var header = await db.SaDeliveryRequests.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Uid == uid
                && x.CompanyCode == auth.Scope!.CompanyCode
                && x.BranchCode == auth.Scope!.BranchCode, cancellationToken);
        if (header is null)
        {
            return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(
                IvMasterErrorCode.NotFound, "Delivery Request not found.");
        }

        return IvMasterOperationResult<SaDeliveryRequestDetail>.Ok(
            await BuildDetailAsync(db, header, cancellationToken));
    }

    public async Task<IvMasterOperationResult<IReadOnlyList<SaDeliveryRequestEligibleSource>>> ListEligibleSalesOrderDemandAsync(
        SaDeliveryRequestEligibleSourceQuery query,
        CancellationToken cancellationToken = default)
    {
        var auth = await AuthorizeAsync(PermissionCodes.Access, write: false, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<IReadOnlyList<SaDeliveryRequestEligibleSource>>(auth.Error.Value);
        }

        query ??= new SaDeliveryRequestEligibleSourceQuery();
        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var details =
            from header in db.SaSos.AsNoTracking()
            join detail in db.SaSoDetails.AsNoTracking()
                on new { header.CompanyCode, header.BranchCode, header.SoNo, header.CustRel }
                equals new { detail.CompanyCode, detail.BranchCode, detail.SoNo, detail.CustRel }
            where header.CompanyCode == scope.CompanyCode
                && header.BranchCode == scope.BranchCode
                && header.IsCurrent
                && (query.SoNo == null || header.SoNo == query.SoNo.Trim())
                && (query.CustRel == null || header.CustRel == query.CustRel)
                && (query.ProductCode == null || detail.ICode == query.ProductCode.Trim())
                && detail.StdQty > 0m
                && detail.StdUom != null
                && detail.StdUom != ""
                && detail.ICode != null
                && detail.ICode != ""
            select new
            {
                header.SoNo,
                header.CustRel,
                HeaderRowVersion = header.RowVersion,
                detail.Line,
                ProductCode = detail.ICode!,
                detail.IDesc,
                ProductionUom = detail.StdUom!,
                ProductionDemandQty = detail.StdQty,
                detail.DeliveredQty,
                CustomerCode = header.CustCode,
                RequestedDeliveryDate = detail.DeliveryDate,
                detail.Warehouse
            };

        var detailRows = await details
            .OrderBy(x => x.SoNo)
            .ThenBy(x => x.CustRel)
            .ThenBy(x => x.Line)
            .Take(1000)
            .ToListAsync(cancellationToken);
        if (detailRows.Count == 0)
        {
            return IvMasterOperationResult<IReadOnlyList<SaDeliveryRequestEligibleSource>>.Ok([]);
        }

        var soNos = detailRows.Select(x => x.SoNo).Distinct().ToList();
        var custRels = detailRows.Select(x => x.CustRel).Distinct().ToList();
        var activeAllocations = await db.SaDeliveryRequestSources.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive
                && soNos.Contains(x.SoNo)
                && custRels.Contains(x.CustRel))
            .GroupBy(x => new { x.SoNo, x.CustRel, x.SoLine })
            .Select(g => new { g.Key.SoNo, g.Key.CustRel, g.Key.SoLine, Qty = g.Sum(x => x.AllocatedProductionQty) })
            .ToListAsync(cancellationToken);
        var allocatedByLine = activeAllocations.ToDictionary(
            x => (x.SoNo.ToUpperInvariant(), x.CustRel, x.SoLine), x => x.Qty);

        var result = detailRows.Select(x =>
        {
            var allocated = allocatedByLine.GetValueOrDefault((x.SoNo.ToUpperInvariant(), x.CustRel, x.Line));
            return new SaDeliveryRequestEligibleSource
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                SoNo = x.SoNo,
                CustRel = x.CustRel,
                SoLine = x.Line,
                ProductCode = x.ProductCode.Trim(),
                ProductDescription = x.IDesc,
                ProductionUom = x.ProductionUom.Trim(),
                ProductionDemandQty = x.ProductionDemandQty,
                ActiveDrAllocatedQty = allocated,
                AvailableForDr = Unplanned(x.ProductionDemandQty, allocated),
                CustomerCode = x.CustomerCode,
                RequestedDeliveryDate = x.RequestedDeliveryDate,
                WarehouseCode = x.Warehouse,
                RowVersion = x.HeaderRowVersion ?? []
            };
        }).Where(x => x.AvailableForDr > QuantityTolerance).ToList();

        return IvMasterOperationResult<IReadOnlyList<SaDeliveryRequestEligibleSource>>.Ok(result);
    }

    public Task<IvMasterOperationResult<SaDeliveryRequestDetail>> CreateDraftAsync(
        SaDeliveryRequestDraftRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteWithDeadlockRetryAsync(
            PermissionCodes.Add,
            request,
            update: false,
            cancellationToken);

    public Task<IvMasterOperationResult<SaDeliveryRequestDetail>> UpdateDraftAsync(
        SaDeliveryRequestUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteWithDeadlockRetryAsync(
            PermissionCodes.Edit,
            request,
            update: true,
            cancellationToken);

    public async Task<IvMasterOperationResult<SaDeliveryRequestDetail>> AddSourceAsync(
        SaDeliveryRequestSourceChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Source is null)
        {
            return Invalid<SaDeliveryRequestDetail>("A Delivery Request source is required.", "Source");
        }

        var current = await GetAsync(request.DeliveryRequestId, cancellationToken);
        if (!current.Succeeded || current.Data is null)
        {
            return current;
        }

        var sources = current.Data.Sources
            .Select(x => new SaDeliveryRequestSourceInput
            {
                SoNo = x.SoNo,
                CustRel = x.CustRel,
                SoLine = x.SoLine,
                AllocatedProductionQty = x.AllocatedProductionQty
            })
            .ToList();
        sources.Add(request.Source);
        return await UpdateDraftAsync(new SaDeliveryRequestUpdateRequest
        {
            Uid = current.Data.Uid,
            RowVersion = current.Data.RowVersion,
            ProductCode = current.Data.ProductCode,
            ProductionUom = current.Data.ProductionUom,
            RequestedQty = current.Data.RequestedQty + request.Source.AllocatedProductionQty,
            RequiredDate = current.Data.RequiredDate,
            DefinitionCode = current.Data.DefinitionCode,
            WarehouseCode = current.Data.WarehouseCode,
            ProjectCode = current.Data.ProjectCode,
            Priority = current.Data.Priority,
            Remark = current.Data.Remark,
            Sources = sources
        }, cancellationToken);
    }

    public async Task<IvMasterOperationResult<SaDeliveryRequestDetail>> RemoveSourceAsync(
        SaDeliveryRequestSourceChangeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null || request.Source is null)
        {
            return Invalid<SaDeliveryRequestDetail>("A Delivery Request source is required.", "Source");
        }

        var current = await GetAsync(request.DeliveryRequestId, cancellationToken);
        if (!current.Succeeded || current.Data is null)
        {
            return current;
        }

        var source = current.Data.Sources.FirstOrDefault(x => SameSource(x, request.Source));
        if (source is null)
        {
            return Invalid<SaDeliveryRequestDetail>("The selected Sales Order line is not in this Delivery Request.", "Source");
        }

        var sources = current.Data.Sources
            .Where(x => !SameSource(x, request.Source))
            .Select(x => new SaDeliveryRequestSourceInput
            {
                SoNo = x.SoNo,
                CustRel = x.CustRel,
                SoLine = x.SoLine,
                AllocatedProductionQty = x.AllocatedProductionQty
            })
            .ToList();
        return await UpdateDraftAsync(new SaDeliveryRequestUpdateRequest
        {
            Uid = current.Data.Uid,
            RowVersion = current.Data.RowVersion,
            ProductCode = current.Data.ProductCode,
            ProductionUom = current.Data.ProductionUom,
            RequestedQty = current.Data.RequestedQty - source.AllocatedProductionQty,
            RequiredDate = current.Data.RequiredDate,
            DefinitionCode = current.Data.DefinitionCode,
            WarehouseCode = current.Data.WarehouseCode,
            ProjectCode = current.Data.ProjectCode,
            Priority = current.Data.Priority,
            Remark = current.Data.Remark,
            Sources = sources
        }, cancellationToken);
    }

    public Task<IvMasterOperationResult<SaDeliveryRequestDetail>> ReleaseAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteLifecycleAsync(request, PermissionCodes.Approve, LifecycleAction.Release, cancellationToken);

    public Task<IvMasterOperationResult<SaDeliveryRequestDetail>> CancelAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteLifecycleAsync(request, PermissionCodes.Cancel, LifecycleAction.Cancel, cancellationToken);

    public Task<IvMasterOperationResult<long>> DeleteDraftAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteDeleteAsync(request, cancellationToken);

    private async Task<IvMasterOperationResult<SaDeliveryRequestDetail>> ExecuteWithDeadlockRetryAsync(
        string permission,
        SaDeliveryRequestDraftRequest request,
        bool update,
        CancellationToken cancellationToken)
    {
        request ??= new SaDeliveryRequestDraftRequest();
        var auth = await AuthorizeAsync(permission, write: true, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<SaDeliveryRequestDetail>(auth.Error.Value);
        }

        var scope = auth.Scope!;

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
                await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
                try
                {
                    SaDeliveryRequest header;
                    List<SaDeliveryRequestSource> existingSources = [];
                    if (update)
                    {
                        var updateRequest = (SaDeliveryRequestUpdateRequest)request;
                        header = await LockDeliveryRequestAsync(db, scope, updateRequest.Uid, cancellationToken)
                            ?? throw new DeliveryRequestCommandException(IvMasterErrorCode.NotFound, "Delivery Request not found.");
                        CheckRowVersion(header.RowVersion, updateRequest.RowVersion, "Delivery Request changed by another user. Reload before saving.");
                        if (header.Status != SaDeliveryRequestStatuses.Draft)
                        {
                            throw new DeliveryRequestCommandException(
                                IvMasterErrorCode.Validation, "Only Draft Delivery Requests can be edited.");
                        }

                        existingSources = await LockSourcesAsync(db, scope, header.Uid, cancellationToken);
                        var historicalAllocation = await db.PrWorkOrderDemandAllocations.AsNoTracking()
                            .AnyAsync(x => x.DeliveryRequestId == header.Uid, cancellationToken);
                        if (historicalAllocation)
                        {
                            throw new DeliveryRequestCommandException(
                                IvMasterErrorCode.InUse,
                                "This Delivery Request has Work Order history and its source lines cannot be rewritten.");
                        }
                    }
                    else
                    {
                        header = new SaDeliveryRequest
                        {
                            CompanyCode = scope.CompanyCode,
                            BranchCode = scope.BranchCode!,
                            Status = SaDeliveryRequestStatuses.Draft
                        };
                    }

                    var prepared = await PrepareSourceRowsAsync(
                        db,
                        scope,
                        request,
                        update ? header.Uid : null,
                        cancellationToken);
                    if (prepared.Error is not null)
                    {
                        throw prepared.Error;
                    }

                    var now = DateTime.UtcNow;
                    if (!update)
                    {
                        header.DeliveryRequestNo = await AllocateNumberAsync(db, request, scope, cancellationToken);
                        header.CreatedDate = now;
                        header.CreatedBy = scope.UserId;
                        db.SaDeliveryRequests.Add(header);
                    }
                    ApplyHeader(header, request, prepared.ProductCode, prepared.ProductDescription, prepared.ProductionUom,
                        prepared.RequestedQty, prepared.RequiredDate, scope.UserId, now);
                    var addedSources = new List<SaDeliveryRequestSource>();
                    if (update)
                    {
                        var preparedKeys = prepared.Sources
                            .Select(SourceKey)
                            .ToHashSet();
                        foreach (var oldSource in existingSources)
                        {
                            if (!preparedKeys.Contains(SourceKey(oldSource)))
                            {
                                db.SaDeliveryRequestAuditEvents.Add(Audit(
                                    header.Uid,
                                    SaDeliveryRequestAuditEventTypes.SourceRemoved,
                                    sourceId: oldSource.Uid,
                                    details: new { oldSource.SoNo, oldSource.CustRel, oldSource.SoLine },
                                    reason: "Draft source removed",
                                    now: now,
                                    actor: scope.UserId));
                                db.SaDeliveryRequestSources.Remove(oldSource);
                            }
                        }

                        foreach (var source in prepared.Sources)
                        {
                            var existing = existingSources.FirstOrDefault(x => SourceKey(x) == SourceKey(source));
                            if (existing is not null)
                            {
                                CopySourceValues(existing, source);
                                continue;
                            }

                            source.DeliveryRequest = header;
                            source.DeliveryRequestId = header.Uid;
                            source.CreatedDate = now;
                            source.CreatedBy = scope.UserId;
                            header.Sources.Add(source);
                            addedSources.Add(source);
                        }
                    }
                    else
                    {
                        foreach (var source in prepared.Sources)
                        {
                            source.DeliveryRequest = header;
                            source.DeliveryRequestId = header.Uid;
                            source.CreatedDate = now;
                            source.CreatedBy = scope.UserId;
                            header.Sources.Add(source);
                            addedSources.Add(source);
                        }
                    }

                    db.SaDeliveryRequestAuditEvents.Add(Audit(
                        header.Uid,
                        update ? SaDeliveryRequestAuditEventTypes.Updated : SaDeliveryRequestAuditEventTypes.Created,
                        details: new { header.ProductCode, header.RequestedQty, SourceCount = prepared.Sources.Count },
                        now: now,
                        actor: scope.UserId));
                    foreach (var source in addedSources)
                    {
                        db.SaDeliveryRequestAuditEvents.Add(Audit(
                            header.Uid,
                            SaDeliveryRequestAuditEventTypes.SourceAdded,
                            sourceId: source.Uid == 0 ? null : source.Uid,
                            details: new { source.SoNo, source.CustRel, source.SoLine, source.AllocatedProductionQty },
                            now: now,
                            actor: scope.UserId));
                    }

                    // The header has an identity key. New audit rows must use the
                    // navigation while the key is still zero so EF can insert
                    // both rows in one transaction without violating the FK.
                    foreach (var audit in db.ChangeTracker.Entries<SaDeliveryRequestAuditEvent>()
                        .Where(x => x.State == EntityState.Added && x.Entity.DeliveryRequestId == 0)
                        .Select(x => x.Entity))
                    {
                        audit.DeliveryRequest = header;
                    }

                    TouchSqliteRowVersions(db);
                    await db.SaveChangesAsync(cancellationToken);
                    await tx.CommitAsync(cancellationToken);

                    await using var readDb = await _dbFactory.CreateDbContextAsync(cancellationToken);
                    var saved = await readDb.SaDeliveryRequests.AsNoTracking()
                        .SingleAsync(x => x.Uid == header.Uid, cancellationToken);
                    return IvMasterOperationResult<SaDeliveryRequestDetail>.Ok(
                        await BuildDetailAsync(readDb, saved, cancellationToken));
                }
                catch
                {
                    await tx.RollbackAsync(cancellationToken);
                    throw;
                }
            }
            catch (DeliveryRequestCommandException ex)
            {
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(ex.Code, ex.Message);
            }
            catch (DbUpdateConcurrencyException)
            {
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(
                    IvMasterErrorCode.Concurrency, "The Delivery Request changed by another user. Reload and try again.");
            }
            catch (DbUpdateException ex) when (IsUniqueConstraint(ex))
            {
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(
                    IvMasterErrorCode.DuplicateKey, "The Delivery Request number or source line already exists.");
            }
            catch (Exception ex) when (IsDeadlock(ex) && attempt < MaxRetries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken);
            }
            catch (Exception ex) when (IsDeadlock(ex))
            {
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "The Delivery Request could not be saved because another transaction kept the demand locked.");
            }
            catch (Exception ex) when (IsNumberingException(ex))
            {
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(IvMasterErrorCode.Validation, ex.Message);
            }
        }

        return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(
            IvMasterErrorCode.Concurrency, "The Delivery Request could not be saved because another transaction kept the demand locked.");
    }

    private async Task<IvMasterOperationResult<SaDeliveryRequestDetail>> ExecuteLifecycleAsync(
        SaDeliveryRequestCommandRequest request,
        string permission,
        LifecycleAction action,
        CancellationToken cancellationToken)
    {
        request ??= new SaDeliveryRequestCommandRequest();
        var auth = await AuthorizeAsync(permission, write: true, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<SaDeliveryRequestDetail>(auth.Error.Value);
        }

        var scope = auth.Scope!;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var header = await LockDeliveryRequestAsync(db, scope, request.Uid, cancellationToken)
                    ?? throw new DeliveryRequestCommandException(IvMasterErrorCode.NotFound, "Delivery Request not found.");
                CheckRowVersion(header.RowVersion, request.RowVersion, "Delivery Request changed by another user. Reload before changing status.");
                var sources = await LockSourcesAsync(db, scope, header.Uid, cancellationToken);
                var allocations = await LockAllocationsAsync(db, scope, header.Uid, cancellationToken);
                var now = DateTime.UtcNow;

                if (action == LifecycleAction.Release)
                {
                    if (header.Status != SaDeliveryRequestStatuses.Draft)
                    {
                        throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, "Only Draft Delivery Requests can be released.");
                    }

                    if (sources.Count == 0 || sources.Sum(x => x.AllocatedProductionQty) <= QuantityTolerance)
                    {
                        throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, "A Delivery Request must contain positive Sales Order demand before release.");
                    }

                    header.Status = SaDeliveryRequestStatuses.Released;
                    foreach (var source in sources.Where(x => x.IsActive))
                    {
                        source.ReleasedDate = now;
                        source.ReleasedBy = scope.UserId;
                        source.ReleaseReason = NullIfEmpty(request.Reason) ?? "Delivery Request released";
                    }

                    db.SaDeliveryRequestAuditEvents.Add(Audit(
                        header.Uid,
                        SaDeliveryRequestAuditEventTypes.Released,
                        details: new { SourceCount = sources.Count, ActiveAllocationQty = allocations.Where(x => x.IsActive).Sum(x => x.AllocatedQty) },
                        reason: request.Reason,
                        now: now,
                        actor: scope.UserId));
                }
                else
                {
                    if (header.Status is SaDeliveryRequestStatuses.Cancelled or SaDeliveryRequestStatuses.Completed)
                    {
                        throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, "This Delivery Request is already terminal.");
                    }

                    var hasLiveWork = allocations.Any(x => x.IsActive)
                        || await db.PrWorkOrderDemandAllocations.AsNoTracking()
                            .Where(x => x.DeliveryRequestId == header.Uid)
                            .Join(db.ProductionWorkOrders.AsNoTracking(), x => x.WorkOrderId, x => x.Uid, (a, wo) => wo)
                            .AnyAsync(x => x.Status != ProductionWorkOrderStatuses.Cancelled
                                && x.GoodQty > QuantityTolerance, cancellationToken);
                    if (hasLiveWork)
                    {
                        throw new DeliveryRequestCommandException(
                            IvMasterErrorCode.InUse,
                            "A Delivery Request with active or produced Work Order quantity cannot be cancelled.");
                    }

                    header.Status = SaDeliveryRequestStatuses.Cancelled;
                    foreach (var source in sources.Where(x => x.IsActive))
                    {
                        source.IsActive = false;
                        source.ReleasedDate = now;
                        source.ReleasedBy = scope.UserId;
                        source.ReleaseReason = NullIfEmpty(request.Reason) ?? "Delivery Request cancelled";
                    }

                    db.SaDeliveryRequestAuditEvents.Add(Audit(
                        header.Uid,
                        SaDeliveryRequestAuditEventTypes.Cancelled,
                        reason: request.Reason,
                        now: now,
                        actor: scope.UserId));
                }

                header.ModifiedDate = now;
                header.ModifiedBy = scope.UserId;
                TouchSqliteRowVersions(db);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return IvMasterOperationResult<SaDeliveryRequestDetail>.Ok(
                    await BuildDetailAsync(db, header, cancellationToken));
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (DeliveryRequestCommandException ex)
        {
            return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IvMasterOperationResult<SaDeliveryRequestDetail>.Fail(IvMasterErrorCode.Concurrency, "The Delivery Request changed by another user. Reload and try again.");
        }
    }

    private async Task<IvMasterOperationResult<long>> ExecuteDeleteAsync(
        SaDeliveryRequestCommandRequest request,
        CancellationToken cancellationToken)
    {
        request ??= new SaDeliveryRequestCommandRequest();
        var auth = await AuthorizeAsync(PermissionCodes.Delete, write: true, cancellationToken);
        if (auth.Error is not null)
        {
            return Failure<long>(auth.Error.Value);
        }

        var scope = auth.Scope!;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var header = await LockDeliveryRequestAsync(db, scope, request.Uid, cancellationToken)
                    ?? throw new DeliveryRequestCommandException(IvMasterErrorCode.NotFound, "Delivery Request not found.");
                CheckRowVersion(header.RowVersion, request.RowVersion, "Delivery Request changed by another user. Reload before deleting.");
                if (header.Status != SaDeliveryRequestStatuses.Draft)
                {
                    throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, "Only a never-released Draft Delivery Request can be deleted.");
                }

                var allocations = await LockAllocationsAsync(db, scope, header.Uid, cancellationToken);
                if (allocations.Count != 0)
                {
                    throw new DeliveryRequestCommandException(
                        IvMasterErrorCode.InUse,
                        "This Draft Delivery Request has Work Order history and cannot be deleted.");
                }

                var sources = await LockSourcesAsync(db, scope, header.Uid, cancellationToken);
                var audits = await db.SaDeliveryRequestAuditEvents
                    .Where(x => x.DeliveryRequestId == header.Uid)
                    .ToListAsync(cancellationToken);
                db.SaDeliveryRequestAuditEvents.RemoveRange(audits);
                db.SaDeliveryRequestSources.RemoveRange(sources);
                db.SaDeliveryRequests.Remove(header);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return IvMasterOperationResult<long>.Ok(header.Uid);
            }
            catch
            {
                await tx.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (DeliveryRequestCommandException ex)
        {
            return IvMasterOperationResult<long>.Fail(ex.Code, ex.Message);
        }
        catch (DbUpdateConcurrencyException)
        {
            return IvMasterOperationResult<long>.Fail(IvMasterErrorCode.Concurrency, "The Delivery Request changed by another user. Reload and try again.");
        }
    }

    private async Task<PreparedSources> PrepareSourceRowsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        SaDeliveryRequestDraftRequest request,
        long? currentDeliveryRequestId,
        CancellationToken cancellationToken)
    {
        var inputs = request.Sources ?? [];
        if (inputs.Count == 0)
        {
            return PreparedSources.Fail("At least one Sales Order line is required.", "Sources");
        }

        var normalized = inputs.Select(x => new SaDeliveryRequestSourceInput
            {
                SoNo = Normalize(x.SoNo),
                CustRel = x.CustRel,
                SoLine = x.SoLine,
                AllocatedProductionQty = x.AllocatedProductionQty
            })
            .OrderBy(x => x.SoNo, StringComparer.Ordinal)
            .ThenBy(x => x.CustRel)
            .ThenBy(x => x.SoLine)
            .ToList();
        if (normalized.Any(x => x.SoNo.Length == 0 || x.CustRel <= 0 || x.SoLine <= 0 || x.AllocatedProductionQty <= QuantityTolerance))
        {
            return PreparedSources.Fail("Each source must identify an exact SO revision/line and have positive production quantity.", "Sources");
        }

        if (normalized.Select(x => (x.SoNo.ToUpperInvariant(), x.CustRel, x.SoLine)).Distinct().Count() != normalized.Count)
        {
            return PreparedSources.Fail("The same Sales Order revision/line cannot be added twice to one Delivery Request.", "Sources");
        }

        var currentHeaders = new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase);
        foreach (var soNo in normalized.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var header = await LockCurrentSalesOrderAsync(db, scope, soNo, cancellationToken);
            if (header is null)
            {
                return PreparedSources.Fail($"Sales Order {soNo} was not found or has no current revision.", "Sources");
            }

            currentHeaders[soNo] = header;
        }

        var rows = new List<SaDeliveryRequestSource>();
        string? productCode = null;
        string? productionUom = null;
        string? productDescription = null;
        DateTime? earliestRequiredDate = null;
        decimal requestedQty = 0m;

        foreach (var input in normalized)
        {
            var header = currentHeaders[input.SoNo];
            if (header.CustRel != input.CustRel || !header.IsCurrent)
            {
                return PreparedSources.Fail(
                    $"Sales Order {input.SoNo} revision {input.CustRel} is not the current revision. Reload the eligible demand.",
                    "Sources");
            }

            var detail = await LockSalesOrderDetailAsync(db, scope, input, cancellationToken);
            if (detail is null)
            {
                return PreparedSources.Fail(
                    $"Sales Order {input.SoNo} revision {input.CustRel} line {input.SoLine} was not found.",
                    "Sources");
            }

            var lineProduct = Normalize(detail.ICode);
            var lineUom = Normalize(detail.StdUom);
            var sourceUom = Normalize(detail.SellingUom);
            if (sourceUom.Length == 0)
            {
                sourceUom = lineUom;
            }
            if (lineProduct.Length == 0 || detail.StdQty <= QuantityTolerance || lineUom.Length == 0)
            {
                return PreparedSources.Fail(
                    $"Sales Order {input.SoNo} line {input.SoLine} has no positive StdQty/StdUom production demand.",
                    "Sources");
            }

            if (productCode is null)
            {
                productCode = lineProduct;
                productionUom = lineUom;
                productDescription = NullIfEmpty(detail.IDesc);
            }
            else if (!string.Equals(productCode, lineProduct, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(productionUom, lineUom, StringComparison.OrdinalIgnoreCase))
            {
                return PreparedSources.Fail(
                    "All Sales Order sources in one Delivery Request must have the same product and production UOM.",
                    "Sources");
            }

            var activeSourceRows = await LockActiveSourceAllocationsAsync(
                db, scope, input, currentDeliveryRequestId, cancellationToken);
            var existingAllocated = activeSourceRows.Sum(x => x.AllocatedProductionQty);
            var available = Unplanned(detail.StdQty, existingAllocated);
            if (input.AllocatedProductionQty - available > QuantityTolerance)
            {
                return PreparedSources.Fail(
                    $"Sales Order {input.SoNo} line {input.SoLine} has only {available:N4} available production quantity.",
                    "Sources");
            }

            var requiredDate = detail.DeliveryDate?.Date;
            if (requiredDate is DateTime date && (earliestRequiredDate is null || date < earliestRequiredDate.Value))
            {
                earliestRequiredDate = date;
            }

            requestedQty += input.AllocatedProductionQty;
            rows.Add(new SaDeliveryRequestSource
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                SoNo = input.SoNo,
                CustRel = input.CustRel,
                SoLine = input.SoLine,
                ProductCode = lineProduct,
                SourceUom = sourceUom,
                ProductionUom = lineUom,
                SourceQty = detail.OrderQty,
                AllocatedProductionQty = input.AllocatedProductionQty,
                CustomerCode = NullIfEmpty(header.CustCode),
                RequestedDeliveryDate = detail.DeliveryDate,
                IsActive = true
            });
        }

        if (productCode is null || productionUom is null)
        {
            return PreparedSources.Fail("A valid production source is required.", "Sources");
        }

        var requestProduct = NullIfEmpty(request.ProductCode);
        if (requestProduct is not null && !string.Equals(requestProduct, productCode, StringComparison.OrdinalIgnoreCase))
        {
            return PreparedSources.Fail("The Delivery Request product does not match its Sales Order sources.", "ProductCode");
        }

        var requestUom = NullIfEmpty(request.ProductionUom);
        if (requestUom is not null && !string.Equals(requestUom, productionUom, StringComparison.OrdinalIgnoreCase))
        {
            return PreparedSources.Fail("The Delivery Request production UOM does not match its Sales Order sources.", "ProductionUom");
        }

        var requiredDateValue = request.RequiredDate == default
            ? earliestRequiredDate ?? DateTime.UtcNow.Date
            : request.RequiredDate.Date;
        if (earliestRequiredDate is DateTime earliest && requiredDateValue > earliest)
        {
            return PreparedSources.Fail(
                "Required Date cannot be later than the earliest selected Sales Order delivery requirement.",
                "RequiredDate");
        }

        if (request.RequestedQty > QuantityTolerance
            && Math.Abs(request.RequestedQty - requestedQty) > QuantityTolerance)
        {
            return PreparedSources.Fail(
                $"Requested Qty must equal the selected active source quantity ({requestedQty:N4}).",
                "RequestedQty");
        }

        return PreparedSources.Ok(
            productCode,
            productDescription,
            productionUom,
            requestedQty,
            requiredDateValue,
            rows);
    }

    private async Task<string> AllocateNumberAsync(
        AppDbContext db,
        SaDeliveryRequestDraftRequest request,
        InventoryTenantScope scope,
        CancellationToken cancellationToken)
    {
        var explicitNumber = NullIfEmpty(request.DeliveryRequestNo);
        if (explicitNumber is not null)
        {
            if (explicitNumber.Length > 30)
            {
                throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, "Delivery Request number must be 30 characters or fewer.");
            }

            if (await db.SaDeliveryRequests.AnyAsync(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.DeliveryRequestNo == explicitNumber, cancellationToken))
            {
                throw new DeliveryRequestCommandException(IvMasterErrorCode.DuplicateKey, "Delivery Request number already exists.");
            }

            return explicitNumber;
        }

        try
        {
            var issued = await _documentNumbers.NextAsync(
                db,
                NumberingModule,
                "",
                request.RequiredDate == default ? DateTime.UtcNow.Date : request.RequiredDate,
                DocumentNumberRequestMode.New,
                "AUTO",
                cancellationToken);
            return issued.DocumentNumber;
        }
        catch (Exception ex) when (IsNumberingException(ex))
        {
            throw new DeliveryRequestCommandException(IvMasterErrorCode.Validation, ex.Message);
        }
    }

    private static void ApplyHeader(
        SaDeliveryRequest header,
        SaDeliveryRequestDraftRequest request,
        string productCode,
        string? productDescription,
        string productionUom,
        decimal requestedQty,
        DateTime requiredDate,
        string actor,
        DateTime now)
    {
        header.ProductCode = productCode;
        header.ProductDescription = TrimTo(productDescription, 200);
        header.ProductionUom = productionUom;
        header.RequestedQty = requestedQty;
        header.RequiredDate = requiredDate;
        header.DefinitionCode = TrimTo(request.DefinitionCode, 30);
        header.WarehouseCode = TrimTo(request.WarehouseCode, 20);
        header.ProjectCode = TrimTo(request.ProjectCode, 20);
        header.Priority = TrimTo(request.Priority, 20);
        header.Remark = TrimTo(request.Remark, 500);
        header.ModifiedDate = now;
        header.ModifiedBy = actor;
    }

    private async Task<SaDeliveryRequestDetail> BuildDetailAsync(
        AppDbContext db,
        SaDeliveryRequest header,
        CancellationToken cancellationToken)
    {
        var sources = await db.SaDeliveryRequestSources.AsNoTracking()
            .Where(x => x.DeliveryRequestId == header.Uid)
            .OrderBy(x => x.SoNo)
            .ThenBy(x => x.CustRel)
            .ThenBy(x => x.SoLine)
            .ToListAsync(cancellationToken);
        var activeSourceAllocations = sources.Count == 0
            ? []
            : await db.SaDeliveryRequestSources.AsNoTracking()
                .Where(x => x.CompanyCode == header.CompanyCode
                    && x.BranchCode == header.BranchCode
                    && x.IsActive
                    && sources.Select(s => s.SoNo).Contains(x.SoNo)
                    && sources.Select(s => s.CustRel).Contains(x.CustRel))
                .GroupBy(x => new { x.SoNo, x.CustRel, x.SoLine })
                .Select(g => new { g.Key.SoNo, g.Key.CustRel, g.Key.SoLine, Qty = g.Sum(x => x.AllocatedProductionQty) })
                .ToListAsync(cancellationToken);
        var activeBySource = activeSourceAllocations.ToDictionary(
            x => (x.SoNo.ToUpperInvariant(), x.CustRel, x.SoLine), x => x.Qty);
        var productionDemandBySource = sources.Count == 0
            ? new Dictionary<(string SoNo, short CustRel, short SoLine), decimal>()
            : await (
                from source in db.SaDeliveryRequestSources.AsNoTracking()
                join detail in db.SaSoDetails.AsNoTracking()
                    on new { source.CompanyCode, source.BranchCode, source.SoNo, source.CustRel, SOLine = source.SoLine }
                    equals new { detail.CompanyCode, detail.BranchCode, detail.SoNo, detail.CustRel, SOLine = detail.Line }
                where source.DeliveryRequestId == header.Uid
                select new
                {
                    source.SoNo,
                    source.CustRel,
                    SoLine = source.SoLine,
                    detail.StdQty
                }).ToDictionaryAsync(
                    x => (x.SoNo.ToUpperInvariant(), x.CustRel, x.SoLine),
                    x => x.StdQty,
                    cancellationToken);

        var allocationRows = await db.PrWorkOrderDemandAllocations.AsNoTracking()
            .Where(x => x.DeliveryRequestId == header.Uid)
            .Join(db.ProductionWorkOrders.AsNoTracking(), x => x.WorkOrderId, x => x.Uid, (allocation, workOrder) => new
            {
                Allocation = allocation,
                WorkOrder = workOrder
            })
            .OrderBy(x => x.WorkOrder.WorkOrderNo)
            .ToListAsync(cancellationToken);
        var audits = await db.SaDeliveryRequestAuditEvents.AsNoTracking()
            .Where(x => x.DeliveryRequestId == header.Uid)
            .OrderByDescending(x => x.OccurredDate)
            .ThenByDescending(x => x.Uid)
            .Take(200)
            .ToListAsync(cancellationToken);

        var progress = new ProgressFacts(
            allocationRows.Where(x => x.Allocation.IsActive).Sum(x => x.Allocation.AllocatedQty),
            allocationRows.Where(x => x.WorkOrder.Status != ProductionWorkOrderStatuses.Cancelled)
                .Sum(x => x.WorkOrder.GoodQty),
            allocationRows.Count,
            allocationRows.Any(x => x.Allocation.IsActive
                && (x.WorkOrder.GoodQty > QuantityTolerance
                    || x.WorkOrder.Status is ProductionWorkOrderStatuses.InProgress
                        or ProductionWorkOrderStatuses.Completed
                        or ProductionWorkOrderStatuses.Closed)));

        return new SaDeliveryRequestDetail
        {
            Uid = header.Uid,
            DeliveryRequestNo = header.DeliveryRequestNo,
            CompanyCode = header.CompanyCode,
            BranchCode = header.BranchCode,
            ProductCode = header.ProductCode,
            ProductDescription = header.ProductDescription,
            ProductionUom = header.ProductionUom,
            RequestedQty = header.RequestedQty,
            WoAllocatedQty = progress.AllocatedQty,
            UnplannedQty = Unplanned(header.RequestedQty, progress.AllocatedQty),
            ProducedQty = progress.ProducedQty,
            RequiredDate = header.RequiredDate,
            DefinitionCode = header.DefinitionCode,
            WarehouseCode = header.WarehouseCode,
            ProjectCode = header.ProjectCode,
            Priority = header.Priority,
            Status = DerivedStatus(header.Status, header.RequestedQty, progress),
            Remark = header.Remark,
            CreatedDate = header.CreatedDate,
            CreatedBy = header.CreatedBy,
            ModifiedDate = header.ModifiedDate,
            ModifiedBy = header.ModifiedBy,
            RowVersion = header.RowVersion ?? [],
            Sources = sources.Select(source => new SaDeliveryRequestSourceTrace
            {
                Uid = source.Uid,
                SoNo = source.SoNo,
                CustRel = source.CustRel,
                SoLine = source.SoLine,
                ProductCode = source.ProductCode,
                SourceUom = source.SourceUom,
                ProductionUom = source.ProductionUom,
                SourceQty = source.SourceQty,
                ProductionDemandQty = productionDemandBySource.GetValueOrDefault(
                    (source.SoNo.ToUpperInvariant(), source.CustRel, source.SoLine), source.SourceQty),
                AllocatedProductionQty = source.AllocatedProductionQty,
                ActiveAllocatedProductionQty = activeBySource.GetValueOrDefault((source.SoNo.ToUpperInvariant(), source.CustRel, source.SoLine)),
                AvailableForDr = Unplanned(
                    productionDemandBySource.GetValueOrDefault(
                        (source.SoNo.ToUpperInvariant(), source.CustRel, source.SoLine), source.SourceQty),
                    activeBySource.GetValueOrDefault((source.SoNo.ToUpperInvariant(), source.CustRel, source.SoLine))),
                CustomerCode = source.CustomerCode,
                RequestedDeliveryDate = source.RequestedDeliveryDate,
                IsActive = source.IsActive,
                ReleasedDate = source.ReleasedDate,
                ReleasedBy = source.ReleasedBy,
                RowVersion = source.RowVersion ?? []
            }).ToList(),
            WorkOrders = allocationRows.Select(x => new SaDeliveryRequestWorkOrderTrace
            {
                WorkOrderId = x.WorkOrder.Uid,
                WorkOrderNo = x.WorkOrder.WorkOrderNo,
                AllocatedQty = x.Allocation.AllocatedQty,
                IsActive = x.Allocation.IsActive,
                Status = x.WorkOrder.Status,
                PlannedQty = x.WorkOrder.PlannedQty,
                GoodQty = x.WorkOrder.GoodQty,
                ReleasedDate = x.WorkOrder.ReleasedDate,
                CreatedDate = x.WorkOrder.CreatedDate,
                RowVersion = x.WorkOrder.RowVersion ?? []
            }).ToList(),
            AuditEvents = audits.Select(x => new SaDeliveryRequestAuditTrace
            {
                Uid = x.Uid,
                EventType = x.EventType,
                SourceId = x.SourceId,
                WorkOrderId = x.WorkOrderId,
                Reason = x.Reason,
                OccurredDate = x.OccurredDate,
                ActorUserId = x.ActorUserId
            }).ToList()
        };
    }

    private async Task<SaSo?> LockCurrentSalesOrderAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string soNo,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.SaSos.FromSqlInterpolated($@"
SELECT * FROM dbo.SaSO WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
  AND SONo = {soNo} AND IsCurrent = 1")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SaSos.SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.SoNo == soNo
            && x.IsCurrent, cancellationToken);
    }

    private async Task<SaSoDetail?> LockSalesOrderDetailAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        SaDeliveryRequestSourceInput input,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.SaSoDetails.FromSqlInterpolated($@"
SELECT * FROM dbo.SaSODetail WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
  AND SONo = {input.SoNo} AND CustRel = {input.CustRel} AND Line = {input.SoLine}")
                .AsTracking()
                .SingleOrDefaultAsync(cancellationToken);
        }

        return await db.SaSoDetails.SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
            && x.BranchCode == scope.BranchCode
            && x.SoNo == input.SoNo
            && x.CustRel == input.CustRel
            && x.Line == input.SoLine, cancellationToken);
    }

    private async Task<SaDeliveryRequest?> LockDeliveryRequestAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long uid,
        CancellationToken cancellationToken)
    {
        if (uid <= 0)
        {
            return null;
        }

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

    private async Task<List<SaDeliveryRequestSource>> LockSourcesAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        long deliveryRequestId,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            return await db.SaDeliveryRequestSources.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequestSource WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE DeliveryRequestID = {deliveryRequestId}
  AND CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
ORDER BY SONo, CustRel, SOLine")
                .AsTracking()
                .ToListAsync(cancellationToken);
        }

        return await db.SaDeliveryRequestSources
            .Where(x => x.DeliveryRequestId == deliveryRequestId
                && x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode)
            .OrderBy(x => x.SoNo).ThenBy(x => x.CustRel).ThenBy(x => x.SoLine)
            .ToListAsync(cancellationToken);
    }

    private async Task<List<PrWorkOrderDemandAllocation>> LockAllocationsAsync(
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

    private async Task<List<SaDeliveryRequestSource>> LockActiveSourceAllocationsAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        SaDeliveryRequestSourceInput input,
        long? currentDeliveryRequestId,
        CancellationToken cancellationToken)
    {
        if (IsSqlServer(db))
        {
            if (currentDeliveryRequestId is long currentId)
            {
                return await db.SaDeliveryRequestSources.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequestSource WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
  AND SONo = {input.SoNo} AND CustRel = {input.CustRel} AND SOLine = {input.SoLine}
  AND IsActive = 1 AND DeliveryRequestID <> {currentId}
ORDER BY DeliveryRequestID")
                    .AsTracking()
                    .ToListAsync(cancellationToken);
            }

            return await db.SaDeliveryRequestSources.FromSqlInterpolated($@"
SELECT * FROM dbo.SaDeliveryRequestSource WITH (UPDLOCK, ROWLOCK, HOLDLOCK)
WHERE CompanyCode = {scope.CompanyCode} AND BranchCode = {scope.BranchCode}
  AND SONo = {input.SoNo} AND CustRel = {input.CustRel} AND SOLine = {input.SoLine}
  AND IsActive = 1
ORDER BY DeliveryRequestID")
                .AsTracking()
                .ToListAsync(cancellationToken);
        }

        var query = db.SaDeliveryRequestSources
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.SoNo == input.SoNo
                && x.CustRel == input.CustRel
                && x.SoLine == input.SoLine
                && x.IsActive);
        if (currentDeliveryRequestId is long currentIdForSqlite)
        {
            query = query.Where(x => x.DeliveryRequestId != currentIdForSqlite);
        }

        return await query.OrderBy(x => x.DeliveryRequestId).ToListAsync(cancellationToken);
    }

    private async Task<(InventoryTenantScope? Scope, (IvMasterErrorCode Code, string Message)? Error)> AuthorizeAsync(
        string permission,
        bool write,
        CancellationToken cancellationToken)
    {
        var scope = write ? _tenant.TryWriteScope() : _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
        {
            return (null, (IvMasterErrorCode.InvalidScope,
                write ? "A valid company, branch and location context is required." : "A valid company and branch context is required."));
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesDeliveryRequest, permission, cancellationToken))
        {
            return (null, (IvMasterErrorCode.AccessDenied, "Not authorized."));
        }

        return (scope, null);
    }

    private static SaDeliveryRequestAuditEvent Audit(
        long deliveryRequestId,
        string eventType,
        long? sourceId = null,
        long? workOrderId = null,
        object? details = null,
        string? reason = null,
        DateTime now = default,
        string actor = "") => new()
        {
            DeliveryRequestId = deliveryRequestId,
            EventType = eventType,
            SourceId = sourceId,
            WorkOrderId = workOrderId,
            DetailsJson = details is null ? null : JsonSerializer.Serialize(details),
            Reason = NullIfEmpty(reason),
            OccurredDate = now == default ? DateTime.UtcNow : now,
            ActorUserId = actor
        };

    private static (string SoNo, short CustRel, short SoLine) SourceKey(SaDeliveryRequestSource source) =>
        (Normalize(source.SoNo), source.CustRel, source.SoLine);

    private static void CopySourceValues(SaDeliveryRequestSource target, SaDeliveryRequestSource source)
    {
        target.ProductCode = source.ProductCode;
        target.SourceUom = source.SourceUom;
        target.ProductionUom = source.ProductionUom;
        target.SourceQty = source.SourceQty;
        target.AllocatedProductionQty = source.AllocatedProductionQty;
        target.CustomerCode = source.CustomerCode;
        target.RequestedDeliveryDate = source.RequestedDeliveryDate;
        target.IsActive = true;
        target.ReleasedDate = null;
        target.ReleasedBy = null;
        target.ReleaseReason = null;
    }

    private static void CheckRowVersion(byte[] actual, byte[] expected, string message)
    {
        if (expected is not { Length: > 0 } || actual is not { Length: > 0 } || !actual.SequenceEqual(expected))
        {
            throw new DeliveryRequestCommandException(IvMasterErrorCode.Concurrency, message);
        }
    }

    private static void TouchSqliteRowVersions(AppDbContext db)
    {
        if (!IsSqlite(db))
        {
            return;
        }

        foreach (var entry in db.ChangeTracker.Entries().Where(x => x.State is EntityState.Added or EntityState.Modified))
        {
            switch (entry.Entity)
            {
                case SaDeliveryRequest request:
                    request.RowVersion = Guid.NewGuid().ToByteArray();
                    break;
                case SaDeliveryRequestSource source:
                    source.RowVersion = Guid.NewGuid().ToByteArray();
                    break;
                case PrWorkOrderDemandAllocation allocation:
                    allocation.RowVersion = Guid.NewGuid().ToByteArray();
                    break;
            }
        }
    }

    private static bool SameSource(SaDeliveryRequestSourceTrace source, SaDeliveryRequestSourceInput input) =>
        string.Equals(source.SoNo, Normalize(input.SoNo), StringComparison.OrdinalIgnoreCase)
        && source.CustRel == input.CustRel
        && source.SoLine == input.SoLine;

    private static string DerivedStatus(string storedStatus, decimal requestedQty, ProgressFacts facts)
    {
        if (storedStatus == SaDeliveryRequestStatuses.Cancelled)
        {
            return storedStatus;
        }

        if (requestedQty > 0m && facts.ProducedQty + QuantityTolerance >= requestedQty)
        {
            return SaDeliveryRequestStatuses.Completed;
        }

        if (facts.HasStarted)
        {
            return SaDeliveryRequestStatuses.InProduction;
        }

        return storedStatus;
    }

    private static decimal Unplanned(decimal requested, decimal allocated) =>
        Math.Max(requested - allocated, 0m);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    private static string? NullIfEmpty(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string? TrimTo(string? value, int maxLength)
    {
        var normalized = NullIfEmpty(value);
        return normalized is null || normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static bool IsSqlite(AppDbContext db) =>
        db.Database.ProviderName?.Contains("Sqlite", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsSqlServer(AppDbContext db) =>
        db.Database.ProviderName?.Contains("SqlServer", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsUniqueConstraint(DbUpdateException exception) =>
        exception.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsDeadlock(Exception exception) =>
        exception is SqlException sql && sql.Number == 1205
        || exception.InnerException is SqlException inner && inner.Number == 1205;

    private static bool IsNumberingException(Exception exception) => exception is
        DocumentNumberingNotConfiguredException
        or DocumentNumberingConfigurationException
        or DocumentNumberingOverflowException
        or DocumentNumberingConcurrencyException
        or DuplicateDocumentNumberException;

    private static IvMasterOperationResult<T> Failure<T>((IvMasterErrorCode Code, string Message) error) =>
        IvMasterOperationResult<T>.Fail(error.Code, error.Message);

    private static IvMasterOperationResult<T> Invalid<T>(string message, string field) =>
        IvMasterOperationResult<T>.Fail(
            IvMasterErrorCode.Validation,
            message,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message });

    private sealed record ProgressFacts(
        decimal AllocatedQty,
        decimal ProducedQty,
        int WorkOrderCount,
        bool HasStarted)
    {
        public static ProgressFacts Empty { get; } = new(0m, 0m, 0, false);
    }

    private sealed record PreparedSources(
        string ProductCode,
        string? ProductDescription,
        string ProductionUom,
        decimal RequestedQty,
        DateTime RequiredDate,
        List<SaDeliveryRequestSource> Sources,
        DeliveryRequestCommandException? Error)
    {
        public static PreparedSources Ok(
            string productCode,
            string? productDescription,
            string productionUom,
            decimal requestedQty,
            DateTime requiredDate,
            List<SaDeliveryRequestSource> sources) =>
            new(productCode, productDescription, productionUom, requestedQty, requiredDate, sources, null);

        public static PreparedSources Fail(string message, string field) =>
            new(string.Empty, null, string.Empty, 0m, default, [],
                new DeliveryRequestCommandException(
                    IvMasterErrorCode.Validation,
                    message,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [field] = message }));
    }

    private enum LifecycleAction
    {
        Release,
        Cancel
    }

    private sealed class DeliveryRequestCommandException : Exception
    {
        public DeliveryRequestCommandException(
            IvMasterErrorCode code,
            string message,
            IReadOnlyDictionary<string, string>? validationErrors = null)
            : base(message)
        {
            Code = code;
            ValidationErrors = validationErrors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public IvMasterErrorCode Code { get; }
        public IReadOnlyDictionary<string, string> ValidationErrors { get; }
    }
}
