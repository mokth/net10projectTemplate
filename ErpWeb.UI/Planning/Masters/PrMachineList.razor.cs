using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrMachineListRow
{
    public string RowKey => $"{MachineCd}|{ProcessCd}";
    public string MachineCd { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double? ConversionTime { get; set; }
    public double? StartupTime { get; set; }
    public double? QueueTime { get; set; }
    public bool Active { get; set; } = true;
    public string? MachineType { get; set; }
    public string? SerialNo { get; set; }
    public decimal HourlyCost { get; set; }
    public DateTime? Updated { get; set; }
}

public sealed class PrProcessOption
{
    public string Code { get; set; } = string.Empty;
}

public partial class PrMachineList : PrRefListPageBase<PrMachineListRow>
{
    protected override string MenuCode => MenuCodes.PlanningWorkMachine;
    protected override string EntityLabel => "Machine";
    protected override string HeroIconClass => "fa-solid fa-robot";
    protected override string HeroTitle => "Machines";
    protected override bool SupportsActivate => true;

    [Inject] private IPrMachineService MachineService { get; set; } = default!;
    [Inject] private IPrProcessService ProcessService { get; set; } = default!;

    protected PrMachineEditVm EditModel { get; set; } = new();
    protected List<PrProcessOption> Processes { get; set; } = [];

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Machine", FieldName = nameof(PrMachineListRow.MachineCd), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Process", FieldName = nameof(PrMachineListRow.ProcessCd), VisibleIndex = 2, Width = "120px" },
        new() { Caption = "Description", FieldName = nameof(PrMachineListRow.Description), VisibleIndex = 3 },
        new() { Caption = "Type", FieldName = nameof(PrMachineListRow.MachineType), VisibleIndex = 4, Width = "110px" },
        new() { Caption = "Serial No", FieldName = nameof(PrMachineListRow.SerialNo), VisibleIndex = 5, Width = "120px" },
        new() { Caption = "Hourly Cost", FieldName = nameof(PrMachineListRow.HourlyCost), DataType = "decimal", DisplayFormat = "n2", VisibleIndex = 6, Width = "100px" },
        new() { Caption = "Active", FieldName = nameof(PrMachineListRow.Active), DataType = "bool", VisibleIndex = 7, Width = "80px" },
        new() { Caption = "Conversion", FieldName = nameof(PrMachineListRow.ConversionTime), DataType = "number", VisibleIndex = 8, Width = "100px" },
        new() { Caption = "Startup", FieldName = nameof(PrMachineListRow.StartupTime), DataType = "number", VisibleIndex = 9, Width = "90px" },
        new() { Caption = "Queue", FieldName = nameof(PrMachineListRow.QueueTime), DataType = "number", VisibleIndex = 10, Width = "90px" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        var p = await ProcessService.ListAsync();
        if (p.Succeeded)
            Processes = p.Value!.Select(x => x.ProcessCd).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(c => new PrProcessOption { Code = c }).ToList();
        await base.OnPageInitializedAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await MachineService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load machines.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrMachineListRow
                {
                    MachineCd = x.MachineCd,
                    ProcessCd = x.ProcessCd,
                    Description = x.MachineDes,
                    ConversionTime = x.ConversionTime,
                    StartupTime = x.StartupTime,
                    QueueTime = x.QueueTime,
                    Active = x.Active,
                    MachineType = x.MachineType,
                    SerialNo = x.SerialNo,
                    HourlyCost = x.HourlyCost,
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
        EditModel = new PrMachineEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            IsNew = true,
            Active = true,
            ConversionTime = 0,
            StartupTime = 0,
            QueueTime = 0,
            HourlyCost = 0
        };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrMachineListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        LoadEdit(row); IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrMachineListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        LoadEdit(row); IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    private void LoadEdit(PrMachineListRow row)
    {
        EditModel = new PrMachineEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            MachineCd = row.MachineCd,
            ProcessCd = row.ProcessCd,
            MachineDes = row.Description,
            ConversionTime = row.ConversionTime,
            StartupTime = row.StartupTime,
            QueueTime = row.QueueTime,
            Active = row.Active,
            MachineType = row.MachineType,
            SerialNo = row.SerialNo,
            HourlyCost = row.HourlyCost,
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
            // Process change on existing machine = Relocated semantics via IsNew=false + ProcessCd change handled in service
            var result = await MachineService.SaveBatchAsync([EditModel]);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Machine updated successfully." : "Machine added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        MachineService.SaveBatchAsync(SelectedRows.Select(r => new PrMachineEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            MachineCd = r.MachineCd,
            ProcessCd = r.ProcessCd,
            OriginalUpdated = r.Updated,
            IsDeleted = true
        }).ToList());

    protected override Task<PlanningServiceResult> SetActiveSelectedAsync(bool isActive) =>
        MachineService.SaveBatchAsync(SelectedRows.Select(r => new PrMachineEditVm
        {
            ClientNodeId = Guid.NewGuid().ToString("N"),
            MachineCd = r.MachineCd,
            ProcessCd = r.ProcessCd,
            MachineDes = r.Description,
            ConversionTime = r.ConversionTime,
            StartupTime = r.StartupTime,
            QueueTime = r.QueueTime,
            Active = isActive,
            MachineType = r.MachineType,
            SerialNo = r.SerialNo,
            HourlyCost = r.HourlyCost,
            OriginalUpdated = r.Updated,
            IsNew = false
        }).ToList());
}
