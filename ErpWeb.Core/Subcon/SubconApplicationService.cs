using System.Text.Json;
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

        if (command.ProductionModeEnabled || command.ProductionWipEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.ProductionModeFrozen);
        }

        if (command.LotReturnEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.LotReturnFrozen);
        }

        if (command.BomConversionEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.BomConversionFrozen);
        }

        if (command.FeeCapitalizationEnabled)
        {
            return SubconResult.Fail(SubconBlockedReasons.FeeCapitalizationFrozen);
        }

        if (!_inventory.SupportsLiveCustodyPosting)
        {
            if (command.SubconEnabled)
            {
                return SubconResult.Fail(SubconBlockedReasons.LiveEnablementFrozen);
            }

            if (command.InventoryModeEnabled || command.InventoryCustodyPostEnabled)
            {
                return SubconResult.Fail(SubconBlockedReasons.InventoryModeFrozen);
            }
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var custodyWh = TrimTo(command.DefaultCustodyWarehouseCode, 10);
        var enableInventoryCustody = command.InventoryCustodyPostEnabled || command.InventoryModeEnabled;
        if (enableInventoryCustody)
        {
            if (string.IsNullOrWhiteSpace(custodyWh))
            {
                return SubconResult.Fail(SubconMessages.CustodyWarehouseRequired);
            }

            var whQuery = db.IvWarehouses.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.WarehouseCode == custodyWh
                            && x.IsActive);
            if (!string.IsNullOrWhiteSpace(scope.BranchCode))
            {
                whQuery = whQuery.Where(x => x.BranchCode == scope.BranchCode);
            }

            var whTypes = await whQuery.Select(x => x.WarehouseType).ToListAsync(cancellationToken);
            if (!whTypes.Any(SubconCustodyGuardService.IsCustodyWarehouseType))
            {
                return SubconResult.Fail(SubconMessages.CustodyWarehouseRequired);
            }
        }

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

        published.SubconEnabled = _inventory.SupportsLiveCustodyPosting && command.SubconEnabled;
        published.InventoryModeEnabled = _inventory.SupportsLiveCustodyPosting && enableInventoryCustody;
        published.InventoryCustodyPostEnabled = _inventory.SupportsLiveCustodyPosting && enableInventoryCustody;
        published.DefaultCustodyWarehouseCode = custodyWh;
        published.ProductionModeEnabled = false;
        published.ProductionWipEnabled = false;
        published.LotReturnEnabled = false;
        published.BomConversionEnabled = false;
        published.FeeCapitalizationEnabled = false;
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

        var scope = RequireBranch()!;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var liveCustody = IsLiveCustodyEnabled(policy);
        var today = DateTime.Today;
        var filteredDrafts = ApplyDispatchFilters(
            db.SubconDispatches.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode!),
            query with { Kind = SubconListKinds.Dispatch })
            .Where(x => x.Status == SubconStatuses.New);

        var open = await filteredDrafts.CountAsync(cancellationToken);
        int overdue;
        int openLines;
        string[] byUom;

        if (liveCustody)
        {
            var postedDispatches = ApplyDispatchFilters(
                db.SubconDispatches.AsNoTracking()
                    .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode!),
                query with { Kind = SubconListKinds.Dispatch })
                .Where(x => x.Status == SubconStatuses.Posted);
            overdue = await postedDispatches.CountAsync(
                x => x.PromisedReturnDate != null && x.PromisedReturnDate < today,
                cancellationToken);

            var activeAllocs = db.SubconCustodyAllocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode
                            && x.BranchCode == scope.BranchCode!
                            && x.IsActive
                            && (x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty) > 0m);
            openLines = await activeAllocs.CountAsync(cancellationToken);
            var uomTotals = await activeAllocs
                .GroupBy(x => x.BaseUom)
                .Select(g => new
                {
                    Uom = g.Key,
                    Qty = g.Sum(x => x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty)
                })
                .OrderBy(x => x.Uom)
                .ToListAsync(cancellationToken);
            byUom = uomTotals.Select(x => $"{x.Qty:0.####} {x.Uom}").ToArray();
        }
        else
        {
            overdue = await filteredDrafts.CountAsync(
                x => x.PromisedReturnDate != null && x.PromisedReturnDate < today,
                cancellationToken);
            openLines = await filteredDrafts.SelectMany(x => x.Lines).CountAsync(cancellationToken);
            var uomTotals = await filteredDrafts
                .SelectMany(x => x.Lines)
                .Where(l => l.Uom != string.Empty)
                .GroupBy(l => l.Uom)
                .Select(g => new { Uom = g.Key, Qty = g.Sum(x => x.Qty) })
                .OrderBy(x => x.Uom)
                .ToListAsync(cancellationToken);
            byUom = uomTotals
                .Select(x => $"{x.Qty:0.####} {x.Uom}")
                .ToArray();
        }

        return SubconResult.OkWorkbench(new SubconWorkbenchPage
        {
            OpenLineCount = openLines,
            OverdueLineCount = overdue,
            DraftCount = open,
            OutstandingByUom = byUom,
            Rows = list.ListPage.Rows,
            TotalCount = list.ListPage.TotalCount,
            PostingBanner = liveCustody ? string.Empty : SubconBlockedReasons.PostingNotEnabled
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
        if (entity is null)
        {
            return SubconResult.Fail(SubconMessages.DispatchNotFound);
        }

        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var activeLink = await LoadActivePostingLinkAsync(
            db, scope, SubconPostingDocKinds.Dispatch, entity.Uid, cancellationToken);
        return SubconResult.OkDispatch(
            MapDispatch(entity, IsLiveCustodyEnabled(policy), activeLink),
            await CanUseProductionAsync(cancellationToken));
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

        var poError = await ValidateDispatchPoAsync(db, scope, supplier.SuppCode, command, cancellationToken);
        if (poError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return SubconResult.Fail(poError);
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
        }

        var lineError = await SyncDispatchLinesAsync(db, scope, entity, isNew, command.Lines, cancellationToken);
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
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

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

        if (SubconModes.IsInventory(loaded.Dispatch.Mode))
        {
            if (_inventory.SupportsLiveCustodyPosting
                && !await CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Post, cancellationToken))
            {
                return SubconResult.Fail(SubconMessages.NotAuthorized);
            }

            return await _inventory.PostAsync(command, loaded.Dispatch, scope, cancellationToken);
        }

        return await _production.PostAsync(loaded.Dispatch, cancellationToken);
    }

    public async Task<SubconResult> RollbackDispatchAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

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

        if (SubconModes.IsInventory(loaded.Dispatch.Mode))
        {
            if (_inventory.SupportsLiveCustodyPosting
                && !await CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Rollback, cancellationToken))
            {
                return SubconResult.Fail(SubconMessages.NotAuthorized);
            }

            return await _inventory.RollbackAsync(command, loaded.Dispatch, scope, cancellationToken);
        }

        return await _production.RollbackAsync(loaded.Dispatch, cancellationToken);
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
        if (entity is null)
        {
            return SubconResult.Fail(SubconMessages.ReceiptNotFound);
        }

        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var activeLink = await LoadActivePostingLinkAsync(
            db, scope, SubconPostingDocKinds.Receipt, entity.Uid, cancellationToken);
        return SubconResult.OkReceipt(MapReceipt(entity, IsLiveCustodyEnabled(policy), activeLink));
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
        }

        var normalizedLines = command.Lines
            .OrderBy(x => x.LineNo <= 0 ? int.MaxValue : x.LineNo)
            .Select((x, idx) => new
            {
                LineNo = x.LineNo <= 0 ? idx + 1 : x.LineNo,
                DispatchDocNo = (x.DispatchDocNo ?? string.Empty).Trim(),
                DispatchLineNo = x.DispatchLineNo,
                Qty = decimal.Round(x.Qty, 4, MidpointRounding.AwayFromZero),
                ToWarehouseCode = TrimTo(x.ToWarehouseCode, 10),
                ToLocationCode = TrimTo(x.ToLocationCode, 10)
            })
            .ToArray();

        if (normalizedLines.Select(x => x.LineNo).Distinct().Count() != normalizedLines.Length)
        {
            await tx.RollbackAsync(cancellationToken);
            return SubconResult.Fail(SubconMessages.DuplicateLineNo);
        }

        foreach (var line in normalizedLines)
        {
            if (line.Qty <= 0m)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.QtyRequired);
            }

            if (line.DispatchDocNo.Length == 0 || line.DispatchLineNo <= 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }
        }

        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var liveCustody = IsLiveCustodyEnabled(policy);

        var activeWarehouses = (await db.IvWarehouses.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsActive)
                .Select(x => new { x.WarehouseCode, x.WarehouseType })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.WarehouseCode, x => x.WarehouseType, StringComparer.OrdinalIgnoreCase);

        var activeLocations = (await db.IvLocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsActive)
                .Select(x => new { x.WarehouseCode, x.LocCode })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(v => v.LocCode).ToDictionary(l => l, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var docNos = normalizedLines
            .Select(x => x.DispatchDocNo)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var dispatches = await db.SubconDispatches
            .Include(x => x.Lines)
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && docNos.Contains(x.DocNo))
            .ToDictionaryAsync(x => x.DocNo, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var resolvedTargets = new List<(SubconDispatch Dispatch, SubconDispatchLine DispatchLine, int LineNo, decimal Qty, string? ToWh, string? ToLoc)>(normalizedLines.Length);
        foreach (var line in normalizedLines)
        {
            if (!dispatches.TryGetValue(line.DispatchDocNo, out var dispatch))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            if (!string.Equals(dispatch.SupplierCode, supplier.SuppCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail("Receive-back supplier must match the send-out supplier.");
            }

            if (dispatch.Status == SubconStatuses.Cancelled)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            if (liveCustody && !SubconStatuses.IsPosted(dispatch.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchMustBePosted);
            }

            var dispatchLine = dispatch.Lines.SingleOrDefault(x => x.LineNo == line.DispatchLineNo);
            if (dispatchLine is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            string? toWh = line.ToWarehouseCode;
            string? toLoc = line.ToLocationCode;
            if (!string.IsNullOrWhiteSpace(toWh))
            {
                if (!activeWarehouses.TryGetValue(toWh, out var whType))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail(SubconMessages.WarehouseInvalid);
                }

                if (SubconCustodyGuardService.IsCustodyWarehouseType(whType))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SubconResult.Fail(SubconMessages.ReceiveDestinationCustodyForbidden);
                }

                if (!string.IsNullOrWhiteSpace(toLoc))
                {
                    if (!activeLocations.TryGetValue(toWh, out var whLocs)
                        || !whLocs.TryGetValue(toLoc, out var canonicalLoc))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SubconResult.Fail(SubconMessages.LocationInvalid);
                    }

                    toLoc = canonicalLoc;
                }
            }

            resolvedTargets.Add((dispatch, dispatchLine, line.LineNo, line.Qty, toWh, toLoc));
        }

        var dispatchLineIds = resolvedTargets
            .Select(x => x.DispatchLine.Uid)
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        if (db.Database.IsSqlServer())
        {
            foreach (var dlId in dispatchLineIds)
            {
                await db.SubconDispatchLines
                    .FromSqlInterpolated($"SELECT * FROM dbo.SubconDispatchLine WITH (UPDLOCK, HOLDLOCK) WHERE UID = {dlId}")
                    .AsNoTracking()
                    .FirstOrDefaultAsync(cancellationToken);
            }
        }

        var alreadyByLineId = await db.SubconReceiptLines
            .Where(x => dispatchLineIds.Contains(x.DispatchLineId)
                        && x.Receipt.Status != SubconStatuses.Cancelled
                        && (isNew || x.ReceiptId != entity.Uid))
            .GroupBy(x => x.DispatchLineId)
            .Select(g => new { DispatchLineId = g.Key, Qty = g.Sum(x => x.Qty) })
            .ToDictionaryAsync(x => x.DispatchLineId, x => x.Qty, cancellationToken);

        foreach (var grp in resolvedTargets.GroupBy(x => x.DispatchLine.Uid))
        {
            var dispatchLine = grp.First().DispatchLine;
            var already = alreadyByLineId.GetValueOrDefault(grp.Key, 0m);
            var incomingTotal = grp.Sum(x => x.Qty);
            if (already + incomingTotal > dispatchLine.Qty)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail($"You can receive only {Math.Max(0m, dispatchLine.Qty - already):0.####} {dispatchLine.Uom} on this dispatch.");
            }
        }

        if (!isNew)
        {
            db.SubconReceiptLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
        }

        foreach (var target in resolvedTargets)
        {
            entity.Lines.Add(new SubconReceiptLine
            {
                LineNo = target.LineNo,
                DispatchId = target.Dispatch.Uid,
                DispatchLineId = target.DispatchLine.Uid,
                Qty = target.Qty,
                Uom = target.DispatchLine.Uom,
                ToWarehouseCode = target.ToWh,
                ToLocationCode = target.ToLoc
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
        return SubconResult.OkReceipt(MapReceipt(entity, liveCustody, null));
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
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

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

        if (_inventory.SupportsLiveCustodyPosting
            && !await CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Post, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        return await _inventory.PostReceiptAsync(command, loaded.Receipt, scope, cancellationToken);
    }

    public async Task<SubconResult> RollbackReceiptAsync(SubconKeyCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        var loaded = await GetReceiptAsync(command.DocNo, cancellationToken);
        if (!loaded.Succeeded || loaded.Receipt is null)
        {
            return loaded;
        }

        if (!await CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Rollback, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Rollback, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (_inventory.SupportsLiveCustodyPosting
            && !await CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Rollback, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        return await _inventory.RollbackReceiptAsync(command, loaded.Receipt, scope, cancellationToken);
    }

    public async Task<SubconResult> GetCustodyReconciliationAsync(
        string? supplierCode = null,
        CancellationToken cancellationToken = default)
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
        var query = db.SubconCustodyAllocations.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode!
                        && x.IsActive);

        var supp = TrimTo(supplierCode, 20);
        if (!string.IsNullOrWhiteSpace(supp))
        {
            query = query.Where(x => x.SupplierCode == supp);
        }

        var grouped = await query
            .GroupBy(x => new
            {
                x.SupplierCode,
                x.ItemCode,
                x.BaseUom,
                x.CustodyWarehouseCode,
                x.CustodyLocationCode,
                x.CustodyBalLocId
            })
            .Select(g => new
            {
                g.Key.SupplierCode,
                g.Key.ItemCode,
                g.Key.BaseUom,
                g.Key.CustodyWarehouseCode,
                g.Key.CustodyLocationCode,
                g.Key.CustodyBalLocId,
                Dispatched = g.Sum(x => x.DispatchedBaseQty),
                Returned = g.Sum(x => x.ReturnedBaseQty),
                Outstanding = g.Sum(x => x.DispatchedBaseQty - x.ReturnedBaseQty - x.ConsumedBaseQty - x.ScrapBaseQty)
            })
            .OrderBy(x => x.SupplierCode)
            .ThenBy(x => x.ItemCode)
            .ToListAsync(cancellationToken);

        var balIds = grouped.Select(x => x.CustodyBalLocId).Distinct().ToArray();
        var physicalById = balIds.Length == 0
            ? new Dictionary<int, decimal>()
            : await db.IvBalLocs.AsNoTracking()
                .Where(b => b.CompanyCode == scope.CompanyCode
                            && b.BranchCode == scope.BranchCode!
                            && balIds.Contains(b.Id))
                .ToDictionaryAsync(b => b.Id, b => b.StdQty, cancellationToken);

        var rows = grouped.Select(g => new SubconCustodyReconciliationRow
        {
            SupplierCode = g.SupplierCode,
            ItemCode = g.ItemCode,
            BaseUom = g.BaseUom,
            CustodyWarehouseCode = g.CustodyWarehouseCode,
            CustodyLocationCode = g.CustodyLocationCode,
            CustodyBalLocId = g.CustodyBalLocId,
            AllocatedDispatchedBaseQty = g.Dispatched,
            AllocatedReturnedBaseQty = g.Returned,
            AllocatedOutstandingBaseQty = g.Outstanding,
            PhysicalCustodyBaseQty = physicalById.GetValueOrDefault(g.CustodyBalLocId, 0m)
        }).ToArray();

        return SubconResult.OkReconciliation(rows);
    }

    public async Task<SubconResult> SearchDispatchLinesAsync(
        SubconDispatchLineLookupQuery query,
        CancellationToken cancellationToken = default)
    {
        var scope = RequireBranch();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        if (!await CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Access, cancellationToken)
            && !await CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Access, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var policy = await LoadPublishedAsync(db, scope.CompanyCode, cancellationToken);
        var requiredStatus = IsLiveCustodyEnabled(policy) ? SubconStatuses.Posted : SubconStatuses.New;
        var excludeReceipt = TrimTo(query.ExcludeReceiptDocNo, 20);
        var linesQuery = db.SubconDispatchLines.AsNoTracking()
            .Where(l => l.Dispatch.CompanyCode == scope.CompanyCode
                        && l.Dispatch.BranchCode == scope.BranchCode!
                        && l.Dispatch.Status == requiredStatus
                        && l.Dispatch.Mode == SubconModes.Inventory);

        var supplierCode = TrimTo(query.SupplierCode, 20);
        if (!string.IsNullOrWhiteSpace(supplierCode))
        {
            linesQuery = linesQuery.Where(l => l.Dispatch.SupplierCode == supplierCode);
        }

        var search = TrimTo(query.SearchText, 100);
        if (!string.IsNullOrWhiteSpace(search))
        {
            linesQuery = linesQuery.Where(l =>
                l.Dispatch.DocNo.Contains(search)
                || l.ItemCode.Contains(search)
                || (l.ItemName != null && l.ItemName.Contains(search))
                || l.Dispatch.SupplierCode.Contains(search)
                || (l.Dispatch.SupplierName != null && l.Dispatch.SupplierName.Contains(search)));
        }

        var withRemaining = linesQuery
            .Select(l => new
            {
                DispatchDocNo = l.Dispatch.DocNo,
                DispatchLineNo = l.LineNo,
                TrxDate = l.Dispatch.TrxDate,
                SupplierCode = l.Dispatch.SupplierCode,
                SupplierName = l.Dispatch.SupplierName,
                ItemCode = l.ItemCode,
                ItemName = l.ItemName,
                Uom = l.Uom,
                DispatchedQty = l.Qty,
                WarehouseCode = l.WarehouseCode,
                LocationCode = l.LocationCode,
                AllocatedQty = l.ReceiptAllocations
                    .Where(a => a.Receipt.Status != SubconStatuses.Cancelled
                                && (excludeReceipt == null || a.Receipt.DocNo != excludeReceipt))
                    .Sum(a => (decimal?)a.Qty) ?? 0m
            })
            .Where(x => x.DispatchedQty - x.AllocatedQty > 0m);

        var total = await withRemaining.CountAsync(cancellationToken);
        var rows = await withRemaining
            .OrderByDescending(x => x.TrxDate)
            .ThenByDescending(x => x.DispatchDocNo)
            .ThenBy(x => x.DispatchLineNo)
            .Skip(Math.Max(0, query.Skip))
            .Take(Math.Clamp(query.Take <= 0 ? 20 : query.Take, 1, 100))
            .Select(x => new SubconDispatchLineLookupRow
            {
                DispatchDocNo = x.DispatchDocNo,
                DispatchLineNo = x.DispatchLineNo,
                TrxDate = x.TrxDate,
                SupplierCode = x.SupplierCode,
                SupplierName = x.SupplierName,
                ItemCode = x.ItemCode,
                ItemName = x.ItemName,
                Uom = x.Uom,
                DispatchedQty = x.DispatchedQty,
                AllocatedQty = x.AllocatedQty,
                RemainingQty = x.DispatchedQty - x.AllocatedQty,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode
            })
            .ToListAsync(cancellationToken);

        return SubconResult.OkDispatchLineLookup(new SubconDispatchLineLookupPage
        {
            Rows = rows,
            TotalCount = total
        });
    }

    public async Task<SubconResult> GetJobAsync(string jobNo, CancellationToken cancellationToken = default)
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
        var entity = await LoadJobAsync(db, scope, jobNo, cancellationToken);
        return entity is null
            ? SubconResult.Fail(SubconMessages.JobNotFound)
            : SubconResult.OkJob(MapJob(entity), canUseProductionMode: await CanUseProductionAsync(cancellationToken));
    }

    public async Task<SubconResult> SaveJobDraftAsync(SubconJobSaveCommand command, CancellationToken cancellationToken = default)
    {
        var scope = RequireWrite();
        if (scope is null)
        {
            return SubconResult.Fail(SubconMessages.CompanyBranchRequired);
        }

        var isNew = string.IsNullOrWhiteSpace(command.JobNo);
        if (!await CanAsync(MenuCodes.SubconSendOut, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken)
            && !await CanAsync(MenuCodes.SubconWorkbench, isNew ? PermissionCodes.Add : PermissionCodes.Edit, cancellationToken))
        {
            return SubconResult.Fail(SubconMessages.NotAuthorized);
        }

        if (string.IsNullOrWhiteSpace(command.SupplierCode))
        {
            return SubconResult.Fail(SubconMessages.SupplierRequired);
        }

        if (command.Materials.Count == 0)
        {
            return SubconResult.Fail(SubconMessages.LineRequired);
        }

        if (!SubconModes.IsKnown(command.Mode) && !string.IsNullOrWhiteSpace(command.Mode))
        {
            return SubconResult.Fail(SubconMessages.UnknownMode);
        }

        if (SubconModes.IsKnown(command.Mode) && !SubconModes.IsInventory(command.Mode))
        {
            return SubconResult.Fail(SubconBlockedReasons.NoProductionCustody);
        }

        if (!SubconJobKinds.IsKnown(command.JobKind) && !string.IsNullOrWhiteSpace(command.JobKind))
        {
            return SubconResult.Fail("Subcontract job kind is not recognized.");
        }

        var jobKind = string.IsNullOrWhiteSpace(command.JobKind)
            ? SubconJobKinds.Processing
            : command.JobKind.Trim().ToUpperInvariant();

        if (SubconJobKinds.IsAssembly(jobKind)
            && (string.IsNullOrWhiteSpace(command.TargetItemCode) || command.TargetQty <= 0m))
        {
            return SubconResult.Fail(SubconMessages.TargetItemRequired);
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

        var poProbeCommand = new SubconDispatchSaveCommand
        {
            PoNo = command.PoNo,
            PoRelNo = command.PoRelNo,
            PoLineNo = command.PoLineNo
        };
        var poError = await ValidateDispatchPoAsync(db, scope, supplier.SuppCode, poProbeCommand, cancellationToken);
        if (poError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return SubconResult.Fail(poError);
        }

        IvStockMaster? targetItem = null;
        if (SubconJobKinds.IsAssembly(jobKind))
        {
            var targetCode = command.TargetItemCode!.Trim();
            targetItem = await db.IvStockMasters.AsNoTracking().SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.ICode == targetCode,
                cancellationToken);
            if (targetItem is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.TargetItemRequired);
            }

            if (!targetItem.StockControl)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.StockControlRequired);
            }

            if (targetItem.LotControl)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconBlockedReasons.LotControlled);
            }
        }

        SubconJob job;
        if (isNew)
        {
            job = new SubconJob
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                JobNo = await AllocateDocNoAsync(
                    db,
                    scope.CompanyCode,
                    RunningNumberKeys.SubconJob,
                    "SJ",
                    (c, n) => db.SubconJobs.AsNoTracking().AnyAsync(x => x.CompanyCode == c && x.JobNo == n, cancellationToken),
                    cancellationToken),
                Status = SubconStatuses.New,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.SubconJobs.Add(job);
        }
        else
        {
            var existingJob = await LoadJobAsync(db, scope, command.JobNo!.Trim(), cancellationToken);
            if (existingJob is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.JobNotFound);
            }

            job = existingJob;

            if (!SubconStatuses.CanEdit(job.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.OnlyNewEditable);
            }

            if (Stale(job.RowVersion, command.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.ChangedReload);
            }
        }

        var isNewDispatch = string.IsNullOrWhiteSpace(job.PrimaryDispatchDocNo);
        SubconDispatch dispatch;
        if (isNewDispatch)
        {
            dispatch = new SubconDispatch
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode!,
                DocNo = await AllocateDocNoAsync(
                    db,
                    scope.CompanyCode,
                    RunningNumberKeys.SubconDispatch,
                    "SD",
                    (c, n) => db.SubconDispatches.AsNoTracking().AnyAsync(x => x.CompanyCode == c && x.DocNo == n, cancellationToken),
                    cancellationToken),
                Status = SubconStatuses.New,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = TruncateUser(scope.UserId)
            };
            db.SubconDispatches.Add(dispatch);
        }
        else
        {
            var existingDispatch = await LoadDispatchAsync(db, scope, job.PrimaryDispatchDocNo!, cancellationToken);
            if (existingDispatch is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SubconResult.Fail(SubconMessages.DispatchNotFound);
            }

            dispatch = existingDispatch;
        }

        var dispatchLines = command.Materials.Select((m, idx) => new SubconDispatchLineSaveCommand
        {
            LineNo = m.LineNo <= 0 ? idx + 1 : m.LineNo,
            ItemCode = m.ItemCode,
            Uom = m.Uom,
            Qty = m.SendNowQty > 0m ? m.SendNowQty : m.RequiredQty,
            WarehouseCode = m.WarehouseCode,
            LocationCode = m.LocationCode,
            PromisedReturnDate = command.PromisedReturnDate,
            Remarks = m.Remarks
        }).ToArray();

        var lineError = await SyncDispatchLinesAsync(db, scope, dispatch, isNewDispatch, dispatchLines, cancellationToken);
        if (lineError is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return SubconResult.Fail(lineError);
        }

        dispatch.TrxDate = command.TrxDate.Date;
        dispatch.Mode = SubconModes.Inventory;
        dispatch.SupplierCode = supplier.SuppCode;
        dispatch.SupplierName = supplier.SuppName;
        dispatch.PromisedReturnDate = command.PromisedReturnDate?.Date;
        dispatch.PoNo = TrimTo(command.PoNo, 30);
        dispatch.PoRelNo = command.PoRelNo;
        dispatch.PoLineNo = command.PoLineNo;
        dispatch.Remarks = TrimTo(command.Remarks, 500);
        dispatch.ModifiedDate = DateTime.UtcNow;
        dispatch.ModifiedBy = TruncateUser(scope.UserId);

        var targetUom = targetItem is null
            ? null
            : TrimTo(command.TargetUom, 10) ?? targetItem.StdUom;

        job.TrxDate = command.TrxDate.Date;
        job.JobKind = jobKind;
        job.Mode = SubconModes.Inventory;
        job.SupplierCode = supplier.SuppCode;
        job.SupplierName = supplier.SuppName;
        job.TargetItemCode = targetItem?.ICode;
        job.TargetItemName = targetItem?.IDesc;
        job.TargetUom = targetUom;
        job.TargetQty = targetItem is null ? 0m : decimal.Round(command.TargetQty, 4, MidpointRounding.AwayFromZero);
        job.DefinitionCode = TrimTo(command.DefinitionCode, 20);
        job.BomVersion = command.BomVersion;
        job.IsManualKit = command.IsManualKit;
        job.PrimaryDispatchDocNo = dispatch.DocNo;
        job.PromisedReturnDate = command.PromisedReturnDate?.Date;
        job.PoNo = TrimTo(command.PoNo, 30);
        job.PoRelNo = command.PoRelNo;
        job.PoLineNo = command.PoLineNo;
        job.Remarks = TrimTo(command.Remarks, 500);
        job.ModifiedDate = DateTime.UtcNow;
        job.ModifiedBy = TruncateUser(scope.UserId);

        db.SubconJobMaterials.RemoveRange(job.Materials);
        job.Materials.Clear();
        var incomingByLineNo = command.Materials
            .Select((m, idx) => (LineNo: m.LineNo <= 0 ? idx + 1 : m.LineNo, Cmd: m))
            .ToDictionary(x => x.LineNo, x => x.Cmd);

        foreach (var dLine in dispatch.Lines.OrderBy(x => x.LineNo))
        {
            incomingByLineNo.TryGetValue(dLine.LineNo, out var mCmd);
            var reqQty = mCmd is { RequiredQty: > 0m } ? mCmd.RequiredQty : dLine.Qty;
            job.Materials.Add(new SubconJobMaterial
            {
                LineNo = dLine.LineNo,
                ItemCode = dLine.ItemCode,
                ItemName = dLine.ItemName,
                Uom = dLine.Uom,
                BaseUom = dLine.BaseUom,
                RequiredQty = decimal.Round(reqQty, 4, MidpointRounding.AwayFromZero),
                SendNowQty = dLine.Qty,
                ScrapPercent = mCmd?.ScrapPercent ?? 0m,
                WarehouseCode = dLine.WarehouseCode,
                LocationCode = dLine.LocationCode,
                LotNo = string.Empty,
                IvBalLocId = mCmd?.IvBalLocId,
                IsManualAddition = mCmd?.IsManualAddition ?? command.IsManualKit,
                Remarks = dLine.Remarks
            });
        }

        db.SubconJobOutputs.RemoveRange(job.Outputs);
        job.Outputs.Clear();
        if (SubconJobKinds.IsAssembly(jobKind) && targetItem is not null)
        {
            var defaultWarehouse = dispatch.Lines.First().WarehouseCode;
            job.Outputs.Add(new SubconJobOutput
            {
                LineNo = 1,
                OutputKind = SubconOutputKinds.FinishedGood,
                ItemCode = targetItem.ICode,
                ItemName = targetItem.IDesc,
                Uom = targetUom ?? targetItem.StdUom ?? "PCS",
                ExpectedQty = job.TargetQty,
                WarehouseCode = TrimTo(targetItem.DefWarehouse, 10) ?? defaultWarehouse,
                LocationCode = TrimTo(targetItem.DefLocation, 10)
            });
        }

        db.SubconJobSourceSnapshots.RemoveRange(job.SourceSnapshots);
        job.SourceSnapshots.Clear();
        var sourceKind = SubconJobKinds.IsAssembly(jobKind)
            ? (command.IsManualKit ? SubconSourceKinds.ManualKit : SubconSourceKinds.Bom)
            : SubconSourceKinds.InventoryItem;
        job.SourceSnapshots.Add(new SubconJobSourceSnapshot
        {
            SourceKind = sourceKind,
            ProdCode = targetItem?.ICode ?? dispatch.Lines.First().ItemCode,
            DefinitionCode = job.DefinitionCode,
            BomVersion = job.BomVersion,
            RootQty = job.TargetQty > 0m ? job.TargetQty : dispatch.Lines.Sum(x => x.Qty),
            RootUom = targetUom ?? dispatch.Lines.First().Uom,
            CapturedDate = DateTime.UtcNow,
            CapturedBy = TruncateUser(scope.UserId)
        });

        db.SubconSupplierAssignments.RemoveRange(job.SupplierAssignments);
        job.SupplierAssignments.Clear();
        job.SupplierAssignments.Add(new SubconSupplierAssignment
        {
            LineNo = 1,
            SupplierCode = supplier.SuppCode,
            SupplierName = supplier.SuppName,
            AssignedQty = job.TargetQty > 0m ? job.TargetQty : dispatch.Lines.Sum(x => x.Qty),
            PoNo = job.PoNo,
            PoRelNo = job.PoRelNo,
            PoLineNo = job.PoLineNo,
            PromisedReturnDate = job.PromisedReturnDate,
            DispatchDocNo = dispatch.DocNo
        });

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return SubconResult.OkJob(
            MapJob(job),
            MapDispatch(dispatch),
            await CanUseProductionAsync(cancellationToken));
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

    private static async Task<string?> ValidateDispatchPoAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string supplierCode,
        SubconDispatchSaveCommand command,
        CancellationToken cancellationToken)
    {
        var poNo = TrimTo(command.PoNo, 30);
        if (string.IsNullOrWhiteSpace(poNo))
        {
            return null;
        }

        var orders = await db.PoOrders.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.PoNo == poNo)
            .ToListAsync(cancellationToken);

        if (command.PoRelNo is { } relNo)
        {
            orders = orders.Where(x => x.PoRelNo == relNo).ToList();
        }

        var matchingOrders = orders
            .Where(x => string.IsNullOrWhiteSpace(x.VendCode)
                        || string.Equals(x.VendCode.Trim(), supplierCode, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingOrders.Count == 0)
        {
            return SubconMessages.PoInvalid;
        }

        if (command.PoLineNo is { } poLineNo)
        {
            var rels = matchingOrders.Select(x => x.PoRelNo).ToArray();
            var hasLine = await db.PoOrderDetails.AsNoTracking()
                .AnyAsync(
                    x => x.CompanyCode == scope.CompanyCode
                         && x.BranchCode == scope.BranchCode
                         && x.PoNo == poNo
                         && rels.Contains(x.PoRelNo)
                         && x.Line == poLineNo,
                    cancellationToken);
            if (!hasLine)
            {
                return SubconMessages.PoInvalid;
            }
        }

        return null;
    }

    private static async Task<string?> SyncDispatchLinesAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        SubconDispatch entity,
        bool isNew,
        IReadOnlyList<SubconDispatchLineSaveCommand> lines,
        CancellationToken cancellationToken)
    {
        var codes = lines.Select(x => x.ItemCode.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var items = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && codes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var activeWarehouses = (await db.IvWarehouses.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsActive)
                .Select(x => new { x.WarehouseCode, x.WarehouseType })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.WarehouseCode, x => x, StringComparer.OrdinalIgnoreCase);

        var activeLocations = (await db.IvLocations.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.IsActive)
                .Select(x => new { x.WarehouseCode, x.LocCode })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.WarehouseCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(v => v.LocCode).ToDictionary(l => l, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        var activeUoms = (await db.MsUoms.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive)
                .Select(x => x.UomCode)
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x, StringComparer.OrdinalIgnoreCase);

        var conversions = await db.IvItemUomConversions.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive && codes.Contains(x.ItemCode))
            .ToListAsync(cancellationToken);

        var allocatedByLineId = isNew
            ? new Dictionary<long, decimal>()
            : await db.SubconReceiptLines.AsNoTracking()
                .Where(x => x.DispatchId == entity.Uid && x.Receipt.Status != SubconStatuses.Cancelled)
                .GroupBy(x => x.DispatchLineId)
                .Select(g => new { DispatchLineId = g.Key, Qty = g.Sum(r => r.Qty) })
                .ToDictionaryAsync(x => x.DispatchLineId, x => x.Qty, cancellationToken);

        var prepared = new List<(
            int LineNo,
            IvStockMaster Item,
            string Uom,
            decimal Qty,
            string WarehouseCode,
            string? LocationCode,
            DateTime? PromisedReturnDate,
            string? Remarks)>(lines.Count);

        var usedLineNos = new HashSet<int>();
        var lineNo = 0;
        foreach (var line in lines)
        {
            lineNo++;
            var normalizedLineNo = line.LineNo <= 0 ? lineNo : line.LineNo;
            if (!usedLineNos.Add(normalizedLineNo))
            {
                return SubconMessages.DuplicateLineNo;
            }

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

            var warehouseInput = (line.WarehouseCode ?? item.DefWarehouse ?? string.Empty).Trim();
            if (warehouseInput.Length == 0)
            {
                return SubconMessages.WarehouseRequired;
            }

            if (!activeWarehouses.TryGetValue(warehouseInput, out var warehouseRow))
            {
                return SubconMessages.WarehouseInvalid;
            }

            if (SubconCustodyGuardService.IsCustodyWarehouseType(warehouseRow.WarehouseType))
            {
                return SubconCustodyConstants.SourceCustodyBlocked;
            }

            var warehouse = warehouseRow.WarehouseCode;

            var locationInput = TrimTo(line.LocationCode ?? item.DefLocation, 10);
            string? location = null;
            if (locationInput is not null)
            {
                if (!activeLocations.TryGetValue(warehouse, out var whLocs)
                    || !whLocs.TryGetValue(locationInput, out location))
                {
                    return SubconMessages.LocationInvalid;
                }
            }

            var uomInput = (line.Uom ?? item.StdUom ?? string.Empty).Trim();
            if (uomInput.Length == 0)
            {
                return SubconMessages.UomRequired;
            }

            if (activeUoms.TryGetValue(uomInput, out var canonicalUom))
            {
                uomInput = canonicalUom;
            }
            else if (!string.Equals(uomInput, item.StdUom, StringComparison.OrdinalIgnoreCase))
            {
                return SubconMessages.UomInvalid;
            }

            if (!string.Equals(uomInput, item.StdUom, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uomInput, item.PurUom, StringComparison.OrdinalIgnoreCase)
                && !conversions.Any(c =>
                    string.Equals(c.ItemCode, item.ICode, StringComparison.OrdinalIgnoreCase)
                    && ((string.Equals(c.FromUom, uomInput, StringComparison.OrdinalIgnoreCase)
                         && string.Equals(c.ToUom, item.StdUom, StringComparison.OrdinalIgnoreCase))
                        || (string.Equals(c.ToUom, uomInput, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(c.FromUom, item.StdUom, StringComparison.OrdinalIgnoreCase)))))
            {
                return SubconMessages.UomInvalid;
            }

            prepared.Add((
                normalizedLineNo,
                item,
                uomInput,
                decimal.Round(line.Qty, 4, MidpointRounding.AwayFromZero),
                warehouse,
                location,
                line.PromisedReturnDate?.Date,
                TrimTo(line.Remarks, 500)));
        }

        var incomingByLineNo = prepared.ToDictionary(x => x.LineNo);

        foreach (var existing in entity.Lines.ToList())
        {
            var isReferenced = allocatedByLineId.TryGetValue(existing.Uid, out var allocatedQty);
            if (!incomingByLineNo.TryGetValue(existing.LineNo, out var updated))
            {
                if (isReferenced)
                {
                    return SubconMessages.DispatchLineReferenced;
                }

                db.SubconDispatchLines.Remove(existing);
                entity.Lines.Remove(existing);
                continue;
            }

            if (isReferenced)
            {
                var identityChanged =
                    !string.Equals(existing.ItemCode, updated.Item.ICode, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.Uom, updated.Uom, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.WarehouseCode, updated.WarehouseCode, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.LocationCode ?? string.Empty, updated.LocationCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);

                if (identityChanged || updated.Qty < allocatedQty)
                {
                    return SubconMessages.DispatchLineReferenced;
                }
            }

            existing.ItemCode = updated.Item.ICode;
            existing.ItemName = updated.Item.IDesc;
            existing.Uom = updated.Uom;
            existing.BaseUom = updated.Item.StdUom;
            existing.Qty = updated.Qty;
            existing.WarehouseCode = updated.WarehouseCode;
            existing.LocationCode = updated.LocationCode;
            existing.LotNo = string.Empty;
            existing.PromisedReturnDate = updated.PromisedReturnDate;
            existing.Remarks = updated.Remarks;
        }

        var existingLineNos = entity.Lines.Select(x => x.LineNo).ToHashSet();
        foreach (var added in prepared.Where(x => !existingLineNos.Contains(x.LineNo)))
        {
            entity.Lines.Add(new SubconDispatchLine
            {
                LineNo = added.LineNo,
                ItemCode = added.Item.ICode,
                ItemName = added.Item.IDesc,
                Uom = added.Uom,
                BaseUom = added.Item.StdUom,
                Qty = added.Qty,
                WarehouseCode = added.WarehouseCode,
                LocationCode = added.LocationCode,
                LotNo = string.Empty,
                PromisedReturnDate = added.PromisedReturnDate,
                Remarks = added.Remarks
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

    private bool IsLiveCustodyEnabled(SubconPolicy? policy) =>
        _inventory.SupportsLiveCustodyPosting
        && policy is { SubconEnabled: true, InventoryCustodyPostEnabled: true };

    private static async Task<SubconPostingLink?> LoadActivePostingLinkAsync(
        AppDbContext db,
        InventoryTenantScope scope,
        string docKind,
        long docId,
        CancellationToken cancellationToken) =>
        await db.SubconPostingLinks.AsNoTracking()
            .SingleOrDefaultAsync(x => x.CompanyCode == scope.CompanyCode
                                       && x.BranchCode == scope.BranchCode!
                                       && x.DocKind == docKind
                                       && x.DocId == docId
                                       && x.PostingRole == SubconPostingRoles.Primary
                                       && x.IsActive,
                cancellationToken);

    private static SubconPolicyDto MapPolicy(SubconPolicy policy) => new()
    {
        Version = policy.Version,
        SubconEnabled = policy.SubconEnabled,
        InventoryModeEnabled = policy.InventoryModeEnabled,
        ProductionModeEnabled = policy.ProductionModeEnabled,
        InventoryCustodyPostEnabled = policy.InventoryCustodyPostEnabled,
        LotReturnEnabled = policy.LotReturnEnabled,
        BomConversionEnabled = policy.BomConversionEnabled,
        ProductionWipEnabled = policy.ProductionWipEnabled,
        FeeCapitalizationEnabled = policy.FeeCapitalizationEnabled,
        DefaultCustodyWarehouseCode = policy.DefaultCustodyWarehouseCode,
        IsPublished = policy.IsPublished,
        RowVersion = policy.RowVersion
    };

    private static SubconDispatchDto MapDispatch(
        SubconDispatch entity,
        bool liveCustody = false,
        SubconPostingLink? activeLink = null) => new()
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
        DocumentRevision = entity.DocumentRevision,
        PostedDate = entity.PostedDate,
        PostedBy = entity.PostedBy,
        PostedTrxBatchNo = activeLink?.TrxBatchNo,
        PostedOperationId = activeLink?.OperationId,
        CanPost = liveCustody && SubconModes.IsInventory(entity.Mode) && SubconStatuses.CanEdit(entity.Status),
        CanRollback = liveCustody && SubconModes.IsInventory(entity.Mode) && SubconStatuses.IsPosted(entity.Status),
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconDispatchLineDto
        {
            LineNo = x.LineNo,
            ItemCode = x.ItemCode,
            ItemName = x.ItemName,
            Uom = x.Uom,
            BaseUom = x.BaseUom,
            Qty = x.Qty,
            BaseQty = x.BaseQty,
            StockStatus = x.StockStatus,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            CustodyWarehouseCode = x.CustodyWarehouseCode,
            CustodyLocationCode = x.CustodyLocationCode,
            CustodyBalLocId = x.CustodyBalLocId,
            LotNo = x.LotNo,
            WorkOrderNo = x.WorkOrderNo,
            OperationCode = x.OperationCode,
            PromisedReturnDate = x.PromisedReturnDate,
            Remarks = x.Remarks
        }).ToArray(),
        PostBlockedReason = !SubconModes.IsInventory(entity.Mode)
            ? SubconBlockedReasons.NoProductionCustody
            : liveCustody
                ? string.Empty
                : SubconBlockedReasons.PostingNotEnabled
    };

    private static async Task<SubconJob?> LoadJobAsync(
        AppDbContext db, InventoryTenantScope scope, string jobNo, CancellationToken cancellationToken) =>
        await db.SubconJobs
            .Include(x => x.Materials)
            .Include(x => x.Outputs)
            .Include(x => x.SourceSnapshots)
            .Include(x => x.SupplierAssignments)
            .SingleOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.BranchCode == scope.BranchCode && x.JobNo == jobNo.Trim(),
                cancellationToken);

    private static SubconJobDto MapJob(SubconJob entity) => new()
    {
        JobNo = entity.JobNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        JobKind = entity.JobKind,
        Mode = entity.Mode,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        TargetItemCode = entity.TargetItemCode,
        TargetItemName = entity.TargetItemName,
        TargetUom = entity.TargetUom,
        TargetQty = entity.TargetQty,
        DefinitionCode = entity.DefinitionCode,
        BomVersion = entity.BomVersion,
        IsManualKit = entity.IsManualKit,
        PrimaryDispatchDocNo = entity.PrimaryDispatchDocNo,
        PromisedReturnDate = entity.PromisedReturnDate,
        PoNo = entity.PoNo,
        PoRelNo = entity.PoRelNo,
        PoLineNo = entity.PoLineNo,
        Remarks = entity.Remarks,
        RowVersion = entity.RowVersion,
        Materials = entity.Materials.OrderBy(x => x.LineNo).Select(x => new SubconJobMaterialDto
        {
            LineNo = x.LineNo,
            ItemCode = x.ItemCode,
            ItemName = x.ItemName,
            Uom = x.Uom,
            RequiredQty = x.RequiredQty,
            SendNowQty = x.SendNowQty,
            ScrapPercent = x.ScrapPercent,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            IvBalLocId = x.IvBalLocId,
            IsManualAddition = x.IsManualAddition,
            Remarks = x.Remarks
        }).ToArray(),
        Outputs = entity.Outputs.OrderBy(x => x.LineNo).Select(x => new SubconJobOutputDto
        {
            LineNo = x.LineNo,
            OutputKind = x.OutputKind,
            ItemCode = x.ItemCode,
            ItemName = x.ItemName,
            Uom = x.Uom,
            ExpectedQty = x.ExpectedQty,
            WarehouseCode = x.WarehouseCode,
            LocationCode = x.LocationCode,
            Remarks = x.Remarks
        }).ToArray(),
        PostBlockedReason = SubconModes.IsInventory(entity.Mode)
            ? SubconBlockedReasons.NoAtomicInventorySeam
            : SubconBlockedReasons.NoProductionCustody
    };

    private static SubconReceiptDto MapReceipt(
        SubconReceipt entity,
        bool liveCustody = false,
        SubconPostingLink? activeLink = null) => new()
    {
        DocNo = entity.DocNo,
        TrxDate = entity.TrxDate,
        Status = entity.Status,
        SupplierCode = entity.SupplierCode,
        SupplierName = entity.SupplierName,
        Remarks = entity.Remarks,
        DocumentRevision = entity.DocumentRevision,
        PostedDate = entity.PostedDate,
        PostedBy = entity.PostedBy,
        PostedTrxBatchNo = activeLink?.TrxBatchNo,
        PostedOperationId = activeLink?.OperationId,
        CanPost = liveCustody && SubconStatuses.CanEdit(entity.Status),
        CanRollback = liveCustody && SubconStatuses.IsPosted(entity.Status),
        RowVersion = entity.RowVersion,
        Lines = entity.Lines.OrderBy(x => x.LineNo).Select(x => new SubconReceiptLineDto
        {
            LineNo = x.LineNo,
            DispatchDocNo = x.Dispatch?.DocNo ?? string.Empty,
            DispatchLineNo = x.DispatchLine?.LineNo ?? 0,
            ItemCode = x.DispatchLine?.ItemCode,
            ItemName = x.DispatchLine?.ItemName,
            Qty = x.Qty,
            Uom = x.Uom,
            BaseQty = x.BaseQty,
            ToWarehouseCode = x.ToWarehouseCode,
            ToLocationCode = x.ToLocationCode
        }).ToArray(),
        PostBlockedReason = liveCustody ? string.Empty : SubconBlockedReasons.PostingNotEnabled
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
