namespace ErpWeb.Core.Sales.Delivery;

public sealed class SaDeliveryBoardQuery
{
    public DateTime TripDate { get; init; } = DateTime.Today;
    public string? SearchText { get; init; }
    public string? StatusFilter { get; init; }
}

public sealed class SaDeliveryBoardTripRow
{
    public string TripNo { get; init; } = string.Empty;
    public DateTime TripDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string? DriverName { get; init; }
    public string? VehicleRegistration { get; init; }
    public DateTime? PlannedDepartureAt { get; init; }
    public DateTime? ActualDepartureAt { get; init; }
    public int StopCount { get; init; }
    public int DeliveredCount { get; init; }
    public int PartialCount { get; init; }
    public int FailedCount { get; init; }
    public int OutstandingCount { get; init; }
}

public sealed class SaDeliveryBoardSummary
{
    public int PlannedTrips { get; init; }
    public int OutForDeliveryTrips { get; init; }
    public int DeliveredStops { get; init; }
    public int PartialStops { get; init; }
    public int FailedStops { get; init; }
    public int RescheduledStops { get; init; }
    public int OutstandingStops { get; init; }
}

public sealed class SaDeliveryBoardResult
{
    public bool FeatureEnabled { get; init; }
    public string? ErrorMessage { get; init; }
    public int MatchingTripCount { get; init; }
    public SaDeliveryBoardSummary Summary { get; init; } = new();
    public IReadOnlyList<SaDeliveryBoardTripRow> Trips { get; init; } = [];
}

public interface ISaDeliveryBoardService
{
    Task<SaDeliveryBoardResult> SearchAsync(
        SaDeliveryBoardQuery query,
        CancellationToken cancellationToken = default);
}
