using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvStockMasterPicker
{
    private bool _popupVisible;

    [Parameter] public string? ICode { get; set; }
    [Parameter] public EventCallback<string?> ICodeChanged { get; set; }
    [Parameter] public EventCallback<IvStockMasterLookupRow> Selected { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public string? InputCssClass { get; set; }

    private Task OpenSearchAsync()
    {
        if (!Enabled)
        {
            return Task.CompletedTask;
        }

        _popupVisible = true;
        return Task.CompletedTask;
    }

    private async Task OnSelectedAsync(IvStockMasterLookupRow row)
    {
        ICode = row.ICode;
        if (ICodeChanged.HasDelegate)
        {
            await ICodeChanged.InvokeAsync(row.ICode);
        }

        if (Selected.HasDelegate)
        {
            await Selected.InvokeAsync(row);
        }

        _popupVisible = false;
    }
}
