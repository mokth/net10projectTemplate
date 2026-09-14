using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed class SaCdnService : ISaCdnService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly IRunningNumberService _runningNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ISaCdnRepository _cdns;
    private readonly ISaInvoiceRepository _invoices;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvInventoryPostingService _posting;
    private readonly ILogger<SaCdnService> _logger;

    /// <summary>Test-only: invoked after CR stock-in succeeds, before CN is marked POSTED.</summary>
    internal Action? TestHookAfterStockIn { get; set; }

    /// <summary>Test-only: invoked after CR stock-in rollback succeeds, before CN is marked NEW.</summary>
    internal Action? TestHookAfterStockRollback { get; set; }

    public SaCdnService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        IRunningNumberService runningNumbers,
        ICurrentDateService dates,
        ISaCdnRepository cdns,
        ISaInvoiceRepository invoices,
        IIvStockPostingRepository postingRepo,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvInventoryPostingService posting,
        ILogger<SaCdnService> logger)
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

    // ─────────────────────────── Lookups ───────────────────────────

    public async Task<SaCdnOperationResult> GetLookupsAsync(
        string type,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(type, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        var items = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var warehouses = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!, context.BranchCode!, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customers = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustCode)
            .Select(x => new SaCdnCustomerLookupRow
            {
                CustCode = x.CustCode,
                CustName = x.CustName,
                Currency = x.Currency,
                DiscountMethod = x.DiscountMethod,
                DecPoint = x.DecPoint
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new SaCdnTaxGroupLookupRow
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

        // Department / Project masters — company + branch scoped, active only.
        var departments = await db.MsDepts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.IsActive)
            .OrderBy(x => x.DeptCode)
            .Select(x => new IvCodeLookupRow { Code = x.DeptCode, Desc = x.DeptName })
            .ToListAsync(cancellationToken);

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new IvCodeLookupRow { Code = x.ProjCode, Desc = x.ProjName })
            .ToListAsync(cancellationToken);

        return SaCdnOperationResult.OkLookups(
            items.Select(x => new SaCdnItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                StdUom = x.StdUom,
                StdPackSize = x.StdPackSize,
                SellingPrice = x.SellingPrice,
                SellingGlCode = x.SellingGlCode,
                TaxGroup = x.TaxGroup,
                StockControl = x.StockControl,
                LotControl = x.LotControl,
                DefWarehouse = x.DefWarehouse,
                DefLocation = x.DefLocation
            }).ToList(),
            warehouses.Select(x => new IvWarehouseLookupRow
            {
                WarehouseCode = x.WarehouseCode,
                WarehouseDesc = x.WarehouseDesc
            }).ToList(),
            customers,
            taxGroups,
            payCodes,
            departments,
            projects);
    }

    // ─────────────────────────── Customer defaults ───────────────────────────

    public async Task<SaCdnOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime docDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var code = (custCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return SaCdnOperationResult.FailValidation("Customer is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["CustCode"] = "Customer is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == context.CompanyCode && x.CustCode == code, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            return SaCdnOperationResult.Fail("Customer was not found or is inactive.", SaCdnErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(customer.Currency)
            ? SaInvoiceCalc.HomeCurrency
            : customer.Currency.Trim();
        var date = docDate == default ? _dates.Today.Date : docDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, date, cancellationToken);

        var shipToAddresses = await db.SaCustAdds.AsNoTracking()
            .Where(a => a.CompanyCode == context.CompanyCode && a.CustCode == code)
            .OrderBy(a => a.Line)
            .Select(a => new SaCustAddressVm
            {
                Line = a.Line,
                AddName = a.AddName,
                DeliverTo = a.DeliverTo,
                Address1 = a.Address1,
                Address2 = a.Address2,
                Address3 = a.Address3,
                City = a.City,
                State = a.State,
                PostalCode = a.PostalCode,
                Country = a.Country,
                Tel = a.Tel,
                Fax = a.Fax
            })
            .ToListAsync(cancellationToken);

        var useMainInv = customer.AppInvoice == true;

        return SaCdnOperationResult.OkDefaults(new SaCdnCustomerDefaults
        {
            CustCode = customer.CustCode,
            CustName = customer.CustName ?? string.Empty,
            Currency = currency,
            CurrRate = rateResult.Rate,
            CurrRateValid = rateResult.Error is null,
            PayCode = customer.PayCode,
            TaxGrCode = customer.TaxGrCode,
            Taxable = customer.Taxable,
            SalesmanCode = customer.SalesmanCode,
            DiscountMethod = customer.DiscountMethod,
            DecPoint = customer.DecPoint,
            InvName = useMainInv ? customer.CustName : null,
            InvAddress1 = useMainInv ? customer.Address1 : null,
            InvAddress2 = useMainInv ? customer.Address2 : null,
            InvAddress3 = useMainInv ? customer.Address3 : null,
            InvAddress4 = useMainInv ? customer.Address4 : null,
            InvCity = useMainInv ? customer.City : null,
            InvState = useMainInv ? customer.State : null,
            InvPostalCode = useMainInv ? customer.PostalCode : null,
            InvCountry = useMainInv ? customer.Country : null,
            InvTel = useMainInv ? customer.Tel : null,
            InvFax = useMainInv ? customer.Fax : null,
            ShipToAddresses = shipToAddresses
        });
    }

    // ─────────────────────────── Currency rate ───────────────────────────

    public async Task<SaCdnOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var curr = (currency ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(curr))
        {
            return SaCdnOperationResult.FailValidation("Currency is required.");
        }

        var date = docDate == default ? _dates.Today.Date : docDate.Date;
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rateResult = await ResolveCurrRateAsync(db, curr, date, cancellationToken);
        return SaCdnOperationResult.OkRate(rateResult.Rate, rateResult.Error is null);
    }

    // ─────────────────────────── Search ───────────────────────────

    public async Task<SaCdnOperationResult> SearchAsync(
        SaCdnListQuery? query,
        CancellationToken cancellationToken = default)
    {
        if (query is null)
        {
            return SaCdnOperationResult.FailValidation("Query is required.");
        }

        var docType = (query.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (docType != SaCdnTypes.CreditNote && docType != SaCdnTypes.DebitNote)
        {
            return SaCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(docType, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _cdns.SearchPagedAsync(db, context.CompanyCode!, context.BranchCode!,
            new SaCdnSearchArgs(
                docType,
                query.SearchText,
                query.Status,
                query.DateFrom,
                query.DateTo,
                query.SortField,
                query.SortDescending,
                query.Skip,
                query.Take),
            cancellationToken);

        var listRows = rows.Select(x => new SaCdnListRow
        {
            DocNo = x.DocNo,
            DocDate = x.DocDate,
            Status = x.Status,
            Type = x.Type,
            CustCode = x.CustCode,
            CustName = x.CustName,
            InvNo = x.InvNo,
            TotAmnt = x.TotAmnt,
            LineCount = x.Details?.Count ?? 0,
            CreatedDate = x.CreatedDate,
            CreatedBy = x.CreatedBy,
            RowVersion = x.RowVersion
        }).ToList();

        return SaCdnOperationResult.OkList(new SaCdnListPage { Rows = listRows, TotalCount = total });
    }

    // ─────────────────────────── Get ───────────────────────────

    public async Task<SaCdnOperationResult> GetAsync(
        string docNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var no = (docNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaCdnOperationResult.Fail("Document number is required.", SaCdnErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var cdn = await _cdns.GetWithDetailsAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (cdn is null)
        {
            return SaCdnOperationResult.Fail("Document was not found.", SaCdnErrorKind.NotFound);
        }

        if (!await CanAsync(cdn.Type, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        return SaCdnOperationResult.OkDocument(MapDocument(cdn));
    }

    // ─────────────────────────── SaveNew ───────────────────────────

    public async Task<SaCdnOperationResult> SaveNewAsync(
        SaCdnSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaCdnOperationResult.FailValidation("Save request is required.");
        }

        var docType = (request.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (docType != SaCdnTypes.CreditNote && docType != SaCdnTypes.DebitNote)
        {
            return SaCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(docType, PermissionCodes.Add, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        // DN: force ReturnStock=false
        if (docType == SaCdnTypes.DebitNote)
        {
            request.ReturnStock = false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var prepared = await PrepareLinesAsync(db, request, docType,
                context.CompanyCode!, context.BranchCode!, excludeDocNo: null, cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            // CN with InvNo: validate remaining
            if (docType == SaCdnTypes.CreditNote && !string.IsNullOrWhiteSpace(request.InvNo))
            {
                var invNo = request.InvNo.Trim();
                var locked = await SaCdnLockOrder.AcquireAsync(
                    db, _invoices, _cdns,
                    context.CompanyCode!, context.BranchCode!,
                    invNo, null, cancellationToken);

                if (locked.Invoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail(
                        $"Invoice {invNo} was not found.", SaCdnErrorKind.NotFound);
                }

                if (!string.Equals(locked.Invoice.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail(
                        $"Invoice {invNo} must be POSTED before a Credit Note can be created.");
                }

                if (!string.Equals(locked.Invoice.CompanyCode, context.CompanyCode, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(locked.Invoice.BranchCode, context.BranchCode, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail("Invoice does not belong to this tenant.");
                }

                var otherRows = await _cdns.ListOtherCreditNotesAsync(
                    db, context.CompanyCode!, context.BranchCode!, invNo, excludeDocNo: null, cancellationToken);
                var decPoint = prepared.Customer!.DecPoint == true;
                var candidateTotal = SaCdnCalc.MoneyNormalize(prepared.TotAmnt, decPoint);
                var eval = SaCdnCalc.EvaluateRemaining(
                    locked.Invoice.TotAmnt,
                    otherRows.Select(x => x.TotAmnt).ToList(),
                    candidateTotal,
                    decPoint,
                    otherRows.Where(x => x.IsDraft).Select(x => x.DocNo).ToList());
                if (!eval.Ok)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail(
                        eval.Error ?? "Credit note total exceeds invoice remaining.",
                        SaCdnErrorKind.BusinessRule);
                }
            }

            // Validate ReturnStock gates
            var rsError = ValidateReturnStockGates(request, prepared.Lines!);
            if (rsError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(rsError, SaCdnErrorKind.Validation);
            }

            var docDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            var candidate = new SaCdn
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                DocNo = "CANDIDATE",
                DocDate = docDate,
                Status = SaCdnStatuses.New,
                Type = docType,
                CustCode = prepared.Customer!.CustCode,
                TaxGrCode = TruncateOptional(request.TaxGrCode, 20),
                SalesRep = TruncateOptional(request.SalesmanCode, 20),
                Taxes = prepared.Taxes,
                TotAmnt = prepared.TotAmnt
            };
            AddDetails(candidate, prepared.Lines!);
            var readiness = await ValidateCommercialReadinessAsync(
                db, candidate, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.FailValidation("Validation failed.", readiness.ToValidationErrors());
            }

            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db, docType, "", docDate, DocumentNumberRequestMode.New, "AUTO", cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(
                    $"{docType} numbering is not configured for this company/branch.",
                    SaCdnErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(
                    $"{docType} numbering is not configured correctly. Contact an administrator.",
                    SaCdnErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(
                    $"The next {docType} number exceeds the configured length.",
                    SaCdnErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(
                    $"The {docType} could not be saved because of a database conflict. Try again.",
                    SaCdnErrorKind.Unexpected);
            }

            var docNo = issued.DocumentNumber;
            var prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim();
            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);

            var cdn = new SaCdn
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                DocNo = docNo,
                DocDate = docDate,
                Status = SaCdnStatuses.New,
                Type = docType,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                InvNo = docType == SaCdnTypes.CreditNote ? Norm(request.InvNo) : null,
                DoNo = Norm(request.DoNo),
                ReturnStock = docType == SaCdnTypes.CreditNote && request.ReturnStock,
                Prefix = prefix,
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                PayCode = TruncateOptional(request.PayCode, 20),
                TaxGrCode = TruncateOptional(request.TaxGrCode, 20),
                SalesRep = TruncateOptional(request.SalesmanCode, 20),
                Dept = TruncateOptional(request.Dept, 20),
                ProjId = TruncateOptional(request.ProjId, 20),
                Remarks = TruncateOptional(request.Remarks, 500),
                RefNo = TruncateOptional(request.RefNo, 50),
                ExternalDocNo = TruncateOptional(request.ExternalDocNo, 50),
                InvAddress1 = UpperSnapshot(request.InvAddress1, 100),
                InvAddress2 = UpperSnapshot(request.InvAddress2, 100),
                InvAddress3 = UpperSnapshot(request.InvAddress3, 100),
                InvAddress4 = UpperSnapshot(request.InvAddress4, 100),
                City = UpperSnapshot(request.InvCity, 50),
                State = UpperSnapshot(request.InvState, 50),
                PostalCode = UpperSnapshot(request.InvPostalCode, 20),
                Country = UpperSnapshot(request.InvCountry, 50),
                Tel = TruncateOptional(request.InvTel, 50),
                Fax = TruncateOptional(request.InvFax, 50),
                CreatedDate = now,
                CreatedBy = uid
            };

            ApplyCalculatedTotals(cdn, prepared.Lines!, prepared.Customer.DecPoint == true);
            TouchRowVersion(db, cdn);
            db.SaCdns.Add(cdn);
            AddDetails(cdn, prepared.Lines!);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "CDN saved. UserId={UserId} Company={Company} DocNo={DocNo} Type={Type}",
                context.UserId, context.CompanyCode, docNo, docType);

            return await GetAsync(docNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail("Document number is already used.", SaCdnErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "CDN save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail(
                "The document could not be saved because of a database conflict. Try again.",
                SaCdnErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CDN save failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail("Unable to save the document.", SaCdnErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Update ───────────────────────────

    public async Task<SaCdnOperationResult> UpdateAsync(
        string docNo,
        SaCdnSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaCdnOperationResult.FailValidation("Save request is required.");
        }

        var no = (docNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaCdnOperationResult.FailValidation("Document number is required.");
        }

        var docType = (request.Type ?? string.Empty).Trim().ToUpperInvariant();
        if (docType != SaCdnTypes.CreditNote && docType != SaCdnTypes.DebitNote)
        {
            return SaCdnOperationResult.FailValidation("Type must be CN or DN.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(docType, PermissionCodes.Edit, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.",
                SaCdnErrorKind.Concurrency);
        }

        if (docType == SaCdnTypes.DebitNote)
        {
            request.ReturnStock = false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var locked = await SaCdnLockOrder.AcquireAsync(
                db, _invoices, _cdns,
                context.CompanyCode!, context.BranchCode!,
                docType == SaCdnTypes.CreditNote ? Norm(request.InvNo) : null,
                no, cancellationToken);

            var cdn = locked.Cdn;
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail("Document was not found.", SaCdnErrorKind.NotFound);
            }

            if (!string.Equals(cdn.Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail("Only NEW documents can be edited.", SaCdnErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(cdn.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(
                    "This document was changed by another user. Reload before saving.",
                    SaCdnErrorKind.Concurrency);
            }

            db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);

            // CN InvNo validation
            if (docType == SaCdnTypes.CreditNote && !string.IsNullOrWhiteSpace(request.InvNo))
            {
                var invNo = request.InvNo.Trim();
                if (locked.Invoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail($"Invoice {invNo} was not found.", SaCdnErrorKind.NotFound);
                }

                if (!string.Equals(locked.Invoice.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail($"Invoice {invNo} must be POSTED.");
                }
            }

            var prepared = await PrepareLinesAsync(db, request, docType,
                context.CompanyCode!, context.BranchCode!, excludeDocNo: no, cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            // Remaining check for CN with InvNo
            if (docType == SaCdnTypes.CreditNote && !string.IsNullOrWhiteSpace(request.InvNo) && locked.Invoice is not null)
            {
                var otherRows = await _cdns.ListOtherCreditNotesAsync(
                    db, context.CompanyCode!, context.BranchCode!, locked.Invoice.InvNo ?? string.Empty, no, cancellationToken);
                var decPoint = prepared.Customer!.DecPoint == true;
                var eval = SaCdnCalc.EvaluateRemaining(
                    locked.Invoice.TotAmnt,
                    otherRows.Select(x => x.TotAmnt).ToList(),
                    prepared.TotAmnt,
                    decPoint,
                    otherRows.Where(x => x.IsDraft).Select(x => x.DocNo).ToList());
                if (!eval.Ok)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail(eval.Error ?? "CN total exceeds invoice remaining.");
                }
            }

            var rsError = ValidateReturnStockGates(request, prepared.Lines!);
            if (rsError is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.Fail(rsError, SaCdnErrorKind.Validation);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            cdn.DocDate = request.DocDate == default ? _dates.Today.Date : request.DocDate.Date;
            cdn.CustCode = prepared.Customer!.CustCode;
            cdn.CustName = UpperSnapshot(prepared.Customer.CustName, 200);
            cdn.InvNo = docType == SaCdnTypes.CreditNote ? Norm(request.InvNo) : null;
            cdn.DoNo = Norm(request.DoNo);
            cdn.ReturnStock = docType == SaCdnTypes.CreditNote && request.ReturnStock;
            cdn.Currency = prepared.Currency;
            cdn.CurrRate = prepared.CurrRate;
            cdn.PayCode = TruncateOptional(request.PayCode, 20);
            cdn.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
            cdn.SalesRep = TruncateOptional(request.SalesmanCode, 20);
            cdn.Dept = TruncateOptional(request.Dept, 20);
            cdn.ProjId = TruncateOptional(request.ProjId, 20);
            cdn.Remarks = TruncateOptional(request.Remarks, 500);
            cdn.RefNo = TruncateOptional(request.RefNo, 50);
            cdn.ExternalDocNo = TruncateOptional(request.ExternalDocNo, 50);
            cdn.InvAddress1 = UpperSnapshot(request.InvAddress1, 100);
            cdn.InvAddress2 = UpperSnapshot(request.InvAddress2, 100);
            cdn.InvAddress3 = UpperSnapshot(request.InvAddress3, 100);
            cdn.InvAddress4 = UpperSnapshot(request.InvAddress4, 100);
            cdn.City = UpperSnapshot(request.InvCity, 50);
            cdn.State = UpperSnapshot(request.InvState, 50);
            cdn.PostalCode = UpperSnapshot(request.InvPostalCode, 20);
            cdn.Country = UpperSnapshot(request.InvCountry, 50);
            cdn.Tel = TruncateOptional(request.InvTel, 50);
            cdn.Fax = TruncateOptional(request.InvFax, 50);
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;

            ApplyCalculatedTotals(cdn, prepared.Lines!, prepared.Customer.DecPoint == true);
            db.SaCdnDetails.RemoveRange(cdn.Details);
            cdn.Details.Clear();
            AddDetails(cdn, prepared.Lines!);

            var readiness = await ValidateCommercialReadinessAsync(
                db, cdn, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnOperationResult.FailValidation("Validation failed.", readiness.ToValidationErrors());
            }

            TouchRowVersion(db, cdn);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail(
                "This document was changed by another user. Reload before saving.",
                SaCdnErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "CDN update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail(
                "The document could not be saved because of a database conflict. Try again.",
                SaCdnErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CDN update failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail("Unable to save the document.", SaCdnErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Delete ───────────────────────────

    public async Task<SaCdnOperationResult> DeleteAsync(
        IReadOnlyList<SaCdnKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaCdnOperationResult.Fail("Select at least one document.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var item in keyed)
            {
                var no = item.DocNo;

                // Invoice → CN lock order (peek InvNo without UPDLOCK first).
                var invNo = await PeekInvNoAsync(
                    db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
                var locked = await SaCdnLockOrder.AcquireAsync(
                    db, _invoices, _cdns,
                    context.CompanyCode!, context.BranchCode!,
                    invNo, no, cancellationToken);
                var cdn = locked.Cdn;
                if (cdn is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail($"Document {no} was not found.", SaCdnErrorKind.NotFound);
                }

                if (!await CanAsync(cdn.Type, PermissionCodes.Delete, cancellationToken))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
                }

                if (!string.Equals(cdn.Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail($"Document {no} is not NEW and cannot be deleted.");
                }

                if (!RowVersionsEqual(cdn.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnOperationResult.Fail(
                        $"Document {no} was changed by another user.",
                        SaCdnErrorKind.Concurrency);
                }

                // Check for CR batch
                var crBatch = await SaCdnCrLock.LockByCnRefAsync(
                    db, _postingRepo, context.CompanyCode!, context.BranchCode!, no, cancellationToken);

                if (crBatch is not null)
                {
                    if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SaCdnOperationResult.Fail(
                            $"Document {no} has a POSTED stock return batch and cannot be deleted.",
                            SaCdnErrorKind.BusinessRule);
                    }

                    if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
                    {
                        await _posting.DeleteNewStockInBatchInTransactionAsync(
                            db, context.CompanyCode!, context.BranchCode!,
                            crBatch.BatchNo, IvTrxTypes.CustomerReturn, cancellationToken);
                    }
                }

                await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);
                db.SaCdnDetails.RemoveRange(cdn.Details);
                db.SaCdns.Remove(cdn);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaCdnOperationResult.Ok();
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "CDN delete deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail(
                "The document could not be deleted because of a database conflict. Try again.",
                SaCdnErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CDN delete failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaCdnOperationResult.Fail("Unable to delete the document.", SaCdnErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Post ───────────────────────────

    public async Task<SaCdnOperationResult> PostAsync(
        IReadOnlyList<SaCdnKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaCdnOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaCdnLimits.MaxPostSelection)
        {
            return SaCdnOperationResult.Fail($"Select at most {SaCdnLimits.MaxPostSelection} documents.");
        }

        var results = new List<SaCdnPostingItemResult>();
        var stop = false;
        foreach (var item in keyed)
        {
            if (stop)
            {
                results.Add(SaCdnPostingItemResult.NotAttempted(item.DocNo));
                continue;
            }

            try
            {
                var one = await PostOneAsync(context, item.DocNo, item.RowVersion, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CDN POST failed for {DocNo}", item.DocNo);
                results.Add(SaCdnPostingItemResult.Failed(item.DocNo, ex.Message));
                stop = true;
            }
        }

        return SaCdnOperationResult.OkPosting(results);
    }

    // ─────────────────────────── Rollback ───────────────────────────

    public async Task<SaCdnOperationResult> RollbackAsync(
        IReadOnlyList<SaCdnKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaCdnOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaCdnLimits.MaxPostSelection)
        {
            return SaCdnOperationResult.Fail($"Select at most {SaCdnLimits.MaxPostSelection} documents.");
        }

        var results = new List<SaCdnPostingItemResult>();
        var stop = false;
        foreach (var item in keyed)
        {
            if (stop)
            {
                results.Add(SaCdnPostingItemResult.NotAttempted(item.DocNo));
                continue;
            }

            try
            {
                var one = await RollbackOneAsync(context, item.DocNo, item.RowVersion, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CDN ROLLBACK failed for {DocNo}", item.DocNo);
                results.Add(SaCdnPostingItemResult.Failed(item.DocNo, ex.Message));
                stop = true;
            }
        }

        return SaCdnOperationResult.OkPosting(results);
    }

    // ─────────────────────────── Invoice picker ───────────────────────────

    public async Task<SaCdnOperationResult> SearchPostedInvoicesAsync(
        string? custCode,
        string? searchText,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(SaCdnTypes.CreditNote, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.Status == SaInvoiceStatuses.Posted);

        var cust = (custCode ?? string.Empty).Trim();
        if (cust.Length > 0)
        {
            query = query.Where(x => x.CustCode == cust);
        }

        var term = (searchText ?? string.Empty).Trim();
        if (term.Length > 0)
        {
            query = query.Where(x =>
                x.InvNo.Contains(term)
                || (x.CustCode != null && x.CustCode.Contains(term))
                || (x.CustName != null && x.CustName.Contains(term)));
        }

        var rows = await query
            .OrderByDescending(x => x.InvDate)
            .ThenByDescending(x => x.InvNo)
            .Take(50)
            .Select(x => new SaCdnInvoicePickerRow
            {
                InvNo = x.InvNo,
                InvDate = x.InvDate,
                CustCode = x.CustCode,
                CustName = x.CustName,
                TotAmnt = x.TotAmnt
            })
            .ToListAsync(cancellationToken);

        return SaCdnOperationResult.OkInvoicePicker(rows);
    }

    // ─────────────────────────── Copy from invoice ───────────────────────────

    // ─────────────────────────── R9 / E7 reservation reporting ───────────────────────────

    public async Task<SaCdnOperationResult> GetInvoiceReservationsAsync(
        string invNo,
        string? excludeDocNo = null,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(SaCdnTypes.CreditNote, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaCdnOperationResult.FailValidation("Invoice number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.InvNo == no)
            .Select(x => new { x.InvNo, x.TotAmnt })
            .FirstOrDefaultAsync(cancellationToken);
        if (invoice is null)
        {
            return SaCdnOperationResult.Fail($"Invoice {no} was not found.", SaCdnErrorKind.NotFound);
        }

        var rows = await _cdns.ListOtherCreditNotesAsync(
            db, context.CompanyCode!, context.BranchCode!, no, excludeDocNo, cancellationToken);
        return SaCdnOperationResult.OkReservations(BuildReservationSummary(no, invoice.TotAmnt, rows));
    }

    public async Task<SaCdnOperationResult> GetReservationReportAsync(
        SaCdnReservationReportQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(SaCdnTypes.CreditNote, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        query ??= new SaCdnReservationReportQuery();
        var take = query.Take <= 0 ? 200 : Math.Min(query.Take, 500);
        var custFilter = string.IsNullOrWhiteSpace(query.CustCode) ? null : query.CustCode.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Report-only: never locks and never mutates.
        var cnRows = await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.Type == SaCdnTypes.CreditNote
                && x.InvNo != null && x.InvNo != string.Empty
                && (x.Status == SaCdnStatuses.New || x.Status == SaCdnStatuses.Posted)
                && (custFilter == null || x.CustCode == custFilter))
            .Select(x => new { InvNo = x.InvNo!, x.DocNo, x.Status, x.TotAmnt })
            .ToListAsync(cancellationToken);

        if (cnRows.Count == 0)
        {
            return SaCdnOperationResult.OkReservationReport(new SaCdnReservationReportPage());
        }

        var invNos = cnRows.Select(x => x.InvNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var invoices = await db.SaInvoices.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.Status == SaInvoiceStatuses.Posted
                && invNos.Contains(x.InvNo))
            .Select(x => new { x.InvNo, x.InvDate, x.CustCode, x.CustName, x.TotAmnt })
            .ToListAsync(cancellationToken);
        var invoiceByNo = invoices.ToDictionary(x => x.InvNo, StringComparer.OrdinalIgnoreCase);

        var rows = new List<SaCdnReservationReportRow>();
        foreach (var group in cnRows.GroupBy(x => x.InvNo, StringComparer.OrdinalIgnoreCase))
        {
            if (!invoiceByNo.TryGetValue(group.Key, out var invoice))
            {
                continue;
            }

            var drafts = group
                .Where(x => string.Equals(x.Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var postedTotal = SaInvoiceCalc.Money(group.Except(drafts).Sum(x => x.TotAmnt));
            var draftTotal = SaInvoiceCalc.Money(drafts.Sum(x => x.TotAmnt));
            var draftNos = drafts
                .Select(x => x.DocNo)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (query.DraftsOnly && draftNos.Count == 0)
            {
                continue;
            }

            if (query.OverReservedOnly && postedTotal + draftTotal <= SaInvoiceCalc.Money(invoice.TotAmnt))
            {
                continue;
            }

            rows.Add(new SaCdnReservationReportRow
            {
                InvNo = invoice.InvNo,
                InvDate = invoice.InvDate,
                CustCode = invoice.CustCode,
                CustName = invoice.CustName,
                InvoiceTotal = SaInvoiceCalc.Money(invoice.TotAmnt),
                PostedCnTotal = postedTotal,
                DraftCnTotal = draftTotal,
                Remaining = SaInvoiceCalc.Money(invoice.TotAmnt - postedTotal - draftTotal),
                DraftCnNos = draftNos
            });
        }

        var ordered = rows
            .OrderByDescending(x => x.OverReserved)
            .ThenBy(x => x.InvNo, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return SaCdnOperationResult.OkReservationReport(new SaCdnReservationReportPage
        {
            Rows = ordered.Take(take).ToList(),
            TotalCount = ordered.Count
        });
    }

    private static SaCdnInvoiceReservationSummary BuildReservationSummary(
        string invNo,
        decimal invoiceTotal,
        IReadOnlyList<SaCdnReservationRow> rows)
    {
        var posted = SaInvoiceCalc.Money(rows.Where(x => !x.IsDraft).Sum(x => x.TotAmnt));
        var draft = SaInvoiceCalc.Money(rows.Where(x => x.IsDraft).Sum(x => x.TotAmnt));
        return new SaCdnInvoiceReservationSummary
        {
            InvNo = invNo,
            InvoiceTotal = SaInvoiceCalc.Money(invoiceTotal),
            PostedCnTotal = posted,
            DraftCnTotal = draft,
            Remaining = SaInvoiceCalc.Money(invoiceTotal - posted - draft),
            DraftCnNos = rows
                .Where(x => x.IsDraft)
                .Select(x => x.DocNo)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Reservations = rows
                .Select(x => new SaCdnReservationLine { DocNo = x.DocNo, Status = x.Status, TotAmnt = x.TotAmnt })
                .ToList()
        };
    }

    public async Task<SaCdnOperationResult> CopyFromInvoiceAsync(
        string invNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaCdnOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(SaCdnTypes.CreditNote, PermissionCodes.Access, cancellationToken))
        {
            return SaCdnOperationResult.Fail("Not authorized.", SaCdnErrorKind.Authorization);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaCdnOperationResult.FailValidation("Invoice number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await _invoices.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (invoice is null)
        {
            return SaCdnOperationResult.Fail("Invoice was not found.", SaCdnErrorKind.NotFound);
        }

        if (!string.Equals(invoice.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
        {
            return SaCdnOperationResult.Fail("Only POSTED invoices can be used for credit note copy.");
        }

        // Map invoice lines to CDN lines (stock fields empty, ReturnStock=false)
        var lines = invoice.Details.OrderBy(x => x.Line).Select((d, i) => new SaCdnLineDto
        {
            Line = i + 1,
            ICode = d.ICode ?? string.Empty,
            IDesc = d.IDesc,
            Qty = d.Qty,
            StdQty = d.StdQty,
            StdCustPsize = 0m,
            StdUom = d.StdUom,
            UnitPrice = d.UnitPrice,
            Amount = d.Amount,
            ItemDiscount = d.ItemDiscount,
            ItemDiscount2 = d.ItemDiscount2,
            ItemDiscount3 = d.ItemDiscount3,
            ItemDiscount4 = d.ItemDiscount4,
            ItemDiscount5 = d.ItemDiscount5,
            ItemDiscount6 = d.ItemDiscount6,
            ItemDiscAmount = d.ItemDiscAmount,
            IsInclusive = d.IsInclusive,
            TaxGrCode = d.TaxGrCode,
            TaxAmt = d.TaxAmt,
            NetAmount = d.NetAmount,
            LocalAmount = d.LocalAmount,
            OrderType = d.OrderType,
            StockControl = d.StockControl,
            ItemGlCode = d.SellingGlCode,
            Classification = d.Classification,
            Remarks = d.Remarks
            // FrWarehouse, LocCode, IStatus, LotNo, ExpiryDate intentionally omitted (ReturnStock=false)
        }).ToList();

        var draft = new SaCdnDocument
        {
            DocNo = string.Empty, // Not yet saved
            DocDate = invoice.InvDate,
            Status = SaCdnStatuses.New,
            Type = SaCdnTypes.CreditNote,
            InvNo = invoice.InvNo,
            CustCode = invoice.CustCode,
            CustName = invoice.CustName,
            Currency = invoice.Currency,
            CurrRate = invoice.CurrRate,
            PayCode = invoice.PayCode,
            TaxGrCode = invoice.TaxGrCode,
            SalesmanCode = invoice.SalesmanCode,
            Remarks = invoice.Remark,
            InvAddress1 = invoice.InvAddress1,
            InvAddress2 = invoice.InvAddress2,
            InvAddress3 = invoice.InvAddress3,
            InvAddress4 = invoice.InvAddress4,
            InvCity = invoice.InvCity,
            InvState = invoice.InvState,
            InvPostalCode = invoice.InvPostalCode,
            InvCountry = invoice.InvCountry,
            InvTel = invoice.InvTel,
            InvFax = invoice.InvFax,
            GrossAmnt = invoice.GrossAmnt,
            Taxes = invoice.Taxes,
            TotAmnt = invoice.TotAmnt,
            ReturnStock = false,
            Lines = lines
        };

        return SaCdnOperationResult.OkDocument(draft);
    }

    // ─────────────────────────── PostOneAsync ───────────────────────────

    private async Task<SaCdnPostingItemResult> PostOneAsync(
        UserContext context,
        string docNo,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Invoice → CN lock order (peek InvNo without UPDLOCK first).
            var peekInvNo = await PeekInvNoAsync(
                db, context.CompanyCode!, context.BranchCode!, docNo, cancellationToken);
            var locked = await SaCdnLockOrder.AcquireAsync(
                db, _invoices, _cdns,
                context.CompanyCode!, context.BranchCode!,
                peekInvNo, docNo, cancellationToken);
            var cdn = locked.Cdn;
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, "Document was not found.");
            }

            if (!await CanAsync(cdn.Type, PermissionCodes.Post, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, "Not authorized.");
            }

            if (string.Equals(cdn.Status, SaCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(
                    docNo,
                    SaCdnReasonCodes.StatusConflict,
                    "Document is already POSTED.");
            }

            if (!string.Equals(cdn.Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, SaCdnReasonCodes.StatusConflict, "Document is not NEW.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(cdn.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnPostingItemResult.Failed(
                        docNo,
                        SaCdnReasonCodes.Concurrency,
                        "Document was changed by another user.");
                }
                db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            // For CN with InvNo: re-check remaining under invoice lock
            if (string.Equals(cdn.Type, SaCdnTypes.CreditNote, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(cdn.InvNo))
            {
                if (locked.Invoice is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnPostingItemResult.Failed(docNo, $"Invoice {cdn.InvNo} was not found.");
                }

                var decPoint = await db.SaCusts.AsNoTracking()
                    .Where(x => x.CompanyCode == context.CompanyCode && x.CustCode == cdn.CustCode)
                    .Select(x => x.DecPoint == true)
                    .FirstOrDefaultAsync(cancellationToken);
                var otherRows = await _cdns.ListOtherCreditNotesAsync(
                    db, context.CompanyCode!, context.BranchCode!, cdn.InvNo!, docNo, cancellationToken);
                var eval = SaCdnCalc.EvaluateRemaining(
                    locked.Invoice.TotAmnt,
                    otherRows.Select(x => x.TotAmnt).ToList(),
                    cdn.TotAmnt,
                    decPoint,
                    otherRows.Where(x => x.IsDraft).Select(x => x.DocNo).ToList());
                if (!eval.Ok)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnPostingItemResult.Failed(
                        docNo,
                        SaCdnReasonCodes.RemainingExceeded,
                        eval.Error ?? "CN total exceeds invoice remaining.");
                }
            }

            await db.Entry(cdn).Collection(x => x.Details).LoadAsync(cancellationToken);

            // Commercial readiness checks
            var readiness = await ValidateCommercialReadinessAsync(db, cdn, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(
                    docNo,
                    readiness.FirstReasonCode!,
                    readiness.JoinedMessage());
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            var refNo = SaCdnSpRefs.ToRefNo(docNo);

            if (!cdn.ReturnStock)
            {
                // No stock to return — clean up any leftover NEW CR batch
                var crBatch = await SaCdnCrLock.LockByCnRefAsync(
                    db, _postingRepo, context.CompanyCode!, context.BranchCode!, docNo, cancellationToken);
                if (crBatch is not null)
                {
                    if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SaCdnPostingItemResult.Failed(
                            docNo,
                            SaCdnReasonCodes.CrOrphan,
                            "Unexpected POSTED CR batch exists. Contact administrator.");
                    }
                    if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
                    {
                        await _posting.DeleteNewStockInBatchInTransactionAsync(
                            db, context.CompanyCode!, context.BranchCode!,
                            crBatch.BatchNo, IvTrxTypes.CustomerReturn, cancellationToken);
                    }
                }
            }
            else
            {
                // ReturnStock=true: create or reuse CR batch
                var stockLines = cdn.Details.Where(d => d.StockControl).ToList();
                if (stockLines.Count > 0)
                {
                    var crBatch = await SaCdnCrLock.LockByCnRefAsync(
                        db, _postingRepo, context.CompanyCode!, context.BranchCode!, docNo, cancellationToken);

                    if (crBatch is null)
                    {
                        // Create new CR batch
                        var batchNo = await _runningNumbers.GetNextAsync(
                            db, context.CompanyCode!, RunningNumberKeys.IvBatch, cancellationToken);
                        crBatch = new IvTrxBatch
                        {
                            CompanyCode = context.CompanyCode!,
                            BranchCode = context.BranchCode!,
                            BatchNo = batchNo,
                            TrxDtTime = cdn.DocDate.Date,
                            TrxType = IvTrxTypes.CustomerReturn,
                            BatchStatus = IvBatchStatuses.New,
                            RefNo = refNo,
                            LocationCode = context.LocationCode,
                            CreatedDate = now,
                            CreatedBy = uid
                        };
                        AddCrBatchDetails(crBatch, cdn, stockLines, context.CompanyCode!, context.BranchCode!, batchNo);
                        crBatch.SourceFingerprint = SaCdnCalc.ComputeSourceFingerprint(cdn.Details);
                        db.IvTrxBatches.Add(crBatch);
                        await db.SaveChangesAsync(cancellationToken); // persist before posting
                    }
                    else if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.New, StringComparison.OrdinalIgnoreCase))
                    {
                        // Replace details and fingerprint
                        var oldDetails = await _postingRepo.LoadDetailsForBatchAsync(db, crBatch.Id, cancellationToken);
                        db.IvTrxBatchDetails.RemoveRange(oldDetails);
                        crBatch.Details.Clear();
                        AddCrBatchDetails(crBatch, cdn, stockLines, context.CompanyCode!, context.BranchCode!, crBatch.BatchNo);
                        crBatch.SourceFingerprint = SaCdnCalc.ComputeSourceFingerprint(cdn.Details);
                        crBatch.ModifiedDate = now;
                        crBatch.ModifiedBy = uid;
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    else if (string.Equals(crBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                    {
                        // Reuse only if fingerprint matches
                        if (!SaCdnCalc.IsValidFingerprint(crBatch.SourceFingerprint))
                        {
                            await tx.RollbackAsync(cancellationToken);
                            return SaCdnPostingItemResult.Failed(
                                docNo,
                                SaCdnReasonCodes.FingerprintMissing,
                                "CR batch fingerprint is missing or invalid.");
                        }

                        var currentFp = SaCdnCalc.ComputeSourceFingerprint(cdn.Details);
                        if (!string.Equals(crBatch.SourceFingerprint, currentFp, StringComparison.Ordinal))
                        {
                            await tx.RollbackAsync(cancellationToken);
                            return SaCdnPostingItemResult.Failed(
                                docNo,
                                SaCdnReasonCodes.FingerprintMismatch,
                                "CN stock lines changed since last post. Rollback the CR batch first.");
                        }
                        // Fingerprint matches → reuse POSTED CR batch, skip re-posting stock
                        goto SetPosted;
                    }

                    // Post the CR batch
                    var core = await _posting.PostStockInInTransactionAsync(
                        db,
                        context.CompanyCode!,
                        context.BranchCode!,
                        context.UserId!,
                        crBatch.BatchNo,
                        IvTrxTypes.CustomerReturn,
                        cancellationToken);

                    if (!core.Succeeded)
                    {
                        await tx.RollbackAsync(cancellationToken);
                        return SaCdnPostingItemResult.Failed(docNo, core.ErrorMessage ?? "Stock return post failed.");
                    }

                    TestHookAfterStockIn?.Invoke();
                }
            }

        SetPosted:
            cdn.Status = SaCdnStatuses.Posted;
            cdn.PostedDate = now;
            cdn.PostedBy = uid;
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;
            TouchRowVersion(db, cdn);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaCdnPostingItemResult.Posted(docNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaCdnPostingItemResult.Failed(
                docNo,
                SaCdnReasonCodes.Concurrency,
                "Document changed during post.");
        }
    }

    // ─────────────────────────── RollbackOneAsync ───────────────────────────

    private async Task<SaCdnPostingItemResult> RollbackOneAsync(
        UserContext context,
        string docNo,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Invoice → CN lock order (peek InvNo without UPDLOCK first).
            var peekInvNo = await PeekInvNoAsync(
                db, context.CompanyCode!, context.BranchCode!, docNo, cancellationToken);
            var locked = await SaCdnLockOrder.AcquireAsync(
                db, _invoices, _cdns,
                context.CompanyCode!, context.BranchCode!,
                peekInvNo, docNo, cancellationToken);
            var cdn = locked.Cdn;
            if (cdn is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, "Document was not found.");
            }

            if (!await CanAsync(cdn.Type, PermissionCodes.Rollback, cancellationToken))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, "Not authorized.");
            }

            if (string.Equals(cdn.Status, SaCdnStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(
                    docNo,
                    SaCdnReasonCodes.StatusConflict,
                    "Document is not POSTED.");
            }

            if (!string.Equals(cdn.Status, SaCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaCdnPostingItemResult.Failed(docNo, "Only POSTED documents can be rolled back.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(cdn.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnPostingItemResult.Failed(
                        docNo,
                        SaCdnReasonCodes.Concurrency,
                        "Document was changed by another user.");
                }
                db.Entry(cdn).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            // Find CR batch
            var crBatch = await SaCdnCrLock.LockByCnRefAsync(
                db, _postingRepo, context.CompanyCode!, context.BranchCode!, docNo, cancellationToken);

            if (crBatch is not null
                && string.Equals(crBatch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                var core = await _posting.RollBackStockInInTransactionAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.UserId!,
                    crBatch.BatchNo,
                    IvTrxTypes.CustomerReturn,
                    cancellationToken);

                if (!core.Succeeded)
                {
                    // Abort — both CN and CR remain POSTED
                    await tx.RollbackAsync(cancellationToken);
                    return SaCdnPostingItemResult.Failed(docNo, core.ErrorMessage ?? "Stock return rollback failed.");
                }

                TestHookAfterStockRollback?.Invoke();
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            cdn.Status = SaCdnStatuses.New;
            cdn.RollbackDate = now;
            cdn.RollbackBy = uid;
            cdn.ModifiedDate = now;
            cdn.ModifiedBy = uid;
            TouchRowVersion(db, cdn);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaCdnPostingItemResult.RolledBack(docNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaCdnPostingItemResult.Failed(
                docNo,
                SaCdnReasonCodes.Concurrency,
                "Document changed during rollback.");
        }
    }

    // ─────────────────────────── Prepare lines ───────────────────────────

    private sealed record PreparedLinesResult(
        string? Error,
        SaCdnErrorKind ErrorKind,
        IReadOnlyDictionary<string, string>? ValidationErrors,
        SaCust? Customer,
        string Currency,
        decimal CurrRate,
        decimal Taxes,
        decimal TotAmnt,
        IReadOnlyList<PreparedLine>? Lines)
    {
        public SaCdnOperationResult ToFail()
        {
            if (Error is null) return SaCdnOperationResult.Fail("Preparation failed.");
            if (ValidationErrors?.Count > 0) return SaCdnOperationResult.FailValidation(Error, ValidationErrors);
            return SaCdnOperationResult.Fail(Error, ErrorKind);
        }

        public static PreparedLinesResult Fail(string error, SaCdnErrorKind kind = SaCdnErrorKind.BusinessRule) =>
            new(error, kind, null, null, string.Empty, 0m, 0m, 0m, null);

        public static PreparedLinesResult Validation(string error, IReadOnlyDictionary<string, string> errors) =>
            new(error, SaCdnErrorKind.Validation, errors, null, string.Empty, 0m, 0m, 0m, null);

        public static PreparedLinesResult Ok(
            SaCust customer,
            string currency,
            decimal rate,
            decimal taxes,
            decimal totAmnt,
            IReadOnlyList<PreparedLine> lines) =>
            new(null, SaCdnErrorKind.None, null, customer, currency, rate, taxes, totAmnt, lines);
    }

    private sealed record PreparedLine(
        int Line,
        string ICode,
        string? IDesc,
        decimal Qty,
        decimal StdQty,
        decimal StdCustPsize,
        string? StdUom,
        decimal UnitPrice,
        decimal ItemDiscount,
        decimal ItemDiscount2,
        decimal ItemDiscount3,
        decimal ItemDiscount4,
        decimal ItemDiscount5,
        decimal ItemDiscount6,
        decimal ItemDiscAmount,
        bool IsInclusive,
        string? TaxGrCode,
        string? OrderType,
        string? Classification,
        string? Remarks,
        decimal CostPrice,
        bool StockControl,
        string? ItemGlCode,
        string? FrWarehouse,
        string? LocCode,
        string? IStatus,
        string? LotNo,
        DateTime? ExpiryDate,
        SaInvoiceLineCalcState Calc);

    private async Task<PreparedLinesResult> PrepareLinesAsync(
        AppDbContext db,
        SaCdnSaveRequest request,
        string docType,
        string companyCode,
        string branchCode,
        string? excludeDocNo,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custCode = (request.CustCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(custCode))
        {
            errors["CustCode"] = "Customer is required.";
        }

        var currency = (request.Currency ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(currency))
        {
            errors["Currency"] = "Currency is required.";
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            errors["Lines"] = "Add at least one line.";
        }

        if (errors.Count > 0)
        {
            return PreparedLinesResult.Validation("Validation failed.", errors);
        }

        // Validate mixed inclusive (all lines must be same)
        if (request.Lines!.Count > 0)
        {
            var firstInclusive = request.Lines[0].IsInclusive;
            if (request.Lines.Any(x => x.IsInclusive != firstInclusive))
            {
                return PreparedLinesResult.Fail(
                    "All lines must use the same tax type (inclusive or exclusive).",
                    SaCdnErrorKind.BusinessRule);
            }
        }

        var customer = await db.SaCusts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CompanyCode == companyCode && x.CustCode == custCode, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            errors["CustCode"] = "Customer was not found or is inactive.";
            return PreparedLinesResult.Validation("Customer was not found or is inactive.", errors);
        }

        // Validate currency
        if (!string.IsNullOrWhiteSpace(currency))
        {
            var currOk = await db.SaCurrencies.AsNoTracking().AnyAsync(
                x => x.CompanyCode == companyCode && x.CurrCode == currency && x.IsActive == true,
                cancellationToken);
            if (!currOk)
            {
                errors["Currency"] = $"Currency '{currency}' is not valid.";
            }
        }

        // Resolve currency rate
        var docDate = request.DocDate == default ? DateTime.Today.Date : request.DocDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, docDate, cancellationToken);
        if (rateResult.Error is not null)
        {
            errors["Currency"] = rateResult.Error;
        }

        // Build tax % lookup
        var headerTax = (request.TaxGrCode ?? string.Empty).Trim();
        var taxByCode = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

        if (errors.Count > 0)
        {
            return PreparedLinesResult.Validation("Validation failed.", errors);
        }

        var decPoint = customer.DecPoint == true;
        var discMethod = customer.DiscountMethod;
        var currRate = rateResult.Rate;
        var prepared = new List<PreparedLine>();
        var calcStates = new List<SaInvoiceLineCalcState>();
        var lineNo = 1;

        foreach (var line in request.Lines!)
        {
            if (line is null)
            {
                errors[$"Lines[{lineNo - 1}]"] = "Line data is required.";
                lineNo++;
                continue;
            }

            var iCode = (line.ICode ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(iCode))
            {
                errors[$"Lines[{lineNo - 1}].ICode"] = "Item code is required.";
                lineNo++;
                continue;
            }

            var qty = IvQty.Round(line.Qty);
            if (qty <= 0m)
            {
                errors[$"Lines[{lineNo - 1}].Qty"] = "Quantity must be greater than zero.";
            }

            var item = await _stockMasters.GetByCodeAsync(db, companyCode, iCode, cancellationToken);
            if (item is null || !item.IsActive)
            {
                errors[$"Lines[{lineNo - 1}].ICode"] = $"Item '{iCode}' was not found or is inactive.";
                lineNo++;
                continue;
            }

            // Determine tax %
            var lineTaxCode = (line.TaxGrCode ?? string.Empty).Trim();
            if (lineTaxCode.Length == 0) lineTaxCode = headerTax;
            if (lineTaxCode.Length == 0 && !string.IsNullOrWhiteSpace(item.TaxGroup))
                lineTaxCode = item.TaxGroup.Trim();

            decimal taxPercent = 0m;
            if (lineTaxCode.Length > 0)
            {
                if (taxByCode.TryGetValue(lineTaxCode, out var tp))
                {
                    taxPercent = tp;
                }
                else
                {
                    errors[$"Lines[{lineNo - 1}].TaxGrCode"] = $"Tax group '{lineTaxCode}' was not found.";
                }
            }

            // Standard qty
            var stdPack = item.StdPackSize ?? 0m;
            var stdQty = stdPack > 0m
                ? IvQty.Round(qty * stdPack)
                : IvQty.Round(qty);

            var calcState = new SaInvoiceLineCalcState
            {
                Line = lineNo,
                Qty = qty,
                UnitPrice = line.UnitPrice,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                ItemDiscAmount = line.ItemDiscAmount,
                IsInclusive = line.IsInclusive,
                OrderType = line.OrderType
            };
            SaInvoiceCalc.CalculateLine(calcState, taxPercent, decPoint, discMethod);
            calcState.LocalAmount = SaInvoiceCalc.Money(calcState.NetAmount * currRate);
            calcStates.Add(calcState);

            // Stock fields only valid when ReturnStock=true
            string? frWarehouse = null, locCode = null, iStatus = null, lotNo = null;
            DateTime? expiryDate = null;

            if (request.ReturnStock && item.StockControl)
            {
                frWarehouse = Norm(line.FrWarehouse);
                locCode = Norm(line.LocCode);
                iStatus = Norm(line.IStatus);
                lotNo = Norm(line.LotNo);
                expiryDate = SaCdnCalc.ToDateOnly(line.ExpiryDate);
            }

            var itemGlRaw = (line.ItemGlCode ?? string.Empty).Trim();
            if (itemGlRaw.Length > 20)
            {
                errors[$"Lines[{lineNo - 1}].ItemGlCode"] = "Item GL must be at most 20 characters.";
            }

            string? itemGlCode;
            if (itemGlRaw.Length > 0 && itemGlRaw.Length <= 20)
            {
                itemGlCode = itemGlRaw;
            }
            else
            {
                itemGlCode = Norm(item.SellingGlCode);
            }

            prepared.Add(new PreparedLine(
                Line: lineNo,
                ICode: item.ICode,
                IDesc: TruncateOptional(line.IDesc ?? item.IDesc, 200),
                Qty: qty,
                StdQty: stdQty,
                StdCustPsize: stdPack,
                StdUom: item.StdUom,
                UnitPrice: line.UnitPrice,
                ItemDiscount: line.ItemDiscount,
                ItemDiscount2: line.ItemDiscount2,
                ItemDiscount3: line.ItemDiscount3,
                ItemDiscount4: line.ItemDiscount4,
                ItemDiscount5: line.ItemDiscount5,
                ItemDiscount6: line.ItemDiscount6,
                ItemDiscAmount: line.ItemDiscAmount,
                IsInclusive: line.IsInclusive,
                TaxGrCode: lineTaxCode.Length > 0 ? lineTaxCode : null,
                OrderType: Norm(line.OrderType),
                Classification: Norm(line.Classification),
                Remarks: TruncateOptional(line.Remarks, 250),
                CostPrice: line.CostPrice,
                StockControl: item.StockControl,
                ItemGlCode: itemGlCode,
                FrWarehouse: frWarehouse,
                LocCode: locCode,
                IStatus: iStatus,
                LotNo: lotNo,
                ExpiryDate: expiryDate,
                Calc: calcState));

            lineNo++;
        }

        // Legacy-aware Department / Project validation (shared rule — see MsRefLookupRules).
        // A stored orphan may stay untouched; a new or changed code must exist and be active.
        string? priorDept = null;
        string? priorProjId = null;
        var editDocNo = (excludeDocNo ?? string.Empty).Trim();
        if (editDocNo.Length > 0)
        {
            var prior = await db.SaCdns.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.DocNo == editDocNo)
                .Select(x => new { x.Dept, x.ProjId })
                .FirstOrDefaultAsync(cancellationToken);
            if (prior is not null)
            {
                priorDept = prior.Dept;
                priorProjId = prior.ProjId;
            }
        }

        var deptError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Department, priorDept, request.Dept, cancellationToken);
        if (deptError is not null)
        {
            errors["Dept"] = deptError;
        }

        var projError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Project, priorProjId, request.ProjId, cancellationToken);
        if (projError is not null)
        {
            errors["ProjId"] = projError;
        }

        if (errors.Count > 0)
        {
            return PreparedLinesResult.Validation("Validation failed.", errors);
        }

        // Apply adaptive tax rounding
        SaInvoiceCalc.ApplyTaxAdaptiveRounding(calcStates);

        var header = SaInvoiceCalc.CalculateHeader(calcStates, decPoint);
        return PreparedLinesResult.Ok(customer, currency, currRate, header.Taxes, header.TotAmnt, prepared);
    }

    // ─────────────────────────── Commercial readiness ───────────────────────────

    private sealed class CdnCommercialReadinessResult
    {
        public bool Ok => Errors.Count == 0;
        public List<(string Field, string Message, string? ReasonCode)> Errors { get; } = [];

        public void Add(string field, string message, string? reasonCode = null)
        {
            if (Errors.Any(e => string.Equals(e.Field, field, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Errors.Add((field, message, reasonCode));
        }

        public Dictionary<string, string> ToValidationErrors() =>
            Errors.ToDictionary(e => e.Field, e => e.Message, StringComparer.OrdinalIgnoreCase);

        public string JoinedMessage() => string.Join(" ", Errors.Select(e => e.Message));

        public string? FirstReasonCode => Errors.Select(e => e.ReasonCode).FirstOrDefault(c => c is not null);
    }

    private async Task<CdnCommercialReadinessResult> ValidateCommercialReadinessAsync(
        AppDbContext db,
        SaCdn cdn,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var result = new CdnCommercialReadinessResult();

        var salesmanCode = Norm(cdn.SalesRep);
        SaSalesRep? rep = null;
        if (salesmanCode is not null)
        {
            rep = await db.SaSalesReps.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode && x.SrepCode == salesmanCode,
                    cancellationToken);
        }

        if (salesmanCode is null || rep is null || rep.IsActive == false)
        {
            result.Add(
                "SalesmanCode",
                "Salesman is missing, unknown, or inactive.",
                "CDN_SALESMAN_INVALID");
        }

        if (SaInvoiceCalc.HasTax(cdn.Taxes))
        {
            var taxCode = Norm(cdn.TaxGrCode);
            if (taxCode is null)
            {
                result.Add(
                    "TaxGrCode",
                    "Tax group is required when the document has tax.",
                    "CDN_TAX_GL_MISSING");
            }
            else
            {
                var taxGroup = await db.SaTaxGroups.AsNoTracking()
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == companyCode && x.TaxGrCode == taxCode,
                        cancellationToken);
                if (taxGroup is null || string.IsNullOrWhiteSpace(taxGroup.TaxGlCode))
                {
                    result.Add(
                        "TaxGlCode",
                        "Tax GL code is required when the document has tax.",
                        "CDN_TAX_GL_MISSING");
                }
            }
        }

        foreach (var line in cdn.Details.OrderBy(x => x.Line))
        {
            if (string.IsNullOrWhiteSpace(line.ItemGlCode) && line.Amount != 0m)
            {
                result.Add(
                    $"Lines[{line.Line - 1}].ItemGlCode",
                    $"Item GL code is missing on line {line.Line}.",
                    "CDN_ITEM_GL_MISSING");
            }
        }

        return result;
    }

    // ─────────────────────────── ReturnStock validation ───────────────────────────

    private static string? ValidateReturnStockGates(SaCdnSaveRequest request, IReadOnlyList<PreparedLine> lines)
    {
        if (!request.ReturnStock)
        {
            return null; // Nothing to validate
        }

        foreach (var line in lines)
        {
            if (!line.StockControl)
            {
                continue; // Non-stock lines: warehouse/lot not required
            }

            if (line.Qty <= 0m)
            {
                return $"Line {line.Line}: quantity must be greater than zero for stock return.";
            }

            if (line.StdQty <= 0m)
            {
                return $"Line {line.Line}: standard quantity must be greater than zero for stock return.";
            }

            if (string.IsNullOrWhiteSpace(line.FrWarehouse))
            {
                return $"Line {line.Line}: warehouse is required for stock return.";
            }
        }

        return null;
    }

    // ─────────────────────────── CR batch helpers ───────────────────────────

    private static void AddCrBatchDetails(
        IvTrxBatch batch,
        SaCdn cdn,
        IEnumerable<SaCdnDetail> stockLines,
        string companyCode,
        string branchCode,
        int batchNo)
    {
        short trxLine = 1;
        foreach (var d in stockLines.OrderBy(x => x.Line))
        {
            batch.Details.Add(new IvTrxBatchDetail
            {
                CompanyCode = companyCode,
                BranchCode = branchCode,
                BatchNo = batchNo,
                TrxLineNo = trxLine,
                TrxType = IvTrxTypes.CustomerReturn,
                ICode = d.ICode,
                IDesc = d.IDesc,
                ProdCode = d.ICode,
                ProdDesc = d.IDesc,
                ToWarehouse = d.FrWarehouse,   // goods return TO warehouse
                ToLocation = d.LocCode,
                ToLotNo = d.LotNo,
                ToStdQty = IvQty.Round(d.StdQty),
                ToStdUom = d.StdUom,
                IStatus = d.IStatus,
                ExpiryDate = d.ExpiryDate,
                UnitPrice = d.CostPrice > 0m ? d.CostPrice : 0m,
                InvNo = cdn.InvNo,
                LocationCode = cdn.LocationCode
            });
            trxLine++;
        }
    }

    // ─────────────────────────── Map document ───────────────────────────

    private static SaCdnDocument MapDocument(SaCdn cdn)
    {
        return new SaCdnDocument
        {
            DocNo = cdn.DocNo,
            DocDate = cdn.DocDate,
            Status = cdn.Status,
            Type = cdn.Type,
            InvNo = cdn.InvNo,
            DoNo = cdn.DoNo,
            ReturnStock = cdn.ReturnStock,
            CustCode = cdn.CustCode,
            CustName = cdn.CustName,
            Prefix = cdn.Prefix,
            Currency = cdn.Currency,
            CurrRate = cdn.CurrRate,
            PayCode = cdn.PayCode,
            TaxGrCode = cdn.TaxGrCode,
            SalesmanCode = cdn.SalesRep,
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
            RowVersion = cdn.RowVersion,
            Lines = (cdn.Details ?? []).OrderBy(x => x.Line).Select(d => new SaCdnLineDto
            {
                Line = d.Line,
                ICode = d.ICode,
                IDesc = d.IDesc,
                Qty = d.Qty,
                StdQty = d.StdQty,
                StdCustPsize = d.StdCustPsize,
                StdUom = d.StdUom,
                UnitPrice = d.UnitPrice,
                Amount = d.Amount,
                ItemDiscount = d.ItemDiscount,
                ItemDiscount2 = d.ItemDiscount2,
                ItemDiscount3 = d.ItemDiscount3,
                ItemDiscount4 = d.ItemDiscount4,
                ItemDiscount5 = d.ItemDiscount5,
                ItemDiscount6 = d.ItemDiscount6,
                ItemDiscAmount = d.ItemDiscount1,     // stored in ItemDiscount1
                IsInclusive = d.IsInclusive,
                TaxGrCode = d.TaxGroup,
                TaxAmt = d.TaxAmt,
                NetAmount = d.NetAmount,
                LocalAmount = d.NetAmount * cdn.CurrRate,
                OrderType = null,
                StockControl = d.StockControl,
                ItemGlCode = d.ItemGlCode,
                Classification = d.Classification,
                Remarks = d.Remarks,
                CostPrice = d.CostPrice,
                FrWarehouse = d.FrWarehouse,
                LocCode = d.LocCode,
                IStatus = d.IStatus,
                LotNo = d.LotNo,
                ExpiryDate = d.ExpiryDate
            }).ToList()
        };
    }

    // ─────────────────────────── Apply totals + details ───────────────────────────

    private static void ApplyCalculatedTotals(SaCdn cdn, IReadOnlyList<PreparedLine> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines.Select(x => x.Calc).ToList(), decPoint);
        cdn.GrossAmnt = header.GrossAmnt;
        cdn.Taxes = header.Taxes;
        cdn.TotAmnt = header.TotAmnt;
    }

    private static void AddDetails(SaCdn cdn, IReadOnlyList<PreparedLine> lines)
    {
        foreach (var line in lines)
        {
            cdn.Details.Add(new SaCdnDetail
            {
                CompanyCode = cdn.CompanyCode,
                BranchCode = cdn.BranchCode,
                DocNo = cdn.DocNo,
                Line = (short)line.Line,
                ICode = line.ICode,
                IDesc = line.IDesc,
                Qty = line.Qty,
                StdQty = line.StdQty,
                StdCustPsize = line.StdCustPsize,
                StdUom = line.StdUom,
                UnitPrice = line.UnitPrice,
                Amount = line.Calc.Amount,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount1 = line.ItemDiscAmount,   // ItemDiscAmount stored in ItemDiscount1
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                IsInclusive = line.IsInclusive,
                TaxGroup = line.TaxGrCode,
                TaxAmt = line.Calc.TaxAmt,
                NetAmount = line.Calc.NetAmount,
                CostPrice = line.CostPrice,
                StockControl = line.StockControl,
                ItemGlCode = line.ItemGlCode,
                Classification = line.Classification,
                Remarks = line.Remarks,
                FrWarehouse = line.FrWarehouse,
                LocCode = line.LocCode,
                IStatus = line.IStatus,
                LotNo = line.LotNo,
                ExpiryDate = line.ExpiryDate
            });
        }
    }

    // ─────────────────────────── Currency rate helper ───────────────────────────

    private async Task<(string? Error, decimal Rate)> ResolveCurrRateAsync(
        AppDbContext db,
        string currency,
        DateTime docDate,
        CancellationToken cancellationToken)
    {
        var isHome = string.Equals(currency, SaInvoiceCalc.HomeCurrency, StringComparison.OrdinalIgnoreCase);
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency && x.Status && x.StartDate <= docDate && x.EndDate >= docDate)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            if (isHome)
            {
                return (null, 1m);
            }
            return ($"No currency rate for {currency} on {docDate:yyyy-MM-dd}.", 0m);
        }

        var decimalRate = Convert.ToDecimal(rate.Value);
        if (!isHome && decimalRate == 1m)
        {
            return ("Non-home currency rate cannot be 1.", 0m);
        }

        return (null, decimalRate);
    }

    // ─────────────────────────── Context helpers ───────────────────────────

    private UserContext ValidateUserContext()
    {
        var scope = _tenant.TryBranchScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company or branch context.");
        }
        return UserContext.Ok(scope.CompanyCode, scope.BranchCode, scope.LocationCode, scope.UserId);
    }

    private UserContext ValidateWriteContext()
    {
        var scope = _tenant.TryWriteScope();
        if (scope is null)
        {
            return UserContext.Fail("Invalid company, branch, or location context.");
        }
        return UserContext.Ok(scope.CompanyCode, scope.BranchCode!, scope.LocationCode!, scope.UserId);
    }

    private Task<bool> CanAsync(string docType, string permission, CancellationToken cancellationToken)
    {
        var menuCode = string.Equals(docType, SaCdnTypes.CreditNote, StringComparison.OrdinalIgnoreCase)
            ? MenuCodes.SalesCreditNote
            : MenuCodes.SalesDebitNote;
        return _accessRights.CanAsync(menuCode, permission, cancellationToken);
    }

    // ─────────────────────────── Misc helpers ───────────────────────────

    private static async Task<string?> PeekInvNoAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string docNo,
        CancellationToken cancellationToken)
    {
        return await db.SaCdns.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.DocNo == docNo)
            .Select(x => x.InvNo)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static List<SaCdnKeyedRequest> NormalizeKeyedItems(IReadOnlyList<SaCdnKeyedRequest>? items)
    {
        return (items ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.DocNo))
            .Select(x => new SaCdnKeyedRequest { DocNo = x.DocNo.Trim(), RowVersion = x.RowVersion })
            .GroupBy(x => x.DocNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string? TruncateOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string? UpperSnapshot(string? value, int maxLength)
    {
        var trimmed = TruncateOptional(value, maxLength);
        return trimmed?.ToUpperInvariant();
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static void TouchRowVersion(AppDbContext db, SaCdn cdn)
    {
        if (!db.Database.IsSqlServer())
        {
            cdn.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sqlEx)
        {
            return sqlEx.Number is 2601 or 2627;
        }
        return false;
    }

    // ─────────────────────────── UserContext ───────────────────────────

    private readonly record struct UserContext(
        string? Error,
        string? CompanyCode,
        string? BranchCode,
        string? LocationCode,
        string? UserId)
    {
        public static UserContext Fail(string error) => new(error, null, null, null, null);

        public static UserContext Ok(string company, string? branch, string? location, string user) =>
            new(null, company, branch, location, user);
    }
}
