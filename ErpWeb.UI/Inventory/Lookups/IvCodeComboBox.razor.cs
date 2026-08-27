using ErpWeb.Core.Inventory;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory.Lookups;

public partial class IvCodeComboBox
{
    [Parameter] public IEnumerable<IvCodeLookupRow>? Data { get; set; }
    [Parameter] public string? Value { get; set; }
    [Parameter] public EventCallback<string?> ValueChanged { get; set; }
    [Parameter] public bool Enabled { get; set; } = true;
    [Parameter] public bool ReadOnly { get; set; }
    [Parameter] public bool IsLoading { get; set; }
    [Parameter] public string? InputCssClass { get; set; }
    [Parameter] public string? NullText { get; set; }

    private async Task OnValueChanged(string? value)
    {
        Value = value;
        if (ValueChanged.HasDelegate)
        {
            await ValueChanged.InvokeAsync(value);
        }
    }
}
