using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Common.DataGrid;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Subcon;

public partial class SubconSendOutList : PageBase
{
    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;

    protected bool IsBootstrapping = true;
    protected bool CanAdd;
    protected int TotalCount;
    protected SubconWorkbenchGridDataSource DataSource { get; private set; } = default!;
    protected string TotalCountLabel => TotalCount == 1 ? "1 draft" : $"{TotalCount:N0} drafts";

    protected List<GridColumnData> Columns { get; } =
    [
        new() { Caption = "Document", FieldName = nameof(SubconListRow.DocNo), VisibleIndex = 1, Size = GridColumnSize.DocumentNo },
        new() { Caption = "Date", FieldName = nameof(SubconListRow.TrxDate), DataType = "date", DisplayFormat = "dd/MM/yyyy", VisibleIndex = 2, Size = GridColumnSize.Date },
        new() { Caption = "Supplier", FieldName = nameof(SubconListRow.SupplierName), VisibleIndex = 3, Size = GridColumnSize.Name },
        new() { Caption = "Lines", FieldName = nameof(SubconListRow.LineCount), VisibleIndex = 4, Size = GridColumnSize.Tiny },
        new() { Caption = "Status", FieldName = nameof(SubconListRow.Status), VisibleIndex = 5, Size = GridColumnSize.Status }
    ];

    protected List<ButtonInfo> Buttons { get; set; } = [];
    protected List<ButtonInfo> ActionButtons { get; set; } = [];

    protected override async Task OnPageInitializedAsync()
    {
        DataSource = new SubconWorkbenchGridDataSource(SearchAsync);
        CanAdd = await AccessRights.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Add);
        Buttons = [new() { Text = "NEW", IConClass = "fas fa-plus", Style = "primary", Enabled = CanAdd }];
        ActionButtons =
        [
            new() { Text = "VIEW", IConClass = "fa-regular fa-eye", Style = "primary" },
            new() { Text = "EDIT", IConClass = "far fa-edit", Style = "primary", Enabled = await AccessRights.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Edit) }
        ];
        var page = await Subcon.SearchAsync(new SubconListQuery { Take = 1 });
        TotalCount = page.ListPage?.TotalCount ?? 0;
        IsBootstrapping = false;
    }

    protected Task OnButtonClick(SelectedButtonInfo<SubconListRow> info)
    {
        if ((info.SelectedButton.Text ?? string.Empty).Equals("NEW", StringComparison.OrdinalIgnoreCase))
        {
            Navigation.NavigateTo("/subcon/send-out/new");
        }

        return Task.CompletedTask;
    }

    protected Task OnActionClick(SelectedButtonInfo<SubconListRow> info)
    {
        if (info.SelectedRow is null)
        {
            return Task.CompletedTask;
        }

        var edit = (info.SelectedButton.Text ?? string.Empty).Equals("EDIT", StringComparison.OrdinalIgnoreCase);
        Navigation.NavigateTo(edit
            ? $"/subcon/send-out/edit/{info.SelectedRow.DocNo}"
            : $"/subcon/send-out/view/{info.SelectedRow.DocNo}");
        return Task.CompletedTask;
    }

    private async Task<(IReadOnlyList<SubconListRow> Rows, int TotalCount)> SearchAsync(
        SubconListQuery query, CancellationToken cancellationToken)
    {
        var result = await Subcon.SearchAsync(query with { Kind = SubconListKinds.Dispatch }, cancellationToken);
        TotalCount = result.ListPage?.TotalCount ?? 0;
        return result.ListPage is null ? ([], 0) : (result.ListPage.Rows, result.ListPage.TotalCount);
    }
}
