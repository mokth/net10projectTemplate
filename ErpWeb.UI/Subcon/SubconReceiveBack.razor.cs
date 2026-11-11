using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Core.Subcon;
using ErpWeb.UI.Components.Pages;
using Microsoft.AspNetCore.Components;

namespace ErpWeb.UI.Subcon;

public partial class SubconReceiveBack : PageBase
{
    [Parameter] public string Mode { get; set; } = "new";
    [Parameter] public string? DocNo { get; set; }

    [Inject] private ISubconApplicationService Subcon { get; set; } = default!;
    [Inject] private IAccessRightService AccessRights { get; set; } = default!;
    [Inject] private ICurrentDateService Clock { get; set; } = default!;

    protected bool IsSubmitting;
    protected bool LinePopupVisible;
    protected bool CanEditAfterView;
    protected HeaderVm Header { get; set; } = new();
    protected List<LineVm> Lines { get; set; } = [];
    protected LineVm DraftLine { get; set; } = new();

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument => !IsView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanEditDocumentAfterView => CanEditAfterView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0 && !string.IsNullOrWhiteSpace(Header.SupplierCode);
    protected string PageHeading => IsNew ? "Receive Back" : $"Receive Back {Header.DocNo}";
    protected string ModeChip => IsNew ? "New" : IsView ? "View" : "Edit";
    protected string DocNoDisplay => string.IsNullOrWhiteSpace(Header.DocNo) ? "(assigned on save)" : Header.DocNo;
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count:N0} lines";

    protected override async Task OnPageInitializedAsync()
    {
        CanEditAfterView = await AccessRights.CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Edit);
        if (IsNew)
        {
            Header = new HeaderVm { TrxDate = Clock.Today, Status = SubconStatuses.New };
            return;
        }

        if (string.IsNullOrWhiteSpace(DocNo))
        {
            ErrorMessage = SubconMessages.ReceiptNotFound;
            return;
        }

        var result = await Subcon.GetReceiptAsync(DocNo);
        if (!result.Succeeded || result.Receipt is null)
        {
            ErrorMessage = result.ErrorMessage ?? SubconMessages.ReceiptNotFound;
            return;
        }

        Header = HeaderVm.From(result.Receipt);
        Lines = result.Receipt.Lines.Select(LineVm.From).ToList();
    }

    protected void OnSupplierSelected(PoSupplierLookupRow row)
    {
        Header.SupplierCode = row.SuppCode;
        Header.SupplierName = row.SuppName;
    }

    protected void CommitLine()
    {
        if (string.IsNullOrWhiteSpace(DraftLine.DispatchDocNo) || DraftLine.DispatchLineNo <= 0 || DraftLine.Qty <= 0m)
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
        var result = await Subcon.SaveReceiptDraftAsync(new SubconReceiptSaveCommand
        {
            DocNo = IsNew ? null : Header.DocNo,
            TrxDate = Header.TrxDate,
            SupplierCode = Header.SupplierCode,
            Remarks = Header.Remarks,
            RowVersion = Header.RowVersion,
            Lines = Lines.Select(x => new SubconReceiptLineSaveCommand
            {
                LineNo = x.LineNo,
                DispatchDocNo = x.DispatchDocNo,
                DispatchLineNo = x.DispatchLineNo,
                Qty = x.Qty
            }).ToArray()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Receipt is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Navigation.NavigateTo($"/subcon/receive-back/view/{result.Receipt.DocNo}");
    }

    protected void OnCancel() => Navigation.NavigateTo("/subcon/receive-back");

    protected sealed class HeaderVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime TrxDate { get; set; }
        public string Status { get; set; } = SubconStatuses.New;
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public string? Remarks { get; set; }
        public byte[] RowVersion { get; set; } = [];
        public string PostBlockedReason { get; set; } = SubconBlockedReasons.NoAtomicInventorySeam;

        public static HeaderVm From(SubconReceiptDto dto) => new()
        {
            DocNo = dto.DocNo,
            TrxDate = dto.TrxDate,
            Status = dto.Status,
            SupplierCode = dto.SupplierCode,
            SupplierName = dto.SupplierName,
            Remarks = dto.Remarks,
            RowVersion = dto.RowVersion,
            PostBlockedReason = dto.PostBlockedReason
        };
    }

    protected sealed class LineVm
    {
        public int LineNo { get; set; }
        public string DispatchDocNo { get; set; } = string.Empty;
        public int DispatchLineNo { get; set; } = 1;
        public decimal Qty { get; set; }
        public string Uom { get; set; } = string.Empty;

        public static LineVm From(SubconReceiptLineDto dto) => new()
        {
            LineNo = dto.LineNo,
            DispatchDocNo = dto.DispatchDocNo,
            DispatchLineNo = dto.DispatchLineNo,
            Qty = dto.Qty,
            Uom = dto.Uom
        };
    }
}
