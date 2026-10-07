using ErpWeb.Core.Inventory;
using ErpWeb.Core.Lookups;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Model.Data;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Purchase;

public sealed class PoPurchasingItemLookupService : IPoPurchasingItemLookupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly IAccessRightService _accessRights;

    public PoPurchasingItemLookupService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        IAccessRightService accessRights)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _accessRights = accessRights;
    }

    public async Task<LargeLookupResolveResult<PoPurchasingItemLookupRow>> ResolveAsync(
        string iCode,
        string? menuCode = null,
        bool includeIndirect = true,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return LargeLookupResolveResult<PoPurchasingItemLookupRow>.Fail("Invalid company context.");
        }

        var code = (iCode ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return LargeLookupResolveResult<PoPurchasingItemLookupRow>.Fail("Item code is required.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var stock = await db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive && x.ICode == code)
            .Select(MapStockExpression)
            .FirstOrDefaultAsync(cancellationToken);

        PoPurchasingItemLookupRow? indirect = null;
        if (includeIndirect)
        {
            indirect = await db.PoPurItems.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode && x.ICode == code)
                .Select(MapIndirectExpression)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (stock is not null && indirect is not null)
        {
            return LargeLookupResolveResult<PoPurchasingItemLookupRow>.Fail(
                $"Item '{code}' exists as both Stock and Indirect. Use search to select the correct source.",
                ambiguous: true);
        }

        var row = stock ?? indirect;
        if (row is null)
        {
            return LargeLookupResolveResult<PoPurchasingItemLookupRow>.Fail($"Item '{code}' was not found.");
        }

        return LargeLookupResolveResult<PoPurchasingItemLookupRow>.Ok(
            await ApplyCostPermissionAsync(row, menuCode, cancellationToken));
    }

    public async Task<LargeLookupPage<PoPurchasingItemLookupRow>> SearchPagedAsync(
        LargeLookupSearchRequest request,
        string? menuCode = null,
        bool includeIndirect = true,
        CancellationToken cancellationToken = default)
    {
        var scope = _tenant.TryCompanyScope();
        if (scope is null)
        {
            return LargeLookupPage<PoPurchasingItemLookupRow>.Fail("Invalid company context.");
        }

        request ??= new LargeLookupSearchRequest();
        var skip = request.NormalizedSkip;
        var take = request.NormalizedTake;
        var term = string.IsNullOrWhiteSpace(request.SearchText) ? null : request.SearchText.Trim();

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var stockQuery = db.IvStockMasters.AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode && x.IsActive);
        if (term is not null)
        {
            stockQuery = stockQuery.Where(x =>
                x.ICode.Contains(term)
                || (x.IDesc != null && x.IDesc.Contains(term))
                || (x.Barcode != null && x.Barcode.Contains(term)));
        }

        var stockProjected = stockQuery.Select(MapStockExpression);

        IQueryable<PoPurchasingItemLookupRow> combined;
        if (includeIndirect)
        {
            var indirectQuery = db.PoPurItems.AsNoTracking()
                .Where(x => x.CompanyCode == scope.CompanyCode);
            if (term is not null)
            {
                indirectQuery = indirectQuery.Where(x =>
                    x.ICode.Contains(term)
                    || (x.IDesc != null && x.IDesc.Contains(term))
                    || (x.Vendor != null && x.Vendor.Contains(term))
                    || (x.VendName != null && x.VendName.Contains(term)));
            }

            var indirectProjected = indirectQuery.Select(MapIndirectExpression);
            combined = stockProjected.Concat(indirectProjected);
        }
        else
        {
            combined = stockProjected;
        }

        var total = await combined.CountAsync(cancellationToken);
        var rows = await combined
            .OrderBy(x => x.ICode)
            .ThenBy(x => x.IsIndirect)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var canViewCost = await CanViewCostAsync(menuCode, cancellationToken);
        if (!canViewCost)
        {
            rows = rows.Select(x => x with { UnitPrice = null }).ToList();
        }

        return LargeLookupPage<PoPurchasingItemLookupRow>.Ok(rows, total);
    }

    private async Task<PoPurchasingItemLookupRow> ApplyCostPermissionAsync(
        PoPurchasingItemLookupRow row,
        string? menuCode,
        CancellationToken cancellationToken)
    {
        if (await CanViewCostAsync(menuCode, cancellationToken))
        {
            return row;
        }

        return row with { UnitPrice = null };
    }

    private async Task<bool> CanViewCostAsync(string? menuCode, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(menuCode))
        {
            return await _accessRights.CanAsync(menuCode.Trim(), PermissionCodes.ViewCost, cancellationToken);
        }

        return await _accessRights.CanAsync(MenuCodes.PurchaseRequisition, PermissionCodes.ViewCost, cancellationToken)
            || await _accessRights.CanAsync(MenuCodes.PurchaseOrder, PermissionCodes.ViewCost, cancellationToken);
    }

    // Expression-bodied projections so Concat/OrderBy stay server-side.
    private static System.Linq.Expressions.Expression<Func<Model.Entities.Inventory.IvStockMaster, PoPurchasingItemLookupRow>>
        MapStockExpression =>
        x => new PoPurchasingItemLookupRow
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            IsIndirect = false,
            IType = x.IType,
            PurchaseUom = x.PurUom,
            StdUom = x.StdUom,
            PackSz = x.PurStdPackSize ?? x.StdPackSize ?? 1m,
            StdPackSize = x.StdPackSize,
            PurStdPackSize = x.PurStdPackSize,
            UnitPrice = x.PurchasePrice,
            TaxGroup = x.PurchaseTaxGroup ?? x.TaxGroup,
            PurchaseTaxGroup = x.PurchaseTaxGroup,
            DefWarehouse = x.DefWarehouse,
            DefLocation = x.DefLocation,
            Category = null,
            VendorCd = null,
            VendNm = null,
            Moq = 0m,
            StockControl = x.StockControl,
            LotControl = x.LotControl,
            Classification = x.Classification,
            PurchaseGlCode = x.PurchaseGlCode
        };

    private static System.Linq.Expressions.Expression<Func<Model.Entities.Purchase.PoPurItem, PoPurchasingItemLookupRow>>
        MapIndirectExpression =>
        x => new PoPurchasingItemLookupRow
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            IsIndirect = true,
            IType = null,
            PurchaseUom = x.PurUom,
            StdUom = x.PurUom,
            PackSz = 1m,
            StdPackSize = 1m,
            PurStdPackSize = 1m,
            UnitPrice = x.UnitPrice,
            TaxGroup = null,
            PurchaseTaxGroup = null,
            DefWarehouse = null,
            DefLocation = null,
            Category = x.Category,
            VendorCd = x.Vendor,
            VendNm = x.VendName,
            Moq = x.Moq,
            StockControl = false,
            LotControl = false,
            Classification = null,
            PurchaseGlCode = x.PurchaseGlCode
        };
}
