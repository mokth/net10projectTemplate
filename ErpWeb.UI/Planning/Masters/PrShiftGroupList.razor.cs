using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrShiftGroupListRow
{
    public string ShfGrpCd { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool DefaultGrp { get; set; }
    public string? ShiftColor { get; set; }
    public string ShiftSummary { get; set; } = string.Empty;
}

public partial class PrShiftGroupList : PrRefListPageBase<PrShiftGroupListRow>
{
    protected override string MenuCode => MenuCodes.PlanningShiftGroup;
    protected override string EntityLabel => "Shift Group";
    protected override string HeroIconClass => "fa-solid fa-layer-group";
    protected override string HeroTitle => "Shift Groups";

    [Inject] private IPrShiftGroupService GroupService { get; set; } = default!;
    [Inject] private IPrShiftService ShiftService { get; set; } = default!;

    protected PrShiftGroupEditVm EditModel { get; set; } = new() { IsNew = true };
    protected List<PrShift> AllShifts { get; set; } = [];
    protected HashSet<string> SelectedShiftCds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Group", FieldName = nameof(PrShiftGroupListRow.ShfGrpCd), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Description", FieldName = nameof(PrShiftGroupListRow.Description), VisibleIndex = 2 },
        new() { Caption = "Shifts", FieldName = nameof(PrShiftGroupListRow.ShiftSummary), VisibleIndex = 3 },
        new() { Caption = "Default", FieldName = nameof(PrShiftGroupListRow.DefaultGrp), DataType = "bool", VisibleIndex = 4, Width = "90px" },
        new() { Caption = "Color", FieldName = nameof(PrShiftGroupListRow.ShiftColor), VisibleIndex = 5, Width = "100px" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        var s = await ShiftService.ListAsync();
        if (s.Succeeded) AllShifts = s.Value!.ToList();
        await base.OnPageInitializedAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await GroupService.ListGroupsAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load shift groups.";
                Data = [];
            }
            else
            {
                Data = result.Value!
                    .GroupBy(x => x.ShfGrpCd, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var first = g.First();
                        return new PrShiftGroupListRow
                        {
                            ShfGrpCd = g.Key,
                            Description = first.ShfGrpDes,
                            DefaultGrp = first.DefaultGrp == true,
                            ShiftColor = first.ShiftColor,
                            ShiftSummary = string.Join(", ", g.Select(x => x.ShiftCd).Distinct())
                        };
                    })
                    .OrderBy(x => x.ShfGrpCd)
                    .ToList();
            }
            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally { IsLoading = false; }
    }

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
        EditModel = new PrShiftGroupEditVm { IsNew = true };
        SelectedShiftCds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrShiftGroupListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        if (!await LoadEditAsync(row.ShfGrpCd)) return;
        IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrShiftGroupListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (!await LoadEditAsync(row.ShfGrpCd)) return;
        IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    private async Task<bool> LoadEditAsync(string code)
    {
        var r = await GroupService.GetAsync(code);
        if (!r.Succeeded || r.Value is null)
        {
            StatusMessage = r.Message ?? "Unable to load shift group.";
            return false;
        }
        EditModel = r.Value;
        SelectedShiftCds = new HashSet<string>(EditModel.SelectedShiftCds, StringComparer.OrdinalIgnoreCase);
        ErrorMessage = null;
        return true;
    }

    protected void ToggleShift(string code, bool on)
    {
        if (!EditEnabled) return;
        if (on)
        {
            if (SelectedShiftCds.Count >= ShiftGroupValidator.MaxShiftsPerGroup && !SelectedShiftCds.Contains(code))
            {
                ErrorMessage = $"Select at most {ShiftGroupValidator.MaxShiftsPerGroup} shifts.";
                return;
            }
            SelectedShiftCds.Add(code);
            ErrorMessage = null;
        }
        else
        {
            SelectedShiftCds.Remove(code);
        }
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            EditModel.IsNew = !IsEditMode;
            EditModel.SelectedShiftCds = SelectedShiftCds.ToList();
            var result = await GroupService.SaveAsync(EditModel);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Shift Group updated successfully." : "Shift Group added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override async Task<PlanningServiceResult> DeleteSelectedAsync()
    {
        PlanningServiceResult? last = null;
        foreach (var row in SelectedRows)
        {
            last = await GroupService.DeleteAsync(row.ShfGrpCd);
            if (!last.Succeeded) return last;
        }
        return last ?? PlanningServiceResult.Ok();
    }
}
