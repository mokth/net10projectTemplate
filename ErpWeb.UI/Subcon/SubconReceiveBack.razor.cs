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

    private const int DispatchLookupPageSize = 20;

    private bool _isDirty;

    protected bool IsSubmitting;
    protected bool LinePopupVisible;
    protected bool DiscardConfirmVisible;
    protected bool PostConfirmVisible;
    protected bool RollbackConfirmVisible;
    protected bool CanEditAfterView;
    protected bool CanPostPermission;
    protected bool CanRollbackPermission;
    protected string? StatusMessage;
    protected string DispatchLookupSearch = string.Empty;
    protected int DispatchLookupSkip;
    protected int DispatchLookupTotal;
    protected IReadOnlyList<SubconDispatchLineLookupRow> DispatchLookupRows { get; private set; } = [];
    protected HeaderVm Header { get; set; } = new();
    protected List<LineVm> Lines { get; set; } = [];
    protected LineVm DraftLine { get; set; } = new();

    protected bool IsNew => string.Equals(Mode, "new", StringComparison.OrdinalIgnoreCase);
    protected bool IsView => string.Equals(Mode, "view", StringComparison.OrdinalIgnoreCase);
    protected bool CanEditDocument => !IsView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanEditDocumentAfterView => CanEditAfterView && SubconStatuses.CanEdit(Header.Status);
    protected bool CanPostDocument => IsView && Header.CanPost && CanPostPermission && !IsSubmitting;
    protected bool CanRollbackDocument => IsView && Header.CanRollback && CanRollbackPermission && !IsSubmitting;
    protected bool CanSave => CanEditDocument && !IsSubmitting && Lines.Count > 0 && !string.IsNullOrWhiteSpace(Header.SupplierCode);
    protected string PageHeading => IsNew ? "Receive Back" : $"Receive Back {Header.DocNo}";
    protected string ModeChip => IsNew ? "New" : IsView ? "View" : "Edit";
    protected string DocNoDisplay => string.IsNullOrWhiteSpace(Header.DocNo) ? "(assigned on save)" : Header.DocNo;
    protected string LineCountLabel => Lines.Count == 1 ? "1 line" : $"{Lines.Count:N0} lines";

    protected override async Task OnPageInitializedAsync()
    {
        CanEditAfterView = await AccessRights.CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Edit);
        CanPostPermission =
            (await AccessRights.CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Post)
             || await AccessRights.CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Post))
            && await AccessRights.CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Post);
        CanRollbackPermission =
            (await AccessRights.CanAsync(MenuCodes.SubconReceiveBack, PermissionCodes.Rollback)
             || await AccessRights.CanAsync(MenuCodes.SubconWorkbench, PermissionCodes.Rollback))
            && await AccessRights.CanAsync(MenuCodes.InventoryStockTransfer, PermissionCodes.Rollback);
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
        _isDirty = false;
    }

    protected void OnTrxDateChanged(DateTime date)
    {
        Header.TrxDate = date;
        _isDirty = true;
    }

    protected void OnRemarksChanged(string? value)
    {
        Header.Remarks = value;
        _isDirty = true;
    }

    protected void OnSupplierSelected(PoSupplierLookupRow row)
    {
        Header.SupplierCode = row.SuppCode;
        Header.SupplierName = row.SuppName;
        DispatchLookupRows = [];
        DispatchLookupTotal = 0;
        _isDirty = true;
    }

    protected async Task OpenAddLinePopupAsync()
    {
        if (string.IsNullOrWhiteSpace(Header.SupplierCode))
        {
            ErrorMessage = SubconMessages.SupplierRequired;
            return;
        }

        ErrorMessage = null;
        DraftLine = new LineVm();
        DispatchLookupSearch = string.Empty;
        await LoadDispatchLookupAsync(0);
        LinePopupVisible = true;
    }

    protected async Task OnDispatchLookupSearchChanged(string value)
    {
        DispatchLookupSearch = value;
        await LoadDispatchLookupAsync(0);
    }

    protected async Task LoadDispatchLookupAsync(int skip)
    {
        var result = await Subcon.SearchDispatchLinesAsync(new SubconDispatchLineLookupQuery
        {
            SupplierCode = Header.SupplierCode,
            SearchText = DispatchLookupSearch,
            ExcludeReceiptDocNo = IsNew ? null : Header.DocNo,
            Skip = skip,
            Take = DispatchLookupPageSize
        });
        if (!result.Succeeded || result.DispatchLineLookup is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        DispatchLookupSkip = skip;
        DispatchLookupRows = result.DispatchLineLookup.Rows;
        DispatchLookupTotal = result.DispatchLineLookup.TotalCount;
    }

    protected void SelectDispatchLine(SubconDispatchLineLookupRow row)
    {
        var alreadyInDraft = Lines
            .Where(x => string.Equals(x.DispatchDocNo, row.DispatchDocNo, StringComparison.OrdinalIgnoreCase)
                        && x.DispatchLineNo == row.DispatchLineNo)
            .Sum(x => x.Qty);
        var availableNow = Math.Max(0m, row.RemainingQty - alreadyInDraft);
        DraftLine.DispatchDocNo = row.DispatchDocNo;
        DraftLine.DispatchLineNo = row.DispatchLineNo;
        DraftLine.Uom = row.Uom;
        DraftLine.RemainingQty = availableNow;
        DraftLine.Qty = availableNow;
    }

    protected void CommitLine()
    {
        if (string.IsNullOrWhiteSpace(DraftLine.DispatchDocNo) || DraftLine.DispatchLineNo <= 0 || DraftLine.Qty <= 0m)
        {
            ErrorMessage = SubconMessages.QtyRequired;
            return;
        }

        if (DraftLine.RemainingQty > 0m && DraftLine.Qty > DraftLine.RemainingQty)
        {
            ErrorMessage = $"You can receive only {DraftLine.RemainingQty:0.####} {DraftLine.Uom} on this dispatch.";
            return;
        }

        DraftLine.LineNo = Lines.Count == 0 ? 1 : Lines.Max(x => x.LineNo) + 1;
        Lines.Add(DraftLine);
        DraftLine = new LineVm();
        _isDirty = true;
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

        _isDirty = true;
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

        _isDirty = false;
        Navigation.NavigateTo($"/subcon/receive-back/view/{result.Receipt.DocNo}");
    }

    protected void OnCancel()
    {
        if (!IsView && _isDirty)
        {
            DiscardConfirmVisible = true;
            return;
        }

        Navigation.NavigateTo("/subcon/receive-back");
    }

    protected void ConfirmDiscard()
    {
        DiscardConfirmVisible = false;
        _isDirty = false;
        Navigation.NavigateTo("/subcon/receive-back");
    }

    protected async Task ConfirmPostAsync()
    {
        if (!CanPostDocument)
        {
            return;
        }

        PostConfirmVisible = false;
        ErrorMessage = null;
        StatusMessage = null;
        IsSubmitting = true;
        var result = await Subcon.PostReceiptAsync(new SubconKeyCommand
        {
            DocNo = Header.DocNo,
            RowVersion = Header.RowVersion,
            RequestId = Guid.NewGuid()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Receipt is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Header = HeaderVm.From(result.Receipt);
        Lines = result.Receipt.Lines.Select(LineVm.From).ToList();
        StatusMessage = result.InfoMessage ?? $"Posted receive-back {Header.DocNo}.";
    }

    protected async Task ConfirmRollbackAsync()
    {
        if (!CanRollbackDocument)
        {
            return;
        }

        RollbackConfirmVisible = false;
        ErrorMessage = null;
        StatusMessage = null;
        IsSubmitting = true;
        var result = await Subcon.RollbackReceiptAsync(new SubconKeyCommand
        {
            DocNo = Header.DocNo,
            RowVersion = Header.RowVersion,
            RequestId = Guid.NewGuid()
        });
        IsSubmitting = false;
        if (!result.Succeeded || result.Receipt is null)
        {
            ErrorMessage = result.ErrorMessage;
            return;
        }

        Header = HeaderVm.From(result.Receipt);
        Lines = result.Receipt.Lines.Select(LineVm.From).ToList();
        StatusMessage = result.InfoMessage ?? $"Rolled back receive-back {Header.DocNo}.";
    }

    protected sealed class HeaderVm
    {
        public string DocNo { get; set; } = string.Empty;
        public DateTime TrxDate { get; set; }
        public string Status { get; set; } = SubconStatuses.New;
        public string SupplierCode { get; set; } = string.Empty;
        public string? SupplierName { get; set; }
        public string? Remarks { get; set; }
        public int? PostedTrxBatchNo { get; set; }
        public Guid? PostedOperationId { get; set; }
        public bool CanPost { get; set; }
        public bool CanRollback { get; set; }
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
            PostedTrxBatchNo = dto.PostedTrxBatchNo,
            PostedOperationId = dto.PostedOperationId,
            CanPost = dto.CanPost,
            CanRollback = dto.CanRollback,
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
        public decimal RemainingQty { get; set; }
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
