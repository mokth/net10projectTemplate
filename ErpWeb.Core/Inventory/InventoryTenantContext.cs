using ErpWeb.Core.Services;
using ErpWeb.Model.Entities.Inventory;

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

public sealed class InventoryTenantContext : IInventoryTenantContext
{
    private const int MaxCompanyLength = 5;
    private const int MaxBranchLength = 5;
    private const int MaxLocationLength = 10;
    private const int MaxUserIdLength = 10;

    private readonly ICurrentUserService _currentUser;

    public InventoryTenantContext(ICurrentUserService currentUser)
    {
        _currentUser = currentUser;
    }

    public InventoryTenantScope? TryCompanyScope() =>
        BuildScope(requireBranch: false, requireLocation: false);

    public InventoryTenantScope? TryBranchScope() =>
        BuildScope(requireBranch: true, requireLocation: false);

    public InventoryTenantScope? TryWriteScope() =>
        BuildScope(requireBranch: true, requireLocation: true);

    private InventoryTenantScope? BuildScope(bool requireBranch, bool requireLocation)
    {
        if (!_currentUser.IsAuthenticated ||
            string.IsNullOrWhiteSpace(_currentUser.SubjectUid))
        {
            return null;
        }

        var company = NormalizeClaim(_currentUser.CompanyCode, MaxCompanyLength);
        var userId = NormalizeClaim(_currentUser.UserId, MaxUserIdLength);
        if (company is null || userId is null)
        {
            return null;
        }

        var branch = NormalizeClaim(_currentUser.BranchCode, MaxBranchLength);
        var location = NormalizeClaim(_currentUser.LocationCode, MaxLocationLength);

        if (requireBranch && branch is null)
        {
            return null;
        }

        if (requireLocation && location is null)
        {
            return null;
        }

        return new InventoryTenantScope
        {
            CompanyCode = company,
            BranchCode = branch,
            LocationCode = location,
            UserId = userId
        };
    }

    private static string? NormalizeClaim(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > maxLength ? null : trimmed;
    }
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
}
