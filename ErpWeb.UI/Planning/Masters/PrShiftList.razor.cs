using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrShiftListRow
{
    public string ShiftCd { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double? TotalTime { get; set; }
}

public partial class PrShiftList : PrRefListPageBase<PrShiftListRow>
{
    protected override string MenuCode => MenuCodes.PlanningShift;
    protected override string EntityLabel => "Shift";
    protected override string HeroIconClass => "fa-solid fa-clock";
    protected override string HeroTitle => "Shifts";

    [Inject] private IPrShiftService ShiftService { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Code", FieldName = nameof(PrShiftListRow.ShiftCd), VisibleIndex = 1, Width = "100px", SortIndex = 0 },
        new() { Caption = "Description", FieldName = nameof(PrShiftListRow.Description), VisibleIndex = 2 },
        new() { Caption = "Total", FieldName = nameof(PrShiftListRow.TotalTime), DataType = "number", VisibleIndex = 3, Width = "90px" }
    ];

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await ShiftService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load shifts.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrShiftListRow
                {
                    ShiftCd = x.ShiftCd,
                    Description = x.ShiftDes,
                    TotalTime = x.TotalTime
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
        Navigation.NavigateTo("/planning/shifts/new");
    }

    protected override async Task OnViewClickAsync(PrShiftListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        Navigation.NavigateTo($"/planning/shifts/{Uri.EscapeDataString(row.ShiftCd)}?mode=view");
    }

    protected override async Task OnEditClickAsync(PrShiftListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        Navigation.NavigateTo($"/planning/shifts/{Uri.EscapeDataString(row.ShiftCd)}");
    }

    protected override async Task<PlanningServiceResult> DeleteSelectedAsync()
    {
        PlanningServiceResult? last = null;
        foreach (var row in SelectedRows)
        {
            last = await ShiftService.DeleteAsync(row.ShiftCd);
            if (!last.Succeeded) return last;
        }
        return last ?? PlanningServiceResult.Ok();
    }
}
