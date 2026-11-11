namespace ErpWeb.Core.Subcon;

public sealed class SubconResult
{
    public bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public string? InfoMessage { get; init; }
    public SubconPolicyDto? Policy { get; init; }
    public SubconDispatchDto? Dispatch { get; init; }
    public SubconReceiptDto? Receipt { get; init; }
    public SubconListPage? ListPage { get; init; }
    public SubconWorkbenchPage? Workbench { get; init; }
    public bool CanUseProductionMode { get; init; }

    public static SubconResult Fail(string message) => new() { Succeeded = false, ErrorMessage = message };

    public static SubconResult OkPolicy(SubconPolicyDto policy, string? info = null) =>
        new() { Succeeded = true, Policy = policy, InfoMessage = info };

    public static SubconResult OkDispatch(SubconDispatchDto value, bool canUseProductionMode = false) =>
        new() { Succeeded = true, Dispatch = value, CanUseProductionMode = canUseProductionMode };

    public static SubconResult OkReceipt(SubconReceiptDto value) =>
        new() { Succeeded = true, Receipt = value };

    public static SubconResult OkList(SubconListPage page, bool canUseProductionMode = false) =>
        new() { Succeeded = true, ListPage = page, CanUseProductionMode = canUseProductionMode };

    public static SubconResult OkWorkbench(SubconWorkbenchPage page, bool canUseProductionMode = false) =>
        new() { Succeeded = true, Workbench = page, CanUseProductionMode = canUseProductionMode };
}

public sealed class SubconPolicyDto
{
    public int Version { get; init; }
    public bool SubconEnabled { get; init; }
    public bool InventoryModeEnabled { get; init; }
    public bool ProductionModeEnabled { get; init; }
    public bool IsPublished { get; init; }
    public byte[] RowVersion { get; init; } = [];
}

public sealed class SubconPolicySaveCommand
{
    public bool SubconEnabled { get; init; }
    public bool InventoryModeEnabled { get; init; }
    public bool ProductionModeEnabled { get; init; }
    public byte[]? RowVersion { get; init; }
}

public sealed record SubconListQuery
{
    public string? SearchText { get; set; }
    public string? Status { get; set; }
    public string? Mode { get; set; }
    public string? SupplierCode { get; set; }
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo { get; set; }
    public string Kind { get; set; } = SubconListKinds.Dispatch;
    public string? SortField { get; set; }
    public bool SortDescending { get; set; } = true;
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public static class SubconListKinds
{
    public const string Dispatch = "DISPATCH";
    public const string Receipt = "RECEIPT";
}

public sealed class SubconListPage
{
    public IReadOnlyList<SubconListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

public sealed class SubconListRow
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime TrxDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string SupplierCode { get; init; } = string.Empty;
    public string? SupplierName { get; init; }
    public string? ItemSummary { get; init; }
    public decimal SentQty { get; init; }
    public string? Uom { get; init; }
    public int LineCount { get; init; }
    public DateTime? PromisedReturnDate { get; init; }
    public string Kind { get; init; } = SubconListKinds.Dispatch;
}

public sealed class SubconWorkbenchPage
{
    public int OpenLineCount { get; init; }
    public int OverdueLineCount { get; init; }
    public int DraftCount { get; init; }
    public IReadOnlyList<string> OutstandingByUom { get; init; } = [];
    public IReadOnlyList<SubconListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
    public string PostingBanner { get; init; } = SubconBlockedReasons.PostingNotEnabled;
}

public sealed class SubconDispatchDto
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime TrxDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string Mode { get; init; } = string.Empty;
    public string SupplierCode { get; init; } = string.Empty;
    public string? SupplierName { get; init; }
    public DateTime? PromisedReturnDate { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }
    public string? Remarks { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SubconDispatchLineDto> Lines { get; init; } = [];
    public string PostBlockedReason { get; init; } = SubconBlockedReasons.NoAtomicInventorySeam;
}

public sealed class SubconDispatchLineDto
{
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemName { get; init; }
    public string Uom { get; init; } = string.Empty;
    public decimal Qty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public string? LotNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? OperationCode { get; init; }
    public DateTime? PromisedReturnDate { get; init; }
    public string? Remarks { get; init; }
}

public sealed class SubconDispatchSaveCommand
{
    public string? DocNo { get; init; }
    public DateTime TrxDate { get; init; }
    public string Mode { get; init; } = SubconModes.Inventory;
    public string SupplierCode { get; init; } = string.Empty;
    public DateTime? PromisedReturnDate { get; init; }
    public string? PoNo { get; init; }
    public short? PoRelNo { get; init; }
    public short? PoLineNo { get; init; }
    public string? Remarks { get; init; }
    public byte[]? RowVersion { get; init; }
    public IReadOnlyList<SubconDispatchLineSaveCommand> Lines { get; init; } = [];
}

public sealed class SubconDispatchLineSaveCommand
{
    public int LineNo { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? Uom { get; init; }
    public decimal Qty { get; init; }
    public string? WarehouseCode { get; init; }
    public string? LocationCode { get; init; }
    public string? LotNo { get; init; }
    public string? WorkOrderNo { get; init; }
    public string? OperationCode { get; init; }
    public DateTime? PromisedReturnDate { get; init; }
    public string? Remarks { get; init; }
}

public sealed class SubconReceiptDto
{
    public string DocNo { get; init; } = string.Empty;
    public DateTime TrxDate { get; init; }
    public string Status { get; init; } = string.Empty;
    public string SupplierCode { get; init; } = string.Empty;
    public string? SupplierName { get; init; }
    public string? Remarks { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public IReadOnlyList<SubconReceiptLineDto> Lines { get; init; } = [];
    public string PostBlockedReason { get; init; } = SubconBlockedReasons.NoAtomicInventorySeam;
}

public sealed class SubconReceiptLineDto
{
    public int LineNo { get; init; }
    public string DispatchDocNo { get; init; } = string.Empty;
    public int DispatchLineNo { get; init; }
    public decimal Qty { get; init; }
    public string Uom { get; init; } = string.Empty;
}

public sealed class SubconReceiptSaveCommand
{
    public string? DocNo { get; init; }
    public DateTime TrxDate { get; init; }
    public string SupplierCode { get; init; } = string.Empty;
    public string? Remarks { get; init; }
    public byte[]? RowVersion { get; init; }
    public IReadOnlyList<SubconReceiptLineSaveCommand> Lines { get; init; } = [];
}

public sealed class SubconReceiptLineSaveCommand
{
    public int LineNo { get; init; }
    public string DispatchDocNo { get; init; } = string.Empty;
    public int DispatchLineNo { get; init; }
    public decimal Qty { get; init; }
}

public sealed class SubconKeyCommand
{
    public string DocNo { get; init; } = string.Empty;
    public byte[]? RowVersion { get; init; }
}
