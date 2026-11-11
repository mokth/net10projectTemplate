using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Subcon;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Subcon;

public sealed class SubconApplicationService : ISubconApplicationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _access;
    private readonly IRunningNumberService _numbers;
    private readonly ISubconInventorySourceAdapter _inventory;
    private readonly ISubconProductionSourceAdapter _production;

    public SubconApplicationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService access,
        IRunningNumberService numbers,
        ISubconInventorySourceAdapter inventory,
        ISubconProductionSourceAdapter production)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _access = access;
        _numbers = numbers;
        _inventory = inventory;
        _production = production;
    }

    public async Task<SubconResult> GetPolicyAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (!await CanAsync(MenuCodes.SubconSettings, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Access, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        return SubconResult.OkPolicy(policy is null ? DefaultPolicy() : MapPolicy(policy));
    }

    public async Task<SubconResult> SavePolicyAsync(SubconPolicySaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryWriteScope() ?? _tenant.TryCompanyScope();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (!await CanAsync(MenuCodes.SubconSettings, PermissionCodes.Edit, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (command.SubconEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.LiveEnablementFrozen);
        }

        if (command.InventoryModeEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.InventoryModeFrozen);
        }

        if (command.ProductionModeEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.ProductionModeFrozen);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var published = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        if (published is null)
        {
            published = new SubconPolicy
            {
                CompanyCode = scope.CompanyCode,
                Version = 1,
                IsPublished = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.SubconPolicies.Add(published);
        }
        else if (Stale(published.RowVersion, command.RowVersion))
        {
            return SubconResult.Fail(SubconMessages.ChangedReload);
        }

        published.SubconEnabled = false;
        published.InventoryModeEnabled = false;
        published.ProductionModeEnabled = false;
        published.IsPublished = true;
        published.ModifiedDate = DateTime.UtcNow;
        published.ModifiedBy = TruncateUser(scope.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return SubconResult.OkPolicy(MapPolicy(published));
    }

    public async Task<SubconResult> GetWorkbenchAsync(SubconListQuery query, CancellationToken cancellationToken = default)
    {
        var list = await SearchAsync(query with { Kind = SubconListKinds.Dispatch }, cancellationToken);
        if (!list.Succeeded || list.ListPage is null)
        {
            return list;
        }

        var open = list.ListPage.Rows.Count(x => x.Status == SubconStatuses.New);
        var overdue = list.ListPage.Rows.Count(x =>
            x.PromisedReturnDate is { } due && due.Date < DateTime.Today && x.Status == SubconStatuses.New);
        var byUom = list.ListPage.Rows
            .Where(x => !string.IsNullOrWhiteSpace(x.Uom) && x.Status == SubconStatuses.New)
            .GroupBy(x => x.Uom!, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Sum(x => x.SentQty):0.####} {g.Key}")
            .ToArray();

        return SubconResult.OkWorkbench(new SubconWorkbenchPage
        {
            OpenLineCount = list.ListPage.Rows.Sum(x => x.LineCount),
            OverdueLineCount = overdue,
            DraftCount = open,
            OutstandingByUom = byUom,
            Rows = list.ListPage.Rows,
            TotalCount = list.ListPage.TotalCount
        }, list.CanUseProductionMode);
    }

    public async Task<SubconResult> SearchAsync(SubconListQuery query, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        var menu = query.Kind == SubconListKinds.Receipt ? MenuCodes.SubconReceiveBack : MenuCodes.SubconSendOut;
        if (!await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(menu, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(MenuCodes.SubconInquiry, PermissionCodes.Access, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        if (query.Kind == SubconListKinds.Receipt)
        {
            var receipts = db.SubconReceipts.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode!);
            receipts = ApplyReceiptFilters(receipts, query);
            var total = await receipts.CountAsync(cancellationToken);
            var rows = await receipts
                .OrderByDescending(x => x.TrxDate)
                .ThenByDescending(x => x.DocNo)
                .Skip(Math.Max(0, query.Skip))
                .Take(Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100))
                .Select(x => new SubconListRow
                {
                    DocNo = x.DocNo,
                    TrxDate = x.TrxDate,
                    Status = x.Status,
                    Mode = string.Empty,
                    SupplierCode = x.SupplierCode,
                    SupplierName = x.SupplierName,
                    LineCount = x.Lines.Count,
                    Kind = SubconListKinds.Receipt
                })
                .ToListAsync(cancellationToken);
            return SubconResult.OkList(new SubconListPage { Rows = rows, TotalCount = total }, await CanUseProductionAsync(cancellationToken));
        }

        var dispatches = db.SubconDispatches.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode!);
        dispatches = ApplyDispatchFilters(dispatches, query);
        var dispatchTotal = await dispatches.CountAsync(cancellationToken);
        var dispatchRows = await dispatches
            .OrderByDescending(x => x.TrxDate)
            .ThenByDescending(x => x.DocNo)
            .Skip(Math.Max(0, query.Skip))
            .Take(Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100))
            .Select(x => new SubconListRow
            {
                DocNo = x.DocNo,
                TrxDate = x.TrxDate,
                Status = x.Status,
                Mode = x.Mode,
                SupplierCode = x.SupplierCode,
                SupplierName = x.SupplierName,
                ItemSummary = x.Lines.Select(l => l.ItemCode).FirstOrDefault(),
                SentQty = x.Lines.Select(l => l.Qty).FirstOrDefault(),
                Uom = x.Lines.Select(l => l.Uom).FirstOrDefault(),
                LineCount = x.Lines.Count,
                PromisedReturnDate = x.PromisedReturnDate,
                Kind = SubconListKinds.Dispatch
            })
            .ToListAsync(cancellationToken);
        return SubconResult.OkList(new SubconListPage { Rows = dispatchRows, TotalCount = dispatchTotal }, await CanUseProductionAsync(cancellationToken));
    }

    public async Task<SubconResult> GetDispatchAsync(string docNo, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await CanViewDispatchAsync(cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadDispatchAsync(db, scope, docNo, cancellationToken);
        return entity is null
            ? SubconResult.Fail(SubconMessages.DispatchNotFound)
            : SubconResult.OkDispatch(MapDispatch(entity), await CanUseProductionAsync(cancellationToken));
    }

    public async Task<SubconResult> SaveDispatchDraftAsync(SubconDispatchSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        var isNew = string.IsNullOrWhiteSpace(command.DocNo);
        if (!await CanAsync(MenuCodes.SubconSendOut, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        var validation = ValidateDispatchCommand(command);
        if (validation is not null)
        {
            return SubconResult.Fail(validation);
        }

        if (SubconModes.IsKnown(command.Mode) && !SubconModes.IsInventory(command.Mode))
        {
            return SubconResult.Fail(SubconBlockedReasons.NoProductionCustody);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var supplier = await db.PoSuppliers.AsNoTracking().SingleOrDefaultAsync(
            x => x.CompanyCode == scope.CompanyCode && x.SuppCode == command.SupplierCode.Trim(),
            cancellationToken);
        if (supplier is null)
        {
            return SubconResult.Fail(SubconMessages.SupplierRequired);
        }

        SubconDispatch entity;
        if (isNew)
        {
            entity = new SubconDispatch
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                DocNo = await AllocateDocNoAsync(db, scope.CompanyCode, RunningNumberKeys.SubconDispatch, "SD",
                    (c, n) => db.SubconDispatches.AsNoTracking().AnyAsync(x => x.CompanyCode == c && x.DocNo == n, cancellationToken),
                    cancellationToken),
                Status = SubconStatuses.New,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.SubconDispatches.Add(entity);
        }
        else
        {
            var existing = await LoadDispatchAsync(db, scope, command.DocNo!.Trim(), cancellationToken);
            if (existing is null)
            {
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            entity = existing;

            if (!SubconStatuses.CanEdit(entity.Status))
            {
                return SubconResult.Fail(SubconMessages.OnlyNewEditable);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            db.SubconDispatchLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
        }

        var lineError = await AddDispatchLinesAsync(db, scope.CompanyCode, entity, command.Lines, cancellationToken);
        if (lineError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return SubconResult.Fail(lineError);
        }

        entity.TrxDate = command.TrxDate.Date;
        entity.Mode = SubconModes.Inventory;
        entity.SupplierCode = supplier.SuppCode;
        entity.SupplierName = supplier.SuppName;
        entity.PromisedReturnDate = command.PromisedReturnDate?.Date;
        entity.PoNo = TrimTo(command.PoNo, 30);
        entity.PoRelNo = command.PoRelNo;
        entity.PoLineNo = command.PoLineNo;
        entity.Remarks = TrimTo(command.Remarks, 500);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return SubconResult.OkDispatch(MapDispatch(entity), await CanUseProductionAsync(cancellationToken));
    }

    public async Task<SubconResult> DeleteDispatchDraftAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Delete, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Delete, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadDispatchAsync(db, scope, command.DocNo, cancellationToken);
        if (entity is null)
        {
            return SubconResult.Fail(SubconMessages.DispatchNotFound);
        }

        if (!SubconStatuses.CanEdit(entity.Status))
        {
            return SubconResult.Fail(SubconMessages.OnlyNewEditable);
        }

        if (Stale(entity.RowVersion, command.RowVersion))
        {
            return SubconResult.Fail(SubconMessages.ChangedReload);
        }

        if (await db.SubconReceiptLines.AnyAsync(x => x.DispatchId == entity.Uid, cancellationToken))
        {
            return SubconResult.Fail("Cannot delete: a receive-back draft still points at this send-out.");
        }

        db.SubconDispatchLines.RemoveRange(entity.Lines);
        db.SubconDispatches.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return SubconResult.OkDispatch(MapDispatch(entity));
    }

    public async Task<SubconResult> PostDispatchAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var loaded = await GetDispatchAsync(command.DocNo, cancellationToken);
        if (!loaded.Succeeded || loaded.Dispatch is null)
        {
            return loaded;
        }

        if (!await CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Post, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Post, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        return SubconModes.IsInventory(loaded.Dispatch.Mode)
            ? await _inventory.PostAsync(loaded.Dispatch, cancellationToken)
            : await _production.PostAsync(loaded.Dispatch, cancellationToken);
    }

    public async Task<SubconResult> RollbackDispatchAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var loaded = await GetDispatchAsync(command.DocNo, cancellationToken);
        if (!loaded.Succeeded || loaded.Dispatch is null)
        {
            return loaded;
        }

        if (!await CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Rollback, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Rollback, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        return SubconModes.IsInventory(loaded.Dispatch.Mode)
            ? await _inventory.RollbackAsync(loaded.Dispatch, cancellationToken)
            : await _production.RollbackAsync(loaded.Dispatch, cancellationToken);
    }

    public async Task<SubconResult> GetReceiptAsync(string docNo, CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Access, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadReceiptAsync(db, scope, docNo, cancellationToken);
        return entity is null
            ? SubconResult.Fail(SubconMessages.ReceiptNotFound)
            : SubconResult.OkReceipt(MapReceipt(entity));
    }

    public async Task<SubconResult> SaveReceiptDraftAsync(SubconReceiptSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        var isNew = string.IsNullOrWhiteSpace(command.DocNo);
        if (!await CanAsync(MenuCodes.SubconReceiveBack, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (string.IsNullOrWhiteSpace(command.SupplierCode))
        {
            return SubconResult.Fail(SubconMessages.SupplierRequired);
        }

        if (command.Lines.Count == 0)
        {
            return SubconResult.Fail(SubconMessages.LineRequired);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var supplier = await db.PoSuppliers.AsNoTracking().SingleOrDefaultAsync(
            x => x.CompanyCode == scope.CompanyCode && x.SuppCode == command.SupplierCode.Trim(),
            cancellationToken);
        if (supplier is null)
        {
            return SubconResult.Fail(SubconMessages.SupplierRequired);
        }

        SubconReceipt entity;
        if (isNew)
        {
            entity = new SubconReceipt
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                DocNo = await AllocateDocNoAsync(db, scope.CompanyCode, RunningNumberKeys.SubconReceipt, "SR",
                    (c, n) => db.SubconReceipts.AsNoTracking().AnyAsync(x => x.CompanyCode == c && x.DocNo == n, cancellationToken),
                    cancellationToken),
                Status = SubconStatuses.New,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.SubconReceipts.Add(entity);
        }
        else
        {
            var existing = await LoadReceiptAsync(db, scope, command.DocNo!.Trim(), cancellationToken);
            if (existing is null)
            {
                return SubconResult.Fail(SubconMessages.ReceiptNotFound);
            }

            entity = existing;
            if (!SubconStatuses.CanEdit(entity.Status))
            {
                return SubconResult.Fail(SubconMessages.OnlyNewEditable);
            }

            if (Stale(entity.RowVersion, command.RowVersion))
            {
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }

            db.SubconReceiptLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
        }

        foreach (var line in command.Lines.OrderBy(x => x.LineNo))
        {
            if (line.Qty <= 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.QtyRequired);
            }

            var dispatch = await db.SubconDispatches
                .Include(x => x.Lines)
                .SingleOrDefaultAsync(
                    x => x.CompanyCode == scope.CompanyCode
                         && x.BranchCode == scope.BranchCode
                         && x.DocNo == line.DispatchDocNo.Trim(),
                    cancellationToken);
            if (dispatch is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            var dispatchLine = dispatch.Lines.SingleOrDefault(x => x.LineNo == line.DispatchLineNo);
            if (dispatchLine is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            if (!string.Equals(dispatch.SupplierCode, supplier.SuppCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail("Receive-back supplier must match the send-out supplier.");
            }

            var already = await db.SubconReceiptLines
                .Where(x => x.DispatchLineId == dispatchLine.Uid && x.Receipt.Status != SubconStatuses.Cancelled)
                .SumAsync(x => (decimal?)x.Qty, cancellationToken) ?? 0m;
            if (already + line.Qty > dispatchLine.Qty)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail($"You can receive only {dispatchLine.Qty - already:0.####} {dispatchLine.Uom} on this dispatch.");
            }

            entity.Lines.Add(new SubconReceiptLine
            {
                LineNo = line.LineNo <= 0 ? entity.Lines.Count + 1 : line.LineNo,
                DispatchId = dispatch.Uid,
                DispatchLineId = dispatchLine.Uid,
                Qty = decimal.Round(line.Qty, 4, MidpointRounding.AwayFromZero),
                Uom = dispatchLine.Uom
            });
        }

        entity.TrxDate = command.TrxDate.Date;
        entity.SupplierCode = supplier.SuppCode;
        entity.SupplierName = supplier.SuppName;
        entity.Remarks = TrimTo(command.Remarks, 500);
        entity.ModifiedDate = DateTime.UtcNow;
        entity.ModifiedBy = TruncateUser(scope.UserId);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return SubconResult.OkReceipt(MapReceipt(entity));
    }

    public async Task<SubconResult> DeleteReceiptDraftAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Delete, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Delete, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await LoadReceiptAsync(db, scope, command.DocNo, cancellationToken);
        if (entity is null)
        {
            return SubconResult.Fail(SubconMessages.ReceiptNotFound);
        }

        if (!SubconStatuses.CanEdit(entity.Status))
        {
            return SubconResult.Fail(SubconMessages.OnlyNewEditable);
        }

        if (Stale(entity.RowVersion, command.RowVersion))
        {
            return SubconResult.Fail(SubconMessages.ChangedReload);
        }

        db.SubconReceiptLines.RemoveRange(entity.Lines);
        db.SubconReceipts.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
        return SubconResult.OkReceipt(MapReceipt(entity));
    }

    public async Task<SubconResult> PostReceiptAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var loaded = await GetReceiptAsync(command.DocNo, cancellationToken);
        if (!loaded.Succeeded || loaded.Receipt is null)
        {
            return loaded;
        }

        if (!await CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Post, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Post, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        return await _inventory.PostAsync(new SubconDispatchDto { Mode = SubconModes.Inventory, DocNo = command.DocNo }, cancellationToken);
    }

    public static SubconPolicyDto DefaultPolicy() => new()
    {
        Version = 0,
        SubconEnabled = false,
        InventoryModeEnabled = false,
        ProductionModeEnabled = false,
        IsPublished = false
    };

    private async Task<bool> CanUseProductionAsync(CancellationToken cancellationToken) =>
        await CanAsync(MenuCodes.PlanningWorkOrder, PermissionCodes.Access, cancellationToken);

    private Task<bool> CanViewDispatchAsync(CancellationToken cancellationToken) =>
        FirstAllowedAsync(cancellationToken, MenuCodes.SubconSendOut, MenuCodes.SubconWorkbench, MenuCodes.SubconInquiry);

    private async Task<bool> FirstAllowedAsync(CancellationToken cancellationToken, params string[] menus)
    {
        foreach (var menu in menus)
        {
            if (await CanAsync(menu, PermissionCodes.Access, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private Task<bool> CanAsync(string menu, string permission, CancellationToken cancellationToken) =>
        _access.CanAsync(menu, permission, cancellationToken);

    private InventoryTenantScope? RequireBranch()
    {
        var scope = _tenant.TryBranchScope() ?? _tenant.TryWriteScope();
        return scope is { BranchCode: not null } ? scope : null;
    }

    private InventoryTenantScope? RequireWrite()
    {
        var scope = _tenant.TryWriteScope();
        return scope is { BranchCode: not null } ? scope : null;
    }

    private static async Task<SubconPolicy?> LoadPublishedAsync(AppDbContext db, string company, CancellationToken cancellationToken) =>
        await db.SubconPolicies.SingleOrDefaultAsync(x => x.CompanyCode == company && x.IsPublished, cancellationToken);

    private static async Task<SubconDispatch?> LoadDispatchAsync(
        AppDbContext db, InventoryTenantScope scope, string docNo, CancellationToken cancellationToken) =>
        await db.SubconDispatches
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo.Trim(),
                cancellationToken);

    private static async Task<SubconReceipt?> LoadReceiptAsync(
        AppDbContext db, InventoryTenantScope scope, string docNo, CancellationToken cancellationToken) =>
        await db.SubconReceipts
            .Include(x => x.Lines)
            .ThenInclude(x => x.Dispatch)
            .Include(x => x.Lines)
            .ThenInclude(x => x.DispatchLine)
            .SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.DocNo == docNo.Trim(),
                cancellationToken);

    private static string? ValidateDispatchCommand(SubconDispatchSaveCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.SupplierCode))
        {
            return SubconMessages.SupplierRequired;
        }

        if (command.Lines.Count == 0)
        {
            return SubconMessages.LineRequired;
        }

        if (!SubconModes.IsKnown(command.Mode) && !string.IsNullOrWhiteSpace(command.Mode))
        {
            return SubconMessages.UnknownMode;
        }

        return null;
    }

    private static async Task<string?> AddDispatchLinesAsync(
        AppDbContext db,
        string company,
        SubconDispatch entity,
        IReadOnlyList<SubconDispatchLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        var codes = lines.Select(x => x.ItemCode.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var items = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && codes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            var itemCode = (line.ItemCode ?? string.Empty).Trim();
            if (itemCode.Length == 0)
            {
                return SubconMessages.ItemRequired;
            }

            if (line.Qty <= 0m)
            {
                return SubconMessages.QtyRequired;
            }

            if (!string.IsNullOrWhiteSpace(line.WorkOrderNo) || !string.IsNullOrWhiteSpace(line.OperationCode))
            {
                return SubconBlockedReasons.NoProductionCustody;
            }

            if (!items.TryGetValue(itemCode, out var item))
            {
                return $"Item '{itemCode}' was not found.";
            }

            if (!item.StockControl)
            {
                return SubconMessages.StockControlRequired;
            }

            if (item.LotControl)
            {
                return SubconBlockedReasons.LotControlled;
            }

            if (!string.IsNullOrWhiteSpace(line.LotNo))
            {
                return SubconBlockedReasons.LotControlled;
            }

            var warehouse = (line.WarehouseCode ?? item.DefWarehouse ?? string.Empty).Trim();
            if (warehouse.Length == 0)
            {
                return SubconMessages.WarehouseRequired;
            }

            var uom = (line.Uom ?? item.StdUom ?? string.Empty).Trim();
            if (uom.Length == 0)
            {
                return SubconMessages.UomRequired;
            }

            entity.Lines.Add(new SubconDispatchLine
            {
                LineNo = line.LineNo <= 0 ? lineNo : line.LineNo,
                ItemCode = item.ICode,
                ItemName = item.IDesc,
                Uom = uom,
                BaseUom = item.StdUom,
                Qty = decimal.Round(line.Qty, 4, MidpointRounding.AwayFromZero),
                WarehouseCode = warehouse,
                LocationCode = TrimTo(line.LocationCode ?? item.DefLocation, 10),
                LotNo = string.Empty,
                PromisedReturnDate = line.PromisedReturnDate?.Date,
                Remarks = TrimTo(line.Remarks, 500)
            });
        }

        return null;
    }

    private async Task<string> AllocateDocNoAsync(
        AppDbContext db,
        string company,
        string key,
        string prefix,
        Func<string, string, Task<bool>> exists,
        CancellationToken cancellationToken)
    {
        for (var i = 0; i < 10; i++)
        {
            var seq = await _numbers.GetNextAsync(db, company, key, cancellationToken);
            var number = $"{prefix}{seq:D8}";
            if (!await exists(company, number))
            {
                return number;
            }
        }

        throw new InvalidOperationException("Unable to allocate a subcontract document number.");
    }

    private static IQueryable<SubconDispatch> ApplyDispatchFilters(IQueryable<SubconDispatch> query, SubconListQuery filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(x => x.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Mode))
        {
            query = query.Where(x => x.Mode == filter.Mode);
        }

        if (!string.IsNullOrWhiteSpace(filter.SupplierCode))
        {
            query = query.Where(x => x.SupplierCode == filter.SupplierCode);
        }

        if (filter.DateFrom is { } from)
        {
            query = query.Where(x => x.TrxDate >= from.Date);
        }

        if (filter.DateTo is { } to)
        {
            query = query.Where(x => x.TrxDate <= to.Date);
        }

        var search = filter.SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x =>
                x.DocNo.Contains(search)
                || x.SupplierCode.Contains(search)
                || (x.SupplierName != null && x.SupplierName.Contains(search))
                || x.Lines.Any(l => l.ItemCode.Contains(search)));
        }

        return query;
    }

    private static IQueryable<SubconReceipt> ApplyReceiptFilters(IQueryable<SubconReceipt> query, SubconListQuery filter)
    {
        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            query = query.Where(x => x.Status == filter.Status);
        }

        if (!string.IsNullOrWhiteSpace(filter.SupplierCode))
        {
            query = query.Where(x => x.SupplierCode == filter.SupplierCode);
        }

        var search = filter.SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(x =>
                x.DocNo.Contains(search)
                || x.SupplierCode.Contains(search)
                || (x.SupplierName != null && x.SupplierName.Contains(search)));
        }

        return query;
    }

    private static SubconPolicyDto MapPolicy(SubconPolicy policy) => new()
    {
        Version = policy.Version,
        SubconEnabled = policy.SubconEnabled,
        InventoryModeEnabled = policy.InventoryModeEnabled,
        ProductionModeEnabled = policy.ProductionModeEnabled,
        IsPublished = policy.IsPublished,
        RowVersion = policy.RowVersion
    };

    private static SubconDispatchDto MapDispatch(SubconDispatch entity) => new()
    {
        DocNo = entity.DocNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        Mode = entity.Mode,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        PromisedReturnDate = entity.PromisedReturnDate,
        PoNo = entity.PoNo,
        PoRelNo = entity.PoRelNo,
        PoLineNo = entity.PoLineNo,
        Remarks = entity.Remarks,
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconDispatchLineDto
        {
            LineNo = x.LineNo,
            ItemCode = x.ItemCode,
            ItemName = x.ItemName,
            Uom = x.Uom,
            Qty = x.Qty,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            LotNo = x.LotNo,
            WorkOrderNo = x.WorkOrderNo,
            OperationCode = x.OperationCode,
            PromisedReturnDate = x.PromisedReturnDate,
            Remarks = x.Remarks
        }).ToArray(),
        PostBlockedReason = SubconModes.IsInventory(entity.Mode)
            ? SubconBlockedReasons.NoAtomicInventorySeam
            : SubconBlockedReasons.NoProductionCustody
    };

    private static SubconReceiptDto MapReceipt(SubconReceipt entity) => new()
    {
        DocNo = entity.DocNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        Remarks = entity.Remarks,
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconReceiptLineDto
        {
            LineNo = x.LineNo,
            DispatchDocNo = x.Dispatch?.DocNo ?? string.Empty,
            DispatchLineNo = x.DispatchLine?.LineNo ?? 0,
            Qty = x.Qty,
            Uom = x.Uom
        }).ToArray()
    };

    private static bool Stale(byte[] stored, byte[]? incoming) =>
        stored is { Length: > 0 }
        && (incoming is not { Length: > 0 } || !stored.SequenceEqual(incoming));

    private static string TruncateUser(string userId) =>
        userId.Length <= 20 ? userId : userId[..20];

    private static string? TrimTo(string? value, int max)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
