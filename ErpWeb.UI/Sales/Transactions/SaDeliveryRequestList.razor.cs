using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Sales.Transactions;

public partial class SaDeliveryRequestList : PageBase
{
    [Inject] private ISaDeliveryRequestService Requests { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsLoading { get; private set; } = true;
    protected bool CanAdd { get; private set; }
    protected string? StatusMessage { get; private set; }
    protected string SearchText { get; private set; } = string.Empty;
    protected string StatusFilter { get; set; } = string.Empty;
    protected IReadOnlyList<SaDeliveryRequestListRow> Rows { get; private set; } = [];
    protected int TotalCount { get; private set; }
    protected decimal TotalUnplanned => Rows.Sum(x => x.UnplannedQty);
    protected string TotalCountLabel => TotalCount == 1 ? "1 Delivery Request" : $"{TotalCount:N0} Delivery Requests";

    protected override async Task OnPageInitializedAsync()
    {
        CanAdd = await AccessRights.CanAsync(MenuCodes.SalesDeliveryRequest, PermissionCodes.Add);
        await LoadAsync();
    }

    protected async Task OnSearchChanged(ChangeEventArgs args)
    {
        SearchText = args.Value?.ToString() ?? string.Empty;
        await LoadAsync();
    }

    protected async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        var result = await Requests.SearchAsync(new SaDeliveryRequestListQuery
        {
            SearchText = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim(),
            Status = string.IsNullOrWhiteSpace(StatusFilter) ? null : StatusFilter,
            Take = 100
        });
        if (result.Succeeded && result.Data is not null)
        {
            Rows = result.Data.Rows;
            TotalCount = result.Data.TotalCount;
        }
        else
        {
            Rows = [];
            TotalCount = 0;
            ErrorMessage = result.Message ?? "Unable to load Delivery Requests.";
        }

        IsLoading = false;
    }

    protected void NewRequest() => Navigation.NavigateTo("/sales/delivery-requests/new");

    protected void Open(long uid) => Navigation.NavigateTo($"/sales/delivery-requests/view/{uid}");

    protected static string StatusClass(string status) => status switch
    {
        SaDeliveryRequestStatuses.Completed => "is-complete",
        SaDeliveryRequestStatuses.Cancelled => "is-cancelled",
        SaDeliveryRequestStatuses.InProduction => "is-progress",
        SaDeliveryRequestStatuses.Released => "is-released",
        _ => "is-draft"
    };
}
