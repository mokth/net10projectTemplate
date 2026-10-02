using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

public sealed partial class ProductionWorkOrderService
{
    private static readonly string[] ReopenBlockingChangeOrderStatuses =
    [
        ProductionChangeOrderStatuses.Draft,
        ProductionChangeOrderStatuses.Requested,
        ProductionChangeOrderStatuses.Approved,
        ProductionChangeOrderStatuses.Applied
    ];

    /// <summary>
    /// Returns a Released Work Order to Draft when no production execution has started.
    /// Concurrency: do not UPDLOCK PostingLinks before the Work Order — new MI drafts lock WO then
    /// insert a PostingLink. Use a read-only PostingLink preflight, lock WO, then re-check.
    /// When daily output posts onto this aggregate, extend the guard with WorkOrderID/OperationID
    /// facts; do not infer blockers from legacy PrSchDailyProd.ScheCode text.
    /// </summary>
    public async Task<IvMasterOperationResult<ProductionWorkOrderDetail>> ReopenForEditAsync(
        ProductionWorkOrderReopenRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new ProductionWorkOrderReopenRequest();

        var auth = await AuthorizeAsync(PermissionCodes.Reopen, requireWriteScope: true, cancellationToken);
        if (auth.Error is not null)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(auth.Error.Value.Code, auth.Error.Value.Message);
        }

        var number = Normalize(request.WorkOrderNo);
        var cleanReason = (request.Reason ?? string.Empty).Trim();
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (number.Length == 0)
        {
            errors["WorkOrderNo"] = "Work Order number is required.";
        }

