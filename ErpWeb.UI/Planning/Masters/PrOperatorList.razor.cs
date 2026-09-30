using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrOperatorListRow
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class PrOperatorEditVm
{
    public string Code { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Active { get; set; } = true;
}

public partial class PrOperatorList : PrRefListPageBase<PrOperatorListRow>
{
    protected override string MenuCode => MenuCodes.PlanningOperator;
    protected override string EntityLabel => "Operator";
    protected override string HeroIconClass => "fa-solid fa-user-gear";
    protected override string HeroTitle => "Operators";
    protected override bool SupportsActivate => true;

    [Inject] private IPrOperatorService OperatorService { get; set; } = default!;

    protected PrOperatorEditVm EditModel { get; set; } = new();

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PrOperatorListRow.Code), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Name", FieldName = nameof(PrOperatorListRow.Name), VisibleIndex = 2 },
        new() { Caption = "Active", FieldName = nameof(PrOperatorListRow.Active), DataType = "bool", VisibleIndex = 3, Width = "80px" }
    ];

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await OperatorService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load operators.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrOperatorListRow
                {
                    Code = x.Code,
                    Name = x.Name,
                    Active = x.Active ?? true
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
        EditModel = new PrOperatorEditVm { Active = true };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrOperatorListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        EditModel = new PrOperatorEditVm { Code = row.Code, Name = row.Name, Active = row.Active };
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrOperatorListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditModel = new PrOperatorEditVm { Code = row.Code, Name = row.Name, Active = row.Active };
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var entity = new PrOperator { Code = EditModel.Code, Name = EditModel.Name, Active = EditModel.Active };
            var result = await OperatorService.SaveBatchAsync([entity], []);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Operator updated successfully." : "Operator added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        OperatorService.SaveBatchAsync([], SelectedRows.Select(r => r.Code).ToList());

    protected override async Task<PlanningServiceResult> SetActiveSelectedAsync(bool isActive)
    {
        var rows = SelectedRows.Select(r => new PrOperator
        {
            Code = r.Code,
            Name = r.Name,
            Active = isActive
        }).ToList();
        return await OperatorService.SaveBatchAsync(rows, []);
    }
}
