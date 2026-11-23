namespace ErpWeb.Model.Entities.Subcon;

/// <summary>Company-level Subcon flags. All live capability flags stay off by default.</summary>
public class SubconPolicy
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool SubconEnabled { get; set; }
    public bool InventoryModeEnabled { get; set; }
    public bool ProductionModeEnabled { get; set; }
    public bool InventoryCustodyPostEnabled { get; set; }
    public bool LotReturnEnabled { get; set; }
    public bool BomConversionEnabled { get; set; }
    public bool ProductionWipEnabled { get; set; }
    public bool FeeCapitalizationEnabled { get; set; }
    public string? DefaultCustodyWarehouseCode { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Send-out document (NEW draft or POSTED via atomic Inventory TR).</summary>
public class SubconDispatch
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public long? JobId { get; set; }
    public DateTime TrxDate { get; set; }
    public string Status { get; set; } = "NEW";
    public string Mode { get; set; } = "INVENTORY";
    public string SupplierCode { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public DateTime? PromisedReturnDate { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
    public string? Remarks { get; set; }
    public int DocumentRevision { get; set; }
    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public int PostedCount { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }
    public int RollbackCount { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SubconJob? Job { get; set; }
    public ICollection<SubconDispatchLine> Lines { get; set; } = [];
    public ICollection<SubconReceiptLine> ReceiptAllocations { get; set; } = [];
}

public class SubconDispatchLine
{
    public long Uid { get; set; }
    public long DispatchId { get; set; }
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string? BaseUom { get; set; }
    public decimal Qty { get; set; }
    public decimal? BaseQty { get; set; }
    public decimal? ConvFactor { get; set; }
    public string? StockStatus { get; set; }
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public string? LotNo { get; set; }
    public int? IvBalLocId { get; set; }
    public string? CustodyWarehouseCode { get; set; }
    public string? CustodyLocationCode { get; set; }
    public int? CustodyBalLocId { get; set; }
    public long? ProductionBalLotUid { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? OperationCode { get; set; }
    public DateTime? PromisedReturnDate { get; set; }
    public string? Remarks { get; set; }

    public SubconDispatch Dispatch { get; set; } = null!;
    public ICollection<SubconReceiptLine> ReceiptAllocations { get; set; } = [];
}

/// <summary>Receive-back document (NEW draft or POSTED via atomic Inventory TR).</summary>
public class SubconReceipt
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
    public DateTime TrxDate { get; set; }
    public string Status { get; set; } = "NEW";
    public string SupplierCode { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public string? Remarks { get; set; }
    public int DocumentRevision { get; set; }
    public DateTime? PostedDate { get; set; }
    public string? PostedBy { get; set; }
    public int PostedCount { get; set; }
    public DateTime? RollbackDate { get; set; }
    public string? RollbackBy { get; set; }
    public int RollbackCount { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SubconReceiptLine> Lines { get; set; } = [];
}

public class SubconReceiptLine
{
    public long Uid { get; set; }
    public long ReceiptId { get; set; }
    public int LineNo { get; set; }
    public long DispatchId { get; set; }
    public long DispatchLineId { get; set; }
    public decimal Qty { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal? BaseQty { get; set; }
    public decimal? ConvFactor { get; set; }
    public string? ToWarehouseCode { get; set; }
    public string? ToLocationCode { get; set; }
    public int? ToBalLocId { get; set; }
    public string? Remarks { get; set; }

    public SubconReceipt Receipt { get; set; } = null!;
    public SubconDispatch Dispatch { get; set; } = null!;
    public SubconDispatchLine DispatchLine { get; set; } = null!;
}

/// <summary>Phase G2 Subcon Job header (PROCESSING, ASSEMBLY, or RETURN_MATERIAL). Draft-only until G3–G5 adapters are enabled.</summary>
public class SubconJob
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string JobNo { get; set; } = string.Empty;
    public DateTime TrxDate { get; set; }
    public string Status { get; set; } = "NEW";
    public string JobKind { get; set; } = "PROCESSING";
    public string Mode { get; set; } = "INVENTORY";
    public string SupplierCode { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public string? TargetItemCode { get; set; }
    public string? TargetItemName { get; set; }
    public string? TargetUom { get; set; }
    public decimal TargetQty { get; set; }
    public string? DefinitionCode { get; set; }
    public int? BomVersion { get; set; }
    public bool IsManualKit { get; set; }
    public string? PrimaryDispatchDocNo { get; set; }
    public DateTime? PromisedReturnDate { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
    public string? Remarks { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<SubconJobSourceSnapshot> SourceSnapshots { get; set; } = [];
    public ICollection<SubconJobMaterial> Materials { get; set; } = [];
    public ICollection<SubconJobOutput> Outputs { get; set; } = [];
    public ICollection<SubconSupplierAssignment> SupplierAssignments { get; set; } = [];
}

public class SubconJobSourceSnapshot
{
    public long Uid { get; set; }
    public long JobId { get; set; }
    public string SourceKind { get; set; } = "BOM";
    public string ProdCode { get; set; } = string.Empty;
    public string? DefinitionCode { get; set; }
    public int? BomVersion { get; set; }
    public long? BomHdrUid { get; set; }
    public decimal RootQty { get; set; }
    public string? RootUom { get; set; }
    public DateTime CapturedDate { get; set; }
    public string CapturedBy { get; set; } = string.Empty;

    public SubconJob Job { get; set; } = null!;
}

public class SubconJobMaterial
{
    public long Uid { get; set; }
    public long JobId { get; set; }
    public int LineNo { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string Uom { get; set; } = string.Empty;
    public string? BaseUom { get; set; }
    public decimal RequiredQty { get; set; }
    public decimal SendNowQty { get; set; }
    public decimal ScrapPercent { get; set; }
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public string? LotNo { get; set; }
    public int? IvBalLocId { get; set; }
    public bool IsManualAddition { get; set; }
    public string? Remarks { get; set; }

    public SubconJob Job { get; set; } = null!;
}

public class SubconJobOutput
{
    public long Uid { get; set; }
    public long JobId { get; set; }
    public int LineNo { get; set; }
    public string OutputKind { get; set; } = "FG";
    public string ItemCode { get; set; } = string.Empty;
    public string? ItemName { get; set; }
    public string Uom { get; set; } = string.Empty;
    public decimal ExpectedQty { get; set; }
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public string? Remarks { get; set; }

    public SubconJob Job { get; set; } = null!;
}

public class SubconSupplierAssignment
{
    public long Uid { get; set; }
    public long JobId { get; set; }
    public int LineNo { get; set; }
    public string SupplierCode { get; set; } = string.Empty;
    public string? SupplierName { get; set; }
    public decimal AssignedQty { get; set; }
    public string? PoNo { get; set; }
    public short? PoRelNo { get; set; }
    public short? PoLineNo { get; set; }
    public DateTime? PromisedReturnDate { get; set; }
    public string? DispatchDocNo { get; set; }
    public string? Remarks { get; set; }

    public SubconJob Job { get; set; } = null!;
}

/// <summary>Immutable audit and active generation link between a Subcon document and its Inventory TR / V2 StockPosting.</summary>
public class SubconPostingLink
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocKind { get; set; } = "DISPATCH";
    public long DocId { get; set; }
    public string DocNo { get; set; } = string.Empty;
    public long? JobId { get; set; }
    public int DocumentRevision { get; set; }
    public string PostingRole { get; set; } = "PRIMARY";
    public string TrxType { get; set; } = "TR";
    public int TrxBatchId { get; set; }
    public int TrxBatchNo { get; set; }
    public long? StockPostingId { get; set; }
    public Guid OperationId { get; set; }
    public Guid RequestId { get; set; }
    public DateTime EffectiveAt { get; set; }
    public bool IsActive { get; set; } = true;
    public long? ReversedByLinkId { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Per-dispatch-line custody balance allocation. Reconciles to physical IvBalLoc; never a shadow valuation authority.</summary>
public class SubconCustodyAllocation
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public long? JobId { get; set; }
    public long DispatchId { get; set; }
    public long DispatchLineId { get; set; }
    public string SupplierCode { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string BaseUom { get; set; } = string.Empty;
    public string StockStatus { get; set; } = string.Empty;
    public int CustodyBalLocId { get; set; }
    public string CustodyWarehouseCode { get; set; } = string.Empty;
    public string CustodyLocationCode { get; set; } = string.Empty;
    public decimal DispatchedBaseQty { get; set; }
    public decimal ReturnedBaseQty { get; set; }
    public decimal ConsumedBaseQty { get; set; }
    public decimal ScrapBaseQty { get; set; }
    public bool IsActive { get; set; } = true;
    public long PostingLinkId { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public SubconDispatch Dispatch { get; set; } = null!;
    public SubconDispatchLine DispatchLine { get; set; } = null!;
    public SubconPostingLink PostingLink { get; set; } = null!;
}
