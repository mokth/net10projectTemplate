using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrWorkPrefixListRow
{
    public string Prefix { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public sealed class PrWorkPrefixEditVm
{
    public string Prefix { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public partial class PrWorkPrefixList : PrRefListPageBase<PrWorkPrefixListRow>
{
    protected override string MenuCode => MenuCodes.PlanningWorkPrefix;
    protected override string EntityLabel => "Work Prefix";
    protected override string HeroIconClass => "fa-solid fa-font";
    protected override string HeroTitle => "Work Prefixes";

    [Inject] private IPrWorkPrefixService PrefixService { get; set; } = default!;

    protected PrWorkPrefixEditVm EditModel { get; set; } = new();

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Prefix", FieldName = nameof(PrWorkPrefixListRow.Prefix), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Description", FieldName = nameof(PrWorkPrefixListRow.Description), VisibleIndex = 2 }
    ];

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await PrefixService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load work prefixes.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrWorkPrefixListRow
                {
                    Prefix = x.Prefix,
                    Description = x.Description
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
        EditModel = new PrWorkPrefixEditVm();
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrWorkPrefixListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        EditModel = new PrWorkPrefixEditVm { Prefix = row.Prefix, Description = row.Description };
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrWorkPrefixListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditModel = new PrWorkPrefixEditVm { Prefix = row.Prefix, Description = row.Description };
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var entity = new PrWorkPefix { Prefix = EditModel.Prefix, Description = EditModel.Description };
            var result = await PrefixService.SaveBatchAsync([entity], []);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Work Prefix updated successfully." : "Work Prefix added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        PrefixService.SaveBatchAsync([], SelectedRows.Select(r => r.Prefix).ToList());
}
