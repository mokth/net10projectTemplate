using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Sales.Delivery;

public sealed class SaDeliveryBoardService : ISaDeliveryBoardService
{
    private const int MaxBoardRows = 250;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IInventoryTenantContext _tenant;
    private readonly ISaDeliveryTrackingFeatureGate _featureGate;

    public SaDeliveryBoardService(
        IDbContextFactory<AppDbContext> dbFactory,
        IInventoryTenantContext tenant,
        ISaDeliveryTrackingFeatureGate featureGate)
    {
        _dbFactory = dbFactory;
        _tenant = tenant;
        _featureGate = featureGate;
    }

    public async Task<SaDeliveryBoardResult> SearchAsync(
        SaDeliveryBoardQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!await _featureGate.IsEnabledAsync(cancellationToken))
        {
            return new SaDeliveryBoardResult { FeatureEnabled = false };
        }

        var scope = _tenant.TryBranchScope();
        if (scope?.BranchCode is null)
        {
            return new SaDeliveryBoardResult
            {
                FeatureEnabled = true,
                ErrorMessage = "A valid company and branch are required to view delivery tracking."
            };
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var date = query.TripDate.Date;
        var trips = db.SaDeliveryTrips
            .AsNoTracking()
            .Where(x => x.CompanyCode == scope.CompanyCode
                        && x.BranchCode == scope.BranchCode
                        && x.TripDate == date);

        var search = query.SearchText?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            trips = trips.Where(x => x.TripNo.Contains(search)
                || (x.DriverNameSnapshot ?? string.Empty).Contains(search)
                || (x.VehicleRegistrationSnapshot ?? string.Empty).Contains(search)
                || x.Stops.Any(stop => stop.CustCode.Contains(search)
                    || (stop.CustNameSnapshot ?? string.Empty).Contains(search)
                    || stop.DeliveryOrders.Any(link => link.Do.CustCode.Contains(search)
                        || (link.Do.CustName ?? string.Empty).Contains(search))));
        }

        var tripStatusCounts = await trips
            .GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var stopStatusCounts = await trips
            .SelectMany(x => x.Stops)
            .GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, cancellationToken);

        var displayedTrips = ApplyStatusFilter(trips, query.StatusFilter);
        var matchingTripCount = await displayedTrips.CountAsync(cancellationToken);
        var rows = await displayedTrips
            .OrderBy(x => x.PlannedDepartureAt ?? DateTime.MaxValue)
            .ThenBy(x => x.TripNo)
            .Take(MaxBoardRows)
            .Select(x => new SaDeliveryBoardTripRow
            {
                TripNo = x.TripNo,
                TripDate = x.TripDate,
                Status = x.Status,
                DriverName = x.DriverNameSnapshot,
                VehicleRegistration = x.VehicleRegistrationSnapshot,
                PlannedDepartureAt = x.PlannedDepartureAt,
                ActualDepartureAt = x.ActualDepartureAt,
                StopCount = x.Stops.Count,
                DeliveredCount = x.Stops.Count(stop => stop.Status == SaDeliveryStopStatuses.Delivered),
                PartialCount = x.Stops.Count(stop => stop.Status == SaDeliveryStopStatuses.PartiallyDelivered),
                FailedCount = x.Stops.Count(stop => stop.Status == SaDeliveryStopStatuses.Failed),
                OutstandingCount = x.Stops.Count(stop => stop.Status != SaDeliveryStopStatuses.Delivered
                    && stop.Status != SaDeliveryStopStatuses.Cancelled)
            })
            .ToListAsync(cancellationToken);

        return new SaDeliveryBoardResult
        {
            FeatureEnabled = true,
            MatchingTripCount = matchingTripCount,
            Trips = rows,
            Summary = new SaDeliveryBoardSummary
            {
                PlannedTrips = GetCount(tripStatusCounts, SaDeliveryTripStatuses.Planned),
                OutForDeliveryTrips = GetCount(tripStatusCounts, SaDeliveryTripStatuses.OutForDelivery),
                DeliveredStops = GetCount(stopStatusCounts, SaDeliveryStopStatuses.Delivered),
                PartialStops = GetCount(stopStatusCounts, SaDeliveryStopStatuses.PartiallyDelivered),
                FailedStops = GetCount(stopStatusCounts, SaDeliveryStopStatuses.Failed),
                RescheduledStops = GetCount(stopStatusCounts, SaDeliveryStopStatuses.Rescheduled),
                OutstandingStops = stopStatusCounts
                    .Where(x => x.Key != SaDeliveryStopStatuses.Delivered && x.Key != SaDeliveryStopStatuses.Cancelled)
                    .Sum(x => x.Value)
            }
        };
    }

    private static IQueryable<SaDeliveryTrip> ApplyStatusFilter(IQueryable<SaDeliveryTrip> trips, string? filter) => filter switch
    {
        SaDeliveryTripStatuses.Planned => trips.Where(x => x.Status == SaDeliveryTripStatuses.Planned),
        SaDeliveryTripStatuses.OutForDelivery => trips.Where(x => x.Status == SaDeliveryTripStatuses.OutForDelivery),
        SaDeliveryStopStatuses.Delivered => trips.Where(x => x.Stops.Any(stop => stop.Status == SaDeliveryStopStatuses.Delivered)),
        SaDeliveryStopStatuses.PartiallyDelivered => trips.Where(x => x.Stops.Any(stop => stop.Status == SaDeliveryStopStatuses.PartiallyDelivered)),
        SaDeliveryStopStatuses.Failed => trips.Where(x => x.Stops.Any(stop => stop.Status == SaDeliveryStopStatuses.Failed)),
        SaDeliveryStopStatuses.Rescheduled => trips.Where(x => x.Stops.Any(stop => stop.Status == SaDeliveryStopStatuses.Rescheduled)),
        "OUTSTANDING" => trips.Where(x => x.Stops.Any(stop => stop.Status != SaDeliveryStopStatuses.Delivered
            && stop.Status != SaDeliveryStopStatuses.Cancelled)),
        _ => trips
    };

    private static int GetCount(IReadOnlyDictionary<string, int> counts, string status) =>
        counts.TryGetValue(status, out var count) ? count : 0;
}
