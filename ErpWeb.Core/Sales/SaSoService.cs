using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

public sealed class SaSoService : ISaSoService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ISaSoRepository _salesOrders;
    private readonly ISaCustRepository _customers;
    private readonly ISaDocApplication _docApplication;
    private readonly ILogger<SaSoService> _logger;

    /// <summary>Test-only: after locking current revision, before validation.</summary>
    internal Action? TestHookAfterLockCurrent { get; set; }

    /// <summary>Test-only: after old current is superseded and saved, before insert of new revision.</summary>
    internal Action? TestHookAfterSupersedeBeforeInsert { get; set; }

    public SaSoService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ISaSoRepository salesOrders,
        ISaCustRepository customers,
        ISaDocApplication docApplication,
        ILogger<SaSoService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _salesOrders = salesOrders;
        _customers = customers;
        _docApplication = docApplication;
        _logger = logger;
    }

    public async Task<SaSoOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var items = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
            .OrderBy(x => x.ICode)
            .Select(x => new SaSoItemLookupRow
            {
                ICode = x.ICode,
                IDesc = x.IDesc,
                SellingUom = x.SellingUom,
                StdUom = x.StdUom,
                StdPackSize = x.StdPackSize,
                SellingPrice = x.SellingPrice,
                TaxGroup = x.TaxGroup,
                StockControl = x.StockControl,
                DefWarehouse = x.DefWarehouse
            })
            .ToListAsync(cancellationToken);

        var warehouses = await db.IvWarehouses.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.IsActive)
            .OrderBy(x => x.WarehouseCode)
            .Select(x => new IvWarehouseLookupRow
            {
                WarehouseCode = x.WarehouseCode,
                WarehouseDesc = x.WarehouseDesc
            })
            .ToListAsync(cancellationToken);

        var customers = await db.SaCusts.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.IsActive)
            .OrderBy(x => x.CustCode)
            .Select(x => new SaSoCustomerLookupRow
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
            .Select(x => new SaSoTaxGroupLookupRow
            {
                TaxGrCode = x.TaxGrCode,
                TaxGrDesc = x.TaxGrDesc,
                Percentage = x.Percentage
            })
            .ToListAsync(cancellationToken);

        var payCodes = await db.IvMsCodes.AsNoTracking()
            .Where(x => x.CodeType == IvMsCodeTypes.PayCode)
            .OrderBy(x => x.Code)
            .Select(x => new IvCodeLookupRow
            {
                Code = x.Code,
                Desc = x.Name
            })
            .ToListAsync(cancellationToken);

        return SaSoOperationResult.OkLookups(items, warehouses, customers, taxGroups, payCodes);
    }

    public async Task<SaSoOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime soDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var code = (custCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return SaSoOperationResult.FailValidation(
                "Customer is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CustCode"] = "Customer is required."
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var customer = await _customers.GetByCodeAsync(db, context.CompanyCode!, code, includeChildren: false, cancellationToken);
        if (customer is null || !customer.IsActive)
        {
            return SaSoOperationResult.Fail("Customer was not found or is inactive.", SaSoErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(customer.Currency)
            ? SaInvoiceCalc.HomeCurrency
            : customer.Currency.Trim();
        var date = soDate == default ? _dates.Today.Date : soDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, date, cancellationToken);
        var useMainBill = customer.AppInvoice == true;
        var useMainShip = customer.AppShip == true;

        var shipToAddresses = await db.SaCustAdds.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode && x.CustCode == code)
            .OrderBy(x => x.Line)
            .Select(x => new SaCustAddressVm
            {
                Line = x.Line,
                AddName = x.AddName,
                DeliverTo = x.DeliverTo,
                Address1 = x.Address1,
                Address2 = x.Address2,
                Address3 = x.Address3,
                Address4 = x.Address4,
                City = x.City,
                State = x.State,
                PostalCode = x.PostalCode,
                Country = x.Country,
                Tel = x.Tel,
                Fax = x.Fax
            })
            .ToListAsync(cancellationToken);

        return SaSoOperationResult.OkDefaults(new SaSoCustomerDefaults
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
            ShipAddress4 = useMainShip ? customer.Address4 : null,
            ShipCity = useMainShip ? customer.City : customer.ShipCity,
            ShipState = useMainShip ? customer.State : customer.ShipState,
            ShipPostalCode = useMainShip ? customer.PostalCode : customer.ShipPostalCode,
            ShipCountry = useMainShip ? customer.Country : customer.ShipCountry,
            ShipTel = useMainShip ? customer.Tel : customer.ShipTel,
            ShipFax = useMainShip ? customer.Fax : customer.ShipFax,
            ShipToAddresses = shipToAddresses
        });
    }

    public async Task<SaSoOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime soDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var curr = (currency ?? string.Empty).Trim();
        if (curr.Length == 0)
        {
            return SaSoOperationResult.FailValidation(
                "Currency is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Currency"] = "Currency is required."
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var date = soDate == default ? _dates.Today.Date : soDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, curr, date, cancellationToken);
        if (rateResult.Error is not null)
        {
            return SaSoOperationResult.Fail(rateResult.Error, SaSoErrorKind.Validation);
        }

        return SaSoOperationResult.OkRate(rateResult.Rate, valid: true);
    }

    /// <summary>
    /// SearchAsync measured SQL budget (non-empty page): repository Count + page (2),
    /// line-stats with consumption flags (1), ListAllocatedSoKeysAsync (1), document usage UNION (1)
    /// = 5 commands ceiling. Prefer collapsing further; never N+1 per row.
    /// </summary>
    public async Task<SaSoOperationResult> SearchAsync(
        SaSoListQuery? query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        query ??= new SaSoListQuery();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var (rows, total) = await _salesOrders.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            new SaSoSearchArgs(
                SearchText: string.IsNullOrWhiteSpace(query.SearchText) ? null : query.SearchText.Trim(),
                Status: string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim(),
                DateFrom: query.DateFrom,
                DateTo: query.DateTo,
                SortField: query.SortField,
                SortDescending: query.SortDescending,
                Skip: query.Skip,
                Take: query.Take),
            cancellationToken);

                var revisionKeys = rows
            .Select(x => new SaDocSoRevisionKey(x.SoNo, x.CustRel))
            .Distinct()
            .ToList();
        var usageByKey = await LoadRevisionUsageAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            revisionKeys,
            includeLineConsumption: false,
            cancellationToken);

        var soNos = rows.Select(x => x.SoNo).ToList();
        var custRels = rows.Select(x => x.CustRel).Distinct().ToList();
        var lineStats = soNos.Count == 0
            ? []
            : await db.SaSoDetails.AsNoTracking()
                .Where(x =>
                    x.CompanyCode == context.CompanyCode
                    && x.BranchCode == context.BranchCode
                    && soNos.Contains(x.SoNo)
                    && custRels.Contains(x.CustRel))
                .GroupBy(x => new { x.SoNo, x.CustRel })
                .Select(g => new
                {
                    g.Key.SoNo,
                    g.Key.CustRel,
                    Count = g.Count(),
                    OrderQty = g.Sum(x => x.OrderQty),
                    DeliveredQty = g.Sum(x => x.DeliveredQty),
                    InvoicedQty = g.Sum(x => x.InvoicedQty),
                    HasDeliveredQty = g.Any(x => x.DeliveredQty != 0m),
                    HasInvoicedQty = g.Any(x => x.InvoicedQty != 0m),
                    HasShippedQty = g.Any(x => x.ShippedQty != 0m),
                    HasWrittenOffQty = g.Any(x => x.WrittenOffQty != 0m)
                })
                .ToListAsync(cancellationToken);

        var statsBySo = lineStats.ToDictionary(x => (x.SoNo.ToUpperInvariant(), x.CustRel));
        return SaSoOperationResult.OkList(new SaSoListPage
        {
            TotalCount = total,
            Rows = rows.Select(x =>
            {
                statsBySo.TryGetValue((x.SoNo.ToUpperInvariant(), x.CustRel), out var stats);
                var orderQty = stats?.OrderQty ?? 0m;
                var delivered = stats?.DeliveredQty ?? 0m;
                var invoiced = stats?.InvoicedQty ?? 0m;
                var key = new SaDocSoRevisionKey(x.SoNo, x.CustRel);
                usageByKey.TryGetValue(key, out var usage);
                usage ??= SaSoRevisionUsage.Empty;
                // Merge consumption flags from the line-stats query (same round-trip as percents).
                if (stats is not null
                    && (stats.HasDeliveredQty || stats.HasInvoicedQty || stats.HasShippedQty || stats.HasWrittenOffQty))
                {
                    usage = new SaSoRevisionUsage
                    {
                        HasDeliveredQty = usage.HasDeliveredQty || stats.HasDeliveredQty,
                        HasInvoicedQty = usage.HasInvoicedQty || stats.HasInvoicedQty,
                        HasShippedQty = usage.HasShippedQty || stats.HasShippedQty,
                        HasWrittenOffQty = usage.HasWrittenOffQty || stats.HasWrittenOffQty,
                        HasAllocation = usage.HasAllocation,
                        HasDraftDeliveryOrder = usage.HasDraftDeliveryOrder,
                        HasDraftInvoice = usage.HasDraftInvoice,
                        HasDeliveryOrderReference = usage.HasDeliveryOrderReference,
                        HasInvoiceReference = usage.HasInvoiceReference
                    };
                }

                var isNew = string.Equals(x.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase);
                var canMutate = isNew && usage.IsUnused;
                return new SaSoListRow
                {
                    SoNo = x.SoNo,
                    CustRel = x.CustRel,
                    SoDate = x.SoDate,
                    Status = x.Status,
                    FulfillmentStatus = string.IsNullOrWhiteSpace(x.FulfillmentStatus) ? SaDualStatuses.None : x.FulfillmentStatus,
                    BillingStatus = string.IsNullOrWhiteSpace(x.BillingStatus) ? SaDualStatuses.None : x.BillingStatus,
                    FulfillmentPct = orderQty <= 0m ? 0m : SaSoQty.RoundQty(delivered / orderQty * 100m),
                    BillingPct = orderQty <= 0m ? 0m : SaSoQty.RoundQty(invoiced / orderQty * 100m),
                    CustCode = x.CustCode,
                    CustName = x.CustName,
                    CustPo = x.CustPo,
                    TotAmnt = x.TotAmnt,
                    LineCount = stats?.Count ?? 0,
                    ClosedDate = x.ClosedDate,
                    CreatedDate = x.CreatedDate,
                    CreatedBy = x.CreatedBy,
                    RowVersion = x.RowVersion ?? [],
                    CanRevise = canMutate,
                    CanDelete = canMutate,
                    MutationBlockReason = usage.IsUnused ? null : usage.ListBlockReason()
                };
            }).ToList()
        });
    }

    public async Task<SaSoOperationResult> GetAsync(
        string soNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaSoOperationResult.Fail("Sales Order number is required.", SaSoErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var salesOrder = await _salesOrders.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);
        if (salesOrder is null)
        {
            return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
        }

        var revisions = await _salesOrders.ListRevisionsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);

        return SaSoOperationResult.OkDocument(MapDocument(salesOrder, revisions));
    }

    public async Task<SaSoOperationResult> GetAsync(
        string soNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0 || custRel <= 0)
        {
            return SaSoOperationResult.Fail("Sales Order number and revision are required.", SaSoErrorKind.Validation);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var salesOrder = await _salesOrders.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            custRel,
            cancellationToken);
        if (salesOrder is null)
        {
            return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
        }

        var revisions = await _salesOrders.ListRevisionsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);

        return SaSoOperationResult.OkDocument(MapDocument(salesOrder, revisions));
    }

    public async Task<SaSoOperationResult> GetReviseDraftAsync(
        string soNo,
        CancellationToken cancellationToken = default)
    {
        var current = await GetAsync(soNo, cancellationToken);
        if (!current.Succeeded || current.Document is null)
        {
            return current;
        }

        if (!string.Equals(current.Document.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase))
        {
            return SaSoOperationResult.Fail(
                "Only NEW Sales Orders can be revised.",
                SaSoErrorKind.BusinessRule);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        var unusedGate = await CheckRevisionUnusedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            current.Document.SoNo,
            current.Document.CustRel,
            SaSoUsageMutation.Revise,
            cancellationToken);
        if (unusedGate is not null)
        {
            return unusedGate;
        }

        var nextCustRel = current.Document.LastCustRel >= SaSoRevisionLimits.MaxCustRel
            ? SaSoRevisionLimits.MaxCustRel
            : (short)(current.Document.LastCustRel + 1);

        return SaSoOperationResult.OkDocument(MapRevisionDraft(current.Document, nextCustRel));
    }

    public async Task<SaSoOperationResult> SaveNewAsync(
        SaSoSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaSoOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
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
                existingByLine: null,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var soDate = request.SoDate == default ? _dates.Today.Date : request.SoDate.Date;
            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    "SO",
                    "",
                    soDate,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "SO numbering is not configured for this company/branch.",
                    SaSoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "SO numbering is not configured correctly. Contact an administrator.",
                    SaSoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "The next SO number exceeds the configured length.",
                    SaSoErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "The SO could not be saved because of a database conflict. Try again.",
                    SaSoErrorKind.Unexpected);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);
            var salesOrder = new SaSo
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode,
                SoNo = issued.DocumentNumber,
                SoDate = soDate,
                Status = SaSoStatuses.New,
                CustRel = 1,
                IsCurrent = true,
                LastCustRel = 1,
                RevisionReason = null,
                FulfillmentStatus = SaDualStatuses.None,
                BillingStatus = SaDualStatuses.None,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                Prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim(),
                CreatedDate = now,
                CreatedBy = uid
            };

            SaDocApplicationService.TouchRowVersion(db, salesOrder);
            ApplyHeaderSnapshots(salesOrder, request);
            AddDetails(salesOrder, prepared.Lines!);
            ApplyCalculatedTotals(salesOrder, prepared.Lines!, prepared.Customer.DecPoint == true);

            db.SaSos.Add(salesOrder);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Sales Order saved. UserId={UserId} Company={Company} SoNo={SoNo}",
                context.UserId,
                context.CompanyCode,
                salesOrder.SoNo);

            return await GetAsync(salesOrder.SoNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("SO number is already used.", SaSoErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "SO save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "The SO could not be saved because of a database conflict. Try again.",
                SaSoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SO save failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("Unable to save the Sales Order.", SaSoErrorKind.Unexpected);
        }
    }

    public async Task<SaSoOperationResult> UpdateAsync(
        string soNo,
        SaSoSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaSoOperationResult.FailValidation("Save request is required.");
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaSoOperationResult.FailValidation("Sales Order number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaSoOperationResult.Fail(
                "This Sales Order was changed by another user. Reload before saving.",
                SaSoErrorKind.Concurrency);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var salesOrder = await _salesOrders.LockForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (salesOrder is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
            }

            if (!salesOrder.IsCurrent
                || string.Equals(salesOrder.Status, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
            }

            if (request.CustRel is { } requestedCustRel && requestedCustRel != salesOrder.CustRel)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(salesOrder.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "This Sales Order was changed by another user. Reload before saving.",
                    SaSoErrorKind.Concurrency);
            }

            db.Entry(salesOrder).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

            if (!string.Equals(salesOrder.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(salesOrder.Status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "Only NEW or SHIPPED Sales Orders can be edited.",
                    SaSoErrorKind.BusinessRule);
            }

            var requestedCustCode = (request.CustCode ?? string.Empty).Trim();
            if (string.Equals(salesOrder.Status, SaSoStatuses.Shipped, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(salesOrder.CustCode, requestedCustCode, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "Customer cannot be changed after the Sales Order is shipped.",
                    SaSoErrorKind.BusinessRule);
            }

            var existingByLine = salesOrder.Details.ToDictionary(x => x.Line, x => x);
            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                existingByLine,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var requestedExistingLines = prepared.Lines!
                .Where(x => x.ExistingLineNo is not null)
                .Select(x => x.ExistingLineNo!.Value)
                .ToHashSet();

            var deletedAllocatedLines = salesOrder.Details
                .Where(x =>
                    !requestedExistingLines.Contains(x.Line)
                    && (SaSoQty.RoundQty(x.DeliveredQty) > 0m
                        || SaSoQty.RoundQty(x.InvoicedQty) > 0m
                        || SaSoQty.RoundQty(x.ShippedQty) > 0m))
                .OrderBy(x => x.Line)
                .ToList();
            if (deletedAllocatedLines.Count > 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    $"Sales Order line {deletedAllocatedLines[0].Line} cannot be deleted because it already has allocated quantity.",
                    SaSoErrorKind.BusinessRule);
            }

            foreach (var deleted in salesOrder.Details.Where(x => !requestedExistingLines.Contains(x.Line)).ToList())
            {
                db.SaSoDetails.Remove(deleted);
                salesOrder.Details.Remove(deleted);
            }

            var nextLine = salesOrder.Details.Count == 0
                ? (short)1
                : (short)(salesOrder.Details.Max(x => x.Line) + 1);

            foreach (var line in prepared.Lines!)
            {
                SaSoDetail detail;
                if (line.ExistingLineNo is short existingLineNo)
                {
                    detail = existingByLine[existingLineNo];
                }
                else
                {
                    detail = new SaSoDetail
                    {
                        CompanyCode = salesOrder.CompanyCode,
                        BranchCode = salesOrder.BranchCode,
                        SoNo = salesOrder.SoNo,
                        Line = nextLine++,
                        CustRel = salesOrder.CustRel
                    };
                    salesOrder.Details.Add(detail);
                }

                ApplyPreparedLine(detail, line, salesOrder.CustRel);
            }

            salesOrder.SoDate = request.SoDate == default ? _dates.Today.Date : request.SoDate.Date;
            salesOrder.CustCode = prepared.Customer!.CustCode;
            salesOrder.CustName = UpperSnapshot(prepared.Customer.CustName, 200);
            salesOrder.Currency = prepared.Currency;
            salesOrder.CurrRate = prepared.CurrRate;
            salesOrder.ModifiedDate = DateTime.UtcNow;
            salesOrder.ModifiedBy = Truncate(context.UserId!, 20);
            SaDocApplicationService.TouchRowVersion(db, salesOrder);
            ApplyHeaderSnapshots(salesOrder, request);
            ApplyCalculatedTotals(salesOrder, prepared.Lines, prepared.Customer.DecPoint == true);
            SaDocApplicationService.DeriveSoHeaderStatus(salesOrder, context.UserId!, salesOrder.ModifiedDate.Value);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "This Sales Order was changed by another user. Reload before saving.",
                SaSoErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "SO update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "The SO could not be saved because of a database conflict. Try again.",
                SaSoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SO update failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("Unable to save the Sales Order.", SaSoErrorKind.Unexpected);
        }
    }

    public async Task<SaSoOperationResult> DeleteAsync(
        IReadOnlyList<SaSoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaSoOperationResult.Fail("Select at least one Sales Order.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in keyed)
            {
                if (item.RowVersion.Length == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"SO {item.SoNo}: row version is required for delete.",
                        SaSoErrorKind.Concurrency);
                }

                var salesOrder = await _salesOrders.LockForUpdateAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    item.SoNo,
                    cancellationToken);
                if (salesOrder is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail($"Sales Order {item.SoNo} was not found.", SaSoErrorKind.NotFound);
                }

                if (!salesOrder.IsCurrent
                    || string.Equals(salesOrder.Status, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
                }

                if (item.CustRel is { } requestedCustRel && requestedCustRel != salesOrder.CustRel)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
                }

                if (!RowVersionsEqual(salesOrder.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"Sales Order {item.SoNo} was changed by another user. Reload before deleting.",
                        SaSoErrorKind.Concurrency);
                }

                db.Entry(salesOrder).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                await db.Entry(salesOrder).Collection(x => x.Details).LoadAsync(cancellationToken);

                if (!string.Equals(salesOrder.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"Sales Order {item.SoNo} cannot be deleted because it is not NEW.",
                        SaSoErrorKind.BusinessRule);
                }

                var unusedGate = await CheckRevisionUnusedAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    salesOrder.SoNo,
                    salesOrder.CustRel,
                    SaSoUsageMutation.Delete,
                    cancellationToken);
                if (unusedGate is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return unusedGate;
                }

                if (salesOrder.CustRel == 1)
                {
                    db.SaSoDetails.RemoveRange(salesOrder.Details);
                    db.SaSos.Remove(salesOrder);
                    continue;
                }

                var previous = await db.SaSos
                    .Include(x => x.Details)
                    .Where(x =>
                        x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.SoNo == salesOrder.SoNo
                        && x.CustRel < salesOrder.CustRel
                        && !x.IsCurrent
                        && x.Status == SaSoStatuses.Superseded)
                    .OrderByDescending(x => x.CustRel)
                    .FirstOrDefaultAsync(cancellationToken);
                if (previous is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"Sales Order {item.SoNo} previous revision was not found.",
                        SaSoErrorKind.Unexpected);
                }

                var previousGate = await CheckRevisionUnusedAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    previous.SoNo,
                    previous.CustRel,
                    SaSoUsageMutation.Delete,
                    cancellationToken);
                if (previousGate is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return previousGate;
                }

                var now = DateTime.UtcNow;
                var uid = Truncate(context.UserId!, 20);
                db.SaSoDetails.RemoveRange(salesOrder.Details);
                db.SaSos.Remove(salesOrder);
                await db.SaveChangesAsync(cancellationToken);

                previous.IsCurrent = true;
                previous.Status = SaSoStatuses.New;
                previous.LastCustRel = Math.Max(previous.LastCustRel, salesOrder.LastCustRel);
                previous.FulfillmentStatus = SaDualStatuses.None;
                previous.BillingStatus = SaDualStatuses.None;
                previous.ClosedReason = null;
                previous.ClosedDate = null;
                previous.ClosedBy = null;
                previous.ModifiedDate = now;
                previous.ModifiedBy = uid;
                SaDocApplicationService.TouchRowVersion(db, previous);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var soNo in keyed.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await AssertOneCurrentIfExistsAsync(db, context.CompanyCode!, context.BranchCode!, soNo, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return SaSoOperationResult.Ok();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SO delete failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("Unable to delete the Sales Order.", SaSoErrorKind.Unexpected);
        }
    }

    public async Task<SaSoOperationResult> ReviseAsync(
        string soNo,
        SaSoSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaSoOperationResult.FailValidation("Save request is required.");
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaSoOperationResult.FailValidation("Sales Order number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var current = await _salesOrders.LockForUpdateAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                no,
                cancellationToken);
            if (current is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
            }

            TestHookAfterLockCurrent?.Invoke();

            if (!current.IsCurrent
                || string.Equals(current.Status, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
            }

            if (request.CustRel is { } requestedCustRel && requestedCustRel != current.CustRel)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
            }

            if (!RowVersionsEqual(current.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "This Sales Order was changed by another user. Reload before revising.",
                    SaSoErrorKind.Concurrency);
            }

            db.Entry(current).Property(x => x.RowVersion).OriginalValue = request.RowVersion!;

            if (!string.Equals(current.Status, SaSoStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(
                    "Only NEW Sales Orders can be revised.",
                    SaSoErrorKind.BusinessRule);
            }

            if (current.LastCustRel >= SaSoRevisionLimits.MaxCustRel)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaSoOperationResult.Fail(SaSoReasonCodes.RevisionLimitMessage, SaSoErrorKind.BusinessRule);
            }

            var unusedGate = await CheckRevisionUnusedAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                current.SoNo,
                current.CustRel,
                SaSoUsageMutation.Revise,
                cancellationToken);
            if (unusedGate is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return unusedGate;
            }

            await db.Entry(current).Collection(x => x.Details).LoadAsync(cancellationToken);
            var existingByLine = current.Details.ToDictionary(x => x.Line, x => x);
            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                existingByLine,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);
            var nextCustRel = (short)(current.LastCustRel + 1);

            current.IsCurrent = false;
            current.Status = SaSoStatuses.Superseded;
            current.ModifiedDate = now;
            current.ModifiedBy = uid;
            SaDocApplicationService.TouchRowVersion(db, current);
            await db.SaveChangesAsync(cancellationToken);

            TestHookAfterSupersedeBeforeInsert?.Invoke();

            var revised = new SaSo
            {
                CompanyCode = current.CompanyCode,
                BranchCode = current.BranchCode,
                LocationCode = context.LocationCode,
                SoNo = current.SoNo,
                CustRel = nextCustRel,
                IsCurrent = true,
                LastCustRel = nextCustRel,
                RevisionReason = NormalizeRevisionReason(request.RevisionReason),
                SoDate = request.SoDate == default ? _dates.Today.Date : request.SoDate.Date,
                Status = SaSoStatuses.New,
                FulfillmentStatus = SaDualStatuses.None,
                BillingStatus = SaDualStatuses.None,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                Prefix = current.Prefix,
                CreatedDate = now,
                CreatedBy = uid
            };

            SaDocApplicationService.TouchRowVersion(db, revised);
            ApplyHeaderSnapshots(revised, request);
            AddDetails(revised, prepared.Lines!);
            ApplyCalculatedTotals(revised, prepared.Lines!, prepared.Customer.DecPoint == true);

            db.SaSos.Add(revised);
            await db.SaveChangesAsync(cancellationToken);
            await AssertOneCurrentAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "This Sales Order was changed by another user. Reload before revising.",
                SaSoErrorKind.Concurrency);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(SaSoReasonCodes.RevisedMessage, SaSoErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "SO revise deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "The SO could not be revised because of a database conflict. Try again.",
                SaSoErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SO revise failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("Unable to revise the Sales Order.", SaSoErrorKind.Unexpected);
        }
    }

    public async Task<SaSoOperationResult> ForceCloseAsync(
        IReadOnlyList<SaSoKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Close, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaSoOperationResult.Fail("No record selected.");
        }

        if (keyed.Count > SaSoLimits.MaxForceCloseSelection)
        {
            return SaSoOperationResult.Fail(
                $"Select at most {SaSoLimits.MaxForceCloseSelection} Sales Orders.",
                SaSoErrorKind.BusinessRule);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var item in keyed)
            {
                if (item.RowVersion.Length == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"SO {item.SoNo}: row version is required for force close.",
                        SaSoErrorKind.Concurrency);
                }

                var salesOrder = await _salesOrders.LockForUpdateAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    item.SoNo,
                    cancellationToken);
                if (salesOrder is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail($"Sales Order {item.SoNo} was not found.", SaSoErrorKind.NotFound);
                }

                if (!salesOrder.IsCurrent
                    || string.Equals(salesOrder.Status, SaSoStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
                }

                if (item.CustRel is { } requestedCustRel && requestedCustRel != salesOrder.CustRel)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(SaSoReasonCodes.SupersededMessage, SaSoErrorKind.BusinessRule);
                }

                if (!RowVersionsEqual(salesOrder.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"Sales Order {item.SoNo} was changed by another user. Reload before force closing.",
                        SaSoErrorKind.Concurrency);
                }

                db.Entry(salesOrder).Property(x => x.RowVersion).OriginalValue = item.RowVersion;

                if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaSoOperationResult.Fail(
                        $"Sales Order {item.SoNo} is already closed.",
                        SaSoErrorKind.BusinessRule);
                }

                var now = DateTime.UtcNow;
                var uid = Truncate(context.UserId!, 20);
                salesOrder.Status = SaSoStatuses.Closed;
                salesOrder.ClosedReason = SaSoClosedReasons.ForceClosed;
                salesOrder.ClosedDate = now;
                salesOrder.ClosedBy = uid;
                salesOrder.ModifiedDate = now;
                salesOrder.ModifiedBy = uid;
                SaDocApplicationService.TouchRowVersion(db, salesOrder);
            }

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return SaSoOperationResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail(
                "A Sales Order changed during force close. Reload and try again.",
                SaSoErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SO force close failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaSoOperationResult.Fail("Unable to force close the Sales Order.", SaSoErrorKind.Unexpected);
        }
    }

    public async Task<SaSoOperationResult> GetRemainingLinesAsync(
        string soNo,
        string? excludeDoNo = null,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaSoOperationResult.FailValidation("Sales Order number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var salesOrder = await _salesOrders.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);
        if (salesOrder is null)
        {
            return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
        }

        if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return SaSoOperationResult.OkRemainingLines(no, []);
        }

        SaSoLineReserve.DocIdentity? excludeDo = null;
        var exclude = (excludeDoNo ?? string.Empty).Trim();
        if (exclude.Length > 0)
        {
            excludeDo = new SaSoLineReserve.DocIdentity(
                context.CompanyCode!,
                context.BranchCode!,
                exclude);
        }

        var sums = await SaSoLineReserve.SumBySoLinesAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            [no],
            excludeDo,
            excludeInv: null,
            cancellationToken);

        var remaining = new List<SaSoLineDto>();
        foreach (var detail in salesOrder.Details.OrderBy(x => x.Line))
        {
            var lineSums = SaSoLineReserve.GetSums(sums, no, salesOrder.CustRel, detail.Line);
            var eval = SaSoLineReserve.Evaluate(
                no,
                detail.Line,
                detail.OrderQty,
                detail.DeliveredQty,
                lineSums,
                thisDoQty: 0m,
                thisSoInvQty: 0m);
            var available = SaSoLineReserve.RemainingForNewDo(eval);
            if (available <= 0m)
            {
                continue;
            }

            var mapped = MapLine(
                detail,
                balanceQtyOverride: available,
                remainingBillableOverride: SaSoLineReserve.RemainingForNewSoInv(eval),
                custPo: salesOrder.CustPo);
            remaining.Add(mapped);
        }

        return SaSoOperationResult.OkRemainingLines(no, remaining);
    }

    public async Task<SaSoOperationResult> GetBillableLinesAsync(
        string soNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaSoOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaSoOperationResult.Fail("Not authorized.", SaSoErrorKind.Authorization);
        }

        var no = (soNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaSoOperationResult.FailValidation("Sales Order number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var salesOrder = await _salesOrders.GetWithDetailsAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            no,
            cancellationToken);
        if (salesOrder is null)
        {
            return SaSoOperationResult.Fail("Sales Order was not found.", SaSoErrorKind.NotFound);
        }

        if (string.Equals(salesOrder.Status, SaSoStatuses.Closed, StringComparison.OrdinalIgnoreCase))
        {
            return SaSoOperationResult.OkRemainingLines(no, []);
        }

        var sums = await SaSoLineReserve.SumBySoLinesAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            [no],
            excludeDo: null,
            excludeInv: null,
            cancellationToken);

        var remaining = new List<SaSoLineDto>();
        foreach (var detail in salesOrder.Details.OrderBy(x => x.Line))
        {
            var lineSums = SaSoLineReserve.GetSums(sums, no, salesOrder.CustRel, detail.Line);
            var eval = SaSoLineReserve.Evaluate(
                no,
                detail.Line,
                detail.OrderQty,
                detail.DeliveredQty,
                lineSums,
                thisDoQty: 0m,
                thisSoInvQty: 0m);
            var available = SaSoLineReserve.RemainingForNewSoInv(eval);
            if (available <= 0m)
            {
                continue;
            }

            var mapped = MapLine(
                detail,
                balanceQtyOverride: SaSoLineReserve.RemainingForNewDo(eval),
                remainingBillableOverride: available,
                custPo: salesOrder.CustPo);
            remaining.Add(mapped);
        }

        return SaSoOperationResult.OkRemainingLines(no, remaining);
    }

    private async Task<PrepareOutcome> PrepareLinesAsync(
        AppDbContext db,
        SaSoSaveRequest request,
        string companyCode,
        string branchCode,
        IReadOnlyDictionary<short, SaSoDetail>? existingByLine,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custCode = (request.CustCode ?? string.Empty).Trim();
        if (custCode.Length == 0)
        {
            errors["CustCode"] = "Customer is required.";
        }

        if (request.SoDate == default)
        {
            errors["SoDate"] = "SO date is required.";
        }

        var payCode = (request.PayCode ?? string.Empty).Trim();
        if (payCode.Length == 0)
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

        if (TruncateOptional(request.CustPo, 50) is null)
        {
            errors["CustPo"] = "Customer PO is required.";
        }

        var currency = (request.Currency ?? string.Empty).Trim();
        if (currency.Length == 0)
        {
            errors["Currency"] = "Currency is required.";
        }

        if (errors.Count > 0 && (custCode.Length == 0 || request.SoDate == default))
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var customer = custCode.Length == 0
            ? null
            : await _customers.GetByCodeAsync(db, companyCode, custCode, includeChildren: false, cancellationToken);
        if (custCode.Length > 0 && (customer is null || !customer.IsActive))
        {
            errors["CustCode"] = "Customer was not found or is inactive.";
            return PrepareOutcome.Validation("Customer was not found or is inactive.", errors);
        }

        if (currency.Length > 0)
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

        var requestLines = request.Lines ?? [];

        if (requestLines.Count == 0)
        {
            errors["Lines"] = "Add at least one SO line.";
        }
        else if (requestLines.Count > short.MaxValue)
        {
            errors["Lines"] = "Too many SO lines.";
        }

        if (requestLines.Count > 0)
        {
            var firstInclusive = requestLines[0].IsInclusive;
            if (requestLines.Any(x => x.IsInclusive != firstInclusive))
            {
                return PrepareOutcome.Fail(
                    "ST000032: All lines must use the same tax type (inclusive or exclusive).",
                    SaSoErrorKind.BusinessRule);
            }
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        var soDate = request.SoDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, soDate, cancellationToken);
        if (rateResult.Error is not null)
        {
            errors["Currency"] = rateResult.Error;
            return PrepareOutcome.Validation(rateResult.Error, errors);
        }

        var warehouses = await db.IvWarehouses.AsNoTracking()
            .Where(x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.IsActive)
            .OrderBy(x => x.WarehouseCode)
            .Select(x => x.WarehouseCode)
            .ToListAsync(cancellationToken);
        var warehouseSet = warehouses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackWh = warehouses.FirstOrDefault();

        var itemCodes = requestLines
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.ICode))
            .Select(x => x.ICode.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var itemsByCode = itemCodes.Count == 0
            ? new Dictionary<string, IvStockMaster>(StringComparer.OrdinalIgnoreCase)
            : await db.IvStockMasters.AsNoTracking()
                .Where(x => x.CompanyCode == companyCode && itemCodes.Contains(x.ICode))
                .ToDictionaryAsync(x => x.ICode, x => x, StringComparer.OrdinalIgnoreCase, cancellationToken);

        var usedExistingLines = new HashSet<short>();
        var prepared = new List<PreparedLine>();
        var calcStates = new List<SaInvoiceLineCalcState>();

        for (var index = 0; index < requestLines.Count; index++)
        {
            var line = requestLines[index];
            if (line is null)
            {
                errors[$"Lines[{index}]"] = "Line data is required.";
                continue;
            }

            short? existingLineNo = null;
            if (line.Line < 0 || line.Line > short.MaxValue)
            {
                errors[$"Lines[{index}].Line"] = "Line number is not valid.";
            }
            else if (line.Line > 0 && existingByLine is not null)
            {
                // Update: Line > 0 must match an existing detail PK.
                // Create (existingByLine null): UI display numbers are ignored; lines are assigned 1..n.
                existingLineNo = (short)line.Line;
                if (!existingByLine.ContainsKey(existingLineNo.Value))
                {
                    errors[$"Lines[{index}].Line"] = $"Line {line.Line} was not found.";
                }
                else if (!usedExistingLines.Add(existingLineNo.Value))
                {
                    errors[$"Lines[{index}].Line"] = $"Line {line.Line} is duplicated.";
                }
            }

            var iCode = (line.ICode ?? string.Empty).Trim();
            if (iCode.Length == 0)
            {
                errors[$"Lines[{index}].ICode"] = "Item code is required.";
                continue;
            }

            if (!itemsByCode.TryGetValue(iCode, out var item) || !item.IsActive)
            {
                errors[$"Lines[{index}].ICode"] = $"Item '{iCode}' was not found or is inactive.";
                continue;
            }

            var orderQty = SaSoQty.RoundQty(line.OrderQty);
            if (orderQty <= 0m)
            {
                errors[$"Lines[{index}].OrderQty"] = "Order quantity must be greater than zero.";
            }
            else if (existingLineNo is short existingNo
                     && existingByLine is not null
                     && existingByLine.TryGetValue(existingNo, out var existingDetail))
            {
                var floor = Math.Max(
                    SaSoQty.RoundQty(existingDetail.DeliveredQty),
                    Math.Max(
                        SaSoQty.RoundQty(existingDetail.InvoicedQty),
                        SaSoQty.RoundQty(existingDetail.ShippedQty)));
                if (orderQty < floor)
                {
                    errors[$"Lines[{index}].OrderQty"] =
                        $"Order quantity cannot be lower than allocated quantity {floor:n4}.";
                }
            }

            var stdPsize = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
            var stdQty = SaSoQty.RoundQty(orderQty * stdPsize);
            if (stdQty == 0m && orderQty > 0m)
            {
                errors[$"Lines[{index}].OrderQty"] = "Standard quantity must not be zero.";
            }

            var warehouse = (line.Warehouse ?? item.DefWarehouse ?? fallbackWh ?? string.Empty).Trim();
            if (warehouse.Length > 0 && !warehouseSet.Contains(warehouse))
            {
                errors[$"Lines[{index}].Warehouse"] = $"Warehouse '{warehouse}' was not found.";
            }

            var deliveryDate = NormalizePlanningDate(line.DeliveryDate);
            var eta = NormalizePlanningDate(line.Eta);
            var etd = NormalizePlanningDate(line.Etd);
            if (etd is { } etdVal && eta is { } etaVal && etdVal > etaVal)
            {
                errors[$"Lines[{index}].Etd"] = "ETD cannot be after ETA.";
            }

            if (eta is { } etaAfter && deliveryDate is { } deliveryVal && etaAfter > deliveryVal)
            {
                errors[$"Lines[{index}].Eta"] = "ETA cannot be after delivery date.";
            }

            var discError = ValidateLineDiscount(line, index, customer!.DiscountMethod);
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
                    errors[$"Lines[{index}].TaxGrCode"] = $"Tax group '{taxGr}' was not found.";
                }
            }
            else if (customer!.Taxable == true)
            {
                if (headerTax.Length == 0 || !taxByCode.TryGetValue(headerTax, out taxPercent))
                {
                    errors["TaxGrCode"] = "Tax group is required for a taxable customer.";
                }
            }

            if (errors.Count > 0)
            {
                continue;
            }

            var state = new SaInvoiceLineCalcState
            {
                Line = index + 1,
                Qty = orderQty,
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
            SaInvoiceCalc.CalculateLine(state, taxPercent, customer.DecPoint == true, customer.DiscountMethod);
            calcStates.Add(state);

            prepared.Add(new PreparedLine
            {
                RequestOrder = index + 1,
                ExistingLineNo = existingLineNo,
                ICode = iCode,
                IDesc = string.IsNullOrWhiteSpace(line.IDesc) ? item.IDesc : line.IDesc.Trim(),
                CustICode = TruncateOptional(line.CustICode, 30),
                OrderQty = orderQty,
                StdQty = stdQty,
                StdPsize = stdPsize,
                SellingUom = TruncateOptional(item.SellingUom, 10),
                StdUom = item.StdUom,
                Warehouse = warehouse.Length == 0 ? null : warehouse,
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
                Remarks = TruncateOptional(line.Remarks, 250),
                StockControl = item.StockControl,
                Classification = TruncateOptional(line.Classification, 50),
                DeliveryDate = deliveryDate,
                Eta = eta,
                Etd = etd,
                Calc = state
            });
        }

        if (errors.Count > 0)
        {
            return PrepareOutcome.Validation("Validation failed.", errors);
        }

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(calcStates);
        foreach (var row in prepared)
        {
            row.Calc.LocalAmount = SaInvoiceCalc.Money(row.Calc.NetAmount * rateResult.Rate);
        }

        return PrepareOutcome.Ok(customer!, currency, rateResult.Rate, prepared);
    }

    private static void ApplyHeaderSnapshots(SaSo salesOrder, SaSoSaveRequest request)
    {
        salesOrder.CustPo = TruncateOptional(request.CustPo, 50);
        salesOrder.Ref1 = TruncateOptional(request.Ref1, 50);
        salesOrder.ProjId = TruncateOptional(request.ProjId, 20);
        salesOrder.PayCode = TruncateOptional(request.PayCode, 20);
        salesOrder.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        salesOrder.SalesRep = TruncateOptional(request.SalesRep, 20);
        salesOrder.Remarks = TruncateOptional(request.Remarks, 500);
        salesOrder.ShipName = UpperSnapshot(request.ShipName, 100);
        salesOrder.ShipAddress1 = UpperSnapshot(request.ShipAddress1, 100);
        salesOrder.ShipAddress2 = UpperSnapshot(request.ShipAddress2, 100);
        salesOrder.ShipAddress3 = UpperSnapshot(request.ShipAddress3, 100);
        salesOrder.ShipAddress4 = UpperSnapshot(request.ShipAddress4, 100);
        salesOrder.ShipCity = UpperSnapshot(request.ShipCity, 50);
        salesOrder.ShipState = UpperSnapshot(request.ShipState, 50);
        salesOrder.ShipPostalCode = UpperSnapshot(request.ShipPostalCode, 20);
        salesOrder.ShipCountry = UpperSnapshot(request.ShipCountry, 50);
        salesOrder.ShipTel = TruncateOptional(request.ShipTel, 50);
        salesOrder.ShipFax = TruncateOptional(request.ShipFax, 50);
        salesOrder.InvName = UpperSnapshot(request.InvName, 100);
        salesOrder.InvAddress1 = UpperSnapshot(request.InvAddress1, 100);
        salesOrder.InvAddress2 = UpperSnapshot(request.InvAddress2, 100);
        salesOrder.InvAddress3 = UpperSnapshot(request.InvAddress3, 100);
        salesOrder.InvAddress4 = UpperSnapshot(request.InvAddress4, 100);
        salesOrder.InvCity = UpperSnapshot(request.InvCity, 50);
        salesOrder.InvState = UpperSnapshot(request.InvState, 50);
        salesOrder.InvPostalCode = UpperSnapshot(request.InvPostalCode, 20);
        salesOrder.InvCountry = UpperSnapshot(request.InvCountry, 50);
        salesOrder.InvTel = TruncateOptional(request.InvTel, 50);
        salesOrder.InvFax = TruncateOptional(request.InvFax, 50);
    }

    private static void ApplyCalculatedTotals(SaSo salesOrder, IReadOnlyList<PreparedLine> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines.Select(x => x.Calc).ToList(), decPoint);
        salesOrder.GrossAmnt = header.GrossAmnt;
        salesOrder.Taxes = header.Taxes;
        salesOrder.TotAmnt = header.TotAmnt;
    }

    private static void AddDetails(SaSo salesOrder, IReadOnlyList<PreparedLine> lines)
    {
        foreach (var line in lines)
        {
            var detail = new SaSoDetail
            {
                CompanyCode = salesOrder.CompanyCode,
                BranchCode = salesOrder.BranchCode,
                SoNo = salesOrder.SoNo,
                Line = (short)line.RequestOrder,
                CustRel = salesOrder.CustRel,
                ICode = line.ICode,
                IDesc = line.IDesc,
                CustICode = line.CustICode,
                UnitPrice = line.UnitPrice,
                SellingUom = line.SellingUom,
                StdUom = line.StdUom,
                StdQty = line.StdQty,
                StdPsize = line.StdPsize,
                TaxAmt = line.Calc.TaxAmt,
                OrderType = line.OrderType,
                Remarks = line.Remarks,
                Amount = line.Calc.Amount,
                NetAmount = line.Calc.NetAmount,
                ItemDiscount = line.ItemDiscount,
                ItemDiscount2 = line.ItemDiscount2,
                ItemDiscount3 = line.ItemDiscount3,
                ItemDiscount4 = line.ItemDiscount4,
                ItemDiscount5 = line.ItemDiscount5,
                ItemDiscount6 = line.ItemDiscount6,
                ItemDiscAmount = line.ItemDiscAmount,
                ItemDiscAmount1 = line.ItemDiscAmount1,
                Warehouse = line.Warehouse,
                TaxGroup = line.TaxGrCode,
                IsInclusive = line.IsInclusive,
                LocalAmount = line.Calc.LocalAmount,
                StockControl = line.StockControl,
                Classification = line.Classification,
                DeliveryDate = line.DeliveryDate,
                Eta = line.Eta,
                Etd = line.Etd
            };
            SaSoQty.SetOrderQty(detail, line.OrderQty);
            salesOrder.Details.Add(detail);
        }
    }

    private static void ApplyPreparedLine(SaSoDetail detail, PreparedLine line, short custRel)
    {
        detail.CustRel = custRel;
        detail.ICode = line.ICode;
        detail.IDesc = line.IDesc;
        detail.CustICode = line.CustICode;
        detail.UnitPrice = line.UnitPrice;
        detail.SellingUom = line.SellingUom;
        detail.StdUom = line.StdUom;
        detail.StdQty = line.StdQty;
        detail.StdPsize = line.StdPsize;
        detail.TaxAmt = line.Calc.TaxAmt;
        detail.OrderType = line.OrderType;
        detail.Remarks = line.Remarks;
        detail.Amount = line.Calc.Amount;
        detail.NetAmount = line.Calc.NetAmount;
        detail.ItemDiscount = line.ItemDiscount;
        detail.ItemDiscount2 = line.ItemDiscount2;
        detail.ItemDiscount3 = line.ItemDiscount3;
        detail.ItemDiscount4 = line.ItemDiscount4;
        detail.ItemDiscount5 = line.ItemDiscount5;
        detail.ItemDiscount6 = line.ItemDiscount6;
        detail.ItemDiscAmount = line.ItemDiscAmount;
        detail.ItemDiscAmount1 = line.ItemDiscAmount1;
        detail.Warehouse = line.Warehouse;
        detail.TaxGroup = line.TaxGrCode;
        detail.IsInclusive = line.IsInclusive;
        detail.LocalAmount = line.Calc.LocalAmount;
        detail.StockControl = line.StockControl;
        detail.Classification = line.Classification;
        detail.DeliveryDate = line.DeliveryDate;
        detail.Eta = line.Eta;
        detail.Etd = line.Etd;
        SaSoQty.SetOrderQty(detail, line.OrderQty);
    }

    private static SaSoDocument MapDocument(SaSo salesOrder, IReadOnlyList<SaSo>? revisions = null) =>
        new()
        {
            SoNo = salesOrder.SoNo,
            CustRel = salesOrder.CustRel,
            IsCurrent = salesOrder.IsCurrent,
            LastCustRel = salesOrder.LastCustRel,
            RevisionReason = salesOrder.RevisionReason,
            SoDate = salesOrder.SoDate,
            Status = salesOrder.Status,
            FulfillmentStatus = string.IsNullOrWhiteSpace(salesOrder.FulfillmentStatus)
                ? SaDualStatuses.None
                : salesOrder.FulfillmentStatus,
            BillingStatus = string.IsNullOrWhiteSpace(salesOrder.BillingStatus)
                ? SaDualStatuses.None
                : salesOrder.BillingStatus,
            ClosedReason = salesOrder.ClosedReason,
            ClosedDate = salesOrder.ClosedDate,
            ClosedBy = salesOrder.ClosedBy,
            CustCode = salesOrder.CustCode,
            CustName = salesOrder.CustName,
            CustPo = salesOrder.CustPo,
            Ref1 = salesOrder.Ref1,
            ProjId = salesOrder.ProjId,
            Prefix = salesOrder.Prefix,
            Currency = salesOrder.Currency,
            CurrRate = salesOrder.CurrRate,
            PayCode = salesOrder.PayCode,
            TaxGrCode = salesOrder.TaxGrCode,
            SalesRep = salesOrder.SalesRep,
            Remarks = salesOrder.Remarks,
            ShipName = salesOrder.ShipName,
            ShipAddress1 = salesOrder.ShipAddress1,
            ShipAddress2 = salesOrder.ShipAddress2,
            ShipAddress3 = salesOrder.ShipAddress3,
            ShipAddress4 = salesOrder.ShipAddress4,
            ShipCity = salesOrder.ShipCity,
            ShipState = salesOrder.ShipState,
            ShipPostalCode = salesOrder.ShipPostalCode,
            ShipCountry = salesOrder.ShipCountry,
            ShipTel = salesOrder.ShipTel,
            ShipFax = salesOrder.ShipFax,
            InvName = salesOrder.InvName,
            InvAddress1 = salesOrder.InvAddress1,
            InvAddress2 = salesOrder.InvAddress2,
            InvAddress3 = salesOrder.InvAddress3,
            InvAddress4 = salesOrder.InvAddress4,
            InvCity = salesOrder.InvCity,
            InvState = salesOrder.InvState,
            InvPostalCode = salesOrder.InvPostalCode,
            InvCountry = salesOrder.InvCountry,
            InvTel = salesOrder.InvTel,
            InvFax = salesOrder.InvFax,
            GrossAmnt = salesOrder.GrossAmnt,
            Taxes = salesOrder.Taxes,
            TotAmnt = salesOrder.TotAmnt,
            CreatedBy = salesOrder.CreatedBy,
            CreatedDate = salesOrder.CreatedDate,
            ModifiedBy = salesOrder.ModifiedBy,
            ModifiedDate = salesOrder.ModifiedDate,
            RowVersion = salesOrder.RowVersion ?? [],
            Lines = salesOrder.Details.OrderBy(x => x.Line).Select(d => MapLine(d, custPo: salesOrder.CustPo)).ToList(),
            Revisions = (revisions ?? [])
                .OrderByDescending(x => x.CustRel)
                .Select(x => new SaSoRevisionHistoryRow
                {
                    CustRel = x.CustRel,
                    IsCurrent = x.IsCurrent,
                    Status = x.Status,
                    RevisionReason = x.RevisionReason,
                    CreatedBy = x.CreatedBy,
                    CreatedDate = x.CreatedDate
                })
                .ToList()
        };

    private static SaSoDocument MapRevisionDraft(SaSoDocument current, short nextCustRel) =>
        new()
        {
            SoNo = current.SoNo,
            CustRel = nextCustRel,
            IsCurrent = true,
            LastCustRel = nextCustRel,
            RevisionReason = null,
            SoDate = current.SoDate,
            Status = SaSoStatuses.New,
            FulfillmentStatus = SaDualStatuses.None,
            BillingStatus = SaDualStatuses.None,
            CustCode = current.CustCode,
            CustName = current.CustName,
            CustPo = current.CustPo,
            Ref1 = current.Ref1,
            ProjId = current.ProjId,
            Prefix = current.Prefix,
            Currency = current.Currency,
            CurrRate = current.CurrRate,
            PayCode = current.PayCode,
            TaxGrCode = current.TaxGrCode,
            SalesRep = current.SalesRep,
            Remarks = current.Remarks,
            ShipName = current.ShipName,
            ShipAddress1 = current.ShipAddress1,
            ShipAddress2 = current.ShipAddress2,
            ShipAddress3 = current.ShipAddress3,
            ShipAddress4 = current.ShipAddress4,
            ShipCity = current.ShipCity,
            ShipState = current.ShipState,
            ShipPostalCode = current.ShipPostalCode,
            ShipCountry = current.ShipCountry,
            ShipTel = current.ShipTel,
            ShipFax = current.ShipFax,
            InvName = current.InvName,
            InvAddress1 = current.InvAddress1,
            InvAddress2 = current.InvAddress2,
            InvAddress3 = current.InvAddress3,
            InvAddress4 = current.InvAddress4,
            InvCity = current.InvCity,
            InvState = current.InvState,
            InvPostalCode = current.InvPostalCode,
            InvCountry = current.InvCountry,
            InvTel = current.InvTel,
            InvFax = current.InvFax,
            GrossAmnt = current.GrossAmnt,
            Taxes = current.Taxes,
            TotAmnt = current.TotAmnt,
            RowVersion = current.RowVersion ?? [],
            Lines = current.Lines
                .OrderBy(x => x.Line)
                .Select(x => new SaSoLineDto
                {
                    Line = x.Line,
                    CustRel = nextCustRel,
                    CustPo = current.CustPo,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    CustICode = x.CustICode,
                    OrderQty = x.OrderQty,
                    ShippedQty = 0m,
                    BalanceQty = x.OrderQty,
                    DeliveredQty = 0m,
                    InvoicedQty = 0m,
                    RemainingBillableQty = x.OrderQty,
                    StdQty = x.StdQty,
                    StdPsize = x.StdPsize,
                    SellingUom = x.SellingUom,
                    StdUom = x.StdUom,
                    Warehouse = x.Warehouse,
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
                    TaxGroup = x.TaxGroup,
                    TaxAmt = x.TaxAmt,
                    NetAmount = x.NetAmount,
                    LocalAmount = x.LocalAmount,
                    OrderType = x.OrderType,
                    StockControl = x.StockControl,
                    Classification = x.Classification,
                    Remarks = x.Remarks,
                    DeliveryDate = x.DeliveryDate,
                    Eta = x.Eta,
                    Etd = x.Etd
                })
                .ToList(),
            Revisions = current.Revisions
        };

    private static SaSoLineDto MapLine(
        SaSoDetail detail,
        decimal? balanceQtyOverride = null,
        decimal? remainingBillableOverride = null,
        string? custPo = null) =>
        new()
        {
            Line = detail.Line,
            CustRel = detail.CustRel,
            CustPo = custPo,
            ICode = detail.ICode ?? string.Empty,
            IDesc = detail.IDesc,
            CustICode = detail.CustICode,
            OrderQty = detail.OrderQty,
            ShippedQty = detail.ShippedQty,
            BalanceQty = balanceQtyOverride ?? detail.BalanceQty,
            DeliveredQty = detail.DeliveredQty,
            InvoicedQty = detail.InvoicedQty,
            WrittenOffQty = detail.WrittenOffQty,
            RemainingBillableQty = remainingBillableOverride
                ?? SaSoQty.RoundQty(detail.OrderQty - detail.InvoicedQty),
            StdQty = detail.StdQty,
            StdPsize = detail.StdPsize,
            SellingUom = detail.SellingUom,
            StdUom = detail.StdUom,
            Warehouse = detail.Warehouse,
            UnitPrice = detail.UnitPrice,
            Amount = detail.Amount,
            ItemDiscount = detail.ItemDiscount,
            ItemDiscount2 = detail.ItemDiscount2,
            ItemDiscount3 = detail.ItemDiscount3,
            ItemDiscount4 = detail.ItemDiscount4,
            ItemDiscount5 = detail.ItemDiscount5,
            ItemDiscount6 = detail.ItemDiscount6,
            ItemDiscAmount = detail.ItemDiscAmount,
            ItemDiscAmount1 = detail.ItemDiscAmount1,
            IsInclusive = detail.IsInclusive,
            TaxGroup = detail.TaxGroup,
            TaxAmt = detail.TaxAmt,
            NetAmount = detail.NetAmount,
            LocalAmount = detail.LocalAmount,
            OrderType = detail.OrderType,
            StockControl = detail.StockControl,
            Classification = detail.Classification,
            Remarks = detail.Remarks,
            DeliveryDate = detail.DeliveryDate,
            Eta = detail.Eta,
            Etd = detail.Etd
        };

    private async Task<SaSoOperationResult?> CheckRevisionUnusedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        short custRel,
        SaSoUsageMutation mutation,
        CancellationToken cancellationToken)
    {
        var key = new SaDocSoRevisionKey(soNo, custRel);
        var usageByKey = await LoadRevisionUsageAsync(
            db,
            companyCode,
            branchCode,
            [key],
            includeLineConsumption: true,
            cancellationToken);
        usageByKey.TryGetValue(key, out var usage);
        usage ??= SaSoRevisionUsage.Empty;
        if (usage.IsUnused)
        {
            return null;
        }

        return SaSoOperationResult.Fail(usage.BlockMessage(mutation), SaSoErrorKind.BusinessRule);
    }

    /// <summary>
    /// Set-based revision usage for a page (or a single key). No per-row round trips.
    /// When <paramref name="includeLineConsumption"/> is false, caller merges consumption from another query
    /// (SearchAsync line-stats). Mutation checks pass true.
    /// </summary>
    private async Task<Dictionary<SaDocSoRevisionKey, SaSoRevisionUsage>> LoadRevisionUsageAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<SaDocSoRevisionKey> keys,
        bool includeLineConsumption,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<SaDocSoRevisionKey, SaSoRevisionUsage>(keys.Count);
        if (keys.Count == 0)
        {
            return result;
        }

        foreach (var key in keys)
        {
            result[key] = SaSoRevisionUsage.Empty;
        }

        var soNos = keys.Select(x => x.SoNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var custRels = keys.Select(x => x.CustRel).Distinct().ToList();

        var hasDelivered = new HashSet<SaDocSoRevisionKey>();
        var hasInvoiced = new HashSet<SaDocSoRevisionKey>();
        var hasShipped = new HashSet<SaDocSoRevisionKey>();
        var hasWrittenOff = new HashSet<SaDocSoRevisionKey>();
        if (includeLineConsumption)
        {
            var consumption = await db.SaSoDetails.AsNoTracking()
                .Where(x =>
                    x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && soNos.Contains(x.SoNo)
                    && custRels.Contains(x.CustRel)
                    && (x.DeliveredQty != 0m || x.InvoicedQty != 0m || x.ShippedQty != 0m || x.WrittenOffQty != 0m))
                .GroupBy(x => new { x.SoNo, x.CustRel })
                .Select(g => new
                {
                    g.Key.SoNo,
                    g.Key.CustRel,
                    HasDeliveredQty = g.Any(x => x.DeliveredQty != 0m),
                    HasInvoicedQty = g.Any(x => x.InvoicedQty != 0m),
                    HasShippedQty = g.Any(x => x.ShippedQty != 0m),
                    HasWrittenOffQty = g.Any(x => x.WrittenOffQty != 0m)
                })
                .ToListAsync(cancellationToken);

            foreach (var row in consumption)
            {
                var matched = MatchKey(keys, row.SoNo, row.CustRel);
                if (matched is null)
                {
                    continue;
                }

                if (row.HasDeliveredQty)
                {
                    hasDelivered.Add(matched.Value);
                }

                if (row.HasInvoicedQty)
                {
                    hasInvoiced.Add(matched.Value);
                }

                if (row.HasShippedQty)
                {
                    hasShipped.Add(matched.Value);
                }

                if (row.HasWrittenOffQty)
                {
                    hasWrittenOff.Add(matched.Value);
                }
            }
        }

        var allocated = await _docApplication.ListAllocatedSoKeysAsync(
            db,
            companyCode,
            branchCode,
            keys,
            cancellationToken);

        var draftDos = new HashSet<SaDocSoRevisionKey>();
        var doRefs = new HashSet<SaDocSoRevisionKey>();
        var draftInvoices = new HashSet<SaDocSoRevisionKey>();
        var invoiceRefs = new HashSet<SaDocSoRevisionKey>();

        var doPart =
            from d in db.SaDoDetails.AsNoTracking()
            join h in db.SaDos.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.DoNo }
                equals new { h.CompanyCode, h.BranchCode, h.DoNo }
            where d.CompanyCode == companyCode
                  && d.BranchCode == branchCode
                  && soNos.Contains(d.SoNo)
                  && d.CustRel != null
                  && custRels.Contains(d.CustRel.Value)
            select new
            {
                d.SoNo,
                CustRel = d.CustRel!.Value,
                IsDo = true,
                IsDraft = h.Status == SaDoStatuses.New
            };

        var invoicePart =
            from d in db.SaInvoiceDetails.AsNoTracking()
            join h in db.SaInvoices.AsNoTracking()
                on new { d.CompanyCode, d.BranchCode, d.InvNo }
                equals new { h.CompanyCode, h.BranchCode, h.InvNo }
            where d.CompanyCode == companyCode
                  && d.BranchCode == branchCode
                  && soNos.Contains(d.SoNo)
                  && d.CustRel != null
                  && custRels.Contains(d.CustRel.Value)
            select new
            {
                d.SoNo,
                CustRel = d.CustRel!.Value,
                IsDo = false,
                IsDraft = h.Status == SaInvoiceStatuses.New
            };

        var documentUsage = await doPart.Concat(invoicePart).ToListAsync(cancellationToken);
        foreach (var row in documentUsage)
        {
            var matched = MatchKey(keys, row.SoNo, row.CustRel);
            if (matched is null)
            {
                continue;
            }

            if (row.IsDo)
            {
                doRefs.Add(matched.Value);
                if (row.IsDraft)
                {
                    draftDos.Add(matched.Value);
                }
            }
            else
            {
                invoiceRefs.Add(matched.Value);
                if (row.IsDraft)
                {
                    draftInvoices.Add(matched.Value);
                }
            }
        }

        foreach (var key in keys)
        {
            result[key] = new SaSoRevisionUsage
            {
                HasDeliveredQty = hasDelivered.Contains(key),
                HasInvoicedQty = hasInvoiced.Contains(key),
                HasShippedQty = hasShipped.Contains(key),
                HasWrittenOffQty = hasWrittenOff.Contains(key),
                HasAllocation = allocated.Contains(key)
                    || allocated.Any(a =>
                        a.CustRel == key.CustRel
                        && string.Equals(a.SoNo, key.SoNo, StringComparison.OrdinalIgnoreCase)),
                HasDraftDeliveryOrder = draftDos.Contains(key),
                HasDraftInvoice = draftInvoices.Contains(key),
                HasDeliveryOrderReference = doRefs.Contains(key),
                HasInvoiceReference = invoiceRefs.Contains(key)
            };
        }

        return result;
    }

    private static SaDocSoRevisionKey? MatchKey(
        IReadOnlyList<SaDocSoRevisionKey> keys,
        string soNo,
        short custRel)
    {
        foreach (var key in keys)
        {
            if (key.CustRel == custRel
                && string.Equals(key.SoNo, soNo, StringComparison.OrdinalIgnoreCase))
            {
                return key;
            }
        }

        return null;
    }

    private static async Task AssertOneCurrentAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken)
    {
        var currentCount = await db.SaSos.AsNoTracking().CountAsync(
            x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.SoNo == soNo
                && x.IsCurrent,
            cancellationToken);
        if (currentCount != 1)
        {
            throw new InvalidOperationException($"Sales Order {soNo} must have exactly one current revision.");
        }
    }

    private static async Task AssertOneCurrentIfExistsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string soNo,
        CancellationToken cancellationToken)
    {
        var anyRevision = await db.SaSos.AsNoTracking().AnyAsync(
            x =>
                x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.SoNo == soNo,
            cancellationToken);
        if (anyRevision)
        {
            await AssertOneCurrentAsync(db, companyCode, branchCode, soNo, cancellationToken);
        }
    }

    private async Task<(string? Error, decimal Rate)> ResolveCurrRateAsync(
        AppDbContext db,
        string currency,
        DateTime soDate,
        CancellationToken cancellationToken)
    {
        var isHome = string.Equals(currency, SaInvoiceCalc.HomeCurrency, StringComparison.OrdinalIgnoreCase);
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency && x.Status && x.StartDate <= soDate && x.EndDate >= soDate)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            if (isHome)
            {
                return (null, 1m);
            }

            return ($"No currency rate for {currency} on {soDate:yyyy-MM-dd}.", 0m);
        }

        var decimalRate = Convert.ToDecimal(rate.Value);
        if (!isHome && decimalRate == 1m)
        {
            return ("Non-home currency rate cannot be 1.", 0m);
        }

        return (null, decimalRate);
    }

    private static DateTime? NormalizePlanningDate(DateTime? value) =>
        value is { } d && d != default ? d.Date : null;

    private static KeyValuePair<string, string>? ValidateLineDiscount(
        SaSoLineRequest line,
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

    private static List<SaSoKeyedRequest> NormalizeKeyedItems(IReadOnlyList<SaSoKeyedRequest>? items) =>
        (items ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.SoNo))
            .Select(x => new SaSoKeyedRequest
            {
                SoNo = x.SoNo.Trim(),
                CustRel = x.CustRel,
                RowVersion = x.RowVersion ?? []
            })
            .GroupBy(x => x.SoNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.SalesOrder, permission, cancellationToken);

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

    private static string? NormalizeRevisionReason(string? value) =>
        TruncateOptional(value, 200);

    private static string? UpperSnapshot(string? value, int maxLength)
    {
        var trimmed = TruncateOptional(value, maxLength);
        return trimmed?.ToUpperInvariant();
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
        public SaSoErrorKind Kind { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public SaCust? Customer { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; }
        public List<PreparedLine>? Lines { get; init; }

        public static PrepareOutcome Ok(SaCust customer, string currency, decimal rate, List<PreparedLine> lines) =>
            new() { Customer = customer, Currency = currency, CurrRate = rate, Lines = lines };

        public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = message, Kind = SaSoErrorKind.Validation, Errors = errors };

        public static PrepareOutcome Fail(string message, SaSoErrorKind kind) =>
            new() { Error = message, Kind = kind };

        public SaSoOperationResult ToFail() =>
            Kind == SaSoErrorKind.Validation
                ? SaSoOperationResult.FailValidation(Error ?? "Validation failed.", Errors)
                : SaSoOperationResult.Fail(Error ?? "Unable to save the Sales Order.", Kind);
    }

    private sealed class PreparedLine
    {
        public int RequestOrder { get; init; }
        public short? ExistingLineNo { get; init; }
        public string ICode { get; init; } = string.Empty;
        public string? IDesc { get; init; }
        public string? CustICode { get; init; }
        public decimal OrderQty { get; init; }
        public decimal StdQty { get; init; }
        public decimal StdPsize { get; init; }
        public string? SellingUom { get; init; }
        public string? StdUom { get; init; }
        public string? Warehouse { get; init; }
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
        public string? Remarks { get; init; }
        public bool StockControl { get; init; }
        public string? Classification { get; init; }
        public DateTime? DeliveryDate { get; init; }
        public DateTime? Eta { get; init; }
        public DateTime? Etd { get; init; }
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
