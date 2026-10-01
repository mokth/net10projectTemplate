using ErpWeb.Core.Inventory;
using ErpWeb.Model.Entities.Planning;

namespace ErpWeb.Core.Planning;

public sealed class PrProductDefListQuery
{
    public string? SearchText { get; set; }
    public string? ProdCode { get; set; }
    public string? ProdDesc { get; set; }
    public string? ComponentCode { get; set; }
    public string? ComponentDesc { get; set; }
    public string? Warehouse { get; set; }
    public bool? IsActive { get; set; }
    public string? SortField { get; set; }
    public bool SortDescending { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; } = 20;
}

public sealed class PrProductDefListRow
{
    public string ProdCode { get; init; } = string.Empty;
    public string? ProdDesc { get; init; }
    public string? StdUom { get; init; }
    public string MfgType { get; init; } = "BUY";
    public bool IsActive { get; init; }

    public string DefinitionCode { get; init; } = string.Empty;
    public string? DefinitionName { get; init; }
    public bool IsDefaultDefinition { get; init; }

    public int? ActiveVersion { get; init; }
    public int LatestVersion { get; init; }
    public string LatestStatus { get; init; } = string.Empty;
    public int LatestBomItemCount { get; init; }
    public DateTime? LatestModifiedDate { get; init; }

    /// <summary>Stable list key: ProdCode + U+001F + DefinitionCode.</summary>
    public string DefinitionKey { get; init; } = string.Empty;

    /// <summary>Compatibility alias for list columns that previously used BomItemCount.</summary>
    public int BomItemCount => LatestBomItemCount;

    public DateTime? CreatedDate { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedBy { get; init; }

    /// <summary>Compatibility alias for list actions that previously used BomVersion.</summary>
    public int BomVersion => LatestVersion;

    /// <summary>Compatibility alias for list actions that previously used BomStatus.</summary>
    public string BomStatus => LatestStatus;
}

public sealed class PrProductDefListPage
{
    public IReadOnlyList<PrProductDefListRow> Rows { get; init; } = [];
    public int TotalCount { get; init; }
}

/// <summary>Logical Product Definition identity (Company implied by tenant scope).</summary>
public sealed class PrProductDefinitionKey
{
    public string ProdCode { get; init; } = string.Empty;
    public string DefinitionCode { get; init; } = string.Empty;
}

/// <summary>Lookup row for Work Order / authoring definition pickers.</summary>
public sealed class PrProductDefinitionLookupRow
{
    public long? BomHdrId { get; init; }
    public string ProdCode { get; init; } = string.Empty;
    public string DefinitionCode { get; init; } = string.Empty;
    public string? DefinitionName { get; init; }
    public int Version { get; init; }
    public int? ActiveVersion { get; init; }
    public int? LatestVersion { get; init; }
    public string? LatestStatus { get; init; }
    public bool IsDefaultDefinition { get; init; }
}

public sealed class PrProductDefLineVm
{
    public Guid? OperationKey { get; set; }
    public long Uid { get; set; }

    /// <summary>Client-only identity for unsaved lines (TreeList keys). Not persisted.</summary>
    public string? TempId { get; set; }

    public string ICode { get; set; } = string.Empty;
    public string? IName { get; set; }
    public decimal StdQty { get; set; }
    public string? StdUom { get; set; }
    public int SeqNo { get; set; }
    public decimal ScrapPercent { get; set; }
    public string? Warehouse { get; set; }
    public bool BomDefault { get; set; } = true;

    /// <summary>Normalized alternate group (trim + uppercase). Required on new Product Definition saves.</summary>
    public string? AlternateGroupCode { get; set; }

    public decimal Tolerance { get; set; }
    public string MfgType { get; set; } = "BUY";

    /// <summary>MANUAL | BACKFLUSH | PICK_LIST. Drives Work Order material issue behaviour.</summary>
    public string IssueMethod { get; set; } = PrMaterialIssueMethods.Manual;

    /// <summary>PURCHASED | INTERNAL_ROUTE_WIP | SEPARATE_PRODUCT_DEFINITION | EXTERNAL_SUPPLY.</summary>
    public string SupplySource { get; set; } = PrMaterialSupplySources.Purchased;

    /// <summary>
    /// When <see cref="SupplySource"/> is SEPARATE_PRODUCT_DEFINITION, the child Product Definition code.
    /// Must be null for other supply sources.
    /// </summary>
    public string? ComponentDefinitionCode { get; set; }

