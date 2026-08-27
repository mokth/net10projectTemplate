using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class IvStockMasterService : IIvStockMasterService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;

    public IvStockMasterService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _stockMasters = stockMasters;
        _common = common;
    }

    public async Task<IvMasterOperationResult<IvStockMasterListPage>> SearchAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IvStockMasterListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvStockMasterListQuery();
        var args = ToSearchArgs(query);
        var (rows, total) = await _stockMasters.SearchPagedAsync(context.CompanyCode!, args, cancellationToken);
        return IvMasterOperationResult<IvStockMasterListPage>.Ok(new IvStockMasterListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<IvStockMasterEditVm>> GetAsync(
        string iCode,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterEditVm>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Access, cancellationToken))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (iCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, "Item code is required.");
        }

        var entity = await _stockMasters.GetByCodeAsync(context.CompanyCode!, code, cancellationToken);
        if (entity is null)
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.NotFound, "Item was not found.");
        }

        return IvMasterOperationResult<IvStockMasterEditVm>.Ok(MapEditVm(entity));
    }

    public async Task<IvMasterOperationResult<IvStockMasterEditVm>> SaveAsync(
        IvStockMasterEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.Validation, "Save model is required.");
        }

        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterEditVm>(context.ErrorCode.Value, context.Error!);
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, permission, cancellationToken))
        {
            return Fail<IvStockMasterEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.ICode ?? string.Empty).Trim();
        var desc = (model.IDesc ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors["ICode"] = "Item code is required.";
        }
        else if (code.Length > 30)
        {
            errors["ICode"] = "Item code must be at most 30 characters.";
        }

        if (string.IsNullOrWhiteSpace(desc))
        {
            errors["IDesc"] = "Description is required.";
        }
        else if (desc.Length > 200)
        {
            errors["IDesc"] = "Description must be at most 200 characters.";
        }

        if (model.LotControl && !model.StockControl)
        {
            errors["LotControl"] = "Lot control requires stock control.";
        }

        if (model.MinStock is not null && model.MaxStock is not null && model.MinStock > model.MaxStock)
        {
            errors["MinStock"] = "Minimum stock cannot be greater than maximum stock.";
        }

        var iType = NullIfWhiteSpace(model.IType);
        var iClass = NullIfWhiteSpace(model.IClassCode);
        var iSubClass = NullIfWhiteSpace(model.ISubClassCode);
        var stdUom = NullIfWhiteSpace(model.StdUom);
        var sellingUom = NullIfWhiteSpace(model.SellingUom);
        var purUom = NullIfWhiteSpace(model.PurUom);
        var defWh = NullIfWhiteSpace(model.DefWarehouse);
        var defLoc = NullIfWhiteSpace(model.DefLocation);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        if (iType is not null)
        {
            var typeRow = await _common.GetActiveTypeAsync(db, context.CompanyCode!, iType, cancellationToken);
            if (typeRow is null)
            {
                errors["IType"] = $"Type '{iType}' was not found or is inactive.";
            }
            else
            {
                iType = typeRow.TypeCode;
            }
        }

        if (iClass is not null)
        {
            var classRow = await _common.GetActiveClassAsync(db, context.CompanyCode!, iClass, cancellationToken);
            if (classRow is null)
            {
                errors["IClassCode"] = $"Class '{iClass}' was not found or is inactive.";
            }
            else
            {
                iClass = classRow.IClassCode;
            }
        }

        if (iSubClass is not null)
        {
            if (iClass is null)
            {
                errors["ISubClassCode"] = "Subclass requires a class.";
            }
            else
            {
                var sub = await _common.GetActiveSubClassAsync(
                    db, context.CompanyCode!, iClass, iSubClass, cancellationToken);
                if (sub is null)
                {
                    errors["ISubClassCode"] = $"Subclass '{iSubClass}' was not found for class '{iClass}'.";
                }
                else
                {
                    iSubClass = sub.ISubClassCode;
                }
            }
        }

        if (stdUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, stdUom, cancellationToken);
            if (uom is null)
            {
                errors["StdUom"] = $"UOM '{stdUom}' was not found or is inactive.";
            }
            else
            {
                stdUom = uom.UomCode;
            }
        }

        if (sellingUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, sellingUom, cancellationToken);
            if (uom is null)
            {
                errors["SellingUom"] = $"UOM '{sellingUom}' was not found or is inactive.";
            }
            else
            {
                sellingUom = uom.UomCode;
            }
        }

        if (purUom is not null)
        {
            var uom = await _common.GetActiveUomAsync(db, context.CompanyCode!, purUom, cancellationToken);
            if (uom is null)
            {
                errors["PurUom"] = $"UOM '{purUom}' was not found or is inactive.";
            }
            else
            {
                purUom = uom.UomCode;
            }
        }

        if (defWh is not null)
        {
            var warehouse = await _common.GetActiveWarehouseAsync(
                db, context.CompanyCode!, context.BranchCode!, defWh, cancellationToken);
            if (warehouse is null)
            {
                errors["DefWarehouse"] = $"Warehouse '{defWh}' was not found for this branch.";
            }
            else
            {
                defWh = warehouse.WarehouseCode;
                if (defLoc is not null)
                {
                    var location = await _common.GetActiveLocationAsync(
                        db,
                        context.CompanyCode!,
                        context.BranchCode!,
                        defWh,
                        defLoc,
                        cancellationToken);
                    if (location is null)
                    {
                        errors["DefLocation"] = $"Location '{defLoc}' was not found for warehouse '{defWh}'.";
                    }
                    else
                    {
                        defLoc = location.LocCode;
                    }
                }
            }
        }
        else if (defLoc is not null)
        {
            errors["DefLocation"] = "Location requires a warehouse.";
        }

        if (isNew)
        {
            if (!errors.ContainsKey("ICode") &&
                await _stockMasters.ExistsAsync(context.CompanyCode!, code, cancellationToken))
            {
                errors["ICode"] = "Item code already exists.";
            }
        }
        else if (model.RowVersion is null || model.RowVersion.Length == 0)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Concurrency,
                "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<IvStockMasterEditVm>.Fail(
                IvMasterErrorCode.Validation,
                "Validation failed.",
                errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.InvalidScope,
                "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var userId = Truncate(writeScope.UserId, 10);

        try
        {
            if (isNew)
            {
                var entity = new IvStockMaster
                {
                    CompanyCode = context.CompanyCode!,
                    ICode = code,
                    CreatedDate = now,
                    CreatedBy = userId,
                    ModifiedDate = now,
                    ModifiedBy = userId
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                ApplyEditableFields(
                    entity,
                    model,
                    desc,
                    iType,
                    iClass,
                    iSubClass,
                    stdUom,
                    sellingUom,
                    purUom,
                    defWh,
                    defLoc);
                // Do not set RowVersion — database generates it.
                db.IvStockMasters.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                await db.Entry(entity).ReloadAsync(cancellationToken);
                return IvMasterOperationResult<IvStockMasterEditVm>.Ok(MapEditVm(entity));
            }

            var existing = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
            if (existing is null)
            {
                return Fail<IvStockMasterEditVm>(IvMasterErrorCode.NotFound, "Item was not found.");
            }

            if (!KeysEqual(existing.ICode, code))
            {
                return Fail<IvStockMasterEditVm>(
                    IvMasterErrorCode.Validation,
                    "Item code cannot be changed.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ICode"] = "Item code cannot be changed."
                    });
            }

            if (!RowVersionsEqual(existing.RowVersion, model.RowVersion!))
            {
                return Fail<IvStockMasterEditVm>(
                    IvMasterErrorCode.Concurrency,
                    "This item was modified by another user. Your changes were not saved.");
            }

            var entry = db.Entry(existing);
            entry.Property(x => x.RowVersion).OriginalValue = model.RowVersion!;

            ApplyEditableFields(
                existing,
                model,
                desc,
                iType,
                iClass,
                iSubClass,
                stdUom,
                sellingUom,
                purUom,
                defWh,
                defLoc);
            // Leftover BranchCode / LocationCode: do not touch on update.
            existing.ModifiedDate = now;
            existing.ModifiedBy = userId;

            await db.SaveChangesAsync(cancellationToken);
            await db.Entry(existing).ReloadAsync(cancellationToken);
            return IvMasterOperationResult<IvStockMasterEditVm>.Ok(MapEditVm(existing));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.Concurrency,
                "This item was modified by another user. Your changes were not saved.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return Fail<IvStockMasterEditVm>(
                IvMasterErrorCode.DuplicateKey,
                "Item code already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ICode"] = "Item code already exists."
                });
        }
    }

    public async Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Edit, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var tracked = new List<(IvStockMaster Entity, byte[] Token)>();
            var stale = 0;

            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Validation, "Item code is required.");
                }

                var entity = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    stale++;
                    continue;
                }

                tracked.Add((entity, item.RowVersion));
            }

            if (stale > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<object>(
                    IvMasterErrorCode.Concurrency,
                    $"{stale} selected item(s) were modified by another user. No records were changed. Please reload and try again.");
            }

            var now = _dates.Now;
            var userId = Truncate(context.UserId!, 10);
            foreach (var (entity, token) in tracked)
            {
                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = token;
                entity.IsActive = isActive;
                entity.ModifiedDate = now;
                entity.ModifiedBy = userId;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.Concurrency,
                "One or more items were modified by another user. No records were changed. Please reload and try again.");
        }
    }

    public async Task<DeleteCheckResult> CanDeleteBulkAsync(
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return DeleteCheckResult.Blocked(context.Error!, []);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Delete, cancellationToken))
        {
            return DeleteCheckResult.Blocked("Not authorized.", []);
        }

        var codeList = (codes ?? Array.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (codeList.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        var refs = await _stockMasters.CountReferencesBulkAsync(context.CompanyCode!, codeList, cancellationToken);
        var hits = new List<IvMasterReferenceHit>();
        foreach (var code in codeList)
        {
            if (!refs.TryGetValue(code, out var list) || list.Count == 0)
            {
                continue;
            }

            foreach (var hit in list)
            {
                hits.Add(new IvMasterReferenceHit
                {
                    ReferenceType = hit.ReferenceType,
                    Count = hit.Count,
                    Detail = code
                });
            }
        }

        if (hits.Count > 0)
        {
            return DeleteCheckResult.Blocked(
                "One or more items are referenced by inventory transactions or balances.",
                hits);
        }

        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeleteAsync(
        IReadOnlyList<IvMasterKeyToken> items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<object>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Delete, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var codes = new List<string>();
            var entities = new List<IvStockMaster>();
            var stale = 0;

            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(code))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Validation, "Item code is required.");
                }

                var entity = await _stockMasters.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    stale++;
                    continue;
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                codes.Add(code);
                entities.Add(entity);
            }

            if (stale > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<object>(
                    IvMasterErrorCode.Concurrency,
                    $"{stale} selected item(s) were modified by another user. No records were changed. Please reload and try again.");
            }

            var refs = await _stockMasters.CountReferencesBulkAsync(context.CompanyCode!, codes, cancellationToken);
            var hits = new List<IvMasterReferenceHit>();
            foreach (var code in codes)
            {
                if (!refs.TryGetValue(code, out var list) || list.Count == 0)
                {
                    continue;
                }

                foreach (var hit in list)
                {
                    hits.Add(new IvMasterReferenceHit
                    {
                        ReferenceType = hit.ReferenceType,
                        Count = hit.Count,
                        Detail = code
                    });
                }
            }

            if (hits.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                var check = DeleteCheckResult.Blocked(
                    "One or more items are referenced by inventory transactions or balances.",
                    hits);
                return IvMasterOperationResult<object>.Fail(
                    IvMasterErrorCode.InUse,
                    check.Message!,
                    deleteCheck: check);
            }

            db.IvStockMasters.RemoveRange(entities);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.Concurrency,
                "One or more items were modified by another user. No records were changed. Please reload and try again.");
        }
        catch (DbUpdateException ex) when (IsForeignKeyViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(
                IvMasterErrorCode.InUse,
                "One or more items are referenced by inventory transactions or balances.");
        }
    }

    public async Task<IvMasterOperationResult<IvStockMasterListPage>> ExportRowsAsync(
        IvStockMasterListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<IvStockMasterListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.InventoryItemMaster, PermissionCodes.Export, cancellationToken))
        {
            return Fail<IvStockMasterListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new IvStockMasterListQuery();
        var args = ToSearchArgs(query);
        var count = await _stockMasters.CountExportAsync(context.CompanyCode!, args, cancellationToken);
        if (count > IvStockMasterRepository.MaxExportRows)
        {
            return Fail<IvStockMasterListPage>(
                IvMasterErrorCode.Validation,
                $"Export exceeds the maximum of {IvStockMasterRepository.MaxExportRows:N0} rows. Narrow your filters and try again.");
        }

        var rows = await _stockMasters.ListExportAsync(context.CompanyCode!, args, cancellationToken);
        return IvMasterOperationResult<IvStockMasterListPage>.Ok(new IvStockMasterListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = count
        });
    }

    private static StockMasterSearchArgs ToSearchArgs(IvStockMasterListQuery query) =>
        new(
            query.SearchText,
            query.IsActive,
            query.IClassCode,
            query.ISubClassCode,
            query.IType,
            query.DefWarehouse,
            query.Brand,
            query.SortField,
            query.SortDescending,
            query.Skip,
            query.Take);

    private static void ApplyEditableFields(
        IvStockMaster entity,
        IvStockMasterEditVm model,
        string desc,
        string? iType,
        string? iClass,
        string? iSubClass,
        string? stdUom,
        string? sellingUom,
        string? purUom,
        string? defWh,
        string? defLoc)
    {
        entity.IDesc = TruncateOptional(desc, 200);
        entity.Barcode = TruncateOptional(model.Barcode, 50);
        entity.Brand = TruncateOptional(model.Brand, 50);
        entity.IsActive = model.IsActive;
        entity.IType = TruncateOptional(iType, 20);
        entity.IClassCode = TruncateOptional(iClass, 30);
        entity.ISubClassCode = TruncateOptional(iSubClass, 30);
        entity.StdUom = TruncateOptional(stdUom, 10);
        entity.SellingUom = TruncateOptional(sellingUom, 10);
        entity.PurUom = TruncateOptional(purUom, 10);
        entity.StockControl = model.StockControl;
        entity.LotControl = model.LotControl;
        entity.DefWarehouse = TruncateOptional(defWh, 20);
        entity.DefLocation = TruncateOptional(defLoc, 10);
        entity.MinStock = model.MinStock;
        entity.MaxStock = model.MaxStock;
        entity.StdPackSize = model.StdPackSize;
        entity.PurStdPackSize = model.PurStdPackSize;
        entity.SellingPrice = model.SellingPrice;
        entity.PurchasePrice = model.PurchasePrice;
        entity.SellingGlCode = TruncateOptional(model.SellingGlCode, 20);
        entity.PurchaseGlCode = TruncateOptional(model.PurchaseGlCode, 20);
        entity.TaxGroup = TruncateOptional(model.TaxGroup, 20);
        entity.PurchaseTaxGroup = TruncateOptional(model.PurchaseTaxGroup, 20);
        entity.Classification = TruncateOptional(model.Classification, 50);
        entity.Size = TruncateOptional(model.Size, 50);
        entity.Color = TruncateOptional(model.Color, 50);
    }

    private static IvStockMasterListRow MapListRow(IvStockMaster x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            Barcode = x.Barcode,
            Brand = x.Brand,
            DefWarehouse = x.DefWarehouse,
            IClassCode = x.IClassCode,
            ISubClassCode = x.ISubClassCode,
            IType = x.IType,
            StdUom = x.StdUom,
            SellingUom = x.SellingUom,
            PurUom = x.PurUom,
            SellingGlCode = x.SellingGlCode,
            PurchaseGlCode = x.PurchaseGlCode,
            Classification = x.Classification,
            IsActive = x.IsActive,
            SellingPrice = x.SellingPrice,
            PurchasePrice = x.PurchasePrice,
            RowVersion = x.RowVersion ?? []
        };

    private static IvStockMasterEditVm MapEditVm(IvStockMaster x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            Barcode = x.Barcode,
            Brand = x.Brand,
            IsActive = x.IsActive,
            IType = x.IType,
            IClassCode = x.IClassCode,
            ISubClassCode = x.ISubClassCode,
            StdUom = x.StdUom,
            SellingUom = x.SellingUom,
            PurUom = x.PurUom,
            StockControl = x.StockControl,
            LotControl = x.LotControl,
            DefWarehouse = x.DefWarehouse,
            DefLocation = x.DefLocation,
            MinStock = x.MinStock,
            MaxStock = x.MaxStock,
            StdPackSize = x.StdPackSize,
            PurStdPackSize = x.PurStdPackSize,
            SellingPrice = x.SellingPrice,
            PurchasePrice = x.PurchasePrice,
            SellingGlCode = x.SellingGlCode,
            PurchaseGlCode = x.PurchaseGlCode,
            TaxGroup = x.TaxGroup,
            PurchaseTaxGroup = x.PurchaseTaxGroup,
            Classification = x.Classification,
            Size = x.Size,
            Color = x.Color,
            RowVersion = x.RowVersion,
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy
        };

    private UserContext ValidateUserContext()
    {
        var branchScope = _tenant.TryBranchScope();
        if (branchScope is null)
        {
            return UserContext.Fail(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        return UserContext.Ok(branchScope.CompanyCode, branchScope.BranchCode, branchScope.UserId);
    }

    private static bool KeysEqual(string? left, string? right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static IvMasterOperationResult<T> Fail<T>(
        IvMasterErrorCode code,
        string message,
        IReadOnlyDictionary<string, string>? validationErrors = null) =>
        IvMasterOperationResult<T>.Fail(code, message, validationErrors);

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return left.AsSpan().SequenceEqual(right);
    }

    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("2627") ||
                message.Contains("2601"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsForeignKeyViolation(DbUpdateException ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            var message = e.Message;
            if (message.Contains("REFERENCE", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("FOREIGN KEY", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("547") ||
                message.Contains("constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct UserContext(
        string? CompanyCode,
        string? BranchCode,
        string? UserId,
        IvMasterErrorCode? ErrorCode,
        string? Error)
    {
        public static UserContext Ok(string companyCode, string branchCode, string userId) =>
            new(companyCode, branchCode, userId, null, null);

        public static UserContext Fail(IvMasterErrorCode code, string error) =>
            new(null, null, null, code, error);
    }
}
