using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Planning.Masters;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Maintenance;

public sealed class PrMachineMaintenanceListRow
{
    public int Id { get; set; }
    public DateTime? TrxDate { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public string? MType { get; set; }
    public string? ReasonCd { get; set; }
    public string? Description { get; set; }
    public DateTime? StartDateTime { get; set; }
    public DateTime? EndDateTime { get; set; }
    public decimal? Hours { get; set; }
    public string Status { get; set; } = PrMaintenanceStatuses.Open;
    public string? ActionBy { get; set; }
    public decimal PartsCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }
    public decimal Total => PartsCost + LabourCost + OtherCost;
    public string? ReportBy { get; set; }
    public string? ActionTaken { get; set; }
    public string? Remark { get; set; }
}

public sealed class PrMachineMaintenanceEditVm
{
    public int Id { get; set; }
    public string MachineCd { get; set; } = string.Empty;
    public string MType { get; set; } = PrMaintenanceTypes.Breakdown;
    public DateTime TrxDate { get; set; } = DateTime.Today;
    public string? ReasonCd { get; set; }
    public string Description { get; set; } = string.Empty;
    public string? ReportBy { get; set; }
    public DateTime? StartDateTime { get; set; }
    public DateTime? EndDateTime { get; set; }
    public string? ActionTaken { get; set; }
    public string? ActionBy { get; set; }
    public string Status { get; set; } = PrMaintenanceStatuses.Open;
    public decimal PartsCost { get; set; }
    public decimal LabourCost { get; set; }
    public decimal OtherCost { get; set; }
    public string? Remark { get; set; }
}

public sealed class MachineCdOption
{
    public string MachineCd { get; set; } = string.Empty;
}

public partial class PrMachineMaintenanceList : PrRefListPageBase<PrMachineMaintenanceListRow>
{
    protected override string MenuCode => MenuCodes.PlanningMachineMaintenance;
    protected override string EntityLabel => "Maintenance";
    protected override string HeroIconClass => "fa-solid fa-wrench";
    protected override string HeroTitle => "Machine Maintenance";

    [Inject] private IPrMachineMaintenanceService MaintenanceService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;
    [Inject] private IPrMaintenanceReasonService ReasonService { get; set; } = default!;

    protected PrMachineMaintenanceEditVm EditModel { get; set; } = new();
    protected List<MachineCdOption> FilterMachines { get; set; } = [];
    protected List<MachineCdOption> ActiveMachines { get; set; } = [];
    protected List<PrMaintenanceReason> Reasons { get; set; } = [];
    protected string[] MaintenanceTypes { get; } = PrMaintenanceTypes.All;
    protected string[] MaintenanceStatuses { get; } = PrMaintenanceStatuses.All;

