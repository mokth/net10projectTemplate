using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Purchase;

/// <summary>
/// Purchase Credit / Debit Notes. Financial supplier adjustment; the physical return is delegated
/// to the existing Vendor Return posting, which already maintains PO ReturnQty / FinClosed.
/// </summary>
public sealed class PoCdnService : IPoCdnService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly IRunningNumberService _runningNumbers;
    private readonly ICurrentDateService _dates;
    private readonly IPoCdnRepository _cdns;
    private readonly IPoInvoiceRepository _invoices;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvInventoryPostingService _posting;
    private readonly ILogger<PoCdnService> _logger;

    /// <summary>Test-only: invoked after the VR stock-out succeeds, before the CN is marked POSTED.</summary>
    internal Action? TestHookAfterStockOut { get; set; }

    /// <summary>Test-only: invoked after the VR stock-out rollback succeeds, before the CN is NEW.</summary>
    internal Action? TestHookAfterStockRollback { get; set; }

    public PoCdnService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        IRunningNumberService runningNumbers,
        ICurrentDateService dates,
        IPoCdnRepository cdns,
        IPoInvoiceRepository invoices,
        IIvStockPostingRepository postingRepo,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvInventoryPostingService posting,
        ILogger<PoCdnService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _runningNumbers = runningNumbers;
        _dates = dates;
        _cdns = cdns;
        _invoices = invoices;
        _postingRepo = postingRepo;
        _stockMasters = stockMasters;
        _common = common;
        _posting = posting;
        _logger = logger;
    }

    // ─────────────────────────── Context helpers ───────────────────────────

    private sealed record UserContext(string? CompanyCode, string? BranchCode, string? LocationCode, string? UserId, string? Error)
    {
        public static UserContext Fail(string error) => new(null, null, null, null, error);
    }

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        return scope is null
            ? UserContext.Fail("Invalid company or branch context.")
            : new UserContext(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId, null);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        return scope is null
            ? UserContext.Fail("Write context is required.")
            : new UserContext(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId, null);
    }

    private static string MenuCodeFor(string? type) =>
        PoCdnCalc.NormalizeType(type) == PoCdnTypes.DebitNote
            ? MenuCodes.PurchaseDebitNote
            : MenuCodes.PurchaseCreditNote;

    public Task<bool> CanAsync(string type, string permissionCode, CancellationToken cancellationToken = default) =>
        _accessRights.CanAsync(MenuCodeFor(type), permissionCode, cancellationToken);

    private static string Truncate(string? value, int max)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length <= max ? v : v[..max];
    }

    private static string? TruncateOptional(string? value, int max)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
        {
            return null;
        }

        return v.Length <= max ? v : v[..max];
    }

    private static bool RowVersionsEqual(byte[]? a, byte[]? b) =>
        a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    private static void TouchRowVersion(AppDbContext db, PoCdn cdn) =>
        db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = cdn.RowVersion;

    /// <summary>C40: the single authoritative POST gate. The approval phase extends this, not replaces it.</summary>
    private static string? CanPost(PoCdn cdn)
    {
        if (!string.Equals(cdn.Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return "Only NEW documents can be posted.";
        }

        return null;
    }

    // ─────────────────────────── Lookups ───────────────────────────

    public async Task<PoCdnOperationResult> GetLookupsAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(type, PermissionCodes.Access, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        var stockRows = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var items = stockRows.Select(x => new PoCdnItemLookupRow
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            IType = x.IType,
            StdUom = x.StdUom,
            PurUom = x.PurUom,
            StdPackSize = x.StdPackSize,
            PurStdPackSize = x.PurStdPackSize,
            PurchasePrice = x.PurchasePrice,
            PurchaseGlCode = x.PurchaseGlCode,
            TaxGroup = x.TaxGroup,
            PurchaseTaxGroup = x.PurchaseTaxGroup,
            StockControl = x.StockControl,
            LotControl = x.LotControl,
            DefWarehouse = x.DefWarehouse,
            DefLocation = x.DefLocation,
            Classification = x.Classification
        }).ToList();

        var warehouseRows = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!, context.BranchCode!, cancellationToken);
        var warehouses = warehouseRows.Select(x => new IvWarehouseLookupRow
        {
            WarehouseCode = x.WarehouseCode,
            WarehouseDesc = x.WarehouseDesc
        }).ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var vendors = await db.PoSuppliers.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .OrderBy(x => x.SuppCode)
            .Select(x => new PoCdnVendorLookupRow
            {
                VendorCode = x.SuppCode,
                VendorName = x.SuppName,
                Currency = x.Currency,
                PayCode = x.PayCode,
                TaxGrCode = x.TaxGrCode,
                IsActive = x.IsActive,
                Suspended = x.Suspend == true
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new PoCdnTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var payCodes = await db.SaPaymentTerms.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive != false)
            .OrderBy(x => x.PayCode)
            .Select(x => new IvCodeLookupRow { Code = x.PayCode, Desc = x.PayDesc })
            .ToListAsync(cancellationToken);

        var canInternalAdjustment = await CanAsync(
            type, PermissionCodes.InternalAdjustment, cancellationToken);

        var reasonCodes = PoCdnCalc.ReasonsFor(type)
            .Select(r => new PoCdnReasonCodeOption
            {
                Code = r.Code,
                InventoryCapable = r.InventoryCapable,
                RequiresPo = r.RequiresPo,
                RequiresInvoice = r.RequiresInvoice,
                InternalAdjustment = r.InternalAdjustment,
                AllowedForUser = !r.InternalAdjustment || canInternalAdjustment
            })
            .ToList();

        return PoCdnOperationResult.OkLookups(items, warehouses, vendors, taxGroups, payCodes, reasonCodes);
    }

    public async Task<PoCdnOperationResult> GetVendorDefaultsAsync(
        string vendorCode,
        DateTime docDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var vendor = (vendorCode ?? string.Empty).Trim();
        if (vendor.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Vendor is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.PoSuppliers.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode && x.SuppCode == vendor,
            cancellationToken);

        if (row is null)
        {
            return PoCdnOperationResult.Fail($"Vendor {vendor} was not found.", PoCdnErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(row.Currency) ? "MYR" : row.Currency!.Trim();
        var (rateError, rate) = await ResolveCurrRateCoreAsync(db, currency, docDate, cancellationToken);

        // C13/C15: save-time gate. Suspended vendors block new documents; inactive vendors too.
        var blocked = !row.IsActive
            ? $"Vendor {row.SuppCode} is inactive. New CN/DN cannot be saved."
            : row.Suspend == true
                ? $"Vendor {row.SuppCode} is suspended. New CN/DN cannot be saved."
                : null;

        return PoCdnOperationResult.OkDefaults(new PoCdnVendorDefaults
        {
            VendorCode = row.SuppCode,
            VendorName = row.SuppName,
            Currency = currency,
            CurrRate = rate,
            CurrRateValid = rateError is null,
            PayCode = row.PayCode,
            TaxGrCode = row.TaxGrCode,
            IsActive = row.IsActive,
            Suspended = row.Suspend == true,
            BlockedForSave = blocked is not null,
            BlockedMessage = blocked,
            InvAddress1 = row.Address1,
            InvAddress2 = row.Address2,
            InvAddress3 = row.Address3,
            InvAddress4 = row.Address4,
            City = row.City,
            State = row.State,
            PostalCode = row.PostalCode,
            Country = row.Country,
            Tel = row.Tel,
            Fax = row.Fax
        });
    }

    public async Task<PoCdnOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (error, rate) = await ResolveCurrRateCoreAsync(db, currency, docDate, cancellationToken);
        return error is null
            ? PoCdnOperationResult.OkRate(rate, true)
            : PoCdnOperationResult.FailRate(error);
    }

    /// <summary>
    /// C18/C26: fail-closed. Home currency without a row resolves to 1; a foreign currency needs a
    /// SaCurrRate window covering <paramref name="docDate"/> and a rate other than 1. Deliberately
    /// not PoInvoiceService.ResolveCurrRateAsync, which silently returns 1m.
    /// </summary>
    private static async Task<(string? Error, decimal Rate)> ResolveCurrRateCoreAsync(
        AppDbContext db,
        string? currency,
        DateTime docDate,
        CancellationToken cancellationToken)
    {
        var code = (currency ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return ("Currency is required.", 0m);
        }

        var isHome = string.Equals(code, "MYR", StringComparison.OrdinalIgnoreCase);
        var date = docDate.Date;

        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == code && x.Status && x.StartDate <= date && x.EndDate >= date)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            return isHome ? (null, 1m) : ($"No currency rate for {code} on {date:yyyy-MM-dd}.", 0m);
        }

        var value = Convert.ToDecimal(rate.Value);
        if (!isHome && value == 1m)
        {
            return ("Non-home currency rate cannot be 1.", 0m);
        }

        return (null, value);
    }

    // ─────────────────────────── Search / get ───────────────────────────

    public async Task<PoCdnOperationResult> SearchAsync(
        PoCdnListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var docType = PoCdnCalc.NormalizeType(query.Type);
        if (!PoCdnCalc.IsValidType(docType))
        {
            return PoCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(docType, PermissionCodes.Access, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _cdns.SearchPagedAsync(
            db, context.CompanyCode!, context.BranchCode!,
            new PoCdnSearchArgs(
                docType, query.SearchText, query.Status, query.DateFrom, query.DateTo,
                query.SortField, query.SortDescending, query.Skip, query.Take),
            cancellationToken);

        // Line counts in one grouped query rather than N+1.
        var docNos = rows.Select(x => x.DocNo).ToList();
        var lineCounts = await db.PoCdnDetails.AsNoTracking()
            .Where(d => d.CompanyCode == context.CompanyCode
                        && d.BranchCode == context.BranchCode
                        && docNos.Contains(d.DocNo))
            .GroupBy(d => d.DocNo)
            .Select(g => new { DocNo = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.DocNo, x => x.Count, cancellationToken);

        return PoCdnOperationResult.OkList(new PoCdnListPage
        {
            TotalCount = total,
            Rows = rows.Select(x => new PoCdnListRow
            {
                DocNo = x.DocNo,
                DocDate = x.DocDate,
                Status = x.Status,
                Type = x.Type,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                InvNo = x.InvNo,
                SupplierDocNo = x.SupplierDocNo,
                SupplierDocDate = x.SupplierDocDate,
                ReasonCode = x.ReasonCode,
                ReturnStock = x.ReturnStock,
                TotAmnt = x.TotAmnt,
                LineCount = lineCounts.TryGetValue(x.DocNo, out var c) ? c : 0,
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy,
                RowVersion = x.RowVersion
            }).ToList()
        });
    }

    public async Task<PoCdnOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var no = (docNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Document number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var cdn = await _cdns.GetWithDetailsAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (cdn is null)
        {
            return PoCdnOperationResult.Fail($"Document {no} was not found.", PoCdnErrorKind.NotFound);
        }

        if (!await CanAsync(cdn.Type, PermissionCodes.Access, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        return PoCdnOperationResult.OkDocument(MapDocument(cdn));
    }

    private static PoCdnDocument MapDocument(PoCdn cdn) => new()
    {
        DocNo = cdn.DocNo,
        DocDate = cdn.DocDate,
        Status = cdn.Status,
        Type = cdn.Type,
        InvNo = cdn.InvNo,
        ReturnStock = cdn.ReturnStock,
        VrBatchNo = cdn.VrBatchNo,
        VendorCode = cdn.VendorCode,
        VendorName = cdn.VendorName,
        Prefix = cdn.Prefix,
        Currency = cdn.Currency,
        CurrRate = cdn.CurrRate,
        PayCode = cdn.PayCode,
        TaxGrCode = cdn.TaxGrCode,
        ReasonCode = cdn.ReasonCode,
        SupplierDocNo = cdn.SupplierDocNo,
        SupplierDocDate = cdn.SupplierDocDate,
        BuyerCode = cdn.BuyerCode,
        Dept = cdn.Dept,
        ProjId = cdn.ProjId,
        Remarks = cdn.Remarks,
        RefNo = cdn.RefNo,
        ExternalDocNo = cdn.ExternalDocNo,
        InvAddress1 = cdn.InvAddress1,
        InvAddress2 = cdn.InvAddress2,
        InvAddress3 = cdn.InvAddress3,
        InvAddress4 = cdn.InvAddress4,
        InvCity = cdn.City,
        InvState = cdn.State,
        InvPostalCode = cdn.PostalCode,
        InvCountry = cdn.Country,
        InvTel = cdn.Tel,
        InvFax = cdn.Fax,
        GrossAmnt = cdn.GrossAmnt,
        Taxes = cdn.Taxes,
        TotAmnt = cdn.TotAmnt,
        PostedDate = cdn.PostedDate,
        PostedBy = cdn.PostedBy,
        RollbackDate = cdn.RollbackDate,
        RollbackBy = cdn.RollbackBy,
        RowVersion = cdn.RowVersion,
        Lines = cdn.Details
            .OrderBy(d => d.Line)
            .Select(d => new PoCdnLineDto
            {
                Line = d.Line,
                InvLineNo = d.InvLineNo,
                IsStockReturn = d.IsStockReturn,
                ICode = d.ICode,
                IDesc = d.IDesc,
                Qty = d.Qty,
                StdQty = d.StdQty,
                StdCustPSize = d.StdCustPsize,
                SellingUom = d.SellingUom,
                StdUom = d.StdUom,
                UnitPrice = d.UnitPrice,
                Amount = d.Amount,
                ItemDiscount = d.ItemDiscount,
                ItemDiscount1 = d.ItemDiscount1,
                IDiscountType = d.IDiscountType,
                IDiscountType1 = d.IDiscountType1,
                IsInclusive = d.IsInclusive,
                TaxGroup = d.TaxGroup,
                TaxAmt = d.TaxAmt,
                NetAmount = d.NetAmount,
                IsTaxOnly = PoCdnCalc.IsTaxOnlyLine(d),
                StockControl = d.StockControl,
                ItemGlCode = d.ItemGlCode,
                Classification = d.Classification,
                Remarks = d.Remarks,
                CostPrice = d.CostPrice,
                PoNo = d.PoNo,
                PoRelNo = d.PoRelNo,
                PoLineNo = d.PoLineNo,
                FrWarehouse = d.FrWarehouse,
                LocCode = d.LocCode,
                IStatus = d.IStatus,
                LotNo = d.LotNo,
                ExpiryDate = d.ExpiryDate,
                FromBalLocId = d.FromBalLocId
            })
            .ToList()
    };

    // ─────────────────────────── Save / update ───────────────────────────

    public async Task<PoCdnOperationResult> SaveNewAsync(
        PoCdnSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoCdnOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var docType = PoCdnCalc.NormalizeType(request.Type);
        if (!PoCdnCalc.IsValidType(docType))
        {
            return PoCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(docType, PermissionCodes.Add, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;

            // C12: no future-dated documents, and not before the company's earliest permitted date.
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var invNo = PoCdnCalc.NormalizeSupplierDocNo(request.InvNo);

            // Canonical lock order: invoice (if any) then the document. There is no document yet,
            // so this is the invoice lock only — held across the reservation and source-line checks (C41).
            PoInvoice? lockedInvoice = null;
            if (invNo is not null)
            {
                lockedInvoice = await _invoices.LockForUpdateAsync(
                    db, context.CompanyCode!, context.BranchCode!, invNo, cancellationToken);
                if (lockedInvoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoCdnOperationResult.Fail(
                        $"Invoice {invNo} was not found.", PoCdnErrorKind.NotFound);
                }
            }

            var prepared = await PrepareAsync(
                db, context, request, docType, docDate, lockedInvoice, excludeDocNo: null, cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var issued = await _documentNumbers.NextAsync(
                db,
                PoCdnCalc.NumberingModuleFor(docType),
                string.Empty,
                docDate,
                DocumentNumberRequestMode.New,
                "AUTO",
                cancellationToken);

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId, 20);

            var header = new PoCdn
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                DocNo = issued.DocumentNumber,
                DocDate = docDate,
                Status = PoCdnStatuses.New,
                Type = docType,
                Prefix = issued.PrefixUsed,
                LocationCode = context.LocationCode,
                CreatedDate = now,
                CreatedBy = uid,
                ReturnStock = docType == PoCdnTypes.CreditNote && request.ReturnStock
            };

            ApplyHeader(header, request, prepared.Vendor!.SuppName, prepared.Currency!, prepared.CurrRate);
            header.GrossAmnt = prepared.GrossAmnt;
            header.Taxes = prepared.Taxes;
            header.TotAmnt = prepared.TotAmnt;

            foreach (var line in prepared.Lines!)
            {
                header.Details.Add(line);
            }

            db.PoCdns.Add(header);
            await db.SaveChangesAsync(cancellationToken);

            // C7/C43: a NEW draft may already own a NEW VR batch when ReturnStock is set.
            if (header.ReturnStock)
            {
                await EnsureVrBatchAsync(db, context, header, now, uid, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return PoCdnOperationResult.OkSaved(header.DocNo);
        }
        catch (DbUpdateException ex) when (IsSupplierDocDuplicate(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail(
                $"Supplier document {PoCdnCalc.NormalizeSupplierDocNo(request.SupplierDocNo)} is already recorded.",
                PoCdnErrorKind.BusinessRule);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.",
                PoCdnErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoCdn save failed for vendor {Vendor}", request.VendorCode);
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail("Unable to save the document.", PoCdnErrorKind.Unexpected);
        }
    }

    public async Task<PoCdnOperationResult> UpdateAsync(
        string docNo,
        PoCdnSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return PoCdnOperationResult.FailValidation("Save request is required.");
        }

        var no = (docNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Document number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var docType = PoCdnCalc.NormalizeType(request.Type);
        if (!PoCdnCalc.IsValidType(docType))
        {
            return PoCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        if (!await CanAsync(docType, PermissionCodes.Edit, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            if (docDate > _dates.Today.Date)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.FailValidation("Document date cannot be in the future.");
            }

            var invNo = PoCdnCalc.NormalizeSupplierDocNo(request.InvNo);

            var locked = await PoCdnLockOrder.AcquireAsync(
                db, _invoices, _cdns,
                context.CompanyCode!, context.BranchCode!, invNo, no, cancellationToken);

            var cdn = locked.Cdn;
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.Fail($"Document {no} was not found.", PoCdnErrorKind.NotFound);
            }

            // C21: business immutability. RowVersion is concurrency control, not immutability.
            // Checked before the RowVersion presence guard deliberately: a caller that omits
            // RowVersion on a POSTED document should be told it is immutable, not that someone
            // else changed it.
            if (!string.Equals(cdn.Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.Fail("Only NEW documents can be edited.", PoCdnErrorKind.BusinessRule);
            }

            if (request.RowVersion is null || request.RowVersion.Length == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.Fail(
                    "This document was changed by another user. Reload before saving.",
                    PoCdnErrorKind.Concurrency);
            }

            if (!RowVersionsEqual(cdn.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.Fail(
                    "This document was changed by another user. Reload before saving.",
                    PoCdnErrorKind.Concurrency);
            }

            db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);

            if (invNo is not null && locked.Invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnOperationResult.Fail($"Invoice {invNo} was not found.", PoCdnErrorKind.NotFound);
            }

            var prepared = await PrepareAsync(
                db, context, request, docType, docDate, locked.Invoice, excludeDocNo: no, cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId, 20);

            ApplyHeader(cdn, request, prepared.Vendor!.SuppName, prepared.Currency!, prepared.CurrRate);
            cdn.ReturnStock = docType == PoCdnTypes.CreditNote && request.ReturnStock;
            cdn.GrossAmnt = prepared.GrossAmnt;
            cdn.Taxes = prepared.Taxes;
            cdn.TotAmnt = prepared.TotAmnt;
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;

            db.PoCdnDetails.RemoveRange(cdn.Details);
            cdn.Details.Clear();
            foreach (var line in prepared.Lines!)
            {
                cdn.Details.Add(line);
            }

            TouchRowVersion(db, cdn);

            // C7/C43: switching ReturnStock true -> false on a NEW draft clears the owned VR batch.
            await SyncVrBatchForDraftAsync(db, context, cdn, now, uid, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return PoCdnOperationResult.OkSaved(cdn.DocNo);
        }
        catch (DbUpdateException ex) when (IsSupplierDocDuplicate(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail(
                $"Supplier document {PoCdnCalc.NormalizeSupplierDocNo(request.SupplierDocNo)} is already recorded.",
                PoCdnErrorKind.BusinessRule);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.",
                PoCdnErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoCdn update failed for {DocNo}", no);
            await tx.RollbackAsync(cancellationToken);
            return PoCdnOperationResult.Fail("Unable to save the document.", PoCdnErrorKind.Unexpected);
        }
    }

    private static void ApplyHeader(
        PoCdn header, PoCdnSaveRequest request, string? vendorName, string currency, decimal currRate)
    {
        header.VendorCode = Truncate(request.VendorCode, 60);
        header.VendorName = TruncateOptional(vendorName, 200);
        header.Currency = TruncateOptional(currency, 20);
        header.CurrRate = currRate > 0m ? currRate : 1m;
        header.PayCode = TruncateOptional(request.PayCode, 20);
        header.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        header.ReasonCode = TruncateOptional(request.ReasonCode, 30);
        header.SupplierDocNo = PoCdnCalc.NormalizeSupplierDocNo(request.SupplierDocNo);
        header.SupplierDocDate = request.SupplierDocDate?.Date;
        header.InvNo = PoCdnCalc.NormalizeSupplierDocNo(request.InvNo);
        header.BuyerCode = TruncateOptional(request.BuyerCode, 20);
        header.Dept = TruncateOptional(request.Dept, 20);
        header.ProjId = TruncateOptional(request.ProjId, 20);
        header.Remarks = TruncateOptional(request.Remarks, 500);
        header.RefNo = TruncateOptional(request.RefNo, 50);
        header.ExternalDocNo = TruncateOptional(request.ExternalDocNo, 50);
        header.InvAddress1 = TruncateOptional(request.InvAddress1, 100);
        header.InvAddress2 = TruncateOptional(request.InvAddress2, 100);
        header.InvAddress3 = TruncateOptional(request.InvAddress3, 100);
        header.InvAddress4 = TruncateOptional(request.InvAddress4, 100);
        header.City = TruncateOptional(request.InvCity, 50);
        header.State = TruncateOptional(request.InvState, 50);
        header.PostalCode = TruncateOptional(request.InvPostalCode, 20);
        header.Country = TruncateOptional(request.InvCountry, 50);
        header.Tel = TruncateOptional(request.InvTel, 50);
        header.Fax = TruncateOptional(request.InvFax, 50);
    }

    private static bool IsSupplierDocDuplicate(DbUpdateException ex) =>
        ex.InnerException?.Message.Contains("UX_PoCdn_SupplierDoc", StringComparison.OrdinalIgnoreCase) == true;

    // ─────────────────────────── Prepare (validation + line build) ───────────────────────────

    private sealed class PreparedOutcome
    {
        public List<PoCdnDetail>? Lines { get; init; }
        public decimal GrossAmnt { get; init; }
        public decimal Taxes { get; init; }
        public decimal TotAmnt { get; init; }
        public PoSupplier? Vendor { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; }
        public string? Error { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static PreparedOutcome Fail(string error) => new() { Error = error };

        public static PreparedOutcome FailValidation(
            string error, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = error, Errors = errors };

        /// <summary>
        /// Surfaces the first real message as <c>ErrorMessage</c> while keeping the per-field
        /// dictionary. Returning only "Validation failed." would leave the operator with nothing
        /// actionable to read.
        /// </summary>
        public PoCdnOperationResult ToFail()
        {
            if (Errors.Count == 0)
            {
                return PoCdnOperationResult.Fail(Error!);
            }

            var detail = string.Join(" ", Errors.Values.Where(v => !string.IsNullOrWhiteSpace(v)));
            return PoCdnOperationResult.FailValidation(
                string.IsNullOrWhiteSpace(detail) ? Error! : detail,
                Errors);
        }
    }

    private async Task<PreparedOutcome> PrepareAsync(
        AppDbContext db,
        UserContext context,
        PoCdnSaveRequest request,
        string docType,
        DateTime docDate,
        PoInvoice? lockedInvoice,
        string? excludeDocNo,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var company = context.CompanyCode!;
        var branch = context.BranchCode!;

        // ── Vendor (C13/C15) ──────────────────────────────────────────────────
        var vendorCode = (request.VendorCode ?? string.Empty).Trim();
        PoSupplier? vendor = null;
        if (vendorCode.Length == 0)
        {
            errors["VendorCode"] = "Vendor is required.";
        }
        else
        {
            vendor = await db.PoSuppliers.AsNoTracking().FirstOrDefaultAsync(
                x => x.CompanyCode == company && x.SuppCode == vendorCode, cancellationToken);
            if (vendor is null)
            {
                errors["VendorCode"] = "Vendor was not found.";
            }
            else if (!vendor.IsActive)
            {
                errors["VendorCode"] = $"Vendor {vendorCode} is inactive. New CN/DN cannot be saved.";
            }
            else if (vendor.Suspend == true)
            {
                errors["VendorCode"] = $"Vendor {vendorCode} is suspended. New CN/DN cannot be saved.";
            }
        }

        // ── Reason code + metadata (C9/C17/C44) ───────────────────────────────
        var reasonCode = (request.ReasonCode ?? string.Empty).Trim().ToUpperInvariant();
        if (reasonCode.Length == 0)
        {
            errors["ReasonCode"] = "Reason code is required.";
        }
        else
        {
            var info = PoCdnCalc.GetReasonInfo(docType, reasonCode);
            if (info is null)
            {
                errors["ReasonCode"] = $"Reason code {reasonCode} is not valid for a {docType}.";
            }
            else if (info.InternalAdjustment)
            {
                // C44: gated, and the business justification must be recorded.
                if (!await CanAsync(docType, PermissionCodes.InternalAdjustment, cancellationToken))
                {
                    errors["ReasonCode"] = "You are not authorized to use INTERNAL_ADJUSTMENT.";
                }

                if (string.IsNullOrWhiteSpace(request.Remarks))
                {
                    errors["Remarks"] = "Remarks are required for an internal adjustment.";
                }
            }
        }

        var reasonInfo = PoCdnCalc.GetReasonInfo(docType, reasonCode);

        // ── Supplier document (C2/C14/C16/C44) ────────────────────────────────
        var supplierDocNo = PoCdnCalc.NormalizeSupplierDocNo(request.SupplierDocNo);
        var externalDocNo = PoCdnCalc.NormalizeSupplierDocNo(request.ExternalDocNo);
        var supplierDocDate = request.SupplierDocDate?.Date;

        if (supplierDocNo is null && reasonInfo?.InternalAdjustment != true)
        {
            errors["SupplierDocNo"] = "Supplier document number is required (or use an internal-adjustment reason).";
        }

        if (supplierDocNo is not null && supplierDocDate is null)
        {
            errors["SupplierDocDate"] = "Supplier document date is required when a supplier document number is entered.";
        }

        if (supplierDocNo is not null && externalDocNo is not null
            && string.Equals(supplierDocNo, externalDocNo, StringComparison.OrdinalIgnoreCase))
        {
            errors["ExternalDocNo"] = "External document number must not repeat the supplier document number.";
        }

        if (supplierDocNo is not null && vendorCode.Length > 0)
        {
            var clash = await _cdns.FindBySupplierDocAsync(
                db, company, branch, vendorCode, docType, supplierDocNo, excludeDocNo, cancellationToken);
            if (clash is not null)
            {
                errors["SupplierDocNo"] =
                    $"Supplier document {supplierDocNo} is already recorded on {clash.DocNo}.";
            }
        }

        // ── Invoice target (C4/C24) ───────────────────────────────────────────
        var invNo = PoCdnCalc.NormalizeSupplierDocNo(request.InvNo);
        if (docType == PoCdnTypes.CreditNote && invNo is null)
        {
            errors["InvNo"] = "A credit note requires a referenced purchase invoice.";
        }

        if (invNo is not null)
        {
            if (lockedInvoice is null)
            {
                errors["InvNo"] = $"Invoice {invNo} was not found.";
            }
            else if (!string.Equals(lockedInvoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase))
            {
                errors["InvNo"] = "The referenced document is not a purchase invoice (INV).";
            }
            else if (!string.Equals(lockedInvoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                errors["InvNo"] = "The referenced purchase invoice must be POSTED.";
            }
            else if (vendorCode.Length > 0
                     && !string.Equals(lockedInvoice.VendorCode, vendorCode, StringComparison.OrdinalIgnoreCase))
            {
                errors["InvNo"] = "The referenced invoice belongs to a different vendor.";
            }
        }

        // ── Currency (C6/C18): the invoice currency governs a referenced note ──
        // C18: with a referenced invoice the header currency MUST equal the invoice currency.
        // A blank request currency inherits silently; a *conflicting* one is rejected rather than
        // quietly overridden, because overriding discards the operator's stated intent (e.g. they
        // deliberately picked a foreign currency) without ever telling them.
        string currency;
        if (invNo is not null)
        {
            var invoiceCurrency = (lockedInvoice?.Currency ?? string.Empty).Trim();
            var requested = (request.Currency ?? string.Empty).Trim();

            if (requested.Length > 0
                && !string.Equals(requested, invoiceCurrency, StringComparison.OrdinalIgnoreCase))
            {
                errors["Currency"] =
                    $"Currency {requested} does not match the currency {invoiceCurrency} of invoice {invNo}.";
                currency = requested;
            }
            else
            {
                currency = invoiceCurrency;
            }
        }
        else
        {
            currency = string.IsNullOrWhiteSpace(request.Currency)
                ? (vendor?.Currency ?? "MYR").Trim()
                : request.Currency!.Trim();
        }

        var (rateError, currRate) = await ResolveCurrRateCoreAsync(db, currency, docDate, cancellationToken);
        if (rateError is not null && !errors.ContainsKey("Currency"))
        {
            // Do not clobber the C18 mismatch above — it is the more specific fault.
            errors["Currency"] = rateError;
        }

        if (errors.Count > 0)
        {
            return PreparedOutcome.FailValidation("Validation failed.", errors);
        }

        // ── Reference data ────────────────────────────────────────────────────
        var taxPercents = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == company)
            .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

        // LockForUpdateAsync returns the invoice header only, so the details are read explicitly
        // here — the source-line ceilings and C24 traceability both depend on them.
        var invoiceDetails = lockedInvoice is null
            ? []
            : await db.PoInvoiceDetails.AsNoTracking()
                .Where(x => x.CompanyCode == company
                            && x.BranchCode == branch
                            && x.DocNo == lockedInvoice.DocNo)
                .ToDictionaryAsync(x => x.Line, cancellationToken);

        // C34/C41: source-line consumption by other NEW/POSTED CNs. Read under the invoice lock.
        var consumption = invNo is null
            ? []
            : await _cdns.ListSourceLineUsageAsync(db, company, branch, invNo, excludeDocNo, cancellationToken);

        var sourceLines = request.Lines ?? [];
        if (sourceLines.Count == 0)
        {
            return PreparedOutcome.FailValidation(
                "Validation failed.", new Dictionary<string, string> { ["Lines"] = "At least one line is required." });
        }

        var itemCodes = sourceLines
            .Select(l => (l.ICode ?? string.Empty).Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var items = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == company && itemCodes.Contains(x.ICode))
            .ToDictionaryAsync(x => x.ICode, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var poNos = sourceLines
            .Select(l => (l.PoNo ?? string.Empty).Trim())
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var poLines = await LoadPoLinesAsync(db, company, branch, poNos, cancellationToken);

        // ── Lines ─────────────────────────────────────────────────────────────
        var lines = new List<PoCdnDetail>();
        var sourceQtyByInvLine = new Dictionary<short, decimal>();
        var sourceAmountByInvLine = new Dictionary<short, decimal>();
        var poCandidateQty = new Dictionary<(string PoNo, short PoRelNo, short PoLineNo), decimal>();
        var seenPoSource = new HashSet<(string PoNo, short PoRelNo, short PoLineNo, int? FromBalLocId)>();

        short lineNo = 1;
        foreach (var src in sourceLines)
        {
            var built = BuildLine(
                src, lineNo, docType, request, items, invoiceDetails, taxPercents,
                poLines, seenPoSource, errors);
            if (built is null)
            {
                lineNo++;
                continue;
            }

            lines.Add(built);
            if (built.InvLineNo is { } invLine)
            {
                sourceQtyByInvLine[invLine] = sourceQtyByInvLine.GetValueOrDefault(invLine) + built.StdQty;
                sourceAmountByInvLine[invLine] = sourceAmountByInvLine.GetValueOrDefault(invLine) + built.Amount;
            }

            if (built.IsStockReturn && built.PoNo is not null
                && built.PoRelNo is { } rel && built.PoLineNo is { } pl)
            {
                var key = (built.PoNo, rel, pl);
                poCandidateQty[key] = poCandidateQty.GetValueOrDefault(key) + built.StdQty;
            }

            lineNo++;
        }

        if (lines.Count == 0 && errors.Count == 0)
        {
            errors["Lines"] = "At least one line is required.";
        }

        // Line-level failures must surface before the document-level aggregates below. Otherwise a
        // masked "total is zero" overwrites the real per-line reason and the operator is told the
        // wrong thing.
        if (errors.Count > 0)
        {
            return PreparedOutcome.FailValidation("Validation failed.", errors);
        }

        // ── C34: source-line quantity and value ceilings ──────────────────────
        if (invNo is not null && reasonInfo is not null)
        {
            foreach (var candidate in sourceQtyByInvLine)
            {
                if (!invoiceDetails.TryGetValue(candidate.Key, out var invLine))
                {
                    continue;
                }

                var consumed = consumption
                    .Where(x => x.InvLineNo == candidate.Key)
                    .Sum(x => x.StdQty);

                if (reasonInfo.QuantityCeiling)
                {
                    var eval = PoCdnCalc.EvaluateSourceLineQuantity(
                        candidate.Key, invLine.StdQty, consumed, candidate.Value);
                    if (!eval.Ok)
                    {
                        errors["Lines"] = eval.Error!;
                        break;
                    }
                }

                if (reasonInfo.ValueCeiling)
                {
                    var consumedAmount = consumption
                        .Where(x => x.InvLineNo == candidate.Key)
                        .Sum(x => x.Amount);
                    var eval = PoCdnCalc.EvaluateSourceLineValue(
                        candidate.Key, invLine.Amount, consumedAmount, sourceAmountByInvLine[candidate.Key]);
                    if (!eval.Ok)
                    {
                        errors["Lines"] = eval.Error!;
                        break;
                    }
                }
            }
        }

        // ── C34/C47: combined physical ceiling per PO line ────────────────────
        foreach (var candidate in poCandidateQty)
        {
            if (!poLines.TryGetValue(candidate.Key, out var poLine))
            {
                continue;
            }

            var eval = PoCdnCalc.EvaluatePhysicalCeiling(
                candidate.Key.PoNo, candidate.Key.PoLineNo,
                poLine.RecvQty, poLine.ReturnQty, candidate.Value);
            if (!eval.Ok)
            {
                errors["Lines"] = eval.Error!;
                break;
            }
        }

        // Same masking rule as above: a ceiling breach must not be overwritten by the
        // document-level reservation message.
        if (errors.Count > 0)
        {
            return PreparedOutcome.FailValidation("Validation failed.", errors);
        }

        // ── C2: header reservation (CN against a posted invoice) ──────────────
        if (docType == PoCdnTypes.CreditNote && invNo is not null && lockedInvoice is not null)
        {
            var (gross, taxes, total) = PoCdnCalc.ComputeHeaderTotals(lines);
            var others = await _cdns.ListOtherCreditNotesAsync(
                db, company, branch, invNo, excludeDocNo, cancellationToken);
            var eval = PoCdnCalc.EvaluateRemaining(
                lockedInvoice.TotAmnt,
                others.Select(x => x.TotAmnt).ToList(),
                total,
                others.Where(x => x.IsDraft).Select(x => x.DocNo).ToList());
            if (!eval.Ok)
            {
                errors["Lines"] = eval.Error!;
            }
        }

        var taxModeError = PoCdnCalc.ValidateTaxModeConsistency(lines);
        if (taxModeError is not null)
        {
            errors["Lines"] = taxModeError;
        }

        var totals = PoCdnCalc.ComputeHeaderTotals(lines);
        if (totals.TotAmnt <= 0m)
        {
            errors["Lines"] = "Document total must be greater than zero.";
        }

        if (errors.Count > 0)
        {
            return PreparedOutcome.FailValidation("Validation failed.", errors);
        }

        return new PreparedOutcome
        {
            Lines = lines,
            GrossAmnt = totals.GrossAmnt,
            Taxes = totals.Taxes,
            TotAmnt = totals.TotAmnt,
            Vendor = vendor,
            Currency = currency,
            CurrRate = currRate
        };
    }

    private static async Task<Dictionary<(string PoNo, short PoRelNo, short PoLineNo), PoOrderDetail>>
        LoadPoLinesAsync(
            AppDbContext db,
            string company,
            string branch,
            IReadOnlyList<string> poNos,
            CancellationToken cancellationToken)
    {
        var result = new Dictionary<(string, short, short), PoOrderDetail>();
        if (poNos.Count == 0)
        {
            return result;
        }

        var orders = await db.PoOrders.AsNoTracking()
            .Include(x => x.Details)
            .Where(x => x.CompanyCode == company && x.BranchCode == branch && poNos.Contains(x.PoNo))
            .ToListAsync(cancellationToken);

        // Mirror IvVendorReturnService: only the latest revision of each PO is usable.
        foreach (var order in orders
                     .GroupBy(x => x.PoNo)
                     .Select(g => g.OrderByDescending(x => x.PoRelNo).First()))
        {
            foreach (var detail in order.Details)
            {
                result[(order.PoNo, order.PoRelNo, detail.Line)] = detail;
            }
        }

        return result;
    }

    /// <summary>
    /// Builds one line, or records the failure in <paramref name="errors"/> and returns null.
    /// </summary>
    private static PoCdnDetail? BuildLine(
        PoCdnLineRequest src,
        short lineNo,
        string docType,
        PoCdnSaveRequest request,
        IReadOnlyDictionary<string, IvStockMaster> items,
        IReadOnlyDictionary<short, PoInvoiceDetail> invoiceDetails,
        IReadOnlyDictionary<string, decimal> taxPercents,
        IReadOnlyDictionary<(string PoNo, short PoRelNo, short PoLineNo), PoOrderDetail> poLines,
        HashSet<(string PoNo, short PoRelNo, short PoLineNo, int? FromBalLocId)> seenPoSource,
        Dictionary<string, string> errors)
    {
        void Fail(string message)
        {
            if (!errors.ContainsKey("Lines"))
            {
                errors["Lines"] = message;
            }
        }

        var detail = new PoCdnDetail
        {
            Line = lineNo,
            InvLineNo = src.InvLineNo,
            IsStockReturn = src.IsStockReturn,
            ICode = TruncateOptional(src.ICode, 30),
            IDesc = TruncateOptional(src.IDesc, 200),
            Qty = src.Qty,
            UnitPrice = src.UnitPrice,
            StdCustPsize = src.StdCustPSize,
            ItemDiscount = src.ItemDiscount,
            ItemDiscount1 = src.ItemDiscount1,
            IDiscountType = TruncateOptional(src.IDiscountType, 20),
            IDiscountType1 = TruncateOptional(src.IDiscountType1, 20),
            IsInclusive = src.IsInclusive,
            ItemGlCode = TruncateOptional(src.ItemGlCode, 20),
            Classification = TruncateOptional(src.Classification, 50),
            Remarks = TruncateOptional(src.Remarks, 250),
            CostPrice = src.CostPrice,
            PoNo = TruncateOptional(src.PoNo, 20),
            PoRelNo = src.PoRelNo,
            PoLineNo = src.PoLineNo,
            FrWarehouse = TruncateOptional(src.FrWarehouse, 20),
            LocCode = TruncateOptional(src.LocCode, 20),
            IStatus = TruncateOptional(src.IStatus, 20),
            LotNo = TruncateOptional(src.LotNo, 50),
            ExpiryDate = src.ExpiryDate?.Date,
            FromBalLocId = src.FromBalLocId
        };

        // C43: header gate + line declaration.
        var headerProbe = new PoCdn { Type = docType, ReturnStock = request.ReturnStock };
        var shapeError = PoCdnCalc.ValidateStockReturnShape(headerProbe, detail);
        if (shapeError is not null)
        {
            Fail(shapeError);
            return null;
        }

        // ── C24: invoice-line resolution ──────────────────────────────────────
        PoInvoiceDetail? invLine = null;
        if (detail.InvLineNo is { } invLineNo)
        {
            invoiceDetails.TryGetValue(invLineNo, out invLine);
        }

        var refError = PoCdnCalc.ValidateInvLineReference(
            detail,
            request.InvNo,
            invLine?.Line,
            invLine?.ICode,
            invLine?.PoNo,
            invLine?.PoRelNo,
            invLine?.PoLineNo);
        if (refError is not null)
        {
            Fail(refError);
            return null;
        }

        // ── Item master ───────────────────────────────────────────────────────
        IvStockMaster? item = null;
        if (detail.ICode is not null)
        {
            items.TryGetValue(detail.ICode, out item);
            if (item is null)
            {
                Fail($"Line {lineNo}: item '{detail.ICode}' was not found.");
                return null;
            }

            if (!item.IsActive)
            {
                Fail($"Line {lineNo}: item '{detail.ICode}' is inactive.");
                return null;
            }

            detail.StockControl = item.StockControl;
            detail.IDesc ??= TruncateOptional(item.IDesc, 200);
            detail.Classification ??= TruncateOptional(item.Classification, 50);
        }

        var isTaxOnly = detail.Qty == 0m && detail.UnitPrice == 0m;

        if (!isTaxOnly && item is null)
        {
            Fail($"Line {lineNo}: item code is required.");
            return null;
        }

        if (item is not null && PoOrderCalc.IsServiceIType(item.IType) && detail.IsStockReturn)
        {
            Fail($"Line {lineNo}: service items cannot be stock-returned.");
            return null;
        }

        if (detail.IsStockReturn && item is not null && !item.StockControl)
        {
            Fail($"Line {lineNo}: item '{item.ICode}' is not stock-controlled and cannot be stock-returned.");
            return null;
        }

        // ── C42: conversion factor from the stored snapshot, never current master data ──
        decimal packSize;
        if (invLine is not null)
        {
            packSize = invLine.StdCustPsize;
            detail.StdUom = TruncateOptional(invLine.StdUom, 10);
            detail.SellingUom = TruncateOptional(invLine.SellingUom, 10);
        }
        else if (detail.PoNo is not null && detail.PoRelNo is { } rel && detail.PoLineNo is { } pl
                 && poLines.TryGetValue((detail.PoNo, rel, pl), out var poLine))
        {
            packSize = poLine.PackSz;
            detail.StdUom = TruncateOptional(poLine.StdUom, 10);
        }
        else
        {
            packSize = item is null
                ? 1m
                : PoOrderCalc.EffectivePackSize(item.PurStdPackSize ?? item.StdPackSize ?? 1m);
            detail.StdUom = TruncateOptional(item?.StdUom, 10);
        }

        if (!PoCdnCalc.IsUsablePackSize(packSize))
        {
            Fail($"Line {lineNo}: the UOM conversion factor is missing or not positive.");
            return null;
        }

        detail.StdCustPsize = packSize;
        detail.StdQty = PoCdnCalc.ComputeStdQty(detail.Qty, packSize);

        // ── C47: one PO line may appear twice only from a different inventory source ──
        if (detail.IsStockReturn && detail.PoNo is not null
            && detail.PoRelNo is { } r && detail.PoLineNo is { } l)
        {
            if (!seenPoSource.Add((detail.PoNo, r, l, detail.FromBalLocId)))
            {
                Fail($"Line {lineNo}: the same PO line and inventory source is already used on this document.");
                return null;
            }
        }

        // ── C27: lot/expiry follow the item master ────────────────────────────
        if (detail.IsStockReturn && item is not null)
        {
            if (item.LotControl)
            {
                if (string.IsNullOrWhiteSpace(detail.LotNo))
                {
                    Fail($"Line {lineNo}: lot number is required for lot-controlled item '{item.ICode}'.");
                    return null;
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(detail.LotNo))
                {
                    Fail($"Line {lineNo}: lot number is not allowed for non-lot-controlled item '{item.ICode}'.");
                    return null;
                }

                detail.LotNo = null;
                detail.ExpiryDate = null;
            }
        }

        // ── C32: inherited tax treatment ──────────────────────────────────────
        string? taxGroup;
        if (invLine is not null)
        {
            taxGroup = invLine.TaxGroup;
            // A line-based adjustment inherits the source line's mode and cannot silently change it.
            if (src.IsInclusive != invLine.IsInclusive && (src.Qty != 0m || src.UnitPrice != 0m))
            {
                Fail($"Line {lineNo}: the tax mode must match invoice line {detail.InvLineNo}.");
                return null;
            }

            detail.IsInclusive = invLine.IsInclusive;
        }
        else
        {
            taxGroup = string.IsNullOrWhiteSpace(src.TaxGroup) ? request.TaxGrCode : src.TaxGroup;
            detail.IsInclusive = src.IsInclusive;
        }

        detail.TaxGroup = TruncateOptional(taxGroup, 20);
        var taxPercent = 0m;
        if (!string.IsNullOrWhiteSpace(detail.TaxGroup))
        {
            if (!taxPercents.TryGetValue(detail.TaxGroup!, out taxPercent))
            {
                Fail($"Line {lineNo}: tax group '{detail.TaxGroup}' was not found.");
                return null;
            }
        }

        // ── C6: the locked calculation chain ─────────────────────────────────
        var (net, tax, amount) = PoCdnCalc.ComputeLineAmounts(
            detail.Qty, detail.UnitPrice, detail.ItemDiscount, detail.IDiscountType,
            detail.ItemDiscount1, detail.IDiscountType1, taxPercent, detail.IsInclusive, 2);
        detail.NetAmount = net;
        detail.TaxAmt = tax;
        detail.Amount = amount;
        detail.StdCustPsize = packSize;

        // ── C46/C30/C37: line value contract ─────────────────────────────────
        var valueError = PoCdnCalc.ValidateLineValueContract(detail);
        if (valueError is not null)
        {
            Fail(valueError);
            return null;
        }

        // ── C38: GL classification required on a non-zero line ───────────────
        if (detail.Amount != 0m && string.IsNullOrWhiteSpace(detail.ItemGlCode))
        {
            detail.ItemGlCode = TruncateOptional(item?.PurchaseGlCode, 20);
            if (string.IsNullOrWhiteSpace(detail.ItemGlCode))
            {
                Fail($"Line {lineNo}: item GL code is required when the line amount is non-zero.");
                return null;
            }
        }

        return detail;
    }

    // ─────────────────────────── Owned VR batch (C7/C20/C43/C45) ───────────────────────────

    private async Task EnsureVrBatchAsync(
        AppDbContext db,
        UserContext context,
        PoCdn cdn,
        DateTime now,
        string uid,
        CancellationToken cancellationToken)
    {
        var stockLines = cdn.Details.Where(d => d.IsStockReturn).ToList();
        var existing = await PoCdnVrLock.LockByVrRefAsync(
            db, _postingRepo, context.CompanyCode!, context.BranchCode!, cdn.DocNo, cancellationToken);

        if (stockLines.Count == 0)
        {
            if (existing is not null
                && string.Equals(existing.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await _posting.DeleteNewStockInBatchInTransactionAsync(
                    db, context.CompanyCode!, context.BranchCode!, existing.BatchNo,
                    IvTrxTypes.VendorReturn, cancellationToken);
            }

            cdn.VrBatchNo = null;
            return;
        }

        if (existing is null)
        {
            var batchNo = await _runningNumbers.GetNextAsync(
                db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
            var batch = new IvTrxBatch
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                BatchNo = batchNo,
                TrxDtTime = cdn.DocDate.Date,
                TrxType = IvTrxTypes.VendorReturn,
                BatchStatus = IvBatchStatuses.New,
                RefNo = PoCdnSpRefs.ToVrRefNo(cdn.DocNo),
                LocationCode = context.LocationCode,
                CreatedDate = now,
                CreatedBy = uid
            };
            AddVrBatchDetails(batch, cdn, stockLines, context.CompanyCode!, context.BranchCode!, batchNo);
            batch.SourceFingerprint = PoCdnCalc.ComputeSourceFingerprint(cdn.Details);
            db.IvTrxBatches.Add(batch);
            cdn.VrBatchNo = batchNo;
            return;
        }

        if (string.Equals(existing.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            // C36: a POSTED VR owned by a NEW CN is an impossible combination for a draft edit.
            throw new InvalidOperationException(
                $"Document {cdn.DocNo} has a POSTED vendor-return batch and cannot be re-drafted.");
        }

        var oldDetails = await _postingRepo.LoadDetailsForBatchAsync(db, existing.Id, cancellationToken);
        db.IvTrxBatchDetails.RemoveRange(oldDetails);
        existing.Details.Clear();
        AddVrBatchDetails(existing, cdn, stockLines, context.CompanyCode!, context.BranchCode!, existing.BatchNo);
        existing.SourceFingerprint = PoCdnCalc.ComputeSourceFingerprint(cdn.Details);
        existing.ModifiedDate = now;
        existing.ModifiedBy = uid;
        cdn.VrBatchNo = existing.BatchNo;
    }

    /// <summary>
    /// C7/C43: a draft may not keep a VR batch that no longer matches its ReturnStock header.
    /// Switching true -> false deletes the NEW batch and clears the linkage.
    /// </summary>
    private async Task SyncVrBatchForDraftAsync(
        AppDbContext db,
        UserContext context,
        PoCdn cdn,
        DateTime now,
        string uid,
        CancellationToken cancellationToken)
    {
        if (cdn.ReturnStock)
        {
            await EnsureVrBatchAsync(db, context, cdn, now, uid, cancellationToken);
            return;
        }

        var existing = await PoCdnVrLock.LockByVrRefAsync(
            db, _postingRepo, context.CompanyCode!, context.BranchCode!, cdn.DocNo, cancellationToken);
        if (existing is null)
        {
            cdn.VrBatchNo = null;
            return;
        }

        if (string.Equals(existing.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Document {cdn.DocNo} has a POSTED vendor-return batch and cannot disable return stock.");
        }

        await _posting.DeleteNewStockInBatchInTransactionAsync(
            db, context.CompanyCode!, context.BranchCode!, existing.BatchNo,
            IvTrxTypes.VendorReturn, cancellationToken);
        cdn.VrBatchNo = null;
    }

    /// <summary>
    /// C20: the VR receives the CN line's standard quantity — no conversion, no aggregation.
    /// C28: no Cost / CostPrice is written; inventory valuation is the posting service's job.
    /// </summary>
    private static void AddVrBatchDetails(
        IvTrxBatch batch,
        PoCdn cdn,
        IReadOnlyList<PoCdnDetail> stockLines,
        string companyCode,
        string branchCode,
        int batchNo)
    {
        short trxLineNo = 1;
        foreach (var row in stockLines)
        {
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = trxLineNo,
                TrxType = IvTrxTypes.VendorReturn,
                ICode = row.ICode ?? string.Empty,
                IDesc = TruncateOptional(row.IDesc, 200),
                ProdCode = row.ICode,
                ProdDesc = TruncateOptional(row.IDesc, 200),
                FromBalLocId = row.FromBalLocId,
                FrWarehouse = row.FrWarehouse,
                FrLocation = row.LocCode,
                FrLotNo = row.LotNo,
                FrStdQty = PoCdnCalc.Qty(row.StdQty),
                FrStdUom = row.StdUom,
                FrPurQty = PoCdnCalc.Qty(row.StdQty),
                IStatus = row.IStatus,
                ExpiryDate = row.ExpiryDate,
                Remarks = TruncateOptional(row.Remarks, 250),
                PoNo = row.PoNo,
                PoRelNo = row.PoRelNo,
                PoLineNo = row.PoLineNo,
                LocationCode = batch.LocationCode
            });
            trxLineNo++;
        }
    }

    // ─────────────────────────── Delete ───────────────────────────

    public async Task<PoCdnOperationResult> DeleteAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoCdnOperationResult.FailValidation("At least one document is required.");
        }

        var write = ValidateWriteContext();
        if (write.Error is not null)
        {
            return PoCdnOperationResult.Fail(write.Error);
        }

        var results = new List<PoCdnPostingItemResult>();
        foreach (var item in items)
        {
            var no = (item.DocNo ?? string.Empty).Trim();
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var cdn = await _cdns.LockForUpdateAsync(
                    db, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
                if (cdn is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(PoCdnPostingItemResult.Failed(no, "Document was not found."));
                    continue;
                }

                if (!await CanAsync(cdn.Type, PermissionCodes.Delete, cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(PoCdnPostingItemResult.Failed(no, "Not authorized."));
                    continue;
                }

                if (!string.Equals(cdn.Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    results.Add(PoCdnPostingItemResult.Failed(no, "Only NEW documents can be deleted."));
                    continue;
                }

                if (item.RowVersion is { Length: > 0 })
                {
                    if (!RowVersionsEqual(cdn.RowVersion, item.RowVersion))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        results.Add(PoCdnPostingItemResult.Failed(no, "Document was changed by another user."));
                        continue;
                    }

                    db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                }

                await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);

                // C7: a NEW draft may own a NEW VR batch — delete it with the document.
                var vrBatch = await PoCdnVrLock.LockByVrRefAsync(
                    db, _postingRepo, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
                if (vrBatch is not null
                    && string.Equals(vrBatch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await _posting.DeleteNewStockInBatchInTransactionAsync(
                        db, write.CompanyCode!, write.BranchCode!, vrBatch.BatchNo,
                        IvTrxTypes.VendorReturn, cancellationToken);
                }

                db.PoCdns.Remove(cdn);
                await db.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                results.Add(new PoCdnPostingItemResult { DocNo = no, Succeeded = true, Outcome = "Deleted" });
            }
            catch (DbUpdateConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                results.Add(PoCdnPostingItemResult.Failed(no, "Document was changed by another user."));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PoCdn delete failed for {DocNo}", no);
                await tx.RollbackAsync(cancellationToken);
                results.Add(PoCdnPostingItemResult.Failed(no, "Unable to delete the document."));
            }
        }

        return PoCdnOperationResult.OkPosting(results);
    }

    // ─────────────────────────── Post / rollback ───────────────────────────

    public async Task<PoCdnOperationResult> PostAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoCdnOperationResult.FailValidation("At least one document is required.");
        }

        if (items.Count > PoCdnLimits.MaxPostSelection)
        {
            return PoCdnOperationResult.FailValidation(
                $"Select at most {PoCdnLimits.MaxPostSelection} documents.");
        }

        var write = ValidateWriteContext();
        if (write.Error is not null)
        {
            return PoCdnOperationResult.Fail(write.Error);
        }

        if (!await CanAsync(PoCdnTypes.CreditNote, PermissionCodes.Post, cancellationToken)
            && !await CanAsync(PoCdnTypes.DebitNote, PermissionCodes.Post, cancellationToken))
        {
            return PoCdnOperationResult.Fail("Not authorized.", PoCdnErrorKind.Authorization);
        }

        var results = new List<PoCdnPostingItemResult>();
        foreach (var item in items)
        {
            results.Add(await PostOneAsync(write, item, cancellationToken));
        }

        return PoCdnOperationResult.OkPosting(results);
    }

    private async Task<PoCdnPostingItemResult> PostOneAsync(
        UserContext write,
        PoCdnKeyedRequest item,
        CancellationToken cancellationToken)
    {
        var no = (item.DocNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var cdn = await _cdns.LockForUpdateAsync(
                db, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Document was not found.");
            }

            // C40: one authoritative POST gate.
            var gate = CanPost(cdn);
            if (gate is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, PoCdnReasonCodes.StatusConflict, gate);
            }

            if (!await CanAsync(cdn.Type, PermissionCodes.Post, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Not authorized.");
            }

            if (item.RowVersion is { Length: > 0 } && !RowVersionsEqual(cdn.RowVersion, item.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, PoCdnReasonCodes.Concurrency, "Document was changed by another user.");
            }

            // C15: a deactivated or suspended vendor does not block posting, but it is reported.
            string? vendorWarning = null;
            var vendor = await db.PoSuppliers.AsNoTracking().FirstOrDefaultAsync(
                x => x.CompanyCode == write.CompanyCode && x.SuppCode == cdn.VendorCode, cancellationToken);
            if (vendor is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Vendor was not found.");
            }

            if (!vendor.IsActive)
            {
                vendorWarning = $"Vendor {vendor.SuppCode} is inactive.";
            }
            else if (vendor.Suspend == true)
            {
                vendorWarning = $"Vendor {vendor.SuppCode} is suspended.";
            }

            // C3/C41: the header reservation is re-checked under the invoice lock.
            await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);
            if (string.Equals(cdn.Type, PoCdnTypes.CreditNote, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cdn.InvNo))
            {
                var invoice = await _invoices.LockForUpdateAsync(
                    db, write.CompanyCode!, write.BranchCode!, cdn.InvNo!, cancellationToken);
                if (invoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoCdnPostingItemResult.Failed(no, $"Invoice {cdn.InvNo} was not found.");
                }

                var others = await _cdns.ListOtherCreditNotesAsync(
                    db, write.CompanyCode!, write.BranchCode!, cdn.InvNo!, no, cancellationToken);
                var eval = PoCdnCalc.EvaluateRemaining(
                    invoice.TotAmnt,
                    others.Select(x => x.TotAmnt).ToList(),
                    cdn.TotAmnt,
                    others.Where(x => x.IsDraft).Select(x => x.DocNo).ToList());
                if (!eval.Ok)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return PoCdnPostingItemResult.Failed(
                        no, PoCdnReasonCodes.RemainingExceeded,
                        eval.Error ?? "Credit note total exceeds the invoice remaining.");
                }
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(write.UserId, 20);

            // C35: the posting service re-validates stock identity and availability under lock.
            // C36/C45: an owned batch is reused only when the fingerprint still matches.
            if (cdn.ReturnStock)
            {
                var stockLines = cdn.Details.Where(d => d.IsStockReturn).ToList();
                var vrBatch = await PoCdnVrLock.LockByVrRefAsync(
                    db, _postingRepo, write.CompanyCode!, write.BranchCode!, no, cancellationToken);

                if (vrBatch is null)
                {
                    var batchNo = await _runningNumbers.GetNextAsync(
                        db, write.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
                    vrBatch = new IvTrxBatch
                    {
                        CompanyCode = write.CompanyCode!,
                        BranchCode = write.BranchCode!,
                        BatchNo = batchNo,
                        TrxDtTime = cdn.DocDate.Date,
                        TrxType = IvTrxTypes.VendorReturn,
                        BatchStatus = IvBatchStatuses.New,
                        RefNo = PoCdnSpRefs.ToVrRefNo(cdn.DocNo),
                        LocationCode = write.LocationCode,
                        CreatedDate = now,
                        CreatedBy = uid
                    };
                    AddVrBatchDetails(vrBatch, cdn, stockLines, write.CompanyCode!, write.BranchCode!, batchNo);
                    vrBatch.SourceFingerprint = PoCdnCalc.ComputeSourceFingerprint(cdn.Details);
                    db.IvTrxBatches.Add(vrBatch);
                    await db.SaveChangesAsync(cancellationToken);
                }
                else if (string.Equals(vrBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    // C36: idempotent retry — reuse only when the intent is unchanged.
                    if (!PoCdnCalc.IsValidFingerprint(vrBatch.SourceFingerprint))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return PoCdnPostingItemResult.Failed(
                            no, PoCdnReasonCodes.FingerprintMissing, "VR batch fingerprint is missing or invalid.");
                    }

                    if (!string.Equals(
                            vrBatch.SourceFingerprint,
                            PoCdnCalc.ComputeSourceFingerprint(cdn.Details),
                            StringComparison.Ordinal))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return PoCdnPostingItemResult.Failed(
                            no, PoCdnReasonCodes.FingerprintMismatch,
                            "Stock lines changed since the last post. Roll back the vendor return first.");
                    }

                    cdn.VrBatchNo = vrBatch.BatchNo;
                    goto SetPosted;
                }
                else
                {
                    var oldDetails = await _postingRepo.LoadDetailsForBatchAsync(db, vrBatch.Id, cancellationToken);
                    db.IvTrxBatchDetails.RemoveRange(oldDetails);
                    vrBatch.Details.Clear();
                    AddVrBatchDetails(vrBatch, cdn, stockLines, write.CompanyCode!, write.BranchCode!, vrBatch.BatchNo);
                    vrBatch.SourceFingerprint = PoCdnCalc.ComputeSourceFingerprint(cdn.Details);
                    vrBatch.ModifiedDate = now;
                    vrBatch.ModifiedBy = uid;
                    await db.SaveChangesAsync(cancellationToken);
                }

                var core = await _posting.PostStockOutInTransactionAsync(
                    db, write.CompanyCode!, write.BranchCode!, write.UserId!, vrBatch.BatchNo,
                    IvTrxTypes.VendorReturn, cancellationToken);
                if (!core.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogError(
                        "PoCdnPostFailed {DocNo} {VrBatchNo} {Error}",
                        no, vrBatch.BatchNo, core.ErrorMessage);
                    return PoCdnPostingItemResult.Failed(
                        no, core.ErrorMessage ?? "Vendor return post failed.");
                }

                TestHookAfterStockOut?.Invoke();
                cdn.VrBatchNo = vrBatch.BatchNo;
            }
            else
            {
                // ReturnStock false: clean up any leftover NEW VR batch owned by this draft.
                var vrBatch = await PoCdnVrLock.LockByVrRefAsync(
                    db, _postingRepo, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
                if (vrBatch is not null)
                {
                    if (string.Equals(vrBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return PoCdnPostingItemResult.Failed(
                            no, PoCdnReasonCodes.VrOrphan,
                            "Unexpected POSTED vendor-return batch exists. Contact administrator.");
                    }

                    await _posting.DeleteNewStockInBatchInTransactionAsync(
                        db, write.CompanyCode!, write.BranchCode!, vrBatch.BatchNo,
                        IvTrxTypes.VendorReturn, cancellationToken);
                }

                cdn.VrBatchNo = null;
            }

        SetPosted:
            cdn.Status = PoCdnStatuses.Posted;
            cdn.PostedDate = now;
            cdn.PostedBy = uid;
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;
            TouchRowVersion(db, cdn);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return PoCdnPostingItemResult.Posted(no, cdn.VrBatchNo, vendorWarning);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnPostingItemResult.Failed(no, PoCdnReasonCodes.Concurrency, "Document was changed by another user.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoCdnPostFailed {DocNo}", no);
            await tx.RollbackAsync(cancellationToken);
            return PoCdnPostingItemResult.Failed(no, "Unable to post the document.");
        }
    }

    public async Task<PoCdnOperationResult> RollbackAsync(
        IReadOnlyList<PoCdnKeyedRequest> items,
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0)
        {
            return PoCdnOperationResult.FailValidation("At least one document is required.");
        }

        if (items.Count > PoCdnLimits.MaxPostSelection)
        {
            return PoCdnOperationResult.FailValidation(
                $"Select at most {PoCdnLimits.MaxPostSelection} documents.");
        }

        var write = ValidateWriteContext();
        if (write.Error is not null)
        {
            return PoCdnOperationResult.Fail(write.Error);
        }

        var results = new List<PoCdnPostingItemResult>();
        foreach (var item in items)
        {
            results.Add(await RollbackOneAsync(write, item, cancellationToken));
        }

        return PoCdnOperationResult.OkPosting(results);
    }

    private async Task<PoCdnPostingItemResult> RollbackOneAsync(
        UserContext write,
        PoCdnKeyedRequest item,
        CancellationToken cancellationToken)
    {
        var no = (item.DocNo ?? string.Empty).Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var cdn = await _cdns.LockForUpdateAsync(
                db, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Document was not found.");
            }

            if (!await CanAsync(cdn.Type, PermissionCodes.Rollback, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Not authorized.");
            }

            // C11: Phase 1 has no accounting and no MyInvois, so an operational rollback is allowed.
            if (!string.Equals(cdn.Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, "Only POSTED documents can be rolled back.");
            }

            if (item.RowVersion is { Length: > 0 } && !RowVersionsEqual(cdn.RowVersion, item.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return PoCdnPostingItemResult.Failed(no, PoCdnReasonCodes.Concurrency, "Document was changed by another user.");
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(write.UserId, 20);

            // C7/C31: roll back only the VR batch owned by this CN, exactly once.
            var vrBatch = await PoCdnVrLock.LockByVrRefAsync(
                db, _postingRepo, write.CompanyCode!, write.BranchCode!, no, cancellationToken);
            if (vrBatch is not null
                && string.Equals(vrBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                var core = await _posting.RollBackStockOutInTransactionAsync(
                    db, write.CompanyCode!, write.BranchCode!, write.UserId!, vrBatch.BatchNo,
                    IvTrxTypes.VendorReturn, cancellationToken);
                if (!core.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    _logger.LogError(
                        "PoCdnRollbackFailed {DocNo} {VrBatchNo} {Error}",
                        no, vrBatch.BatchNo, core.ErrorMessage);
                    return PoCdnPostingItemResult.Failed(
                        no, core.ErrorMessage ?? "Vendor return rollback failed.");
                }

                TestHookAfterStockRollback?.Invoke();
            }

            cdn.Status = PoCdnStatuses.New;
            cdn.PostedDate = null;
            cdn.PostedBy = null;
            cdn.RollbackDate = now;
            cdn.RollbackBy = uid;
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;
            TouchRowVersion(db, cdn);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return PoCdnPostingItemResult.RolledBack(no);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return PoCdnPostingItemResult.Failed(no, PoCdnReasonCodes.Concurrency, "Document was changed by another user.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PoCdnRollbackFailed {DocNo}", no);
            await tx.RollbackAsync(cancellationToken);
            return PoCdnPostingItemResult.Failed(no, "Unable to roll back the document.");
        }
    }

    // ─────────────────────────── Invoice picker / copy ───────────────────────────

    public async Task<PoCdnOperationResult> SearchPostedInvoicesAsync(
        string vendorCode,
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var vendor = (vendorCode ?? string.Empty).Trim();
        if (vendor.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Vendor is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var term = (searchText ?? string.Empty).Trim();

        var query = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.Type == PoInvoiceTypes.Invoice
                        && x.Status == PoInvoiceStatuses.Posted
                        && x.VendorCode == vendor);

        if (term.Length > 0)
        {
            query = query.Where(x =>
                x.DocNo.Contains(term)
                || (x.InvNo != null && x.InvNo.Contains(term)));
        }

        var rows = await query
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Take(50)
            .Select(x => new PoCdnInvoicePickerRow
            {
                InvNo = x.DocNo,
                InvDate = x.DocDate,
                VendorCode = x.VendorCode,
                VendorName = x.VendorName,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        return PoCdnOperationResult.OkInvoicePicker(rows);
    }

    public async Task<PoCdnOperationResult> GetInvoiceLinesAsync(
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Invoice number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await _invoices.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (invoice is null)
        {
            return PoCdnOperationResult.Fail($"Invoice {no} was not found.", PoCdnErrorKind.NotFound);
        }

        // C34/C41: consumption by other NEW/POSTED CNs, so the picker shows the true remaining.
        var consumption = await _cdns.ListSourceLineUsageAsync(
            db, context.CompanyCode!, context.BranchCode!, no, excludeDocNo, cancellationToken);

        var rows = invoice.Details
            .OrderBy(d => d.Line)
            .Select(d =>
            {
                var usedQty = consumption.Where(x => x.InvLineNo == d.Line).Sum(x => x.StdQty);
                var usedAmount = consumption.Where(x => x.InvLineNo == d.Line).Sum(x => x.Amount);
                return new PoCdnInvoiceLinePickerRow
                {
                    Line = d.Line,
                    ICode = d.ICode,
                    IDesc = d.IDesc,
                    Qty = d.Qty,
                    StdQty = d.StdQty,
                    StdCustPSize = d.StdCustPsize,
                    UnitPrice = d.UnitPrice,
                    Amount = d.Amount,
                    NetAmount = d.NetAmount,
                    TaxGroup = d.TaxGroup,
                    IsInclusive = d.IsInclusive,
                    ItemGlCode = d.ItemGlCode,
                    PoNo = d.PoNo,
                    PoRelNo = d.PoRelNo,
                    PoLineNo = d.PoLineNo,
                    RemainingStdQty = PoCdnCalc.Qty(d.StdQty - usedQty),
                    RemainingAmount = PoCdnCalc.Money(d.Amount - usedAmount)
                };
            })
            .ToList();

        return PoCdnOperationResult.OkInvoiceLines(rows);
    }

    public async Task<PoCdnOperationResult> CopyFromInvoiceAsync(
        string invNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Invoice number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await _invoices.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (invoice is null || !string.Equals(invoice.Type, PoInvoiceTypes.Invoice, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(invoice.Status, PoInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return PoCdnOperationResult.Fail("A posted purchase invoice is required.", PoCdnErrorKind.NotFound);
        }

        // C5: this path creates a PoCdn CN. It must never create a PoInvoice.Type=CN quantity correction.
        var document = new PoCdnDocument
        {
            DocNo = string.Empty,
            DocDate = _dates.Today.Date,
            Status = PoCdnStatuses.New,
            Type = PoCdnTypes.CreditNote,
            InvNo = invoice.DocNo,
            ReturnStock = false,
            VendorCode = invoice.VendorCode,
            VendorName = invoice.VendorName,
            Currency = invoice.Currency,
            CurrRate = invoice.CurrRate,
            PayCode = invoice.PayCode,
            TaxGrCode = invoice.TaxGrCode,
            ProjId = invoice.ProjId,
            Dept = invoice.Dept,
            InvAddress1 = invoice.InvAddress1,
            InvAddress2 = invoice.InvAddress2,
            InvAddress3 = invoice.InvAddress3,
            InvAddress4 = invoice.InvAddress4,
            InvCity = invoice.City,
            InvState = invoice.State,
            InvPostalCode = invoice.PostalCode,
            InvCountry = invoice.Country,
            InvTel = invoice.Tel,
            InvFax = invoice.Fax,
            Lines = invoice.Details.OrderBy(d => d.Line).Select(d => new PoCdnLineDto
            {
                Line = d.Line,
                InvLineNo = d.Line,
                IsStockReturn = false,
                ICode = d.ICode,
                IDesc = d.IDesc,
                Qty = d.Qty,
                StdQty = d.StdQty,
                StdCustPSize = d.StdCustPsize,
                SellingUom = d.SellingUom,
                StdUom = d.StdUom,
                UnitPrice = d.UnitPrice,
                ItemDiscount = d.ItemDiscount,
                ItemDiscount1 = d.ItemDiscount1,
                IDiscountType = d.IDiscountType,
                IDiscountType1 = d.IDiscountType1,
                IsInclusive = d.IsInclusive,
                TaxGroup = d.TaxGroup,
                ItemGlCode = d.ItemGlCode,
                PoNo = d.PoNo,
                PoRelNo = d.PoRelNo,
                PoLineNo = d.PoLineNo
            }).ToList()
        };

        return PoCdnOperationResult.OkDocument(document);
    }

    // ─────────────────────────── Reservation reporting ───────────────────────────

    public async Task<PoCdnOperationResult> GetInvoiceReservationsAsync(
        string invNo,
        string? excludeDocNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return PoCdnOperationResult.FailValidation("Invoice number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.PoInvoices.AsNoTracking().FirstOrDefaultAsync(
            x => x.CompanyCode == context.CompanyCode && x.BranchCode == context.BranchCode && x.DocNo == no,
            cancellationToken);
        if (invoice is null)
        {
            return PoCdnOperationResult.Fail($"Invoice {no} was not found.", PoCdnErrorKind.NotFound);
        }

        var rows = await _cdns.ListOtherCreditNotesAsync(
            db, context.CompanyCode!, context.BranchCode!, no, excludeDocNo, cancellationToken);

        var posted = PoCdnCalc.SumMoney(rows.Where(x => !x.IsDraft).Select(x => x.TotAmnt));
        var draft = PoCdnCalc.SumMoney(rows.Where(x => x.IsDraft).Select(x => x.TotAmnt));
        var remaining = PoCdnCalc.Money(invoice.TotAmnt - posted - draft);

        var details = await db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.InvNo == no
                        && (x.Status == PoCdnStatuses.New || x.Status == PoCdnStatuses.Posted))
            .Where(x => excludeDocNo == null || x.DocNo != excludeDocNo)
            .Select(x => new PoCdnReservationRowDto
            {
                DocNo = x.DocNo,
                Status = x.Status,
                TotAmnt = x.TotAmnt,
                DocDate = x.DocDate,
                SupplierDocNo = x.SupplierDocNo
            })
            .ToListAsync(cancellationToken);

        return PoCdnOperationResult.OkReservations(new PoCdnInvoiceReservationSummary
        {
            InvNo = invoice.DocNo,
            InvDate = invoice.DocDate,
            InvoiceTotal = invoice.TotAmnt,
            PostedCnTotal = posted,
            DraftCnTotal = draft,
            Remaining = remaining,
            OverReserved = remaining < 0m,
            Rows = details
        });
    }

    public async Task<PoCdnOperationResult> GetReservationReportAsync(
        PoCdnReservationReportQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return PoCdnOperationResult.Fail(context.Error);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var take = Math.Clamp(query.Take <= 0 ? 50 : query.Take, 1, 200);

        var invoices = db.PoInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.Type == PoInvoiceTypes.Invoice
                        && x.Status == PoInvoiceStatuses.Posted);

        if (!string.IsNullOrWhiteSpace(query.InvNo))
        {
            var term = query.InvNo.Trim();
            invoices = invoices.Where(x => x.DocNo.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(query.VendorCode))
        {
            var vendor = query.VendorCode.Trim();
            invoices = invoices.Where(x => x.VendorCode == vendor);
        }

        var cnTotals = await db.PoCdns.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.Type == PoCdnTypes.CreditNote
                        && x.InvNo != null
                        && (x.Status == PoCdnStatuses.New || x.Status == PoCdnStatuses.Posted))
            .GroupBy(x => new { x.InvNo, x.Status })
            .Select(g => new
            {
                g.Key.InvNo,
                g.Key.Status,
                Total = g.Sum(x => x.TotAmnt),
                Count = g.Count()
            })
            .ToListAsync(cancellationToken);

        var candidates = await invoices
            .OrderByDescending(x => x.DocDate)
            .ThenByDescending(x => x.DocNo)
            .Take(take * 4)
            .Select(x => new
            {
                x.DocNo,
                x.DocDate,
                x.VendorCode,
                x.VendorName,
                x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        var rows = new List<PoCdnReservationReportRow>();
        foreach (var inv in candidates)
        {
            var posted = PoCdnCalc.SumMoney(cnTotals
                .Where(t => string.Equals(t.InvNo, inv.DocNo, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Total));
            var draft = PoCdnCalc.SumMoney(cnTotals
                .Where(t => string.Equals(t.InvNo, inv.DocNo, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Total));
            var remaining = PoCdnCalc.Money(inv.TotAmnt - posted - draft);
            var draftCount = cnTotals
                .Where(t => string.Equals(t.InvNo, inv.DocNo, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(t.Status, PoCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
                .Sum(t => t.Count);

            rows.Add(new PoCdnReservationReportRow
            {
                InvNo = inv.DocNo,
                InvDate = inv.DocDate,
                VendorCode = inv.VendorCode,
                VendorName = inv.VendorName,
                InvoiceTotal = inv.TotAmnt,
                PostedCnTotal = posted,
                DraftCnTotal = draft,
                Remaining = remaining,
                DraftCount = draftCount,
                OverReserved = remaining < 0m
            });
        }

        // Over-reserved first — those are the rows the report exists to surface.
        var ordered = rows
            .OrderByDescending(x => x.OverReserved)
            .ThenBy(x => x.Remaining)
            .ThenBy(x => x.InvNo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (query.OverReservedOnly)
        {
            ordered = ordered.Where(x => x.OverReserved).ToList();
        }

        return PoCdnOperationResult.OkReservationReport(new PoCdnReservationReportPage
        {
            TotalCount = ordered.Count,
            Rows = ordered.Take(take).ToList()
        });
    }
}

