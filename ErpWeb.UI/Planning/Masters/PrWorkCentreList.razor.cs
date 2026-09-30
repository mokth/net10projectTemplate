using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrWorkCentreListRow
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Class { get; set; }
    public DateTime? Updated { get; set; }
}

public partial class PrWorkCentreList : PrRefListPageBase<PrWorkCentreListRow>
{
    protected override string MenuCode => MenuCodes.PlanningWorkCentre;
    protected override string EntityLabel => "Work Centre";
    protected override string HeroIconClass => "fa-solid fa-industry";
    protected override string HeroTitle => "Work Centres";

    [Inject] private IPrWorkCentreService WorkCentreService { get; set; } = default!;

    protected PrWorkCentreEditVm EditModel { get; set; } = new();

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PrWorkCentreListRow.Code), DataType = "string", SortIndex = 0, VisibleIndex = 1, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PrWorkCentreListRow.Description), DataType = "string", VisibleIndex = 2 },
        new() { Caption = "Class", FieldName = nameof(PrWorkCentreListRow.Class), DataType = "string", VisibleIndex = 3, Width = "100px" }
    ];

    protected override async Task ReloadListAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await WorkCentreService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load work centres.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrWorkCentreListRow
                {
                    Code = x.WrkCtrCd,
                    Description = x.WrkCtrDes,
                    Class = x.Class,
                    Updated = x.Updated
                }).ToList();
            }
            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally
        {
            IsLoading = false;
        }
    }

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
        EditModel = new PrWorkCentreEditVm { ClientNodeId = Guid.NewGuid().ToString("N"), IsNew = true };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrWorkCentreListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        LoadEditFromRow(row);
        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrWorkCentreListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        LoadEditFromRow(row);
        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private void LoadEditFromRow(PrWorkCentreListRow row)
    {
        EditModel = new PrWorkCentreEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            WrkCtrCd = row.Code,
            WrkCtrDes = row.Description,
            Class = row.Class,
            OriginalUpdated = row.Updated,
            IsNew = false
        };
        ErrorMessage = null;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            EditModel.IsNew = !IsEditMode;
            var result = await WorkCentreService.SaveBatchAsync([EditModel]);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Work Centre updated successfully." : "Work Centre added successfully.";
                await ReloadListAsync();
            }
            else
            {
                ErrorMessage = FormatPlanningResult(result);
            }
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    protected override async Task<PlanningServiceResult> DeleteSelectedAsync()
    {
        var rows = SelectedRows.Select(r => new PrWorkCentreEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            WrkCtrCd = r.Code,
            OriginalUpdated = r.Updated,
            IsDeleted = true
        }).ToList();
        return await WorkCentreService.SaveBatchAsync(rows);
    }
}
