using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Entities;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvPostingWorkbench : PageBase
{
    [Inject] private IPostingEngine Engine { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    protected DocumentType DocType { get; private set; }
    protected string MenuCode { get; private set; } = MenuCodes.Inventory;
    protected string Title { get; private set; } = "Inventory";
    protected bool IsSubmitting;
    protected string? StatusMessage;
    protected string? LastDocNo;
    protected long? LastDocId;
    protected WorkbenchModel Model { get; set; } = new();

    protected override void OnInitialized()
    {
        var path = new Uri(Nav.Uri).AbsolutePath.TrimEnd('/').ToLowerInvariant();
        (DocType, MenuCode, Title) = path switch
        {
            "/inventory/ob" => (DocumentType.OB, MenuCodes.InvOb, "Opening Balance"),
            "/inventory/grn" => (DocumentType.GRN, MenuCodes.InvGrn, "Goods Receipt (GRN)"),
            "/inventory/gi" => (DocumentType.GI, MenuCodes.InvGi, "Goods Issue (GI)"),
            "/inventory/st" => (DocumentType.ST, MenuCodes.InvSt, "Stock Transfer"),
            "/inventory/sa" => (DocumentType.SA, MenuCodes.InvSa, "Stock Adjustment"),
            _ => (DocumentType.GRN, MenuCodes.InvGrn, "Inventory Document")
        };
        Model.DocDate = DateTime.Today;
        Model.DirectionValue = 1;
    }

    protected async Task CreateAndPostAsync()
    {
        if (IsSubmitting) return;
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var dto = new CreateDocumentDto
            {
                DocType = DocType,
                DocDate = Model.DocDate.Date,
                WarehouseId = DocType == DocumentType.ST ? null : Model.WarehouseId,
                SourceWarehouseId = DocType == DocumentType.ST ? Model.SourceWarehouseId : null,
                DestinationWarehouseId = DocType == DocumentType.ST ? Model.DestinationWarehouseId : null,
                SourceLocationId = DocType == DocumentType.ST ? Model.SourceLocationId : null,
                DestinationLocationId = DocType == DocumentType.ST ? Model.DestinationLocationId : null,
                AllowZeroCost = Model.AllowZeroCost,
                Lines =
                [
                    new CreateDocumentLineDto
                    {
                        ItemVariantId = Model.ItemVariantId,
                        UOMId = Model.UomId,
                        Qty = Model.Qty,
                        UnitCost = Model.UnitCost,
                        LocationId = Model.LocationId,
                        Direction = DocType == DocumentType.SA
                            ? (AdjustmentDirection)Model.DirectionValue
                            : null,
                        ReasonCodeId = DocType == DocumentType.SA ? Model.ReasonCodeId : null
                    }
                ]
            };

            var created = await Engine.CreateDocumentAsync(dto);
            if (!created.Succeeded)
            {
                ErrorMessage = $"{created.ErrorCode}: {created.ErrorMessage}";
                return;
            }

            var posted = await Engine.PostAsync(created.Document!.Id, CurrentUser?.UserId ?? "user");
            if (!posted.Succeeded)
            {
                ErrorMessage = $"{posted.ErrorCode}: {posted.ErrorMessage}";
                return;
            }

            LastDocNo = posted.Document!.DocNo;
            LastDocId = posted.Document.Id;
            StatusMessage = "Posted successfully.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected sealed class WorkbenchModel
    {
        public DateTime DocDate { get; set; }
        public long WarehouseId { get; set; }
        public long SourceWarehouseId { get; set; }
        public long DestinationWarehouseId { get; set; }
        public long SourceLocationId { get; set; }
        public long DestinationLocationId { get; set; }
        public long ItemVariantId { get; set; }
        public long UomId { get; set; }
        public long LocationId { get; set; }
        public decimal Qty { get; set; } = 1;
        public decimal UnitCost { get; set; }
        public bool AllowZeroCost { get; set; }
        public int DirectionValue { get; set; } = 1;
        public long ReasonCodeId { get; set; }
    }
}
