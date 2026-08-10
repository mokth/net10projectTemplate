using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvStockBalance : PageBase
{
    [Inject] private IStockInquiryService Inquiry { get; set; } = default!;
    protected string? StatusMessage;
    protected List<StockBalanceRowDto> Rows { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        Rows = (await Inquiry.GetBalancesAsync()).ToList();
        StatusMessage = Rows.Count == 0 ? null : $"{Rows.Count} balance row(s).";
    }
}
