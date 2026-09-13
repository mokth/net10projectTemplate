using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class PoSupplierService : IPoSupplierService
{
    public const string AttachDocKey = "SUPPLIER";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly IPoSupplierRepository _suppliers;
    private readonly IPoSupplierLookupService _lookups;
    private readonly IPoSupplierAttachmentFileCleanup? _attachmentCleanup;

    public PoSupplierService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        IPoSupplierRepository suppliers,
        IPoSupplierLookupService lookups,
        IPoSupplierAttachmentFileCleanup? attachmentCleanup = null)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _suppliers = suppliers;
        _lookups = lookups;
        _attachmentCleanup = attachmentCleanup;
    }

    public async Task<IvMasterOperationResult<PoSupplierListPage>> SearchAsync(
        PoSupplierListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoSupplierListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<PoSupplierListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new PoSupplierListQuery();
        var (rows, total) = await _suppliers.SearchPagedAsync(
            context.CompanyCode!,
            context.BranchCode!,
            ToSearchArgs(query),
            cancellationToken);
        return IvMasterOperationResult<PoSupplierListPage>.Ok(new PoSupplierListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<PoSupplierEditVm>> GetAsync(
        string suppCode,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoSupplierEditVm>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (suppCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.Validation, "Supplier code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _suppliers.GetByCodeAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            code,
            includeChildren: true,
            cancellationToken);
        if (entity is null)
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.NotFound, "Supplier was not found.");
        }

        return IvMasterOperationResult<PoSupplierEditVm>.Ok(MapEditVm(entity));
    }

    public async Task<IvMasterOperationResult<PoSupplierEditVm>> SaveAsync(
        PoSupplierEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.Validation, "Save model is required.");
        }

        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoSupplierEditVm>(context.ErrorCode.Value, context.Error!);
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, permission, cancellationToken))
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var writeScope = _tenant.TryBranchScope();
        if (writeScope is null)
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company or branch context.");
        }

        var code = (model.SuppCode ?? string.Empty).Trim();
        model.SuppCode = code;

        var errors = ValidateModel(model, isNew, null);
        if (isNew)
        {
            await AddLookupValidationErrorsAsync(errors, model, existingSnapshot: null, cancellationToken);
            if (!errors.ContainsKey("SuppCode") &&
                await _suppliers.ExistsAsync(context.CompanyCode!, context.BranchCode!, code, cancellationToken))
            {
                errors["SuppCode"] = "Supplier code already exists.";
            }
        }

        if (!isNew && (model.RowVersion is null || model.RowVersion.Length == 0))
        {
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<PoSupplierEditVm>.Fail(
                IvMasterErrorCode.Validation,
                "Validation failed.",
                errors);
        }

        var now = _dates.Now;
        var userId = Truncate(writeScope.UserId, 20);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var entity = new PoSupplier
                {
                    CompanyCode = context.CompanyCode!,
                    BranchCode = context.BranchCode!,
                    SuppCode = code,
                    CreatedDate = now,
                    CreatedBy = userId,
                    ModifiedDate = now,
                    ModifiedBy = userId
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                ApplyHeaderFields(entity, model, snapshot: null);
                db.PoSuppliers.Add(entity);
                await db.SaveChangesAsync(cancellationToken);

                await ReplaceChildrenAsync(db, context.CompanyCode!, context.BranchCode!, code, model, writeScope, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                var saved = await _suppliers.GetByCodeAsync(
                    db, context.CompanyCode!, context.BranchCode!, code, true, cancellationToken);
                return IvMasterOperationResult<PoSupplierEditVm>.Ok(MapEditVm(saved!));
            }

            var existing = await _suppliers.GetTrackedAsync(
                db, context.CompanyCode!, context.BranchCode!, code, cancellationToken);
            if (existing is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<PoSupplierEditVm>(IvMasterErrorCode.NotFound, "Supplier was not found.");
            }

            if (!RowVersionsEqual(existing.RowVersion, model.RowVersion!))
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<PoSupplierEditVm>(IvMasterErrorCode.Concurrency, "This supplier was modified by another user.");
            }

            var snapshot = MapEditVm(existing);
            var editErrors = ValidateModel(model, isNew: false, snapshot);
            await AddLookupValidationErrorsAsync(editErrors, model, snapshot, cancellationToken);
            if (editErrors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<PoSupplierEditVm>.Fail(
                    IvMasterErrorCode.Validation,
                    "Validation failed.",
                    editErrors);
            }

            // Concurrency gate: header-only SaveChanges while no child is tracked.
            db.Entry(existing).Property(x => x.RowVersion).OriginalValue = model.RowVersion!;
            ApplyHeaderFields(existing, model, snapshot);
            existing.ModifiedDate = now;
            existing.ModifiedBy = userId;

            await db.SaveChangesAsync(cancellationToken);

            await ReplaceChildrenAsync(db, context.CompanyCode!, context.BranchCode!, code, model, writeScope, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var reloaded = await _suppliers.GetByCodeAsync(
                db, context.CompanyCode!, context.BranchCode!, code, true, cancellationToken);
            return IvMasterOperationResult<PoSupplierEditVm>.Ok(MapEditVm(reloaded!));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<PoSupplierEditVm>(IvMasterErrorCode.Concurrency, "This supplier was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<PoSupplierEditVm>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "Supplier code already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["SuppCode"] = "Supplier code already exists."
                });
        }
        catch
        {
            await tx.RollbackAsync(cancellationToken);
            throw;
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

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Edit, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        var now = _dates.Now;
        var userId = Truncate(context.UserId!, 20);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                var entity = await _suppliers.GetTrackedAsync(
                    db, context.CompanyCode!, context.BranchCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
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
            return Fail<object>(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
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

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Delete, cancellationToken))
        {
            return DeleteCheckResult.Blocked("Not authorized.", []);
        }

        if (codes is null || codes.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        // Phase-1: reference checking is not active. Empty result means "not scanned", not "zero refs found".
        var refs = await _suppliers.CountReferencesBulkAsync(
            context.CompanyCode!,
            context.BranchCode!,
            codes,
            cancellationToken);
        var blocked = refs.Where(kv => kv.Value.Count > 0).ToList();
        if (blocked.Count > 0)
        {
            var hits = blocked.SelectMany(kv => kv.Value.Select(r => new IvMasterReferenceHit
            {
                ReferenceType = r.ReferenceType,
                Count = r.Count
            })).ToList();
            return DeleteCheckResult.Blocked("Supplier is in use and cannot be deleted.", hits);
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

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Delete, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        var codes = items.Select(x => (x.Code ?? string.Empty).Trim()).Where(x => x.Length > 0).ToList();
        var check = await CanDeleteBulkAsync(codes, cancellationToken);
        if (!check.CanDelete)
        {
            return IvMasterOperationResult<object>.Fail(
                IvMasterErrorCode.InUse,
                check.Message ?? "Supplier is in use.",
                deleteCheck: check);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var pendingFileCleanup = new List<(string SuppCode, string DocId, string DocName, string? DocPath)>();

        try
        {
            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                var entity = await _suppliers.GetTrackedAsync(
                    db, context.CompanyCode!, context.BranchCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                var docId = SupplierAttachDocId.Compute(code);
                var attachRows = await db.PoAttachFiles
                    .Where(x =>
                        x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.DocKey == AttachDocKey
                        && x.DocId == docId)
                    .ToListAsync(cancellationToken);

                foreach (var attach in attachRows)
                {
                    pendingFileCleanup.Add((code, attach.DocId, attach.DocName, attach.DocPath));
                }

                db.PoAttachFiles.RemoveRange(attachRows);
                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                db.PoSuppliers.Remove(entity);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<object>(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
        }

        if (_attachmentCleanup is not null)
        {
            foreach (var file in pendingFileCleanup)
            {
                await _attachmentCleanup.TryDeletePhysicalFileAsync(
                    context.CompanyCode!,
                    context.BranchCode!,
                    file.SuppCode,
                    file.DocId,
                    file.DocName,
                    file.DocPath,
                    "supplier-delete",
                    cancellationToken);
            }
        }

        return IvMasterOperationResult<object>.Ok();
    }

    public async Task<IvMasterOperationResult<PoSupplierListPage>> ExportRowsAsync(
        PoSupplierListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<PoSupplierListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.PurchaseSupplierProfile, PermissionCodes.Export, cancellationToken))
        {
            return Fail<PoSupplierListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new PoSupplierListQuery();
        var rows = await _suppliers.ListExportAsync(
            context.CompanyCode!,
            context.BranchCode!,
            ToSearchArgs(query),
            cancellationToken);
        return IvMasterOperationResult<PoSupplierListPage>.Ok(new PoSupplierListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = rows.Count
        });
    }

    private static async Task ReplaceChildrenAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string suppCode,
        PoSupplierEditVm model,
        InventoryTenantScope writeScope,
        CancellationToken cancellationToken)
    {
        var existingAdds = await db.PoSupplierAdds
            .Where(x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.SuppCode == suppCode)
            .ToListAsync(cancellationToken);
        db.PoSupplierAdds.RemoveRange(existingAdds);

        var line = 1;
        foreach (var addr in model.Addresses.Where(IsAddressPersisted))
        {
            var child = new PoSupplierAdd
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                SuppCode = suppCode,
                Line = line++,
                SuppName = NullIfWhiteSpace(addr.SuppName),
                Address1 = NullIfWhiteSpace(addr.Address1),
                Address2 = NullIfWhiteSpace(addr.Address2),
                Address3 = NullIfWhiteSpace(addr.Address3),
                Address4 = NullIfWhiteSpace(addr.Address4),
                City = NullIfWhiteSpace(addr.City),
                State = NullIfWhiteSpace(addr.State),
                PostalCode = NullIfWhiteSpace(addr.PostalCode),
                Country = NullIfWhiteSpace(addr.Country),
                Tel = NullIfWhiteSpace(addr.Tel),
                Fax = NullIfWhiteSpace(addr.Fax)
            };
            InventoryLeftoverSite.Apply(child, writeScope);
            db.PoSupplierAdds.Add(child);
        }
    }

    private static bool IsAddressPersisted(PoSupplierAddressVm addr) =>
        !string.IsNullOrWhiteSpace(addr.SuppName)
        || !string.IsNullOrWhiteSpace(addr.Address1)
        || !string.IsNullOrWhiteSpace(addr.Address2)
        || !string.IsNullOrWhiteSpace(addr.Address3)
        || !string.IsNullOrWhiteSpace(addr.Address4)
        || !string.IsNullOrWhiteSpace(addr.City)
        || !string.IsNullOrWhiteSpace(addr.State)
        || !string.IsNullOrWhiteSpace(addr.PostalCode)
        || !string.IsNullOrWhiteSpace(addr.Country)
        || !string.IsNullOrWhiteSpace(addr.Tel)
        || !string.IsNullOrWhiteSpace(addr.Fax);

    private static void ApplyHeaderFields(PoSupplier entity, PoSupplierEditVm model, PoSupplierEditVm? snapshot)
    {
        entity.SuppName = (model.SuppName ?? string.Empty).Trim();
        SetIfChanged(entity, snapshot, () => entity.SuppShortName, v => entity.SuppShortName = v, NullIfWhiteSpace(model.SuppShortName));
        SetIfChanged(entity, snapshot, () => entity.SuppType, v => entity.SuppType = v, NullIfWhiteSpace(model.SuppType));
        SetIfChanged(entity, snapshot, () => entity.SupplierBrn, v => entity.SupplierBrn = v, NullIfWhiteSpace(model.SupplierBrn));
        SetIfChanged(entity, snapshot, () => entity.CategoryCode, v => entity.CategoryCode = v, NullIfWhiteSpace(model.CategoryCode));
        SetIfChanged(entity, snapshot, () => entity.CreditorSubGroup, v => entity.CreditorSubGroup = v, NullIfWhiteSpace(model.CreditorSubGroup));
        SetIfChanged(entity, snapshot, () => entity.AreaCode, v => entity.AreaCode = v, NullIfWhiteSpace(model.AreaCode));
        SetIfChanged(entity, snapshot, () => entity.RegType, v => entity.RegType = v, NullIfWhiteSpace(model.RegType));
        SetIfChanged(entity, snapshot, () => entity.PoPrefix, v => entity.PoPrefix = v, NullIfWhiteSpace(model.PoPrefix));
        entity.IsActive = model.IsActive;
        SetIfChanged(entity, snapshot, () => entity.Lmw, v => entity.Lmw = v, model.Lmw);
        SetIfChanged(entity, snapshot, () => entity.MiscCode, v => entity.MiscCode = v, NullIfWhiteSpace(model.MiscCode));

        entity.Address1 = NullIfWhiteSpace(model.Address1);
        entity.Address2 = NullIfWhiteSpace(model.Address2);
        entity.Address3 = NullIfWhiteSpace(model.Address3);
        entity.Address4 = NullIfWhiteSpace(model.Address4);
        entity.City = NullIfWhiteSpace(model.City);
        entity.State = NullIfWhiteSpace(model.State);
        entity.PostalCode = NullIfWhiteSpace(model.PostalCode);
        entity.Country = NullIfWhiteSpace(model.Country);
        entity.Tel = NullIfWhiteSpace(model.Tel);
        entity.Fax = NullIfWhiteSpace(model.Fax);
        entity.Telex = NullIfWhiteSpace(model.Telex);
        entity.Email = NullIfWhiteSpace(model.Email);
        entity.Website = NullIfWhiteSpace(model.Website);

        entity.ContactPerson = NullIfWhiteSpace(model.ContactPerson);
        entity.Title = NullIfWhiteSpace(model.Title);
        entity.Department = NullIfWhiteSpace(model.Department);
        entity.ContactEmail = NullIfWhiteSpace(model.ContactEmail);
        entity.ContactTelp = NullIfWhiteSpace(model.ContactTelp);
        entity.ContactFax = NullIfWhiteSpace(model.ContactFax);

        entity.ContactPerson2 = NullIfWhiteSpace(model.ContactPerson2);
        entity.Title2 = NullIfWhiteSpace(model.Title2);
        entity.Department2 = NullIfWhiteSpace(model.Department2);
        entity.ContactEmail2 = NullIfWhiteSpace(model.ContactEmail2);
        entity.ContactTelp2 = NullIfWhiteSpace(model.ContactTelp2);
        entity.ContactFax2 = NullIfWhiteSpace(model.ContactFax2);

        entity.ContactPerson3 = NullIfWhiteSpace(model.ContactPerson3);
        entity.Title3 = NullIfWhiteSpace(model.Title3);
        entity.Department3 = NullIfWhiteSpace(model.Department3);
        entity.ContactEmail3 = NullIfWhiteSpace(model.ContactEmail3);
        entity.ContactTelp3 = NullIfWhiteSpace(model.ContactTelp3);
        entity.ContactFax3 = NullIfWhiteSpace(model.ContactFax3);

        entity.ContactPerson4 = NullIfWhiteSpace(model.ContactPerson4);
        entity.Title4 = NullIfWhiteSpace(model.Title4);
        entity.Department4 = NullIfWhiteSpace(model.Department4);
        entity.ContactEmail4 = NullIfWhiteSpace(model.ContactEmail4);
        entity.ContactTelp4 = NullIfWhiteSpace(model.ContactTelp4);
        entity.ContactFax4 = NullIfWhiteSpace(model.ContactFax4);

        SetIfChanged(entity, snapshot, () => entity.Taxable, v => entity.Taxable = v, model.Taxable);
        SetIfChanged(entity, snapshot, () => entity.TaxGrCode, v => entity.TaxGrCode = v, NullIfWhiteSpace(model.TaxGrCode));
        SetIfChanged(entity, snapshot, () => entity.GstregNo, v => entity.GstregNo = v, NullIfWhiteSpace(model.GstregNo));
        SetIfChanged(entity, snapshot, () => entity.BankName, v => entity.BankName = v, NullIfWhiteSpace(model.BankName));
        SetIfChanged(entity, snapshot, () => entity.AccountNo, v => entity.AccountNo = v, NullIfWhiteSpace(model.AccountNo));
        SetIfChanged(entity, snapshot, () => entity.StatementType, v => entity.StatementType = v, NullIfWhiteSpace(model.StatementType));
        entity.PayCode = NullIfWhiteSpace(model.PayCode);
        entity.Currency = NullIfWhiteSpace(model.Currency);
        SetIfChanged(entity, snapshot, () => entity.BuyingTerm, v => entity.BuyingTerm = v, NullIfWhiteSpace(model.BuyingTerm));
        SetIfChanged(entity, snapshot, () => entity.GlCode, v => entity.GlCode = v, NullIfWhiteSpace(model.GlCode));
        SetIfChanged(entity, snapshot, () => entity.AgingType, v => entity.AgingType = v, NullIfWhiteSpace(model.AgingType));
        SetIfChanged(entity, snapshot, () => entity.CreditLimit, v => entity.CreditLimit = v, model.CreditLimit);

        SetIfChanged(entity, snapshot, () => entity.Remark, v => entity.Remark = v, NullIfWhiteSpace(model.Remark));
        SetIfChanged(entity, snapshot, () => entity.BizDesc, v => entity.BizDesc = v, NullIfWhiteSpace(model.BizDesc));
    }

    private static void SetIfChanged<T>(
        PoSupplier entity,
        PoSupplierEditVm? snapshot,
        Func<T?> getCurrent,
        Action<T?> set,
        T? newValue)
    {
        if (snapshot is null)
        {
            set(newValue);
            return;
        }

        var current = getCurrent();
        if (!Equals(current, newValue))
        {
            set(newValue);
        }
    }

    private static Dictionary<string, string> ValidateModel(PoSupplierEditVm model, bool isNew, PoSupplierEditVm? snapshot)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.SuppCode ?? string.Empty).Trim();
        var name = (model.SuppName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors["SuppCode"] = "Supplier code is required.";
        }
        else if (code.Length > 60)
        {
            errors["SuppCode"] = "Supplier code must be at most 60 characters.";
        }
        else if (code.Any(char.IsControl))
        {
            errors["SuppCode"] = "Supplier code must not contain control characters.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["SuppName"] = "Supplier name is required.";
        }
        else if (name.Length > 200)
        {
            errors["SuppName"] = "Supplier name must be at most 200 characters.";
        }

        if (string.IsNullOrWhiteSpace(model.Currency))
        {
            errors["Currency"] = "Currency is required.";
        }

        if (isNew && string.IsNullOrWhiteSpace(model.GlCode))
        {
            errors["GlCode"] = "GL code is required.";
        }
        else if (!isNew && snapshot is not null && PaymentCreditChanged(model, snapshot) && string.IsNullOrWhiteSpace(model.GlCode))
        {
            errors["GlCode"] = "GL code is required when payment/credit fields change.";
        }

        if (!string.IsNullOrWhiteSpace(model.StatementType))
        {
            var st = model.StatementType.Trim();
            if (!string.Equals(st, PoSupplierPaymentOptions.StatementOpenItem, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(st, PoSupplierPaymentOptions.StatementBalanceForward, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(st, PoSupplierPaymentOptions.StatementNone, StringComparison.OrdinalIgnoreCase))
            {
                errors["StatementType"] = "Statement type is not valid.";
            }
        }

        if (!string.IsNullOrWhiteSpace(model.AgingType))
        {
            var aging = model.AgingType.Trim();
            if (!string.Equals(aging, PoSupplierPaymentOptions.AgingInvoice, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(aging, PoSupplierPaymentOptions.AgingDue, StringComparison.OrdinalIgnoreCase))
            {
                errors["AgingType"] = "Aging type is not valid.";
            }
        }

        return errors;
    }

    private async Task AddLookupValidationErrorsAsync(
        Dictionary<string, string> errors,
        PoSupplierEditVm model,
        PoSupplierEditVm? existingSnapshot,
        CancellationToken cancellationToken)
    {
        if (!await _lookups.ValidateAreaAssignmentAsync(model.AreaCode, existingSnapshot?.AreaCode, cancellationToken))
        {
            errors["AreaCode"] = $"Area '{model.AreaCode}' is not valid.";
        }

        if (!await _lookups.ValidateCountryAssignmentAsync(model.Country, existingSnapshot?.Country, cancellationToken))
        {
            errors["Country"] = $"Country '{model.Country}' is not valid.";
        }

        if (!await _lookups.ValidateStateAssignmentAsync(model.State, existingSnapshot?.State, cancellationToken))
        {
            errors["State"] = $"State '{model.State}' is not valid.";
        }

        if (!await _lookups.ValidateCurrencyAssignmentAsync(model.Currency, existingSnapshot?.Currency, cancellationToken))
        {
            errors["Currency"] = $"Currency '{model.Currency}' is not valid.";
        }

        if (!await _lookups.ValidateTaxGroupAssignmentAsync(model.TaxGrCode, existingSnapshot?.TaxGrCode, cancellationToken))
        {
            errors["TaxGrCode"] = $"Tax group '{model.TaxGrCode}' is not valid.";
        }

        if (!await _lookups.ValidatePayCodeAssignmentAsync(model.PayCode, existingSnapshot?.PayCode, cancellationToken))
        {
            errors["PayCode"] = $"Payment term '{model.PayCode}' is not valid.";
        }

        if (!await _lookups.ValidateBuyingTermAssignmentAsync(model.BuyingTerm, existingSnapshot?.BuyingTerm, cancellationToken))
        {
            errors["BuyingTerm"] = $"Buying term '{model.BuyingTerm}' is not valid.";
        }

        for (var i = 0; i < model.Addresses.Count; i++)
        {
            var addr = model.Addresses[i];
            if (!IsAddressPersisted(addr))
            {
                continue;
            }

            var existing = existingSnapshot?.Addresses.ElementAtOrDefault(i);
            var line = i + 1;

            if (!await _lookups.ValidateCountryAssignmentAsync(addr.Country, existing?.Country, cancellationToken))
            {
                errors[$"Addresses[{i}].Country"] = $"Address {line}: country '{addr.Country}' is not valid.";
            }

            if (!await _lookups.ValidateStateAssignmentAsync(addr.State, existing?.State, cancellationToken))
            {
                errors[$"Addresses[{i}].State"] = $"Address {line}: state '{addr.State}' is not valid.";
            }
        }
    }

    private static bool PaymentCreditChanged(PoSupplierEditVm model, PoSupplierEditVm snapshot) =>
        !string.Equals(model.PayCode, snapshot.PayCode, StringComparison.Ordinal)
        || !string.Equals(model.Currency, snapshot.Currency, StringComparison.Ordinal)
        || model.Taxable != snapshot.Taxable
        || !string.Equals(model.TaxGrCode, snapshot.TaxGrCode, StringComparison.Ordinal)
        || !string.Equals(model.GstregNo, snapshot.GstregNo, StringComparison.Ordinal)
        || !string.Equals(model.BankName, snapshot.BankName, StringComparison.Ordinal)
        || !string.Equals(model.AccountNo, snapshot.AccountNo, StringComparison.Ordinal)
        || !string.Equals(model.StatementType, snapshot.StatementType, StringComparison.Ordinal)
        || !string.Equals(model.BuyingTerm, snapshot.BuyingTerm, StringComparison.Ordinal)
        || !string.Equals(model.AgingType, snapshot.AgingType, StringComparison.Ordinal)
        || model.CreditLimit != snapshot.CreditLimit
        || !string.Equals(model.GlCode, snapshot.GlCode, StringComparison.Ordinal);

    private static PoSupplierListRow MapListRow(PoSupplier x) => new()
    {
        SuppCode = x.SuppCode,
        SuppName = x.SuppName,
        SuppShortName = x.SuppShortName,
        SuppType = x.SuppType,
        CategoryCode = x.CategoryCode,
        CreditorSubGroup = x.CreditorSubGroup,
        AreaCode = x.AreaCode,
        City = x.City,
        State = x.State,
        Country = x.Country,
        Tel = x.Tel,
        Email = x.Email,
        SupplierBrn = x.SupplierBrn,
        PayCode = x.PayCode,
        Currency = x.Currency,
        BuyingTerm = x.BuyingTerm,
        GlCode = x.GlCode,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static PoSupplierEditVm MapEditVm(PoSupplier x) => new()
    {
        SuppCode = x.SuppCode,
        SuppName = x.SuppName,
        SuppShortName = x.SuppShortName,
        SuppType = x.SuppType,
        SupplierBrn = x.SupplierBrn,
        CategoryCode = x.CategoryCode,
        CreditorSubGroup = x.CreditorSubGroup,
        AreaCode = x.AreaCode,
        RegType = x.RegType,
        PoPrefix = x.PoPrefix,
        IsActive = x.IsActive,
        Lmw = x.Lmw,
        MiscCode = x.MiscCode,
        Address1 = x.Address1,
        Address2 = x.Address2,
        Address3 = x.Address3,
        Address4 = x.Address4,
        City = x.City,
        State = x.State,
        PostalCode = x.PostalCode,
        Country = x.Country,
        Tel = x.Tel,
        Fax = x.Fax,
        Telex = x.Telex,
        Email = x.Email,
        Website = x.Website,
        Addresses = x.Addresses.OrderBy(a => a.Line).Select(a => new PoSupplierAddressVm
        {
            Line = a.Line,
            SuppName = a.SuppName,
            Address1 = a.Address1,
            Address2 = a.Address2,
            Address3 = a.Address3,
            Address4 = a.Address4,
            City = a.City,
            State = a.State,
            PostalCode = a.PostalCode,
            Country = a.Country,
            Tel = a.Tel,
            Fax = a.Fax
        }).ToList(),
        ContactPerson = x.ContactPerson,
        Title = x.Title,
        Department = x.Department,
        ContactEmail = x.ContactEmail,
        ContactTelp = x.ContactTelp,
        ContactFax = x.ContactFax,
        ContactPerson2 = x.ContactPerson2,
        Title2 = x.Title2,
        Department2 = x.Department2,
        ContactEmail2 = x.ContactEmail2,
        ContactTelp2 = x.ContactTelp2,
        ContactFax2 = x.ContactFax2,
        ContactPerson3 = x.ContactPerson3,
        Title3 = x.Title3,
        Department3 = x.Department3,
        ContactEmail3 = x.ContactEmail3,
        ContactTelp3 = x.ContactTelp3,
        ContactFax3 = x.ContactFax3,
        ContactPerson4 = x.ContactPerson4,
        Title4 = x.Title4,
        Department4 = x.Department4,
        ContactEmail4 = x.ContactEmail4,
        ContactTelp4 = x.ContactTelp4,
        ContactFax4 = x.ContactFax4,
        Taxable = x.Taxable,
        TaxGrCode = x.TaxGrCode,
        GstregNo = x.GstregNo,
        BankName = x.BankName,
        AccountNo = x.AccountNo,
        StatementType = x.StatementType,
        PayCode = x.PayCode,
        Currency = x.Currency,
        BuyingTerm = x.BuyingTerm,
        GlCode = x.GlCode,
        AgingType = x.AgingType,
        CreditLimit = x.CreditLimit,
        Remark = x.Remark,
        BizDesc = x.BizDesc,
        RowVersion = x.RowVersion,
        CreatedDate = x.CreatedDate,
        CreatedBy = x.CreatedBy,
        ModifiedDate = x.ModifiedDate,
        ModifiedBy = x.ModifiedBy
    };

    private static PoSupplierSearchArgs ToSearchArgs(PoSupplierListQuery query) => new()
    {
        SearchText = query.SearchText,
        IsActive = query.IsActive,
        SuppType = query.SuppType,
        CategoryCode = query.CategoryCode,
        AreaCode = query.AreaCode,
        SortField = query.SortField,
        SortDescending = query.SortDescending,
        Skip = query.Skip,
        Take = query.Take
    };

    private (IvMasterErrorCode? ErrorCode, string? Error, string? CompanyCode, string? BranchCode, string? UserId) ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return (IvMasterErrorCode.InvalidScope, "Invalid company or branch context.", null, null, null);
        }

        return (null, null, scope.CompanyCode, scope.BranchCode, scope.UserId);
    }

    private static IvMasterOperationResult<T> Fail<T>(IvMasterErrorCode code, string message) =>
        IvMasterOperationResult<T>.Fail(code, message);

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return left.SequenceEqual(right);
    }

    private static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true
        || ex.InnerException?.Message.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase) == true;
}
