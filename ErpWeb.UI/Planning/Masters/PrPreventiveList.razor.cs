using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrPreventiveListRow
{
    public int Uid { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public DateTime DownDt { get; set; }
    public string? ReasonCd { get; set; }
    public string? Remark { get; set; }
}

public sealed class PrPreventiveEditVm
{
    public int Uid { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public DateTime DownDt { get; set; } = DateTime.Today;
    public string? ReasonCd { get; set; }
    public string? Remark { get; set; }
}

public partial class PrPreventiveList : PrRefListPageBase<PrPreventiveListRow>
{
    protected override string MenuCode => MenuCodes.PlanningMacPreventive;
    protected override string EntityLabel => "Preventive";
    protected override string HeroIconClass => "fa-solid fa-screwdriver-wrench";
    protected override string HeroTitle => "Machine Preventive";

    [Inject] private IPrPreventiveService PreventiveService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;

    protected PrPreventiveEditVm EditModel { get; set; } = new();
    protected List<PrMachine> Machines { get; set; } = [];
    protected bool RangePopupVisible;

    private string? _rangeMac, _startText = "08:00", _endText = "17:00", _reasonCd;
    private DateTime _from = DateTime.Today, _to = DateTime.Today;

    protected override List<ButtonInfo> BuildToolbarButtons()
    {
        var buttons = base.BuildToolbarButtons();
        buttons.Insert(1, new() { IConClass = "fa-solid fa-calendar-plus", Style = "primary", Text = "RANGE", ToolTip = "Add date range" });
        return buttons;
    }

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "UID", FieldName = nameof(PrPreventiveListRow.Uid), DataType = "number", VisibleIndex = 1, Width = "80px" },
        new() { Caption = "Machine", FieldName = nameof(PrPreventiveListRow.MachineCd), VisibleIndex = 2, Width = "120px", SortIndex = 0 },
        new() { Caption = "Date", FieldName = nameof(PrPreventiveListRow.DownDt), DataType = "date", VisibleIndex = 3, Width = "120px" },
        new() { Caption = "Reason", FieldName = nameof(PrPreventiveListRow.ReasonCd), VisibleIndex = 4, Width = "100px" },
        new() { Caption = "Remark", FieldName = nameof(PrPreventiveListRow.Remark), VisibleIndex = 5 }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        var m = await MachineService.ListAsync();
        if (m.Succeeded) Machines = m.Value!.ToList();
        await base.OnPageInitializedAsync();
    }

    protected override async Task OnToolbarButtonAsync(SelectedButtonInfo<PrPreventiveListRow> info)
    {
        if (string.Equals(info.SelectedButton.Text, "RANGE", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
            RangePopupVisible = true;
            ErrorMessage = null;
            return;
        }
        await base.OnToolbarButtonAsync(info);
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await PreventiveService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load preventive rows.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrPreventiveListRow
                {
                    Uid = x.Uid,
                    MachineCd = x.MachineCd,
                    DownDt = x.DownDt,
                    ReasonCd = x.ReasonCd,
                    Remark = x.Remark
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
        EditModel = new PrPreventiveEditVm { DownDt = DateTime.Today };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrPreventiveListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        EditModel = new PrPreventiveEditVm { Uid = row.Uid, MachineCd = row.MachineCd, DownDt = row.DownDt, ReasonCd = row.ReasonCd, Remark = row.Remark };
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrPreventiveListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditModel = new PrPreventiveEditVm { Uid = row.Uid, MachineCd = row.MachineCd, DownDt = row.DownDt, ReasonCd = row.ReasonCd, Remark = row.Remark };
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var entity = new PrPreventive
            {
                Uid = IsEditMode ? EditModel.Uid : 0,
                MachineCd = EditModel.MachineCd,
                DownDt = EditModel.DownDt,
                ReasonCd = EditModel.ReasonCd,
                Remark = EditModel.Remark
            };
            var result = await PreventiveService.SaveBatchAsync([entity], []);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Preventive updated successfully." : "Preventive added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected async Task AddRangeAsync()
    {
        if (!TimeOnly.TryParse(_startText, out var start) || !TimeOnly.TryParse(_endText, out var end))
        {
            ErrorMessage = "Invalid time.";
            return;
        }
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var r = await PreventiveService.AddRangeAsync(_rangeMac ?? "", _from, _to, start, end, _reasonCd, null);
            if (r.Succeeded)
            {
                RangePopupVisible = false;
                StatusMessage = "Preventive range added.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(r);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        PreventiveService.SaveBatchAsync([], SelectedRows.Select(r => r.Uid).ToList());
}
