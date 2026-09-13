using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Purchase reference masters (Buyer, Buying Term, Category, Authorised, PurItem).
/// Phase-1 CanDelete* does not check references (e.g. PoSupplier.BuyingTerm / CategoryCode).
/// </summary>
public sealed class PoMasterRefService : IPoMasterRefService
{
    public const int MaxExportRows = 50_000;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;

    public PoMasterRefService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
    }

    // ================= Buyer =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ListBuyersAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoBuyer, PoBuyerListRow>(
            MenuCodes.PurchaseBuyer, PermissionCodes.Access,
            q => q.OrderBy(x => x.BuyerCode), MapBuyerRow, cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyerListRow>>> ExportBuyersAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoBuyer, PoBuyerListRow>(
            MenuCodes.PurchaseBuyer, PermissionCodes.Export,
            q => q.OrderBy(x => x.BuyerCode).Take(MaxExportRows), MapBuyerRow, cancellationToken);

    public Task<IvMasterOperationResult<PoBuyerEditVm>> GetBuyerAsync(
        string code, CancellationToken cancellationToken = default) =>
        GetCoreAsync<PoBuyer, PoBuyerEditVm>(
            MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), code, MapBuyer, cancellationToken);

    public Task<IvMasterOperationResult<PoBuyerEditVm>> SaveBuyerAsync(
        PoBuyerEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Task.FromResult(FailVm<PoBuyerEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Name", model.Name, 100);
        ValidateOptionalLength(errors, "Desc", model.Desc, 200);
        return SaveCoreAsync<PoBuyer, PoBuyerEditVm>(
            MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.BuyerName = Null(v.Name);
                e.BuyerDesc = Null(v.Desc);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoBuyer
            {
                CompanyCode = company,
                BuyerCode = code,
                BranchCode = branch,
                LocationCode = location,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user
            },
            MapBuyer, cancellationToken);
    }

    public Task<IvMasterOperationResult<object>> SetBuyerActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default) =>
        SetActiveCoreAsync<PoBuyer>(
            MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), items, isActive,
            e => e.RowVersion,
            static (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteBuyersAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseBuyer, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteBuyersAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync<PoBuyer>(
            MenuCodes.PurchaseBuyer, "Buyer", nameof(PoBuyer.BuyerCode), items, e => e.RowVersion, cancellationToken);

    // ================= Buying Term =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ListBuyingTermsAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoBuyingTerm, PoBuyingTermListRow>(
            MenuCodes.PurchaseBuyingTerm, PermissionCodes.Access,
            q => q.OrderBy(x => x.BuyingTerm), MapBuyingTermRow, cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<PoBuyingTermListRow>>> ExportBuyingTermsAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoBuyingTerm, PoBuyingTermListRow>(
            MenuCodes.PurchaseBuyingTerm, PermissionCodes.Export,
            q => q.OrderBy(x => x.BuyingTerm).Take(MaxExportRows), MapBuyingTermRow, cancellationToken);

    public Task<IvMasterOperationResult<PoBuyingTermEditVm>> GetBuyingTermAsync(
        string code, CancellationToken cancellationToken = default) =>
        GetCoreAsync<PoBuyingTerm, PoBuyingTermEditVm>(
            MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), code, MapBuyingTerm,
            cancellationToken);

    public Task<IvMasterOperationResult<PoBuyingTermEditVm>> SaveBuyingTermAsync(
        PoBuyingTermEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Task.FromResult(FailVm<PoBuyingTermEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Description", model.Description, 200);
        return SaveCoreAsync<PoBuyingTerm, PoBuyingTermEditVm>(
            MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Description = Null(v.Description);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoBuyingTerm
            {
                CompanyCode = company,
                BuyingTerm = code,
                BranchCode = branch,
                LocationCode = location,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user
            },
            MapBuyingTerm, cancellationToken);
    }

    public Task<IvMasterOperationResult<object>> SetBuyingTermActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default) =>
        SetActiveCoreAsync<PoBuyingTerm>(
            MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), items, isActive,
            e => e.RowVersion,
            static (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteBuyingTermsAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseBuyingTerm, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteBuyingTermsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync<PoBuyingTerm>(
            MenuCodes.PurchaseBuyingTerm, "Buying term", nameof(PoBuyingTerm.BuyingTerm), items, e => e.RowVersion,
            cancellationToken);

    // ================= Category =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ListCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoCategory, PoCategoryListRow>(
            MenuCodes.PurchaseCategory, PermissionCodes.Access,
            q => q.OrderBy(x => x.Category), MapCategoryRow, cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<PoCategoryListRow>>> ExportCategoriesAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoCategory, PoCategoryListRow>(
            MenuCodes.PurchaseCategory, PermissionCodes.Export,
            q => q.OrderBy(x => x.Category).Take(MaxExportRows), MapCategoryRow, cancellationToken);

    public Task<IvMasterOperationResult<PoCategoryEditVm>> GetCategoryAsync(
        string code, CancellationToken cancellationToken = default) =>
        GetCoreAsync<PoCategory, PoCategoryEditVm>(
            MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), code, MapCategory, cancellationToken);

    public Task<IvMasterOperationResult<PoCategoryEditVm>> SaveCategoryAsync(
        PoCategoryEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Task.FromResult(FailVm<PoCategoryEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Description", model.Description, 200);
        return SaveCoreAsync<PoCategory, PoCategoryEditVm>(
            MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Description = Null(v.Description);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoCategory
            {
                CompanyCode = company,
                Category = code,
                BranchCode = branch,
                LocationCode = location,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user
            },
            MapCategory, cancellationToken);
    }

    public Task<IvMasterOperationResult<object>> SetCategoryActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default) =>
        SetActiveCoreAsync<PoCategory>(
            MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), items, isActive,
            e => e.RowVersion,
            static (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteCategoriesAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseCategory, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteCategoriesAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync<PoCategory>(
            MenuCodes.PurchaseCategory, "Category", nameof(PoCategory.Category), items, e => e.RowVersion,
            cancellationToken);

    // ================= Authorised =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ListAuthorisedAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoAuthorised, PoAuthorisedListRow>(
            MenuCodes.PurchaseAuthorised, PermissionCodes.Access,
            q => q.OrderBy(x => x.Authorised), MapAuthorisedRow, cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<PoAuthorisedListRow>>> ExportAuthorisedAsync(
        CancellationToken cancellationToken = default) =>
        ListCoreAsync<PoAuthorised, PoAuthorisedListRow>(
            MenuCodes.PurchaseAuthorised, PermissionCodes.Export,
            q => q.OrderBy(x => x.Authorised).Take(MaxExportRows), MapAuthorisedRow, cancellationToken);

    public Task<IvMasterOperationResult<PoAuthorisedEditVm>> GetAuthorisedAsync(
        string code, CancellationToken cancellationToken = default) =>
        GetCoreAsync<PoAuthorised, PoAuthorisedEditVm>(
            MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), code, MapAuthorised,
            cancellationToken);

    public Task<IvMasterOperationResult<PoAuthorisedEditVm>> SaveAuthorisedAsync(
        PoAuthorisedEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Task.FromResult(FailVm<PoAuthorisedEditVm>(IvMasterErrorCode.Validation, "Save request is required."));
        }

        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        ValidateOptionalLength(errors, "Name", model.Name, 100);
        ValidateOptionalLength(errors, "Email", model.Email, 100);
        ValidateOptionalLength(errors, "MobileNo", model.MobileNo, 50);
        if (!string.IsNullOrWhiteSpace(model.Email) && !model.Email.Contains('@'))
        {
            errors["Email"] = "Email is not valid.";
        }

        return SaveCoreAsync<PoAuthorised, PoAuthorisedEditVm>(
            MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), "Code", 20,
            model.Code, model, isNew, errors,
            e => e.RowVersion, v => v.RowVersion,
            static (e, v, now, user) =>
            {
                e.Name = Null(v.Name);
                e.Email = Null(v.Email);
                e.MobileNo = Null(v.MobileNo);
                e.IsActive = v.IsActive;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            static (company, code, branch, location, now, user) => new PoAuthorised
            {
                CompanyCode = company,
                Authorised = code,
                BranchCode = branch,
                LocationCode = location,
                CreatedDate = now,
                CreatedBy = user,
                ModifiedDate = now,
                ModifiedBy = user
            },
            MapAuthorised, cancellationToken);
    }

    public Task<IvMasterOperationResult<object>> SetAuthorisedActiveAsync(
        IReadOnlyList<IvMasterKeyToken> items, bool isActive, CancellationToken cancellationToken = default) =>
        SetActiveCoreAsync<PoAuthorised>(
            MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), items, isActive,
            e => e.RowVersion,
            static (e, active, now, user) =>
            {
                e.IsActive = active;
                e.ModifiedDate = now;
                e.ModifiedBy = user;
            },
            cancellationToken);

    public Task<DeleteCheckResult> CanDeleteAuthorisedAsync(
        IReadOnlyList<string> codes, CancellationToken cancellationToken = default) =>
        CanDeleteCoreAsync(MenuCodes.PurchaseAuthorised, codes, cancellationToken);

    public Task<IvMasterOperationResult<object>> DeleteAuthorisedAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default) =>
        DeleteCoreAsync<PoAuthorised>(
            MenuCodes.PurchaseAuthorised, "Authorised person", nameof(PoAuthorised.Authorised), items, e => e.RowVersion,
            cancellationToken);

    // ================= Purchase Item (PK = Id) =================
    public Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ListPurItemsAsync(
        CancellationToken cancellationToken = default) =>
        ListPurItemsCoreAsync(PermissionCodes.Access, take: null, cancellationToken);

    public Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ExportPurItemsAsync(
        CancellationToken cancellationToken = default) =>
        ListPurItemsCoreAsync(PermissionCodes.Export, take: MaxExportRows, cancellationToken);

    private async Task<IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>> ListPurItemsCoreAsync(
        string permission, int? take, CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, permission, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailList<PoPurItemListRow>(ctx.ErrorCode.Value, ctx.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PoPurItems.AsNoTracking()
            .Where(x => x.CompanyCode == ctx.CompanyCode)
            .OrderBy(x => x.ICode)
            .ThenBy(x => x.Vendor);
        var rows = take is null
            ? await query.ToListAsync(cancellationToken)
            : await query.Take(take.Value).ToListAsync(cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<PoPurItemListRow>>.Ok(rows.Select(MapPurItemRow).ToList());
    }

    public async Task<IvMasterOperationResult<PoPurItemEditVm>> GetPurItemAsync(
        string id, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Access, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailVm<PoPurItemEditVm>(ctx.ErrorCode.Value, ctx.Error);
        }

        if (!int.TryParse(NormalizeCode(id), out var key))
        {
            return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Validation, "Purchase item id is required.", "Id");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.PoPurItems.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == ctx.CompanyCode && x.Id == key, cancellationToken);
        return entity is null
            ? FailVm<PoPurItemEditVm>(IvMasterErrorCode.NotFound, "Purchase item not found.")
            : IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(entity));
    }

    public async Task<IvMasterOperationResult<PoPurItemEditVm>> SavePurItemAsync(
        PoPurItemEditVm model, bool isNew, CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return FailVm<PoPurItemEditVm>(IvMasterErrorCode.Validation, "Save request is required.");
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, permission, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailVm<PoPurItemEditVm>(ctx.ErrorCode.Value, ctx.Error);
        }

        var errors = ValidatePurItem(model);
        if (!isNew && model.RowVersion is not { Length: > 0 })
        {
            return FailVm<PoPurItemEditVm>(
                IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PoPurItemEditVm>.Fail(
                IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return FailVm<PoPurItemEditVm>(
                IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var iCode = (model.ICode ?? string.Empty).Trim();
        var vendor = (model.Vendor ?? string.Empty).Trim();
        model.ICode = iCode;
        model.Vendor = vendor;

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            var duplicateQuery = db.PoPurItems.Where(x =>
                x.CompanyCode == scope.CompanyCode &&
                x.ICode == iCode &&
                x.Vendor == vendor);
            if (!isNew)
            {
                duplicateQuery = duplicateQuery.Where(x => x.Id != model.Id);
            }

            if (await duplicateQuery.AnyAsync(cancellationToken))
            {
                return FailVmPurItemDuplicate(iCode, vendor);
            }

            if (isNew)
            {
                var entity = new PoPurItem
                {
                    CompanyCode = scope.CompanyCode!,
                    BranchCode = scope.BranchCode,
                    LocationCode = scope.LocationCode,
                    CreatedDate = now,
                    CreatedBy = user
                };
                ApplyPurItem(entity, model, now, user);
                EnsureSqliteRowVersion(db, entity);
                db.PoPurItems.Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(entity));
            }

            var tracked = await db.PoPurItems.FirstOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.Id == model.Id, cancellationToken);
            if (tracked is null)
            {
                return FailVm<PoPurItemEditVm>(IvMasterErrorCode.NotFound, "Purchase item not found.");
            }

            if (!RowVersionsEqual(tracked.RowVersion, model.RowVersion))
            {
                return FailVm<PoPurItemEditVm>(
                    IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property(nameof(PoPurItem.RowVersion)).OriginalValue = model.RowVersion!;
            ApplyPurItem(tracked, model, now, user);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<PoPurItemEditVm>.Ok(MapPurItem(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<PoPurItemEditVm>(
                IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVmPurItemDuplicate(iCode, vendor);
        }
    }

    public async Task<DeleteCheckResult> CanDeletePurItemsAsync(
        IReadOnlyList<string> ids, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Delete, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.ErrorCode.Value), []);
        }

        if (!NormalizeCodes(ids).Any())
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        // Phase-1: no reference checks modelled for purchase items.
        return DeleteCheckResult.Ok();
    }

    public async Task<IvMasterOperationResult<object>> DeletePurItemsAsync(
        IReadOnlyList<IvMasterKeyToken> items, CancellationToken cancellationToken = default)
    {
        var ctx = await RequireCompanyScopeAsync(MenuCodes.PurchasePurItem, PermissionCodes.Delete, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailObj(ctx.ErrorCode.Value, ctx.Error);
        }

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var check = await CanDeletePurItemsAsync(items.Select(x => x.Code).ToList(), cancellationToken);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(
                IvMasterErrorCode.InUse, check.Message ?? "Record is in use.", deleteCheck: check);
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                if (!int.TryParse(NormalizeCode(item.Code), out var key))
                {
                    continue;
                }

                var entity = await db.PoPurItems.FirstOrDefaultAsync(
                    x => x.CompanyCode == scope.CompanyCode && x.Id == key, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property(nameof(PoPurItem.RowVersion)).OriginalValue = item.RowVersion;
                db.PoPurItems.Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    // ================= generic row-version core =================
    private async Task<IvMasterOperationResult<IReadOnlyList<TRow>>> ListCoreAsync<TEntity, TRow>(
        string menuCode,
        string permission,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> order,
        Func<TEntity, TRow> map,
        CancellationToken cancellationToken) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, permission, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailList<TRow>(ctx.ErrorCode.Value, ctx.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await order(
                db.Set<TEntity>().AsNoTracking()
                    .Where(e => EF.Property<string>(e, "CompanyCode") == ctx.CompanyCode))
            .ToListAsync(cancellationToken);
        return IvMasterOperationResult<IReadOnlyList<TRow>>.Ok(rows.Select(map).ToList());
    }

    private async Task<IvMasterOperationResult<TVm>> GetCoreAsync<TEntity, TVm>(
        string menuCode, string entityLabel, string codeColumn, string code,
        Func<TEntity, TVm> map, CancellationToken cancellationToken) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Access, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailVm<TVm>(ctx.ErrorCode.Value, ctx.Error);
        }

        var normalized = NormalizeCode(code);
        if (normalized.Length == 0)
        {
            return FailVm<TVm>(IvMasterErrorCode.Validation, "Code is required.", "Code");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Set<TEntity>().AsNoTracking().FirstOrDefaultAsync(
            e => EF.Property<string>(e, "CompanyCode") == ctx.CompanyCode
                 && EF.Property<string>(e, codeColumn) == normalized,
            cancellationToken);
        return entity is null
            ? FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.")
            : IvMasterOperationResult<TVm>.Ok(map(entity));
    }

    private async Task<IvMasterOperationResult<TVm>> SaveCoreAsync<TEntity, TVm>(
        string menuCode, string entityLabel, string codeColumn, string codeField, int codeMaxLength,
        string code, TVm model, bool isNew, Dictionary<string, string> errors,
        Func<TEntity, byte[]?> rowVersionOf, Func<TVm, byte[]?> vmRowVersionOf,
        Action<TEntity, TVm, DateTime, string> applyFields,
        Func<string, string, string?, string?, DateTime, string, TEntity> create,
        Func<TEntity, TVm> mapVm, CancellationToken cancellationToken) where TEntity : class
    {
        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        var ctx = await RequireCompanyScopeAsync(menuCode, permission, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailVm<TVm>(ctx.ErrorCode.Value, ctx.Error);
        }

        var normalized = NormalizeCode(code);
        if (normalized.Length == 0)
        {
            errors[codeField] = "Code is required.";
        }
        else if (normalized.Length > codeMaxLength)
        {
            errors[codeField] = $"Code must be at most {codeMaxLength} characters.";
        }

        if (!isNew && vmRowVersionOf(model) is not { Length: > 0 })
        {
            return FailVm<TVm>(
                IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<TVm>.Fail(IvMasterErrorCode.Validation, "Validation failed.", errors);
        }

        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return FailVm<TVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            if (isNew)
            {
                // EF == uses DB collation (production CI); do not add a separate OrdinalIgnoreCase check.
                var exists = await db.Set<TEntity>().AnyAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                         && EF.Property<string>(e, codeColumn) == normalized,
                    cancellationToken);
                if (exists)
                {
                    return FailVm<TVm>(
                        IvMasterErrorCode.DuplicateKey, $"{entityLabel} code already exists.", codeField);
                }

                var entity = create(scope.CompanyCode!, normalized, scope.BranchCode, scope.LocationCode, now, user);
                applyFields(entity, model, now, user);
                EnsureSqliteRowVersion(db, entity);
                db.Set<TEntity>().Add(entity);
                await db.SaveChangesAsync(cancellationToken);
                return IvMasterOperationResult<TVm>.Ok(mapVm(entity));
            }

            var tracked = await db.Set<TEntity>().FirstOrDefaultAsync(
                e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                     && EF.Property<string>(e, codeColumn) == normalized,
                cancellationToken);
            if (tracked is null)
            {
                return FailVm<TVm>(IvMasterErrorCode.NotFound, $"{entityLabel} not found.");
            }

            if (!RowVersionsEqual(rowVersionOf(tracked), vmRowVersionOf(model)))
            {
                return FailVm<TVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
            }

            db.Entry(tracked).Property("RowVersion").OriginalValue = vmRowVersionOf(model)!;
            applyFields(tracked, model, now, user);
            await db.SaveChangesAsync(cancellationToken);
            return IvMasterOperationResult<TVm>.Ok(mapVm(tracked));
        }
        catch (DbUpdateConcurrencyException)
        {
            return FailVm<TVm>(IvMasterErrorCode.Concurrency, "This record was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            return FailVm<TVm>(IvMasterErrorCode.DuplicateKey, $"{entityLabel} code already exists.", codeField);
        }
    }

    private async Task<IvMasterOperationResult<object>> SetActiveCoreAsync<TEntity>(
        string menuCode, string entityLabel, string codeColumn,
        IReadOnlyList<IvMasterKeyToken> items, bool isActive,
        Func<TEntity, byte[]?> rowVersionOf, Action<TEntity, bool, DateTime, string> setActive,
        CancellationToken cancellationToken) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Edit, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailObj(ctx.ErrorCode.Value, ctx.Error);
        }

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var user = Truncate(scope.UserId, 20);
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                var code = NormalizeCode(item.Code);
                var entity = await db.Set<TEntity>().FirstOrDefaultAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                         && EF.Property<string>(e, codeColumn) == code,
                    cancellationToken);
                if (entity is null || !RowVersionsEqual(rowVersionOf(entity), item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property("RowVersion").OriginalValue = item.RowVersion;
                setActive(entity, isActive, now, user);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    private async Task<IvMasterOperationResult<object>> DeleteCoreAsync<TEntity>(
        string menuCode, string entityLabel, string codeColumn,
        IReadOnlyList<IvMasterKeyToken> items, Func<TEntity, byte[]?> rowVersionOf,
        CancellationToken cancellationToken) where TEntity : class
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return FailObj(ctx.ErrorCode.Value, ctx.Error);
        }

        if (items is null || items.Count == 0)
        {
            return FailObj(IvMasterErrorCode.Validation, "No records selected.");
        }

        var check = await CanDeleteCoreAsync(menuCode, items.Select(x => x.Code).ToList(), cancellationToken);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(
                IvMasterErrorCode.InUse, check.Message ?? "Record is in use.", deleteCheck: check);
        }

        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return FailObj(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in items)
            {
                var code = NormalizeCode(item.Code);
                var entity = await db.Set<TEntity>().FirstOrDefaultAsync(
                    e => EF.Property<string>(e, "CompanyCode") == scope.CompanyCode
                         && EF.Property<string>(e, codeColumn) == code,
                    cancellationToken);
                if (entity is null || !RowVersionsEqual(rowVersionOf(entity), item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return FailObj(
                        IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property("RowVersion").OriginalValue = item.RowVersion;
                db.Set<TEntity>().Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return IvMasterOperationResult<object>.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return FailObj(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }
    }

    /// <summary>
    /// Phase-1: returns Ok after permission check. Does not count references
    /// (PoSupplier.BuyingTerm / CategoryCode, future PO AuthorisedBy, etc.).
    /// </summary>
    private async Task<DeleteCheckResult> CanDeleteCoreAsync(
        string menuCode, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        var ctx = await RequireCompanyScopeAsync(menuCode, PermissionCodes.Delete, cancellationToken);
        if (ctx.ErrorCode is not null)
        {
            return DeleteCheckResult.Blocked(MessageFor(ctx.ErrorCode.Value), []);
        }

        if (!NormalizeCodes(codes).Any())
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        return DeleteCheckResult.Ok();
    }

    // ================= mappers =================
    private static PoBuyerListRow MapBuyerRow(PoBuyer x) => new()
    {
        Code = x.BuyerCode,
        Name = x.BuyerName,
        Desc = x.BuyerDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static PoBuyerEditVm MapBuyer(PoBuyer x) => new()
    {
        Code = x.BuyerCode,
        Name = x.BuyerName,
        Desc = x.BuyerDesc,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static PoBuyingTermListRow MapBuyingTermRow(PoBuyingTerm x) => new()
    {
        Code = x.BuyingTerm,
        Description = x.Description,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static PoBuyingTermEditVm MapBuyingTerm(PoBuyingTerm x) => new()
    {
        Code = x.BuyingTerm,
        Description = x.Description,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static PoCategoryListRow MapCategoryRow(PoCategory x) => new()
    {
        Code = x.Category,
        Description = x.Description,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static PoCategoryEditVm MapCategory(PoCategory x) => new()
    {
        Code = x.Category,
        Description = x.Description,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static PoAuthorisedListRow MapAuthorisedRow(PoAuthorised x) => new()
    {
        Code = x.Authorised,
        Name = x.Name,
        Email = x.Email,
        MobileNo = x.MobileNo,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static PoAuthorisedEditVm MapAuthorised(PoAuthorised x) => new()
    {
        Code = x.Authorised,
        Name = x.Name,
        Email = x.Email,
        MobileNo = x.MobileNo,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static PoPurItemListRow MapPurItemRow(PoPurItem x) => new()
    {
        Id = x.Id,
        ICode = x.ICode,
        IDesc = x.IDesc,
        Category = x.Category,
        Vendor = x.Vendor,
        VendName = x.VendName,
        Currency = x.Currency,
        UnitPrice = x.UnitPrice,
        Moq = x.Moq,
        Status = x.Status,
        RowVersion = x.RowVersion ?? []
    };

    private static PoPurItemEditVm MapPurItem(PoPurItem x) => new()
    {
        Id = x.Id,
        ICode = x.ICode,
        IDesc = x.IDesc,
        Category = x.Category,
        SubCategory = x.SubCategory,
        Dept = x.Dept,
        PurQty = x.PurQty,
        PurUom = x.PurUom,
        Vendor = x.Vendor,
        VendName = x.VendName,
        VendorPartNo = x.VendorPartNo,
        Currency = x.Currency,
        UnitPrice = x.UnitPrice,
        Moq = x.Moq,
        LeadTime = x.LeadTime,
        Status = x.Status,
        Remarks = x.Remarks,
        PurchaseGlCode = x.PurchaseGlCode,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static void ApplyPurItem(PoPurItem e, PoPurItemEditVm v, DateTime now, string user)
    {
        e.ICode = (v.ICode ?? string.Empty).Trim();
        e.IDesc = Null(v.IDesc);
        e.Category = Null(v.Category);
        e.SubCategory = Null(v.SubCategory);
        e.Dept = Null(v.Dept);
        e.PurQty = v.PurQty;
        e.PurUom = Null(v.PurUom);
        e.Vendor = (v.Vendor ?? string.Empty).Trim();
        e.VendName = Null(v.VendName);
        e.VendorPartNo = Null(v.VendorPartNo);
        e.Currency = Null(v.Currency);
        e.UnitPrice = v.UnitPrice;
        e.Moq = v.Moq;
        e.LeadTime = v.LeadTime;
        e.Status = Null(v.Status);
        e.Remarks = Null(v.Remarks);
        e.PurchaseGlCode = Null(v.PurchaseGlCode);
        e.ModifiedDate = now;
        e.ModifiedBy = user;
    }

    private static Dictionary<string, string> ValidatePurItem(PoPurItemEditVm v)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var iCode = (v.ICode ?? string.Empty).Trim();
        if (iCode.Length == 0)
        {
            errors["ICode"] = "Item code is required.";
        }
        else if (iCode.Length > 30)
        {
            errors["ICode"] = "Item code must be at most 30 characters.";
        }

        var vendor = (v.Vendor ?? string.Empty).Trim();
        if (vendor.Length == 0)
        {
            errors["Vendor"] = "Vendor is required.";
        }
        else if (vendor.Length > 60)
        {
            errors["Vendor"] = "Vendor must be at most 60 characters.";
        }

        ValidateOptionalLength(errors, "IDesc", v.IDesc, 200);
        ValidateOptionalLength(errors, "Category", v.Category, 20);
        ValidateOptionalLength(errors, "SubCategory", v.SubCategory, 20);
        ValidateOptionalLength(errors, "Dept", v.Dept, 20);
        ValidateOptionalLength(errors, "PurUom", v.PurUom, 10);
        ValidateOptionalLength(errors, "VendName", v.VendName, 200);
        ValidateOptionalLength(errors, "VendorPartNo", v.VendorPartNo, 50);
        ValidateOptionalLength(errors, "Currency", v.Currency, 20);
        ValidateOptionalLength(errors, "Status", v.Status, 20);
        ValidateOptionalLength(errors, "Remarks", v.Remarks, 250);
        ValidateOptionalLength(errors, "PurchaseGlCode", v.PurchaseGlCode, 20);

        if (v.PurQty < 0)
        {
            errors["PurQty"] = "Purchase quantity cannot be negative.";
        }

        if (v.Moq < 0)
        {
            errors["Moq"] = "MOQ cannot be negative.";
        }

        if (v.UnitPrice is < 0)
        {
            errors["UnitPrice"] = "Unit price cannot be negative.";
        }

        if (v.LeadTime is < 0)
        {
            errors["LeadTime"] = "Lead time cannot be negative.";
        }

        return errors;
    }

    // ================= helpers =================
    private async Task<ScopeResult> RequireCompanyScopeAsync(
        string menuCode, string permission, CancellationToken cancellationToken)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return ScopeResult.Fail(IvMasterErrorCode.InvalidScope, "Invalid company context.");
        }

        if (!await _accessRights.CanAsync(menuCode, permission, cancellationToken))
        {
            return ScopeResult.Fail(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        return ScopeResult.Ok(scope.CompanyCode!, scope.UserId);
    }

    private sealed record ScopeResult(
        string? CompanyCode, string? UserId, IvMasterErrorCode? ErrorCode, string? Error)
    {
        public static ScopeResult Ok(string companyCode, string? userId) =>
            new(companyCode, userId, null, null);

        public static ScopeResult Fail(IvMasterErrorCode code, string message) =>
            new(null, null, code, message);
    }

    private static string NormalizeCode(string? code) => (code ?? string.Empty).Trim();

    private static List<string> NormalizeCodes(IReadOnlyList<string>? codes) =>
        (codes ?? [])
            .Select(NormalizeCode)
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? Null(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    /// <summary>
    /// SQL Server generates rowversion. SQLite (tests) needs a non-empty client token.
    /// </summary>
    private static void EnsureSqliteRowVersion(DbContext db, object entity)
    {
        var provider = db.Database.ProviderName ?? string.Empty;
        if (!provider.Contains("Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prop = db.Entry(entity).Property("RowVersion");
        if (prop.CurrentValue is not byte[] { Length: > 0 })
        {
            prop.CurrentValue = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;

    private static void ValidateOptionalLength(
        Dictionary<string, string> errors, string field, string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (value.Trim().Length > maxLength)
        {
            errors[field] = $"{field} must be at most {maxLength} characters.";
        }
    }

    private static string MessageFor(IvMasterErrorCode code) => code switch
    {
        IvMasterErrorCode.AccessDenied => "Not authorized.",
        IvMasterErrorCode.InvalidScope => "Invalid company context.",
        _ => "Request failed."
    };

    private static IvMasterOperationResult<IReadOnlyList<T>> FailList<T>(
        IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<IReadOnlyList<T>>.Fail(code, message ?? MessageFor(code));

    private static IvMasterOperationResult<T> FailVm<T>(
        IvMasterErrorCode code, string? message = null, string? field = null)
    {
        IReadOnlyDictionary<string, string>? errors = null;
        if (!string.IsNullOrWhiteSpace(field) && !string.IsNullOrWhiteSpace(message))
        {
            errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [field] = message
            };
        }

        return IvMasterOperationResult<T>.Fail(code, message ?? MessageFor(code), errors);
    }

    private static IvMasterOperationResult<PoPurItemEditVm> FailVmPurItemDuplicate(string iCode, string vendor)
    {
        var message = $"Purchase item already exists for item code '{iCode}' and vendor '{vendor}'.";
        return IvMasterOperationResult<PoPurItemEditVm>.Fail(
            IvMasterErrorCode.DuplicateKey,
            message,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ICode"] = message
            });
    }

    private static IvMasterOperationResult<object> FailObj(IvMasterErrorCode code, string? message = null) =>
        IvMasterOperationResult<object>.Fail(code, message ?? MessageFor(code));
}
