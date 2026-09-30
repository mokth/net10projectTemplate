using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrProcessListRow
{
    public string RowKey => $"{ProcessCd}|{WorkCentre}";
    public string ProcessCd { get; set; } = string.Empty;
    public string WorkCentre { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? Sequence { get; set; }
    public bool? Stock { get; set; }
    public DateTime? Updated { get; set; }
}

public sealed class PrWorkCentreOption
{
    public string Code { get; set; } = string.Empty;
}

public partial class PrProcessList : PrRefListPageBase<PrProcessListRow>
{
    protected override string MenuCode => MenuCodes.PlanningWorkProcess;
    protected override string EntityLabel => "Process";
    protected override string HeroIconClass => "fa-solid fa-gears";
    protected override string HeroTitle => "Processes";

    [Inject] private IPrProcessService ProcessService { get; set; } = default!;
    [Inject] private IPrWorkCentreService WorkCentreService { get; set; } = default!;

    protected PrProcessEditVm EditModel { get; set; } = new();
    protected List<PrWorkCentreOption> WorkCentres { get; set; } = [];

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Process", FieldName = nameof(PrProcessListRow.ProcessCd), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Work Centre", FieldName = nameof(PrProcessListRow.WorkCentre), VisibleIndex = 2, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PrProcessListRow.Description), VisibleIndex = 3 },
        new() { Caption = "Seq", FieldName = nameof(PrProcessListRow.Sequence), DataType = "number", VisibleIndex = 4, Width = "80px" },
        new() { Caption = "Stock", FieldName = nameof(PrProcessListRow.Stock), DataType = "bool", VisibleIndex = 5, Width = "80px" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        var wc = await WorkCentreService.ListAsync();
        if (wc.Succeeded)
            WorkCentres = wc.Value!.Select(x => new PrWorkCentreOption { Code = x.WrkCtrCd }).ToList();
        await base.OnPageInitializedAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await ProcessService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load processes.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrProcessListRow
                {
                    ProcessCd = x.ProcessCd,
                    WorkCentre = x.WorkCentre,
                    Description = x.ProcessDes,
                    Sequence = x.Sequence,
                    Stock = x.Stock,
                    Updated = x.Updated
                }).ToList();
            }
            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally { IsLoading = false; }
    }

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
        EditModel = new PrProcessEditVm { ClientNodeId = Guid.NewGuid().ToString("N"), IsNew = true, Sequence = 10 };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrProcessListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        LoadEdit(row); IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrProcessListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        LoadEdit(row); IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    private void LoadEdit(PrProcessListRow row)
    {
        EditModel = new PrProcessEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            ProcessCd = row.ProcessCd,
            WorkCentre = row.WorkCentre,
            ProcessDes = row.Description,
            Sequence = row.Sequence,
            Stock = row.Stock,
            OriginalUpdated = row.Updated,
            IsNew = false
        };
        ErrorMessage = null;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            EditModel.IsNew = !IsEditMode;
            var result = await ProcessService.SaveBatchAsync([EditModel]);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Process updated successfully." : "Process added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        ProcessService.SaveBatchAsync(SelectedRows.Select(r => new PrProcessEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            ProcessCd = r.ProcessCd,
            WorkCentre = r.WorkCentre,
            OriginalUpdated = r.Updated,
            IsDeleted = true
        }).ToList());
}
