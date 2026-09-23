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
/// Self-billed purchase credit / debit note (LHDN e-Invoice types 12 / 13).
/// <para>
/// A note always references an originating self-billed invoice and must keep its vendor and currency
/// consistent with it. Save-time requires the origin to exist in the same company and branch; the
/// stricter "the origin must already be VALID at MyInvois with a UUID" rule is enforced when the payload
/// is built, so a note can still be drafted while its invoice is being submitted.
/// </para>
/// </summary>
public sealed class PoSbCdnService : IPoSbCdnService
{
    private const int MaxDocNoLength = 30;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ILogger<PoSbCdnService> _logger;

    public PoSbCdnService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ILogger<PoSbCdnService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _logger = logger;
    }

    /// <summary>CN and DN are separate menus, so the type chooses what is checked.</summary>
    private static string MenuCodeFor(string? type) =>
        PoSbCalc.NormalizeType(type) == PoSbTypes.DebitNote
            ? MenuCodes.PurchaseSbDebitNote
            : MenuCodes.PurchaseSbCreditNote;

    public Task<bool> CanAsync(
        string type, string permissionCode, CancellationToken cancellationToken = default) =>
        _accessRights.CanAsync(MenuCodeFor(type), permissionCode, cancellationToken);

    /// <summary>
    /// Used where the document type is not yet known (a look-up by number). Holding EITHER note's right
    /// is enough to read; the per-document type is re-checked as soon as the row is loaded.
    /// </summary>
    private async Task<bool> CanAnyAsync(string permissionCode, CancellationToken cancellationToken) =>
        await _accessRights.CanAsync(MenuCodes.PurchaseSbCreditNote, permissionCode, cancellationToken)
        || await _accessRights.CanAsync(MenuCodes.PurchaseSbDebitNote, permissionCode, cancellationToken);

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

    public async Task<PoSbCdnOperationResult> GetLookupsAsync(
        string type, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return PoSbCdnOperationResult.Fail("Invalid company context.", PoSbErrorKind.Authorization);
        }

        var docType = PoSbCalc.NormalizeType(type);
        if (!PoSbCalc.IsValidType(docType))
        {
            return PoSbCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(docType, PermissionCodes.Access, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return PoSbCdnOperationResult.OkLookups(
            await PoSbLookupsLoader.LoadAsync(db, scope.CompanyCode, cancellationToken));
    }

    public async Task<PoSbCdnOperationResult> GetVendorDefaultsAsync(
        string vendorCode, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return PoSbCdnOperationResult.Fail("Invalid company context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAnyAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var defaults = await PoSbLookupsLoader.LoadVendorDefaultsAsync(
            db, scope.CompanyCode, vendorCode, cancellationToken);

        return defaults is null
            ? PoSbCdnOperationResult.Fail("Vendor was not found.", PoSbErrorKind.NotFound)
            : PoSbCdnOperationResult.OkVendorDefaults(defaults);
    }

    // ── Read ───────────────────────────────────────────────────────────────────

    public async Task<PoSbCdnOperationResult> SearchAsync(
        PoSbQuery query, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return PoSbCdnOperationResult.Fail("Invalid company or branch context.", PoSbErrorKind.Authorization);
        }

        var type = PoSbCalc.NormalizeType(query.Type);
        if (!PoSbCalc.IsValidType(type))
        {
            return PoSbCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(type, PermissionCodes.Access, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        var company = scope.CompanyCode;
        var branch = scope.BranchCode!;
        var (skip, take) = query.NormalizedPaging();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // ONE filter definition (PoSbQueryApplier), shared with the no-selection E-STATUS refresh-all in
        // SaEInvoiceService: "what the grid shows" and "what gets refreshed" must not drift apart.
        var q = PoSbQueryApplier.Apply(db.PoSbCdns.AsNoTracking(), query, company, branch, type);

        var totalCount = await q.CountAsync(cancellationToken);

        var raw = await q.Skip(skip).Take(take)
            .Select(x => new
            {
                x.DocNo,
                x.DocDate,
                x.Status,
                x.Type,
                x.VendorCode,
                x.VendorName,
                x.OriginSbInvNo,
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

            return new PoSbCdnListRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Status = x.Status,
                Type = x.Type,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                OriginSbInvNo = x.OriginSbInvNo,
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

        return PoSbCdnOperationResult.OkList(
            new PoSbPage<PoSbCdnListRow> { Rows = rows, TotalCount = totalCount });
    }

    public async Task<PoSbCdnOperationResult> GetAsync(
        string docNo, CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null || string.IsNullOrWhiteSpace(scope.BranchCode))
        {
            return PoSbCdnOperationResult.Fail("Invalid company or branch context.", PoSbErrorKind.Authorization);
        }

        if (!await CanAnyAsync(PermissionCodes.Access, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        var wanted = PoSbCalc.NullIfBlank(docNo);
        if (wanted is null)
        {
            return PoSbCdnOperationResult.Fail("Document number is required.", PoSbErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var header = await db.PoSbCdns.AsNoTracking()
            .Include(x => x.Details)
            .FirstOrDefaultAsync(
                x => x.CompanyCode == scope.CompanyCode
                     && x.BranchCode == scope.BranchCode
                     && x.DocNo == wanted,
                cancellationToken);

        if (header is null)
        {
            return PoSbCdnOperationResult.Fail(
                $"Self-billed note {wanted} was not found.", PoSbErrorKind.NotFound);
        }

        // Now that the type is known, re-authorize against that type's menu.
        if (!await CanAsync(header.Type, PermissionCodes.Access, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        return PoSbCdnOperationResult.OkDocument(ToDocument(header));
    }

    // ── Write ──────────────────────────────────────────────────────────────────

    public async Task<PoSbCdnOperationResult> SaveNewAsync(
        PoSbCdnSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSbCdnOperationResult.FailValidation("Save request is required.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbCdnOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        var docType = PoSbCalc.NormalizeType(request.Type);
        if (!PoSbCalc.IsValidType(docType))
        {
            return PoSbCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(docType, PermissionCodes.Add, cancellationToken))
        {
            return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var taxPercents = await PoSbLookupsLoader.LoadTaxPercentsAsync(
                db, context!.CompanyCode, cancellationToken);

            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PoSbCalc.ValidateLines(request.Lines, taxPercents, errors);

            var vendor = await LoadVendorAsync(db, context.CompanyCode, request.VendorCode, errors, cancellationToken);
            var origin = await LoadOriginAsync(
                db, context.CompanyCode, context.BranchCode, request, vendor, errors, cancellationToken);

            if (errors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.FailValidation("Validation failed.", errors);
            }

            var issued = await _documentNumbers.NextAsync(
                db,
                PoSbCalc.NumberingModuleFor(docType),
                string.Empty,
                docDate,
                DocumentNumberRequestMode.New,
                "AUTO",
                cancellationToken);

            var now = DateTime.UtcNow;
            var uid = PoSbCalc.Truncate(context.UserId, 20);

            var header = new PoSbCdn
            {
                CompanyCode = context.CompanyCode,
                BranchCode = context.BranchCode,
                DocNo = PoSbCalc.Truncate(issued.DocumentNumber, MaxDocNoLength)!,
                DocDate = docDate,
                Status = PoSbStatuses.New,
                Type = docType,
                Prefix = PoSbCalc.Truncate(issued.PrefixUsed, 20),
                LocationCode = context.LocationCode,
                CreatedDate = now,
                CreatedBy = uid
            };

            ApplyHeader(header, request, vendor!, origin);

            short lineNo = 1;
            foreach (var line in request.Lines)
            {
                header.Details.Add(PoSbCalc.BuildCdnDetail(
                    lineNo++, line, PoSbCalc.ResolveTaxPercent(line.TaxGroup, taxPercents)));
            }

            PoSbCalc.ApplyHeaderTotals(header, header.Details);

            db.PoSbCdns.Add(header);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PoSbCdnOperationResult.OkSaved(header.DocNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.", PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed note save failed for vendor {Vendor}", request.VendorCode);
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                "Unable to save the self-billed note.", PoSbErrorKind.Unexpected);
        }
    }

    public async Task<PoSbCdnOperationResult> UpdateAsync(
        string docNo, PoSbCdnSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoSbCdnOperationResult.FailValidation("Save request is required.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbCdnOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        var wanted = PoSbCalc.NullIfBlank(docNo);
        if (wanted is null)
        {
            return PoSbCdnOperationResult.Fail("Document number is required.", PoSbErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var header = await db.PoSbCdns
                .Include(x => x.Details)
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == context!.CompanyCode
                         && x.BranchCode == context.BranchCode
                         && x.DocNo == wanted,
                    cancellationToken);

            if (header is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.Fail(
                    $"Self-billed note {wanted} was not found.", PoSbErrorKind.NotFound);
            }

            if (!await CanAsync(header.Type, PermissionCodes.Edit, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
            }

            // The e-Invoice state is the ONLY structural gate on a self-billed note. The ERP NEW/POSTED
            // dimension is retired (nothing writes POSTED any more), so a note stays editable while it
            // has not been submitted and while it is in a fix-and-resubmit state (INVALID / REJECTED /
            // CANCELLED / FAILED). Only SUBMITTING / SUBMITTED / VALID lock it.
            if (PoSbCalc.IsEInvoiceLocked(header.IrbmStatus))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.Fail(
                    $"This document cannot be edited while its e-Invoice status is {EInvoiceStatuses.Normalize(header.IrbmStatus)}.",
                    PoSbErrorKind.BusinessRule);
            }

           // The type is fixed at creation: a CN can never become a DN.
            var docType = PoSbCalc.NormalizeType(request.Type);
            if (PoSbCalc.IsValidType(docType)
                && !string.Equals(docType, PoSbCalc.NormalizeType(header.Type), StringComparison.Ordinal))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.Fail(
                    "The note type cannot be changed after it is created.", PoSbErrorKind.BusinessRule);
            }

            if (request.RowVersion is { Length: > 0 })
            {
                db.Entry(header).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            }

            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var taxPercents = await PoSbLookupsLoader.LoadTaxPercentsAsync(
                db, context.CompanyCode, cancellationToken);

            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            PoSbCalc.ValidateLines(request.Lines, taxPercents, errors);

            var vendor = await LoadVendorAsync(db, context.CompanyCode, request.VendorCode, errors, cancellationToken);
            var origin = await LoadOriginAsync(
                db, context.CompanyCode, context.BranchCode, request, vendor, errors, cancellationToken);

            if (errors.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoSbCdnOperationResult.FailValidation("Validation failed.", errors);
            }

            header.DocDate = docDate;
            ApplyHeader(header, request, vendor!, origin);
            header.ModifiedDate = DateTime.UtcNow;
            header.ModifiedBy = PoSbCalc.Truncate(context.UserId, 20);

            header.Details.Clear();
            short lineNo = 1;
            foreach (var line in request.Lines)
            {
                header.Details.Add(PoSbCalc.BuildCdnDetail(
                    lineNo++, line, PoSbCalc.ResolveTaxPercent(line.TaxGroup, taxPercents)));
            }

            PoSbCalc.ApplyHeaderTotals(header, header.Details);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            return PoSbCdnOperationResult.OkSaved(header.DocNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.", PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed note update failed for {DocNo}", docNo);
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                "Unable to save the self-billed note.", PoSbErrorKind.Unexpected);
        }
    }

    public Task<PoSbCdnOperationResult> DeleteAsync(
        IReadOnlyList<PoSbKeyedRequest> items, CancellationToken cancellationToken = default) =>
        TransitionAsync(
            items,
            PermissionCodes.Delete,
            "delete",
            static header =>
                // The e-Invoice state is the only structural gate: the same predicate as Edit. A legacy
                // POSTED row is therefore deletable — the ERP status carries no accounting meaning here.
                PoSbCalc.IsEInvoiceLocked(header.IrbmStatus)
                    ? $"Document {header.DocNo} cannot be deleted while its e-Invoice status is {EInvoiceStatuses.Normalize(header.IrbmStatus)}."
                    : null,
            static (db, header) => db.PoSbCdns.Remove(header),
            cancellationToken);

    /// <summary>
    /// The ONE driver for the batch action that remains on the note family (delete). Authorization is
    /// checked per document against <b>that document's own type menu</b> (CN and DN are separate
    /// rights), and every selected document is validated before any of them is changed.
    /// </summary>
    private async Task<PoSbCdnOperationResult> TransitionAsync(
        IReadOnlyList<PoSbKeyedRequest> items,
        string permission,
        string actionVerb,
        Func<PoSbCdn, string?> validate,
        Action<AppDbContext, PoSbCdn> apply,
        CancellationToken cancellationToken)
    {
        if (items is null || items.Count == 0)
        {
            return PoSbCdnOperationResult.Fail("No record selected.");
        }

        if (items.Count > PoSbLimits.MaxPostSelection)
        {
            return PoSbCdnOperationResult.Fail(
                $"Select at most {PoSbLimits.MaxPostSelection} documents at a time.");
        }

        var (context, contextError) = ResolveWriteContext();
        if (contextError is not null)
        {
            return PoSbCdnOperationResult.Fail(contextError, PoSbErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var headers = new List<PoSbCdn>(items.Count);

            foreach (var item in items)
            {
                var wanted = PoSbCalc.NullIfBlank(item.DocNo);
                if (wanted is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbCdnOperationResult.Fail("Document number is required.");
                }

                var header = await db.PoSbCdns.FirstOrDefaultAsync(
                    x => x.CompanyCode == context!.CompanyCode
                         && x.BranchCode == context.BranchCode
                         && x.DocNo == wanted,
                    cancellationToken);

                if (header is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbCdnOperationResult.Fail(
                        $"Self-billed note {wanted} was not found.", PoSbErrorKind.NotFound);
                }

                if (!await CanAsync(header.Type, permission, cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbCdnOperationResult.Fail("Not authorized.", PoSbErrorKind.Authorization);
                }

                var refusal = validate(header);
                if (refusal is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoSbCdnOperationResult.Fail(refusal, PoSbErrorKind.BusinessRule);
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

            return PoSbCdnOperationResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                "One of the documents was changed by another user. Reload before retrying.",
                PoSbErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Self-billed note {Action} failed", actionVerb);
            await tx.RollbackAsync(cancellationToken);
            return PoSbCdnOperationResult.Fail(
                $"Unable to {actionVerb} the selected document(s).", PoSbErrorKind.Unexpected);
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

    /// <summary>
    /// Resolves the originating self-billed invoice. Save time requires it to EXIST in this company and
    /// branch and to match the note's vendor and currency; the "must be VALID with a UUID" rule is left
    /// to the payload build (PoSbOriginResolver at submit) so a note can be drafted while its invoice is
    /// still being submitted.
    /// </summary>
    private static async Task<PoSbInvoice?> LoadOriginAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        PoSbCdnSaveRequest request,
        PoSupplier? vendor,
        IDictionary<string, string> errors,
        CancellationToken cancellationToken)
    {
        var docNo = PoSbCalc.NullIfBlank(request.OriginSbInvNo);
        if (docNo is null)
        {
            errors["OriginSbInvNo"] = "The originating self-billed invoice is required.";
            return null;
        }

        var origin = await db.PoSbInvoices.AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DocNo == docNo,
                cancellationToken);

        if (origin is null)
        {
            errors["OriginSbInvNo"] = $"Self-billed invoice {docNo} was not found in this company and branch.";
            return null;
        }

        if (EInvoiceStatuses.Normalize(origin.IrbmStatus) == EInvoiceStatuses.Cancelled)
        {
            errors["OriginSbInvNo"] = $"Self-billed invoice {docNo} was cancelled at MyInvois, so it cannot be referenced.";
            return null;
        }

        // The note must describe the same trading relationship as the invoice it adjusts.
        if (vendor is not null
            && !string.Equals(origin.VendorCode, vendor.SuppCode, StringComparison.OrdinalIgnoreCase))
        {
            errors["VendorCode"] =
                $"Vendor '{vendor.SuppCode}' does not match the vendor of self-billed invoice {docNo} ({origin.VendorCode}).";
            return null;
        }

        // C18 parity with the purchase CN/DN rule: a blank currency inherits, a conflicting one is refused.
        var requestCurrency = PoSbCalc.NullIfBlank(request.Currency);
        if (requestCurrency is not null
            && !string.IsNullOrWhiteSpace(origin.Currency)
            && !string.Equals(requestCurrency, origin.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors["Currency"] =
                $"Currency '{requestCurrency}' does not match the currency of self-billed invoice {docNo} ({origin.Currency}).";
            return null;
        }

        return origin;
    }

    private static void ApplyHeader(
        PoSbCdn header, PoSbCdnSaveRequest request, PoSupplier vendor, PoSbInvoice? origin)
    {
        header.VendorCode = PoSbCalc.Truncate(vendor.SuppCode, 60)!;
        header.VendorName = PoSbCalc.Truncate(request.VendorName ?? vendor.SuppName, 200);
        header.OriginSbInvNo = PoSbCalc.Truncate(origin?.DocNo ?? request.OriginSbInvNo, 30);
        header.Currency = PoSbCalc.Truncate(
            PoSbCalc.NullIfBlank(request.Currency) ?? origin?.Currency ?? vendor.Currency, 20);
        header.CurrRate = request.CurrRate <= 0m
            ? (origin is { CurrRate: > 0m } ? origin.CurrRate : 1m)
            : request.CurrRate;
        header.TaxGrCode = PoSbCalc.Truncate(request.TaxGrCode ?? vendor.TaxGroup ?? vendor.TaxGrCode, 20);
        header.Remarks = PoSbCalc.Truncate(request.Remarks, 500);
    }

    private static PoSbCdnDocument ToDocument(PoSbCdn header) => new()
    {
        CompanyCode = header.CompanyCode,
        BranchCode = header.BranchCode,
        DocNo = header.DocNo,
        DocDate = header.DocDate,
        Status = header.Status,
        Type = header.Type,
        Prefix = header.Prefix,
        VendorCode = header.VendorCode,
        VendorName = header.VendorName,
        OriginSbInvNo = header.OriginSbInvNo,
        Currency = header.Currency,
        CurrRate = header.CurrRate,
        TaxGrCode = header.TaxGrCode,
        Remarks = header.Remarks,
        GrossAmnt = header.GrossAmnt,
        Taxes = header.Taxes,
        TotAmnt = header.TotAmnt,
        IrbmStatus = header.IrbmStatus,
        IrbmUuid = header.IrbmUuid,
        IrbmOriUuid = header.IrbmOriUuid,
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

    private static PoSbLineDto ToLineDto(PoSbCdnDetail line) => new()
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
