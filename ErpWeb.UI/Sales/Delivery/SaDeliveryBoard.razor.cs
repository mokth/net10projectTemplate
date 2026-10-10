using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales.Delivery;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Delivery;

public partial class SaDeliveryBoard
{
    [Inject] private ISaDeliveryBoardService Board { get; set; } = default!;

    protected DateTime SelectedDate { get; set; } = DateTime.Today;
    protected string SearchText { get; set; } = string.Empty;
    protected string? StatusFilter { get; set; }
    protected bool? FeatureEnabled { get; set; }
    protected bool IsLoading { get; set; }
    protected int MatchingTripCount { get; set; }
    protected SaDeliveryBoardSummary Summary { get; set; } = new();
    protected List<SaDeliveryBoardTripRow> Trips { get; set; } = [];

    protected IReadOnlyList<BoardKpiTile> KpiTiles =>
    [
        new("Planned", SaDeliveryTripStatuses.Planned, Summary.PlannedTrips),
        new("Out for delivery", SaDeliveryTripStatuses.OutForDelivery, Summary.OutForDeliveryTrips),
        new("Delivered", SaDeliveryStopStatuses.Delivered, Summary.DeliveredStops),
        new("Partial", SaDeliveryStopStatuses.PartiallyDelivered, Summary.PartialStops),
        new("Failed", SaDeliveryStopStatuses.Failed, Summary.FailedStops),
        new("Rescheduled", SaDeliveryStopStatuses.Rescheduled, Summary.RescheduledStops),
        new("Outstanding", "OUTSTANDING", Summary.OutstandingStops)
    ];

    protected override Task OnPageInitializedAsync() => ReloadAsync();

    protected async Task ReloadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await Board.SearchAsync(new SaDeliveryBoardQuery
            {
                TripDate = SelectedDate,
                SearchText = SearchText,
                StatusFilter = StatusFilter
            });

            FeatureEnabled = result.FeatureEnabled;
            ErrorMessage = result.ErrorMessage;
            MatchingTripCount = result.MatchingTripCount;
            Summary = result.Summary;
            Trips = result.Trips.ToList();
        }
        catch
        {
            FeatureEnabled = null;
            Trips = [];
            ErrorMessage = "Unable to load the delivery board. Check the application log and try again.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected async Task SelectStatusFilterAsync(string? filter)
    {
        StatusFilter = StatusFilter == filter ? null : filter;
        await ReloadAsync();
    }

    protected async Task ClearStatusFilter()
    {
        StatusFilter = null;
        await ReloadAsync();
    }

    protected async Task ResetFiltersAsync()
    {
        SelectedDate = DateTime.Today;
        SearchText = string.Empty;
        StatusFilter = null;
        await ReloadAsync();
    }

    protected static string StatusLabel(string? status) => status switch
    {
        SaDeliveryTripStatuses.Planned => "Planned",
        SaDeliveryTripStatuses.OutForDelivery => "Out for delivery",
        SaDeliveryTripStatuses.Completed => "Completed",
        SaDeliveryTripStatuses.Cancelled => "Cancelled",
        SaDeliveryStopStatuses.Delivered => "Delivered",
        SaDeliveryStopStatuses.PartiallyDelivered => "Partial",
        SaDeliveryStopStatuses.Failed => "Failed",
        SaDeliveryStopStatuses.Rescheduled => "Rescheduled",
        "OUTSTANDING" => "Outstanding",
        _ => string.IsNullOrWhiteSpace(status) ? "Unknown" : status.Replace('_', ' ')
    };

    protected static string TripStatusClass(string status) => status switch
    {
        SaDeliveryTripStatuses.Completed => "is-on",
        SaDeliveryTripStatuses.OutForDelivery => "is-hold",
        SaDeliveryTripStatuses.Cancelled => "is-off",
        _ => "is-hold"
    };

    protected static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    protected static string DepartureLabel(SaDeliveryBoardTripRow trip) =>
        trip.ActualDepartureAt?.ToString("HH:mm") ?? trip.PlannedDepartureAt?.ToString("HH:mm") ?? "Not set";

    protected sealed record BoardKpiTile(string Label, string Filter, int Value);
}
