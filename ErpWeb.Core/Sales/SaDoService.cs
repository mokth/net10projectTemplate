using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed class SaDoService : ISaDoService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ISaDoRepository _dos;
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
    private readonly ILogger<SaDoService> _logger;

    public SaDoService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ISaDoRepository dos,
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
        ILogger<SaDoService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _dos = dos;
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

    // ─────────────────────────── Lookups ───────────────────────────

    public async Task<SaDoOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var items = await _stockMasters.ListActiveForLookupAsync(context.CompanyCode!, cancellationToken);
        var warehouses = await _common.ListActiveWarehousesAsync(
            context.CompanyCode!, context.BranchCode!, cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customers = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustCode)
            .Select(x => new SaDoCustomerLookupRow
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
            .Select(x => new SaDoTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var payCodes = await _custLookups.ListPayCodesForAssignmentAsync(cancellationToken);

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

        return SaDoOperationResult.OkLookups(
            items.Select(x => new SaDoItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                StdUom = x.StdUom,
                StdPackSize = x.StdPackSize,
                SellingPrice = x.SellingPrice,
                TaxGroup = x.TaxGroup,
                StockControl = x.StockControl,
                DefWarehouse = x.DefWarehouse
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

    public async Task<SaDoOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime doDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var code = (custCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
        {
            return SaDoOperationResult.FailValidation("Customer is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["CustCode"] = "Customer is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, includeChildren: false, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            return SaDoOperationResult.Fail("Customer was not found or is inactive.", SaDoErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(customer.Currency)
            ? SaInvoiceCalc.HomeCurrency
            : customer.Currency.Trim();
        var date = doDate == default ? _dates.Today.Date : doDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, date, cancellationToken);
        var useMainBill = customer.AppInvoice == true;
        var useMainShip = customer.AppShip == true;

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

        return SaDoOperationResult.OkDefaults(new SaDoCustomerDefaults
        {
            CustCode = customer.CustCode,
            CustName = customer.CustName,
            Currency = currency,
            CurrRate = rateResult.Error is null ? rateResult.Rate : 0m,
            CurrRateValid = rateResult.Error is null && rateResult.Rate != 0m,
            TaxGrCode = customer.TaxGrCode,
            Taxable = customer.Taxable,
            PayCode = customer.PayCode,
            SalesRep = customer.SalesmanCode,
            DiscountMethod = customer.DiscountMethod,
            DecPoint = customer.DecPoint,
            InvName = useMainBill ? customer.CustName : customer.InvName,
            InvAddress1 = useMainBill ? customer.Address1 : customer.InvAddress1,
            InvAddress2 = useMainBill ? customer.Address2 : customer.InvAddress2,
            InvAddress3 = useMainBill ? customer.Address3 : customer.InvAddress3,
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

    public async Task<SaDoOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime doDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var curr = (currency ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(curr))
        {
            return SaDoOperationResult.FailValidation("Currency is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Currency"] = "Currency is required." });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var date = doDate == default ? _dates.Today.Date : doDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, curr, date, cancellationToken);
        if (rateResult.Error is not null)
        {
            return SaDoOperationResult.Fail(rateResult.Error, SaDoErrorKind.Validation);
        }

        return SaDoOperationResult.OkRate(rateResult.Rate, valid: true);
    }

    // ─────────────────────────── Search / Get ───────────────────────────

    public async Task<SaDoOperationResult> SearchAsync(
        SaDoListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        query ??= new SaDoListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _dos.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            new SaDoSearchArgs(
                SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
                Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
                DateFrom: query.DateFrom,
                DateTo: query.DateTo,
                SortField: query.SortField,
                SortDescending: query.SortDescending,
                Skip: query.Skip,
                Take: query.Take),
            cancellationToken);

        var doNos = rows.Select(x => x.DoNo).ToList();
        var lineCounts = doNos.Count == 0
            ? []
            : await db.SaDoDetails.AsNoTracking()
                .Where(d => d.CompanyCode == context.CompanyCode
                    && d.BranchCode == context.BranchCode
                    && doNos.Contains(d.DoNo))
                .GroupBy(d => d.DoNo)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken);
        var countByDo = lineCounts.ToDictionary(x => x.Key, x => x.Count, StringComparer.OrdinalIgnoreCase);

        // Determine shipment completeness per DO: a DO is complete if all its stock lines have exact ship qty.
        // For the list view we compute this via the SP batch details count vs line count heuristic.
        // Full completeness is computed only in GetAsync; list view uses a simplified flag from the entity.

        return SaDoOperationResult.OkList(new SaDoListPage
        {
            TotalCount = total,
            Rows = rows.Select(x =>
            {
                var totals = SalesDocTotals.FromDeliveryOrder(x.GrossAmnt, x.Taxes, x.TotAmnt);
                return new SaDoListRow
                {
                    DoNo = x.DoNo,
                    DoDate = x.DoDate,
                    Status = x.Status,
                    CustCode = x.CustCode,
                    CustName = x.CustName,
                    TotAmnt = x.TotAmnt,
                    GrossExTax = totals.GrossExTax,
                    Tax = totals.Tax,
                    Totals = totals,
                    LineCount = countByDo.GetValueOrDefault(x.DoNo),
                    ShipmentComplete = false, // computed in GetAsync per document
                    CreatedDate = x.CreatedDate,
                    CreatedBy = x.CreatedBy,
                    RowVersion = x.RowVersion ?? []
                };
            }).ToList()
        });
    }

    public async Task<SaDoOperationResult> GetAsync(
        string doNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var no = (doNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaDoOperationResult.Fail("DO number is required.", SaDoErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var deliveryOrder = await _dos.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (deliveryOrder is null)
        {
            return SaDoOperationResult.Fail("Delivery order was not found.", SaDoErrorKind.NotFound);
        }

        var doRef = SaDoSpRefs.ToRefNo(no);
        var sp = await FindSpBatchAsync(db, context.CompanyCode!, context.BranchCode!, doRef, cancellationToken);
        IReadOnlyList<IvTrxBatchDetail> spDetails = [];
        if (sp is not null)
        {
            spDetails = await _postingRepo.LoadDetailsForBatchAsync(db, sp.Id, cancellationToken);
        }

        var custPoBySo = await SaDocCustPoLookup.LoadAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            deliveryOrder.Details.Select(x => (x.SoNo, x.CustRel)),
            cancellationToken);
        return SaDoOperationResult.OkDocument(MapDocument(deliveryOrder, sp, spDetails, custPoBySo));
    }

    // ─────────────────────────── Save New ───────────────────────────

    public async Task<SaDoOperationResult> SaveNewAsync(
        SaDoSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaDoOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
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
                excludeDoNo: null,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var doDate = request.DoDate == default ? _dates.Today.Date : request.DoDate.Date;
            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    "DO",
                    "",
                    doDate,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "DO numbering is not configured for this company/branch.",
                    SaDoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "DO numbering is not configured correctly. Contact an administrator.",
                    SaDoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "The next DO number exceeds the configured length.",
                    SaDoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "The DO could not be saved because of a database conflict. Try again.",
                    SaDoErrorKind.Unexpected);
            }

            var doNo = issued.DocumentNumber;
            var prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim();

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);

            // Stamp tenant scope (InventoryLeftoverSite pattern): Company, Branch, Location come from scope.
            var deliveryOrder = new SaDo
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode,
                DoNo = doNo,
                DoDate = doDate,
                Status = SaDoStatuses.New,
                BillingStatus = SaDualStatuses.None,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                Prefix = prefix,
                CreatedDate = now,
                CreatedBy = uid
            };

            TouchRowVersion(db, deliveryOrder);
            ApplyHeaderSnapshots(deliveryOrder, request);
            ApplyCalculatedTotals(deliveryOrder, prepared.Lines!, prepared.Customer!.DecPoint == true);
            db.SaDos.Add(deliveryOrder);
            AddDetails(deliveryOrder, prepared.Lines!);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Delivery order saved. UserId={UserId} Company={Company} DoNo={DoNo}",
                context.UserId, context.CompanyCode, doNo);

            return await GetAsync(doNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("DO number is already used.", SaDoErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "DO save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("The DO could not be saved because of a database conflict. Try again.", SaDoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DO save failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("Unable to save the delivery order.", SaDoErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Update ───────────────────────────

    /// <summary>
    /// Edits a NEW delivery order. Details are deleted and re-inserted, renumbering from 1
    /// (R10.6). Safe today because edit is restricted to NEW DOs and the allocation ledger only
    /// keys <c>TargetLineId</c> for posted documents. <b>Pre-condition:</b> this becomes unsafe
    /// if NEW-DO allocation is ever introduced — the ledger would then reference line numbers
    /// that are renumbered here.
    /// </summary>
    public async Task<SaDoOperationResult> UpdateAsync(
        string doNo,
        SaDoSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaDoOperationResult.FailValidation("Save request is required.");
        }

        var no = (doNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaDoOperationResult.FailValidation("DO number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Delivery order was not found.", SaDoErrorKind.NotFound);
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Only NEW delivery orders can be edited.", SaDoErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(deliveryOrder.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "This delivery order was changed by another user. Reload before saving.",
                    SaDoErrorKind.Concurrency);
            }

            db.Entry(deliveryOrder).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            var previousIdentity = SnapshotIdentity(deliveryOrder.Details);
            var customerChanged = !string.Equals(deliveryOrder.CustCode, (request.CustCode ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

            var requestedLines = (request.Lines ?? []).Where(x => x is not null).ToList();
            var requestedExistingLines = requestedLines
                .Where(x => x.Line > 0)
                .Select(x => x.Line)
                .ToHashSet();

            foreach (var existing in deliveryOrder.Details.OrderBy(x => x.Line))
            {
                var hasAllocation = await _docApplication.HasAllocationForDoLineAsync(
                    db, context.CompanyCode!, context.BranchCode!, deliveryOrder.DoNo, existing.Line, cancellationToken);
                if (!hasAllocation)
                {
                    continue;
                }

                if (!requestedExistingLines.Contains(existing.Line))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoOperationResult.Fail(
                        $"Delivery order line {existing.Line} cannot be deleted because it already has allocations.",
                        SaDoErrorKind.BusinessRule);
                }

                var requested = requestedLines.First(x => x.Line == existing.Line);
                var requestedSoNo = (requested.SoNo ?? string.Empty).Trim();
                var requestedSoLine = requested.SoLine;
                var existingSoNo = (existing.SoNo ?? string.Empty).Trim();
                if (!string.Equals(requestedSoNo, existingSoNo, StringComparison.OrdinalIgnoreCase)
                    || requestedSoLine != existing.SoLine
                    || (requested.CustRel is > 0 && requested.CustRel != existing.CustRel))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoOperationResult.Fail(
                        $"Delivery order line {existing.Line} Sales Order lineage cannot change while allocations exist.",
                        SaDoErrorKind.BusinessRule);
                }

                var invoicedQty = await _docApplication.SumDoInvoicedQtyAsync(
                    db, context.CompanyCode!, context.BranchCode!, deliveryOrder.DoNo, existing.Line, cancellationToken);
                if (IvQty.Round(requested.Qty) < invoicedQty)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoOperationResult.Fail(
                        $"Delivery order line {existing.Line} quantity cannot be lower than invoiced quantity {invoicedQty:n4}.",
                        SaDoErrorKind.BusinessRule);
                }
            }

            if (customerChanged
                && (deliveryOrder.Details.Any(x => HasSoReference(x.SoNo, x.SoLine))
                    || RequestHasSoReferences(request.Lines)))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.FailValidation(
                    "Customer cannot be changed when the delivery order contains Sales Order references.",
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["CustCode"] = "Customer cannot be changed when the delivery order contains Sales Order references."
                    });
            }

            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                deliveryOrder.Details.ToList(),
                excludeDoNo: no,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var doRef = SaDoSpRefs.ToRefNo(no);
            if (customerChanged)
            {
                await _shipments.ReleaseShipmentReservationAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.LocationCode!,
                    doRef,
                    removeBatch: true,
                    cancellationToken);
                deliveryOrder.CustName = UpperSnapshot(prepared.Customer!.CustName, 200);
            }

            var doDate = request.DoDate == default ? _dates.Today.Date : request.DoDate.Date;
            deliveryOrder.DoDate = doDate;
            deliveryOrder.CustCode = prepared.Customer!.CustCode;
            deliveryOrder.Currency = prepared.Currency;
            deliveryOrder.CurrRate = prepared.CurrRate;
            deliveryOrder.ModifiedDate = DateTime.UtcNow;
            deliveryOrder.ModifiedBy = Truncate(context.UserId!, 10);
            TouchRowVersion(db, deliveryOrder);
            ApplyHeaderSnapshots(deliveryOrder, request);

            db.SaDoDetails.RemoveRange(deliveryOrder.Details);
            deliveryOrder.Details.Clear();
            AddDetails(deliveryOrder, prepared.Lines!);
            ApplyCalculatedTotals(deliveryOrder, prepared.Lines!, prepared.Customer!.DecPoint == true);

            if (!customerChanged && !IdentityEquals(previousIdentity, SnapshotIdentity(deliveryOrder.Details)))
            {
                await _shipments.ReleaseShipmentReservationAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    context.LocationCode!,
                    doRef,
                    removeBatch: true,
                    cancellationToken);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "DO update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("The DO could not be saved because of a database conflict. Try again.", SaDoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DO update failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("Unable to save the delivery order.", SaDoErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Delete ───────────────────────────

    public async Task<SaDoOperationResult> DeleteAsync(
        IReadOnlyList<SaDoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaDoOperationResult.Fail("Select at least one delivery order.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var item in keyed)
        {
            if (item.RowVersion.Length == 0)
            {
                return SaDoOperationResult.Fail(
                    $"DO {item.DoNo}: row version is required for delete.",
                    SaDoErrorKind.Concurrency);
            }

            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, item.DoNo, cancellationToken);
            if (deliveryOrder is null)
            {
                return SaDoOperationResult.Fail($"Delivery order {item.DoNo} was not found.");
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                return SaDoOperationResult.Fail($"Delivery order {item.DoNo} cannot be deleted because it is not NEW.");
            }

            if (!RowVersionsEqual(deliveryOrder.RowVersion, item.RowVersion))
            {
                return SaDoOperationResult.Fail(
                    $"Delivery order {item.DoNo} was changed by another user. Reload before deleting.",
                    SaDoErrorKind.Concurrency);
            }

            var doRef = SaDoSpRefs.ToRefNo(item.DoNo);
            await _shipments.ReleaseShipmentReservationAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.LocationCode!,
                doRef,
                removeBatch: true,
                cancellationToken);
            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            db.SaDoDetails.RemoveRange(deliveryOrder.Details);
            db.SaDos.Remove(deliveryOrder);
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return SaDoOperationResult.Ok();
    }

    // ─────────────────────────── Shipment ───────────────────────────

    public async Task<SaDoOperationResult> AddShipmentAsync(
        string doNo,
        bool overwriteExisting,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default)
    {
        var no = (doNo ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(no))
        {
            return SaDoOperationResult.FailValidation("DO number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        if (rowVersion is null || rowVersion.Length == 0)
        {
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Delivery order was not found.", SaDoErrorKind.NotFound);
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Only NEW delivery orders can add shipment.", SaDoErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(deliveryOrder.RowVersion, rowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "This delivery order was changed by another user. Reload before saving.",
                    SaDoErrorKind.Concurrency);
            }

            db.Entry(deliveryOrder).Property(x => x.RowVersion).OriginalValue = rowVersion;
            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

            var doRef = SaDoSpRefs.ToRefNo(no);
            var batch = await _postingRepo.LockSpBatchByRefAsync(
                db, context.CompanyCode!, context.BranchCode!, doRef, cancellationToken);

            if (batch is not null
                && string.Equals(batch.BatchStatus, IvBatchStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Posted shipment cannot be rebuilt.", SaDoErrorKind.BusinessRule);
            }

            if (batch is not null && !overwriteExisting)
            {
                var existingDetails = await _postingRepo.LoadDetailsForBatchAsync(db, batch.Id, cancellationToken);
                if (existingDetails.Count > 0)
                {
                    var preview = MapDocument(deliveryOrder, batch, existingDetails);
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoOperationResult.OkConfirmation(
                        preview,
                        "ST000001: Shipment already exists. Confirm to overwrite.");
                }
            }

            var uid = Truncate(context.UserId!, 10);
            deliveryOrder.ModifiedDate = DateTime.UtcNow;
            deliveryOrder.ModifiedBy = uid;
            TouchRowVersion(db, deliveryOrder);

            var shipResult = await _shipments.CreateOrReplaceShipmentAsync(
                db,
                new IvSpCreateOrReplaceCommand
                {
                    CompanyCode = context.CompanyCode!,
                    BranchCode = context.BranchCode!,
                    LocationCode = context.LocationCode!,
                    UserId = context.UserId!,
                    DocumentNo = doRef,
                    DocumentDate = deliveryOrder.DoDate.Date,
                    DoNo = no,  // stamps DoNo on SP details
                    OverwriteExisting = true,
                    RequiredLines = deliveryOrder.Details
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
                return SaDoOperationResult.Fail(
                    shipResult.ErrorMessage ?? "Unable to add shipment.",
                    shipResult.ErrorKind switch
                    {
                        IvSpShipmentErrorKind.Concurrency => SaDoErrorKind.Concurrency,
                        IvSpShipmentErrorKind.Validation => SaDoErrorKind.Validation,
                        _ => SaDoErrorKind.BusinessRule
                    });
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "DO shipment deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("The shipment could not be saved because of a database conflict. Try again.", SaDoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DO shipment failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("Unable to add shipment.", SaDoErrorKind.Unexpected);
        }
    }

    public async Task<SaDoOperationResult> GetShipmentEditAsync(
        string doNo,
        int soLineNo,
        CancellationToken cancellationToken = default)
    {
        var no = (doNo ?? string.Empty).Trim();
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var deliveryOrder = await _dos.GetWithDetailsAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (deliveryOrder is null)
        {
            return SaDoOperationResult.Fail("Delivery order was not found.", SaDoErrorKind.NotFound);
        }

        // §5.4: a force-closed DO retains its shipment batch as an immutable tombstone. The editor
        // must not offer to change it — surface the read-only state instead.
        if (string.Equals(deliveryOrder.Status, SaDoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return SaDoOperationResult.Fail(
                "This delivery order was force-closed; its shipment is read-only.",
                SaDoErrorKind.BusinessRule);
        }

        var line = deliveryOrder.Details.FirstOrDefault(x => x.Line == soLineNo);
        if (line is null)
        {
            return SaDoOperationResult.FailValidation("DO line was not found.");
        }

        var doRef = SaDoSpRefs.ToRefNo(no);
        var edit = await _shipments.GetShipmentEditAsync(
            db,
            new IvSpShipmentEditQuery
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode!,
                DocumentNo = doRef,
                SoLineNo = soLineNo,
                DocumentDate = deliveryOrder.DoDate.Date,
                ICode = line.ICode ?? string.Empty,
                FrWarehouse = line.FrWarehouse ?? string.Empty,
                RequestedStdQty = line.StdQty
            },
            cancellationToken);

        if (!edit.Succeeded)
        {
            return SaDoOperationResult.Fail(edit.ErrorMessage ?? "Unable to load shipment edit.");
        }

        var doc = await GetAsync(no, cancellationToken);
        if (!doc.Succeeded || doc.Document is null)
        {
            return doc;
        }

        doc.Document.Shipment = edit.Lots.Select(x => new SaDoShipmentLineDto
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
        return SaDoOperationResult.OkDocument(doc.Document);
    }

    public async Task<SaDoOperationResult> ReplaceShipmentLineAsync(
        string doNo,
        int soLineNo,
        IReadOnlyList<SaDoShipmentLotRequest> lots,
        byte[]? rowVersion,
        CancellationToken cancellationToken = default)
    {
        var no = (doNo ?? string.Empty).Trim();
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        if (rowVersion is null || rowVersion.Length == 0)
        {
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Delivery order was not found.", SaDoErrorKind.NotFound);
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail("Only NEW delivery orders can edit shipment.", SaDoErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(deliveryOrder.RowVersion, rowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.Fail(
                    "This delivery order was changed by another user. Reload before saving.",
                    SaDoErrorKind.Concurrency);
            }

            db.Entry(deliveryOrder).Property(x => x.RowVersion).OriginalValue = rowVersion;
            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            var line = deliveryOrder.Details.FirstOrDefault(x => x.Line == soLineNo);
            if (line is null || !IvSpFifoEligibility.IsShipmentRequired(line.StockControl, line.StdQty))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoOperationResult.FailValidation("DO line was not found or does not require shipment.");
            }

            deliveryOrder.ModifiedDate = DateTime.UtcNow;
            deliveryOrder.ModifiedBy = Truncate(context.UserId!, 10);
            TouchRowVersion(db, deliveryOrder);

            var doRef = SaDoSpRefs.ToRefNo(no);
            var replace = await _shipments.ReplaceShipmentLineAsync(
                db,
                new IvSpReplaceLineCommand
                {
                    CompanyCode = context.CompanyCode!,
                    BranchCode = context.BranchCode!,
                    LocationCode = context.LocationCode!,
                    UserId = context.UserId!,
                    DocumentNo = doRef,
                    DocumentDate = deliveryOrder.DoDate.Date,
                    DoNo = no,
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
                var failDoc = MapDocument(deliveryOrder, null, []);
                failDoc.Shipment = replace.Lots.Select(x => new SaDoShipmentLineDto
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
                return new SaDoOperationResult
                {
                    Succeeded = false,
                    ErrorKind = SaDoErrorKind.BusinessRule,
                    ErrorMessage = replace.ErrorMessage,
                    Document = failDoc,
                    DoNo = deliveryOrder.DoNo
                };
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail(
                "This delivery order was changed by another user. Reload before saving.",
                SaDoErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "DO shipment edit deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("The shipment could not be saved because of a database conflict. Try again.", SaDoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DO shipment edit failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaDoOperationResult.Fail("Unable to update shipment.", SaDoErrorKind.Unexpected);
        }
    }

    // ─────────────────────────── Post ───────────────────────────

    public async Task<SaDoOperationResult> PostAsync(
        IReadOnlyList<SaDoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Post, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaDoOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaDoLimits.MaxPostSelection)
        {
            return SaDoOperationResult.Fail($"Select at most {SaDoLimits.MaxPostSelection} delivery orders.");
        }

        var results = new List<SaDoPostingItemResult>();
        var stop = false;
        foreach (var item in keyed)
        {
            if (stop)
            {
                results.Add(SaDoPostingItemResult.NotAttempted(item.DoNo));
                continue;
            }

            try
            {
                var one = await PostOneAsync(context, item.DoNo, item.RowVersion, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DO POST failed for {DoNo}", item.DoNo);
                results.Add(SaDoPostingItemResult.Failed(item.DoNo, ex.Message));
                stop = true;
            }
        }

        return SaDoOperationResult.OkPosting(results);
    }

    // ─────────────────────────── Rollback ───────────────────────────

    public async Task<SaDoOperationResult> RollbackAsync(
        IReadOnlyList<SaDoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Rollback, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaDoOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaDoLimits.MaxPostSelection)
        {
            return SaDoOperationResult.Fail($"Select at most {SaDoLimits.MaxPostSelection} delivery orders.");
        }

        var results = new List<SaDoPostingItemResult>();
        var stop = false;
        foreach (var item in keyed)
        {
            if (stop)
            {
                results.Add(SaDoPostingItemResult.NotAttempted(item.DoNo));
                continue;
            }

            try
            {
                var one = await RollbackOneAsync(context, item.DoNo, item.RowVersion, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DO ROLLBACK failed for {DoNo}", item.DoNo);
                results.Add(SaDoPostingItemResult.Failed(item.DoNo, ex.Message));
                stop = true;
            }
        }

        return SaDoOperationResult.OkPosting(results);
    }

    // ─────────────────────────── ForceClose ───────────────────────────

    public async Task<SaDoOperationResult> ForceCloseAsync(
        IReadOnlyList<SaDoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Close, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaDoOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaDoLimits.MaxPostSelection)
        {
            return SaDoOperationResult.Fail($"Select at most {SaDoLimits.MaxPostSelection} delivery orders.");
        }

        var results = new List<SaDoPostingItemResult>();
        var stop = false;
        foreach (var item in keyed)
        {
            if (stop)
            {
                results.Add(SaDoPostingItemResult.NotAttempted(item.DoNo));
                continue;
            }

            try
            {
                var one = await ForceCloseOneAsync(context, item.DoNo, item.RowVersion, cancellationToken);
                results.Add(one);
                if (!one.Succeeded)
                {
                    stop = true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DO FORCE-CLOSE failed for {DoNo}", item.DoNo);
                results.Add(SaDoPostingItemResult.Failed(item.DoNo, ex.Message));
                stop = true;
            }
        }

        return SaDoOperationResult.OkPosting(results);
    }

    public async Task<SaDoOperationResult> GetBillableLinesAsync(
        string custCode,
        string? currency,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaDoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaDoOperationResult.Fail("Not authorized.", SaDoErrorKind.Authorization);
        }

        var cust = (custCode ?? string.Empty).Trim();
        if (cust.Length == 0)
        {
            return SaDoOperationResult.FailValidation("Customer is required.");
        }

        var curr = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var headers = await db.SaDos.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.CustCode == cust
                && x.Status == SaDoStatuses.Posted
                && (curr == null || x.Currency == null || x.Currency == curr))
            .OrderBy(x => x.DoNo)
            .ToListAsync(cancellationToken);

        if (headers.Count == 0)
        {
            return SaDoOperationResult.OkBillableLines([]);
        }

        var doNos = headers.Select(x => x.DoNo).ToList();
        var details = await db.SaDoDetails.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && doNos.Contains(x.DoNo))
            .OrderBy(x => x.DoNo)
            .ThenBy(x => x.Line)
            .ToListAsync(cancellationToken);

        var billed = await db.SaDocApplications.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.SourceDocType == SaDocTypes.Do
                && doNos.Contains(x.SourceDocId)
                && x.TargetDocType == SaDocTypes.Inv)
            .GroupBy(x => new { x.SourceDocId, x.SourceLineId })
            .Select(g => new { g.Key.SourceDocId, g.Key.SourceLineId, Qty = g.Sum(x => x.AppliedQty) })
            .ToListAsync(cancellationToken);

        var billedMap = billed.ToDictionary(
            x => (x.SourceDocId.ToUpperInvariant(), x.SourceLineId),
            x => x.Qty);

        var custPoBySo = await SaDocCustPoLookup.LoadAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            details.Select(x => (x.SoNo, x.CustRel)),
            cancellationToken);

        var rows = new List<SaDoBillableLineDto>();
        foreach (var detail in details)
        {
            var used = billedMap.GetValueOrDefault((detail.DoNo.ToUpperInvariant(), detail.Line));
            var remaining = SaSoQty.RoundQty(detail.Qty - used);
            if (remaining <= 0m)
            {
                continue;
            }

            rows.Add(new SaDoBillableLineDto
            {
                DoNo = detail.DoNo,
                Line = detail.Line,
                SoNo = (detail.SoNo ?? string.Empty).Trim(),
                SoLine = detail.SoLine is > 0 ? detail.SoLine.Value : (short)0,
                CustRel = detail.SoLine is > 0 && detail.CustRel is > 0
                    ? detail.CustRel.Value
                    : detail.SoLine is > 0
                        ? (short)1
                        : (short)0,
                CustPo = SaDocCustPoLookup.Resolve(custPoBySo, detail.SoNo, detail.CustRel, detail.CustPo),
                ICode = detail.ICode ?? string.Empty,
                IDesc = detail.IDesc,
                Qty = detail.Qty,
                RemainingBillableQty = remaining,
                UnitPrice = detail.UnitPrice,
                PricingSource = detail.PricingSource,
                PricingRef = detail.PricingRef,
                OriginalUnitPrice = detail.OriginalUnitPrice,
                OverrideReason = detail.OverrideReason,
                SellingUom = detail.SellingUom,
                FrWarehouse = detail.FrWarehouse,
                StockControl = detail.StockControl
            });
        }

        return SaDoOperationResult.OkBillableLines(rows);
    }

    // ─────────────────────────── Private: PostOneAsync ───────────────────────────

    private async Task<SaDoPostingItemResult> PostOneAsync(
        UserContext context,
        string doNo,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, doNo, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, "Delivery order was not found.");
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(
                    doNo,
                    SaDoPostReasonCodes.Concurrency,
                    "Delivery order is not NEW.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(deliveryOrder.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(
                        doNo,
                        SaDoPostReasonCodes.Concurrency,
                        "Delivery order was changed by another user.");
                }
                db.Entry(deliveryOrder).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

            if (deliveryOrder.Details.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, SaDoPostReasonCodes.NoLines, "Delivery order has no lines.");
            }

            var allocations = BuildSoToDoAllocations(deliveryOrder);
            var allocate = await _docApplication.AllocateSOToDOAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.UserId!,
                deliveryOrder.Currency,
                allocations,
                cancellationToken);
            if (!allocate.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(
                    doNo,
                    allocate.Code ?? SaSoReasonCodes.NotFound,
                    allocate.Message ?? "Sales Order allocation failed.");
            }

            ApplyAllocatedQuantities(deliveryOrder.Details, allocate.Lines);

            var stockLines = deliveryOrder.Details
                .Where(x => IvSpFifoEligibility.IsShipmentRequired(x.StockControl, x.StdQty))
                .ToList();

            var doRef = SaDoSpRefs.ToRefNo(doNo);
            var batch = await _postingRepo.LockSpBatchByRefAsync(
                db, context.CompanyCode!, context.BranchCode!, doRef, cancellationToken);

            if (stockLines.Count > 0)
            {
                if (batch is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(doNo, SaDoPostReasonCodes.SpMissing, "Add shipment before posting.");
                }

                if (batch.TrxDtTime.Date != deliveryOrder.DoDate.Date)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(
                        doNo,
                        SaDoPostReasonCodes.DateMismatch,
                        "Shipment date does not match DO date. Please re-add shipment.");
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
                            return SaDoPostingItemResult.Failed(
                                doNo,
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
                        DocumentNo = doRef,
                        DocumentDate = deliveryOrder.DoDate.Date,
                        Batch = batch,
                        Details = spDetails,
                        RequiredLines = deliveryOrder.Details.Select(d => new IvSpRequiredLine
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
                    return SaDoPostingItemResult.Failed(
                        doNo,
                        SaDoPostReasonCodes.SpIncomplete,
                        validate.ErrorMessage ?? "Shipment validation failed.");
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
                    return SaDoPostingItemResult.Failed(doNo, core.ErrorMessage ?? "Stock post failed.");
                }
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            deliveryOrder.Status = SaDoStatuses.Posted;
            deliveryOrder.PostedDate = now;
            deliveryOrder.PostedBy = uid;
            deliveryOrder.ModifiedDate = now;
            deliveryOrder.ModifiedBy = uid;
            TouchRowVersion(db, deliveryOrder);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaDoPostingItemResult.Posted(doNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoPostingItemResult.Failed(
                doNo,
                SaDoPostReasonCodes.Concurrency,
                "Delivery order changed during post.");
        }
    }

    // ─────────────────────────── Private: RollbackOneAsync ───────────────────────────

    private async Task<SaDoPostingItemResult> RollbackOneAsync(
        UserContext context,
        string doNo,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, doNo, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, "Delivery order was not found.");
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, "Only POSTED delivery orders can be rolled back.");
            }

            if (expectedRowVersion is { Length: > 0 })
            {
                if (!RowVersionsEqual(deliveryOrder.RowVersion, expectedRowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(
                        doNo,
                        SaDoPostReasonCodes.Concurrency,
                        "Delivery order was changed by another user.");
                }

                db.Entry(deliveryOrder).Property(x => x.RowVersion).OriginalValue = expectedRowVersion;
            }

            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

            var reverse = await _docApplication.ReverseDocumentAllocationsAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                context.UserId!,
                SaDocTypes.Do,
                doNo,
                cancellationToken);
            if (!reverse.Succeeded)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(
                    doNo,
                    reverse.Code ?? SaSoReasonCodes.NotFound,
                    reverse.Message ?? "Sales Order allocation rollback failed.");
            }

            foreach (var detail in deliveryOrder.Details)
            {
                detail.SoConsumedQty = 0m;
            }

            var doRef = SaDoSpRefs.ToRefNo(doNo);
            var batch = await FindSpBatchAsync(db, context.CompanyCode!, context.BranchCode!, doRef, cancellationToken);
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
                    return SaDoPostingItemResult.Failed(doNo, core.ErrorMessage ?? "Stock rollback failed.");
                }
            }
            // SP batch is kept (not deleted) — status reverts to NEW by the posting service.

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);
            deliveryOrder.Status = SaDoStatuses.New;
            deliveryOrder.RollbackDate = now;
            deliveryOrder.RollbackBy = uid;
            deliveryOrder.ModifiedDate = now;
            deliveryOrder.ModifiedBy = uid;
            TouchRowVersion(db, deliveryOrder);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaDoPostingItemResult.RolledBack(doNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoPostingItemResult.Failed(
                doNo,
                SaDoPostReasonCodes.Concurrency,
                "Delivery order changed during rollback.");
        }
    }

    // ─────────────────────────── Private: ForceCloseOneAsync ───────────────────────────

    /// <summary>
    /// POSTED → CLOSED. The SP batch and its details are <b>retained</b> and stamped with the
    /// force-close audit (R4) so the immutable origin of the close survives; <c>BatchStatus</c>
    /// deliberately stays <c>POSTED</c> and <see cref="IvTrxBatch.ForceCloseDate"/> is the tombstone.
    /// Does NOT reverse stock — the physical shipment has already occurred.
    /// </summary>
    private async Task<SaDoPostingItemResult> ForceCloseOneAsync(
        UserContext context,
        string doNo,
        byte[]? expectedRowVersion,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var deliveryOrder = await _dos.LockForUpdateAsync(db, context.CompanyCode!, context.BranchCode!, doNo, cancellationToken);
            if (deliveryOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, "Delivery order was not found.");
            }

            if (!string.Equals(deliveryOrder.Status, SaDoStatuses.Posted, StringComparison.OrdinalIgnoreCase))
            {
                // Covers the repeat force-close of a CLOSED (force-closed) DO: fail deterministically
                // rather than silently no-op so a double-submit surfaces as a business error (§5.3.1).
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(doNo, "Only POSTED delivery orders can be force-closed.");
            }

            if (expectedRowVersion is { Length: > 0 } && !RowVersionsEqual(deliveryOrder.RowVersion, expectedRowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaDoPostingItemResult.Failed(
                    doNo,
                    SaDoPostReasonCodes.Concurrency,
                    "Delivery order was changed by another user.");
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 10);

            await db.Entry(deliveryOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

            // ── §5.5 global lock order: DO (held above) → SO ────────────────────────────────
            // The SO set is derived from the locked DO details, which is why SOs cannot be locked
            // first. WrittenOffQty is a read-modify-write on a shared Shared SO row, so the SO
            // header must be locked before it is read or incremented (D14, I10).
            var soNos = deliveryOrder.Details
                .Where(d => !string.IsNullOrWhiteSpace(d.SoNo) && d.SoLine is > 0)
                .Select(d => d.SoNo!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, SaSoLockOrder.Comparer)
                .ToList();

            var soHeaders = new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase);
            foreach (var soNo in soNos)
            {
                var salesOrder = await _salesOrders.LockForUpdateAsync(
                    db, context.CompanyCode!, context.BranchCode!, soNo, cancellationToken);
                if (salesOrder is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(doNo, $"Sales Order {soNo} was not found.");
                }

                if (!salesOrder.IsCurrent)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(doNo, SaSoReasonCodes.RevisedMessage);
                }

                await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
                soHeaders[soNo] = salesOrder;
            }

            // ── §5.1 write-off: W_l = max(0, DO line Qty − Σ posted DO→INV allocations) ─────
            foreach (var detail in deliveryOrder.Details)
            {
                var soNo = (detail.SoNo ?? string.Empty).Trim();
                if (soNo.Length == 0 || detail.SoLine is not > 0 || !soHeaders.TryGetValue(soNo, out var salesOrder))
                {
                    // Standalone DO line (no SO) — contributes nothing to any SO.
                    continue;
                }

                var invoiced = await SumDoLineInvoicedQtyAsync(
                    db, context.CompanyCode!, context.BranchCode!, doNo, detail.Line, cancellationToken);
                var writeOff = Math.Max(0m, SaSoQty.RoundQty(detail.Qty) - invoiced);
                if (writeOff == 0m)
                {
                    // Fully invoiced — contributes nothing and must not block the close.
                    continue;
                }

                var custRel = detail.CustRel is > 0 ? detail.CustRel!.Value : (short)1;
                var soLine = salesOrder.Details.FirstOrDefault(
                    x => x.Line == detail.SoLine!.Value && x.CustRel == custRel);
                if (soLine is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(
                        doNo, $"Sales Order {soNo} line {detail.SoLine} was not found.");
                }

                // Additive: force-closing a second DO on the same SO line accrues further write-off.
                // The result is committed in the SAME transaction as the status flip — force-close is
                // irreversible (D8), so there is no compensating path.
                soLine.WrittenOffQty = SaSoQty.RoundQty(soLine.WrittenOffQty + writeOff);
            }

            // R4: retain the SP batch — do NOT delete it or its details. Stamp the tombstone so the
            // retained batch can never be mistaken for a live reservation (guards live in §5.4).
            var doRef = SaDoSpRefs.ToRefNo(doNo);
            var batch = await _postingRepo.LockSpBatchByRefAsync(
                db, context.CompanyCode!, context.BranchCode!, doRef, cancellationToken);
            if (batch is not null)
            {
                if (batch.IsForceClosed)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaDoPostingItemResult.Failed(doNo, "This shipment batch has already been force-closed.");
                }

                batch.ForceCloseDate = now;
                batch.ForceCloseBy = uid;
                batch.ForceCloseReason = ForceCloseReason;
                batch.ModifiedDate = now;
                batch.ModifiedBy = uid;
                // BatchStatus deliberately stays POSTED — the stamp is the tombstone, not the status.
            }

            deliveryOrder.Status = SaDoStatuses.Closed;
            deliveryOrder.ModifiedDate = now;
            deliveryOrder.ModifiedBy = uid;
            TouchRowVersion(db, deliveryOrder);

            // Project the SO/DO quantities from the ledger under the locks already held. It takes no
            // locks of its own and does not touch WrittenOffQty, so it cannot clobber the write-off.
            await _docApplication.RecalculateAffectedLinesAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                uid,
                soHeaders.Keys.ToList(),
                [doNo],
                cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaDoPostingItemResult.ForceClosed(doNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaDoPostingItemResult.Failed(
                doNo,
                SaDoPostReasonCodes.Concurrency,
                "Delivery order changed during force-close.");
        }
    }

    /// <summary>
    /// Σ posted <c>DO → INV</c> applied qty for one DO line — the authority for the force-close
    /// write-off remainder (§3.2). Never use <c>SaDoDetail.SoConsumedQty</c> for this.
    /// </summary>
    private static async Task<decimal> SumDoLineInvoicedQtyAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doNo,
        short doLine,
        CancellationToken cancellationToken)
    {
        var sum = await db.SaDocApplications.AsNoTracking()
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.SourceDocType == SaDocTypes.Do
                && x.SourceDocId == doNo
                && x.SourceLineId == doLine
                && x.TargetDocType == SaDocTypes.Inv)
            .SumAsync(x => (decimal?)x.AppliedQty, cancellationToken) ?? 0m;
        return SaSoQty.RoundQty(sum);
    }

    /// <summary>Default audit reason stamped on a retained batch by <see cref="ForceCloseOneAsync"/>.</summary>
    internal const string ForceCloseReason = "DO_FORCE_CLOSE";

    // ─────────────────────────── Private: PrepareLinesAsync ───────────────────────────

    private async Task<PrepareOutcome> PrepareLinesAsync(
        AppDbContext db,
        SaDoSaveRequest request,
        string companyCode,
        string branchCode,
        IReadOnlyList<SaDoDetail>? existingDetails,
        string? excludeDoNo,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custCode = (request.CustCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(custCode))
        {
            errors["CustCode"] = "Customer is required.";
        }

        if (request.DoDate == default)
        {
            errors["DoDate"] = "DO date is required.";
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

        if (errors.Count > 0 && (string.IsNullOrWhiteSpace(custCode) || request.DoDate == default))
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
            errors["Lines"] = "Add at least one DO line.";
        }
        else if (request.Lines.Count > short.MaxValue)
        {
            errors["Lines"] = "Too many DO lines.";
        }

        if (request.Lines is { Count: > 0 })
        {
            var firstInclusive = request.Lines[0].IsInclusive;
            if (request.Lines.Any(x => x.IsInclusive != firstInclusive))
            {
                return PrepareOutcome.Fail(
                    "ST000032: All lines must use the same tax type (inclusive or exclusive).",
                    SaDoErrorKind.BusinessRule);
            }
        }

        // Legacy-aware Project validation (shared rule — see MsRefLookupRules).
        // A stored orphan may stay untouched; a new or changed code must exist and be active.
        string? priorProjId = null;
        var editDoNo = (excludeDoNo ?? string.Empty).Trim();
        if (editDoNo.Length > 0)
        {
            priorProjId = await db.SaDos.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.DoNo == editDoNo)
                .Select(x => x.ProjId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        var projError = await MsRefLookupRules.ValidateAsync(
            db, companyCode, branchCode, MsRefLookupKind.Project, priorProjId, request.ProjId, cancellationToken);
        if (projError is not null)
        {
            errors["ProjId"] = projError;
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var doDate = request.DoDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, doDate, cancellationToken);
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
                return PrepareOutcome.Validation(
                    SaSoReasonCodes.LinkDoNotSupported,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [$"Lines[{lineNo - 1}].LinkDo"] = "Sales Order fulfillment does not support LinkDo lines."
                    });
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

            // StdPsize = StdPackSize from item master (1 if not set)
            var stdPsize = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
            var stdQty = IvQty.Round(qty * stdPsize);
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
            string? custPo = null;
            string? sellingUom = item.SellingUom;
            if (soNo.Length > 0)
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
                        return PrepareOutcome.Fail(SaSoReasonCodes.RevisedMessage, SaDoErrorKind.Concurrency);
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
                        custPo = salesOrder.CustPo;
                        var soDetail = salesOrder.Details.FirstOrDefault(x => x.Line == soLine.Value && x.CustRel == salesOrder.CustRel);
                        if (soDetail is null)
                        {
                            errors[$"Lines[{lineNo - 1}].SoLine"] = $"Sales Order {soNo} line {soLine.Value} was not found.";
                        }
                        else
                        {
                            sellingUom = soDetail.SellingUom ?? item.SellingUom;
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
                CustPo = custPo,
                LinkDo = false,
                SoConsumedQty = 0m,
                ICode = iCode,
                IDesc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim(),
                Qty = qty,
                StdQty = stdQty,
                StdPsize = stdPsize,
                StdUom = item.StdUom,
                SellingUom = sellingUom,
                FrWarehouse = string.IsNullOrWhiteSpace(warehouse) ? null : warehouse,
                UnitPrice = line.UnitPrice,
                PricingSource = TruncateOptional(line.PricingSource, 40),
                PricingRef = TruncateOptional(line.PricingRef, 60),
                // Phase 4: normalised HERE so "NULL = never overridden" stays true.
                OriginalUnitPrice = SaPriceOverridePolicy.NormalizeOriginal(line.UnitPrice, line.OriginalUnitPrice),
                OverrideReason = SaPriceOverridePolicy.NormalizeReason(line.UnitPrice, line.OriginalUnitPrice, line.OverrideReason),
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
                Remarks = TruncateOptional(line.Remarks, 250),
                Calc = state
            });
            lineNo++;
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var reserveGate = await ValidateDoSoReserveAsync(
            db,
            companyCode,
            branchCode,
            prepared,
            salesOrdersByNo,
            excludeDoNo,
            cancellationToken);
        if (reserveGate is not null)
        {
            return reserveGate;
        }

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(calcStates);
        foreach (var row in prepared)
        {
            row.Calc.LocalAmount = SaInvoiceCalc.Money(row.Calc.NetAmount * rateResult.Rate);
        }

        // Phase 4: price-override governance, enforced SERVER-SIDE (the page is not the execution point).
        // A line that echoes the resolved price back is not an override.
        var overrideError = SaPriceOverridePolicy.Validate(
            prepared
                .Select(x => new SaPriceOverrideDeclaration(x.UnitPrice, x.OriginalUnitPrice, x.OverrideReason))
                .ToList(),
            await CanAsync(PermissionCodes.PriceOverride, cancellationToken));
        if (overrideError is not null)
        {
            return PrepareOutcome.Validation(
                overrideError,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UnitPrice"] = overrideError });
        }

        return PrepareOutcome.Ok(customer!, currency, rateResult.Rate, prepared);
    }

    private static async Task<PrepareOutcome?> ValidateDoSoReserveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        List<PreparedLine> prepared,
        IReadOnlyDictionary<string, SaSo> salesOrdersByNo,
        string? excludeDoNo,
        CancellationToken cancellationToken)
    {
        var thisDoBySoLine = prepared
            .Where(x => !string.IsNullOrWhiteSpace(x.SoNo) && x.SoLine is > 0)
            .GroupBy(x => (SoNo: x.SoNo.Trim(), CustRel: x.CustRel is > 0 ? x.CustRel.Value : (short)1, SoLine: x.SoLine!.Value), new SoLineGroupComparer())
            .ToDictionary(g => g.Key, g => SaSoQty.RoundQty(g.Sum(x => x.Qty)), new SoLineGroupComparer());

        if (thisDoBySoLine.Count == 0)
        {
            return null;
        }

        SaSoLineReserve.DocIdentity? excludeDo = null;
        var exclude = (excludeDoNo ?? string.Empty).Trim();
        if (exclude.Length > 0)
        {
            excludeDo = new SaSoLineReserve.DocIdentity(companyCode, branchCode, exclude);
        }

        var soNos = thisDoBySoLine.Keys.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var sums = await SaSoLineReserve.SumBySoLinesAsync(
            db, companyCode, branchCode, soNos, excludeDo, excludeInv: null, cancellationToken);

        foreach (var ((soNo, custRel, soLine), thisDoQty) in thisDoBySoLine.OrderBy(x => x.Key.SoNo, SaSoLockOrder.Comparer).ThenBy(x => x.Key.CustRel).ThenBy(x => x.Key.SoLine))
        {
            if (!salesOrdersByNo.TryGetValue(soNo, out var salesOrder))
            {
                return PrepareOutcome.Fail($"Sales Order {soNo} was not found.", SaDoErrorKind.BusinessRule);
            }

            if (custRel != salesOrder.CustRel)
            {
                return PrepareOutcome.Fail(SaSoReasonCodes.RevisedMessage, SaDoErrorKind.Concurrency);
            }

            var soDetail = salesOrder.Details.FirstOrDefault(x => x.Line == soLine && x.CustRel == salesOrder.CustRel);
            if (soDetail is null)
            {
                return PrepareOutcome.Fail(
                    $"Sales Order {soNo} line {soLine} was not found.",
                    SaDoErrorKind.BusinessRule);
            }

            var eval = SaSoLineReserve.Evaluate(
                soNo,
                soLine,
                soDetail.OrderQty,
                soDetail.DeliveredQty,
                SaSoLineReserve.GetSums(sums, soNo, custRel, soLine),
                thisDoQty,
                thisSoInvQty: 0m);
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

    // ─────────────────────────── Private: Helpers ───────────────────────────

    private static void ApplyHeaderSnapshots(SaDo deliveryOrder, SaDoSaveRequest request)
    {
        deliveryOrder.PayCode = TruncateOptional(request.PayCode, 20);
        deliveryOrder.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        deliveryOrder.SalesRep = TruncateOptional(request.SalesRep, 20);
        deliveryOrder.Ref1 = TruncateOptional(request.Ref1, 50);
        deliveryOrder.ProjId = TruncateOptional(request.ProjId, 20);
        deliveryOrder.Remarks = TruncateOptional(request.Remarks, 500);
        deliveryOrder.ShipVia = TruncateOptional(request.ShipVia, 50);
        deliveryOrder.ShipName = UpperSnapshot(request.ShipName, 100);
        deliveryOrder.ShipAddress1 = UpperSnapshot(request.ShipAddress1, 100);
        deliveryOrder.ShipAddress2 = UpperSnapshot(request.ShipAddress2, 100);
        deliveryOrder.ShipAddress3 = UpperSnapshot(request.ShipAddress3, 100);
        deliveryOrder.ShipCity = UpperSnapshot(request.ShipCity, 50);
        deliveryOrder.ShipState = UpperSnapshot(request.ShipState, 50);
        deliveryOrder.ShipPostalCode = UpperSnapshot(request.ShipPostalCode, 20);
        deliveryOrder.ShipCountry = UpperSnapshot(request.ShipCountry, 50);
        deliveryOrder.ShipTel = TruncateOptional(request.ShipTel, 50);
        deliveryOrder.ShipFax = TruncateOptional(request.ShipFax, 50);
        deliveryOrder.InvName = UpperSnapshot(request.InvName, 100);
        deliveryOrder.InvAddress1 = UpperSnapshot(request.InvAddress1, 100);
        deliveryOrder.InvAddress2 = UpperSnapshot(request.InvAddress2, 100);
        deliveryOrder.InvAddress3 = UpperSnapshot(request.InvAddress3, 100);
        deliveryOrder.InvCity = UpperSnapshot(request.InvCity, 50);
        deliveryOrder.InvState = UpperSnapshot(request.InvState, 50);
        deliveryOrder.InvPostalCode = UpperSnapshot(request.InvPostalCode, 20);
        deliveryOrder.InvCountry = UpperSnapshot(request.InvCountry, 50);
        deliveryOrder.InvTel = TruncateOptional(request.InvTel, 50);
        deliveryOrder.InvFax = TruncateOptional(request.InvFax, 50);
    }

    /// <summary>
    /// R7: DO header totals must follow the same convention as INVOICE/CN/SO — i.e.
    /// <see cref="SaInvoiceCalc.CalculateHeader"/> where <c>GrossAmnt</c> is ex-tax and
    /// <c>TotAmnt = GrossAmnt + Taxes</c>. The previous hand-rolled aggregate summed
    /// <c>Amount</c> into gross and <c>NetAmount</c> into total, so the DO disagreed with every
    /// other document kind and dropped tax from the total.
    /// </summary>
    private static void ApplyCalculatedTotals(SaDo deliveryOrder, IReadOnlyList<PreparedLine> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines.Select(x => x.Calc).ToList(), decPoint);
        deliveryOrder.GrossAmnt = header.GrossAmnt;
        deliveryOrder.Taxes = header.Taxes;
        deliveryOrder.TotAmnt = header.TotAmnt;
    }

    private static void AddDetails(SaDo deliveryOrder, IReadOnlyList<PreparedLine> lines)
    {
        foreach (var line in lines)
        {
            deliveryOrder.Details.Add(new SaDoDetail
            {
                CompanyCode = deliveryOrder.CompanyCode,
                BranchCode = deliveryOrder.BranchCode,
                DoNo = deliveryOrder.DoNo,
                Line = (short)line.Line,
                SoNo = line.SoNo,
                CustRel = line.CustRel,
                CustPo = TruncateOptional(line.CustPo, 50),
                SoLine = line.SoLine,
                ICode = line.ICode,
                IDesc = line.IDesc,
                SellingUom = line.SellingUom,
                Qty = line.Qty,
                StdQty = line.StdQty,
                StdPsize = line.StdPsize,
                StdUom = line.StdUom,
                FrWarehouse = line.FrWarehouse,
                UnitPrice = line.UnitPrice,
                PricingSource = line.PricingSource,
                PricingRef = line.PricingRef,
                OriginalUnitPrice = line.OriginalUnitPrice,
                OverrideReason = line.OverrideReason,
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
                TaxGroup = line.TaxGrCode,
                SoConsumedQty = line.SoConsumedQty,
                TaxAmt = line.Calc.TaxAmt,
                NetAmount = line.Calc.NetAmount,
                LocalAmount = line.Calc.LocalAmount,
                OrderType = line.OrderType,
                StockControl = line.StockControl,
                Remarks = line.Remarks
            });
        }
    }

    private static SaDoDocument MapDocument(
        SaDo deliveryOrder,
        IvTrxBatch? sp,
        IReadOnlyList<IvTrxBatchDetail> spDetails,
        IReadOnlyDictionary<(string SoNo, short CustRel), string?>? custPoBySo = null)
    {
        var shippedByLine = spDetails
            .GroupBy(x => x.SoLineNo ?? 0)
            .ToDictionary(g => (int)g.Key, g => IvQty.Round(g.Sum(x => x.FrStdQty ?? 0m)));

        var lines = deliveryOrder.Details.OrderBy(x => x.Line).Select(x =>
        {
            var shipped = shippedByLine.GetValueOrDefault(x.Line);
            var complete = !x.StockControl || shipped == IvQty.Round(x.StdQty);
            return new SaDoLineDto
            {
                Line = x.Line,
                SoNo = x.SoNo,
                SoLine = x.SoLine,
                CustRel = x.CustRel,
                CustPo = SaDocCustPoLookup.Resolve(custPoBySo, x.SoNo, x.CustRel, x.CustPo),
                SoConsumedQty = x.SoConsumedQty,
                ICode = x.ICode ?? string.Empty,
                IDesc = x.IDesc,
                Qty = x.Qty,
                StdQty = x.StdQty,
                StdPsize = x.StdPsize,
                StdUom = x.StdUom,
                FrWarehouse = x.FrWarehouse,
                UnitPrice = x.UnitPrice,
                PricingSource = x.PricingSource,
                PricingRef = x.PricingRef,
                OriginalUnitPrice = x.OriginalUnitPrice,
                OverrideReason = x.OverrideReason,
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
                TaxGroup = x.TaxGroup,
                TaxAmt = x.TaxAmt,
                NetAmount = x.NetAmount,
                LocalAmount = x.LocalAmount,
                OrderType = x.OrderType,
                StockControl = x.StockControl,
                Remarks = x.Remarks,
                ShipQty = shipped,
                ShipmentComplete = complete
            };
        }).ToList();

        return new SaDoDocument
        {
            DoNo = deliveryOrder.DoNo,
            DoDate = deliveryOrder.DoDate,
            Status = deliveryOrder.Status,
            BillingStatus = string.IsNullOrWhiteSpace(deliveryOrder.BillingStatus)
                ? SaDualStatuses.None
                : deliveryOrder.BillingStatus,
            CustCode = deliveryOrder.CustCode,
            CustName = deliveryOrder.CustName,
            Prefix = deliveryOrder.Prefix,
            Currency = deliveryOrder.Currency,
            CurrRate = deliveryOrder.CurrRate,
            PayCode = deliveryOrder.PayCode,
            TaxGrCode = deliveryOrder.TaxGrCode,
            SalesRep = deliveryOrder.SalesRep,
            Ref1 = deliveryOrder.Ref1,
            ProjId = deliveryOrder.ProjId,
            Remarks = deliveryOrder.Remarks,
            ShipVia = deliveryOrder.ShipVia,
            ShipName = deliveryOrder.ShipName,
            ShipAddress1 = deliveryOrder.ShipAddress1,
            ShipAddress2 = deliveryOrder.ShipAddress2,
            ShipAddress3 = deliveryOrder.ShipAddress3,
            ShipCity = deliveryOrder.ShipCity,
            ShipState = deliveryOrder.ShipState,
            ShipPostalCode = deliveryOrder.ShipPostalCode,
            ShipCountry = deliveryOrder.ShipCountry,
            ShipTel = deliveryOrder.ShipTel,
            ShipFax = deliveryOrder.ShipFax,
            InvName = deliveryOrder.InvName,
            InvAddress1 = deliveryOrder.InvAddress1,
            InvAddress2 = deliveryOrder.InvAddress2,
            InvAddress3 = deliveryOrder.InvAddress3,
            InvCity = deliveryOrder.InvCity,
            InvState = deliveryOrder.InvState,
            InvPostalCode = deliveryOrder.InvPostalCode,
            InvCountry = deliveryOrder.InvCountry,
            InvTel = deliveryOrder.InvTel,
            InvFax = deliveryOrder.InvFax,
            GrossAmnt = deliveryOrder.GrossAmnt,
            Taxes = deliveryOrder.Taxes,
            TotAmnt = deliveryOrder.TotAmnt,
            ShipmentComplete = lines.All(x => x.ShipmentComplete),
            SpBatchNo = sp?.BatchNo,
            SpBatchStatus = sp?.BatchStatus,
            NeedsShipment = string.Equals(deliveryOrder.Status, SaDoStatuses.New, StringComparison.OrdinalIgnoreCase)
                && deliveryOrder.Details.Any(d => IvSpFifoEligibility.IsShipmentRequired(d.StockControl, d.StdQty))
                && sp is null,
            RowVersion = deliveryOrder.RowVersion ?? [],
            Lines = lines,
            Shipment = spDetails.OrderBy(x => x.TrxLineNo).Select(x => new SaDoShipmentLineDto
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

    private async Task<IvTrxBatch?> FindSpBatchAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string doRef,
        CancellationToken cancellationToken)
    {
        var rows = await db.IvTrxBatches
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.TrxType == IvTrxTypes.SalesOut
                && x.RefNo == doRef)
            .ToListAsync(cancellationToken);
        if (rows.Count > 1)
        {
            throw new InvalidOperationException($"Multiple SP batches exist for DO reference {doRef}.");
        }

        return rows.SingleOrDefault();
    }

    private async Task<(IReadOnlyDictionary<string, SaSo> Headers, PrepareOutcome? Failure)> LockSalesOrdersForSaveAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<SaDoDetail>? existingDetails,
        IReadOnlyList<SaDoLineRequest>? requestedLines,
        CancellationToken cancellationToken)
    {
        var soNos = (existingDetails ?? [])
            .Select(x => x.SoNo)
            .Concat((requestedLines ?? []).Where(x => x is not null).Select(x => x.SoNo ?? string.Empty))
            .Select(x => (x ?? string.Empty).Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, SaSoLockOrder.Comparer)
            .ToList();

        if (soNos.Count == 0)
        {
            return (new Dictionary<string, SaSo>(StringComparer.OrdinalIgnoreCase), null);
        }

        if (soNos.Count > SaSoLimits.MaxDistinctSoHeaders)
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
        foreach (var soNo in soNos)
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
                    PrepareOutcome.Fail($"Sales Order {soNo} was not found.", SaDoErrorKind.BusinessRule));
            }

            await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);
            headers[soNo] = salesOrder;
        }

        return (headers, null);
    }

    private static List<SaDocAllocationLine> BuildSoToDoAllocations(SaDo deliveryOrder) =>
        deliveryOrder.Details
            .OrderBy(x => x.Line)
            .Where(x => HasSoReference(x.SoNo, x.SoLine))
            .Select(x => new SaDocAllocationLine
            {
                SourceDocId = x.SoNo.Trim(),
                SourceLineId = x.SoLine!.Value,
                TargetDocId = deliveryOrder.DoNo,
                TargetLineId = x.Line,
                AppliedQty = SaSoQty.RoundQty(x.Qty),
                AppliedAmount = x.NetAmount,
                SellingUom = NormalizeOptional(x.SellingUom),
                CustCode = deliveryOrder.CustCode,
                Currency = deliveryOrder.Currency
            })
            .ToList();

    private static void ApplyAllocatedQuantities(
        IEnumerable<SaDoDetail> details,
        IReadOnlyList<SaDocAllocationLine> allocations)
    {
        var byLine = allocations.ToDictionary(x => x.TargetLineId);
        foreach (var detail in details)
        {
            detail.SoConsumedQty = byLine.TryGetValue(detail.Line, out var row)
                ? SaSoQty.RoundQty(row.SoConsumedQty)
                : 0m;
        }
    }

    private async Task<(string? Error, decimal Rate)> ResolveCurrRateAsync(
        AppDbContext db,
        string currency,
        DateTime doDate,
        CancellationToken cancellationToken)
    {
        var isHome = string.Equals(currency, SaInvoiceCalc.HomeCurrency, StringComparison.OrdinalIgnoreCase);
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency && x.Status && x.StartDate <= doDate && x.EndDate >= doDate)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            if (isHome)
            {
                return (null, 1m);
            }

            return ($"No currency rate for {currency} on {doDate:yyyy-MM-dd}.", 0m);
        }

        var decimalRate = Convert.ToDecimal(rate.Value);
        if (!isHome && decimalRate == 1m)
        {
            return ("Non-home currency rate cannot be 1.", 0m);
        }

        return (null, decimalRate);
    }

    private static KeyValuePair<string, string>? ValidateLineDiscount(
        SaDoLineRequest line,
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

    private static List<(string ICode, decimal StdQty, string Wh, bool StockControl)> SnapshotIdentity(
        IEnumerable<SaDoDetail> details) =>
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

    private static bool RequestHasSoReferences(IReadOnlyList<SaDoLineRequest>? lines) =>
        (lines ?? []).Any(x => x is not null && !string.IsNullOrWhiteSpace(x.SoNo));

    private static bool HasSoReference(string? soNo, short? soLine) =>
        !string.IsNullOrWhiteSpace(soNo) && soLine is > 0;

    private static List<SaDoKeyedRequest> NormalizeKeyedItems(IReadOnlyList<SaDoKeyedRequest>? items) =>
        (items ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.DoNo))
            .Select(x => new SaDoKeyedRequest { DoNo = x.DoNo.Trim(), RowVersion = x.RowVersion ?? [] })
            .GroupBy(x => x.DoNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.SalesDeliveryOrder, permission, cancellationToken);

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

    private static string? UpperSnapshot(string? value, int maxLength)
    {
        var trimmed = TruncateOptional(value, maxLength);
        return trimmed?.ToUpperInvariant();
    }

    private static string? NormalizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    private static void TouchRowVersion(AppDbContext db, SaDo deliveryOrder)
    {
        if (!db.Database.IsSqlServer())
        {
            deliveryOrder.RowVersion = Guid.NewGuid().ToByteArray();
        }
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

    // ─────────────────────────── Private Types ───────────────────────────

    private sealed class PrepareOutcome
    {
        public string? Error { get; init; }
        public SaDoErrorKind Kind { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public ErpWeb.Model.Entities.CustomerProfile.SaCust? Customer { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; }
        public List<PreparedLine>? Lines { get; init; }

        public static PrepareOutcome Ok(ErpWeb.Model.Entities.CustomerProfile.SaCust customer, string currency, decimal rate, List<PreparedLine> lines) =>
            new() { Customer = customer, Currency = currency, CurrRate = rate, Lines = lines };

        public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = message, Kind = SaDoErrorKind.Validation, Errors = errors };

        public static PrepareOutcome Fail(string message, SaDoErrorKind kind) =>
            new() { Error = message, Kind = kind };

        public SaDoOperationResult ToFail() =>
            Kind == SaDoErrorKind.Validation
                ? SaDoOperationResult.FailValidation(ValidationMessageFormat.ResolveServiceMessage(Errors, Error), Errors)
                : SaDoOperationResult.Fail(Error ?? "Unable to save the delivery order.", Kind);
    }

    private sealed class PreparedLine
    {
        public int Line { get; init; }
        public string SoNo { get; init; } = string.Empty;
        public short? SoLine { get; init; }
        public short? CustRel { get; init; }
        public string? CustPo { get; init; }
        public bool LinkDo { get; init; }
        public decimal SoConsumedQty { get; init; }
        public string ICode { get; init; } = string.Empty;
        public string? IDesc { get; init; }
        public decimal Qty { get; init; }
        public decimal StdQty { get; init; }
        public decimal StdPsize { get; init; }
        public string? StdUom { get; init; }
        public string? SellingUom { get; init; }
        public string? FrWarehouse { get; init; }
        public decimal UnitPrice { get; init; }
        public string? PricingSource { get; init; }
        public string? PricingRef { get; init; }

        /// <summary>Phase 4: the engine price, non-null ONLY on a line an operator actually overrode.</summary>
        public decimal? OriginalUnitPrice { get; init; }

        /// <summary>Phase 4: the stated reason, non-null ONLY on a real override.</summary>
        public string? OverrideReason { get; init; }
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
