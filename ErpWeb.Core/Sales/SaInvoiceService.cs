using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed class SaInvoiceService : ISaInvoiceService
{
    /// <summary>Test-only: after stock Core succeeds, before invoice status mutation.</summary>
    internal Action? TestHookAfterStockCore { get; set; }

    /// <summary>Test-only: after invoice status set, before outer Commit.</summary>
    internal Action? TestHookAfterInvoiceStatus { get; set; }

    /// <summary>Test-only: after SP details deleted, before FIFO insert.</summary>
    internal Action? TestHookAfterSpDelete { get; set; }

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IRunningNumberService _runningNumbers;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ISaInvoiceRepository _invoices;
    private readonly ISaCustRepository _customers;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IIvStockTransactionRepository _transactions;
    private readonly IIvStockPostingRepository _postingRepo;
    private readonly IIvInventoryPostingService _posting;
    private readonly IIvSpShipmentService _shipments;
    private readonly ISaSoRepository _salesOrders;
    private readonly ISaDocApplication _docApplication;
    private readonly ISaCustLookupService _custLookups;
    private readonly ILogger<SaInvoiceService> _logger;

    public SaInvoiceService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IRunningNumberService runningNumbers,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ISaInvoiceRepository invoices,
        ISaCustRepository customers,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IIvStockTransactionRepository transactions,
        IIvStockPostingRepository postingRepo,
        IIvInventoryPostingService posting,
        IIvSpShipmentService shipments,
        ISaSoRepository salesOrders,
        ISaDocApplication docApplication,
        ISaCustLookupService custLookups,
        ILogger<SaInvoiceService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _runningNumbers = runningNumbers;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _invoices = invoices;
        _customers = customers;
        _stockMasters = stockMasters;
        _common = common;
        _transactions = transactions;
        _postingRepo = postingRepo;
        _posting = posting;
        _shipments = shipments;
        _salesOrders = salesOrders;
        _docApplication = docApplication;
        _custLookups = custLookups;
        _logger = logger;
    }

    public async Task<SaInvoiceOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        var items = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var warehouses = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!, context.BranchCode!, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customers = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustCode)
            .Select(x => new SaInvoiceCustomerLookupRow
            {
                CustCode = x.CustCode,
                CustName = x.CustName,
                Currency = x.Currency,
                InvoicePrefix = x.InvoicePrefix,
                DiscountMethod = x.DiscountMethod,
                DecPoint = x.DecPoint
            })
            .ToListAsync(cancellationToken);

        var taxGroups = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode)
            .OrderBy(x => x.TaxGrCode)
            .Select(x => new SaInvoiceTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var payCodes = await _custLookups.ListPayCodesForAssignmentAsync(cancellationToken);

        var salesReps = await db.SaSalesReps.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive != false)
            .OrderBy(x => x.SrepCode)
            .Select(x => new IvCodeLookupRow
            {
                Code = x.SrepCode,
                Desc = x.SrepName
            })
            .ToListAsync(cancellationToken);

        return SaInvoiceOperationResult.OkLookups(
            items.Select(x => new SaInvoiceItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                StdUom = x.StdUom,
                StdPackSize = x.StdPackSize,
                SellingPrice = x.SellingPrice,
                SellingGlCode = x.SellingGlCode,
                TaxGroup = x.TaxGroup,
                StockControl = x.StockControl,
                DefWarehouse = x.DefWarehouse,
                Classification = x.Classification
            }).ToList(),
            warehouses.Select(x => new IvWarehouseLookupRow
            {
                WarehouseCode = x.WarehouseCode,
                WarehouseDesc = x.WarehouseDesc
            }).ToList(),
            customers,
            taxGroups,
            payCodes,
            salesReps);
    }

    public async Task<SaInvoiceOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime invDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        var code = (custCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return SaInvoiceOperationResult.FailValidation("Customer is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CustCode"] = "Customer is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, includeChildren: false, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            return SaInvoiceOperationResult.Fail("Customer was not found or is inactive.", SaInvoiceErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(customer.Currency)
            ? SaInvoiceCalc.HomeCurrency
            : customer.Currency.Trim();
        var date = invDate == default ? _dates.Today.Date : invDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, date, cancellationToken);
        var useMainBill = customer.AppInvoice == true;
        var useMainShip = customer.AppShip == true;

        // SaCustAdd has no IsActive — return all lines for this company+customer.
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

        return SaInvoiceOperationResult.OkDefaults(new SaInvoiceCustomerDefaults
        {
            CustCode = customer.CustCode,
            CustName = customer.CustName,
            InvPrefix = string.IsNullOrWhiteSpace(customer.InvoicePrefix) ? "INV" : customer.InvoicePrefix.Trim(),
            Currency = currency,
            CurrRate = rateResult.Error is null ? rateResult.Rate : 0m,
            CurrRateValid = rateResult.Error is null && rateResult.Rate != 0m,
            PayCode = customer.PayCode,
            TaxGrCode = customer.TaxGrCode,
            Taxable = customer.Taxable,
            SalesmanCode = customer.SalesmanCode,
            DiscountMethod = customer.DiscountMethod,
            DecPoint = customer.DecPoint,
            InvEmail = customer.Email,
            BuyerTin = customer.TinNo,
            BuyerBrn = customer.CustBrn,
            InvName = useMainBill ? customer.CustName : customer.InvName,
            InvAddress1 = useMainBill ? customer.Address1 : customer.InvAddress1,
            InvAddress2 = useMainBill ? customer.Address2 : customer.InvAddress2,
            InvAddress3 = useMainBill ? customer.Address3 : customer.InvAddress3,
            InvAddress4 = useMainBill ? customer.Address4 : null,
            InvCity = useMainBill ? customer.City : customer.InvCity,
            InvState = useMainBill ? customer.State : customer.InvState,
            InvPostalCode = useMainBill ? customer.PostalCode : customer.InvPostalCode,
            InvCountry = useMainBill ? customer.Country : customer.InvCountry,
            InvTel = useMainBill ? customer.Tel : customer.InvTel,
            InvFax = useMainBill ? customer.Fax : customer.InvFax,
            ShipName = useMainShip ? customer.CustName : customer.ShipName,
            ShipAddress1 = useMainShip ? customer.Address1 : customer.ShipAddress1,
            ShipAddress2 = useMainShip ? customer.Address2 : customer.ShipAddress2,
            ShipAddress3 = useMainShip ? customer.Address3 : customer.ShipAddress3,
            ShipCity = useMainShip ? customer.City : customer.ShipCity,
            ShipState = useMainShip ? customer.State : customer.ShipState,
            ShipPostalCode = useMainShip ? customer.PostalCode : customer.ShipPostalCode,
            ShipCountry = useMainShip ? customer.Country : customer.ShipCountry,
            ShipTel = useMainShip ? customer.Tel : customer.ShipTel,
            ShipFax = useMainShip ? customer.Fax : customer.ShipFax,
            ShipToAddresses = shipToAddresses
        });
    }

    public async Task<SaInvoiceOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime invDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        var curr = (currency ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(curr))
        {
            return SaInvoiceOperationResult.FailValidation("Currency is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Currency"] = "Currency is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var date = invDate == default ? _dates.Today.Date : invDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, curr, date, cancellationToken);
        if (rateResult.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(rateResult.Error, SaInvoiceErrorKind.Validation);
        }

        return SaInvoiceOperationResult.OkRate(rateResult.Rate, valid: true);
    }

    public async Task<SaInvoiceOperationResult> SearchAsync(
        SaInvoiceListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        query ??= new SaInvoiceListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _invoices.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            new SaInvoiceSearchArgs(
                SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
                Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
                DateFrom: query.DateFrom,
                DateTo: query.DateTo,
                SortField: query.SortField,
                SortDescending: query.SortDescending,
                Skip: query.Skip,
                Take: query.Take),
            cancellationToken);

        var names = await LoadCustNamesAsync(db, context.CompanyCode!, rows.Select(x => x.CustCode).ToList(), cancellationToken);
        var invNos = rows.Select(x => x.InvNo).ToList();
        var lineCounts = invNos.Count == 0
            ? []
            : await db.SaInvoiceDetails.AsNoTracking()
                .Where(d => d.CompanyCode == context.CompanyCode
                    && d.BranchCode == context.BranchCode
                    && invNos.Contains(d.InvNo))
                .GroupBy(d => d.InvNo)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
        var countByInv = lineCounts.ToDictionary(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase);

        return SaInvoiceOperationResult.OkList(new SaInvoiceListPage
        {
            TotalCount = total,
            Rows = rows.Select(x => new SaInvoiceListRow
            {
                InvNo = x.InvNo,
                InvDate = x.InvDate,
                Status = x.Status,
                CustCode = x.CustCode,
                CustName = string.IsNullOrWhiteSpace(x.CustName) ? names.GetValueOrDefault(x.CustCode) : x.CustName,
                TotAmnt = x.TotAmnt,
                LineCount = countByInv.GetValueOrDefault(x.InvNo),
                CreatedDate = x.CreatedDate,
                CreatedBy = x.CreatedBy
            }).ToList()
        });
    }

    public async Task<SaInvoiceOperationResult> GetAsync(
        string invNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        var no = (invNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaInvoiceOperationResult.Fail("Invoice number is required.", SaInvoiceErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await _invoices.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (invoice is null)
        {
            return SaInvoiceOperationResult.Fail("Invoice was not found.", SaInvoiceErrorKind.NotFound);
        }

        var sp = await FindSpBatchAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        IReadOnlyList<IvTrxBatchDetail> spDetails = [];
        if (sp is not null)
        {
            spDetails = await _postingRepo.LoadDetailsForBatchAsync(db, sp.Id, cancellationToken);
        }

        var cust = await _customers.GetByCodeAsync(db, context.CompanyCode!, invoice.CustCode, includeChildren: false, cancellationToken);
        var custPoBySo = await SaDocCustPoLookup.LoadAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            invoice.Details.Select(x => (x.SoNo, x.CustRel)),
            cancellationToken);
        var document = MapDocument(invoice, invoice.CustName ?? cust?.CustName, sp, spDetails, custPoBySo);
        return await OkDocumentWithPostWarningsAsync(
            db, invoice, document, context.CompanyCode!, cancellationToken);
    }

    public async Task<SaInvoiceOperationResult> SaveNewAsync(
        SaInvoiceSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                existingDetails: null,
                excludeInvNo: null,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            BackfillCommercialHeader(request, prepared.Customer!);

            var invDate = request.InvDate == default ? _dates.Today.Date : request.InvDate.Date;
            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            var invoice = new SaInvoice
            {
                CompanyCode = context.CompanyCode!,
                InvNo = "CANDIDATE",
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode,
                CustCode = prepared.Customer!.CustCode,
                InvDate = invDate,
                Status = SaInvoiceStatuses.New,
                DoNo = "CANDIDATE",
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                CreatedDate = now,
                CreatedBy = uid
            };

            ApplyHeaderSnapshots(invoice, request, customerChanged: true);
            await ApplyMasterSnapshotsAsync(
                db, invoice, request, prepared.Customer!, prepared.Lines!, context.CompanyCode!, cancellationToken);
            AddDetails(invoice, prepared.Lines!);
            ApplyCalculatedTotals(invoice, prepared.Lines!, prepared.Customer.DecPoint == true);

            var readiness = await ValidateCommercialReadinessAsync(
                db, invoice, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.FailValidation("Validation failed.", readiness.ToValidationErrors());
            }

            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    "INV",
                    "",
                    invDate,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "Invoice numbering is not configured for this company/branch.",
                    SaInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "Invoice numbering is not configured correctly. Contact an administrator.",
                    SaInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "The next invoice number exceeds the configured length.",
                    SaInvoiceErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "The invoice could not be saved because of a database conflict. Try again.",
                    SaInvoiceErrorKind.Unexpected);
            }

            var invNo = issued.DocumentNumber;
            var prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim();
            invoice.InvNo = invNo;
            invoice.DoNo = invNo;
            invoice.InvPrefix = prefix;
            foreach (var detail in invoice.Details)
            {
                detail.InvNo = invNo;
            }

            TouchRowVersion(db, invoice);
            db.SaInvoices.Add(invoice);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Sales invoice saved. UserId={UserId} Company={Company} InvNo={InvNo}",
                context.UserId, context.CompanyCode, invNo);

            return await GetAsync(invNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("Invoice number is already used.", SaInvoiceErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Invoice save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("The invoice could not be saved because of a database conflict. Try again.", SaInvoiceErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice save failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("Unable to save the invoice.", SaInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<SaInvoiceOperationResult> UpdateAsync(
        string invNo,
        SaInvoiceSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaInvoiceOperationResult.FailValidation("Save request is required.");
        }

        var no = (invNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaInvoiceOperationResult.FailValidation("Invoice number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Invoice was not found.", SaInvoiceErrorKind.NotFound);
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Only NEW invoices can be edited.", SaInvoiceErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(invoice.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "This invoice was changed by another user. Reload before saving.",
                    SaInvoiceErrorKind.Concurrency);
            }

            db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            var previousIdentity = SnapshotIdentity(invoice.Details);
            var customerChanged = !string.Equals(invoice.CustCode, (request.CustCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

            if (customerChanged
                && (invoice.Details.Any(x => HasSoReference(x.SoNo, x.SoLine))
                    || RequestHasSoReferences(request.Lines)))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.FailValidation(
                    "Customer cannot be changed when the invoice contains Sales Order references.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["CustCode"] = "Customer cannot be changed when the invoice contains Sales Order references."
                    });
            }

            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                invoice.Details.ToList(),
                excludeInvNo: no,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            if (customerChanged)
            {
                await _shipments.ReleaseShipmentReservationAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.LocationCode!,
                    invoice.InvNo,
                    removeBatch: true,
                    cancellationToken);
                invoice.CustName = UpperSnapshot(prepared.Customer!.CustName, 200);
            }

            var invDate = request.InvDate == default ? _dates.Today.Date : request.InvDate.Date;
            invoice.InvDate = invDate;
            invoice.CustCode = prepared.Customer!.CustCode;
            invoice.Currency = prepared.Currency;
            invoice.CurrRate = prepared.CurrRate;
            invoice.ModifiedDate = DateTime.UtcNow;
            invoice.ModifiedBy = Truncate(context.UserId!, 10);
            BackfillCommercialHeader(request, prepared.Customer!);

            ApplyHeaderSnapshots(invoice, request, customerChanged);
            await ApplyMasterSnapshotsAsync(
                db, invoice, request, prepared.Customer!, prepared.Lines!, context.CompanyCode!, cancellationToken);

            db.SaInvoiceDetails.RemoveRange(invoice.Details);
            invoice.Details.Clear();
            AddDetails(invoice, prepared.Lines!);
            ApplyCalculatedTotals(invoice, prepared.Lines!, prepared.Customer.DecPoint == true);

            var readiness = await ValidateCommercialReadinessAsync(
                db, invoice, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.FailValidation("Validation failed.", readiness.ToValidationErrors());
            }

            TouchRowVersion(db, invoice);

            if (!customerChanged && !IdentityEquals(previousIdentity, SnapshotIdentity(invoice.Details)))
            {
                await _shipments.ReleaseShipmentReservationAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.LocationCode!,
                    invoice.InvNo,
                    removeBatch: true,
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(invoice.InvNo, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Invoice update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("The invoice could not be saved because of a database conflict. Try again.", SaInvoiceErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice update failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("Unable to save the invoice.", SaInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<SaInvoiceOperationResult> DeleteAsync(
        IReadOnlyList<string>? invNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        var nos = (invNos ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (nos.Count == 0)
        {
            return SaInvoiceOperationResult.Fail("Select at least one invoice.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var no in nos)
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (invoice is null)
            {
                return SaInvoiceOperationResult.Fail($"Invoice {no} was not found.");
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return SaInvoiceOperationResult.Fail($"Invoice {no} cannot be deleted because it is not NEW.");
            }

            await _shipments.ReleaseShipmentReservationAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.LocationCode!,
                no,
                removeBatch: true,
                cancellationToken);
            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            db.SaInvoiceDetails.RemoveRange(invoice.Details);
            db.SaInvoices.Remove(invoice);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return SaInvoiceOperationResult.Ok();
    }

    public async Task<SaInvoiceOperationResult> AddShipmentAsync(
        string invNo,
        bool overwriteExisting,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default)
    {
        var no = (invNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaInvoiceOperationResult.FailValidation("Invoice number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        if (rowVersion is null || rowVersion.Length == 0)
        {
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Invoice was not found.", SaInvoiceErrorKind.NotFound);
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Only NEW invoices can add shipment.", SaInvoiceErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(invoice.RowVersion, rowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "This invoice was changed by another user. Reload before saving.",
                    SaInvoiceErrorKind.Concurrency);
            }

            db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = rowVersion;
            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);

            var batch = await _postingRepo.LockSpBatchByRefAsync(
                db, context.CompanyCode!, context.BranchCode!, invoice.InvNo, cancellationToken);

            if (batch is not null
                && string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Posted shipment cannot be rebuilt.", SaInvoiceErrorKind.BusinessRule);
            }

            if (batch is not null && !overwriteExisting)
            {
                var existingDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
                if (existingDetails.Count > 0)
                {
                    var preview = MapDocument(invoice, invoice.CustName, batch, existingDetails);
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoiceOperationResult.OkConfirmation(
                        preview,
                        "ST000001: Shipment already exists. Confirm to overwrite.");
                }
            }

            var uid = Truncate(context.UserId!, 10);
            var now = DateTime.UtcNow;
            invoice.ModifiedDate = now;
            invoice.ModifiedBy = uid;
            TouchRowVersion(db, invoice);

            var shipResult = await _shipments.CreateOrReplaceShipmentAsync(
                db,
                new IvSpCreateOrReplaceCommand
                {
                    CompanyCode = context.CompanyCode!,
                    BranchCode = context.BranchCode!,
                    LocationCode = context.LocationCode!,
                    UserId = context.UserId!,
                    DocumentNo = invoice.InvNo,
                    DocumentDate = invoice.InvDate.Date,
                    OverwriteExisting = true,
                    AfterSpDelete = TestHookAfterSpDelete,
                    RequiredLines = invoice.Details
                        .OrderBy(x => x.Line)
                        .Select(x => new IvSpRequiredLine
                        {
                            Line = x.Line,
                            ICode = x.ICode ?? string.Empty,
                            IDesc = x.IDesc,
                            StdQty = x.StdQty,
                            StdUom = x.StdUom,
                            FrWarehouse = x.FrWarehouse ?? string.Empty,
                            UnitPrice = x.UnitPrice,
                            StockControl = x.StockControl
                        })
                        .ToList()
                },
                cancellationToken);

            if (!shipResult.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    shipResult.ErrorMessage ?? "Unable to add shipment.",
                    shipResult.ErrorKind switch
                    {
                        IvSpShipmentErrorKind.Concurrency => SaInvoiceErrorKind.Concurrency,
                        IvSpShipmentErrorKind.Validation => SaInvoiceErrorKind.Validation,
                        _ => SaInvoiceErrorKind.BusinessRule
                    });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(invoice.InvNo, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Invoice shipment deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("The shipment could not be saved because of a database conflict. Try again.", SaInvoiceErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice shipment failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("Unable to add shipment.", SaInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<SaInvoiceOperationResult> GetShipmentEditAsync(
        string invNo,
        int soLineNo,
        CancellationToken cancellationToken = default)
    {
        var no = (invNo ?? string.Empty).Trim();
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var invoice = await _invoices.GetWithDetailsAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (invoice is null)
        {
            return SaInvoiceOperationResult.Fail("Invoice was not found.", SaInvoiceErrorKind.NotFound);
        }

        var line = invoice.Details.FirstOrDefault(x => x.Line == soLineNo);
        if (line is null)
        {
            return SaInvoiceOperationResult.FailValidation("Invoice line was not found.");
        }

        var edit = await _shipments.GetShipmentEditAsync(
            db,
            new IvSpShipmentEditQuery
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode!,
                DocumentNo = no,
                SoLineNo = soLineNo,
                DocumentDate = invoice.InvDate.Date,
                ICode = line.ICode ?? string.Empty,
                FrWarehouse = line.FrWarehouse ?? string.Empty,
                RequestedStdQty = line.StdQty
            },
            cancellationToken);

        if (!edit.Succeeded)
        {
            return SaInvoiceOperationResult.Fail(edit.ErrorMessage ?? "Unable to load shipment edit.");
        }

        var doc = await GetAsync(no, cancellationToken);
        if (!doc.Succeeded || doc.Document is null)
        {
            return doc;
        }

        doc.Document.Shipment = edit.Lots.Select(x => new SaInvoiceShipmentLineDto
        {
            Line = x.SoLineNo,
            ICode = x.ICode,
            FromBalLocId = x.FromBalLocId,
            FrWarehouse = x.FrWarehouse,
            FrLocation = x.FrLocation,
            FrLotNo = x.FrLotNo,
            FrStdQty = x.FrStdQty,
            IStatus = x.IStatus,
            CurrentAvailableQty = x.CurrentAvailableQty,
            FailReason = x.FailReason == IvSpLotFailReason.None ? null : x.FailReason.ToString()
        }).ToList();
        return SaInvoiceOperationResult.OkDocument(doc.Document, doc.PostWarnings);
    }

    public async Task<SaInvoiceOperationResult> ReplaceShipmentLineAsync(
        string invNo,
        int soLineNo,
        IReadOnlyList<SaInvoiceShipmentLotRequest> lots,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default)
    {
        var no = (invNo ?? string.Empty).Trim();
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.", SaInvoiceErrorKind.Authorization);
        }

        if (rowVersion is null || rowVersion.Length == 0)
        {
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Invoice was not found.", SaInvoiceErrorKind.NotFound);
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail("Only NEW invoices can edit shipment.", SaInvoiceErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(invoice.RowVersion, rowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.Fail(
                    "This invoice was changed by another user. Reload before saving.",
                    SaInvoiceErrorKind.Concurrency);
            }

            db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = rowVersion;
            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);
            var line = invoice.Details.FirstOrDefault(x => x.Line == soLineNo);
            if (line is null || !IvSpFifoEligibility.IsShipmentRequired(line.StockControl, line.StdQty))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoiceOperationResult.FailValidation("Invoice line was not found or does not require shipment.");
            }

            invoice.ModifiedDate = DateTime.UtcNow;
            invoice.ModifiedBy = Truncate(context.UserId!, 10);
            TouchRowVersion(db, invoice);

            var replace = await _shipments.ReplaceShipmentLineAsync(
                db,
                new IvSpReplaceLineCommand
                {
                    CompanyCode = context.CompanyCode!,
                    BranchCode = context.BranchCode!,
                    LocationCode = context.LocationCode!,
                    UserId = context.UserId!,
                    DocumentNo = invoice.InvNo,
                    DocumentDate = invoice.InvDate.Date,
                    SoLineNo = soLineNo,
                    ICode = line.ICode ?? string.Empty,
                    IDesc = line.IDesc,
                    PersistedStdQty = line.StdQty,
                    StdUom = line.StdUom,
                    FrWarehouse = line.FrWarehouse ?? string.Empty,
                    UnitPrice = line.UnitPrice,
                    Lots = (lots ?? []).Select(x => new IvSpSubmittedLot
                    {
                        FromBalLocId = x.FromBalLocId,
                        IssueQty = x.IssueQty
                    }).ToList()
                },
                cancellationToken);

            if (!replace.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                var failDoc = MapDocument(invoice, invoice.CustName, null, []);
                failDoc.Shipment = replace.Lots.Select(x => new SaInvoiceShipmentLineDto
                {
                    Line = x.SoLineNo,
                    ICode = x.ICode,
                    FromBalLocId = x.FromBalLocId,
                    FrWarehouse = x.FrWarehouse,
                    FrLocation = x.FrLocation,
                    FrLotNo = x.FrLotNo,
                    FrStdQty = x.FrStdQty,
                    IStatus = x.IStatus,
                    CurrentAvailableQty = x.CurrentAvailableQty,
                    FailReason = x.FailReason == IvSpLotFailReason.None ? null : x.FailReason.ToString()
                }).ToList();
                return new SaInvoiceOperationResult
                {
                    Succeeded = false,
                    ErrorKind = SaInvoiceErrorKind.BusinessRule,
                    ErrorMessage = replace.ErrorMessage,
                    Document = failDoc,
                    InvNo = invoice.InvNo
                };
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(invoice.InvNo, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail(
                "This invoice was changed by another user. Reload before saving.",
                SaInvoiceErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Invoice shipment edit deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("The shipment could not be saved because of a database conflict. Try again.", SaInvoiceErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice shipment edit failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaInvoiceOperationResult.Fail("Unable to update shipment.", SaInvoiceErrorKind.Unexpected);
        }
    }

    public async Task<SaInvoiceOperationResult> PostAsync(
        IReadOnlyList<string>? invNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Post, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.");
        }

        var nos = NormalizeInvNos(invNos);
        if (nos.Count == 0)
        {
            return SaInvoiceOperationResult.Fail("No record selected.");
        }

        if (nos.Count > SaInvoiceLimits.MaxPostSelection)
        {
            return SaInvoiceOperationResult.Fail(
                $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices.");
        }

        var results = new List<SaInvoicePostingItemResult>();
        var stop = false;
        foreach (var no in nos)
        {
            if (stop)
            {
                results.Add(SaInvoicePostingItemResult.NotAttempted(no));
                continue;
            }

            try
            {
                var one = await PostOneAsync(context, no, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice POST failed for {InvNo}", no);
                results.Add(SaInvoicePostingItemResult.Failed(no, ex.Message));
                stop = true;
            }
        }

        return SaInvoiceOperationResult.OkPosting(results);
    }

    public async Task<SaInvoiceOperationResult> RollbackAsync(
        IReadOnlyList<string>? invNos,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaInvoiceOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Rollback, cancellationToken))
        {
            return SaInvoiceOperationResult.Fail("Not authorized.");
        }

        var nos = NormalizeInvNos(invNos);
        if (nos.Count == 0)
        {
            return SaInvoiceOperationResult.Fail("No record selected.");
        }

        if (nos.Count > SaInvoiceLimits.MaxPostSelection)
        {
            return SaInvoiceOperationResult.Fail(
                $"Select at most {SaInvoiceLimits.MaxPostSelection} invoices.");
        }

        var results = new List<SaInvoicePostingItemResult>();
        var stop = false;
        foreach (var no in nos)
        {
            if (stop)
            {
                results.Add(SaInvoicePostingItemResult.NotAttempted(no));
                continue;
            }

            try
            {
                var one = await RollbackOneAsync(context, no, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invoice ROLLBACK failed for {InvNo}", no);
                results.Add(SaInvoicePostingItemResult.Failed(no, ex.Message));
                stop = true;
            }
        }

        return SaInvoiceOperationResult.OkPosting(results);
    }

    private async Task<SaInvoicePostingItemResult> PostOneAsync(
        UserContext context,
        string invNo,
        CancellationToken cancellationToken,
        byte[]? expectedRowVersion = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, invNo, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(invNo, "Invoice was not found.");
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(
                    invNo,
                    SaInvoicePostReasonCodes.Concurrency,
                    "Invoice is not NEW.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(invoice.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(
                        invNo,
                        SaInvoicePostReasonCodes.Concurrency,
                        "Invoice was changed by another user.");
                }

                db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);

            var readiness = await ValidateCommercialReadinessAsync(db, invoice, context.CompanyCode!, cancellationToken);
            if (!readiness.Ok)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(
                    invNo,
                    readiness.FirstReasonCode!,
                    readiness.JoinedMessage());
            }

            var allocResult = await AllocateInvoiceLinesAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.UserId!,
                invoice,
                cancellationToken);
            if (!allocResult.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(
                    invNo,
                    allocResult.Code ?? SaSoReasonCodes.NotFound,
                    allocResult.Message ?? "Document allocation failed.");
            }

            // LinkDo keep-stock lines already shipped on DO — do not require a second SP batch.
            var stockLines = invoice.Details
                .Where(x => !x.LinkDo && IvSpFifoEligibility.IsShipmentRequired(x.StockControl, x.StdQty))
                .ToList();
            var batch = await _postingRepo.LockSpBatchByRefAsync(
                db, context.CompanyCode!, context.BranchCode!, invNo, cancellationToken);

            if (stockLines.Count > 0)
            {
                if (batch is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(invNo, "Add shipment before posting.");
                }

                if (batch.TrxDtTime.Date != invoice.InvDate.Date)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(
                        invNo,
                        "Some shipment date is not updated, please add shipment.");
                }

                var spDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
                var balLocIds = spDetails
                    .Where(d => d.FromBalLocId is > 0)
                    .Select(d => d.FromBalLocId!.Value)
                    .Distinct()
                    .ToList();

                var lockedBalances = new Dictionary<int, IvBalLocLockResult>();
                if (balLocIds.Count > 0)
                {
                    var rows = await db.IvBalLocs.AsNoTracking()
                        .Where(x =>
                            balLocIds.Contains(x.Id)
                            && x.CompanyCode == context.CompanyCode
                            && x.BranchCode == context.BranchCode)
                        .ToListAsync(cancellationToken);
                    var orderedIds = rows
                        .Select(x => new
                        {
                            x.Id,
                            Slice = IvStockSliceKey.Create(
                                x.CompanyCode, x.BranchCode, x.ICode, x.WhCode, x.LocCode, x.LotNo, x.IStatus)
                        })
                        .OrderBy(x => x.Slice)
                        .ThenBy(x => x.Id)
                        .Select(x => x.Id)
                        .ToList();

                    foreach (var id in orderedIds)
                    {
                        var locked = await _postingRepo.LockBalLocByIdForTenantAsync(
                            db, id, context.CompanyCode!, context.BranchCode!, cancellationToken);
                        if (locked is null)
                        {
                            await tx.RollbackAsync(cancellationToken);
                            return SaInvoicePostingItemResult.Failed(
                                invNo,
                                $"Source balance Id {id} was not found for this company/branch.");
                        }

                        lockedBalances[id] = locked;
                    }
                }

                var validate = await _shipments.ValidateShipmentForPostAsync(
                    db,
                    new IvSpValidatePostQuery
                    {
                        CompanyCode = context.CompanyCode!,
                        BranchCode = context.BranchCode!,
                        LocationCode = context.LocationCode!,
                        DocumentNo = invNo,
                        DocumentDate = invoice.InvDate.Date,
                        Batch = batch,
                        Details = spDetails,
                        RequiredLines = invoice.Details.Select(d => new IvSpRequiredLine
                        {
                            Line = d.Line,
                            ICode = d.ICode ?? string.Empty,
                            IDesc = d.IDesc,
                            StdQty = d.StdQty,
                            StdUom = d.StdUom,
                            FrWarehouse = d.FrWarehouse ?? string.Empty,
                            UnitPrice = d.UnitPrice,
                            StockControl = d.StockControl
                        }).ToList(),
                        LockedBalances = lockedBalances
                    },
                    cancellationToken);
                if (!validate.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(invNo, validate.ErrorMessage ?? "Shipment validation failed.");
                }

                var core = await _posting.PostStockOutInTransactionAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.UserId!,
                    batch.BatchNo,
                    IvTrxTypes.SalesOut,
                    cancellationToken);
                if (!core.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(invNo, core.ErrorMessage ?? "Stock post failed.");
                }
            }

            TestHookAfterStockCore?.Invoke();

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            invoice.Status = SaInvoiceStatuses.Posted;
            invoice.PostedDate = now;
            invoice.PostedBy = uid;
            invoice.ModifiedDate = now;
            invoice.ModifiedBy = uid;

            TestHookAfterInvoiceStatus?.Invoke();

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaInvoicePostingItemResult.Posted(invNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoicePostingItemResult.Failed(
                invNo,
                SaInvoicePostReasonCodes.Concurrency,
                "Invoice changed during post.");
        }
    }

    private async Task<SaInvoicePostingItemResult> RollbackOneAsync(
        UserContext context,
        string invNo,
        CancellationToken cancellationToken,
        byte[]? expectedRowVersion = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var invoice = await _invoices.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, invNo, cancellationToken);
            if (invoice is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(invNo, "Invoice was not found.");
            }

            if (!string.Equals(invoice.Status, SaInvoiceStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(invNo, "Only POSTED invoices can be rolled back.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(invoice.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(
                        invNo,
                        SaInvoicePostReasonCodes.Concurrency,
                        "Invoice was changed by another user.");
                }

                db.Entry(invoice).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            await db.Entry(invoice).Collection(x => x.Details).LoadAsync(cancellationToken);

            var reverse = await _docApplication.ReverseDocumentAllocationsAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.UserId!,
                SaDocTypes.Inv,
                invNo,
                cancellationToken);
            if (!reverse.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaInvoicePostingItemResult.Failed(
                    invNo,
                    reverse.Code ?? SaSoReasonCodes.NotFound,
                    reverse.Message ?? "Document allocation rollback failed.");
            }

            foreach (var detail in invoice.Details)
            {
                detail.SoConsumedQty = 0m;
            }

            var batch = await FindSpBatchAsync(db, context.CompanyCode!, context.BranchCode!, invNo, cancellationToken);
            if (batch is not null
                && string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                var core = await _posting.RollBackStockOutInTransactionAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.UserId!,
                    batch.BatchNo,
                    IvTrxTypes.SalesOut,
                    cancellationToken);
                if (!core.Succeeded)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaInvoicePostingItemResult.Failed(invNo, core.ErrorMessage ?? "Stock rollback failed.");
                }
            }

            TestHookAfterStockCore?.Invoke();

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            invoice.Status = SaInvoiceStatuses.New;
            invoice.RollbackDate = now;
            invoice.RollbackBy = uid;
            invoice.ModifiedDate = now;
            invoice.ModifiedBy = uid;

            TestHookAfterInvoiceStatus?.Invoke();

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaInvoicePostingItemResult.RolledBack(invNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaInvoicePostingItemResult.Failed(
                invNo,
                SaInvoicePostReasonCodes.Concurrency,
                "Invoice changed during rollback.");
        }
    }

    private async Task<PrepareOutcome> PrepareLinesAsync(
        AppDbContext db,
        SaInvoiceSaveRequest request,
        string companyCode,
        string branchCode,
        IReadOnlyList<SaInvoiceDetail>? existingDetails,
        string? excludeInvNo,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custCode = (request.CustCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(custCode))
        {
            errors["CustCode"] = "Customer is required.";
        }

        if (request.InvDate == default)
        {
            errors["InvDate"] = "Invoice date is required.";
        }

        var payCode = (request.PayCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(payCode))
        {
            errors["PayCode"] = "Payment term is required.";
        }
        else
        {
            var payOk = await db.IvMsCodes.AsNoTracking().AnyAsync(
                x => x.CodeType == IvMsCodeTypes.PayCode && x.Code == payCode,
                cancellationToken);
            if (!payOk)
            {
                errors["PayCode"] = $"Payment term '{payCode}' is not valid.";
            }
        }

        var currency = (request.Currency ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(currency))
        {
            errors["Currency"] = "Currency is required.";
        }

        if (errors.Count > 0 && (string.IsNullOrWhiteSpace(custCode) || request.InvDate == default))
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var customer = string.IsNullOrWhiteSpace(custCode)
            ? null
            : await _customers.GetByCodeAsync(db, companyCode, custCode, includeChildren: false, cancellationToken);
        if (!string.IsNullOrWhiteSpace(custCode) && (customer is null || !customer.IsActive))
        {
            errors["CustCode"] = "Customer was not found or is inactive.";
            return PrepareOutcome.Validation("Customer was not found or is inactive.", errors);
        }

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

        var headerTax = (request.TaxGrCode ?? string.Empty).Trim();
        var taxByCode = await db.SaTaxGroups.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode)
            .ToDictionaryAsync(x => x.TaxGrCode, x => x.Percentage, StringComparer.OrdinalIgnoreCase, cancellationToken);

        if (customer?.Taxable == true)
        {
            if (headerTax.Length == 0)
            {
                errors["TaxGrCode"] = "Tax group is required for a taxable customer.";
            }
            else if (!taxByCode.ContainsKey(headerTax))
            {
                errors["TaxGrCode"] = $"Tax group '{headerTax}' was not found.";
            }
        }
        else if (headerTax.Length > 0 && !taxByCode.ContainsKey(headerTax))
        {
            errors["TaxGrCode"] = $"Tax group '{headerTax}' was not found.";
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            errors["Lines"] = "Add at least one invoice line.";
        }
        else if (request.Lines.Count > short.MaxValue)
        {
            errors["Lines"] = "Too many invoice lines.";
        }

        if (request.Lines is { Count: > 0 })
        {
            var firstInclusive = request.Lines[0].IsInclusive;
            if (request.Lines.Any(x => x.IsInclusive != firstInclusive))
            {
                return PrepareOutcome.Fail(
                    "ST000032: All lines must use the same tax type (inclusive or exclusive).",
                    SaInvoiceErrorKind.BusinessRule);
            }
        }

        ValidateHeaderLengths(request, errors);

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var invDate = request.InvDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, invDate, cancellationToken);
        if (rateResult.Error is not null)
        {
            errors["Currency"] = rateResult.Error;
            return PrepareOutcome.Validation(rateResult.Error, errors);
        }

        var warehouses = await _common.ListActiveWarehousesAsync(companyCode, branchCode, cancellationToken);
        var warehouseSet = warehouses.Select(x => x.WarehouseCode).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackWh = warehouses.Select(x => x.WarehouseCode).FirstOrDefault();
        var soLock = await LockSalesOrdersForSaveAsync(
            db,
            companyCode,
            branchCode,
            existingDetails,
            request.Lines,
            cancellationToken);
        if (soLock.Failure is not null)
        {
            return soLock.Failure;
        }
        var salesOrdersByNo = soLock.Headers;

        var seenDoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (reqLine, idx) in (request.Lines ?? []).Select((x, i) => (x, i)))
        {
            if (reqLine is null || !reqLine.LinkDo)
            {
                continue;
            }

            var keyDoNo = (reqLine.DoNo ?? string.Empty).Trim();
            if (keyDoNo.Length == 0 || reqLine.DoLine is not > 0)
            {
                continue;
            }

            var doKey = $"{keyDoNo}|{reqLine.DoLine.Value}";
            if (!seenDoKeys.Add(doKey))
            {
                errors[$"Lines[{idx}].DoNo"] = $"Delivery order {keyDoNo} line {reqLine.DoLine} is already on this invoice.";
            }
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation(
                errors.Values.Any(x => x.Contains("already on this invoice", StringComparison.OrdinalIgnoreCase))
                    ? SaDocAllocationReasonCodes.Duplicate
                    : "Validation failed.",
                errors);
        }

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

            if (line.LinkDo)
            {
                var doNo = (line.DoNo ?? string.Empty).Trim();
                if (doNo.Length == 0 || line.DoLine is not > 0)
                {
                    errors[$"Lines[{lineNo - 1}].DoNo"] = "Delivery order and line are required for LinkDo lines.";
                    lineNo++;
                    continue;
                }

                var deliveryOrder = await db.SaDos
                    .Include(x => x.Details)
                    .FirstOrDefaultAsync(
                        x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DoNo == doNo,
                        cancellationToken);
                if (deliveryOrder is null)
                {
                    errors[$"Lines[{lineNo - 1}].DoNo"] = $"Delivery order {doNo} was not found.";
                    lineNo++;
                    continue;
                }

                if (!string.Equals(deliveryOrder.Status, SaDoStatuses.Posted, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"Lines[{lineNo - 1}].DoNo"] = $"Delivery order {doNo} must be posted.";
                    lineNo++;
                    continue;
                }

                if (!string.Equals(deliveryOrder.CustCode, custCode, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"Lines[{lineNo - 1}].DoNo"] = $"Delivery order {doNo} customer does not match.";
                    lineNo++;
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(deliveryOrder.Currency)
                    && !string.Equals(deliveryOrder.Currency, currency, StringComparison.OrdinalIgnoreCase))
                {
                    errors[$"Lines[{lineNo - 1}].DoNo"] = $"Delivery order {doNo} currency does not match.";
                    lineNo++;
                    continue;
                }

                var doDetail = deliveryOrder.Details.FirstOrDefault(x => x.Line == line.DoLine.Value);
                if (doDetail is null)
                {
                    errors[$"Lines[{lineNo - 1}].DoLine"] = $"Delivery order {doNo} line {line.DoLine} was not found.";
                    lineNo++;
                    continue;
                }

                // Continue into item validation; SO identity copied from DO line when present.
            }

            var item = await _stockMasters.GetByCodeAsync(db, companyCode, iCode, cancellationToken);
            if (item is null || !item.IsActive)
            {
                errors[$"Lines[{lineNo - 1}].ICode"] = $"Item '{iCode}' was not found or is inactive.";
                lineNo++;
                continue;
            }

            var qty = IvQty.Round(line.Qty);
            if (qty <= 0m)
            {
                errors[$"Lines[{lineNo - 1}].Qty"] = "Quantity must be greater than zero.";
            }

            var pack = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
            var stdQty = IvQty.Round(qty * pack);
            if (stdQty == 0m && qty > 0m)
            {
                errors[$"Lines[{lineNo - 1}].Qty"] = "Standard quantity must not be zero.";
            }

            var warehouse = (line.FrWarehouse ?? item.DefWarehouse ?? fallbackWh ?? string.Empty).Trim();
            if (item.StockControl)
            {
                if (string.IsNullOrWhiteSpace(warehouse))
                {
                    errors[$"Lines[{lineNo - 1}].FrWarehouse"] = "Warehouse is required.";
                }
                else if (!warehouseSet.Contains(warehouse))
                {
                    errors[$"Lines[{lineNo - 1}].FrWarehouse"] = $"Warehouse '{warehouse}' was not found.";
                }
            }

            var discError = ValidateLineDiscount(line, lineNo - 1, customer!.DiscountMethod);
            if (discError is not null)
            {
                errors[discError.Value.Key] = discError.Value.Value;
            }

            var taxGr = (line.TaxGrCode ?? string.Empty).Trim();
            decimal taxPercent = 0m;
            if (taxGr.Length > 0)
            {
                if (!taxByCode.TryGetValue(taxGr, out taxPercent))
                {
                    errors[$"Lines[{lineNo - 1}].TaxGrCode"] = $"Tax group '{taxGr}' was not found.";
                }
            }
            else if (customer!.Taxable == true)
            {
                if (headerTax.Length == 0 || !taxByCode.TryGetValue(headerTax, out taxPercent))
                {
                    errors["TaxGrCode"] = "Tax group is required for a taxable customer.";
                }
            }

            var soNo = (line.SoNo ?? string.Empty).Trim();
            short? soLine = null;
            short? custRel = null;
            var linkDo = line.LinkDo;
            var lineDoNo = (line.DoNo ?? string.Empty).Trim();
            short? lineDoLine = line.DoLine is > 0 ? line.DoLine : null;
            string? sellingUom = item.SellingUom;

            if (linkDo)
            {
                var doNo = lineDoNo;
                var deliveryOrder = await db.SaDos
                    .Include(x => x.Details)
                    .AsNoTracking()
                    .FirstAsync(
                        x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.DoNo == doNo,
                        cancellationToken);
                var doDetail = deliveryOrder.Details.First(x => x.Line == lineDoLine!.Value);
                soNo = (doDetail.SoNo ?? string.Empty).Trim();
                soLine = doDetail.SoLine is > 0 ? doDetail.SoLine : null;
                custRel = null;
                sellingUom = doDetail.SellingUom ?? sellingUom;

                if (HasSoReference(soNo, soLine))
                {
                    custRel = doDetail.CustRel is > 0 ? doDetail.CustRel : (short)1;
                    if (!salesOrdersByNo.TryGetValue(soNo, out var salesOrder))
                    {
                        errors[$"Lines[{lineNo - 1}].DoNo"] = $"Sales Order {soNo} was not found for delivery order {doNo}.";
                    }
                    else if (!string.Equals(salesOrder.CustCode, custCode, StringComparison.OrdinalIgnoreCase))
                    {
                        errors[$"Lines[{lineNo - 1}].DoNo"] = $"Sales Order {soNo} customer does not match document customer.";
                    }
                    else if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
                    {
                        errors[$"Lines[{lineNo - 1}].DoNo"] = $"Sales Order {soNo} is closed.";
                    }
                    else
                    {
                        custRel = salesOrder.CustRel;
                    }
                }
                else
                {
                    soNo = string.Empty;
                    soLine = null;
                    custRel = null;
                }
            }
            else if (soNo.Length > 0)
            {
                if (line.SoLine is not > 0)
                {
                    errors[$"Lines[{lineNo - 1}].SoLine"] = "Sales Order line is required.";
                }
                else
                {
                    soLine = line.SoLine.Value;

                    if (!salesOrdersByNo.TryGetValue(soNo, out var salesOrder))
                    {
                        errors[$"Lines[{lineNo - 1}].SoNo"] = $"Sales Order {soNo} was not found.";
                    }
                    else if (line.CustRel is > 0 && line.CustRel != salesOrder.CustRel)
                    {
                        return PrepareOutcome.Fail(SaSoReasonCodes.RevisedMessage, SaInvoiceErrorKind.Concurrency);
                    }
                    else if (!string.Equals(salesOrder.CustCode, custCode, StringComparison.OrdinalIgnoreCase))
                    {
                        errors[$"Lines[{lineNo - 1}].SoNo"] = $"Sales Order {soNo} customer does not match document customer.";
                    }
                    else if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
                    {
                        errors[$"Lines[{lineNo - 1}].SoNo"] = $"Sales Order {soNo} is closed.";
                    }
                    else
                    {
                        custRel = salesOrder.CustRel;
                        var soDetail = salesOrder.Details.FirstOrDefault(x => x.Line == soLine.Value && x.CustRel == salesOrder.CustRel);
                        if (soDetail is null)
                        {
                            errors[$"Lines[{lineNo - 1}].SoLine"] = $"Sales Order {soNo} line {soLine.Value} was not found.";
                        }
                        else if (sellingUom is not null
                            && !string.Equals(
                                NormalizeOptional(soDetail.SellingUom),
                                NormalizeOptional(sellingUom),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            errors[$"Lines[{lineNo - 1}].SoNo"] = $"Sales Order {soNo} line {soLine.Value} UOM does not match.";
                        }
                        else
                        {
                            sellingUom = soDetail.SellingUom ?? sellingUom;
                        }
                    }
                }
            }

            if (errors.Count > 0)
            {
                lineNo++;
                continue;
            }

            var state = new SaInvoiceLineCalcState
            {
                Qty = qty,
                UnitPrice = line.UnitPrice,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                ItemDiscAmount = line.ItemDiscAmount,
                ItemDiscAmount1 = line.ItemDiscAmount1,
                IsInclusive = line.IsInclusive,
                OrderType = line.OrderType
            };
            SaInvoiceCalc.CalculateLine(state, taxPercent, customer!.DecPoint == true, customer.DiscountMethod);
            calcStates.Add(state);

            prepared.Add(new PreparedLine
            {
                Line = lineNo,
                SoNo = soNo,
                SoLine = soLine,
                CustRel = custRel,
                LinkDo = linkDo,
                DoNo = linkDo ? lineDoNo : string.Empty,
                DoLine = linkDo ? lineDoLine : null,
                SoConsumedQty = 0m,
                ICode = iCode,
                IDesc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim(),
                Qty = qty,
                StdQty = stdQty,
                StdUom = item.StdUom,
                StdPackSize = item.StdPackSize,
                SellingUom = sellingUom,
                FrWarehouse = string.IsNullOrWhiteSpace(warehouse) ? null : warehouse,
                UnitPrice = line.UnitPrice,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                ItemDiscAmount = line.ItemDiscAmount,
                ItemDiscAmount1 = line.ItemDiscAmount1,
                IsInclusive = line.IsInclusive,
                TaxGrCode = taxGr.Length == 0 ? null : taxGr,
                OrderType = TruncateOptional(line.OrderType, 20),
                StockControl = item.StockControl,
                SellingGlCode = item.SellingGlCode,
                Classification = string.IsNullOrWhiteSpace(line.Classification)
                    ? TruncateOptional(item.Classification, 50)
                    : TruncateOptional(line.Classification, 50),
                Remarks = TruncateOptional(line.Remarks, 250),
                Calc = state
            });
            lineNo++;
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var anyLinkDo = prepared.Any(x => x.LinkDo);
        var anyDirectSo = prepared.Any(x => !x.LinkDo && !string.IsNullOrWhiteSpace(x.SoNo));
        if (anyLinkDo && anyDirectSo)
        {
            return PrepareOutcome.Validation(
                SaDocAllocationReasonCodes.MixForbidden,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Lines"] = "An invoice cannot mix direct Sales Order lines with Delivery Order lines."
                });
        }

        var reserveGate = await ValidateInvoiceSoReserveAsync(
            db,
            companyCode,
            branchCode,
            prepared,
            salesOrdersByNo,
            excludeInvNo,
            cancellationToken);
        if (reserveGate is not null)
        {
            return reserveGate;
        }

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(calcStates, 0m);
        foreach (var row in prepared)
        {
            row.Calc.LocalAmount = SaInvoiceCalc.Money(row.Calc.NetAmount * rateResult.Rate);
        }

        return PrepareOutcome.Ok(customer!, currency, rateResult.Rate, prepared);
    }

    private static async Task<PrepareOutcome?> ValidateInvoiceSoReserveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        List<PreparedLine> prepared,
        IReadOnlyDictionary<string, SaSo> salesOrdersByNo,
        string? excludeInvNo,
        CancellationToken cancellationToken)
    {
        var thisInvBySoLine = prepared
            .Where(x => !x.LinkDo && !string.IsNullOrWhiteSpace(x.SoNo) && x.SoLine is > 0)
            .GroupBy(x => (SoNo: x.SoNo.Trim(), CustRel: x.CustRel is > 0 ? x.CustRel.Value : (short)1, SoLine: x.SoLine!.Value), new SoLineGroupComparer())
            .ToDictionary(g => g.Key, g => SaSoQty.RoundQty(g.Sum(x => x.Qty)), new SoLineGroupComparer());

        if (thisInvBySoLine.Count == 0)
        {
            return null;
        }

        SaSoLineReserve.DocIdentity? excludeInv = null;
        var exclude = (excludeInvNo ?? string.Empty).Trim();
        if (exclude.Length > 0)
        {
            excludeInv = new SaSoLineReserve.DocIdentity(companyCode, branchCode, exclude);
        }

        var soNos = thisInvBySoLine.Keys.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sums = await SaSoLineReserve.SumBySoLinesAsync(
            db, companyCode, branchCode, soNos, excludeDo: null, excludeInv, cancellationToken);

        foreach (var ((soNo, custRel, soLine), thisSoInvQty) in thisInvBySoLine.OrderBy(x => x.Key.SoNo, SaSoLockOrder.Comparer).ThenBy(x => x.Key.CustRel).ThenBy(x => x.Key.SoLine))
        {
            if (!salesOrdersByNo.TryGetValue(soNo, out var salesOrder))
            {
                return PrepareOutcome.Fail($"Sales Order {soNo} was not found.", SaInvoiceErrorKind.BusinessRule);
            }

            if (custRel != salesOrder.CustRel)
            {
                return PrepareOutcome.Fail(SaSoReasonCodes.RevisedMessage, SaInvoiceErrorKind.Concurrency);
            }

            var soDetail = salesOrder.Details.FirstOrDefault(x => x.Line == soLine && x.CustRel == salesOrder.CustRel);
            if (soDetail is null)
            {
                return PrepareOutcome.Fail(
                    $"Sales Order {soNo} line {soLine} was not found.",
                    SaInvoiceErrorKind.BusinessRule);
            }

            var eval = SaSoLineReserve.Evaluate(
                soNo,
                soLine,
                soDetail.OrderQty,
                soDetail.DeliveredQty,
                SaSoLineReserve.GetSums(sums, soNo, custRel, soLine),
                thisDoQty: 0m,
                thisSoInvQty);
            if (!eval.Succeeded)
            {
                return PrepareOutcome.Validation(
                    SaSoLineReserve.FormatOverAllocate(eval),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Lines"] = SaSoLineReserve.FormatOverAllocate(eval)
                    });
            }
        }

        return null;
    }

    private sealed class SoLineGroupComparer : IEqualityComparer<(string SoNo, short CustRel, short SoLine)>
    {
        public bool Equals((string SoNo, short CustRel, short SoLine) x, (string SoNo, short CustRel, short SoLine) y) =>
            x.SoLine == y.SoLine
            && x.CustRel == y.CustRel
            && string.Equals(x.SoNo, y.SoNo, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string SoNo, short CustRel, short SoLine) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SoNo ?? string.Empty), obj.CustRel, obj.SoLine);
    }

    private async Task<(string? Error, decimal Rate)> ResolveCurrRateAsync(
        AppDbContext db,
        string currency,
        DateTime invDate,
        CancellationToken cancellationToken)
    {
        var isHome = string.Equals(currency, SaInvoiceCalc.HomeCurrency, StringComparison.OrdinalIgnoreCase);
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency && x.Status && x.StartDate <= invDate && x.EndDate >= invDate)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            if (isHome)
            {
                return (null, 1m);
            }

            return ($"No currency rate for {currency} on {invDate:yyyy-MM-dd}.", 0m);
        }

        var decimalRate = Convert.ToDecimal(rate.Value);
        if (!isHome && decimalRate == 1m)
        {
            return ("Non-home currency rate cannot be 1.", 0m);
        }

        return (null, decimalRate);
    }

    private static void ApplyCalculatedTotals(SaInvoice invoice, IReadOnlyList<PreparedLine> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines.Select(x => x.Calc).ToList(), decPoint);
        invoice.GrossAmnt = header.GrossAmnt;
        invoice.Taxes = header.Taxes;
        invoice.TotAmnt = header.TotAmnt;
    }

    private static void AddDetails(SaInvoice invoice, IReadOnlyList<PreparedLine> lines)
    {
        foreach (var line in lines)
        {
            invoice.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = invoice.CompanyCode,
                BranchCode = invoice.BranchCode,
                InvNo = invoice.InvNo,
                Line = line.Line,
                SoNo = line.SoNo,
                SoLine = line.SoLine,
                CustRel = line.CustRel,
                LinkDo = line.LinkDo,
                DoNo = line.DoNo,
                DoLine = line.DoLine,
                SoConsumedQty = line.SoConsumedQty,
                ICode = line.ICode,
                IDesc = line.IDesc,
                Qty = line.Qty,
                StdQty = line.StdQty,
                StdUom = line.StdUom,
                FrWarehouse = line.FrWarehouse,
                UnitPrice = line.UnitPrice,
                Amount = line.Calc.Amount,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                ItemDiscAmount = line.ItemDiscAmount,
                ItemDiscAmount1 = line.ItemDiscAmount1,
                IsInclusive = line.IsInclusive,
                TaxGrCode = line.TaxGrCode,
                TaxAmt = line.Calc.TaxAmt,
                NetAmount = line.Calc.NetAmount,
                LocalAmount = line.Calc.LocalAmount,
                OrderType = line.OrderType,
                StockControl = line.StockControl,
                SellingGlCode = line.SellingGlCode,
                Classification = line.Classification,
                Remarks = line.Remarks
            });
        }
    }

    private async Task<IvTrxBatch?> FindSpBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string invNo,
        CancellationToken cancellationToken)
    {
        var rows = await db.IvTrxBatches
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.TrxType == IvTrxTypes.SalesOut
                && x.RefNo == invNo)
            .ToListAsync(cancellationToken);
        if (rows.Count > 1)
        {
            throw new InvalidOperationException($"Multiple SP batches exist for invoice {invNo}.");
        }

        return rows.SingleOrDefault();
    }

    private async Task<(IReadOnlyDictionary<string, SaSo> Headers, PrepareOutcome? Failure)> LockSalesOrdersForSaveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<SaInvoiceDetail>? existingDetails,
        IReadOnlyList<SaInvoiceLineRequest>? requestedLines,
        CancellationToken cancellationToken)
    {
        var soNos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var soNo in (existingDetails ?? [])
            .Select(x => x.SoNo)
            .Concat((requestedLines ?? []).Where(x => x is not null).Select(x => x.SoNo ?? string.Empty))
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0))
        {
            soNos.Add(soNo);
        }

        var doRefs = (existingDetails ?? [])
            .Where(x => x.LinkDo && !string.IsNullOrWhiteSpace(x.DoNo) && x.DoLine is > 0)
            .Select(x => (DoNo: x.DoNo.Trim(), Line: x.DoLine!.Value))
            .Concat(
                (requestedLines ?? [])
                    .Where(x => x is not null && x.LinkDo
                        && !string.IsNullOrWhiteSpace(x.DoNo) && x.DoLine is > 0)
                    .Select(x => (DoNo: x.DoNo!.Trim(), Line: x.DoLine!.Value)))
            .Distinct()
            .ToList();

        foreach (var (doNo, line) in doRefs)
        {
            var doDetail = await db.SaDoDetails.AsNoTracking()
                .Where(x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.DoNo == doNo
                    && x.Line == line)
                .Select(x => new { x.SoNo, x.SoLine })
                .FirstOrDefaultAsync(cancellationToken);
            if (doDetail is not null && !string.IsNullOrWhiteSpace(doDetail.SoNo) && doDetail.SoLine is > 0)
            {
                soNos.Add(doDetail.SoNo.Trim());
            }
        }

        var ordered = soNos.OrderBy(x => x, SaSoLockOrder.Comparer).ToList();
        if (ordered.Count == 0)
        {
            return (new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase), null);
        }

        if (ordered.Count > SaSoLimits.MaxDistinctSoHeaders)
        {
            return (
                new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase),
                PrepareOutcome.Validation(
                    SaSoReasonCodes.TooManyHeadersMessage,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Lines"] = SaSoReasonCodes.TooManyHeadersMessage
                    }));
        }

        var headers = new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase);
        foreach (var soNo in ordered)
        {
            var salesOrder = await _salesOrders.LockForUpdateAsync(
                db,
                companyCode,
                branchCode,
                soNo,
                cancellationToken);
            if (salesOrder is null)
            {
                return (
                    headers,
                    PrepareOutcome.Fail($"Sales Order {soNo} was not found.", SaInvoiceErrorKind.BusinessRule));
            }

            await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            headers[soNo] = salesOrder;
        }

        return (headers, null);
    }

    private async Task<SaDocAllocationResult> AllocateInvoiceLinesAsync(
        AppDbContext db,
        string company,
        string branch,
        string userId,
        SaInvoice invoice,
        CancellationToken cancellationToken)
    {
        var soInv = invoice.Details
            .Where(x => !x.LinkDo && HasSoReference(x.SoNo, x.SoLine))
            .OrderBy(x => x.Line)
            .Select(x => new SaDocAllocationLine
            {
                SourceDocId = x.SoNo.Trim(),
                SourceLineId = x.SoLine!.Value,
                TargetDocId = invoice.InvNo,
                TargetLineId = (short)x.Line,
                AppliedQty = SaSoQty.RoundQty(x.Qty),
                AppliedAmount = x.NetAmount,
                SellingUom = null,
                CustCode = invoice.CustCode,
                Currency = invoice.Currency
            })
            .ToList();

        var doInv = invoice.Details
            .Where(x => x.LinkDo)
            .OrderBy(x => x.Line)
            .Select(x => new SaDocAllocationLine
            {
                SourceDocId = (x.DoNo ?? string.Empty).Trim(),
                SourceLineId = x.DoLine ?? 0,
                TargetDocId = invoice.InvNo,
                TargetLineId = (short)x.Line,
                AppliedQty = SaSoQty.RoundQty(x.Qty),
                AppliedAmount = x.NetAmount,
                SellingUom = null,
                CustCode = invoice.CustCode,
                Currency = invoice.Currency
            })
            .ToList();

        if (soInv.Count > 0 && doInv.Count > 0)
        {
            return SaDocAllocationResult.Fail(
                SaDocAllocationReasonCodes.MixForbidden,
                "An invoice cannot mix direct Sales Order lines with Delivery Order lines.");
        }

        SaDocAllocationResult result;
        if (doInv.Count > 0)
        {
            if (doInv.Any(x => x.SourceDocId.Length == 0 || x.SourceLineId <= 0))
            {
                return SaDocAllocationResult.Fail(
                    SaDocAllocationReasonCodes.NotFound,
                    "LinkDo invoice lines require DONo and DOLine.");
            }

            result = await _docApplication.AllocateDOToInvoiceAsync(
                db, company, branch, userId, invoice.Currency, doInv, cancellationToken);
        }
        else
        {
            result = await _docApplication.AllocateSOToInvoiceAsync(
                db, company, branch, userId, invoice.Currency, soInv, cancellationToken);
        }

        if (!result.Succeeded)
        {
            return result;
        }

        var byTarget = result.Lines.ToDictionary(x => x.TargetLineId);
        foreach (var detail in invoice.Details)
        {
            detail.SoConsumedQty = byTarget.TryGetValue((short)detail.Line, out var row)
                ? SaSoQty.RoundQty(row.SoConsumedQty)
                : 0m;
        }

        return result;
    }

    private static SaInvoiceDocument MapDocument(
        SaInvoice invoice,
        string? custName,
        IvTrxBatch? sp,
        IReadOnlyList<IvTrxBatchDetail> spDetails,
        IReadOnlyDictionary<(string SoNo, short CustRel), string?>? custPoBySo = null)
    {
        var shippedByLine = spDetails
            .GroupBy(x => x.SoLineNo ?? 0)
            .ToDictionary(g => (int)g.Key, g => IvQty.Round(g.Sum(x => x.FrStdQty ?? 0m)));

        var lines = invoice.Details.OrderBy(x => x.Line).Select(x =>
        {
            var shipped = shippedByLine.GetValueOrDefault(x.Line);
            var complete = !x.StockControl || shipped == IvQty.Round(x.StdQty);
            return new SaInvoiceLineDto
            {
                Line = x.Line,
                SoNo = x.SoNo,
                SoLine = x.SoLine,
                CustRel = x.CustRel,
                CustPo = SaDocCustPoLookup.Resolve(custPoBySo, x.SoNo, x.CustRel),
                LinkDo = x.LinkDo,
                DoNo = x.DoNo,
                DoLine = x.DoLine,
                SoConsumedQty = x.SoConsumedQty,
                ICode = x.ICode ?? string.Empty,
                IDesc = x.IDesc,
                Qty = x.Qty,
                StdQty = x.StdQty,
                StdUom = x.StdUom,
                FrWarehouse = x.FrWarehouse,
                UnitPrice = x.UnitPrice,
                Amount = x.Amount,
                ItemDiscount = x.ItemDiscount,
                ItemDiscount2 = x.ItemDiscount2,
                ItemDiscount3 = x.ItemDiscount3,
                ItemDiscount4 = x.ItemDiscount4,
                ItemDiscount5 = x.ItemDiscount5,
                ItemDiscount6 = x.ItemDiscount6,
                ItemDiscAmount = x.ItemDiscAmount,
                ItemDiscAmount1 = x.ItemDiscAmount1,
                IsInclusive = x.IsInclusive,
                TaxGrCode = x.TaxGrCode,
                TaxAmt = x.TaxAmt,
                NetAmount = x.NetAmount,
                LocalAmount = x.LocalAmount,
                OrderType = x.OrderType,
                StockControl = x.StockControl,
                SellingGlCode = x.SellingGlCode,
                Classification = x.Classification,
                Remarks = x.Remarks,
                ShipQty = shipped,
                ShipmentComplete = complete
            };
        }).ToList();

        return new SaInvoiceDocument
        {
            InvNo = invoice.InvNo,
            InvDate = invoice.InvDate,
            Status = invoice.Status,
            DoNo = invoice.DoNo,
            CustCode = invoice.CustCode,
            CustName = invoice.CustName ?? custName,
            InvPrefix = invoice.InvPrefix,
            Currency = invoice.Currency,
            CurrRate = invoice.CurrRate,
            PayCode = invoice.PayCode,
            TaxGrCode = invoice.TaxGrCode,
            SalesmanCode = invoice.SalesmanCode,
            PoNo = invoice.PoNo,
            Remark = invoice.Remark,
            InvName = invoice.InvName,
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
            InvEmail = invoice.InvEmail,
            DueDate = invoice.DueDate,
            ArGlCode = invoice.ArGlCode,
            BuyerTin = invoice.BuyerTin,
            BuyerBrn = invoice.BuyerBrn,
            BuyerRegType = invoice.BuyerRegType,
            GstregNo = invoice.GstregNo,
            CustType = invoice.CustType,
            CustGroupCode = invoice.CustGroupCode,
            AreaCode = invoice.AreaCode,
            IndustryCode = invoice.IndustryCode,
            ChannelCode = invoice.ChannelCode,
            ShipName = invoice.ShipName,
            ShipAddress1 = invoice.ShipAddress1,
            ShipAddress2 = invoice.ShipAddress2,
            ShipAddress3 = invoice.ShipAddress3,
            ShipCity = invoice.ShipCity,
            ShipState = invoice.ShipState,
            ShipPostalCode = invoice.ShipPostalCode,
            ShipCountry = invoice.ShipCountry,
            ShipTel = invoice.ShipTel,
            ShipFax = invoice.ShipFax,
            GrossAmnt = invoice.GrossAmnt,
            Taxes = invoice.Taxes,
            TotAmnt = invoice.TotAmnt,
            ShipmentComplete = lines.All(x => x.ShipmentComplete),
            SpBatchNo = sp?.BatchNo,
            SpBatchStatus = sp?.BatchStatus,
            RowVersion = invoice.RowVersion ?? [],
            Lines = lines,
            Shipment = spDetails.OrderBy(x => x.TrxLineNo).Select(x => new SaInvoiceShipmentLineDto
            {
                Line = x.SoLineNo ?? 0,
                ICode = x.ICode,
                FromBalLocId = x.FromBalLocId,
                FrWarehouse = x.FrWarehouse,
                FrLocation = x.FrLocation,
                FrLotNo = x.FrLotNo,
                FrStdQty = x.FrStdQty ?? 0m,
                IStatus = x.IStatus
            }).ToList()
        };
    }

    private static async Task<Dictionary<string, string>> LoadCustNamesAsync(
        AppDbContext db,
        string companyCode,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var rows = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode && codes.Contains(x.CustCode))
            .Select(x => new { x.CustCode, x.CustName })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(x => x.CustCode, x => x.CustName, StringComparer.OrdinalIgnoreCase);
    }

    private static List<(string ICode, decimal StdQty, string Wh, bool StockControl)> SnapshotIdentity(
        IEnumerable<SaInvoiceDetail> details) =>
        details
            .OrderBy(x => x.Line)
            .Select(x => ((x.ICode ?? string.Empty).Trim(), IvQty.Round(x.StdQty), (x.FrWarehouse ?? string.Empty).Trim(), x.StockControl))
            .ToList();

    private static bool IdentityEquals(
        List<(string ICode, decimal StdQty, string Wh, bool StockControl)> left,
        List<(string ICode, decimal StdQty, string Wh, bool StockControl)> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i].ICode, right[i].ICode, StringComparison.OrdinalIgnoreCase)
                || left[i].StdQty != right[i].StdQty
                || !string.Equals(left[i].Wh, right[i].Wh, StringComparison.OrdinalIgnoreCase)
                || left[i].StockControl != right[i].StockControl)
            {
                return false;
            }
        }

        return true;
    }

    private static List<string> NormalizeInvNos(IReadOnlyList<string>? invNos) =>
        (invNos ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.SalesInvoice, permission, cancellationToken);

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

    private static void TouchRowVersion(AppDbContext db, SaInvoice invoice)
    {
        // SQL Server rowversion is DB-generated. SQLite tests require an explicit CLR token.
        if (!db.Database.IsSqlServer())
        {
            invoice.RowVersion = Guid.NewGuid().ToByteArray();
        }
    }

    private static void BackfillCommercialHeader(SaInvoiceSaveRequest request, SaCust customer)
    {
        var useMainBill = customer.AppInvoice == true;
        if (string.IsNullOrWhiteSpace(request.InvName))
        {
            request.InvName = useMainBill ? customer.CustName : customer.InvName;
        }

        if (string.IsNullOrWhiteSpace(request.InvAddress1))
        {
            request.InvAddress1 = useMainBill ? customer.Address1 : customer.InvAddress1;
        }

        if (string.IsNullOrWhiteSpace(request.InvCountry))
        {
            request.InvCountry = useMainBill ? customer.Country : customer.InvCountry;
        }

        if (string.IsNullOrWhiteSpace(request.SalesmanCode))
        {
            request.SalesmanCode = customer.SalesmanCode;
        }

        if (string.IsNullOrWhiteSpace(request.InvTel))
        {
            request.InvTel = useMainBill ? customer.Tel : customer.InvTel;
        }

        if (string.IsNullOrWhiteSpace(request.InvCity))
        {
            request.InvCity = useMainBill ? customer.City : customer.InvCity;
        }

        if (string.IsNullOrWhiteSpace(request.InvPostalCode))
        {
            request.InvPostalCode = useMainBill ? customer.PostalCode : customer.InvPostalCode;
        }
    }

    /// <summary>
    /// Server-owned snapshots (DueDate, AR GL, KPI, e-invoice IDs) vs user-owned bill/ship/pay.
    /// BuyerTin/BuyerBrn/InvEmail/line Classification preserve when already set on the request.
    /// </summary>
    private static async Task ApplyMasterSnapshotsAsync(
        AppDbContext db,
        SaInvoice invoice,
        SaInvoiceSaveRequest request,
        SaCust customer,
        IReadOnlyList<PreparedLine> lines,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var payCode = (request.PayCode ?? string.Empty).Trim();
        var payDays = 0;
        if (payCode.Length > 0)
        {
            var term = await db.SaPaymentTerms.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.CompanyCode == companyCode && x.PayCode == payCode,
                    cancellationToken);
            payDays = term?.Days ?? 0;
        }

        invoice.DueDate = invoice.InvDate.Date.AddDays(payDays);
        invoice.ArGlCode = TruncateOptional(customer.GlCode, 20);
        invoice.CustType = TruncateOptional(customer.CustType, 20);
        invoice.CustGroupCode = TruncateOptional(customer.CustGroupCode, 20);
        invoice.AreaCode = TruncateOptional(customer.AreaCode, 20);
        invoice.IndustryCode = TruncateOptional(customer.IndustryCode, 20);
        invoice.ChannelCode = TruncateOptional(customer.ChannelCode, 20);
        invoice.BuyerRegType = TruncateOptional(customer.RegType, 20);
        invoice.GstregNo = TruncateOptional(customer.GstregNo, 50);

        invoice.BuyerTin = string.IsNullOrWhiteSpace(request.BuyerTin)
            ? TruncateOptional(customer.TinNo, 20)
            : TruncateOptional(request.BuyerTin, 20);
        invoice.BuyerBrn = string.IsNullOrWhiteSpace(request.BuyerBrn)
            ? TruncateOptional(customer.CustBrn, 50)
            : TruncateOptional(request.BuyerBrn, 50);
        invoice.InvEmail = string.IsNullOrWhiteSpace(request.InvEmail)
            ? TruncateOptional(customer.Email, 100)
            : TruncateOptional(request.InvEmail, 100);

        // Classification already resolved onto PreparedLine during PrepareLinesAsync.
        _ = lines;
    }

    public static bool HasTax(decimal taxes) => SaInvoiceCalc.HasTax(taxes);

    internal static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>
    /// Freeline / non-stock: no inventory item code (null/whitespace ICode).
    /// Stock/inventory lines are everything else with a resolvable ICode.
    /// </summary>
    internal static bool IsFreeline(SaInvoiceDetail line) =>
        string.IsNullOrWhiteSpace(line.ICode);

    private async Task<SaInvoiceOperationResult> OkDocumentWithPostWarningsAsync(
        AppDbContext db,
        SaInvoice invoice,
        SaInvoiceDocument document,
        string companyCode,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> warnings = [];
        if (string.Equals(invoice.Status, SaInvoiceStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            var readiness = await ValidateCommercialReadinessAsync(db, invoice, companyCode, cancellationToken);
            warnings = readiness.Errors.Select(e => e.Message).ToList();
        }

        return SaInvoiceOperationResult.OkDocument(document, warnings);
    }

    private static void ApplyHeaderSnapshots(SaInvoice invoice, SaInvoiceSaveRequest request, bool customerChanged)
    {
        _ = customerChanged;
        invoice.PayCode = TruncateOptional(request.PayCode, 20);
        invoice.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        invoice.SalesmanCode = TruncateOptional(request.SalesmanCode, 20);
        invoice.PoNo = TruncateOptional(request.PoNo, 50);
        invoice.Remark = TruncateOptional(request.Remark, 500);
        invoice.InvName = UpperSnapshot(request.InvName, 100);
        invoice.InvAddress1 = UpperSnapshot(request.InvAddress1, 100);
        invoice.InvAddress2 = UpperSnapshot(request.InvAddress2, 100);
        invoice.InvAddress3 = UpperSnapshot(request.InvAddress3, 100);
        invoice.InvAddress4 = UpperSnapshot(request.InvAddress4, 100);
        invoice.InvCity = UpperSnapshot(request.InvCity, 50);
        invoice.InvState = UpperSnapshot(request.InvState, 50);
        invoice.InvPostalCode = UpperSnapshot(request.InvPostalCode, 20);
        invoice.InvCountry = UpperSnapshot(request.InvCountry, 50);
        invoice.InvTel = TruncateOptional(request.InvTel, 50);
        invoice.InvFax = TruncateOptional(request.InvFax, 50);
        invoice.ShipName = UpperSnapshot(request.ShipName, 100);
        invoice.ShipAddress1 = UpperSnapshot(request.ShipAddress1, 100);
        invoice.ShipAddress2 = UpperSnapshot(request.ShipAddress2, 100);
        invoice.ShipAddress3 = UpperSnapshot(request.ShipAddress3, 100);
        invoice.ShipCity = UpperSnapshot(request.ShipCity, 50);
        invoice.ShipState = UpperSnapshot(request.ShipState, 50);
        invoice.ShipPostalCode = UpperSnapshot(request.ShipPostalCode, 20);
        invoice.ShipCountry = UpperSnapshot(request.ShipCountry, 50);
        invoice.ShipTel = TruncateOptional(request.ShipTel, 50);
        invoice.ShipFax = TruncateOptional(request.ShipFax, 50);
    }

    private sealed class CommercialReadinessResult
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

    private async Task<CommercialReadinessResult> ValidateCommercialReadinessAsync(
        AppDbContext db,
        SaInvoice inv,
        string companyCode,
        CancellationToken cancellationToken)
    {
        var result = new CommercialReadinessResult();

        var salesmanCode = Norm(inv.SalesmanCode);
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
                nameof(inv.SalesmanCode),
                "Salesman is missing, unknown, or inactive.",
                SaInvoicePostReasonCodes.SalesmanInvalid);
        }

        if (inv.DueDate is null)
        {
            result.Add(
                nameof(inv.DueDate),
                "Due date is required.",
                SaInvoicePostReasonCodes.DueDateMissing);
        }

        if (string.IsNullOrWhiteSpace(inv.ArGlCode))
        {
            result.Add(
                nameof(inv.ArGlCode),
                "Customer AR GL code is missing.",
                SaInvoicePostReasonCodes.ArGlMissing);
        }

        if (SaInvoiceCalc.HasTax(inv.Taxes))
        {
            var taxCode = Norm(inv.TaxGrCode);
            if (taxCode is null)
            {
                result.Add(
                    nameof(inv.TaxGrCode),
                    "Tax group is required when the document has tax.",
                    SaInvoicePostReasonCodes.TaxGlMissing);
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
                        SaInvoicePostReasonCodes.TaxGlMissing);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(inv.InvName))
        {
            result.Add(nameof(inv.InvName), "Billing name is required.", SaInvoicePostReasonCodes.BuyerAddress);
        }

        if (string.IsNullOrWhiteSpace(inv.InvAddress1))
        {
            result.Add(nameof(inv.InvAddress1), "Billing address is required.", SaInvoicePostReasonCodes.BuyerAddress);
        }

        if (string.IsNullOrWhiteSpace(inv.InvCity))
        {
            result.Add(nameof(inv.InvCity), "Billing city is required.", SaInvoicePostReasonCodes.BuyerAddress);
        }

        if (string.IsNullOrWhiteSpace(inv.InvPostalCode))
        {
            result.Add(nameof(inv.InvPostalCode), "Billing postal code is required.", SaInvoicePostReasonCodes.BuyerAddress);
        }

        if (string.IsNullOrWhiteSpace(inv.InvCountry))
        {
            result.Add(nameof(inv.InvCountry), "Billing country is required.", SaInvoicePostReasonCodes.BuyerAddress);
        }

        if (string.IsNullOrWhiteSpace(inv.InvTel) && string.IsNullOrWhiteSpace(inv.InvEmail))
        {
            result.Add(
                nameof(inv.InvTel),
                "Telephone or email is required.",
                SaInvoicePostReasonCodes.BuyerContact);
        }

        if (Norm(inv.BuyerTin) is null && Norm(inv.BuyerBrn) is null)
        {
            result.Add(
                nameof(inv.BuyerTin),
                "Buyer TIN or BRN is required.",
                SaInvoicePostReasonCodes.BuyerId);
        }

        foreach (var line in inv.Details.OrderBy(x => x.Line))
        {
            var needsSalesGl = !IsFreeline(line) || line.Amount != 0m;
            if (needsSalesGl && string.IsNullOrWhiteSpace(line.SellingGlCode))
            {
                result.Add(
                    $"Lines[{line.Line - 1}].SellingGlCode",
                    $"Sales GL is missing on line {line.Line}.",
                    SaInvoicePostReasonCodes.LineSalesGlMissing);
            }
        }

        foreach (var line in inv.Details.OrderBy(x => x.Line))
        {
            if (!IsFreeline(line) && string.IsNullOrWhiteSpace(line.Classification))
            {
                result.Add(
                    $"Lines[{line.Line - 1}].Classification",
                    $"Classification is missing on line {line.Line}.",
                    SaInvoicePostReasonCodes.LineClassification);
            }
        }

        return result;
    }

    private static string? UpperSnapshot(string? value, int maxLength)
    {
        var trimmed = TruncateOptional(value, maxLength);
        return trimmed?.ToUpperInvariant();
    }

    private static void ValidateHeaderLengths(SaInvoiceSaveRequest request, Dictionary<string, string> errors)
    {
        void Check(string field, string? value, int max)
        {
            if (!string.IsNullOrEmpty(value) && value.Trim().Length > max)
            {
                errors[field] = $"{field} must be at most {max} characters.";
            }
        }

        Check(nameof(request.PayCode), request.PayCode, 20);
        Check(nameof(request.TaxGrCode), request.TaxGrCode, 20);
        Check(nameof(request.SalesmanCode), request.SalesmanCode, 20);
        Check(nameof(request.PoNo), request.PoNo, 50);
        Check(nameof(request.Remark), request.Remark, 500);
        Check(nameof(request.InvName), request.InvName, 100);
        Check(nameof(request.InvAddress1), request.InvAddress1, 100);
        Check(nameof(request.InvAddress2), request.InvAddress2, 100);
        Check(nameof(request.InvAddress3), request.InvAddress3, 100);
        Check(nameof(request.InvAddress4), request.InvAddress4, 100);
        Check(nameof(request.InvCity), request.InvCity, 50);
        Check(nameof(request.InvState), request.InvState, 50);
        Check(nameof(request.InvPostalCode), request.InvPostalCode, 20);
        Check(nameof(request.InvCountry), request.InvCountry, 50);
        Check(nameof(request.InvTel), request.InvTel, 50);
        Check(nameof(request.InvFax), request.InvFax, 50);
        Check(nameof(request.ShipName), request.ShipName, 100);
        Check(nameof(request.ShipAddress1), request.ShipAddress1, 100);
        Check(nameof(request.ShipAddress2), request.ShipAddress2, 100);
        Check(nameof(request.ShipAddress3), request.ShipAddress3, 100);
        Check(nameof(request.ShipCity), request.ShipCity, 50);
        Check(nameof(request.ShipState), request.ShipState, 50);
        Check(nameof(request.ShipPostalCode), request.ShipPostalCode, 20);
        Check(nameof(request.ShipCountry), request.ShipCountry, 50);
        Check(nameof(request.ShipTel), request.ShipTel, 50);
        Check(nameof(request.ShipFax), request.ShipFax, 50);
    }

    private static KeyValuePair<string, string>? ValidateLineDiscount(
        SaInvoiceLineRequest line,
        int index,
        string? discMethod)
    {
        var percents = new[]
        {
            line.ItemDiscount, line.ItemDiscount2, line.ItemDiscount3,
            line.ItemDiscount4, line.ItemDiscount5, line.ItemDiscount6
        };
        var amounts = new[] { line.ItemDiscAmount, line.ItemDiscAmount1 };
        var hasPct = percents.Any(x => x != 0m);
        var hasAmt = amounts.Any(x => x != 0m);
        var prefix = $"Lines[{index}]";

        if (hasPct && hasAmt)
        {
            return new($"{prefix}.ItemDiscount", "Use either percent or amount discount, not both.");
        }

        if (percents.Any(x => x < 0m || x > 100m))
        {
            return new($"{prefix}.ItemDiscount", "Discount percent must be between 0 and 100.");
        }

        if (amounts.Any(x => x < 0m))
        {
            return new($"{prefix}.ItemDiscAmount", "Discount amount cannot be negative.");
        }

        if (line.UnitPrice == 0m && (hasPct || hasAmt))
        {
            return new($"{prefix}.UnitPrice", "Discount is not allowed when unit price is zero.");
        }

        var perUnit = SaInvoiceCalc.CalculateDiscountPerUnit(
            line.UnitPrice,
            line.ItemDiscount,
            line.ItemDiscount2,
            line.ItemDiscount3,
            line.ItemDiscount4,
            line.ItemDiscount5,
            line.ItemDiscount6,
            line.ItemDiscAmount,
            line.ItemDiscAmount1,
            discMethod);
        if (perUnit > line.UnitPrice)
        {
            return new($"{prefix}.ItemDiscount", "Discount cannot exceed unit price.");
        }

        return null;
    }

    private static bool RequestHasSoReferences(IReadOnlyList<SaInvoiceLineRequest>? lines) =>
        (lines ?? []).Any(x => x is not null && !string.IsNullOrWhiteSpace(x.SoNo));

    private static bool HasSoReference(string? soNo, short? soLine) =>
        !string.IsNullOrWhiteSpace(soNo) && soLine is > 0;

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static bool RowVersionsEqual(byte[]? left, byte[]? right) =>
        left is not null && right is not null && left.SequenceEqual(right);

    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is SqlException sql)
        {
            return sql.Number is 2601 or 2627;
        }

        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PrepareOutcome
    {
        public string? Error { get; init; }
        public SaInvoiceErrorKind Kind { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public SaCust? Customer { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; }
        public List<PreparedLine>? Lines { get; init; }

        public static PrepareOutcome Ok(SaCust customer, string currency, decimal rate, List<PreparedLine> lines) =>
            new() { Customer = customer, Currency = currency, CurrRate = rate, Lines = lines };

        public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = message, Kind = SaInvoiceErrorKind.Validation, Errors = errors };

        public static PrepareOutcome Fail(string message, SaInvoiceErrorKind kind) =>
            new() { Error = message, Kind = kind };

        public SaInvoiceOperationResult ToFail() =>
            Kind == SaInvoiceErrorKind.Validation
                ? SaInvoiceOperationResult.FailValidation(Error ?? "Validation failed.", Errors)
                : SaInvoiceOperationResult.Fail(Error ?? "Unable to save the invoice.", Kind);
    }

    private sealed class PreparedLine
    {
        public int Line { get; init; }
        public string SoNo { get; init; } = string.Empty;
        public short? SoLine { get; init; }
        public short? CustRel { get; init; }
        public bool LinkDo { get; init; }
        public string DoNo { get; init; } = string.Empty;
        public short? DoLine { get; init; }
        public decimal SoConsumedQty { get; init; }
        public string ICode { get; init; } = string.Empty;
        public string? IDesc { get; init; }
        public decimal Qty { get; init; }
        public decimal StdQty { get; init; }
        public string? StdUom { get; init; }
        public decimal? StdPackSize { get; init; }
        public string? SellingUom { get; init; }
        public string? FrWarehouse { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal ItemDiscount { get; init; }
        public decimal ItemDiscount2 { get; init; }
        public decimal ItemDiscount3 { get; init; }
        public decimal ItemDiscount4 { get; init; }
        public decimal ItemDiscount5 { get; init; }
        public decimal ItemDiscount6 { get; init; }
        public decimal ItemDiscAmount { get; init; }
        public decimal ItemDiscAmount1 { get; init; }
        public bool IsInclusive { get; init; }
        public string? TaxGrCode { get; init; }
        public string? OrderType { get; init; }
        public bool StockControl { get; init; }
        public string? SellingGlCode { get; init; }
        public string? Classification { get; init; }
        public string? Remarks { get; init; }
        public SaInvoiceLineCalcState Calc { get; init; } = new();
    }

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