        if (cleanReason.Length == 0)
        {
            errors["Reason"] = "Reopen reason is required.";
        }
        else if (cleanReason.Length > CancellationReasonMax)
        {
            errors["Reason"] = $"Reopen reason must be {CancellationReasonMax} characters or fewer.";
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Validation, "Correct the highlighted fields.", errors);
        }

        if (request.RowVersion is not { Length: > 0 })
        {
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "The Work Order version is missing. Reload before reopening.");
        }

        var scope = auth.Scope!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var identity = await db.ProductionWorkOrders
                .AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                    && x.BranchCode == scope.BranchCode
                    && x.WorkOrderNo == number)
                .Select(x => new { x.Uid, x.WorkOrderNo })
                .SingleOrDefaultAsync(cancellationToken);
            if (identity is null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            var preflightBlocker = await GetReopenPostingLinkBlockerAsync(db, identity.Uid, cancellationToken);
            if (preflightBlocker is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse, preflightBlocker);
            }

            var order = await LockWorkOrderForReopenAsync(
                db, scope.CompanyCode, scope.BranchCode!, number, cancellationToken);
            if (order is null
                || !string.Equals(order.CompanyCode, scope.CompanyCode, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(order.BranchCode, scope.BranchCode, StringComparison.OrdinalIgnoreCase))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound, "Work Order not found.");
            }

            if (!ProductionWorkOrderRules.CanReopen(order.Status))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Validation,
                    "Only a Released Work Order can be reopened for editing.");
            }

            if (!order.RowVersion.SequenceEqual(request.RowVersion))
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.Concurrency,
                    "This Work Order was changed by another user. Reload the latest version before reopening.");
            }

            db.Entry(order).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            var blocker = await EnsureNoReopenBlockersAsync(db, order, cancellationToken);
            if (blocker is not null)
            {
                return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.InUse, blocker);
            }

            // Lifecycle transition only — do not RequireCurrentSnapshot or rebuild/upgrade snapshot.
            var now = DateTime.UtcNow;
            order.Status = ProductionWorkOrderStatuses.Draft;
            order.ReleasedDate = null;
            order.ReleasedBy = null;
            order.ModifiedDate = now;
            order.ModifiedBy = scope.UserId;

            db.ProductionAuditEvents.Add(new ProductionAuditEvent
            {
                WorkOrderId = order.Uid,
                EventType = ProductionAuditEventTypes.ReopenedForEdit,
                FromStatus = ProductionWorkOrderStatuses.Released,
                ToStatus = ProductionWorkOrderStatuses.Draft,
                SnapshotRevision = order.SnapshotRevision,
                Reason = cleanReason,
                OccurredDate = now,
                ActorUserId = scope.UserId
            });

            TouchSqliteRowVersions(db, order);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var refreshed = await LoadAggregateAsync(db, scope, order.WorkOrderNo, tracking: false, cancellationToken);
            return refreshed is null
                ? IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                    IvMasterErrorCode.NotFound,
                    "Work Order was not found after reopening.")
                : IvMasterOperationResult<ProductionWorkOrderDetail>.Ok(MapDetail(refreshed));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<ProductionWorkOrderDetail>.Fail(
                IvMasterErrorCode.Concurrency,
                "This Work Order was changed by another user. Reload the latest version before reopening.");
        }
    }

    private static async Task<string?> GetReopenPostingLinkBlockerAsync(
        AppDbContext db,
        long workOrderId,
        CancellationToken cancellationToken)
    {
        var statuses = await db.ProductionPostingLinks
            .AsNoTracking()
            .Where(x => x.WorkOrderId == workOrderId)
            .Select(x => x.Status)
            .Distinct()
            .ToListAsync(cancellationToken);

        return DescribePostingLinkBlocker(statuses);
    }

    private static string? DescribePostingLinkBlocker(IReadOnlyCollection<string> statuses)
    {
        if (statuses.Any(s => string.Equals(s, ProductionPostingLinkStatuses.Draft, StringComparison.OrdinalIgnoreCase)))
        {
            return "A Material Issue draft exists for this Work Order. Cancel the Material Issue draft before reopening the Work Order.";
        }

        if (statuses.Any(s => string.Equals(s, ProductionPostingLinkStatuses.Pending, StringComparison.OrdinalIgnoreCase)))
        {
            return "A production posting is in progress for this Work Order. Retry reopening after it resolves.";
        }

        if (statuses.Any(s =>
                string.Equals(s, ProductionPostingLinkStatuses.Succeeded, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, ProductionPostingLinkStatuses.Reversed, StringComparison.OrdinalIgnoreCase)))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        return null;
    }

    private static async Task<ProductionWorkOrder?> LockWorkOrderForReopenAsync(
        AppDbContext db,
        string company,
        string branch,
        string workOrderNo,
        CancellationToken cancellationToken)
    {
        return db.Database.IsSqlServer()
            ? await db.ProductionWorkOrders
                .FromSqlInterpolated($@"
                    SELECT *
                    FROM dbo.PrWorkOrder WITH (UPDLOCK, HOLDLOCK)
                    WHERE CompanyCode = {company}
                      AND BranchCode = {branch}
                      AND WorkOrderNo = {workOrderNo}")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.ProductionWorkOrders
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == company
                        && x.BranchCode == branch
                        && x.WorkOrderNo == workOrderNo,
                    cancellationToken);
    }

    private static async Task<string?> EnsureNoReopenBlockersAsync(
        AppDbContext db,
        ProductionWorkOrder order,
        CancellationToken cancellationToken)
    {
        var postingBlocker = await GetReopenPostingLinkBlockerAsync(db, order.Uid, cancellationToken);
        if (postingBlocker is not null)
        {
            return postingBlocker;
        }

        var hasMovement = await db.ProductionMaterialMovements
            .AsNoTracking()
            .AnyAsync(x => x.WorkOrderId == order.Uid, cancellationToken);
        if (hasMovement)
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        if (HasHeaderExecutionProjection(order))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        var operations = await db.ProductionWorkOrderOperations
            .AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid)
            .Select(x => new
            {
                x.InputQty,
                x.ProcessedQty,
                x.GoodQty,
                x.ScrapQty,
                x.RejectQty,
                x.HoldQty,
                x.ReworkQty,
                x.TransferredQty
            })
            .ToListAsync(cancellationToken);
        if (operations.Any(op =>
                HasExecutionProjection(
                    op.InputQty, op.ProcessedQty, op.GoodQty, op.ScrapQty,
                    op.RejectQty, op.HoldQty, op.ReworkQty, op.TransferredQty)))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        var materials = await db.ProductionWorkOrderMaterials
            .AsNoTracking()
            .Where(x => x.WorkOrderId == order.Uid)
            .Select(x => new
            {
                x.ReservedQty,
                x.PickedQty,
                x.IssuedQty,
                x.ReturnedQty,
                x.ConsumedQty,
                x.VarianceQty
            })
            .ToListAsync(cancellationToken);
        if (materials.Any(m =>
                HasExecutionProjection(
                    m.ReservedQty, m.PickedQty, m.IssuedQty,
                    m.ReturnedQty, m.ConsumedQty, m.VarianceQty)))
        {
            return "This Work Order has production execution history and cannot be reopened as Draft. Use the controlled Change Order/correction process.";
        }

        var hasOpenChangeOrder = await db.ProductionChangeOrders
            .AsNoTracking()
            .AnyAsync(
                x => x.WorkOrderId == order.Uid
                    && ReopenBlockingChangeOrderStatuses.Contains(x.Status),
                cancellationToken);
        if (hasOpenChangeOrder)
        {
            return "An active or applied Change Order exists for this Work Order. Resolve it before reopening as Draft.";
        }

        return null;
    }

    private static bool HasHeaderExecutionProjection(ProductionWorkOrder order) =>
        HasExecutionProjection(
            order.GoodQty,
            order.ScrapQty,
            order.RejectQty,
            order.HoldQty,
            order.ApprovedVarianceQty);

    private static bool HasExecutionProjection(params decimal[] quantities) =>
        quantities.Any(q => q != 0m);
}
