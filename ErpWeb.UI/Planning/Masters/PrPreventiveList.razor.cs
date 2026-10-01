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
    public string StartText { get; set; } = string.Empty;
    public string EndText { get; set; } = string.Empty;
    public string? ReasonCd { get; set; }
    public string Status { get; set; } = PreventiveStatuses.Planned;
    public string? CompletedBy { get; set; }
    public string? Remark { get; set; }
    public DateTime StartTm { get; set; }
    public DateTime EndTm { get; set; }
}

public sealed class PrPreventiveEditVm
{
    public int Uid { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public DateTime DownDt { get; set; } = DateTime.Today;
    public string StartText { get; set; } = "08:00";
    public string EndText { get; set; } = "17:00";
    public string? ReasonCd { get; set; }
    public string? Remark { get; set; }
    public string Status { get; set; } = PreventiveStatuses.Planned;
}

public sealed class CompletePreventiveVm
{
    public int Uid { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public string? ReasonCd { get; set; }
    public DateTime ActualStart { get; set; }
    public DateTime ActualEnd { get; set; } = DateTime.Now;
    public string? ActionTaken { get; set; }
    public decimal PartsCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }
}

public partial class PrPreventiveList : PrRefListPageBase<PrPreventiveListRow>
{
    protected override string MenuCode => MenuCodes.PlanningMacPreventive;
    protected override string EntityLabel => "Preventive";
    protected override string HeroIconClass => "fa-solid fa-screwdriver-wrench";
    protected override string HeroTitle => "Machine Preventive";

    [Inject] private IPrPreventiveService PreventiveService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;
    [Inject] private IPrMachineMaintenanceService MaintenanceService { get; set; } = default!;
    [Inject] private IPrMaintenanceReasonService ReasonService { get; set; } = default!;

    protected PrPreventiveEditVm EditModel { get; set; } = new();
    protected CompletePreventiveVm CompleteModel { get; set; } = new();
    protected List<PrMachine> Machines { get; set; } = [];
    protected List<PrMaintenanceReason> Reasons { get; set; } = [];
    protected bool RangePopupVisible;
    protected bool CompletePopupVisible;

    private string? _rangeMac, _startText = "08:00", _endText = "17:00", _reasonCd;
    private DateTime _from = DateTime.Today, _to = DateTime.Today;

    protected override List<ButtonInfo> BuildToolbarButtons()
    {
        var buttons = base.BuildToolbarButtons();
        buttons.Insert(1, new() { IConClass = "fa-solid fa-calendar-plus", Style = "primary", Text = "RANGE", ToolTip = "Add date range" });
        return buttons;
    }

