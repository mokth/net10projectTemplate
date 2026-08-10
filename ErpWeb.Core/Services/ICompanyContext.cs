namespace ErpWeb.Core.Services;

/// <summary>
/// Resolves authenticated cookie claims to company/branch IDs for inventory and scoped services.
/// Call <see cref="ResolveAsync"/> before reading ID properties.
/// </summary>
public interface ICompanyContext
{
    Task ResolveAsync(CancellationToken cancellationToken = default);

    bool IsResolved { get; }

    int CompanyId { get; }
    string CompanyCode { get; }
    long BranchId { get; }
    string BranchCode { get; }

    /// <summary>Legacy userlogin claim. NOT an inventory WarehouseLocation.</summary>
    string? LegacyLocationCode { get; }

    string TimeZoneId { get; }
    string BaseCurrencyCode { get; }
}
