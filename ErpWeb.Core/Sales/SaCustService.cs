using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales;

public sealed class SaCustService : ISaCustService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly ICurrentDateService _dates;
    private readonly ISaCustRepository _customers;
    private readonly ISaCustLookupService _lookups;

    public SaCustService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        ICurrentDateService dates,
        ISaCustRepository customers,
        ISaCustLookupService lookups)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _dates = dates;
        _customers = customers;
        _lookups = lookups;
    }

    public async Task<IvMasterOperationResult<SaCustListPage>> SearchAsync(
        SaCustListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<SaCustListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<SaCustListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new SaCustListQuery();
        var (rows, total) = await _customers.SearchPagedAsync(context.CompanyCode!, ToSearchArgs(query), cancellationToken);
        return IvMasterOperationResult<SaCustListPage>.Ok(new SaCustListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = total
        });
    }

    public async Task<IvMasterOperationResult<SaCustEditVm>> GetAsync(
        string custCode,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<SaCustEditVm>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Access, cancellationToken))
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var code = (custCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.Validation, "Customer code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, includeChildren: true, cancellationToken);
        if (entity is null)
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.NotFound, "Customer was not found.");
        }

        return IvMasterOperationResult<SaCustEditVm>.Ok(MapEditVm(entity));
    }

    public async Task<IvMasterOperationResult<SaCustEditVm>> SaveAsync(
        SaCustEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default)
    {
        if (model is null)
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.Validation, "Save model is required.");
        }

        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<SaCustEditVm>(context.ErrorCode.Value, context.Error!);
        }

        var permission = isNew ? PermissionCodes.Add : PermissionCodes.Edit;
        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, permission, cancellationToken))
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        var errors = ValidateModel(model, isNew, null);
        if (isNew)
        {
            await AddLookupValidationErrorsAsync(errors, model, existingSnapshot: null, cancellationToken);
        }

        var code = (model.CustCode ?? string.Empty).Trim();
        if (isNew && !errors.ContainsKey("CustCode") &&
            await _customers.ExistsAsync(context.CompanyCode!, code, cancellationToken))
        {
            errors["CustCode"] = "Customer code already exists.";
        }

        if (!isNew && (model.RowVersion is null || model.RowVersion.Length == 0))
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.Concurrency, "Concurrency token is missing. Reload and try again.");
        }

        if (errors.Count > 0)
        {
            return IvMasterOperationResult<SaCustEditVm>.Fail(
                IvMasterErrorCode.Validation,
                "Validation failed.",
                errors);
        }

        var writeScope = _tenant.TryWriteScope();
        if (writeScope is null)
        {
            return Fail<SaCustEditVm>(IvMasterErrorCode.InvalidScope, "Invalid company, branch, or location context.");
        }

        var now = _dates.Now;
        var userId = Truncate(writeScope.UserId, 10);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            if (isNew)
            {
                var entity = new SaCust
                {
                    CompanyCode = context.CompanyCode!,
                    CustCode = code,
                    CreatedDate = now,
                    CreatedBy = userId,
                    ModifiedDate = now,
                    ModifiedBy = userId
                };
                InventoryLeftoverSite.Apply(entity, writeScope);
                ApplyHeaderFields(entity, model, snapshot: null);
                ApplyContactLine1(entity, model.Contacts.FirstOrDefault());
                ApplyInvoiceShipment(entity, model);
                db.SaCusts.Add(entity);
                await db.SaveChangesAsync(cancellationToken);

                await ReplaceChildrenAsync(db, context.CompanyCode!, code, model, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);

                var saved = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, true, cancellationToken);
                return IvMasterOperationResult<SaCustEditVm>.Ok(MapEditVm(saved!));
            }

            var existing = await _customers.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
            if (existing is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<SaCustEditVm>(IvMasterErrorCode.NotFound, "Customer was not found.");
            }

            if (!RowVersionsEqual(existing.RowVersion, model.RowVersion!))
            {
                await tx.RollbackAsync(cancellationToken);
                return Fail<SaCustEditVm>(IvMasterErrorCode.Concurrency, "This customer was modified by another user.");
            }

            var snapshot = MapEditVm(existing);
            var editErrors = ValidateModel(model, isNew: false, snapshot);
            await AddLookupValidationErrorsAsync(editErrors, model, snapshot, cancellationToken);
            if (editErrors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return IvMasterOperationResult<SaCustEditVm>.Fail(
                    IvMasterErrorCode.Validation,
                    "Validation failed.",
                    editErrors);
            }

            db.Entry(existing).Property(x => x.RowVersion).OriginalValue = model.RowVersion!;
            ApplyHeaderFields(existing, model, snapshot);
            ApplyContactLine1(existing, model.Contacts.FirstOrDefault());
            ApplyInvoiceShipment(existing, model);
            existing.ModifiedDate = now;
            existing.ModifiedBy = userId;

            await db.SaveChangesAsync(cancellationToken);

            await ReplaceChildrenAsync(db, context.CompanyCode!, code, model, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            var reloaded = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, true, cancellationToken);
            return IvMasterOperationResult<SaCustEditVm>.Ok(MapEditVm(reloaded!));
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return Fail<SaCustEditVm>(IvMasterErrorCode.Concurrency, "This customer was modified by another user.");
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return IvMasterOperationResult<SaCustEditVm>.Fail(
                IvMasterErrorCode.DuplicateKey,
                "Customer code already exists.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CustCode"] = "Customer code already exists."
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

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Edit, cancellationToken))
        {
            return Fail<object>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        if (items is null || items.Count == 0)
        {
            return Fail<object>(IvMasterErrorCode.Validation, "No records selected.");
        }

        var now = _dates.Now;
        var userId = Truncate(context.UserId!, 10);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                var entity = await _customers.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
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

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Delete, cancellationToken))
        {
            return DeleteCheckResult.Blocked("Not authorized.", []);
        }

        if (codes is null || codes.Count == 0)
        {
            return DeleteCheckResult.Blocked("No records selected.", []);
        }

        var refs = await _customers.CountReferencesBulkAsync(context.CompanyCode!, codes, cancellationToken);
        var blocked = refs.Where(kv => kv.Value.Count > 0).ToList();
        if (blocked.Count > 0)
        {
            var hits = blocked.SelectMany(kv => kv.Value.Select(r => new IvMasterReferenceHit
            {
                ReferenceType = r.ReferenceType,
                Count = r.Count
            })).ToList();
            return DeleteCheckResult.Blocked("Customer is in use and cannot be deleted.", hits);
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

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Delete, cancellationToken))
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
                check.Message ?? "Customer is in use.",
                deleteCheck: check);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in items)
            {
                var code = (item.Code ?? string.Empty).Trim();
                var entity = await _customers.GetTrackedAsync(db, context.CompanyCode!, code, cancellationToken);
                if (entity is null || !RowVersionsEqual(entity.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return Fail<object>(IvMasterErrorCode.Concurrency, "One or more records changed. Refresh and try again.");
                }

                db.Entry(entity).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                db.SaCusts.Remove(entity);
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

    public async Task<IvMasterOperationResult<SaCustListPage>> ExportRowsAsync(
        SaCustListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.ErrorCode is not null)
        {
            return Fail<SaCustListPage>(context.ErrorCode.Value, context.Error!);
        }

        if (!await _accessRights.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Export, cancellationToken))
        {
            return Fail<SaCustListPage>(IvMasterErrorCode.AccessDenied, "Not authorized.");
        }

        query ??= new SaCustListQuery();
        var rows = await _customers.ListExportAsync(context.CompanyCode!, ToSearchArgs(query), cancellationToken);
        return IvMasterOperationResult<SaCustListPage>.Ok(new SaCustListPage
        {
            Rows = rows.Select(MapListRow).ToList(),
            TotalCount = rows.Count
        });
    }

    private static async Task ReplaceChildrenAsync(
        AppDbContext db,
        string companyCode,
        string custCode,
        SaCustEditVm model,
        CancellationToken cancellationToken)
    {
        var existingAdds = await db.SaCustAdds
            .Where(x => x.CompanyCode == companyCode && x.CustCode == custCode)
            .ToListAsync(cancellationToken);
        db.SaCustAdds.RemoveRange(existingAdds);

        var line = 1;
        foreach (var addr in model.Addresses)
        {
            db.SaCustAdds.Add(new SaCustAdd
            {
                CompanyCode = companyCode,
                CustCode = custCode,
                Line = line++,
                AddName = addr.AddName,
                DeliverTo = addr.DeliverTo,
                Address1 = addr.Address1,
                Address2 = addr.Address2,
                Address3 = addr.Address3,
                Address4 = addr.Address4,
                City = addr.City,
                State = addr.State,
                PostalCode = addr.PostalCode,
                Country = addr.Country,
                Tel = addr.Tel,
                Fax = addr.Fax
            });
        }

        var existingContacts = await db.SaCustContacts
            .Where(x => x.CompanyCode == companyCode && x.CustCode == custCode)
            .ToListAsync(cancellationToken);
        db.SaCustContacts.RemoveRange(existingContacts);

        line = 1;
        foreach (var contact in model.Contacts.Skip(1))
        {
            db.SaCustContacts.Add(new SaCustContact
            {
                CompanyCode = companyCode,
                CustCode = custCode,
                Line = line++,
                ContactPerson = contact.ContactPerson,
                Title = contact.Title,
                Department = contact.Department,
                ContactEmail = contact.ContactEmail,
                ContactTelp = contact.ContactTelp,
                ContactFax = contact.ContactFax
            });
        }
    }

    private static void ApplyContactLine1(SaCust entity, SaCustContactVm? line1)
    {
        if (line1 is null)
        {
            entity.ContactPerson = null;
            entity.Title = null;
            entity.Department = null;
            entity.ContactEmail = null;
            entity.ContactTelp = null;
            entity.ContactFax = null;
            return;
        }

        entity.ContactPerson = NullIfWhiteSpace(line1.ContactPerson);
        entity.Title = NullIfWhiteSpace(line1.Title);
        entity.Department = NullIfWhiteSpace(line1.Department);
        entity.ContactEmail = NullIfWhiteSpace(line1.ContactEmail);
        entity.ContactTelp = NullIfWhiteSpace(line1.ContactTelp);
        entity.ContactFax = NullIfWhiteSpace(line1.ContactFax);
    }

    private static void ApplyInvoiceShipment(SaCust entity, SaCustEditVm model)
    {
        if (model.AppInvoice == true)
        {
            entity.InvName = entity.CustName;
            entity.InvAddress1 = model.Address1;
            entity.InvAddress2 = model.Address2;
            entity.InvAddress3 = model.Address3;
            entity.InvCity = model.City;
            entity.InvState = model.State;
            entity.InvPostalCode = model.PostalCode;
            entity.InvCountry = model.Country;
            entity.InvTel = model.Tel;
            entity.InvFax = model.Fax;
            entity.InvEmail = model.Email;
            entity.InvWebsite = model.Website;
        }

        if (UsesMainShip(model))
        {
            entity.ShipName = entity.CustName;
            entity.ShipAddress1 = model.Address1;
            entity.ShipAddress2 = model.Address2;
            entity.ShipAddress3 = model.Address3;
            entity.ShipCity = model.City;
            entity.ShipState = model.State;
            entity.ShipPostalCode = model.PostalCode;
            entity.ShipCountry = model.Country;
            entity.ShipTel = model.Tel;
            entity.ShipFax = model.Fax;
            entity.ShipEmail = model.Email;
            entity.ShipWebsite = model.Website;
        }
        else
        {
            entity.ShipName = NullIfWhiteSpace(model.ShipName);
            entity.ShipAddress1 = NullIfWhiteSpace(model.ShipAddress1);
            entity.ShipAddress2 = NullIfWhiteSpace(model.ShipAddress2);
            entity.ShipAddress3 = NullIfWhiteSpace(model.ShipAddress3);
            entity.ShipCity = NullIfWhiteSpace(model.ShipCity);
            entity.ShipState = NullIfWhiteSpace(model.ShipState);
            entity.ShipPostalCode = NullIfWhiteSpace(model.ShipPostalCode);
            entity.ShipCountry = NullIfWhiteSpace(model.ShipCountry);
            entity.ShipTel = NullIfWhiteSpace(model.ShipTel);
            entity.ShipFax = NullIfWhiteSpace(model.ShipFax);
            entity.ShipEmail = NullIfWhiteSpace(model.ShipEmail);
            entity.ShipWebsite = NullIfWhiteSpace(model.ShipWebsite);
        }
    }

    private static bool UsesMainShip(SaCustEditVm model) => model.AppShip == true;

    private static void ApplyHeaderFields(SaCust entity, SaCustEditVm model, SaCustEditVm? snapshot)
    {
        entity.CustName = (model.CustName ?? string.Empty).Trim();
        SetIfChanged(entity, snapshot, () => entity.CustShortName, v => entity.CustShortName = v, NullIfWhiteSpace(model.CustShortName));
        SetIfChanged(entity, snapshot, () => entity.CustType, v => entity.CustType = v, NullIfWhiteSpace(model.CustType));
        SetIfChanged(entity, snapshot, () => entity.InvoicePrefix, v => entity.InvoicePrefix = v, NullIfWhiteSpace(model.InvoicePrefix));
        SetIfChanged(entity, snapshot, () => entity.CustGroupCode, v => entity.CustGroupCode = v, NullIfWhiteSpace(model.CustGroupCode));
        SetIfChanged(entity, snapshot, () => entity.LmwAts, v => entity.LmwAts = v, model.LmwAts);
        SetIfChanged(entity, snapshot, () => entity.SalesmanCode, v => entity.SalesmanCode = v, NullIfWhiteSpace(model.SalesmanCode));
        SetIfChanged(entity, snapshot, () => entity.AreaCode, v => entity.AreaCode = v, NullIfWhiteSpace(model.AreaCode));
        SetIfChanged(entity, snapshot, () => entity.SubGroupCode, v => entity.SubGroupCode = v, NullIfWhiteSpace(model.SubGroupCode));
        SetIfChanged(entity, snapshot, () => entity.IndustryCode, v => entity.IndustryCode = v, NullIfWhiteSpace(model.IndustryCode));
        SetIfChanged(entity, snapshot, () => entity.ChannelCode, v => entity.ChannelCode = v, NullIfWhiteSpace(model.ChannelCode));
        entity.IsActive = model.IsActive;

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
        entity.Email = NullIfWhiteSpace(model.Email);
        entity.Website = NullIfWhiteSpace(model.Website);
        SetIfChanged(entity, snapshot, () => entity.CjLmw, v => entity.CjLmw = v, NullIfWhiteSpace(model.CjLmw));
        SetIfChanged(entity, snapshot, () => entity.CustBrn, v => entity.CustBrn = v, NullIfWhiteSpace(model.CustBrn));
        SetIfChanged(entity, snapshot, () => entity.RegType, v => entity.RegType = v, NullIfWhiteSpace(model.RegType));
        SetIfChanged(entity, snapshot, () => entity.Remark, v => entity.Remark = v, NullIfWhiteSpace(model.Remark));
        SetIfChanged(entity, snapshot, () => entity.AppInvoice, v => entity.AppInvoice = v, model.AppInvoice);
        SetIfChanged(entity, snapshot, () => entity.AppShip, v => entity.AppShip = v, model.AppShip);

        SetIfChanged(entity, snapshot, () => entity.Taxable, v => entity.Taxable = v, model.Taxable);
        SetIfChanged(entity, snapshot, () => entity.TaxGrCode, v => entity.TaxGrCode = v, NullIfWhiteSpace(model.TaxGrCode));
        SetIfChanged(entity, snapshot, () => entity.GstregNo, v => entity.GstregNo = v, NullIfWhiteSpace(model.GstregNo));
        entity.PayCode = NullIfWhiteSpace(model.PayCode);
        entity.Currency = NullIfWhiteSpace(model.Currency);
        SetIfChanged(entity, snapshot, () => entity.GroupDiscount, v => entity.GroupDiscount = v, NullIfWhiteSpace(model.GroupDiscount));
        SetIfChanged(entity, snapshot, () => entity.DiscountMethod, v => entity.DiscountMethod = v, NullIfWhiteSpace(model.DiscountMethod));
        SetIfChanged(entity, snapshot, () => entity.PriceMethod, v => entity.PriceMethod = v, NullIfWhiteSpace(model.PriceMethod));
        SetIfChanged(entity, snapshot, () => entity.AgingType, v => entity.AgingType = v, NullIfWhiteSpace(model.AgingType));
        SetIfChanged(entity, snapshot, () => entity.PaidUpCapital, v => entity.PaidUpCapital = v, model.PaidUpCapital);
        SetIfChanged(entity, snapshot, () => entity.GlCode, v => entity.GlCode = v, NullIfWhiteSpace(model.GlCode));
        SetIfChanged(entity, snapshot, () => entity.OpeningAmount, v => entity.OpeningAmount = v, model.OpeningAmount);
        SetIfChanged(entity, snapshot, () => entity.CreditTerm, v => entity.CreditTerm = v, NullIfWhiteSpace(model.CreditTerm));
        SetIfChanged(entity, snapshot, () => entity.CreditLimit, v => entity.CreditLimit = v, model.CreditLimit);
        SetIfChanged(entity, snapshot, () => entity.CustPriceCode, v => entity.CustPriceCode = v, NullIfWhiteSpace(model.CustPriceCode));
    }

    private static void SetIfChanged<T>(
        SaCust entity,
        SaCustEditVm? snapshot,
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

    private static Dictionary<string, string> ValidateModel(SaCustEditVm model, bool isNew, SaCustEditVm? snapshot)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var code = (model.CustCode ?? string.Empty).Trim();
        var name = (model.CustName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(code))
        {
            errors["CustCode"] = "Customer code is required.";
        }
        else if (code.Length > 30)
        {
            errors["CustCode"] = "Customer code must be at most 30 characters.";
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors["CustName"] = "Customer name is required.";
        }

        if (string.IsNullOrWhiteSpace(model.CustType))
        {
            errors["CustType"] = "Customer type is required.";
        }

        if (string.IsNullOrWhiteSpace(model.Country))
        {
            errors["Country"] = "Country is required.";
        }

        if (!UsesMainShip(model) && string.IsNullOrWhiteSpace(model.ShipCountry))
        {
            errors["ShipCountry"] = "Ship-to country is required when shipment is not the same as the main address.";
        }

        if (string.IsNullOrWhiteSpace(model.PayCode))
        {
            errors["PayCode"] = "Payment term is required.";
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

        return errors;
    }

    private async Task AddLookupValidationErrorsAsync(
        Dictionary<string, string> errors,
        SaCustEditVm model,
        SaCustEditVm? existingSnapshot,
        CancellationToken cancellationToken)
    {
        if (!await _lookups.ValidateTypeAssignmentAsync(model.CustType, existingSnapshot?.CustType, cancellationToken))
        {
            errors["CustType"] = $"Type '{model.CustType}' is not valid.";
        }

        if (!await _lookups.ValidateGroupAssignmentAsync(model.CustGroupCode, existingSnapshot?.CustGroupCode, cancellationToken))
        {
            errors["CustGroupCode"] = $"Group '{model.CustGroupCode}' is not valid.";
        }

        if (!await _lookups.ValidateAreaAssignmentAsync(model.AreaCode, existingSnapshot?.AreaCode, cancellationToken))
        {
            errors["AreaCode"] = $"Area '{model.AreaCode}' is not valid.";
        }

        if (!await _lookups.ValidateCountryAssignmentAsync(model.Country, existingSnapshot?.Country, cancellationToken))
        {
            errors["Country"] = $"Country '{model.Country}' is not valid.";
        }

        if (!UsesMainShip(model)
            && !await _lookups.ValidateCountryAssignmentAsync(model.ShipCountry, existingSnapshot?.ShipCountry, cancellationToken))
        {
            errors["ShipCountry"] = $"Ship-to country '{model.ShipCountry}' is not valid.";
        }

        if (!UsesMainShip(model)
            && !await _lookups.ValidateStateAssignmentAsync(model.ShipState, existingSnapshot?.ShipState, cancellationToken))
        {
            errors["ShipState"] = $"Ship-to state '{model.ShipState}' is not valid.";
        }

        if (!await _lookups.ValidateIndustryAssignmentAsync(model.IndustryCode, existingSnapshot?.IndustryCode, cancellationToken))
        {
            errors["IndustryCode"] = $"Industry '{model.IndustryCode}' is not valid.";
        }

        if (!await _lookups.ValidateChannelAssignmentAsync(model.ChannelCode, existingSnapshot?.ChannelCode, cancellationToken))
        {
            errors["ChannelCode"] = $"Channel '{model.ChannelCode}' is not valid.";
        }

        if (!await _lookups.ValidateCurrencyAssignmentAsync(model.Currency, existingSnapshot?.Currency, cancellationToken))
        {
            errors["Currency"] = $"Currency '{model.Currency}' is not valid.";
        }

        if (!await _lookups.ValidateDisGroupAssignmentAsync(model.GroupDiscount, existingSnapshot?.GroupDiscount, cancellationToken))
        {
            errors["GroupDiscount"] = $"Discount group '{model.GroupDiscount}' is not valid.";
        }

        if (!await _lookups.ValidateStateAssignmentAsync(model.State, existingSnapshot?.State, cancellationToken))
        {
            errors["State"] = $"State '{model.State}' is not valid.";
        }

        if (!await _lookups.ValidateTaxGroupAssignmentAsync(model.TaxGrCode, existingSnapshot?.TaxGrCode, cancellationToken))
        {
            errors["TaxGrCode"] = $"Tax group '{model.TaxGrCode}' is not valid.";
        }

        if (!await _lookups.ValidatePayCodeAssignmentAsync(model.PayCode, existingSnapshot?.PayCode, cancellationToken))
        {
            errors["PayCode"] = $"Payment term '{model.PayCode}' is not valid.";
        }

        for (var i = 0; i < model.Addresses.Count; i++)
        {
            var addr = model.Addresses[i];
            var existing = existingSnapshot?.Addresses.FirstOrDefault(a => a.Line == addr.Line)
                ?? (i < (existingSnapshot?.Addresses.Count ?? 0) ? existingSnapshot!.Addresses[i] : null);
            var line = addr.Line > 0 ? addr.Line : i + 1;

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

    private static bool PaymentCreditChanged(SaCustEditVm model, SaCustEditVm snapshot) =>
        !string.Equals(model.PayCode, snapshot.PayCode, StringComparison.Ordinal)
        || !string.Equals(model.Currency, snapshot.Currency, StringComparison.Ordinal)
        || model.Taxable != snapshot.Taxable
        || !string.Equals(model.TaxGrCode, snapshot.TaxGrCode, StringComparison.Ordinal)
        || !string.Equals(model.GstregNo, snapshot.GstregNo, StringComparison.Ordinal)
        || !string.Equals(model.GroupDiscount, snapshot.GroupDiscount, StringComparison.Ordinal)
        || !string.Equals(model.DiscountMethod, snapshot.DiscountMethod, StringComparison.Ordinal)
        || !string.Equals(model.PriceMethod, snapshot.PriceMethod, StringComparison.Ordinal)
        || !string.Equals(model.AgingType, snapshot.AgingType, StringComparison.Ordinal)
        || model.PaidUpCapital != snapshot.PaidUpCapital
        || !string.Equals(model.GlCode, snapshot.GlCode, StringComparison.Ordinal)
        || model.OpeningAmount != snapshot.OpeningAmount
        || !string.Equals(model.CreditTerm, snapshot.CreditTerm, StringComparison.Ordinal)
        || model.CreditLimit != snapshot.CreditLimit
        || !string.Equals(model.CustPriceCode, snapshot.CustPriceCode, StringComparison.Ordinal);

    private static SaCustListRow MapListRow(SaCust x) => new()
    {
        CustCode = x.CustCode,
        CustName = x.CustName,
        CustShortName = x.CustShortName,
        CustType = x.CustType,
        CustGroupCode = x.CustGroupCode,
        SalesmanCode = x.SalesmanCode,
        AreaCode = x.AreaCode,
        City = x.City,
        Tel = x.Tel,
        PayCode = x.PayCode,
        Currency = x.Currency,
        CreditLimit = x.CreditLimit,
        IsActive = x.IsActive,
        RowVersion = x.RowVersion ?? []
    };

    private static SaCustEditVm MapEditVm(SaCust x)
    {
        var contacts = new List<SaCustContactVm>
        {
            new()
            {
                Line = 1,
                ContactPerson = x.ContactPerson,
                Title = x.Title,
                Department = x.Department,
                ContactEmail = x.ContactEmail,
                ContactTelp = x.ContactTelp,
                ContactFax = x.ContactFax
            }
        };

        contacts.AddRange(x.Contacts.OrderBy(c => c.Line).Select(c => new SaCustContactVm
        {
            Line = c.Line + 1,
            ContactPerson = c.ContactPerson,
            Title = c.Title,
            Department = c.Department,
            ContactEmail = c.ContactEmail,
            ContactTelp = c.ContactTelp,
            ContactFax = c.ContactFax
        }));

        return new SaCustEditVm
        {
            CustCode = x.CustCode,
            CustName = x.CustName,
            CustShortName = x.CustShortName,
            CustType = x.CustType,
            InvoicePrefix = x.InvoicePrefix,
            CustGroupCode = x.CustGroupCode,
            LmwAts = x.LmwAts,
            SalesmanCode = x.SalesmanCode,
            AreaCode = x.AreaCode,
            SubGroupCode = x.SubGroupCode,
            IndustryCode = x.IndustryCode,
            ChannelCode = x.ChannelCode,
            IsActive = x.IsActive,
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
            Email = x.Email,
            Website = x.Website,
            CjLmw = x.CjLmw,
            CustBrn = x.CustBrn,
            RegType = x.RegType,
            Remark = x.Remark,
            AppInvoice = x.AppInvoice,
            AppShip = x.AppShip,
            ShipName = x.ShipName,
            ShipAddress1 = x.ShipAddress1,
            ShipAddress2 = x.ShipAddress2,
            ShipAddress3 = x.ShipAddress3,
            ShipCity = x.ShipCity,
            ShipState = x.ShipState,
            ShipPostalCode = x.ShipPostalCode,
            ShipCountry = x.ShipCountry,
            ShipTel = x.ShipTel,
            ShipFax = x.ShipFax,
            ShipEmail = x.ShipEmail,
            ShipWebsite = x.ShipWebsite,
            Addresses = x.Addresses.OrderBy(a => a.Line).Select(a => new SaCustAddressVm
            {
                Line = a.Line,
                AddName = a.AddName,
                DeliverTo = a.DeliverTo,
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
            Contacts = contacts,
            Taxable = x.Taxable,
            TaxGrCode = x.TaxGrCode,
            GstregNo = x.GstregNo,
            PayCode = x.PayCode,
            Currency = x.Currency,
            GroupDiscount = x.GroupDiscount,
            DiscountMethod = x.DiscountMethod,
            PriceMethod = x.PriceMethod,
            AgingType = x.AgingType,
            PaidUpCapital = x.PaidUpCapital,
            GlCode = x.GlCode,
            OpeningAmount = x.OpeningAmount,
            CreditTerm = x.CreditTerm,
            CreditLimit = x.CreditLimit,
            CustPriceCode = x.CustPriceCode,
            RowVersion = x.RowVersion,
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            ModifiedDate = x.ModifiedDate,
            ModifiedBy = x.ModifiedBy
        };
    }

    private static SaCustSearchArgs ToSearchArgs(SaCustListQuery query) => new()
    {
        SearchText = query.SearchText,
        IsActive = query.IsActive,
        CustType = query.CustType,
        CustGroupCode = query.CustGroupCode,
        SalesmanCode = query.SalesmanCode,
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
        || ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true;
}