    protected override async Task OnPageInitializedAsync()
    {
        if (RowActionButtons.All(b => !string.Equals(b.Text, "COMPLETE", StringComparison.OrdinalIgnoreCase)))
        {
            RowActionButtons.Add(new() { IConClass = "fa-solid fa-check", Style = "success", Text = "COMPLETE", ToolTip = "Complete" });
            RowActionButtons.Add(new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "CANCEL", ToolTip = "Cancel" });
        }
        var m = await MachineService.ListAsync();
        if (m.Succeeded) Machines = m.Value!.Where(x => x.Active).ToList();
        var r = await ReasonService.ListAsync(activeOnly: true);
        if (r.Succeeded) Reasons = r.Value!.ToList();
        await base.OnPageInitializedAsync();
    }

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "UID", FieldName = nameof(PrPreventiveListRow.Uid), DataType = "number", VisibleIndex = 1, Width = "80px" },
        new() { Caption = "Machine", FieldName = nameof(PrPreventiveListRow.MachineCd), VisibleIndex = 2, Width = "120px", SortIndex = 0 },
        new() { Caption = "Date", FieldName = nameof(PrPreventiveListRow.DownDt), DataType = "date", VisibleIndex = 3, Width = "120px" },
        new() { Caption = "Start", FieldName = nameof(PrPreventiveListRow.StartText), VisibleIndex = 4, Width = "80px" },
        new() { Caption = "End", FieldName = nameof(PrPreventiveListRow.EndText), VisibleIndex = 5, Width = "80px" },
        new() { Caption = "Reason", FieldName = nameof(PrPreventiveListRow.ReasonCd), VisibleIndex = 6, Width = "100px" },
        new() { Caption = "Status", FieldName = nameof(PrPreventiveListRow.Status), VisibleIndex = 7, Width = "110px" },
        new() { Caption = "Completed By", FieldName = nameof(PrPreventiveListRow.CompletedBy), VisibleIndex = 8, Width = "110px" },
        new() { Caption = "Remark", FieldName = nameof(PrPreventiveListRow.Remark), VisibleIndex = 9 }
    ];

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

    protected override async Task OnRowActionAsync(SelectedButtonInfo<PrPreventiveListRow> info)
    {
        if (info.SelectedRow is null)
        {
            await base.OnRowActionAsync(info);
            return;
        }
        if (string.Equals(info.SelectedButton.Text, "COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
            if (!string.Equals(info.SelectedRow.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Only PLANNED preventive rows can be completed.";
                return;
            }
            CompleteModel = new CompletePreventiveVm
            {
                Uid = info.SelectedRow.Uid,
                MachineCd = info.SelectedRow.MachineCd,
                ReasonCd = info.SelectedRow.ReasonCd,
                ActualStart = info.SelectedRow.DownDt.Date.Add(info.SelectedRow.StartTm.TimeOfDay),
                ActualEnd = DateTime.Now
            };
            CompletePopupVisible = true;
            ErrorMessage = null;
            return;
        }
        if (string.Equals(info.SelectedButton.Text, "CANCEL", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
            IsSubmitting = true;
            try
            {
                var result = await PreventiveService.CancelAsync(info.SelectedRow.Uid);
                if (result.Succeeded)
                {
                    StatusMessage = "Preventive cancelled.";
                    await ReloadListAsync();
                }
                else ErrorMessage = FormatPlanningResult(result);
            }
            finally { IsSubmitting = false; }
            return;
        }
        await base.OnRowActionAsync(info);
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
                    StartTm = x.StartTm,
                    EndTm = x.EndTm,
                    StartText = x.StartTm.ToString("HH:mm"),
                    EndText = x.EndTm.ToString("HH:mm"),
                    ReasonCd = x.ReasonCd,
                    Status = x.Status,
                    CompletedBy = x.CompletedBy,
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
        EditModel = new PrPreventiveEditVm { DownDt = DateTime.Today, StartText = "08:00", EndText = "17:00", Status = PreventiveStatuses.Planned };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrPreventiveListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        LoadEdit(row);
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = string.Equals(row.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase)
                          && await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrPreventiveListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (!string.Equals(row.Status, PreventiveStatuses.Planned, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Only PLANNED preventive rows can be edited.";
            return;
        }
        LoadEdit(row);
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    private void LoadEdit(PrPreventiveListRow row)
    {
        EditModel = new PrPreventiveEditVm
        {
            Uid = row.Uid,
            MachineCd = row.MachineCd,
            DownDt = row.DownDt,
            StartText = row.StartText,
            EndText = row.EndText,
            ReasonCd = row.ReasonCd,
            Remark = row.Remark,
            Status = row.Status
        };
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        if (!TimeOnly.TryParse(EditModel.StartText, out var start) || !TimeOnly.TryParse(EditModel.EndText, out var end))
        {
            ErrorMessage = "Start Time and End Time are required (HH:mm).";
            return;
        }
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var day = EditModel.DownDt.Date;
            var entity = new PrPreventive
            {
                Uid = IsEditMode ? EditModel.Uid : 0,
                MachineCd = EditModel.MachineCd,
                DownDt = day,
                StartTm = day.Add(start.ToTimeSpan()),
                EndTm = day.Add(end.ToTimeSpan()),
                ReasonCd = EditModel.ReasonCd,
                Remark = EditModel.Remark,
                Status = PreventiveStatuses.Planned
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

    protected async Task CompleteAsync()
    {
        if (IsSubmitting) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            var result = await MaintenanceService.CompletePreventiveAsync(CompleteModel.Uid, new CompletePreventiveRequest
            {
                ActualStart = CompleteModel.ActualStart,
                ActualEnd = CompleteModel.ActualEnd,
                ActionTaken = CompleteModel.ActionTaken,
                PartsCost = CompleteModel.PartsCost,
                LabourCost = CompleteModel.LabourCost,
                OtherCost = CompleteModel.OtherCost
            });
            if (result.Succeeded)
            {
                CompletePopupVisible = false;
                StatusMessage = result.Message ?? "Preventive completed.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        PreventiveService.SaveBatchAsync([], SelectedRows.Select(r => r.Uid).ToList());
}
