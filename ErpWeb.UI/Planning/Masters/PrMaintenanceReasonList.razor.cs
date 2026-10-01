using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrMaintenanceReasonListRow
{
    public string ReasonCd { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReasonType { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
}

public sealed class PrMaintenanceReasonEditVm
{
    public string ReasonCd { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ReasonType { get; set; } = PrMaintenanceReasonTypes.Other;
    public bool Active { get; set; } = true;
}

public partial class PrMaintenanceReasonList : PrRefListPageBase<PrMaintenanceReasonListRow>
{
    protected override string MenuCode => MenuCodes.PlanningMaintenanceReason;
    protected override string EntityLabel => "Maintenance Reason";
    protected override string HeroIconClass => "fa-solid fa-list-check";
    protected override string HeroTitle => "Maintenance Reasons";
    protected override bool SupportsActivate => true;

    [Inject] private IPrMaintenanceReasonService ReasonService { get; set; } = default!;

    protected PrMaintenanceReasonEditVm EditModel { get; set; } = new();
    protected string[] ReasonTypes { get; } = PrMaintenanceReasonTypes.All;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PrMaintenanceReasonListRow.ReasonCd), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Description", FieldName = nameof(PrMaintenanceReasonListRow.Description), VisibleIndex = 2 },
        new() { Caption = "Type", FieldName = nameof(PrMaintenanceReasonListRow.ReasonType), VisibleIndex = 3, Width = "120px" },
        new() { Caption = "Active", FieldName = nameof(PrMaintenanceReasonListRow.Active), DataType = "bool", VisibleIndex = 4, Width = "80px" }
    ];

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await ReasonService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load reasons.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrMaintenanceReasonListRow
                {
                    ReasonCd = x.ReasonCd,
                    Description = x.Description,
                    ReasonType = x.ReasonType,
                    Active = x.Active
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
        EditModel = new PrMaintenanceReasonEditVm { Active = true, ReasonType = PrMaintenanceReasonTypes.Other };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrMaintenanceReasonListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        EditModel = new PrMaintenanceReasonEditVm { ReasonCd = row.ReasonCd, Description = row.Description, ReasonType = row.ReasonType, Active = row.Active };
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrMaintenanceReasonListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditModel = new PrMaintenanceReasonEditVm { ReasonCd = row.ReasonCd, Description = row.Description, ReasonType = row.ReasonType, Active = row.Active };
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var entity = new PrMaintenanceReason
            {
                ReasonCd = EditModel.ReasonCd,
                Description = EditModel.Description,
                ReasonType = EditModel.ReasonType,
                Active = EditModel.Active
            };
            var result = await ReasonService.SaveBatchAsync([entity], []);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Reason updated." : "Reason added.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        ReasonService.SaveBatchAsync([], SelectedRows.Select(r => r.ReasonCd).ToList());

    protected override async Task<PlanningServiceResult> SetActiveSelectedAsync(bool isActive)
    {
        var entities = SelectedRows.Select(r => new PrMaintenanceReason
        {
            ReasonCd = r.ReasonCd,
            Description = r.Description,
            ReasonType = r.ReasonType,
            Active = isActive
        }).ToList();
        return await ReasonService.SaveBatchAsync(entities, []);
    }
}
