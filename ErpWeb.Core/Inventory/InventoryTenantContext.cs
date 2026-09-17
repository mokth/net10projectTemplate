using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;

namespace ErpWeb.Core.Inventory;

public interface IInventoryTenantContext
{
    InventoryTenantScope? TryCompanyScope();

    InventoryTenantScope? TryBranchScope();

    InventoryTenantScope? TryWriteScope();
}

public sealed class InventoryTenantScope
{
    public required string CompanyCode { get; init; }
    public string? BranchCode { get; init; }
    public string? LocationCode { get; init; }
    public required string UserId { get; init; }
}

/// <summary>
/// Inventory-facing tenant context. Delegates to the shared <see cref="ITenantScopeContext"/> so there
/// is exactly ONE implementation of claim normalisation; this type exists only to keep the
/// <see cref="InventoryTenantScope"/> shape stable for its existing callers.
///
/// <para>
/// Do NOT add normalisation logic here. Change <see cref="TenantScopeContext"/> instead, or the two will
/// drift and one module will accept a company code that another rejects.
/// </para>
/// </summary>
public sealed class InventoryTenantContext : IInventoryTenantContext
{
    private readonly ITenantScopeContext _scope;

    public InventoryTenantContext(ITenantScopeContext scope)
    {
        _scope = scope;
    }

    public InventoryTenantScope? TryCompanyScope() => Map(_scope.TryCompanyScope());

    public InventoryTenantScope? TryBranchScope() => Map(_scope.TryBranchScope());

    public InventoryTenantScope? TryWriteScope() => Map(_scope.TryWriteScope());

    private static InventoryTenantScope? Map(TenantScope? scope) =>
        scope is null
            ? null
            : new InventoryTenantScope
            {
                CompanyCode = scope.CompanyCode,
                BranchCode = scope.BranchCode,
                LocationCode = scope.LocationCode,
                UserId = scope.UserId
            };
}

internal static class InventoryLeftoverSite
{
    public static void Apply(IvWarehouse entity, InventoryTenantScope writeScope)
    {
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvLocation entity, InventoryTenantScope writeScope)
    {
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvStatus entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(MsUom entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvType entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvClass entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvSubClass entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvStockMaster entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaCust entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaCustType entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaCustGroup entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvAreaCode entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaCurrency entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaPaymentTerm entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaSalesRep entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaTaxGroup entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaDisGroup entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvCustPriceGroup entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(IvCustPrice entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaItemCust entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaDisGroupItem entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaCustSubGroup entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaShipVia entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaSOType entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaComment entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaShippingLeadTime entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(SaLMW entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode;
        entity.LocationCode = writeScope.LocationCode;
    }

    public static void Apply(PoSupplier entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode!;
    }

    public static void Apply(PoSupplierAdd entity, InventoryTenantScope writeScope)
    {
        entity.BranchCode = writeScope.BranchCode!;
    }
}
