using ErpWeb.Core.EInvoice;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Self-billed purchase invoice (LHDN e-Invoice type 11).
/// <para>
/// A slim purchase document: header + quantity-bearing lines, a NEW/POSTED lifecycle, and the same
/// server-side e-Invoice edit lock the sales documents enforce. It has no PO/GR lineage, no stock or
/// accounting effect, and it never stores a vendor snapshot — the e-Invoice payload reads the vendor
/// master live at submit time.
/// </para>
/// </summary>
public sealed class PoSbInvoiceService : IPoSbInvoiceService
{
    private const int MaxDocNoLength = 30;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ILogger<PoSbInvoiceService> _logger;

    public PoSbInvoiceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ILogger<PoSbInvoiceService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _logger = logger;
    }

    public Task<bool> CanAsync(string permissionCode, CancellationToken cancellationToken = default) =>
        _accessRights.CanAsync(MenuCodes.PurchaseSbInvoice, permissionCode, cancellationToken);

    private sealed record WriteContext(
        string CompanyCode, string BranchCode, string? LocationCode, string UserId);

    private (WriteContext? Context, string? Error) ResolveWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return (null, "Invalid company or branch context.");
        }

        return (new WriteContext(scope.CompanyCode, scope.BranchCode!, scope.LocationCode, scope.UserId), null);
    }

    // ── Lookups / defaults ─────────────────────────────────────────────────────

    public async Task<PoSbInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return PoSbInvoiceOperationResult.Fail("Invalid company context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return PoSbInvoiceOperationResult.OkLookups(
            await PoSbLookupsLoader.LoadAsync(db, scope.CompanyCode, cancellationToken));
    }

    public async Task<PoSbInvoiceOperationResult> GetVendorDefaultsAsync(
        string vendorCode, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return PoSbInvoiceOperationResult.Fail("Invalid company context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var defaults = await PoSbLookupsLoader.LoadVendorDefaultsAsync(
            db, scope.CompanyCode, vendorCode, cancellationToken);

        return defaults is null
            ? PoSbInvoiceOperationResult.Fail("Vendor was not found.", PoSbErrorKind.NotFound)
            : PoSbInvoiceOperationResult.OkVendorDefaults(defaults);
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    public async Task<PoSbInvoiceOperationResult> SearchAsync(
        PoSbQuery query, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return PoSbInvoiceOperationResult.Fail("Invalid company or branch context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        var company = scope.CompanyCode;
        var branch = scope.BranchCode!;
        var (skip, take) = query.NormalizedPaging();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // The filter set lives in ONE place (PoSbQueryApplier) because the no-selection E-STATUS
        // refresh-all resolves its candidate set through the same applier: "what the grid shows" and
        // "what gets refreshed" must not drift apart.
        var q = PoSbQueryApplier.Apply(db.PoSbInvoices.AsNoTracking(), query, company, branch);

        var totalCount = await q.CountAsync(cancellationToken);

        var raw = await q.Skip(skip).Take(take)
            .Select(x => new
            {
                x.DocNo,
                x.DocDate,
                x.Status,
                x.VendorCode,
                x.VendorName,
                x.Currency,
                x.TotAmnt,
                x.IrbmStatus,
                x.IrbmUuid,
                x.CreatedDate,
                x.CreatedBy,
                x.ModifiedDate,
                x.ModifiedBy,
                x.RowVersion,
                LineCount = x.Details.Count
            })
            .ToListAsync(cancellationToken);

        var rows = raw.Select(x =>
        {
            var locked = PoSbCalc.IsEInvoiceLocked(x.IrbmStatus);

            return new PoSbInvoiceListRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Status = x.Status,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                Currency = x.Currency,
                TotAmnt = x.TotAmnt,
                LineCount = x.LineCount,
                IrbmStatus = x.IrbmStatus,
                IrbmUuid = x.IrbmUuid,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy,
                ModifiedDate = x.ModifiedDate,
                ModifiedBy = x.ModifiedBy,
                RowVersion = x.RowVersion,
                // The e-Invoice state is the only structural gate (see UpdateAsync).
                CanEdit = !locked,
                CanDelete = !locked
            };
        }).ToList();

        return PoSbInvoiceOperationResult.OkList(
            new PoSbPage<PoSbInvoiceListRow> { Rows = rows, TotalCount = totalCount });
    }

    public async Task<PoSbInvoiceOperationResult> GetAsync(
        string docNo, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return PoSbInvoiceOperationResult.Fail("Invalid company or branch context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        var wanted = PoSbCalc.NullIfBlank(docNo);
        if (wanted is null)
        {
            return PoSbInvoiceOperationResult.Fail("Document number is required.", PoSbErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await db.PoSbInvoices.AsNoTracking()
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode
                     && x.BranchCode == scope.BranchCode
                     && x.DocNo == wanted,
                cancellationToken);

        return header is null
            ? PoSbInvoiceOperationResult.Fail(
                $"Self-billed invoice {wanted} was not found.", PoSbErrorKind.NotFound)
            : PoSbInvoiceOperationResult.OkDocument(ToDocument(header));
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public async Task<PoSbInvoiceOperationResult> SaveNewAsync(
        PoSbInvoiceSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSbInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbInvoiceOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var taxPercents = await PoSbLookupsLoader.LoadTaxPercentsAsync(
                db, context!.CompanyCode, cancellationToken);

            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PoSbCalc.ValidateLines(request.Lines, taxPercents, errors);

            var vendor = await LoadVendorAsync(db, context.CompanyCode, request.VendorCode, errors, cancellationToken);
            if (errors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.FailValidation("Validation failed.", errors);
            }

            var issued = await _documentNumbers.NextAsync(
                db,
                PoSbLimits.InvoiceNumberingModule,
                string.Empty,
                docDate,
                DocumentNumberRequestMode.New,
                "AUTO",
                cancellationToken);

            var now = DateTime.UtcNow;
            var uid = PoSbCalc.Truncate(context.UserId, 20);

            var header = new PoSbInvoice
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                DocNo = PoSbCalc.Truncate(issued.DocumentNumber, MaxDocNoLength)!,
                DocDate = docDate,
                Status = PoSbStatuses.New,
                Prefix = PoSbCalc.Truncate(issued.PrefixUsed, 20),
                LocationCode = context.LocationCode,
                CreatedDate = now,
                CreatedBy = uid
            };

            ApplyHeader(header, request, vendor!);

            short lineNo = 1;
            foreach (var line in request.Lines)
            {
                header.Details.Add(PoSbCalc.BuildInvoiceDetail(
                    lineNo++, line, PoSbCalc.ResolveTaxPercent(line.TaxGroup, taxPercents)));
            }

            PoSbCalc.ApplyHeaderTotals(header, header.Details);

            db.PoSbInvoices.Add(header);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PoSbInvoiceOperationResult.OkSaved(header.DocNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                "This document was changed by another user. Reload before saving.", PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed invoice save failed for vendor {Vendor}", request.VendorCode);
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                "Unable to save the self-billed invoice.", PoSbErrorKind.Unexpected);
        }
    }

    public async Task<PoSbInvoiceOperationResult> UpdateAsync(
        string docNo, PoSbInvoiceSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSbInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbInvoiceOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        var wanted = PoSbCalc.NullIfBlank(docNo);
        if (wanted is null)
        {
            return PoSbInvoiceOperationResult.Fail("Document number is required.", PoSbErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var header = await db.PoSbInvoices
                .Include(x => x.Details)
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == context!.CompanyCode
                         && x.BranchCode == context.BranchCode
                         && x.DocNo == wanted,
                    cancellationToken);

            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.Fail(
                    $"Self-billed invoice {wanted} was not found.", PoSbErrorKind.NotFound);
            }

            // The e-Invoice state is the ONLY structural gate on a self-billed document. The ERP
            // NEW/POSTED dimension is retired (nothing writes POSTED any more), so a document stays
            // editable while it has not been submitted and while it is in a fix-and-resubmit state
            // (INVALID / REJECTED / CANCELLED / FAILED). Only SUBMITTING / SUBMITTED / VALID lock it.
            if (PoSbCalc.IsEInvoiceLocked(header.IrbmStatus))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.Fail(
                    $"This document cannot be edited while its e-Invoice status is {EInvoiceStatuses.Normalize(header.IrbmStatus)}.",
                    PoSbErrorKind.BusinessRule);
            }

            if (request.RowVersion is { Length: > 0 })
            {
                db.Entry(header).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            }

            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var taxPercents = await PoSbLookupsLoader.LoadTaxPercentsAsync(
                db, context.CompanyCode, cancellationToken);

            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PoSbCalc.ValidateLines(request.Lines, taxPercents, errors);

            var vendor = await LoadVendorAsync(db, context.CompanyCode, request.VendorCode, errors, cancellationToken);
            if (errors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbInvoiceOperationResult.FailValidation("Validation failed.", errors);
            }

            header.DocDate = docDate;
            ApplyHeader(header, request, vendor!);
            header.ModifiedDate = DateTime.UtcNow;
            header.ModifiedBy = PoSbCalc.Truncate(context.UserId, 20);

            // Full replace: the line set is small and a partial merge would leave orphaned totals.
            header.Details.Clear();
            short lineNo = 1;
            foreach (var line in request.Lines)
            {
                header.Details.Add(PoSbCalc.BuildInvoiceDetail(
                    lineNo++, line, PoSbCalc.ResolveTaxPercent(line.TaxGroup, taxPercents)));
            }

            PoSbCalc.ApplyHeaderTotals(header, header.Details);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PoSbInvoiceOperationResult.OkSaved(header.DocNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                "This document was changed by another user. Reload before saving.", PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed invoice update failed for {DocNo}", docNo);
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                "Unable to save the self-billed invoice.", PoSbErrorKind.Unexpected);
        }
    }

    public Task<PoSbInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<PoSbKeyedRequest> items, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            items,
            PermissionCodes.Delete,
            "deleted",
            static header =>
                // The e-Invoice state is the only structural gate: the same predicate as Edit. A legacy
                // POSTED row is therefore deletable — the ERP status carries no accounting meaning here.
                PoSbCalc.IsEInvoiceLocked(header.IrbmStatus)
                    ? $"Document {header.DocNo} cannot be deleted while its e-Invoice status is {EInvoiceStatuses.Normalize(header.IrbmStatus)}."
                    : null,
            static (db, header) => db.PoSbInvoices.Remove(header),
            cancellationToken);

    /// <summary>
    /// The ONE driver for the batch actions that remain (delete): authorizes, validates EVERY selected
    /// document before changing any of them (so a refused item cannot leave a half-applied batch), then
    /// commits once.
    /// </summary>
    private async Task<PoSbInvoiceOperationResult> TransitionAsync(
        IReadOnlyList<PoSbKeyedRequest> items,
        string permission,
        string actionPastTense,
        Func<PoSbInvoice, string?> validate,
        Action<AppDbContext, PoSbInvoice> apply,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0)
        {
            return PoSbInvoiceOperationResult.Fail("No record selected.");
        }

        if (items.Count > PoSbLimits.MaxPostSelection)
        {
            return PoSbInvoiceOperationResult.Fail(
                $"Select at most {PoSbLimits.MaxPostSelection} documents at a time.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbInvoiceOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        if (!await CanAsync(permission, cancellationToken))
        {
            return PoSbInvoiceOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var headers = new List<PoSbInvoice>(items.Count);

            foreach (var item in items)
            {
                var wanted = PoSbCalc.NullIfBlank(item.DocNo);
                if (wanted is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbInvoiceOperationResult.Fail("Document number is required.");
                }

                var header = await db.PoSbInvoices.FirstOrDefaultAsync(
                    x => x.CompanyCode == context!.CompanyCode
                         && x.BranchCode == context.BranchCode
                         && x.DocNo == wanted,
                    cancellationToken);

                if (header is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbInvoiceOperationResult.Fail(
                        $"Self-billed invoice {wanted} was not found.", PoSbErrorKind.NotFound);
                }

                var refusal = validate(header);
                if (refusal is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbInvoiceOperationResult.Fail(refusal, PoSbErrorKind.BusinessRule);
                }

                if (item.RowVersion is { Length: > 0 })
                {
                    db.Entry(header).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                }

                headers.Add(header);
            }

            var uid = PoSbCalc.Truncate(context.UserId, 20);
            foreach (var header in headers)
            {
                apply(db, header);
                header.ModifiedDate = DateTime.UtcNow;
                header.ModifiedBy = uid;
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PoSbInvoiceOperationResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                "One of the documents was changed by another user. Reload before retrying.",
                PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed invoice {Action} failed", actionPastTense);
            await tx.RollbackAsync(cancellationToken);
            return PoSbInvoiceOperationResult.Fail(
                $"Unable to complete the {actionPastTense} action.", PoSbErrorKind.Unexpected);
        }
    }

    // ── Mapping helpers ────────────────────────────────────────────────────────

    private static async Task<PoSupplier?> LoadVendorAsync(
        AppDbContext db,
        string companyCode,
        string? vendorCode,
        IDictionary<string, string> errors,
        CancellationToken cancellationToken)
    {
        var code = PoSbCalc.NullIfBlank(vendorCode);
        if (code is null)
        {
            errors["VendorCode"] = "Vendor is required.";
            return null;
        }

        var vendor = await db.PoSuppliers.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.SuppCode == code, cancellationToken);

        if (vendor is null)
        {
            errors["VendorCode"] = $"Vendor '{code}' was not found.";
        }
        else if (!vendor.IsActive)
        {
            errors["VendorCode"] = $"Vendor '{code}' is inactive.";
        }

        return vendor;
    }

    private static void ApplyHeader(PoSbInvoice header, PoSbInvoiceSaveRequest request, PoSupplier vendor)
    {
        header.VendorCode = PoSbCalc.Truncate(vendor.SuppCode, 60)!;
        header.VendorName = PoSbCalc.Truncate(request.VendorName ?? vendor.SuppName, 200);
        header.Currency = PoSbCalc.Truncate(request.Currency ?? vendor.Currency, 20);
        header.CurrRate = request.CurrRate <= 0m ? 1m : request.CurrRate;
        header.TaxGrCode = PoSbCalc.Truncate(request.TaxGrCode ?? vendor.TaxGroup ?? vendor.TaxGrCode, 20);
        header.Remarks = PoSbCalc.Truncate(request.Remarks, 500);
    }

    private static PoSbInvoiceDocument ToDocument(PoSbInvoice header) => new()
    {
        CompanyCode = header.CompanyCode,
        BranchCode = header.BranchCode,
        DocNo = header.DocNo,
        DocDate = header.DocDate,
        Status = header.Status,
        Prefix = header.Prefix,
        VendorCode = header.VendorCode,
        VendorName = header.VendorName,
        Currency = header.Currency,
        CurrRate = header.CurrRate,
        TaxGrCode = header.TaxGrCode,
        Remarks = header.Remarks,
        GrossAmnt = header.GrossAmnt,
        Taxes = header.Taxes,
        TotAmnt = header.TotAmnt,
        IrbmStatus = header.IrbmStatus,
        IrbmUuid = header.IrbmUuid,
        IrbmError = header.IrbmError,
        RowVersion = header.RowVersion,
        IsEInvoiceLocked = PoSbCalc.IsEInvoiceLocked(header.IrbmStatus),
        // The e-Invoice state is the only structural gate (see UpdateAsync).
        CanEdit = !PoSbCalc.IsEInvoiceLocked(header.IrbmStatus),
        CanDelete = !PoSbCalc.IsEInvoiceLocked(header.IrbmStatus),
        Lines = header.Details
            .OrderBy(x => x.Line)
            .Select(ToLineDto)
            .ToList()
    };

    private static PoSbLineDto ToLineDto(PoSbInvoiceDetail line) => new()
    {
        Line = line.Line,
        ICode = line.ICode,
        IDesc = line.IDesc,
        Qty = line.Qty,
        UnitPrice = line.UnitPrice,
        SellingUom = line.SellingUom,
        StdUom = line.StdUom,
        Amount = line.Amount,
        TaxAmt = line.TaxAmt,
        NetAmount = line.NetAmount,
        TaxGroup = line.TaxGroup,
        IsInclusive = line.IsInclusive,
        Discount = line.Discount,
        ItemDiscount = line.ItemDiscount,
        ItemDiscount1 = line.ItemDiscount1,
        IDiscountType = line.IDiscountType,
        IDiscountType1 = line.IDiscountType1,
        Classification = line.Classification,
        Remarks = line.Remarks
    };
}
