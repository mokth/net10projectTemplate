using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Subcon;

public partial class SubconSendOut : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;

    protected bool IsSubmitting;
    protected bool LinePopupVisible;
    protected bool CanUseProductionMode;
    protected bool CanEditAfterView;
    protected string? StatusMessage;
    protected HeaderVm Header { get; set; } = new();
    protected List<LineVm> Lines { get; set; } = [];
    protected LineVm DraftLine { get; set; } = new();

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument => !IsView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanEditDocumentAfterView => CanEditAfterView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0 && !string.IsNullOrWhiteSpace(Header.SupplierCode);
    protected string PageHeading => IsNew ? "Send Out" : $"Send Out {Header.DocNo}";
    protected string ModeChip => IsNew ? "New" : IsView ? "View" : "Edit";
    protected string DocNoDisplay => string.IsNullOrWhiteSpace(Header.DocNo) ? "(assigned on save)" : Header.DocNo;
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count:N0} lines";

    protected override async Task OnPageInitializedAsync()
    {
        CanEditAfterView = await AccessRights.CanAsync(MenuCodes.SubconSendOut, PermissionCodes.Edit);
        if (IsNew)
        {
            Header = new HeaderVm { TrxDate = Clock.Today, Status = SubconStatuses.New, Mode = SubconModes.Inventory };
            var probe = await Subcon.SearchAsync(new SubconListQuery { Take = 1 });
            CanUseProductionMode = probe.CanUseProductionMode;
            return;
        }

        if (string.IsNullOrWhiteSpace(DocNo))
        {
            ErrorMessage = SubconMessages.DispatchNotFound;
            return;
        }

        var result = await Subcon.GetDispatchAsync(DocNo);
        if (!result.Succeeded || result.Dispatch is null)
        {
            ErrorMessage = result.ErrorMessage ?? SubconMessages.DispatchNotFound;
            return;
        }

        CanUseProductionMode = result.CanUseProductionMode;
        Header = HeaderVm.From(result.Dispatch);
        Lines = result.Dispatch.Lines.Select(LineVm.From).ToList();
    }

    protected void OnSupplierSelected(PoSupplierLookupRow row)
    {
        Header.SupplierCode = row.SuppCode;
        Header.SupplierName = row.SuppName;
    }

    protected void OnItemSelected(IvStockMasterLookupRow row)
    {
        DraftLine.ItemCode = row.ICode;
        DraftLine.ItemName = row.IDesc;
        DraftLine.Uom = row.StdUom ?? DraftLine.Uom;
        DraftLine.WarehouseCode = row.DefWarehouse ?? DraftLine.WarehouseCode;
        DraftLine.LocationCode = row.DefLocation ?? DraftLine.LocationCode;
    }

    protected void CommitLine()
    {
        if (string.IsNullOrWhiteSpace(DraftLine.ItemCode) || DraftLine.Qty <= 0m)
        {
            ErrorMessage = SubconMessages.QtyRequired;
            return;
        }

        DraftLine.LineNo = Lines.Count + 1;
        Lines.Add(DraftLine);
        DraftLine = new LineVm();
        LinePopupVisible = false;
    }

    protected void RemoveLine(LineVm line)
    {
        Lines.Remove(line);
        var n = 1;
        foreach (var remaining in Lines)
        {
            remaining.LineNo = n++;
        }
    }

    protected async Task OnSaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSubmitting = true;
        var result = await Subcon.SaveDispatchDraftAsync(new SubconDispatchSaveCommand
        {
            DocNo = IsNew ? null : Header.DocNo,
            TrxDate = Header.TrxDate,
            Mode = SubconModes.Inventory,
            SupplierCode = Header.SupplierCode,
            PromisedReturnDate = Header.PromisedReturnDate,
            PoNo = Header.PoNo,
            Remarks = Header.Remarks,
            RowVersion = Header.RowVersion,
            Lines = Lines.Select(x => new SubconDispatchLineSaveCommand
            {
                LineNo = x.LineNo,
                ItemCode = x.ItemCode,
                Uom = x.Uom,
                Qty = x.Qty,
                WarehouseCode = x.WarehouseCode,
                LocationCode = x.LocationCode,
                Remarks = x.Remarks
            }).ToArray()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Dispatch is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Navigation.NavigateTo($"/subcon/send-out/view/{result.Dispatch.DocNo}");
    }

    protected void OnCancel() => Navigation.NavigateTo("/subcon/send-out");

    protected sealed class HeaderVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime TrxDate { get; set; }
        public DateTime? PromisedReturnDate { get; set; }
        public string Status { get; set; } = SubconStatuses.New;
        public string Mode { get; set; } = SubconModes.Inventory;
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public string? PoNo { get; set; }
        public string? Remarks { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string PostBlockedReason { get; set; } = SubconBlockedReasons.NoAtomicInventorySeam;

        public static HeaderVm From(SubconDispatchDto dto) => new()
        {
            DocNo = dto.DocNo,
            TrxDate = dto.TrxDate,
            PromisedReturnDate = dto.PromisedReturnDate,
            Status = dto.Status,
            Mode = dto.Mode,
            SupplierCode = dto.SupplierCode,
            SupplierName = dto.SupplierName,
            PoNo = dto.PoNo,
            Remarks = dto.Remarks,
            RowVersion = dto.RowVersion,
            PostBlockedReason = dto.PostBlockedReason
        };
    }

    protected sealed class LineVm
    {
        public int LineNo { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string? ItemName { get; set; }
        public string? WarehouseCode { get; set; }
        public string? LocationCode { get; set; }
        public decimal Qty { get; set; }
        public string Uom { get; set; } = string.Empty;
        public string? Remarks { get; set; }

        public static LineVm From(SubconDispatchLineDto dto) => new()
        {
            LineNo = dto.LineNo,
            ItemCode = dto.ItemCode,
            ItemName = dto.ItemName,
            WarehouseCode = dto.WarehouseCode,
            LocationCode = dto.LocationCode,
            Qty = dto.Qty,
            Uom = dto.Uom,
            Remarks = dto.Remarks
        };
    }
}
