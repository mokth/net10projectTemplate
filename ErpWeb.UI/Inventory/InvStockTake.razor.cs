using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Inventory;

public partial class InvStockTake : PageBase
{
    [Inject] private IStockTakeService StockTake { get; set; } = default!;

    protected bool IsSubmitting;
    protected string? StatusMessage;
    protected StockTakeModel Model { get; set; } = new() { CountDate = DateTime.Today };

    protected async Task RunFlowAsync()
    {
        if (IsSubmitting) return;
        IsSubmitting = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var create = await StockTake.CreateAsync(Model.CountDate, Model.WarehouseId, [
                new StockTakeLineInput
                {
                    ItemVariantId = Model.ItemVariantId,
                    LocationId = Model.LocationId,
                    SystemQty = Model.SystemQty,
                    CountedQty = Model.CountedQty,
                    ReasonCodeId = Model.ReasonCodeId
                }
            ]);
            if (!create.Succeeded) { ErrorMessage = create.ErrorMessage; return; }

            var id = create.Document!.Id;
            foreach (var step in new Func<Task<PostingResultDto>>[]
                     {
                         () => StockTake.StartCountingAsync(id),
                         () => StockTake.CompleteCountingAsync(id, [
                             new StockTakeLineInput
                             {
                                 ItemVariantId = Model.ItemVariantId,
                                 LocationId = Model.LocationId,
                                 CountedQty = Model.CountedQty,
                                 ReasonCodeId = Model.ReasonCodeId
                             }]),
                         () => StockTake.SubmitForApprovalAsync(id),
                         () => StockTake.ApproveAsync(id, CurrentUser?.UserId ?? "user"),
                         () => StockTake.GenerateAdjustmentAsync(id),
                         () => StockTake.PostGeneratedAdjustmentAsync(id, CurrentUser?.UserId ?? "user")
                     })
            {
                var result = await step();
                if (!result.Succeeded)
                {
                    ErrorMessage = $"{result.ErrorCode}: {result.ErrorMessage}";
                    return;
                }
            }

            StatusMessage = $"Stock take {create.Document.DocNo} posted via generated SA.";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected sealed class StockTakeModel
    {
        public DateTime CountDate { get; set; }
        public long WarehouseId { get; set; }
        public long ItemVariantId { get; set; }
        public long LocationId { get; set; }
        public decimal SystemQty { get; set; }
        public decimal CountedQty { get; set; }
        public long ReasonCodeId { get; set; }
    }
}