    protected DateTime? FilterFrom { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
    protected DateTime? FilterTo { get; set; } = DateTime.Today;
    protected string? FilterMachineCd { get; set; }
    protected string? FilterType { get; set; }
    protected string? FilterStatus { get; set; }
    protected string? FilterReasonCd { get; set; }

    protected List<MachineCdOption> EditMachineOptions
    {
        get
        {
            if (string.IsNullOrWhiteSpace(EditModel.MachineCd)
                || ActiveMachines.Any(m => string.Equals(m.MachineCd, EditModel.MachineCd, StringComparison.OrdinalIgnoreCase)))
                return ActiveMachines;

            return ActiveMachines
                .Append(new MachineCdOption { MachineCd = EditModel.MachineCd })
                .OrderBy(m => m.MachineCd, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    protected override List<ButtonInfo> BuildToolbarButtons() =>
    [
        new() { IConClass = "fas fa-plus", Style = "primary", Text = "NEW" },
        new() { IConClass = "fa-solid fa-rotate", Style = "secondary", Text = "REFRESH", ToolTip = "Refresh" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        if (RowActionButtons.All(b => !string.Equals(b.Text, "COMPLETE", StringComparison.OrdinalIgnoreCase)))
        {
            RowActionButtons.Add(new() { IConClass = "fa-solid fa-check", Style = "success", Text = "COMPLETE", ToolTip = "Complete" });
            RowActionButtons.Add(new() { IConClass = "fa-solid fa-ban", Style = "warning", Text = "CANCEL", ToolTip = "Cancel" });
        }

        var m = await MachineService.ListAsync();
        if (m.Succeeded)
        {
            var all = m.Value!
                .Select(x => x.MachineCd)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(c => new MachineCdOption { MachineCd = c })
                .ToList();
            FilterMachines = all;
            ActiveMachines = m.Value!
                .Where(x => x.Active)
                .Select(x => x.MachineCd)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(c => new MachineCdOption { MachineCd = c })
                .ToList();
        }

        var r = await ReasonService.ListAsync(activeOnly: true);
        if (r.Succeeded) Reasons = r.Value!.ToList();

        await base.OnPageInitializedAsync();
    }

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Date", FieldName = nameof(PrMachineMaintenanceListRow.TrxDate), DataType = "date", VisibleIndex = 1, Width = "110px", SortIndex = 0, SortOrder = DevExpress.Blazor.GridColumnSortOrder.Descending },
        new() { Caption = "Machine", FieldName = nameof(PrMachineMaintenanceListRow.MachineCd), VisibleIndex = 2, Width = "110px" },
        new() { Caption = "Type", FieldName = nameof(PrMachineMaintenanceListRow.MType), VisibleIndex = 3, Width = "110px" },
        new() { Caption = "Reason", FieldName = nameof(PrMachineMaintenanceListRow.ReasonCd), VisibleIndex = 4, Width = "100px" },
        new() { Caption = "Description", FieldName = nameof(PrMachineMaintenanceListRow.Description), VisibleIndex = 5 },
        new() { Caption = "Start", FieldName = nameof(PrMachineMaintenanceListRow.StartDateTime), DataType = "time", DisplayFormat = "dd/MM/yyyy HH:mm", VisibleIndex = 6, Width = "140px" },
        new() { Caption = "End", FieldName = nameof(PrMachineMaintenanceListRow.EndDateTime), DataType = "time", DisplayFormat = "dd/MM/yyyy HH:mm", VisibleIndex = 7, Width = "140px" },
        new() { Caption = "Hours", FieldName = nameof(PrMachineMaintenanceListRow.Hours), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 8, Width = "80px" },
        new() { Caption = "Status", FieldName = nameof(PrMachineMaintenanceListRow.Status), VisibleIndex = 9, Width = "110px" },
        new() { Caption = "Action By", FieldName = nameof(PrMachineMaintenanceListRow.ActionBy), VisibleIndex = 10, Width = "100px" },
        new() { Caption = "Parts", FieldName = nameof(PrMachineMaintenanceListRow.PartsCost), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 11, Width = "90px" },
        new() { Caption = "Labour", FieldName = nameof(PrMachineMaintenanceListRow.LabourCost), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 12, Width = "90px" },
        new() { Caption = "Other", FieldName = nameof(PrMachineMaintenanceListRow.OtherCost), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 13, Width = "90px" },
        new() { Caption = "Total", FieldName = nameof(PrMachineMaintenanceListRow.Total), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 14, Width = "90px" }
    ];

    protected async Task SearchAsync() => await ReloadListAsync();

    protected async Task ClearFiltersAsync()
    {
        FilterFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        FilterTo = DateTime.Today;
        FilterMachineCd = null;
        FilterType = null;
        FilterStatus = null;
        FilterReasonCd = null;
        await ReloadListAsync();
    }

    protected override async Task OnRowActionAsync(SelectedButtonInfo<PrMachineMaintenanceListRow> info)
    {
        if (info.SelectedRow is null)
        {
            await base.OnRowActionAsync(info);
            return;
        }

        if (string.Equals(info.SelectedButton.Text, "COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
            if (string.Equals(info.SelectedRow.Status, PrMaintenanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "Cancelled maintenance cannot be completed.";
                return;
            }
            if (string.Equals(info.SelectedRow.Status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Already completed.";
                return;
            }

            IsSubmitting = true;
            ErrorMessage = null;
            try
            {
                var result = await MaintenanceService.CompleteAsync(info.SelectedRow.Id);
                if (result.Succeeded)
                {
                    StatusMessage = result.Message ?? "Maintenance completed.";
                    await ReloadListAsync();
                }
                else ErrorMessage = FormatPlanningResult(result);
            }
            finally { IsSubmitting = false; }
            return;
        }

        if (string.Equals(info.SelectedButton.Text, "CANCEL", StringComparison.OrdinalIgnoreCase))
        {
            if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
            if (string.Equals(info.SelectedRow.Status, PrMaintenanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Already cancelled.";
                return;
            }

            IsSubmitting = true;
            ErrorMessage = null;
            try
            {
                var result = await MaintenanceService.CancelAsync(info.SelectedRow.Id);
                if (result.Succeeded)
                {
                    StatusMessage = result.Message ?? "Maintenance cancelled.";
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
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var result = await MaintenanceService.ListAsync(new PrMachineMaintenanceFilter
            {
                FromDate = FilterFrom,
                ToDate = FilterTo,
                MachineCd = FilterMachineCd,
                MaintenanceType = FilterType,
                Status = FilterStatus,
                ReasonCd = FilterReasonCd
            });

            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load maintenance rows.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(MapRow).ToList();
            }

            SelectedRows.Clear();
            Grid?.Reload();
        }
        finally { IsLoading = false; }
    }

    private static PrMachineMaintenanceListRow MapRow(PrMacMaintenance x)
    {
        decimal? hours = null;
        if (x.StartDateTime is not null && x.EndDateTime is not null && x.EndDateTime > x.StartDateTime)
            hours = (decimal)(x.EndDateTime.Value - x.StartDateTime.Value).TotalHours;

        return new PrMachineMaintenanceListRow
        {
            Id = x.Id,
            TrxDate = x.TrxDate,
            MachineCd = x.MacCode ?? string.Empty,
            MType = x.MType,
            ReasonCd = x.ReasonCd,
            Description = x.Description,
            StartDateTime = x.StartDateTime,
            EndDateTime = x.EndDateTime,
            Hours = hours,
            Status = x.Status ?? PrMaintenanceStatuses.Open,
            ActionBy = x.ActionBy,
            PartsCost = x.PartsCost,
            LabourCost = x.LabourCost,
            OtherCost = x.OtherCost,
            ReportBy = x.ReportBy,
            ActionTaken = x.ActionTaken,
            Remark = x.Remark
        };
    }

    protected override async Task OnNewClickAsync()
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Add)) return;
        EditModel = new PrMachineMaintenanceEditVm
        {
            TrxDate = DateTime.Today,
            Status = PrMaintenanceStatuses.Open,
            MType = PrMaintenanceTypes.Breakdown
        };
        ErrorMessage = null;
        IsEditMode = false;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrMachineMaintenanceListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        LoadEdit(row);
        ErrorMessage = null;
        IsEditMode = true;
        EditEnabled = false;
        CanEditFromView = !IsTerminalStatus(row.Status)
                          && await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit);
        PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrMachineMaintenanceListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        if (IsTerminalStatus(row.Status))
        {
            ErrorMessage = "Completed or cancelled maintenance cannot be edited.";
            return;
        }
        LoadEdit(row);
        ErrorMessage = null;
        IsEditMode = true;
        EditEnabled = true;
        CanEditFromView = false;
        PopupVisible = true;
    }

    private void LoadEdit(PrMachineMaintenanceListRow row)
    {
        EditModel = new PrMachineMaintenanceEditVm
        {
            Id = row.Id,
            MachineCd = row.MachineCd,
            MType = row.MType ?? PrMaintenanceTypes.Other,
            TrxDate = row.TrxDate?.Date ?? DateTime.Today,
            ReasonCd = row.ReasonCd,
            Description = row.Description ?? string.Empty,
            ReportBy = row.ReportBy,
            StartDateTime = row.StartDateTime,
            EndDateTime = row.EndDateTime,
            ActionTaken = row.ActionTaken,
            ActionBy = row.ActionBy,
            Status = row.Status,
            PartsCost = row.PartsCost,
            LabourCost = row.LabourCost,
            OtherCost = row.OtherCost,
            Remark = row.Remark
        };
    }

    private static bool IsTerminalStatus(string? status) =>
        string.Equals(status, PrMaintenanceStatuses.Completed, StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, PrMaintenanceStatuses.Cancelled, StringComparison.OrdinalIgnoreCase);

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true;
        ErrorMessage = null;
        try
        {
            var entity = new PrMacMaintenance
            {
                Id = EditModel.Id,
                MacCode = EditModel.MachineCd,
                MType = EditModel.MType,
                TrxDate = EditModel.TrxDate.Date,
                ReasonCd = EditModel.ReasonCd,
                Description = EditModel.Description,
                ReportBy = EditModel.ReportBy,
                StartDateTime = EditModel.StartDateTime,
                EndDateTime = EditModel.EndDateTime,
                ActionTaken = EditModel.ActionTaken,
                ActionBy = EditModel.ActionBy,
                Status = EditModel.Status,
                PartsCost = EditModel.PartsCost,
                LabourCost = EditModel.LabourCost,
                OtherCost = EditModel.OtherCost,
                Remark = EditModel.Remark
            };

            if (IsEditMode)
            {
                var result = await MaintenanceService.UpdateAsync(entity);
                if (result.Succeeded)
                {
                    PopupVisible = false;
                    StatusMessage = result.Message ?? "Maintenance updated successfully.";
                    await ReloadListAsync();
                }
                else ErrorMessage = FormatPlanningResult(result);
            }
            else
            {
                var result = await MaintenanceService.CreateAsync(entity);
                if (result.Succeeded)
                {
                    PopupVisible = false;
                    StatusMessage = result.Message ?? "Maintenance added successfully.";
                    await ReloadListAsync();
                }
                else ErrorMessage = FormatPlanningResult(result);
            }
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        Task.FromResult(PlanningServiceResult.Fail(
            PlanningErrorCode.ValidationFailed,
            "Delete is not supported. Cancel the record instead."));
}
