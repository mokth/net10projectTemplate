using ErpWeb.Core.Lookups;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Inventory;

public sealed class IvCodeLookupRow
{
    public string Code { get; init; } = string.Empty;
    public string? Desc { get; init; }
    public decimal? Rate { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(Desc) ? Code : $"{Code} — {Desc}";
}

public sealed class IvStockMasterLookupRow
{
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string MfgType { get; init; } = "BUY";
    public string? IType { get; init; }
    public string? IClassCode { get; init; }
    public string? StdUom { get; init; }
    public string? SellingUom { get; init; }
    public string? PurUom { get; init; }
    public decimal? StdPackSize { get; init; }
    public decimal? PurStdPackSize { get; init; }
    public string? DefWarehouse { get; init; }
    public string? DefLocation { get; init; }
    public bool LotControl { get; init; }
    public bool StockControl { get; init; }
    public decimal? SellingPrice { get; init; }
    public decimal? PurchasePrice { get; init; }
    public string? TaxGroup { get; init; }
    public string? PurchaseTaxGroup { get; init; }
    public string? Classification { get; init; }
    public string? SellingGlCode { get; init; }
    public string? PurchaseGlCode { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class IvStockMasterSearchRequest
{
    public string? ICode { get; set; }
    public string? IDesc { get; set; }
    public string? IType { get; set; }
    public string? IClassCode { get; set; }
    public bool IsActive { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 200;
}

public sealed class IvBalLocLookupRow
{
    public int Id { get; init; }
    public string ICode { get; init; } = string.Empty;
    public string? IDesc { get; init; }
    public string WhCode { get; init; } = string.Empty;
    public string LocCode { get; init; } = string.Empty;
    public string LotNo { get; init; } = string.Empty;
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public string IStatus { get; init; } = string.Empty;
    public DateTime? ExpiryDate { get; init; }
    public DateTime? TransDate { get; init; }
    public string? IClassCode { get; init; }
    public bool LotControl { get; init; }
    public decimal? PurchasePrice { get; init; }
    public int? LotId { get; init; }
    public string DisplayText => string.IsNullOrWhiteSpace(IDesc) ? ICode : $"{ICode} — {IDesc}";
}

public sealed class IvOnHandSearchRequest
{
    public string? ICode { get; set; }
    public string? SearchText { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
    /// <summary>When set, only balances with TransDate on or before this date are returned (INV-03).</summary>
    public DateTime? AsOfDate { get; set; }
}

public sealed class IvItemResolveResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public IvStockMasterLookupRow? Item { get; init; }

    public static IvItemResolveResult Ok(IvStockMasterLookupRow item) =>
        new() { Succeeded = true, Item = item };

    public static IvItemResolveResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvOnHandSearchResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<IvBalLocLookupRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }

    public static IvOnHandSearchResult Ok(IReadOnlyList<IvBalLocLookupRow> rows, int totalCount) =>
        new() { Succeeded = true, Rows = rows, TotalCount = totalCount };

    public static IvOnHandSearchResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public sealed class IvInventoryLookupResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<IvStockMasterLookupRow> Items { get; init; } = [];
    public IReadOnlyList<IvCodeLookupRow> Rows { get; init; } = [];

    public static IvInventoryLookupResult OkItems(IReadOnlyList<IvStockMasterLookupRow> items) =>
        new() { Succeeded = true, Items = items };

    public static IvInventoryLookupResult OkRows(IReadOnlyList<IvCodeLookupRow> rows) =>
        new() { Succeeded = true, Rows = rows };

    public static IvInventoryLookupResult Fail(string message) =>
        new() { Succeeded = false, ErrorMessage = message };
}

public interface IIvInventoryLookupService
{
    Task<IvInventoryLookupResult> SearchStockMastersAsync(
        IvStockMasterSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-paged active item search for smart lookup popups. Never returns an unbounded set.
    /// </summary>
    Task<LargeLookupPage<IvStockMasterLookupRow>> SearchStockMastersPagedAsync(
        LargeLookupSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IvOnHandSearchResult> SearchOnHandAsync(
        IvOnHandSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IvItemResolveResult> ResolveItemAsync(
        string iCodeOrBarcode,
        CancellationToken cancellationToken = default);

    Task<IvOnHandSearchResult> GetOnHandByIdAsync(
        int balLocId,
        CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveWarehousesAsync(CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveLocationsAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveClassesAsync(CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveTypesAsync(CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListClassificationsAsync(CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveSubClassesAsync(
        string iClassCode,
        CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveUomsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// LHDN UNECE unit-of-measure codes from the global <c>MsLHDNUOM</c> table. Ungated (like the other
    /// inventory lookup lists): the UOM master page's picker must work for a user who can edit UOMs but
    /// does not carry an inventory transaction menu. Blank codes are excluded and duplicates collapsed.
    /// </summary>
    Task<IvInventoryLookupResult> ListLhdnUomsAsync(CancellationToken cancellationToken = default);

    Task<IvInventoryLookupResult> ListActiveStatusesAsync(CancellationToken cancellationToken = default);

    Task<bool> LotExistsAsync(
        string iCode,
        string lotNo,
        CancellationToken cancellationToken = default);
}

public sealed class IvInventoryLookupService : IIvInventoryLookupService
{
    private readonly ICurrentUserService _currentUser;
    private readonly IIvStockMasterRepository _stockMasters;
    private readonly IIvStockCommonRepository _common;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public IvInventoryLookupService(
        ICurrentUserService currentUser,
        IIvStockMasterRepository stockMasters,
        IIvStockCommonRepository common,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _currentUser = currentUser;
        _stockMasters = stockMasters;
        _common = common;
        _dbFactory = dbFactory;
    }

    public async Task<IvInventoryLookupResult> SearchStockMastersAsync(
        IvStockMasterSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        request ??= new IvStockMasterSearchRequest();
        IReadOnlyList<Model.Entities.Inventory.IvStockMaster> rows;
        if (string.IsNullOrWhiteSpace(request.ICode) &&
            string.IsNullOrWhiteSpace(request.IDesc) &&
            string.IsNullOrWhiteSpace(request.IType) &&
            string.IsNullOrWhiteSpace(request.IClassCode))
        {
            rows = await _stockMasters.ListActiveForLookupAsync(ctx.CompanyCode!, cancellationToken);
        }
        else
        {
            rows = await _stockMasters.SearchActiveAsync(
                ctx.CompanyCode!,
                request.ICode,
                request.IDesc,
                request.IType,
                request.IClassCode,
                request.Page,
                request.PageSize,
                cancellationToken);
        }

        var items = rows.Select(MapStock).ToList();

        return IvInventoryLookupResult.OkItems(items);
    }

    public async Task<LargeLookupPage<IvStockMasterLookupRow>> SearchStockMastersPagedAsync(
        LargeLookupSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return LargeLookupPage<IvStockMasterLookupRow>.Fail(ctx.Error);
        }

        request ??= new LargeLookupSearchRequest();
        var (rows, total) = await _stockMasters.SearchPagedAsync(
            ctx.CompanyCode!,
            new StockMasterSearchArgs(
                SearchText: request.SearchText,
                IsActive: true,
                IClassCode: null,
                ISubClassCode: null,
                IType: null,
                DefWarehouse: null,
                Brand: null,
                SortField: nameof(Model.Entities.Inventory.IvStockMaster.ICode),
                SortDescending: false,
                Skip: request.NormalizedSkip,
                Take: request.NormalizedTake),
            cancellationToken);

        return LargeLookupPage<IvStockMasterLookupRow>.Ok(
            rows.Select(MapStock).ToList(),
            total);
    }

    public async Task<IvOnHandSearchResult> SearchOnHandAsync(
        IvOnHandSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvOnHandSearchResult.Fail(ctx.Error);
        }

        request ??= new IvOnHandSearchRequest();
        var (rows, total) = await _common.SearchOnHandPagedAsync(
            ctx.CompanyCode!,
            ctx.BranchCode!,
            request.ICode,
            request.SearchText,
            request.Skip,
            request.Take,
            request.AsOfDate,
            cancellationToken);

        return IvOnHandSearchResult.Ok(
            rows.Select(MapOnHand).ToList(),
            total);
    }

    public async Task<IvItemResolveResult> ResolveItemAsync(
        string iCodeOrBarcode,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvItemResolveResult.Fail(ctx.Error);
        }

        var term = (iCodeOrBarcode ?? string.Empty).Trim();
        if (term.Length == 0)
        {
            return IvItemResolveResult.Fail("Item code or barcode is required.");
        }

        var byCode = await _stockMasters.GetByCodeAsync(ctx.CompanyCode!, term, cancellationToken);
        if (byCode is not null && byCode.IsActive)
        {
            return IvItemResolveResult.Ok(MapStock(byCode));
        }

        var byBarcode = await _stockMasters.GetByBarcodeAsync(ctx.CompanyCode!, term, cancellationToken);
        if (byBarcode.Count == 0)
        {
            return IvItemResolveResult.Fail($"Item '{term}' was not found.");
        }

        if (byBarcode.Count > 1)
        {
            return IvItemResolveResult.Fail(
                $"Barcode '{term}' matches multiple items. Select the correct item.");
        }

        return IvItemResolveResult.Ok(MapStock(byBarcode[0]));
    }

    public async Task<IvOnHandSearchResult> GetOnHandByIdAsync(
        int balLocId,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvOnHandSearchResult.Fail(ctx.Error);
        }

        var row = await _common.GetOnHandByIdAsync(
            ctx.CompanyCode!,
            ctx.BranchCode!,
            balLocId,
            cancellationToken);
        if (row is null)
        {
            return IvOnHandSearchResult.Fail("On-hand balance was not found.");
        }

        return IvOnHandSearchResult.Ok([MapOnHand(row)], 1);
    }

    public async Task<IvInventoryLookupResult> ListActiveWarehousesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveWarehousesAsync(
            ctx.CompanyCode!,
            ctx.BranchCode!,
            cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.WarehouseCode,
            Desc = x.WarehouseDesc
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListActiveLocationsAsync(
        string warehouseCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveLocationsAsync(
            ctx.CompanyCode!,
            ctx.BranchCode!,
            warehouseCode ?? string.Empty,
            cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.LocCode,
            Desc = x.LocDesc
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListActiveClassesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveClassesAsync(ctx.CompanyCode!, cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.IClassCode,
            Desc = x.IDesc
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListActiveTypesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveTypesAsync(ctx.CompanyCode!, cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.TypeCode,
            Desc = x.TypeName ?? x.TypeDesc
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListClassificationsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListClassificationsAsync(cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.Code,
            Desc = x.Description
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListActiveSubClassesAsync(
        string iClassCode,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveSubClassesAsync(
            ctx.CompanyCode!,
            iClassCode ?? string.Empty,
            cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.ISubClassCode,
            Desc = x.ISubClassName
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListActiveUomsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveUomsAsync(ctx.CompanyCode!, cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.UomCode,
            Desc = x.UomDesc
        }).ToList());
    }

    public async Task<IvInventoryLookupResult> ListLhdnUomsAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListLhdnUomsAsync(cancellationToken);

        // The table has no unique constraint on Code, so collapse any duplicates before they reach a
        // picker (a duplicated option would look like a bug to the user).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<IvCodeLookupRow>();
        foreach (var row in rows)
        {
            var code = (row.Code ?? string.Empty).Trim();
            if (code.Length == 0 || !seen.Add(code))
            {
                continue;
            }

            options.Add(new IvCodeLookupRow
            {
                Code = code,
                Desc = row.Measurement
            });
        }

        return IvInventoryLookupResult.OkRows(options);
    }

    public async Task<IvInventoryLookupResult> ListActiveStatusesAsync(
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return IvInventoryLookupResult.Fail(ctx.Error);
        }

        var rows = await _common.ListActiveStatusesAsync(ctx.CompanyCode!, cancellationToken);
        return IvInventoryLookupResult.OkRows(rows.Select(x => new IvCodeLookupRow
        {
            Code = x.IStatus,
            Desc = x.StatusDesc
        }).ToList());
    }

    public async Task<bool> LotExistsAsync(
        string iCode,
        string lotNo,
        CancellationToken cancellationToken = default)
    {
        var ctx = Authorize();
        if (ctx.Error is not null)
        {
            return false;
        }

        var code = (iCode ?? string.Empty).Trim();
        var lot = (lotNo ?? string.Empty).Trim();
        if (code.Length == 0 || lot.Length == 0)
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.IvLots.AsNoTracking().AnyAsync(
            x => x.CompanyCode == ctx.CompanyCode
                 && x.ICode == code
                 && x.LotNo == lot,
            cancellationToken);
    }

    private (string? CompanyCode, string? BranchCode, string? Error) Authorize()
    {
        if (!_currentUser.IsAuthenticated)
        {
            return (null, null, "Not authorized.");
        }

        var company = _currentUser.CompanyCode?.Trim();
        var branch = _currentUser.BranchCode?.Trim();
        if (string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(branch))
        {
            return (null, null, "Invalid company context.");
        }

        return (company, branch, null);
    }

    private static IvStockMasterLookupRow MapStock(Model.Entities.Inventory.IvStockMaster x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            MfgType = string.IsNullOrWhiteSpace(x.MfgType) ? "BUY" : x.MfgType,
            IType = x.IType,
            IClassCode = x.IClassCode,
            StdUom = x.StdUom,
            SellingUom = x.SellingUom,
            PurUom = x.PurUom,
            StdPackSize = x.StdPackSize,
            PurStdPackSize = x.PurStdPackSize,
            DefWarehouse = x.DefWarehouse,
            DefLocation = x.DefLocation,
            LotControl = x.LotControl,
            StockControl = x.StockControl,
            SellingPrice = x.SellingPrice,
            PurchasePrice = x.PurchasePrice,
            TaxGroup = x.TaxGroup,
            PurchaseTaxGroup = x.PurchaseTaxGroup,
            Classification = x.Classification,
            SellingGlCode = x.SellingGlCode,
            PurchaseGlCode = x.PurchaseGlCode
        };

    private static IvBalLocLookupRow MapOnHand(IvOnHandBalanceRow x) =>
        new()
        {
            Id = x.Id,
            ICode = x.ICode,
            IDesc = x.IDesc,
            WhCode = x.WhCode,
            LocCode = x.LocCode,
            LotNo = x.LotNo,
            StdQty = x.StdQty,
            StdUom = x.StdUom,
            IStatus = x.IStatus,
            ExpiryDate = x.ExpiryDate,
            TransDate = x.TransDate,
            IClassCode = x.IClassCode,
            LotControl = x.LotControl,
            PurchasePrice = x.PurchasePrice,
            LotId = x.LotId
        };
}