    /// <summary>
    /// Stable key of the in-house route step that produces this component. Required when
    /// <see cref="SupplySource"/> is INTERNAL_ROUTE_WIP; ignored for purchased / external lines.
    /// </summary>
    public Guid? ProducingRouteStepKey { get; set; }
}

/// <summary>One process step in the versioned route, including its eligible resources.</summary>
public sealed class PrProductDefOperationVm
{
    public Guid OperationKey { get; set; } = Guid.NewGuid();
    public long Uid { get; set; }
    public string? TempId { get; set; }
    public string WorkCentreCode { get; set; } = string.Empty;
    public string? WorkCentreDescription { get; set; }
    public string OutputItemCode { get; set; } = string.Empty;
    public int CentralSequence { get; set; }
    public decimal OutputBaseQty { get; set; } = 1m;
    public string? OutputUom { get; set; }
            public string OperationCode { get; set; } = string.Empty;
    public string? OperationDescription { get; set; }
    public int ProcessSequence { get; set; }

    /// <summary>
    /// Persisted route-step key for the owning (work centre, output, stage) group. Populated on
    /// Get so material INTERNAL_ROUTE_WIP lines can pick a producer; empty until the revision is saved.
    /// </summary>
    public Guid? RouteStepKey { get; set; }

    /// <summary>MACHINE | AUTOMATED | MANUAL | INSPECTION | WAIT | PACKING | SUBCONTRACT.</summary>
    public string ProcessType { get; set; } = PrProcessTypes.Machine;

    /// <summary>
    /// Cycle minutes for duration-based process types (MANUAL, INSPECTION, WAIT, SUBCONTRACT and
    /// machine-less PACKING). Machine-based steps derive duration from the selected resource option
    /// and keep this at zero.
    /// </summary>
    public decimal StandardDurationMinutes { get; set; }

    public decimal SetupLossQty { get; set; }
    public decimal OperationLossQty { get; set; }
    public bool IsFinalOperation { get; set; }
    public string? Remark { get; set; }
    public List<PrProductDefMachineVm> Machines { get; set; } = [];
}

public sealed class PrProductDefMachineVm
{
    public long Uid { get; set; }
    public string? TempId { get; set; }
    public string MachineCode { get; set; } = string.Empty;
    public string? MachineDescription { get; set; }
    public int ResourceSequence { get; set; }
    public bool IsPrimary { get; set; } = true;

    /// <summary>Selection order when several machine options are eligible for the same step.</summary>
    public int Priority { get; set; } = 1;

    public decimal CycleSeconds { get; set; }

    /// <summary>Output units produced per machine cycle. Must be greater than zero.</summary>
    public decimal OutputPerCycle { get; set; } = 1m;

    public decimal ConversionSeconds { get; set; }
    public decimal SetupSeconds { get; set; }
    public decimal QueueSeconds { get; set; }
    public decimal MachineRatePerHour { get; set; }
    public int ParallelMachineCount { get; set; } = 1;
    public List<PrProductDefLabourVm> Labours { get; set; } = [];
}

public sealed class PrProductDefLabourVm
{
    public long Uid { get; set; }
    public string? TempId { get; set; }
    public string LabourCode { get; set; } = string.Empty;
    public string? LabourDescription { get; set; }
    public decimal CostPerOutputUnit { get; set; }
}

public enum PrBomStructureNodeStatus
{
    Normal = 0,
    MissingBom = 1,
    Circular = 2,
    MaxDepth = 3
}

public sealed class PrBomStructureNode
{
    public string Key { get; init; } = string.Empty;
    public string? ParentKey { get; init; }
    public int Level { get; init; }
    public string ItemCode { get; init; } = string.Empty;
    public string? ItemDesc { get; init; }
    public string MfgType { get; init; } = "BUY";
    public decimal StdQty { get; init; }
    public string? StdUom { get; init; }
    public decimal ScrapPercent { get; init; }
    public string? Warehouse { get; init; }
    public int SeqNo { get; init; }

    /// <summary>Whose BOM this line belongs to. Null on the synthetic root.</summary>
    public string? OwnerProdCode { get; init; }

    /// <summary>Owner Product Definition code. Null on the synthetic root.</summary>
    public string? OwnerDefinitionCode { get; init; }

    /// <summary>
    /// Child Product Definition code when the line supply source is SEPARATE_PRODUCT_DEFINITION.
    /// </summary>
    public string? ComponentDefinitionCode { get; init; }

    public long? SourceLineUid { get; init; }
    public string? LineTempId { get; init; }
    public long? BomHdrId { get; init; }
    public int? BomVersion { get; init; }
    public string? BomStatus { get; init; }
    public PrBomStructureNodeStatus Status { get; init; } = PrBomStructureNodeStatus.Normal;
}

public sealed class PrBomStructureResult
{
    public string RootProdCode { get; init; } = string.Empty;
    public string RootDefinitionCode { get; init; } = string.Empty;
    public long? RootBomHdrId { get; init; }
    public int? RootBomVersion { get; init; }
    public string? RootBomStatus { get; init; }
    public IReadOnlyList<PrBomStructureNode> Nodes { get; init; } = [];
}

/// <summary>Occurrence keys for BOM structure TreeList (never bare ItemCode).</summary>
public static class PrBomStructureKeys
{
    public const int MaxDepth = 20;

