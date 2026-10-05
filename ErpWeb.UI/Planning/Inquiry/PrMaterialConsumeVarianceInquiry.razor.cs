using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Inquiry;

public partial class PrMaterialConsumeVarianceInquiry : PageBase
{
    [Inject] private IProductionMaterialConsumeVarianceInquiryService Inquiry { get; set; } = default!;

    protected bool IsBootstrapping = true;
    protected bool IsSearching;
    protected string ActiveTab = "detail";
    protected int PageIndex;
    protected const int PageSize = 50;

    protected DateTime? DateFrom;
    protected DateTime? DateTo;
    protected string WorkOrderNo = string.Empty;
    protected string ProductCode = string.Empty;
    protected string ComponentCode = string.Empty;
    protected string WorkCentreCode = string.Empty;
    protected string OperationCode = string.Empty;
    protected string VarianceDirection = ProductionMaterialVarianceDirections.Any;
    protected string? VarianceReasonCode;
    protected string DocumentStatus = ProductionOutputStatuses.Posted;
    protected string PostedBy = string.Empty;

    protected ProductionMaterialConsumeVariancePage Detail { get; set; } = new();
    protected ProductionMaterialConsumeVarianceSummaries Summaries { get; set; } = new();

    protected string TotalCountLabel => Detail.TotalCount == 1 ? "1 line" : $"{Detail.TotalCount:N0} lines";
    protected int PageCount => Math.Max(1, (Detail.TotalCount + PageSize - 1) / PageSize);

    protected IReadOnlyList<ProductionOutputChoice> DirectionChoices { get; } =
    [
        new(ProductionMaterialVarianceDirections.Any, "Any"),
        new(ProductionMaterialVarianceDirections.Over, "Over"),
        new(ProductionMaterialVarianceDirections.Under, "Under"),
        new(ProductionMaterialVarianceDirections.Exact, "Exact"),
    ];

    protected IReadOnlyList<ProductionOutputChoice> StatusChoices { get; } =
    [
        new(ProductionOutputStatuses.Posted, "POSTED"),
        new(ProductionOutputStatuses.Reversed, "REVERSED"),
    ];

    protected IReadOnlyList<ProductionOutputChoice> ReasonChoices { get; } =
        ProductionMaterialVarianceReasonCodes.UserSelectable
            .Append(ProductionMaterialVarianceReasonCodes.LegacyUnclassified)
            .Select(code => new ProductionOutputChoice(code, code))
            .ToList();

    protected override async Task OnPageInitializedAsync()
    {
        await SearchAsync();
        IsBootstrapping = false;
    }

    protected async Task SearchAsync()
    {
        IsSearching = true;
        ErrorMessage = null;
        try
        {
            var query = BuildQuery();
            var detail = await Inquiry.SearchDetailAsync(query);
            if (!detail.Succeeded || detail.Data is null)
            {
                ErrorMessage = detail.Message ?? "Unable to load material consume variance.";
                Detail = new();
                Summaries = new();
                return;
            }

            Detail = detail.Data;
            var summaries = await Inquiry.SearchSummariesAsync(query);
            Summaries = summaries.Succeeded && summaries.Data is not null
                ? summaries.Data
                : new();
        }
        finally
        {
            IsSearching = false;
        }
    }

    protected async Task ApplyFiltersAsync()
    {
        PageIndex = 0;
        await SearchAsync();
    }

    protected async Task ClearFiltersAsync()
    {
        DateFrom = DateTo = null;
        WorkOrderNo = ProductCode = ComponentCode = WorkCentreCode = OperationCode = PostedBy = string.Empty;
        VarianceDirection = ProductionMaterialVarianceDirections.Any;
        VarianceReasonCode = null;
        DocumentStatus = ProductionOutputStatuses.Posted;
        PageIndex = 0;
        await SearchAsync();
    }

    protected async Task ChangePageAsync(int change)
    {
        PageIndex = Math.Clamp(PageIndex + change, 0, PageCount - 1);
        await SearchAsync();
    }

    protected void OpenDailyProduction(long outputId) =>
        Navigation.NavigateTo($"/planning/daily-production/view/{outputId}");

    protected void OpenWorkOrder(string workOrderNo) =>
        Navigation.NavigateTo($"/planning/work-orders/view/{Uri.EscapeDataString(workOrderNo)}");

    protected void DismissError() => ErrorMessage = null;

    private ProductionMaterialConsumeVarianceQuery BuildQuery() => new()
    {
        DateFrom = DateFrom,
        DateTo = DateTo,
        WorkOrderNo = NullIfEmpty(WorkOrderNo),
        ProductCode = NullIfEmpty(ProductCode),
        ComponentCode = NullIfEmpty(ComponentCode),
        WorkCentreCode = NullIfEmpty(WorkCentreCode),
        OperationCode = NullIfEmpty(OperationCode),
        VarianceDirection = VarianceDirection,
        VarianceReasonCode = VarianceReasonCode,
        DocumentStatus = DocumentStatus,
        PostedBy = NullIfEmpty(PostedBy),
        Skip = PageIndex * PageSize,
        Take = PageSize,
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
