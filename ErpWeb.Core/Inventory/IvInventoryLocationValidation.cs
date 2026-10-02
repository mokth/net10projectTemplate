using ErpWeb.Model.Data;
using ErpWeb.Model.Repositories.Inventory;

namespace ErpWeb.Core.Inventory;

/// <summary>
/// Validates a transaction's destination bin against the current warehouse master.
/// The tenant/site LocationCode stored on a batch is deliberately not involved here.
/// </summary>
internal static class IvInventoryLocationValidation
{
    internal static async Task<(string Location, string? Error)> ValidateDestinationAsync(
        AppDbContext db,
        IIvStockCommonRepository common,
        string companyCode,
        string branchCode,
        string warehouseCode,
        string? requestedLocation,
        string linePrefix,
        string locationLabel,
        CancellationToken cancellationToken)
    {
        var warehouse = (warehouseCode ?? string.Empty).Trim();
        var location = (requestedLocation ?? string.Empty).Trim();
        if (location.Length > 10)
        {
            return (string.Empty, $"{linePrefix}: {locationLabel} must be at most 10 characters.");
        }

        var hasActiveLocations = await common.HasActiveLocationsAsync(
            db,
            companyCode,
            branchCode,
            warehouse,
            cancellationToken);

        if (location.Length == 0)
        {
            return hasActiveLocations
                ? (string.Empty, $"{linePrefix}: {locationLabel} is required for warehouse '{warehouse}'.")
                : (string.Empty, null);
        }

        var activeLocation = await common.GetActiveLocationAsync(
            db,
            companyCode,
            branchCode,
            warehouse,
            location,
            cancellationToken);
        if (activeLocation is null)
        {
            return (
                string.Empty,
                $"{linePrefix}: {locationLabel} '{location}' was not found or is inactive for warehouse '{warehouse}'.");
        }

        return (activeLocation.LocCode.Trim(), null);
    }
}