    public static string Root(string prodCode, string definitionCode) =>
        "R:" + FormatNode(prodCode, definitionCode);

    /// <summary>Compatibility overload — defaults to STANDARD definition.</summary>
    public static string Root(string prodCode) =>
        Root(prodCode, PrProductDefinitionCodes.Standard);

    public static string Line(string parentKey, string ownerProdCode, string ownerDefinitionCode, string lineId) =>
        parentKey + "/" + FormatNode(ownerProdCode, ownerDefinitionCode) + ":" + lineId;

    /// <summary>Compatibility overload — defaults owner definition to STANDARD.</summary>
    public static string Line(string parentKey, string ownerProdCode, string lineId) =>
        Line(parentKey, ownerProdCode, PrProductDefinitionCodes.Standard, lineId);

    public static string LineId(long uid, string? tempId) =>
        uid > 0 ? uid.ToString() : (string.IsNullOrWhiteSpace(tempId) ? "0" : tempId.Trim());

    public static string FormatNode(string? prodCode, string? definitionCode) =>
        Normalize(prodCode) + "[" + Normalize(definitionCode) + "]";

    public static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToUpperInvariant();
}

public sealed class PrProductDefEditVm
{
    public string ProdCode { get; set; } = string.Empty;
    public string? ProdDesc { get; set; }
    public string? StdUom { get; set; }
    public string MfgType { get; set; } = "BUY";
    public bool IsActive { get; set; } = true;

    /// <summary>Manufacturing-method code within the product. Immutable after first save.</summary>
    public string DefinitionCode { get; set; } = string.Empty;

    /// <summary>Human-readable name for the logical definition.</summary>
    public string? DefinitionName { get; set; }

    /// <summary>Whether this ACTIVE revision is the product's default definition.</summary>
    public bool IsDefaultDefinition { get; set; }

    public long BomHdrId { get; set; }
    public int Version { get; set; } = 1;
    public string Status { get; set; } = "DRAFT";
    public decimal BaseQty { get; set; } = 1m;
    public string? BaseUom { get; set; }

    /// <summary>
    /// Deprecated date-window fields retained for in-flight service compatibility during
    /// multi-definition migration. New selection uses DefinitionCode + ACTIVE status.
    /// </summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>Deprecated — see <see cref="EffectiveFrom"/>.</summary>
    public DateTime? EffectiveTo { get; set; }

    /// <summary>Work-order number prefix (max 10).</summary>
    public string? Prefix { get; set; }

    /// <summary>Free-form remark (max 1000).</summary>
    public string? Remark { get; set; }

    /// <summary>Header concurrency token from Get.</summary>
    public byte[]? HeaderRowVersion { get; set; }

    public List<PrProductDefLineVm> Lines { get; set; } = [];
    public List<PrProductDefOperationVm> Operations { get; set; } = [];
}

public interface IPrProductDefService
{
    Task<IvMasterOperationResult<PrProductDefListPage>> SearchAsync(
        PrProductDefListQuery query,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PrProductDefEditVm>> GetAsync(
        string prodCode,
        string definitionCode,
        int? version = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flat composition tree for Product Definition entry (DxTreeList).
    /// Uses the same header version resolution as <see cref="GetAsync"/>.
    /// Branch problems become node statuses; they do not fail the whole tree.
    /// </summary>
    Task<IvMasterOperationResult<PrBomStructureResult>> GetStructureTreeAsync(
        string prodCode,
        string definitionCode,
        int? version = null,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PrProductDefEditVm>> SaveAsync(
        PrProductDefEditVm model,
        bool isNew,
        bool activate,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PrProductDefEditVm>> CreateNewVersionAsync(
        string prodCode,
        string definitionCode,
        int? fromVersion = null,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<PrProductDefEditVm>> ActivateAsync(
        string prodCode,
        string definitionCode,
        int version,
        byte[]? headerRowVersion,
        CancellationToken cancellationToken = default);

    /// <summary>ACTIVE definitions only — Work Order / runtime selection.</summary>
    Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListActiveDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logical definitions with ACTIVE and/or DRAFT revisions — Product Definition authoring picker.
    /// </summary>
    Task<IvMasterOperationResult<IReadOnlyList<PrProductDefinitionLookupRow>>> ListDefinitionsAsync(
        string prodCode,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<DeleteCheckResult>> CanDeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object?>> DeleteAsync(
        IReadOnlyList<PrProductDefinitionKey> keys,
        CancellationToken cancellationToken = default);
}
