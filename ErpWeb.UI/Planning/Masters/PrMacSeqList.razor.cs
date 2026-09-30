using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Security;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.UI.Components.Common.DataGrid;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Planning.Masters;

public sealed class PrMacSeqListRow
{
    public string RowKey => $"{MachineCd}|{ProcessCd}";
    public string MachineCd { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public int? SeqNo { get; set; }
}

public sealed class PrMacSeqEditVm
{
    public string MachineCd { get; set; } = string.Empty;
    public string ProcessCd { get; set; } = string.Empty;
    public int? SeqNo { get; set; }
}

public partial class PrMacSeqList : PrRefListPageBase<PrMacSeqListRow>
{
    protected override string MenuCode => MenuCodes.PlanningMacSeq;
    protected override string EntityLabel => "Machine Sequence";
    protected override string HeroIconClass => "fa-solid fa-list-ol";
    protected override string HeroTitle => "Machine Sequences";

    [Inject] private IPrMacSeqService MacSeqService { get; set; } = default!;
    [Inject] private IPrMachineService MachineService { get; set; } = default!;

    protected PrMacSeqEditVm EditModel { get; set; } = new();
    protected List<PrMachine> Machines { get; set; } = [];

    public List<GridColumnData> Columns() =>
    [
        new() { Caption = "Machine", FieldName = nameof(PrMacSeqListRow.MachineCd), VisibleIndex = 1, Width = "120px", SortIndex = 0 },
        new() { Caption = "Process", FieldName = nameof(PrMacSeqListRow.ProcessCd), VisibleIndex = 2, Width = "120px" },
        new() { Caption = "Seq No", FieldName = nameof(PrMacSeqListRow.SeqNo), DataType = "number", VisibleIndex = 3, Width = "90px" }
    ];

    protected override async Task OnPageInitializedAsync()
    {
        var m = await MachineService.ListAsync();
        if (m.Succeeded) Machines = m.Value!.ToList();
        await base.OnPageInitializedAsync();
    }

    protected override async Task ReloadListAsync()
    {
        IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await MacSeqService.ListAsync();
            if (!result.Succeeded)
            {
                StatusMessage = result.Message ?? "Unable to load machine sequences.";
                Data = [];
            }
            else
            {
                Data = result.Value!.Select(x => new PrMacSeqListRow
                {
                    MachineCd = x.MachineCd,
                    ProcessCd = x.ProcessCd,
                    SeqNo = x.SeqNo
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
        EditModel = new PrMacSeqEditVm { SeqNo = 10 };
        ErrorMessage = null; IsEditMode = false; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected override async Task OnViewClickAsync(PrMacSeqListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Access)) return;
        EditModel = new PrMacSeqEditVm { MachineCd = row.MachineCd, ProcessCd = row.ProcessCd, SeqNo = row.SeqNo };
        ErrorMessage = null; IsEditMode = true; EditEnabled = false;
        CanEditFromView = await AccessRights.CanAsync(MenuCode, PermissionCodes.Edit); PopupVisible = true;
    }

    protected override async Task OnEditClickAsync(PrMacSeqListRow row)
    {
        if (!await EnsurePermissionAsync(PermissionCodes.Edit)) return;
        EditModel = new PrMacSeqEditVm { MachineCd = row.MachineCd, ProcessCd = row.ProcessCd, SeqNo = row.SeqNo };
        ErrorMessage = null; IsEditMode = true; EditEnabled = true; CanEditFromView = false; PopupVisible = true;
    }

    protected async Task HandleValidSubmitAsync()
    {
        if (IsSubmitting || !EditEnabled) return;
        IsSubmitting = true; ErrorMessage = null;
        try
        {
            // Sync process from selected machine when creating
            if (!IsEditMode)
            {
                var mac = Machines.FirstOrDefault(m => string.Equals(m.MachineCd, EditModel.MachineCd, StringComparison.OrdinalIgnoreCase));
                if (mac is not null) EditModel.ProcessCd = mac.ProcessCd;
            }
            var entity = new PrMacSeq { MachineCd = EditModel.MachineCd, ProcessCd = EditModel.ProcessCd, SeqNo = EditModel.SeqNo };
            var result = await MacSeqService.SaveBatchAsync([entity], []);
            if (result.Succeeded)
            {
                PopupVisible = false;
                StatusMessage = IsEditMode ? "Machine Sequence updated successfully." : "Machine Sequence added successfully.";
                await ReloadListAsync();
            }
            else ErrorMessage = FormatPlanningResult(result);
        }
        finally { IsSubmitting = false; }
    }

    protected override Task<PlanningServiceResult> DeleteSelectedAsync() =>
        MacSeqService.SaveBatchAsync([], SelectedRows.Select(r => (r.MachineCd, r.ProcessCd)).ToList());
}
