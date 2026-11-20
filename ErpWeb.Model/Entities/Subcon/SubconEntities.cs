namespace ErpWeb.Model.Entities.Subcon;

/// <summary>Company-level Subcon flags. Live source modes stay off in Phase 1.</summary>
public class SubconPolicy
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public int Version { get; set; }
    public bool SubconEnabled { get; set; }
    public bool InventoryModeEnabled { get; set; }
    public bool ProductionModeEnabled { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Send-out draft. Status stays NEW until a later approved posting owner exists.</summary>
public class SubconDispatch
{
    public long Uid { get; set; }
    public string CompanyCode { get; set; } = string.Empty;
    public string BranchCode { get; set; } = string.Empty;
    public string DocNo { get; set; } = string.Empty;
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
    public DateTime CreatedDate { get; set; }
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

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
    public string? WarehouseCode { get; set; }
    public string? LocationCode { get; set; }
    public string? LotNo { get; set; }
    public int? IvBalLocId { get; set; }
    public long? ProductionBalLotUid { get; set; }
    public string? WorkOrderNo { get; set; }
    public string? OperationCode { get; set; }
    public DateTime? PromisedReturnDate { get; set; }
    public string? Remarks { get; set; }

    public SubconDispatch Dispatch { get; set; } = null!;
    public ICollection<SubconReceiptLine> ReceiptAllocations { get; set; } = [];
}

/// <summary>Receive-back draft. Cannot post in Phase 1.</summary>
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
