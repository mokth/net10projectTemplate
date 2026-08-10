using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvStockCard : PageBase
{
    [Inject] private IStockInquiryService Inquiry { get; set; } = default!;
    protected string? StatusMessage;
    protected CardModel Model { get; set; } = new();
    protected List<StockCardRowDto> Rows { get; set; } = [];

    protected async Task LoadAsync()
    {
        if (Model.ItemVariantId <= 0) { StatusMessage = "Item Variant Id required."; return; }
        long? wh = Model.WarehouseId > 0 ? Model.WarehouseId : null;
        Rows = (await Inquiry.GetStockCardAsync(Model.ItemVariantId, wh)).ToList();
        StatusMessage = $"{Rows.Count} ledger row(s).";
    }

    protected sealed class CardModel
    {
        public long ItemVariantId { get; set; }
        public long WarehouseId { get; set; }
    }
}
