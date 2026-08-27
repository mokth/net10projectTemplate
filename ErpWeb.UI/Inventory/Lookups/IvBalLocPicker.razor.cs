using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvBalLocPicker
{
    private bool _popupVisible;

    [Parameter] public int? BalLocId { get; set; }
    [Parameter] public EventCallback<int?> BalLocIdChanged { get; set; }
    [Parameter] public EventCallback<IvBalLocLookupRow> Selected { get; set; }
    [Parameter] public string? ICodeFilter { get; set; }
    [Parameter] public string? DisplayLotNo { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }

    private string DisplayText =>
        string.IsNullOrWhiteSpace(DisplayLotNo)
            ? (BalLocId is null ? string.Empty : $"#{BalLocId}")
            : DisplayLotNo;

    private Task OpenSearchAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        _popupVisible = true;
        return Task.CompletedTask;
    }

    private async Task OnSelectedAsync(IvBalLocLookupRow row)
    {
        BalLocId = row.Id;
        DisplayLotNo = row.LotNo;
        if (BalLocIdChanged.HasDelegate)
        {
            await BalLocIdChanged.InvokeAsync(row.Id);
        }

        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }

        _popupVisible = false;
    }
}
