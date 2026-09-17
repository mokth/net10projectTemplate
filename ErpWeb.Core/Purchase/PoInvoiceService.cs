using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ErpWeb.Core.Purchase;

public sealed class PoInvoiceService : IPoInvoiceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly IPoInvoiceRepository _invoices;
    private readonly IPoOrderRepository _orders;
    private readonly IPoCdnRepository _cdns;
    private readonly PoOrderOptions _options;
    private readonly ILogger<PoInvoiceService> _logger;

    public PoInvoiceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        IPoInvoiceRepository invoices,
        IPoOrderRepository orders,
        IPoCdnRepository cdns,
        IOptions<PoOrderOptions> options,
        ILogger<PoInvoiceService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _invoices = invoices;
        _orders = orders;
        _cdns = cdns;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PoInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var vendors = await db.PoSuppliers.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive)
            .OrderBy(x => x.SuppCode)
            .Select(x => new PoInvoiceVendorLookupRow
            {
                SuppCode = x.SuppCode,
                SuppName = x.SuppName ?? string.Empty,
                Currency = x.Currency
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new PoInvoiceTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var payCodes = await db.SaPaymentTerms.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive != false)
            .OrderBy(x => x.PayCode)
            .Select(x => new IvCodeLookupRow { Code = x.PayCode, Desc = x.PayDesc })
            .ToListAsync(cancellationToken);

        var currencies = await db.SaCurrencies.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive == true)
            .OrderBy(x => x.CurrCode)
            .Select(x => new IvCodeLookupRow { Code = x.CurrCode, Desc = x.CurrDesc })
            .ToListAsync(cancellationToken);

        // Department / Project masters — company + branch scoped, active only.
        var departments = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.IsActive)
            .OrderBy(x => x.DeptCode)
            .Select(x => new IvCodeLookupRow { Code = x.DeptCode, Desc = x.DeptName })
            .ToListAsync(cancellationToken);

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new IvCodeLookupRow { Code = x.ProjCode, Desc = x.ProjName })
            .ToListAsync(cancellationToken);

        return PoInvoiceOperationResult.OkLookups(new PoInvoiceLookups
        {
            Vendors = vendors,
            TaxGroups = taxGroups,
            PayCodes = payCodes,
            Currencies = currencies,
            Departments = departments,
            Projects = projects
        });
    }

    public async Task<PoInvoiceOperationResult> GetVendorDefaultsAsync(
        string vendorCode,
        DateTime docDate,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        var code = (vendorCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return PoInvoiceOperationResult.FailValidation("Vendor is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["VendorCode"] = "Vendor is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var vendor = await db.PoSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode && x.SuppCode == code && x.IsActive,
                cancellationToken);
        if (vendor is null)
        {
            return PoInvoiceOperationResult.Fail("Vendor was not found or is inactive.", PoInvoiceErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(vendor.Currency) ? "MYR" : vendor.Currency.Trim();
        var rate = await ResolveCurrRateAsync(db, scope.CompanyCode, currency, docDate == default ? _dates.Today : docDate, cancellationToken);

        return PoInvoiceOperationResult.OkVendorDefaults(new PoInvoiceVendorDefaults
        {
            VendorCode = vendor.SuppCode,
            VendorName = vendor.SuppName ?? string.Empty,
            Currency = currency,
            CurrRate = rate,
            PayCode = vendor.PayCode,
            TaxGrCode = vendor.TaxGrCode,
            InvAddress1 = vendor.Address1,
            InvAddress2 = vendor.Address2,
            InvAddress3 = vendor.Address3,
            InvAddress4 = vendor.Address4,
            City = vendor.City,
            State = vendor.State,
            PostalCode = vendor.PostalCode,
            Country = vendor.Country,
            Tel = vendor.Tel,
            Fax = vendor.Fax
        });
    }

    public async Task<PoInvoiceOperationResult> SearchAsync(
        PoInvoiceListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _invoices.SearchPagedAsync(
            db,
            scope.CompanyCode,
            scope.BranchCode,
            new PoInvoiceSearchArgs(
                query.Type,
                query.SearchText,
                query.Status,
                query.DateFrom,
                query.DateTo,
                query.SortField,
                query.SortDescending,
                query.Skip,
                query.Take),
            cancellationToken);

        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);
        var canPost = await CanAsync(PermissionCodes.Post, cancellationToken);
        var canRollback = await CanAsync(PermissionCodes.Rollback, cancellationToken);

        var docNos = rows.Select(x => x.DocNo).ToList();
        var lineCounts = await db.PoInvoiceDetails.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && docNos.Contains(x.DocNo))
            .GroupBy(x => x.DocNo)
            .Select(g => new { DocNo = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DocNo, x => x.Count, StringComparer.OrdinalIgnoreCase, cancellationToken);

        return PoInvoiceOperationResult.OkList(new PoInvoiceListPage
        {
            TotalCount = total,
            Rows = rows.Select(x =>
            {
                var isNew = string.Equals(x.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase);
                var isPosted = string.Equals(x.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase);
                return new PoInvoiceListRow
                {
                    DocNo = x.DocNo,
                    DocDate = x.DocDate,
                    Status = x.Status,
                    Type = x.Type,
                    VendorCode = x.VendorCode,
                    VendorName = x.VendorName,
                    InvNo = x.InvNo,
                    TotAmnt = x.TotAmnt,
                    LineCount = lineCounts.GetValueOrDefault(x.DocNo),
                    CreatedDate = x.CreatedDate,
                    CreatedBy = x.CreatedBy,
                    RowVersion = x.RowVersion,
                    CanEdit = canEdit && isNew,
                    CanDelete = canDelete && isNew,
                    CanPost = canPost && isNew,
                    CanRollback = canRollback && isPosted
                };
            }).ToList()
        });
    }

    public async Task<PoInvoiceOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await _invoices.GetWithDetailsAsync(
            db, scope.CompanyCode, scope.BranchCode, docNo, cancellationToken);
        if (entity is null)
        {
            return PoInvoiceOperationResult.Fail("Document was not found.", PoInvoiceErrorKind.NotFound);
        }

        return PoInvoiceOperationResult.OkDocument(await MapDocumentAsync(entity, cancellationToken));
    }

    public async Task<PoInvoiceOperationResult> SaveNewAsync(
        PoInvoiceSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return PoInvoiceOperationResult.Fail("Write context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var docType = PoInvoiceCalc.NormalizeType(request.Type);
        if (!PoInvoiceCalc.IsValidType(docType))
        {
            return PoInvoiceOperationResult.FailValidation("Type must be INV or CN.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["Type"] = "Type must be INV or CN." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var prepared = await PrepareAsync(db, write.CompanyCode, write.BranchCode, request, docType, excludeDocNo: null, cancellationToken);
            if (!prepared.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.FailValidation(
                    prepared.ErrorMessage ?? "Validation failed.",
                    prepared.Errors);
            }

            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    PoInvoiceLimits.NumberingModule,
                    "",
                    docDate,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail(
                    "PO invoice numbering is not configured for this company/branch.",
                    PoInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail(
                    "PO invoice numbering is not configured correctly. Contact an administrator.",
                    PoInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail(
                    "The next PO invoice number exceeds the configured length.",
                    PoInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail(
                    "The document could not be saved because of a database conflict. Try again.",
                    PoInvoiceErrorKind.Unexpected);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(write.UserId, 20);
            var invoice = BuildHeader(write.CompanyCode, write.BranchCode, issued, docType, docDate, request, prepared, now, uid);
            ApplyDetails(invoice, prepared.Lines!);
            PoInvoiceCalc.ApplyHeaderTotals(invoice, invoice.Details);
            TouchRowVersion(db, invoice);
            db.PoInvoices.Add(invoice);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(invoice.DocNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoiceOperationResult.Fail("Document number is already used.", PoInvoiceErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "PO invoice save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return PoInvoiceOperationResult.Fail(
                "The document could not be saved because of a database conflict. Try again.",
                PoInvoiceErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO invoice save failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoInvoiceOperationResult.Fail("Unable to save the document.", PoInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<PoInvoiceOperationResult> UpdateAsync(
        string docNo,
        PoInvoiceSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return PoInvoiceOperationResult.Fail("Write context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var no = (docNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, write.CompanyCode, write.BranchCode, no, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail("Document was not found.", PoInvoiceErrorKind.NotFound);
            }

            if (!string.Equals(invoice.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.Fail("Only NEW documents can be edited.", PoInvoiceErrorKind.BusinessRule);
            }

            if (request.RowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(invoice.RowVersion, request.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoInvoiceOperationResult.Fail(
                        "Document was changed by another user.",
                        PoInvoiceErrorKind.Concurrency);
                }

                db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            }

            var docType = PoInvoiceCalc.NormalizeType(invoice.Type);
            var prepared = await PrepareAsync(
                db, write.CompanyCode, write.BranchCode, request, docType, excludeDocNo: no, cancellationToken);
            if (!prepared.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoiceOperationResult.FailValidation(
                    prepared.ErrorMessage ?? "Validation failed.",
                    prepared.Errors);
            }

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            db.PoInvoiceDetails.RemoveRange(invoice.Details);
            invoice.Details.Clear();

            ApplyHeaderFields(invoice, request, prepared);
            ApplyDetails(invoice, prepared.Lines!);
            PoInvoiceCalc.ApplyHeaderTotals(invoice, invoice.Details);
            invoice.ModifiedDate = DateTime.UtcNow;
            invoice.ModifiedBy = Truncate(write.UserId, 20);
            TouchRowVersion(db, invoice);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(invoice.DocNo, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoiceOperationResult.Fail("Document was changed by another user.", PoInvoiceErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO invoice update failed.");
            await tx.RollbackAsync(cancellationToken);
            return PoInvoiceOperationResult.Fail("Unable to update the document.", PoInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<PoInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoInvoiceOperationResult.FailValidation("At least one document is required.");
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return PoInvoiceOperationResult.Fail("Write context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var results = new List<PoInvoicePostingItemResult>();
        foreach (var item in items)
        {
            var docNo = (item.DocNo ?? string.Empty).Trim();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var invoice = await _invoices.LockForUpdateAsync(db, write.CompanyCode, write.BranchCode, docNo, cancellationToken);
                if (invoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(PoInvoicePostingItemResult.Failed(docNo, "Document was not found."));
                    continue;
                }

                if (!string.Equals(invoice.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(PoInvoicePostingItemResult.Failed(docNo, "Only NEW documents can be deleted."));
                    continue;
                }

                if (item.RowVersion is { Length: > 0 })
                {
                    if (!RowVersionsEqual(invoice.RowVersion, item.RowVersion))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        results.Add(PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user."));
                        continue;
                    }

                    db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                }

                await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
                db.PoInvoices.Remove(invoice);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                results.Add(new PoInvoicePostingItemResult
                {
                    DocNo = docNo,
                    Succeeded = true,
                    Outcome = "Deleted"
                });
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                results.Add(PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PO invoice delete failed for {DocNo}", docNo);
                await tx.RollbackAsync(cancellationToken);
                results.Add(PoInvoicePostingItemResult.Failed(docNo, "Unable to delete the document."));
            }
        }

        return PoInvoiceOperationResult.OkPosting(results);
    }

    public async Task<PoInvoiceOperationResult> PostAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoInvoiceOperationResult.FailValidation("At least one document is required.");
        }

        if (items.Count > PoInvoiceLimits.MaxPostSelection)
        {
            return PoInvoiceOperationResult.FailValidation($"Select at most {PoInvoiceLimits.MaxPostSelection} documents.");
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return PoInvoiceOperationResult.Fail("Write context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Post, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var results = new List<PoInvoicePostingItemResult>();
        foreach (var item in items)
        {
            results.Add(await PostOneAsync(write, item, cancellationToken));
        }

        return PoInvoiceOperationResult.OkPosting(results);
    }

    public async Task<PoInvoiceOperationResult> RollbackAsync(
        IReadOnlyList<PoInvoiceKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoInvoiceOperationResult.FailValidation("At least one document is required.");
        }

        if (items.Count > PoInvoiceLimits.MaxPostSelection)
        {
            return PoInvoiceOperationResult.FailValidation($"Select at most {PoInvoiceLimits.MaxPostSelection} documents.");
        }

        var write = _tenant.TryWriteScope();
        if (write is null)
        {
            return PoInvoiceOperationResult.Fail("Write context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Rollback, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var results = new List<PoInvoicePostingItemResult>();
        foreach (var item in items)
        {
            results.Add(await RollbackOneAsync(write, item, cancellationToken));
        }

        return PoInvoiceOperationResult.OkPosting(results);
    }

    public async Task<PoInvoiceOperationResult> SearchPostedInvoicesAsync(
        string? vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.Type == PoInvoiceTypes.Invoice
                && x.Status == PoInvoiceStatuses.Posted);

        var vendor = (vendorCode ?? string.Empty).Trim();
        if (vendor.Length > 0)
        {
            query = query.Where(x => x.VendorCode == vendor);
        }

        var term = (searchText ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            query = query.Where(x =>
                x.DocNo.Contains(term)
                || (x.InvNo != null && x.InvNo.Contains(term))
                || (x.VendorName != null && x.VendorName.Contains(term)));
        }

        var rows = await query
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Take(50)
            .Select(x => new PoInvoiceInvoicePickerRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        return PoInvoiceOperationResult.OkInvoicePicker(rows);
    }

    public async Task<PoInvoiceOperationResult> SearchInvoiceablePoLinesAsync(
        string vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        var vendor = (vendorCode ?? string.Empty).Trim();
        if (vendor.Length == 0)
        {
            return PoInvoiceOperationResult.FailValidation("Vendor is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var term = (searchText ?? string.Empty).Trim();
        var headers = await db.PoOrders.AsNoTracking()
            .Include(x => x.Details)
            .Where(x => x.CompanyCode == scope.CompanyCode
                && x.BranchCode == scope.BranchCode
                && x.VendCode == vendor
                && x.Status != PoOrderStatuses.Cancelled)
            .OrderByDescending(x => x.PoDate)
            .Take(100)
            .ToListAsync(cancellationToken);

        var latestByPo = headers
            .GroupBy(x => x.PoNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.PoRelNo).First())
            .ToList();

        var rows = new List<PoInvoicePoLinePickerRow>();
        foreach (var po in latestByPo)
        {
            foreach (var d in po.Details.OrderBy(x => x.Line))
            {
                var net = PoOrderCalc.ComputeNetReceived(d.RecvQty, d.ReturnQty);
                var invoiceable = PoOrderCalc.ComputeInvoiceable(d.RecvQty, d.ReturnQty, d.InvoicedQty);
                if (net <= 0m || invoiceable <= 0m)
                {
                    continue;
                }

                if (term.Length > 0
                    && !(po.PoNo.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || (d.ICode?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (d.IDesc?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)))
                {
                    continue;
                }

                rows.Add(new PoInvoicePoLinePickerRow
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    Line = d.Line,
                    ICode = d.ICode,
                    IDesc = d.IDesc,
                    PurchaseUom = d.PurchaseUom,
                    PoUnitPrice = d.PoUnitPrice,
                    OrderedQty = d.PoPurQty,
                    RecvQty = d.RecvQty,
                    ReturnQty = d.ReturnQty,
                    InvoicedQty = d.InvoicedQty,
                    InvoiceableQty = invoiceable,
                    NetReceivedQty = net,
                    OneTime = d.OneTime,
                    TaxGroup = d.TaxGroup,
                    IsInclusive = d.IsInclusive
                });
            }
        }

        return PoInvoiceOperationResult.OkPoLines(rows.Take(100).ToList());
    }

    public async Task<PoInvoiceOperationResult> CopyFromInvoiceAsync(
        string invDocNo,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return PoInvoiceOperationResult.Fail("Branch context is required.", PoInvoiceErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoInvoiceOperationResult.Fail("Not authorized.", PoInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var inv = await _invoices.GetWithDetailsAsync(
            db, scope.CompanyCode, scope.BranchCode, invDocNo, cancellationToken);
        if (inv is null
            || !string.Equals(inv.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(inv.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return PoInvoiceOperationResult.Fail("Posted purchase invoice was not found.", PoInvoiceErrorKind.NotFound);
        }

        var draft = new PoInvoiceDocument
        {
            DocNo = string.Empty,
            DocDate = _dates.Today.Date,
            Status = PoInvoiceStatuses.New,
            Type = PoInvoiceTypes.CreditNote,
            InvNo = inv.DocNo,
            VendorCode = inv.VendorCode,
            VendorName = inv.VendorName,
            Currency = inv.Currency,
            CurrRate = inv.CurrRate,
            PayCode = inv.PayCode,
            TaxGrCode = inv.TaxGrCode,
            LocationCode = inv.LocationCode,
            ProjId = inv.ProjId,
            Dept = inv.Dept,
            PriceTolerance = inv.PriceTolerance,
            InvAddress1 = inv.InvAddress1,
            InvAddress2 = inv.InvAddress2,
            InvAddress3 = inv.InvAddress3,
            InvAddress4 = inv.InvAddress4,
            City = inv.City,
            State = inv.State,
            PostalCode = inv.PostalCode,
            Country = inv.Country,
            Tel = inv.Tel,
            Fax = inv.Fax,
            CanEdit = true,
            Lines = inv.Details.OrderBy(x => x.Line).Select(x => new PoInvoiceLineDto
            {
                Line = x.Line,
                ICode = x.ICode,
                IDesc = x.IDesc,
                Qty = x.Qty,
                UnitPrice = x.UnitPrice,
                SellingUom = x.SellingUom,
                StdUom = x.StdUom,
                StdQty = x.StdQty,
                Amount = x.Amount,
                ItemDiscount = x.ItemDiscount,
                ItemDiscount1 = x.ItemDiscount1,
                IDiscountType = x.IDiscountType,
                IDiscountType1 = x.IDiscountType1,
                IsInclusive = x.IsInclusive,
                TaxGroup = x.TaxGroup,
                TaxAmt = x.TaxAmt,
                NetAmount = x.NetAmount,
                ItemGlCode = x.ItemGlCode,
                Remarks = x.Remarks,
                OneTime = x.OneTime,
                PoNo = x.PoNo,
                PoRelNo = x.PoRelNo,
                PoLineNo = x.PoLineNo
            }).ToList()
        };

        return PoInvoiceOperationResult.OkDocument(draft);
    }

    // ─────────────────────────── Post / Rollback core ───────────────────────────

    private async Task<PoInvoicePostingItemResult> PostOneAsync(
        InventoryTenantScope write,
        PoInvoiceKeyedRequest item,
        CancellationToken cancellationToken)
    {
        var docNo = (item.DocNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, write.CompanyCode, write.BranchCode, docNo, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Document was not found.");
            }

            if (string.Equals(invoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Document is already POSTED.");
            }

            if (!string.Equals(invoice.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Document is not NEW.");
            }

            if (item.RowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(invoice.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user.");
                }

                db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
            }

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            var applyError = await ApplyInvoiceQtyAsync(
                db, write, invoice, sign: +1, cancellationToken);
            if (applyError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, applyError);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(write.UserId, 20);
            invoice.Status = PoInvoiceStatuses.Posted;
            invoice.PostedDate = now;
            invoice.PostedBy = uid;
            invoice.ModifiedDate = now;
            invoice.ModifiedBy = uid;
            TouchRowVersion(db, invoice);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return PoInvoicePostingItemResult.Posted(docNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user.");
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Database conflict. Try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO invoice post failed for {DocNo}", docNo);
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Unable to post the document.");
        }
    }

    private async Task<PoInvoicePostingItemResult> RollbackOneAsync(
        InventoryTenantScope write,
        PoInvoiceKeyedRequest item,
        CancellationToken cancellationToken)
    {
        var docNo = (item.DocNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, write.CompanyCode, write.BranchCode, docNo, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Document was not found.");
            }

            if (string.Equals(invoice.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Document is not POSTED.");
            }

            if (!string.Equals(invoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, "Only POSTED documents can be rolled back.");
            }

            if (string.Equals(invoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase))
            {
                var hasCn = await _invoices.HasPostedCnReferencingInvAsync(
                    db, write.CompanyCode, write.BranchCode, invoice.DocNo, cancellationToken);
                if (hasCn)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoInvoicePostingItemResult.Failed(
                        docNo,
                        "Cannot rollback invoice while posted credit notes still reference it.");
                }

                // C25: a purchase CN/DN pins the invoice for as long as it is NEW or POSTED.
                // Rolling back would strand its reservation basis and its InvLineNo traceability.
                var hasPoCdn = await _cdns.ExistsForInvoiceAsync(
                    db, write.CompanyCode, write.BranchCode, invoice.DocNo, cancellationToken);
                if (hasPoCdn)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoInvoicePostingItemResult.Failed(
                        docNo,
                        "Cannot rollback invoice while a purchase credit/debit note still references it.");
                }
            }

            if (item.RowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(invoice.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user.");
                }

                db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
            }

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            // Rollback reverses the post sign.
            var applyError = await ApplyInvoiceQtyAsync(
                db, write, invoice, sign: -1, cancellationToken);
            if (applyError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoInvoicePostingItemResult.Failed(docNo, applyError);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(write.UserId, 20);
            invoice.Status = PoInvoiceStatuses.New;
            invoice.RollbackDate = now;
            invoice.RollbackBy = uid;
            invoice.ModifiedDate = now;
            invoice.ModifiedBy = uid;
            TouchRowVersion(db, invoice);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return PoInvoicePostingItemResult.RolledBack(docNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Document was changed by another user.");
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Database conflict. Try again.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PO invoice rollback failed for {DocNo}", docNo);
            await tx.RollbackAsync(cancellationToken);
            return PoInvoicePostingItemResult.Failed(docNo, "Unable to rollback the document.");
        }
    }

    /// <summary>
    /// sign +1 = post (INV increases InvoicedQty, CN decreases); sign -1 = rollback (inverse).
    /// </summary>
    private async Task<string?> ApplyInvoiceQtyAsync(
        AppDbContext db,
        InventoryTenantScope write,
        PoInvoice invoice,
        int sign,
        CancellationToken cancellationToken)
    {
        var isCn = string.Equals(invoice.Type, PoInvoiceTypes.CreditNote, StringComparison.OrdinalIgnoreCase);
        var qtySign = isCn ? -sign : sign; // INV post +1; CN post -1; INV rollback -1; CN rollback +1

        var poKeys = invoice.Details
            .Where(d => !string.IsNullOrWhiteSpace(d.PoNo) && d.PoRelNo is not null)
            .Select(d => ((d.PoNo ?? string.Empty).Trim(), d.PoRelNo!.Value))
            .Distinct()
            .OrderBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Item2)
            .ToList();

        if (poKeys.Count == 0)
        {
            return "Document has no PO-linked lines.";
        }

        var orders = new Dictionary<(string PoNo, short Rel), PoOrder>();
        foreach (var key in poKeys)
        {
            var po = await _orders.LockForUpdateAsync(
                db, write.CompanyCode, write.BranchCode, key.Item1, key.Item2, cancellationToken);
            if (po is null)
            {
                return $"Purchase Order {key.Item1}/{key.Item2} was not found.";
            }

            await db.Entry(po).Collection(x => x.Details).LoadAsync(cancellationToken);
            orders[key] = po;
        }

        PoInvoice? referencedInv = null;
        if (isCn)
        {
            var invNo = (invoice.InvNo ?? string.Empty).Trim();
            if (invNo.Length == 0)
            {
                return "Credit note must reference a purchase invoice.";
            }

            referencedInv = await _invoices.GetWithDetailsAsync(
                db, write.CompanyCode, write.BranchCode, invNo, cancellationToken);
            if (referencedInv is null
                || !string.Equals(referencedInv.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(referencedInv.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                return $"Posted purchase invoice {invNo} was not found.";
            }

            if (!string.Equals(referencedInv.VendorCode, invoice.VendorCode, StringComparison.OrdinalIgnoreCase))
            {
                return "Credit note vendor must match the referenced invoice vendor.";
            }
        }

        foreach (var detail in invoice.Details.OrderBy(x => x.PoNo).ThenBy(x => x.PoLineNo))
        {
            var poNo = (detail.PoNo ?? string.Empty).Trim();
            if (poNo.Length == 0 || detail.PoRelNo is null || detail.PoLineNo is null)
            {
                return $"Line {detail.Line}: PO link is required.";
            }

            if (!orders.TryGetValue((poNo, detail.PoRelNo.Value), out var po))
            {
                return $"Purchase Order {poNo}/{detail.PoRelNo} was not found.";
            }

            var poLine = po.Details.FirstOrDefault(x => x.Line == detail.PoLineNo.Value);
            if (poLine is null)
            {
                return $"Purchase Order {poNo} line {detail.PoLineNo} was not found.";
            }

            var qty = PoOrderCalc.RoundQty(detail.Qty);
            if (qty <= 0m)
            {
                return $"Line {detail.Line}: quantity must be greater than zero.";
            }

            // Match validation only on post (sign > 0 for document status NEW→POSTED).
            if (sign > 0)
            {
                var matchError = await ValidateMatchOnPostAsync(
                    db, write, invoice, detail, po, poLine, referencedInv, isCn, qty, cancellationToken);
                if (matchError is not null)
                {
                    return matchError;
                }
            }

            var newInvoiced = PoOrderCalc.RoundQty(poLine.InvoicedQty + (qtySign * qty));
            if (newInvoiced < 0m)
            {
                return $"Line {detail.Line}: invoiced quantity cannot fall below zero.";
            }

            if (sign > 0 && !isCn)
            {
                var net = PoOrderCalc.ComputeNetReceived(poLine.RecvQty, poLine.ReturnQty);
                if (newInvoiced > net)
                {
                    return $"Line {detail.Line}: invoice quantity exceeds net received ({net:n4}).";
                }
            }

            poLine.InvoicedQty = newInvoiced;
            PoOrderCalc.ApplyComputedQtyFields(poLine);
        }

        var now = DateTime.UtcNow;
        var uid = Truncate(write.UserId, 20);
        foreach (var po in orders.Values)
        {
            PoOrderCalc.RecalculateFinClosed(po, po.Details, uid, now);
            po.ModifiedDate = now;
            po.ModifiedBy = uid;
            if (!db.Database.IsSqlServer())
            {
                po.RowVersion = Guid.NewGuid().ToByteArray();
            }
        }

        return null;
    }

    private async Task<string?> ValidateMatchOnPostAsync(
        AppDbContext db,
        InventoryTenantScope write,
        PoInvoice invoice,
        PoInvoiceDetail detail,
        PoOrder po,
        PoOrderDetail poLine,
        PoInvoice? referencedInv,
        bool isCn,
        decimal qty,
        CancellationToken cancellationToken)
    {
        var net = PoOrderCalc.ComputeNetReceived(poLine.RecvQty, poLine.ReturnQty);
        if (!isCn && net <= 0m)
        {
            return $"Line {detail.Line}: cannot invoice before goods receipt (net received is zero).";
        }

        var uom = (detail.SellingUom ?? string.Empty).Trim();
        var poUom = (poLine.PurchaseUom ?? string.Empty).Trim();
        if (!string.Equals(uom, poUom, StringComparison.OrdinalIgnoreCase))
        {
            return $"Line {detail.Line}: invoice UOM must equal PO purchase UOM ({poUom}).";
        }

        if (!isCn)
        {
            var allowed = PoOrderCalc.AllowedInvoicedQty(poLine.RecvQty, poLine.ReturnQty);
            var remaining = PoOrderCalc.RoundQty(allowed - poLine.InvoicedQty);
            if (qty > remaining)
            {
                return $"Line {detail.Line}: quantity {qty:n4} exceeds invoiceable {remaining:n4}.";
            }
        }
        else
        {
            if (qty > poLine.InvoicedQty)
            {
                return $"Line {detail.Line}: credit quantity exceeds invoiced quantity ({poLine.InvoicedQty:n4}).";
            }

            var invLine = referencedInv!.Details.FirstOrDefault(x =>
                string.Equals(x.PoNo, detail.PoNo, StringComparison.OrdinalIgnoreCase)
                && x.PoRelNo == detail.PoRelNo
                && x.PoLineNo == detail.PoLineNo);
            if (invLine is null)
            {
                return $"Line {detail.Line}: referenced invoice has no matching PO line.";
            }

            var cnPosted = await _invoices.SumPostedCnQtyOnInvLineAsync(
                db,
                write.CompanyCode,
                write.BranchCode,
                referencedInv.DocNo,
                detail.PoNo!,
                detail.PoRelNo!.Value,
                detail.PoLineNo!.Value,
                excludeDocNo: invoice.DocNo,
                cancellationToken);
            var remainingOnInv = PoInvoiceCalc.RemainingOnInvLine(invLine.Qty, cnPosted);
            if (qty > remainingOnInv)
            {
                return $"Line {detail.Line}: credit quantity exceeds remaining on invoice {referencedInv.DocNo} ({remainingOnInv:n4}).";
            }
        }

        var vendorTol = await PoToleranceLookup.GetPriceToleranceAsync(db, po, poLine, cancellationToken);
        var effective = PoToleranceLookup.EffectivePriceTolerance(invoice.PriceTolerance, vendorTol);
        if (!PoOrderCalc.ValidatePriceTolerance(
                poLine.PoUnitPrice,
                detail.UnitPrice,
                effective,
                _options.POPriceDecimal,
                out var priceError))
        {
            return $"Line {detail.Line}: {priceError}";
        }

        return null;
    }

    // ─────────────────────────── Prepare / map ───────────────────────────

    private sealed class PrepareOutcome
    {
        public bool Ok { get; init; }
        public string? ErrorMessage { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string? VendorName { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; } = 1m;
        public List<PoInvoiceDetail>? Lines { get; init; }

        public static PrepareOutcome Fail(string message, IReadOnlyDictionary<string, string>? errors = null) =>
            new()
            {
                Ok = false,
                ErrorMessage = message,
                Errors = errors ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };
    }

    private async Task<PrepareOutcome> PrepareAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoInvoiceSaveRequest request,
        string docType,
        string? excludeDocNo,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var vendorCode = (request.VendorCode ?? string.Empty).Trim();
        if (vendorCode.Length == 0)
        {
            errors["VendorCode"] = "Vendor is required.";
        }

        var invNo = (request.InvNo ?? string.Empty).Trim();
        if (docType == PoInvoiceTypes.Invoice && invNo.Length == 0)
        {
            errors["InvNo"] = "Supplier invoice number is required.";
        }

        if (docType == PoInvoiceTypes.CreditNote && invNo.Length == 0)
        {
            errors["InvNo"] = "Referenced purchase invoice is required.";
        }

        if (request.PriceTolerance is not null
            && !PoOrderCalc.ValidateTolerancePercent(request.PriceTolerance.Value, "Price tolerance", out var tolErr))
        {
            errors["PriceTolerance"] = tolErr!;
        }

        var vendor = vendorCode.Length == 0
            ? null
            : await db.PoSuppliers.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode && x.SuppCode == vendorCode && x.IsActive,
                    cancellationToken);
        if (vendorCode.Length > 0 && vendor is null)
        {
            errors["VendorCode"] = "Vendor was not found or is inactive.";
        }

        PoInvoice? referencedInv = null;
        if (docType == PoInvoiceTypes.CreditNote && invNo.Length > 0)
        {
            referencedInv = await _invoices.GetWithDetailsAsync(db, companyCode, branchCode, invNo, cancellationToken);
            if (referencedInv is null
                || !string.Equals(referencedInv.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(referencedInv.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                errors["InvNo"] = "Referenced purchase invoice must be a posted INV.";
            }
            else if (vendorCode.Length > 0
                && !string.Equals(referencedInv.VendorCode, vendorCode, StringComparison.OrdinalIgnoreCase))
            {
                errors["InvNo"] = "Referenced invoice belongs to a different vendor.";
            }
        }

        var sourceLines = request.Lines ?? [];
        if (sourceLines.Count == 0)
        {
            errors["Lines"] = "At least one line is required.";
        }

        // Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
        // A stored orphan may stay untouched; a new or changed code must exist and be active.
        string? priorDept = null;
        string? priorProj = null;
        var editDocNo = (excludeDocNo ?? string.Empty).Trim();
        if (editDocNo.Length > 0)
        {
            var prior = await db.PoInvoices.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.DocNo == editDocNo)
                .Select(x => new { x.Dept, x.ProjId })
                .FirstOrDefaultAsync(cancellationToken);
            if (prior is not null)
            {
                priorDept = prior.Dept;
                priorProj = prior.ProjId;
            }
        }

        var deptError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Department, priorDept, request.Dept, cancellationToken);
        if (deptError is not null)
        {
            errors["Dept"] = deptError;
        }

        var projError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Project, priorProj, request.ProjId, cancellationToken);
        if (projError is not null)
        {
            errors["ProjId"] = projError;
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Fail("Validation failed.", errors);
        }

        var taxPercents = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var currency = string.IsNullOrWhiteSpace(request.Currency)
            ? (vendor!.Currency ?? "MYR")
            : request.Currency.Trim();
        var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
        var currRate = request.CurrRate > 0m
            ? request.CurrRate
            : await ResolveCurrRateAsync(db, companyCode, currency, docDate, cancellationToken);

        var prepared = new List<PoInvoiceDetail>();
        bool? documentInclusive = null;
        short lineNo = 0;
        foreach (var src in sourceLines)
        {
            lineNo++;
            var prefix = $"Lines[{lineNo - 1}]";
            var iCode = (src.ICode ?? string.Empty).Trim();
            if (iCode.Length == 0)
            {
                errors[$"{prefix}.ICode"] = "Item code is required.";
                continue;
            }

            var qty = PoOrderCalc.RoundQty(src.Qty);
            if (qty <= 0m)
            {
                errors[$"{prefix}.Qty"] = "Quantity must be greater than zero.";
            }

            if (src.UnitPrice < 0m)
            {
                errors[$"{prefix}.UnitPrice"] = "Unit price cannot be negative.";
            }

            var poNo = (src.PoNo ?? string.Empty).Trim();
            if (poNo.Length == 0 || src.PoRelNo is null || src.PoLineNo is null)
            {
                errors[$"{prefix}.PoNo"] = "PO link (PoNo / PoRelNo / PoLineNo) is required.";
                continue;
            }

            var po = await db.PoOrders.AsNoTracking()
                .Include(x => x.Details)
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode
                        && x.BranchCode == branchCode
                        && x.PoNo == poNo
                        && x.PoRelNo == src.PoRelNo.Value,
                    cancellationToken);
            if (po is null)
            {
                errors[$"{prefix}.PoNo"] = $"Purchase Order {poNo}/{src.PoRelNo} was not found.";
                continue;
            }

            if (!string.Equals(po.VendCode, vendorCode, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.PoNo"] = "PO vendor must match the invoice vendor.";
            }

            if (string.Equals(po.Status, PoOrderStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.PoNo"] = "Cannot invoice a cancelled purchase order.";
            }

            var poLine = po.Details.FirstOrDefault(x => x.Line == src.PoLineNo.Value);
            if (poLine is null)
            {
                errors[$"{prefix}.PoLineNo"] = $"PO line {src.PoLineNo} was not found.";
                continue;
            }

            if (!string.Equals(poLine.ICode, iCode, StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.ICode"] = "Item code must match the PO line.";
            }

            var uom = string.IsNullOrWhiteSpace(src.SellingUom)
                ? (poLine.PurchaseUom ?? string.Empty).Trim()
                : src.SellingUom.Trim();
            if (!string.Equals(uom, (poLine.PurchaseUom ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                errors[$"{prefix}.SellingUom"] = "Invoice UOM must equal PO purchase UOM.";
            }

            // Soft draft checks — authoritative match runs again under lock on post.
            var net = PoOrderCalc.ComputeNetReceived(poLine.RecvQty, poLine.ReturnQty);
            if (docType == PoInvoiceTypes.Invoice)
            {
                if (net <= 0m)
                {
                    errors[$"{prefix}.Qty"] = "Cannot invoice before goods receipt.";
                }
                else
                {
                    var remaining = PoOrderCalc.ComputeInvoiceable(poLine.RecvQty, poLine.ReturnQty, poLine.InvoicedQty);
                    if (qty > remaining)
                    {
                        errors[$"{prefix}.Qty"] = $"Quantity exceeds invoiceable {remaining:n4}.";
                    }
                }
            }
            else if (referencedInv is not null)
            {
                var invLine = referencedInv.Details.FirstOrDefault(x =>
                    string.Equals(x.PoNo, poNo, StringComparison.OrdinalIgnoreCase)
                    && x.PoRelNo == src.PoRelNo
                    && x.PoLineNo == src.PoLineNo);
                if (invLine is null)
                {
                    errors[$"{prefix}.PoLineNo"] = "Referenced invoice has no matching PO line.";
                }
                else
                {
                    var cnPosted = await _invoices.SumPostedCnQtyOnInvLineAsync(
                        db, companyCode, branchCode, referencedInv.DocNo,
                        poNo, src.PoRelNo.Value, src.PoLineNo.Value, excludeDocNo, cancellationToken);
                    var remainingOnInv = PoInvoiceCalc.RemainingOnInvLine(invLine.Qty, cnPosted);
                    if (qty > remainingOnInv)
                    {
                        errors[$"{prefix}.Qty"] = $"Quantity exceeds remaining on invoice ({remainingOnInv:n4}).";
                    }
                }
            }

            var unitPrice = PoOrderCalc.RoundPrice(src.UnitPrice, _options.POPriceDecimal);
            var vendorTol = await PoToleranceLookup.GetPriceToleranceAsync(db, po, poLine, cancellationToken);
            var effective = PoToleranceLookup.EffectivePriceTolerance(request.PriceTolerance, vendorTol);
            if (!PoOrderCalc.ValidatePriceTolerance(
                    poLine.PoUnitPrice, unitPrice, effective, _options.POPriceDecimal, out var priceError))
            {
                errors[$"{prefix}.UnitPrice"] = priceError!;
            }

            documentInclusive ??= src.IsInclusive;
            if (documentInclusive != src.IsInclusive)
            {
                errors[$"{prefix}.IsInclusive"] = "All lines must use the same tax inclusive setting.";
            }

            var taxGroup = string.IsNullOrWhiteSpace(src.TaxGroup) ? poLine.TaxGroup : src.TaxGroup.Trim();
            var taxPercent = 0m;
            if (!string.IsNullOrWhiteSpace(taxGroup))
            {
                if (!taxPercents.TryGetValue(taxGroup, out taxPercent))
                {
                    errors[$"{prefix}.TaxGroup"] = "Tax group was not found.";
                }
            }

            var gl = TruncateOptional(src.ItemGlCode, 20);
            var (netAmt, taxAmt, amount) = PoInvoiceCalc.ComputeLineAmounts(
                qty,
                unitPrice,
                src.ItemDiscount,
                src.IDiscountType,
                src.ItemDiscount1,
                src.IDiscountType1,
                taxPercent,
                src.IsInclusive,
                _options.PurchaseTaxDec);

            if (netAmt != 0m && string.IsNullOrWhiteSpace(gl))
            {
                // Commercial readiness: GL required when amount nonzero (validation only; no AP journal).
                var stock = await db.IvStockMasters.AsNoTracking()
                    .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.ICode == iCode, cancellationToken);
                gl = TruncateOptional(stock?.PurchaseGlCode, 20);
                if (string.IsNullOrWhiteSpace(gl))
                {
                    errors[$"{prefix}.ItemGlCode"] = "Item GL code is required when line amount is non-zero.";
                }
            }

            prepared.Add(new PoInvoiceDetail
            {
                Line = lineNo,
                ICode = iCode,
                IDesc = TruncateOptional(src.IDesc ?? poLine.IDesc, 200),
                Qty = qty,
                UnitPrice = unitPrice,
                SellingUom = TruncateOptional(uom, 10),
                StdUom = TruncateOptional(poLine.StdUom, 10),
                StdQty = PoOrderCalc.ComputeStdQty(qty, poLine.PackSz),
                StdCustPsize = poLine.PackSz,
                Amount = amount,
                ItemDiscount = src.ItemDiscount,
                ItemDiscount1 = src.ItemDiscount1,
                IDiscountType = TruncateOptional(src.IDiscountType, 20),
                IDiscountType1 = TruncateOptional(src.IDiscountType1, 20),
                IsInclusive = src.IsInclusive,
                TaxGroup = TruncateOptional(taxGroup, 20),
                TaxAmt = taxAmt,
                NetAmount = netAmt,
                ItemGlCode = gl,
                Remarks = TruncateOptional(src.Remarks, 250),
                OneTime = src.OneTime ?? poLine.OneTime,
                PoNo = poNo,
                PoRelNo = src.PoRelNo,
                PoLineNo = src.PoLineNo
            });
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Fail(ValidationMessageFormat.JoinMessages(errors), errors);
        }

        return new PrepareOutcome
        {
            Ok = true,
            VendorName = vendor!.SuppName,
            Currency = currency,
            CurrRate = currRate,
            Lines = prepared
        };
    }

    private PoInvoice BuildHeader(
        string company,
        string branch,
        DocumentNumberResult issued,
        string docType,
        DateTime docDate,
        PoInvoiceSaveRequest request,
        PrepareOutcome prepared,
        DateTime now,
        string? uid)
    {
        var invoice = new PoInvoice
        {
            CompanyCode = company,
            BranchCode = branch,
            DocNo = issued.DocumentNumber,
            DocDate = docDate,
            Status = PoInvoiceStatuses.New,
            Type = docType,
            Prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim(),
            CreatedDate = now,
            CreatedBy = uid
        };
        ApplyHeaderFields(invoice, request, prepared);
        return invoice;
    }

    private static void ApplyHeaderFields(PoInvoice invoice, PoInvoiceSaveRequest request, PrepareOutcome prepared)
    {
        invoice.VendorCode = (request.VendorCode ?? string.Empty).Trim();
        invoice.VendorName = TruncateOptional(prepared.VendorName, 200);
        invoice.InvNo = TruncateOptional(request.InvNo, 30);
        invoice.Currency = TruncateOptional(prepared.Currency, 20);
        invoice.CurrRate = prepared.CurrRate <= 0m ? 1m : prepared.CurrRate;
        invoice.PayCode = TruncateOptional(request.PayCode, 20);
        invoice.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        invoice.LocationCode = TruncateOptional(request.LocationCode, 10);
        invoice.ProjId = TruncateOptional(request.ProjId, 20);
        invoice.Dept = TruncateOptional(request.Dept, 20);
        invoice.Remarks = TruncateOptional(request.Remarks, 500);
        invoice.RefNo = TruncateOptional(request.RefNo, 50);
        invoice.ExternalDocNo = TruncateOptional(request.ExternalDocNo, 50);
        invoice.PriceTolerance = request.PriceTolerance;
        invoice.InvAddress1 = TruncateOptional(request.InvAddress1, 100);
        invoice.InvAddress2 = TruncateOptional(request.InvAddress2, 100);
        invoice.InvAddress3 = TruncateOptional(request.InvAddress3, 100);
        invoice.InvAddress4 = TruncateOptional(request.InvAddress4, 100);
        invoice.City = TruncateOptional(request.City, 50);
        invoice.State = TruncateOptional(request.State, 50);
        invoice.PostalCode = TruncateOptional(request.PostalCode, 20);
        invoice.Country = TruncateOptional(request.Country, 50);
        invoice.Tel = TruncateOptional(request.Tel, 50);
        invoice.Fax = TruncateOptional(request.Fax, 50);
        invoice.DocDate = request.DocDate == default ? invoice.DocDate : request.DocDate.Date;
    }

    private static void ApplyDetails(PoInvoice invoice, IReadOnlyList<PoInvoiceDetail> lines)
    {
        foreach (var line in lines)
        {
            line.CompanyCode = invoice.CompanyCode;
            line.BranchCode = invoice.BranchCode;
            line.DocNo = invoice.DocNo;
            invoice.Details.Add(line);
        }
    }

    private async Task<PoInvoiceDocument> MapDocumentAsync(PoInvoice entity, CancellationToken cancellationToken)
    {
        var isNew = string.Equals(entity.Status, PoInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase);
        var isPosted = string.Equals(entity.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase);
        var canEdit = await CanAsync(PermissionCodes.Edit, cancellationToken);
        var canDelete = await CanAsync(PermissionCodes.Delete, cancellationToken);
        var canPost = await CanAsync(PermissionCodes.Post, cancellationToken);
        var canRollback = await CanAsync(PermissionCodes.Rollback, cancellationToken);

        return new PoInvoiceDocument
        {
            DocNo = entity.DocNo,
            DocDate = entity.DocDate,
            Status = entity.Status,
            Type = entity.Type,
            InvNo = entity.InvNo,
            VendorCode = entity.VendorCode,
            VendorName = entity.VendorName,
            Prefix = entity.Prefix,
            Currency = entity.Currency,
            CurrRate = entity.CurrRate,
            PayCode = entity.PayCode,
            TaxGrCode = entity.TaxGrCode,
            LocationCode = entity.LocationCode,
            ProjId = entity.ProjId,
            Dept = entity.Dept,
            Remarks = entity.Remarks,
            RefNo = entity.RefNo,
            ExternalDocNo = entity.ExternalDocNo,
            PriceTolerance = entity.PriceTolerance,
            InvAddress1 = entity.InvAddress1,
            InvAddress2 = entity.InvAddress2,
            InvAddress3 = entity.InvAddress3,
            InvAddress4 = entity.InvAddress4,
            City = entity.City,
            State = entity.State,
            PostalCode = entity.PostalCode,
            Country = entity.Country,
            Tel = entity.Tel,
            Fax = entity.Fax,
            GrossAmnt = entity.GrossAmnt,
            Taxes = entity.Taxes,
            TotAmnt = entity.TotAmnt,
            PostedDate = entity.PostedDate,
            PostedBy = entity.PostedBy,
            RowVersion = entity.RowVersion,
            CanEdit = canEdit && isNew,
            CanDelete = canDelete && isNew,
            CanPost = canPost && isNew,
            CanRollback = canRollback && isPosted,
            Lines = entity.Details.OrderBy(x => x.Line).Select(x => new PoInvoiceLineDto
            {
                Line = x.Line,
                ICode = x.ICode,
                IDesc = x.IDesc,
                Qty = x.Qty,
                UnitPrice = x.UnitPrice,
                SellingUom = x.SellingUom,
                StdUom = x.StdUom,
                StdQty = x.StdQty,
                Amount = x.Amount,
                ItemDiscount = x.ItemDiscount,
                ItemDiscount1 = x.ItemDiscount1,
                IDiscountType = x.IDiscountType,
                IDiscountType1 = x.IDiscountType1,
                IsInclusive = x.IsInclusive,
                TaxGroup = x.TaxGroup,
                TaxAmt = x.TaxAmt,
                NetAmount = x.NetAmount,
                ItemGlCode = x.ItemGlCode,
                Remarks = x.Remarks,
                OneTime = x.OneTime,
                PoNo = x.PoNo,
                PoRelNo = x.PoRelNo,
                PoLineNo = x.PoLineNo
            }).ToList()
        };
    }

    private async Task<decimal> ResolveCurrRateAsync(
        AppDbContext db,
        string companyCode,
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken)
    {
        _ = companyCode;
        var code = (currency ?? string.Empty).Trim();
        if (code.Length == 0 || code.Equals("MYR", StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        var date = docDate.Date;
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == code && x.Status && x.StartDate <= date && x.EndDate >= date)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);
        return rate is null or <= 0d ? 1m : (decimal)rate.Value;
    }

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.PurchaseInvoice, permission, cancellationToken);

    private static void TouchRowVersion(AppDbContext db, PoInvoice header)
    {
        if (!db.Database.IsSqlServer())
        {
            header.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right)
    {
        if (left is null || right is null || left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
               || message.Contains("2627", StringComparison.Ordinal)
               || message.Contains("2601", StringComparison.Ordinal);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? TruncateOptional(string? value, int max) => Truncate(value, max);
}
