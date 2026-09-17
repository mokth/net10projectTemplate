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
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErpWeb.Core.Sales;

/// <summary>
/// Sales Quotation lifecycle. Mirrors <see cref="SaSoService"/> for revision mechanics and the
/// pricing/tax/font stack, and deliberately adds nothing that a quotation does not need: no
/// fulfilment or billing state, no stock reservation, no approval workflow, no force-close.
/// <para>
/// <b>Service invariant:</b> every lifecycle mutation loads the row with <c>UPDLOCK</c> and requires
/// <c>IsCurrent = true</c>. Historical (SUPERSEDED) revisions are read-only, and this is enforced
/// here rather than only in the UI.
/// </para>
/// </summary>
public sealed class SaQtService : ISaQtService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;
    private readonly IDocumentNumberingService _documentNumbers;
    private readonly ICurrentDateService _dates;
    private readonly ISaQtRepository _quotations;
    private readonly ISaCustRepository _customers;
    private readonly SaSoService _salesOrders;
    private readonly ErpWeb.Core.Settings.IAppSettingService _settings;
    private readonly ILogger<SaQtService> _logger;

    /// <summary>Test-only: after locking current revision, before validation.</summary>
    internal Action? TestHookAfterLockCurrent { get; set; }

    /// <summary>Test-only: after the current revision is superseded and saved, before the new insert.</summary>
    internal Action? TestHookAfterSupersedeBeforeInsert { get; set; }

    /// <summary>Test-only: after the SO snapshot is persisted, before the quotation is closed.</summary>
    internal Action? TestHookAfterSoInsertBeforeClose { get; set; }

    public SaQtService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights,
        IDocumentNumberingService documentNumbers,
        ICurrentDateService dates,
        ISaQtRepository quotations,
        ISaCustRepository customers,
        SaSoService salesOrders,
        ErpWeb.Core.Settings.IAppSettingService settings,
        ILogger<SaQtService> logger)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
        _documentNumbers = documentNumbers;
        _dates = dates;
        _quotations = quotations;
        _customers = customers;
        _salesOrders = salesOrders;
        _settings = settings;
        _logger = logger;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Lookups / read models
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<SaQtOperationResult> GetLookupsAsync(CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
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

        var projects = await db.MsProjects.AsNoTracking()
            .Where(x => x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.Status == MsProjectStatus.Active)
            .OrderBy(x => x.ProjCode)
            .Select(x => new IvCodeLookupRow { Code = x.ProjCode, Desc = x.ProjName })
            .ToListAsync(cancellationToken);

        return SaQtOperationResult.OkLookups(items, warehouses, customers, taxGroups, payCodes, projects);
    }

    public async Task<SaQtOperationResult> GetCustomerDefaultsAsync(
        string custCode,
        DateTime qtDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var code = (custCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return SaQtOperationResult.FailValidation(
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
            return SaQtOperationResult.Fail("Customer was not found or is inactive.", SaQtErrorKind.NotFound);
        }

        var currency = string.IsNullOrWhiteSpace(customer.Currency)
            ? SaInvoiceCalc.HomeCurrency
            : customer.Currency.Trim();
        var date = qtDate == default ? _dates.Today.Date : qtDate.Date;
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

        return SaQtOperationResult.OkDefaults(new SaSoCustomerDefaults
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
            ShipFax = useMainBill ? customer.Fax : customer.ShipFax,
            ShipToAddresses = shipToAddresses
        });
    }

    public async Task<SaQtOperationResult> ResolveCurrencyRateAsync(
        string currency,
        DateTime qtDate,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var curr = (currency ?? string.Empty).Trim();
        if (curr.Length == 0)
        {
            return SaQtOperationResult.FailValidation(
                "Currency is required.",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Currency"] = "Currency is required."
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var date = qtDate == default ? _dates.Today.Date : qtDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, curr, date, cancellationToken);
        if (rateResult.Error is not null)
        {
            return SaQtOperationResult.Fail(rateResult.Error, SaQtErrorKind.Validation);
        }

        return SaQtOperationResult.OkRate(rateResult.Rate, valid: true);
    }

    public async Task<SaQtOperationResult> SearchAsync(
        SaQtListQuery query,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        query ??= new SaQtListQuery();
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var (rows, total) = await _quotations.SearchPagedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            new SaQtSearchArgs(
                query.SearchText,
                query.Status,
                query.DateFrom,
                query.DateTo,
                query.SortField,
                query.SortDescending,
                query.Skip,
                query.Take),
            cancellationToken);

        // Lazy expiry on list: only the page's own documents are swept, so a list load can never
        // rewrite the whole table.
        var expired = await ExpireLapsedAsync(
            db,
            context.CompanyCode!,
            context.BranchCode!,
            rows.Select(x => x.QtNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            cancellationToken);
        if (expired)
        {
            (rows, total) = await _quotations.SearchPagedAsync(
                db,
                context.CompanyCode!,
                context.BranchCode!,
                new SaQtSearchArgs(
                    query.SearchText,
                    query.Status,
                    query.DateFrom,
                    query.DateTo,
                    query.SortField,
                    query.SortDescending,
                    query.Skip,
                    query.Take),
                cancellationToken);
        }

        var rowList = rows.ToList();
        if (rowList.Count == 0)
        {
            return SaQtOperationResult.OkList(new SaQtListPage { Rows = [], TotalCount = total });
        }

        var qtNos = rowList.Select(x => x.QtNo).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var lineStats = await db.SaQtDetails.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && qtNos.Contains(x.QtNo))
            .GroupBy(x => new { x.QtNo, x.CustRel })
            .Select(g => new
            {
                g.Key.QtNo,
                g.Key.CustRel,
                LineCount = g.Count(),
                HasConverted = g.Any(x => x.ConvertedQty != 0m)
            })
            .ToListAsync(cancellationToken);

        var convertedSo = await db.SaSos.AsNoTracking()
            .Where(x =>
                x.CompanyCode == context.CompanyCode
                && x.BranchCode == context.BranchCode
                && x.QtNo != null
                && qtNos.Contains(x.QtNo))
            .Select(x => new { x.QtNo, x.QtCustRel, x.SoNo })
            .ToListAsync(cancellationToken);

        var today = _dates.Today.Date;
        var statsByKey = lineStats.ToDictionary(
            x => (x.QtNo.ToUpperInvariant(), x.CustRel),
            x => x);
        var soByKey = new Dictionary<(string, short), string>();
        foreach (var row in convertedSo)
        {
            if (row.QtCustRel is not { } custRel || row.QtNo is null)
            {
                continue;
            }

            soByKey[(row.QtNo.ToUpperInvariant(), custRel)] = row.SoNo;
        }

        var listRows = new List<SaQtListRow>(rowList.Count);
        foreach (var row in rowList)
        {
            var key = (row.QtNo.ToUpperInvariant(), row.CustRel);
            statsByKey.TryGetValue(key, out var stats);
            var lineCount = stats?.LineCount ?? 0;
            var hasConverted = stats?.HasConverted ?? false;
            soByKey.TryGetValue(key, out var soNo);
            var isExpired = SaQtValidity.IsExpired(row.ValidUntil, today);

            listRows.Add(new SaQtListRow
            {
                QtNo = row.QtNo,
                CustRel = row.CustRel,
                QtDate = row.QtDate,
                ValidUntil = row.ValidUntil,
                Status = row.Status,
                ConversionStatus = row.ConversionStatus,
                CustCode = row.CustCode,
                CustName = row.CustName,
                CustPo = row.CustPo,
                TotAmnt = row.TotAmnt,
                LineCount = lineCount,
                SalesRep = row.SalesRep,
                CreatedBy = row.CreatedBy,
                CreatedDate = row.CreatedDate,
                RowVersion = row.RowVersion ?? [],
                ConvertedSoNo = soNo,
                IsExpired = isExpired,
                CanEdit = row.Status == SaQtStatuses.New,
                CanSend = row.Status == SaQtStatuses.New && !isExpired,
                CanAccept = row.Status == SaQtStatuses.Sent && !isExpired,
                CanLose = row.Status is SaQtStatuses.New or SaQtStatuses.Sent,
                CanCancel = row.Status is SaQtStatuses.New or SaQtStatuses.Sent,
                CanRevise = SaQtStatuses.Revisable.Contains(row.Status) && !hasConverted && soNo is null,
                CanConvert = row.Status == SaQtStatuses.Accepted
                    && row.ConversionStatus == SaQtConversionStatuses.None
                    && !hasConverted
                    && !isExpired,
                CanDelete = row.Status == SaQtStatuses.New
                    && row.ConversionStatus == SaQtConversionStatuses.None
                    && !hasConverted
                    && soNo is null,
                MutationBlockReason = ResolveMutationBlockReason(row.Status, hasConverted, soNo)
            });
        }

        return SaQtOperationResult.OkList(new SaQtListPage { Rows = listRows, TotalCount = total });
    }

    public async Task<SaQtOperationResult> GetAsync(
        string qtNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var no = (qtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaQtOperationResult.FailValidation("Quotation number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Lazy expiry on open.
        await ExpireLapsedAsync(db, context.CompanyCode!, context.BranchCode!, [no], cancellationToken);

        var quotation = await _quotations.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (quotation is null)
        {
            return SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound);
        }

        var revisions = await _quotations.ListRevisionsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        var convertedSoNo = await _quotations.FindConvertedSoNoAsync(
            db, context.CompanyCode!, context.BranchCode!, no, quotation.CustRel, cancellationToken);

        return SaQtOperationResult.OkDocument(MapDocument(quotation, revisions, convertedSoNo, _dates.Today.Date));
    }

    public async Task<SaQtOperationResult> GetAsync(
        string qtNo,
        short custRel,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Access, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var no = (qtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaQtOperationResult.FailValidation("Quotation number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var quotation = await _quotations.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, custRel, cancellationToken);
        if (quotation is null)
        {
            return SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound);
        }

        var revisions = await _quotations.ListRevisionsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        var convertedSoNo = await _quotations.FindConvertedSoNoAsync(
            db, context.CompanyCode!, context.BranchCode!, no, custRel, cancellationToken);

        return SaQtOperationResult.OkDocument(MapDocument(quotation, revisions, convertedSoNo, _dates.Today.Date));
    }

    public async Task<SaQtOperationResult> GetReviseDraftAsync(
        string qtNo,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateUserContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var no = (qtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaQtOperationResult.FailValidation("Quotation number is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var current = await _quotations.GetWithDetailsAsync(
            db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
        if (current is null)
        {
            return SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound);
        }

        if (!current.IsCurrent)
        {
            return SaQtOperationResult.Fail(SaQtReasonCodes.SupersededMessage, SaQtErrorKind.BusinessRule);
        }

        if (!SaQtStatuses.Revisable.Contains(current.Status))
        {
            return SaQtOperationResult.Fail(
                $"Only NEW, SENT or ACCEPTED quotations can be revised. This quotation is {current.Status}.",
                SaQtErrorKind.BusinessRule);
        }

        var blocked = await CheckRevisionUnusedAsync(
            db, context.CompanyCode!, context.BranchCode!, no, current.CustRel, cancellationToken);
        if (blocked is not null)
        {
            return blocked;
        }

        if (current.LastCustRel >= SaQtRevisionLimits.MaxCustRel)
        {
            return SaQtOperationResult.Fail(SaQtReasonCodes.RevisionLimitMessage, SaQtErrorKind.BusinessRule);
        }

        var nextCustRel = (short)(current.LastCustRel + 1);
        var document = MapDocument(current, null, null, _dates.Today.Date);
        return SaQtOperationResult.OkDocument(MapRevisionDraft(document, nextCustRel));
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Lifecycle
    // ─────────────────────────────────────────────────────────────────────────────

    public async Task<SaQtOperationResult> SaveNewAsync(
        SaQtSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaQtOperationResult.FailValidation("Save request is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Add, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var validUntilResult = await ResolveValidUntilAsync(
            request.QtDate, request.ValidUntil, context.CompanyCode, cancellationToken);
        if (validUntilResult.Error is not null)
        {
            return SaQtOperationResult.FailValidation(
                validUntilResult.Error,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ValidUntil"] = validUntilResult.Error
                });
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
                priorProjId: null,
                cancellationToken);
            if (prepared.Error is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return prepared.ToFail();
            }

            var qtDate = request.QtDate == default ? _dates.Today.Date : request.QtDate.Date;
            DocumentNumberResult issued;
            try
            {
                issued = await _documentNumbers.NextAsync(
                    db,
                    "QT",
                    "",
                    qtDate,
                    DocumentNumberRequestMode.New,
                    "AUTO",
                    cancellationToken);
            }
            catch (DocumentNumberingNotConfiguredException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "Quotation numbering is not configured for this company/branch.",
                    SaQtErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConfigurationException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "Quotation numbering is not configured correctly. Contact an administrator.",
                    SaQtErrorKind.BusinessRule);
            }
            catch (DocumentNumberingOverflowException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "The next quotation number exceeds the configured length.",
                    SaQtErrorKind.BusinessRule);
            }
            catch (DocumentNumberingConcurrencyException)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "The quotation could not be saved because of a database conflict. Try again.",
                    SaQtErrorKind.Unexpected);
            }

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);
            var quotation = new SaQt
            {
                CompanyCode = context.CompanyCode!,
                BranchCode = context.BranchCode!,
                LocationCode = context.LocationCode,
                QtNo = issued.DocumentNumber,
                QtDate = qtDate,
                ValidUntil = validUntilResult.Value,
                Status = SaQtStatuses.New,
                ConversionStatus = SaQtConversionStatuses.None,
                ClosedReason = null,
                CustRel = 1,
                IsCurrent = true,
                LastCustRel = 1,
                RevisionReason = null,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                Prefix = string.IsNullOrWhiteSpace(issued.PrefixUsed) ? null : issued.PrefixUsed.Trim(),
                CreatedDate = now,
                CreatedBy = uid
            };

            SaDocApplicationService.TouchQtRowVersion(db, quotation);
            ApplyHeaderSnapshots(quotation, request);
            ApplyQtOnlyHeaderFields(quotation, request);
            AddDetails(quotation, prepared.Lines!);
            ApplyCalculatedTotals(quotation, prepared.Lines!, prepared.Customer.DecPoint == true);

            db.SaQts.Add(quotation);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Sales Quotation saved. UserId={UserId} Company={Company} QtNo={QtNo}",
                context.UserId,
                context.CompanyCode,
                quotation.QtNo);

            return await GetAsync(quotation.QtNo, cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Quotation number is already used.", SaQtErrorKind.Unexpected);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Quotation save deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "The quotation could not be saved because of a database conflict. Try again.",
                SaQtErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation save failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to save the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> UpdateAsync(
        string qtNo,
        SaQtSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaQtOperationResult.FailValidation("Save request is required.");
        }

        var no = (qtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaQtOperationResult.FailValidation("Quotation number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before saving.",
                SaQtErrorKind.Concurrency);
        }

        var validUntilResult = await ResolveValidUntilAsync(
            request.QtDate, request.ValidUntil, context.CompanyCode, cancellationToken);
        if (validUntilResult.Error is not null)
        {
            return SaQtOperationResult.FailValidation(
                validUntilResult.Error,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ValidUntil"] = validUntilResult.Error
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await _quotations.LockForUpdateAsync(
                db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (quotation is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound);
            }

            TestHookAfterLockCurrent?.Invoke();

            var currentGate = CheckIsCurrent(quotation, request.CustRel);
            if (currentGate is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return currentGate;
            }

            if (!RowVersionsEqual(quotation.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "This quotation was changed by another user. Reload before saving.",
                    SaQtErrorKind.Concurrency);
            }

            db.Entry(quotation).Property(x => x.RowVersion).OriginalValue = request.RowVersion;
            await db.Entry(quotation).Collection(x => x.Details).LoadAsync(cancellationToken);

            if (!string.Equals(quotation.Status, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "Only NEW quotations can be edited. Use Revise to change a sent or accepted quotation.",
                    SaQtErrorKind.BusinessRule);
            }

            var existingByLine = quotation.Details.ToDictionary(x => x.Line, x => x);
            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                existingByLine,
                priorProjId: quotation.ProjId,
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
            var nextLine = 1;
            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);
            var orderedDetails = new List<SaQtDetail>();
            foreach (var line in prepared.Lines!)
            {
                if (line.ExistingLineNo is short existingLineNo
                    && existingByLine.TryGetValue(existingLineNo, out var existingDetail))
                {
                    ApplyPreparedLine(existingDetail, line, quotation.CustRel);
                    orderedDetails.Add(existingDetail);
                    continue;
                }

                var created = new SaQtDetail
                {
                    CompanyCode = quotation.CompanyCode,
                    BranchCode = quotation.BranchCode,
                    QtNo = quotation.QtNo,
                    CustRel = quotation.CustRel,
                    Line = 0,
                    ConvertedQty = 0m
                };
                ApplyPreparedLine(created, line, quotation.CustRel);
                orderedDetails.Add(created);
            }

            // Re-number the surviving + new lines into a dense 1..n sequence; removed lines are
            // dropped. Remap so an untouched line keeps its number whenever the prefix is stable.
            var removed = quotation.Details.Where(x => !requestedExistingLines.Contains(x.Line)).ToList();
            foreach (var detail in removed)
            {
                quotation.Details.Remove(detail);
                db.SaQtDetails.Remove(detail);
            }

            var usedNumbers = new HashSet<short>();
            foreach (var detail in orderedDetails.Where(x => x.Line > 0))
            {
                usedNumbers.Add(detail.Line);
            }

            foreach (var detail in orderedDetails)
            {
                if (detail.Line > 0)
                {
                    continue;
                }

                while (usedNumbers.Contains((short)nextLine))
                {
                    nextLine++;
                }

                detail.Line = (short)nextLine;
                usedNumbers.Add((short)nextLine);
                quotation.Details.Add(detail);
            }

            quotation.QtDate = request.QtDate == default ? quotation.QtDate : request.QtDate.Date;
            quotation.ValidUntil = validUntilResult.Value;
            quotation.CustCode = prepared.Customer!.CustCode;
            quotation.CustName = UpperSnapshot(prepared.Customer.CustName, 200);
            quotation.Currency = prepared.Currency;
            quotation.CurrRate = prepared.CurrRate;
            ApplyHeaderSnapshots(quotation, request);
            ApplyQtOnlyHeaderFields(quotation, request);
            ApplyCalculatedTotals(quotation, prepared.Lines!, prepared.Customer.DecPoint == true);
            quotation.ModifiedDate = now;
            quotation.ModifiedBy = uid;
            SaDocApplicationService.TouchQtRowVersion(db, quotation);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before saving.",
                SaQtErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Quotation update deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "The quotation could not be saved because of a database conflict. Try again.",
                SaQtErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation update failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to save the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> ReviseAsync(
        string qtNo,
        SaQtSaveRequest? request,
        CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            return SaQtOperationResult.FailValidation("Save request is required.");
        }

        var no = (qtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return SaQtOperationResult.FailValidation("Quotation number is required.");
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Edit, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        if (request.RowVersion is null || request.RowVersion.Length == 0)
        {
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before revising.",
                SaQtErrorKind.Concurrency);
        }

        var validUntilResult = await ResolveValidUntilAsync(
            request.QtDate, request.ValidUntil, context.CompanyCode, cancellationToken);
        if (validUntilResult.Error is not null)
        {
            return SaQtOperationResult.FailValidation(
                validUntilResult.Error,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["ValidUntil"] = validUntilResult.Error
                });
        }

        var revisionReason = NormalizeRevisionReason(request.RevisionReason);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var current = await _quotations.LockForUpdateAsync(
                db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            if (current is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound);
            }

            TestHookAfterLockCurrent?.Invoke();

            var currentGate = CheckIsCurrent(current, request.CustRel);
            if (currentGate is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return currentGate;
            }

            if (!RowVersionsEqual(current.RowVersion, request.RowVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "This quotation was changed by another user. Reload before revising.",
                    SaQtErrorKind.Concurrency);
            }

            db.Entry(current).Property(x => x.RowVersion).OriginalValue = request.RowVersion;

            if (!SaQtStatuses.Revisable.Contains(current.Status))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    $"Only NEW, SENT or ACCEPTED quotations can be revised. This quotation is {current.Status}.",
                    SaQtErrorKind.BusinessRule);
            }

            if (current.LastCustRel >= SaQtRevisionLimits.MaxCustRel)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(SaQtReasonCodes.RevisionLimitMessage, SaQtErrorKind.BusinessRule);
            }

            // Revising past a sent/accepted offer is a commercial statement about the customer, so the
            // reason is mandatory there — but a brand-new unsent draft needs no justification.
            var reasonRequired =
                string.Equals(current.Status, SaQtStatuses.Sent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(current.Status, SaQtStatuses.Accepted, StringComparison.OrdinalIgnoreCase);
            if (reasonRequired && revisionReason is null)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.FailValidation(
                    SaQtReasonCodes.RevisionReasonRequired,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["RevisionReason"] = SaQtReasonCodes.RevisionReasonRequired
                    });
            }

            var blocked = await CheckRevisionUnusedAsync(
                db, context.CompanyCode!, context.BranchCode!, no, current.CustRel, cancellationToken);
            if (blocked is not null)
            {
                await tx.RollbackAsync(cancellationToken);
                return blocked;
            }

            await db.Entry(current).Collection(x => x.Details).LoadAsync(cancellationToken);
            var existingByLine = current.Details.ToDictionary(x => x.Line, x => x);
            var prepared = await PrepareLinesAsync(
                db,
                request,
                context.CompanyCode!,
                context.BranchCode!,
                existingByLine,
                priorProjId: current.ProjId,
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
            current.Status = SaQtStatuses.Superseded;
            current.ModifiedDate = now;
            current.ModifiedBy = uid;
            SaDocApplicationService.TouchQtRowVersion(db, current);
            await db.SaveChangesAsync(cancellationToken);

            TestHookAfterSupersedeBeforeInsert?.Invoke();

            var revised = new SaQt
            {
                CompanyCode = current.CompanyCode,
                BranchCode = current.BranchCode,
                LocationCode = context.LocationCode,
                QtNo = current.QtNo,
                CustRel = nextCustRel,
                IsCurrent = true,
                LastCustRel = nextCustRel,
                RevisionReason = revisionReason,
                QtDate = request.QtDate == default ? _dates.Today.Date : request.QtDate.Date,
                ValidUntil = validUntilResult.Value,
                Status = SaQtStatuses.New,
                // Acceptance never carries forward: the customer must accept the new offer.
                ConversionStatus = SaQtConversionStatuses.None,
                ClosedReason = null,
                ClosedDate = null,
                ClosedBy = null,
                SentDate = null,
                SentBy = null,
                AcceptedDate = null,
                AcceptedBy = null,
                LostDate = null,
                LostBy = null,
                LostReason = null,
                ExpiredDate = null,
                CustCode = prepared.Customer!.CustCode,
                CustName = UpperSnapshot(prepared.Customer.CustName, 200),
                Currency = prepared.Currency,
                CurrRate = prepared.CurrRate,
                Prefix = current.Prefix,
                CreatedDate = now,
                CreatedBy = uid
            };

            SaDocApplicationService.TouchQtRowVersion(db, revised);
            ApplyHeaderSnapshots(revised, request);
            ApplyQtOnlyHeaderFields(revised, request);
            AddDetails(revised, prepared.Lines!);
            ApplyCalculatedTotals(revised, prepared.Lines!, prepared.Customer.DecPoint == true);

            db.SaQts.Add(revised);
            await db.SaveChangesAsync(cancellationToken);
            await AssertOneCurrentAsync(db, context.CompanyCode!, context.BranchCode!, no, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before revising.",
                SaQtErrorKind.Concurrency);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was revised by another user. Reload before revising.",
                SaQtErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Quotation revise deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "The quotation could not be revised because of a database conflict. Try again.",
                SaQtErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation revise failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to revise the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> SendAsync(
        SaQtKeyedRequest? request,
        CancellationToken cancellationToken = default)
    {
        var (gate, context, no) = await ValidateKeyedAsync(request, PermissionCodes.Submit, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await LockCurrentAsync(db, context, no!, request!.RowVersion, tx, cancellationToken);
            if (quotation.Result is not null)
            {
                return quotation.Result;
            }

            var current = quotation.Quotation!;
            if (!string.Equals(current.Status, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    $"Only a NEW quotation can be sent. This quotation is {current.Status}.",
                    SaQtErrorKind.BusinessRule);
            }

            // A lapsed offer is expired rather than sent.
            var expiryGate = await EnsureNotExpiredAsync(db, current, tx, cancellationToken);
            if (expiryGate is not null)
            {
                return expiryGate;
            }

            var now = DateTime.UtcNow;
            current.Status = SaQtStatuses.Sent;
            current.SentDate = now;
            current.SentBy = Truncate(context.UserId!, 20);
            current.ModifiedDate = now;
            current.ModifiedBy = current.SentBy;
            SaDocApplicationService.TouchQtRowVersion(db, current);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no!, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before sending.",
                SaQtErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation send failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to send the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> AcceptAsync(
        SaQtKeyedRequest? request,
        CancellationToken cancellationToken = default)
    {
        var (gate, context, no) = await ValidateKeyedAsync(request, PermissionCodes.Approve, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await LockCurrentAsync(db, context, no!, request!.RowVersion, tx, cancellationToken);
            if (quotation.Result is not null)
            {
                return quotation.Result;
            }

            var current = quotation.Quotation!;
            if (!string.Equals(current.Status, SaQtStatuses.Sent, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    $"Only a SENT quotation can be accepted. This quotation is {current.Status}.",
                    SaQtErrorKind.BusinessRule);
            }

            // Acceptance checks expiry too: an offer that has lapsed can never be accepted, and the
            // refusal carries the reason (the quotation is left EXPIRED, not ACCEPTED).
            var expiryGate = await EnsureNotExpiredAsync(db, current, tx, cancellationToken);
            if (expiryGate is not null)
            {
                return expiryGate;
            }

            var now = DateTime.UtcNow;
            current.Status = SaQtStatuses.Accepted;
            current.AcceptedDate = now;
            current.AcceptedBy = Truncate(context.UserId!, 20);
            current.ModifiedDate = now;
            current.ModifiedBy = current.AcceptedBy;
            SaDocApplicationService.TouchQtRowVersion(db, current);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no!, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before accepting.",
                SaQtErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation accept failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to accept the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> LoseAsync(
        SaQtKeyedRequest? request,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var (gate, context, no) = await ValidateKeyedAsync(request, PermissionCodes.Reject, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        var lostReason = TruncateOptional(reason, 200);
        if (lostReason is null)
        {
            return SaQtOperationResult.FailValidation(
                SaQtReasonCodes.LostReasonRequired,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["LostReason"] = SaQtReasonCodes.LostReasonRequired
                });
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await LockCurrentAsync(db, context, no!, request!.RowVersion, tx, cancellationToken);
            if (quotation.Result is not null)
            {
                return quotation.Result;
            }

            var current = quotation.Quotation!;
            if (current.Status is not (SaQtStatuses.New or SaQtStatuses.Sent))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    $"Only a NEW or SENT quotation can be marked lost. This quotation is {current.Status}.",
                    SaQtErrorKind.BusinessRule);
            }

            var now = DateTime.UtcNow;
            current.Status = SaQtStatuses.Lost;
            current.LostDate = now;
            current.LostBy = Truncate(context.UserId!, 20);
            current.LostReason = lostReason;
            current.ModifiedDate = now;
            current.ModifiedBy = current.LostBy;
            SaDocApplicationService.TouchQtRowVersion(db, current);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no!, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before continuing.",
                SaQtErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation lose failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to update the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> CancelAsync(
        SaQtKeyedRequest? request,
        CancellationToken cancellationToken = default)
    {
        var (gate, context, no) = await ValidateKeyedAsync(request, PermissionCodes.Cancel, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await LockCurrentAsync(db, context, no!, request!.RowVersion, tx, cancellationToken);
            if (quotation.Result is not null)
            {
                return quotation.Result;
            }

            var current = quotation.Quotation!;
            if (current.Status is not (SaQtStatuses.New or SaQtStatuses.Sent))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    $"Only a NEW or SENT quotation can be cancelled. This quotation is {current.Status}.",
                    SaQtErrorKind.BusinessRule);
            }

            var now = DateTime.UtcNow;
            current.Status = SaQtStatuses.Cancelled;
            current.ModifiedDate = now;
            current.ModifiedBy = Truncate(context.UserId!, 20);
            SaDocApplicationService.TouchQtRowVersion(db, current);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return await GetAsync(no!, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before cancelling.",
                SaQtErrorKind.Concurrency);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation cancel failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to cancel the quotation.", SaQtErrorKind.Unexpected);
        }
    }

    /// <summary>
    /// Converts an accepted quotation into exactly one Sales Order, copying the quotation's frozen
    /// commercial snapshot rather than re-pricing or re-taxing it.
    /// <para>
    /// Everything happens inside one transaction: the SO insert, the quotation line consumption and
    /// the CLOSED / CONVERTED state. Any failure rolls the whole conversion back, so a partially
    /// converted quotation can never be observed.
    /// </para>
    /// </summary>
    public async Task<SaQtOperationResult> ConvertToSoAsync(
        SaQtKeyedRequest? request,
        CancellationToken cancellationToken = default)
    {
        var (gate, context, no) = await ValidateKeyedAsync(request, PermissionCodes.Close, cancellationToken);
        if (gate is not null)
        {
            return gate;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            var quotation = await LockCurrentAsync(db, context, no!, request!.RowVersion, tx, cancellationToken);
            if (quotation.Result is not null)
            {
                return quotation.Result;
            }

            var current = quotation.Quotation!;

            if (!string.Equals(current.Status, SaQtStatuses.Accepted, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    SaQtReasonCodes.NotAcceptedMessage,
                    SaQtErrorKind.BusinessRule);
            }

            if (!string.Equals(current.ConversionStatus, SaQtConversionStatuses.None, StringComparison.OrdinalIgnoreCase))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    SaQtReasonCodes.AlreadyConvertedMessage,
                    SaQtErrorKind.BusinessRule);
            }

            // An accepted offer that has passed ValidUntil keeps its ACCEPTED status (it never
            // auto-expires) but may not be converted.
            if (SaQtValidity.IsExpired(current.ValidUntil, _dates.Today))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(SaQtReasonCodes.ExpiredMessage, SaQtErrorKind.BusinessRule);
            }

            await db.Entry(current).Collection(x => x.Details).LoadAsync(cancellationToken);
            if (current.Details.Count == 0)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    "The quotation has no lines to convert.",
                    SaQtErrorKind.BusinessRule);
            }

            if (current.Details.Any(x => x.ConvertedQty != 0m))
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    SaQtReasonCodes.AlreadyConvertedMessage,
                    SaQtErrorKind.BusinessRule);
            }

            // Duplicate-conversion guard that works on every provider (the filtered unique index
            // UX_SaSO_QtSource is the database backstop, not the primary check).
            var alreadyConverted = await _quotations.HasConvertedSalesOrderAsync(
                db, context.CompanyCode!, context.BranchCode!, current.QtNo, current.CustRel, cancellationToken);
            if (alreadyConverted)
            {
                await tx.RollbackAsync(cancellationToken);
                return SaQtOperationResult.Fail(
                    SaQtReasonCodes.AlreadyConvertedMessage,
                    SaQtErrorKind.BusinessRule);
            }

            var import = await _salesOrders.ImportQuotationSnapshotAsync(
                db,
                current,
                context.UserId!,
                context.LocationCode,
                cancellationToken);
            if (!import.Succeeded || string.IsNullOrWhiteSpace(import.SoNo))
            {
                await tx.RollbackAsync(cancellationToken);
                _logger.LogWarning(
                    "Quotation conversion could not create the Sales Order. Company={Company} QtNo={QtNo} CustRel={CustRel} Reason={Reason}",
                    context.CompanyCode,
                    current.QtNo,
                    current.CustRel,
                    import.ErrorMessage);
                return SaQtOperationResult.Fail(
                    import.ErrorMessage ?? "Unable to create the Sales Order from this quotation.",
                    SaQtErrorKind.BusinessRule);
            }

            TestHookAfterSoInsertBeforeClose?.Invoke();

            var now = DateTime.UtcNow;
            var uid = Truncate(context.UserId!, 20);

            // Consume every line in full. MVP never produces PARTIAL.
            foreach (var detail in current.Details)
            {
                SaQtQty.Consume(detail, detail.OrderQty);
            }

            current.ConversionStatus = SaQtConversionStatuses.Full;
            current.Status = SaQtStatuses.Closed;
            current.ClosedReason = SaQtClosedReasons.Converted;
            current.ClosedDate = now;
            current.ClosedBy = uid;
            current.ModifiedDate = now;
            current.ModifiedBy = uid;
            SaDocApplicationService.TouchQtRowVersion(db, current);

            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            _logger.LogInformation(
                "Sales Quotation converted. UserId={UserId} Company={Company} QtNo={QtNo} CustRel={CustRel} SoNo={SoNo}",
                context.UserId,
                context.CompanyCode,
                current.QtNo,
                current.CustRel,
                import.SoNo);

            return SaQtOperationResult.OkConverted(current.QtNo, import.SoNo);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before converting.",
                SaQtErrorKind.Concurrency);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await tx.RollbackAsync(cancellationToken);
            _logger.LogWarning(ex, "Quotation conversion hit the duplicate-source guard.");
            return SaQtOperationResult.Fail(
                SaQtReasonCodes.AlreadyConvertedMessage,
                SaQtErrorKind.BusinessRule);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Quotation conversion deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "The quotation could not be converted because of a database conflict. Try again.",
                SaQtErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation conversion failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "Unable to convert the quotation to a Sales Order.",
                SaQtErrorKind.Unexpected);
        }
    }

    public async Task<SaQtOperationResult> DeleteAsync(
        IReadOnlyList<SaQtKeyedRequest>? items,
        CancellationToken cancellationToken = default)
    {
        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return SaQtOperationResult.Fail(context.Error);
        }

        if (!await CanAsync(PermissionCodes.Delete, cancellationToken))
        {
            return SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization);
        }

        var keyed = NormalizeKeyedItems(items);
        if (keyed.Count == 0)
        {
            return SaQtOperationResult.Fail("Select at least one quotation.");
        }

        if (keyed.Count > SaQtLimits.MaxDistinctQtHeaders)
        {
            return SaQtOperationResult.Fail(
                $"Select at most {SaQtLimits.MaxDistinctQtHeaders} quotations to delete.",
                SaQtErrorKind.BusinessRule);
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
                    return SaQtOperationResult.Fail(
                        $"Quotation {item.QtNo}: row version is required for delete.",
                        SaQtErrorKind.Concurrency);
                }

                var quotation = await _quotations.LockForUpdateAsync(
                    db, context.CompanyCode!, context.BranchCode!, item.QtNo, cancellationToken);
                if (quotation is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaQtOperationResult.Fail(
                        $"Quotation {item.QtNo} was not found.", SaQtErrorKind.NotFound);
                }

                var currentGate = CheckIsCurrent(quotation, item.CustRel);
                if (currentGate is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return currentGate;
                }

                if (!RowVersionsEqual(quotation.RowVersion, item.RowVersion))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaQtOperationResult.Fail(
                        $"Quotation {item.QtNo} was changed by another user. Reload before deleting.",
                        SaQtErrorKind.Concurrency);
                }

                db.Entry(quotation).Property(x => x.RowVersion).OriginalValue = item.RowVersion;
                await db.Entry(quotation).Collection(x => x.Details).LoadAsync(cancellationToken);

                if (!string.Equals(quotation.Status, SaQtStatuses.New, StringComparison.OrdinalIgnoreCase))
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaQtOperationResult.Fail(
                        $"Quotation {item.QtNo} cannot be deleted because it is not NEW.",
                        SaQtErrorKind.BusinessRule);
                }

                var blocked = await CheckRevisionUnusedAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    quotation.QtNo,
                    quotation.CustRel,
                    cancellationToken);
                if (blocked is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return blocked;
                }

                if (quotation.CustRel == 1)
                {
                    db.SaQtDetails.RemoveRange(quotation.Details);
                    db.SaQts.Remove(quotation);
                    continue;
                }

                var previous = await db.SaQts
                    .Include(x => x.Details)
                    .Where(x =>
                        x.CompanyCode == context.CompanyCode
                        && x.BranchCode == context.BranchCode
                        && x.QtNo == quotation.QtNo
                        && x.CustRel < quotation.CustRel
                        && !x.IsCurrent
                        && x.Status == SaQtStatuses.Superseded)
                    .OrderByDescending(x => x.CustRel)
                    .FirstOrDefaultAsync(cancellationToken);
                if (previous is null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return SaQtOperationResult.Fail(
                        $"Quotation {item.QtNo} previous revision was not found.",
                        SaQtErrorKind.Unexpected);
                }

                var previousGate = await CheckRevisionUnusedAsync(
                    db,
                    context.CompanyCode!,
                    context.BranchCode!,
                    previous.QtNo,
                    previous.CustRel,
                    cancellationToken);
                if (previousGate is not null)
                {
                    await tx.RollbackAsync(cancellationToken);
                    return previousGate;
                }

                var now = DateTime.UtcNow;
                var uid = Truncate(context.UserId!, 20);
                db.SaQtDetails.RemoveRange(quotation.Details);
                db.SaQts.Remove(quotation);
                await db.SaveChangesAsync(cancellationToken);

                // Promote the previous revision back to NEW. Its lifecycle stamps are cleared because
                // it is a draft again — the supersede that displaced it is being undone.
                previous.IsCurrent = true;
                previous.Status = SaQtStatuses.New;
                previous.LastCustRel = Math.Max(previous.LastCustRel, quotation.LastCustRel);
                previous.ConversionStatus = SaQtConversionStatuses.None;
                previous.ClosedReason = null;
                previous.ClosedDate = null;
                previous.ClosedBy = null;
                previous.SentDate = null;
                previous.SentBy = null;
                previous.AcceptedDate = null;
                previous.AcceptedBy = null;
                previous.LostDate = null;
                previous.LostBy = null;
                previous.LostReason = null;
                previous.ExpiredDate = null;
                previous.ModifiedDate = now;
                previous.ModifiedBy = uid;
                SaDocApplicationService.TouchQtRowVersion(db, previous);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var qtNo in keyed.Select(x => x.QtNo).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                await AssertOneCurrentIfExistsAsync(db, context.CompanyCode!, context.BranchCode!, qtNo, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
            return SaQtOperationResult.Ok();
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "A selected quotation was changed by another user. Reload before deleting.",
                SaQtErrorKind.Concurrency);
        }
        catch (SqlException ex) when (ex.Number == 1205)
        {
            _logger.LogWarning(ex, "Quotation delete deadlock.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail(
                "The quotation(s) could not be deleted because of a database conflict. Try again.",
                SaQtErrorKind.Unexpected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Quotation delete failed.");
            await tx.RollbackAsync(cancellationToken);
            return SaQtOperationResult.Fail("Unable to delete the quotation(s).", SaQtErrorKind.Unexpected);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mutation helpers
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Locks the current revision and applies the three invariants every lifecycle mutation shares:
    /// the row must exist, <c>IsCurrent</c> must be true, and the caller's RowVersion must match.
    /// </summary>
    private async Task<(SaQtOperationResult? Result, SaQt? Quotation)> LockCurrentAsync(
        AppDbContext db,
        UserContext context,
        string qtNo,
        byte[] rowVersion,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken cancellationToken)
    {
        if (rowVersion.Length == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return (SaQtOperationResult.Fail(
                "Row version is required. Reload the quotation before continuing.",
                SaQtErrorKind.Concurrency), null);
        }

        var quotation = await _quotations.LockForUpdateAsync(
            db, context.CompanyCode!, context.BranchCode!, qtNo, cancellationToken);
        if (quotation is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return (SaQtOperationResult.Fail("Quotation was not found.", SaQtErrorKind.NotFound), null);
        }

        TestHookAfterLockCurrent?.Invoke();

        var currentGate = CheckIsCurrent(quotation, requestedCustRel: null);
        if (currentGate is not null)
        {
            await tx.RollbackAsync(cancellationToken);
            return (currentGate, null);
        }

        if (!RowVersionsEqual(quotation.RowVersion, rowVersion))
        {
            await tx.RollbackAsync(cancellationToken);
            return (SaQtOperationResult.Fail(
                "This quotation was changed by another user. Reload before continuing.",
                SaQtErrorKind.Concurrency), null);
        }

        db.Entry(quotation).Property(x => x.RowVersion).OriginalValue = rowVersion;
        return (null, quotation);
    }

    /// <summary>
    /// Expiry gate shared by Send / Accept. A lapsed NEW or SENT quotation becomes EXPIRED and the
    /// caller's action is refused. An ACCEPTED quotation is never touched here — its status stays
    /// ACCEPTED, and it is <c>ConvertToSoAsync</c> that refuses conversion past ValidUntil.
    /// </summary>
    private async Task<SaQtOperationResult?> EnsureNotExpiredAsync(
        AppDbContext db,
        SaQt quotation,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken cancellationToken)
    {
        if (!SaQtStatuses.Expirable.Contains(quotation.Status))
        {
            return null;
        }

        if (!SaQtValidity.IsExpired(quotation.ValidUntil, _dates.Today))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        quotation.Status = SaQtStatuses.Expired;
        quotation.ExpiredDate = now;
        quotation.ModifiedDate = now;
        SaDocApplicationService.TouchQtRowVersion(db, quotation);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return SaQtOperationResult.Fail(SaQtReasonCodes.ExpiredMessage, SaQtErrorKind.BusinessRule);
    }

    /// <summary>
    /// Lazy-expiry sweep. Only documents in <paramref name="qtNos"/> are considered, and only
    /// <c>NEW</c> / <c>SENT</c> revisions — an ACCEPTED quotation never auto-expires.
    /// </summary>
    private async Task<bool> ExpireLapsedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        IReadOnlyList<string> qtNos,
        CancellationToken cancellationToken)
    {
        if (qtNos.Count == 0)
        {
            return false;
        }

        var today = _dates.Today.Date;
        var lapsed = await db.SaQts
            .Where(x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.IsCurrent
                && qtNos.Contains(x.QtNo)
                && (x.Status == SaQtStatuses.New || x.Status == SaQtStatuses.Sent)
                && x.ValidUntil < today)
            .ToListAsync(cancellationToken);
        if (lapsed.Count == 0)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        foreach (var quotation in lapsed)
        {
            quotation.Status = SaQtStatuses.Expired;
            quotation.ExpiredDate = now;
            quotation.ModifiedDate = now;
            SaDocApplicationService.TouchQtRowVersion(db, quotation);
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Blocks revise/delete when the revision has already produced a Sales Order, or when any line
    /// carries converted quantity. MVP converts in full, so either signal means "in use".
    /// </summary>
    private async Task<SaQtOperationResult?> CheckRevisionUnusedAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        short custRel,
        CancellationToken cancellationToken)
    {
        var hasConvertedLine = await db.SaQtDetails.AsNoTracking()
            .AnyAsync(
                x => x.CompanyCode == companyCode
                    && x.BranchCode == branchCode
                    && x.QtNo == qtNo
                    && x.CustRel == custRel
                    && x.ConvertedQty != 0m,
                cancellationToken);
        if (hasConvertedLine)
        {
            return SaQtOperationResult.Fail(
                SaQtReasonCodes.ConvertedLineMessage,
                SaQtErrorKind.BusinessRule);
        }

        var hasSo = await _quotations.HasConvertedSalesOrderAsync(
            db, companyCode, branchCode, qtNo, custRel, cancellationToken);
        if (hasSo)
        {
            return SaQtOperationResult.Fail(
                SaQtReasonCodes.AlreadyConvertedMessage,
                SaQtErrorKind.BusinessRule);
        }

        return null;
    }

    /// <summary>
    /// Guards a keyed mutation: validates the request shape, write scope and permission.
    /// </summary>
    private async Task<(SaQtOperationResult? Gate, UserContext Context, string? QtNo)> ValidateKeyedAsync(
        SaQtKeyedRequest? request,
        string permission,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return (SaQtOperationResult.FailValidation("Request is required."), default, null);
        }

        var no = (request.QtNo ?? string.Empty).Trim();
        if (no.Length == 0)
        {
            return (SaQtOperationResult.FailValidation("Quotation number is required."), default, null);
        }

        var context = ValidateWriteContext();
        if (context.Error is not null)
        {
            return (SaQtOperationResult.Fail(context.Error), context, null);
        }

        if (!await CanAsync(permission, cancellationToken))
        {
            return (SaQtOperationResult.Fail("Not authorized.", SaQtErrorKind.Authorization), context, null);
        }

        return (null, context, no);
    }

    private static SaQtOperationResult? CheckIsCurrent(SaQt quotation, short? requestedCustRel)
    {
        if (!quotation.IsCurrent
            || string.Equals(quotation.Status, SaQtStatuses.Superseded, StringComparison.OrdinalIgnoreCase))
        {
            return SaQtOperationResult.Fail(SaQtReasonCodes.SupersededMessage, SaQtErrorKind.BusinessRule);
        }

        if (requestedCustRel is { } requested && requested != quotation.CustRel)
        {
            return SaQtOperationResult.Fail(SaQtReasonCodes.SupersededMessage, SaQtErrorKind.BusinessRule);
        }

        return null;
    }

    private static string? ResolveMutationBlockReason(string status, bool hasConverted, string? soNo)
    {
        if (hasConverted || soNo is not null)
        {
            return "already converted to a Sales Order";
        }

        return SaQtStatuses.Revisable.Contains(status) ? null : $"status is {status}";
    }

    private readonly record struct ValidityOutcome(DateTime Value, string? Error)
    {
        public static ValidityOutcome Ok(DateTime value) => new(value, null);
        public static ValidityOutcome Fail(string error) => new(default, error);
    }

    /// <summary>
    /// Resolves the offer validity limit. Explicit values are honoured; an omitted value defaults to
    /// QT date + <paramref name="defaultValidityDays"/>. A limit before the quotation date is always rejected.
    /// </summary>
    private ValidityOutcome ResolveValidUntil(DateTime qtDate, DateTime? requested, int defaultValidityDays)
    {
        var date = qtDate == default ? _dates.Today.Date : qtDate.Date;
        var validUntil = requested is { } value && value != default
            ? value.Date
            : SaQtValidity.DefaultValidUntil(date, defaultValidityDays);

        if (validUntil < date)
        {
            return ValidityOutcome.Fail("Valid until cannot be before the quotation date.");
        }

        return ValidityOutcome.Ok(validUntil);
    }

    private async Task<ValidityOutcome> ResolveValidUntilAsync(
        DateTime qtDate,
        DateTime? requested,
        string? companyCode,
        CancellationToken cancellationToken) =>
        ResolveValidUntil(
            qtDate,
            requested,
            await ResolveQuoteValidityDaysAsync(companyCode, cancellationToken));

    /// <summary>
    /// The company's quotation validity window, read from the settings registry (SALES.QUOTE_VALID_DAYS).
    ///
    /// <para>
    /// A setting that cannot be read falls back to <see cref="SaQtValidity.DefaultValidityDays"/>, so a
    /// settings problem can never stop a quotation being created — the behaviour is then exactly what it
    /// was before this setting existed. Read SERVER-SIDE: the caller can never influence it, only omit
    /// <c>ValidUntil</c> and let this fill the gap.
    /// </para>
    /// </summary>
    private async Task<int> ResolveQuoteValidityDaysAsync(
        string? companyCode,
        CancellationToken cancellationToken)
    {
        var result = await _settings.GetValueAsync(
            ErpWeb.Core.Settings.AppSettingModules.Sales,
            ErpWeb.Core.Settings.AppSettingCatalogue.SalesKeys.QuoteValidDays,
            ErpWeb.Core.Settings.AppSettingScope.Company,
            companyCode,
            branchCode: null,
            cancellationToken);

        var parsed = decimal.TryParse(
            result.Data,
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var days);

        return result.Succeeded && parsed && days > 0m
            ? (int)days
            : SaQtValidity.DefaultValidityDays;
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Line preparation (pricing is resolved by the caller; this only validates + calculates)
    // ─────────────────────────────────────────────────────────────────────────────

    private async Task<PrepareOutcome> PrepareLinesAsync(
        AppDbContext db,
        SaQtSaveRequest request,
        string companyCode,
        string branchCode,
        IReadOnlyDictionary<short, SaQtDetail>? existingByLine,
        string? priorProjId,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var custCode = (request.CustCode ?? string.Empty).Trim();
        if (custCode.Length == 0)
        {
            errors["CustCode"] = "Customer is required.";
        }

        if (request.QtDate == default)
        {
            errors["QtDate"] = "Quotation date is required.";
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

        // CustPo is optional on a quotation: the customer may raise their reference only after the
        // offer is accepted. (SaSO requires it; that is an SO-specific rule and is not copied here.)

        var currency = (request.Currency ?? string.Empty).Trim();
        if (currency.Length == 0)
        {
            errors["Currency"] = "Currency is required.";
        }

        if (errors.Count > 0 && (custCode.Length == 0 || request.QtDate == default))
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
            errors["Lines"] = "Add at least one quotation line.";
        }
        else if (requestLines.Count > short.MaxValue)
        {
            errors["Lines"] = "Too many quotation lines.";
        }

        if (requestLines.Count > 0)
        {
            var firstInclusive = requestLines[0].IsInclusive;
            if (requestLines.Any(x => x.IsInclusive != firstInclusive))
            {
                return PrepareOutcome.Fail(
                    "ST000032: All lines must use the same tax type (inclusive or exclusive).",
                    SaQtErrorKind.BusinessRule);
            }
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

        var qtDate = request.QtDate.Date;
        var rateResult = await ResolveCurrRateAsync(db, currency, qtDate, cancellationToken);
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

            var orderQty = SaQtQty.RoundQty(line.OrderQty);
            if (orderQty <= 0m)
            {
                errors[$"Lines[{index}].OrderQty"] = "Order quantity must be greater than zero.";
            }

            if (line.UnitPrice < 0m)
            {
                errors[$"Lines[{index}].UnitPrice"] = "Unit price cannot be negative.";
            }

            var stdPsize = item.StdPackSize is > 0m ? item.StdPackSize.Value : 1m;
            var stdQty = SaQtQty.RoundQty(orderQty * stdPsize);
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
                PricingSource = TruncateOptional(line.PricingSource, 40),
                PricingRef = TruncateOptional(line.PricingRef, 60),
                // Phase 4: normalised HERE so "NULL = never overridden" stays true - a line that echoes
                // the resolved price back stores nothing.
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

        // Defence in depth: the calculator must never produce a negative money value, and a
        // quotation is never allowed to carry one.
        foreach (var row in prepared)
        {
            if (row.Calc.Amount < 0m || row.Calc.NetAmount < 0m || row.Calc.TaxAmt < 0m)
            {
                return PrepareOutcome.Fail(
                    $"Line {row.RequestOrder}: the calculated amount cannot be negative.",
                    SaQtErrorKind.BusinessRule);
            }
        }

        SaInvoiceCalc.ApplyTaxAdaptiveRounding(calcStates);
        foreach (var row in prepared)
        {
            row.Calc.LocalAmount = SaInvoiceCalc.Money(row.Calc.NetAmount * rateResult.Rate);
        }

        // Phase 4: price-override governance, enforced SERVER-SIDE because the page is not the execution
        // point. A line that echoes the resolved price back is NOT an override. A quotation is where a
        // price is first offered, so this is exactly where an override needs to be governed.
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

    private static void ApplyHeaderSnapshots(SaQt quotation, SaQtSaveRequest request)
    {
        quotation.CustPo = TruncateOptional(request.CustPo, 50);
        quotation.ContactPerson = UpperSnapshot(request.ContactPerson, 100);
        quotation.Ref1 = TruncateOptional(request.Ref1, 50);
        quotation.ProjId = TruncateOptional(request.ProjId, 20);
        quotation.PayCode = TruncateOptional(request.PayCode, 20);
        quotation.TaxGrCode = TruncateOptional(request.TaxGrCode, 20);
        quotation.SalesRep = TruncateOptional(request.SalesRep, 20);
        quotation.Remarks = TruncateOptional(request.Remarks, 500);
        quotation.ShipName = UpperSnapshot(request.ShipName, 100);
        quotation.ShipAddress1 = UpperSnapshot(request.ShipAddress1, 100);
        quotation.ShipAddress2 = UpperSnapshot(request.ShipAddress2, 100);
        quotation.ShipAddress3 = UpperSnapshot(request.ShipAddress3, 100);
        quotation.ShipAddress4 = UpperSnapshot(request.ShipAddress4, 100);
        quotation.ShipCity = UpperSnapshot(request.ShipCity, 50);
        quotation.ShipState = UpperSnapshot(request.ShipState, 50);
        quotation.ShipPostalCode = UpperSnapshot(request.ShipPostalCode, 20);
        quotation.ShipCountry = UpperSnapshot(request.ShipCountry, 50);
        quotation.ShipTel = TruncateOptional(request.ShipTel, 50);
        quotation.ShipFax = TruncateOptional(request.ShipFax, 50);
        quotation.InvName = UpperSnapshot(request.InvName, 100);
        quotation.InvAddress1 = UpperSnapshot(request.InvAddress1, 100);
        quotation.InvAddress2 = UpperSnapshot(request.InvAddress2, 100);
        quotation.InvAddress3 = UpperSnapshot(request.InvAddress3, 100);
        quotation.InvAddress4 = UpperSnapshot(request.InvAddress4, 100);
        quotation.InvCity = UpperSnapshot(request.InvCity, 50);
        quotation.InvState = UpperSnapshot(request.InvState, 50);
        quotation.InvPostalCode = UpperSnapshot(request.InvPostalCode, 20);
        quotation.InvCountry = UpperSnapshot(request.InvCountry, 50);
        quotation.InvTel = TruncateOptional(request.InvTel, 50);
        quotation.InvFax = TruncateOptional(request.InvFax, 50);
    }

    /// <summary>
    /// Header fields that exist only on a quotation. Kept separate from
    /// <see cref="ApplyHeaderSnapshots"/> so it is obvious these never belong to an SO.
    /// </summary>
    private static void ApplyQtOnlyHeaderFields(SaQt quotation, SaQtSaveRequest request)
    {
        quotation.InternalRemarks = TruncateOptional(request.InternalRemarks, 500);
        quotation.ShipVia = TruncateOptional(request.ShipVia, 100);
        quotation.DeliveryTerms = TruncateOptional(request.DeliveryTerms, 200);
    }

    private static void ApplyCalculatedTotals(SaQt quotation, IReadOnlyList<PreparedLine> lines, bool decPoint)
    {
        var header = SaInvoiceCalc.CalculateHeader(lines.Select(x => x.Calc).ToList(), decPoint);
        quotation.GrossAmnt = header.GrossAmnt;
        quotation.Taxes = header.Taxes;
        quotation.TotAmnt = header.TotAmnt;
    }

    private static void AddDetails(SaQt quotation, IReadOnlyList<PreparedLine> lines)
    {
        foreach (var line in lines)
        {
            var detail = new SaQtDetail
            {
                CompanyCode = quotation.CompanyCode,
                BranchCode = quotation.BranchCode,
                QtNo = quotation.QtNo,
                Line = (short)line.RequestOrder,
                CustRel = quotation.CustRel,
                ICode = line.ICode,
                IDesc = line.IDesc,
                CustICode = line.CustICode,
                UnitPrice = line.UnitPrice,
                PricingSource = line.PricingSource,
                PricingRef = line.PricingRef,
                OriginalUnitPrice = line.OriginalUnitPrice,
                OverrideReason = line.OverrideReason,
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
                Etd = line.Etd,
                ConvertedQty = 0m
            };
            SaQtQty.SetOrderQty(detail, line.OrderQty);
            quotation.Details.Add(detail);
        }
    }

    private static void ApplyPreparedLine(SaQtDetail detail, PreparedLine line, short custRel)
    {
        detail.CustRel = custRel;
        detail.ICode = line.ICode;
        detail.IDesc = line.IDesc;
        detail.CustICode = line.CustICode;
        detail.UnitPrice = line.UnitPrice;
        detail.PricingSource = line.PricingSource;
        detail.PricingRef = line.PricingRef;
        detail.OriginalUnitPrice = line.OriginalUnitPrice;
        detail.OverrideReason = line.OverrideReason;
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
        SaQtQty.SetOrderQty(detail, line.OrderQty);
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Mapping
    // ─────────────────────────────────────────────────────────────────────────────

    private static SaQtDocument MapDocument(
        SaQt quotation,
        IReadOnlyList<SaQt>? revisions,
        string? convertedSoNo,
        DateTime today) =>
        new()
        {
            QtNo = quotation.QtNo,
            CustRel = quotation.CustRel,
            IsCurrent = quotation.IsCurrent,
            LastCustRel = quotation.LastCustRel,
            RevisionReason = quotation.RevisionReason,
            QtDate = quotation.QtDate,
            ValidUntil = quotation.ValidUntil,
            Status = quotation.Status,
            ConversionStatus = string.IsNullOrWhiteSpace(quotation.ConversionStatus)
                ? SaQtConversionStatuses.None
                : quotation.ConversionStatus,
            ClosedReason = quotation.ClosedReason,
            ClosedDate = quotation.ClosedDate,
            ClosedBy = quotation.ClosedBy,
            SentDate = quotation.SentDate,
            SentBy = quotation.SentBy,
            AcceptedDate = quotation.AcceptedDate,
            AcceptedBy = quotation.AcceptedBy,
            LostDate = quotation.LostDate,
            LostBy = quotation.LostBy,
            LostReason = quotation.LostReason,
            ExpiredDate = quotation.ExpiredDate,
            CustCode = quotation.CustCode,
            CustName = quotation.CustName,
            CustPo = quotation.CustPo,
            ContactPerson = quotation.ContactPerson,
            Ref1 = quotation.Ref1,
            ProjId = quotation.ProjId,
            Prefix = quotation.Prefix,
            Currency = quotation.Currency,
            CurrRate = quotation.CurrRate,
            PayCode = quotation.PayCode,
            TaxGrCode = quotation.TaxGrCode,
            SalesRep = quotation.SalesRep,
            Remarks = quotation.Remarks,
            InternalRemarks = quotation.InternalRemarks,
            ShipVia = quotation.ShipVia,
            DeliveryTerms = quotation.DeliveryTerms,
            ShipName = quotation.ShipName,
            ShipAddress1 = quotation.ShipAddress1,
            ShipAddress2 = quotation.ShipAddress2,
            ShipAddress3 = quotation.ShipAddress3,
            ShipAddress4 = quotation.ShipAddress4,
            ShipCity = quotation.ShipCity,
            ShipState = quotation.ShipState,
            ShipPostalCode = quotation.ShipPostalCode,
            ShipCountry = quotation.ShipCountry,
            ShipTel = quotation.ShipTel,
            ShipFax = quotation.ShipFax,
            InvName = quotation.InvName,
            InvAddress1 = quotation.InvAddress1,
            InvAddress2 = quotation.InvAddress2,
            InvAddress3 = quotation.InvAddress3,
            InvAddress4 = quotation.InvAddress4,
            InvCity = quotation.InvCity,
            InvState = quotation.InvState,
            InvPostalCode = quotation.InvPostalCode,
            InvCountry = quotation.InvCountry,
            InvTel = quotation.InvTel,
            InvFax = quotation.InvFax,
            GrossAmnt = quotation.GrossAmnt,
            Taxes = quotation.Taxes,
            TotAmnt = quotation.TotAmnt,
            CreatedBy = quotation.CreatedBy,
            CreatedDate = quotation.CreatedDate,
            ModifiedBy = quotation.ModifiedBy,
            ModifiedDate = quotation.ModifiedDate,
            RowVersion = quotation.RowVersion ?? [],
            ConvertedSoNo = convertedSoNo,
            IsExpired = SaQtValidity.IsExpired(quotation.ValidUntil, today),
            Lines = quotation.Details.OrderBy(x => x.Line).Select(MapLine).ToList(),
            Revisions = (revisions ?? [])
                .OrderByDescending(x => x.CustRel)
                .Select(x => new SaQtRevisionHistoryRow
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

    private static SaQtDocument MapRevisionDraft(SaQtDocument current, short nextCustRel) =>
        new()
        {
            QtNo = current.QtNo,
            CustRel = nextCustRel,
            IsCurrent = true,
            LastCustRel = nextCustRel,
            RevisionReason = null,
            QtDate = current.QtDate,
            ValidUntil = current.ValidUntil,
            // A revision always restarts as NEW: acceptance never carries forward.
            Status = SaQtStatuses.New,
            ConversionStatus = SaQtConversionStatuses.None,
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
            InternalRemarks = current.InternalRemarks,
            ShipVia = current.ShipVia,
            DeliveryTerms = current.DeliveryTerms,
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
            RowVersion = current.RowVersion,
            Lines = current.Lines
                .OrderBy(x => x.Line)
                .Select(x => new SaQtLineDto
                {
                    Line = x.Line,
                    CustRel = nextCustRel,
                    ICode = x.ICode,
                    IDesc = x.IDesc,
                    CustICode = x.CustICode,
                    OrderQty = x.OrderQty,
                    ConvertedQty = 0m,
                    RemainingQty = x.OrderQty,
                    StdQty = x.StdQty,
                    StdPsize = x.StdPsize,
                    SellingUom = x.SellingUom,
                    StdUom = x.StdUom,
                    Warehouse = x.Warehouse,
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
                    Classification = x.Classification,
                    Remarks = x.Remarks,
                    DeliveryDate = x.DeliveryDate,
                    Eta = x.Eta,
                    Etd = x.Etd
                })
                .ToList(),
            Revisions = current.Revisions
        };

    private static SaQtLineDto MapLine(SaQtDetail detail) =>
        new()
        {
            Line = detail.Line,
            CustRel = detail.CustRel,
            ICode = detail.ICode ?? string.Empty,
            IDesc = detail.IDesc,
            CustICode = detail.CustICode,
            OrderQty = detail.OrderQty,
            ConvertedQty = detail.ConvertedQty,
            RemainingQty = SaQtQty.Remaining(detail),
            StdQty = detail.StdQty,
            StdPsize = detail.StdPsize,
            SellingUom = detail.SellingUom,
            StdUom = detail.StdUom,
            Warehouse = detail.Warehouse,
            UnitPrice = detail.UnitPrice,
            PricingSource = detail.PricingSource,
            PricingRef = detail.PricingRef,
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

    // ─────────────────────────────────────────────────────────────────────────────
    // Infrastructure helpers
    // ─────────────────────────────────────────────────────────────────────────────

    private static async Task AssertOneCurrentAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken)
    {
        var currentCount = await db.SaQts.AsNoTracking().CountAsync(
            x => x.CompanyCode == companyCode
                && x.BranchCode == branchCode
                && x.QtNo == qtNo
                && x.IsCurrent,
            cancellationToken);
        if (currentCount != 1)
        {
            throw new InvalidOperationException($"Quotation {qtNo} must have exactly one current revision.");
        }
    }

    private static async Task AssertOneCurrentIfExistsAsync(
        AppDbContext db,
        string companyCode,
        string branchCode,
        string qtNo,
        CancellationToken cancellationToken)
    {
        var anyRevision = await db.SaQts.AsNoTracking().AnyAsync(
            x => x.CompanyCode == companyCode && x.BranchCode == branchCode && x.QtNo == qtNo,
            cancellationToken);
        if (anyRevision)
        {
            await AssertOneCurrentAsync(db, companyCode, branchCode, qtNo, cancellationToken);
        }
    }

    private async Task<(string? Error, decimal Rate)> ResolveCurrRateAsync(
        AppDbContext db,
        string currency,
        DateTime qtDate,
        CancellationToken cancellationToken)
    {
        var isHome = string.Equals(currency, SaInvoiceCalc.HomeCurrency, StringComparison.OrdinalIgnoreCase);
        var rate = await db.SaCurrRates.AsNoTracking()
            .Where(x => x.CurrCode == currency && x.Status && x.StartDate <= qtDate && x.EndDate >= qtDate)
            .OrderByDescending(x => x.StartDate)
            .Select(x => (double?)x.HomeCurPerUnit)
            .FirstOrDefaultAsync(cancellationToken);

        if (rate is null)
        {
            if (isHome)
            {
                return (null, 1m);
            }

            return ($"No currency rate for {currency} on {qtDate:yyyy-MM-dd}.", 0m);
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
        SaQtLineRequest line,
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

    private static List<SaQtKeyedRequest> NormalizeKeyedItems(IReadOnlyList<SaQtKeyedRequest>? items) =>
        (items ?? [])
            .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.QtNo))
            .Select(x => new SaQtKeyedRequest
            {
                QtNo = x.QtNo.Trim(),
                CustRel = x.CustRel,
                RowVersion = x.RowVersion ?? []
            })
            .GroupBy(x => x.QtNo, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

    private Task<bool> CanAsync(string permission, CancellationToken cancellationToken) =>
        _accessRights.CanAsync(MenuCodes.SalesQuotation, permission, cancellationToken);

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
        public SaQtErrorKind Kind { get; init; }
        public IReadOnlyDictionary<string, string> Errors { get; init; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public SaCust? Customer { get; init; }
        public string? Currency { get; init; }
        public decimal CurrRate { get; init; }
        public List<PreparedLine>? Lines { get; init; }

        public static PrepareOutcome Ok(SaCust customer, string currency, decimal rate, List<PreparedLine> lines) =>
            new() { Customer = customer, Currency = currency, CurrRate = rate, Lines = lines };

        public static PrepareOutcome Validation(string message, IReadOnlyDictionary<string, string> errors) =>
            new() { Error = message, Kind = SaQtErrorKind.Validation, Errors = errors };

        public static PrepareOutcome Fail(string message, SaQtErrorKind kind) =>
            new() { Error = message, Kind = kind };

        public SaQtOperationResult ToFail() =>
            Kind == SaQtErrorKind.Validation
                ? SaQtOperationResult.FailValidation(ValidationMessageFormat.ResolveServiceMessage(Errors, Error), Errors)
                : SaQtOperationResult.Fail(Error ?? "Unable to save the quotation.", Kind);
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
