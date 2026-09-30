namespace ErpWeb.Model.Entities.Planning;

/// <summary>
/// Product Definition / BOM component line (<c>dbo.PrDefBOM</c>).
/// Direct children only — multi-level structure is built by following each Make/Phantom item's own BOM.
/// <para>
/// <see cref="StdQty"/> = component quantity required to manufacture <see cref="PrBomHdr.BaseQty"/>
/// of <see cref="ProdCode"/>. RequiredQty = IvQty.Round(ProductionQty / BaseQty × StdQty × (1 + ScrapPercent/100)).
/// Production Orders must <b>copy</b> these values at create time — do not live-link historical orders.
/// </para>
/// </summary>
public class PrDefBOM
{
    public long Uid { get; set; }

    public long BomHdrId { get; set; }
    public Guid? OperationKey { get; set; }
    public PrBomHdr? Header { get; set; }

    /// <summary>Authoritative operation ownership. Nullable only during compatibility migration.</summary>
    public long? OperationId { get; set; }
    public PrBomOperation? Operation { get; set; }

    /// <summary>
    /// The in-house route step that produces this component when <see cref="SupplySource"/> is
    /// <see cref="PrMaterialSupplySources.InternalRouteWip"/>. Null for purchased / external lines.
    /// The step's <see cref="PrBomRouteStep.OutputItemCode"/> must equal <see cref="ICode"/>; the
    /// Work Order snapshot validates the producer graph and rejects cycles.
    /// </summary>
    public long? ProducingRouteStepId { get; set; }
    public PrBomRouteStep? ProducingRouteStep { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string ProdCode { get; set; } = string.Empty;
    public string ICode { get; set; } = string.Empty;

    /// <summary>Snapshot of component description at save time.</summary>
    public string? IName { get; set; }

    public decimal StdQty { get; set; }
    public string? StdUom { get; set; }

    public int SeqNo { get; set; }

    /// <summary>Component scrap percent (>= 0). Compounds per explosion level.</summary>
    public decimal ScrapPercent { get; set; }

    /// <summary>Default / preferred source warehouse for material issue (Production may override).</summary>
    public string? Warehouse { get; set; }

    public bool BomDefault { get; set; } = true;

    /// <summary>
    /// Normalized alternate group identity (trim + uppercase, max 30). Same
    /// <c>(OperationKey, AlternateGroupCode)</c> may carry one default and zero or more alternates.
    /// Null on legacy non-default rows that were never explicitly grouped.
    /// </summary>
    public string? AlternateGroupCode { get; set; }

    /// <summary>Legacy WIP flag — persist only; not exposed in UI.</summary>
    public bool WipBomDefault { get; set; }

    public decimal Tolerance { get; set; }
    public string IssueMethod { get; set; } = PrMaterialIssueMethods.Manual;
    public string SupplySource { get; set; } = PrMaterialSupplySources.Purchased;

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrBomMaterialBranchDefault> BranchDefaults { get; set; } = new List<PrBomMaterialBranchDefault>();

    public decimal MaterialStandardQty
    {
        get => StdQty;
        set => StdQty = value;
    }

    public string? StandardUom
    {
        get => StdUom;
        set => StdUom = value;
    }

    public decimal TolerancePercent
    {
        get => Tolerance;
        set => Tolerance = value;
    }
}

public static class PrMaterialIssueMethods
{
    public const string Manual = "MANUAL";
    public const string Backflush = "BACKFLUSH";
    public const string PickList = "PICK_LIST";

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> All { get; } = [Manual, Backflush, PickList];
}

public static class PrMaterialSupplySources
{
    public const string Purchased = "PURCHASED";
    public const string InternalRouteWip = "INTERNAL_ROUTE_WIP";
    public const string SeparateProductDefinition = "SEPARATE_PRODUCT_DEFINITION";
    public const string ExternalSupply = "EXTERNAL_SUPPLY";

    public static bool IsValid(string? value) =>
        value is not null && All.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> All { get; } =
        [Purchased, InternalRouteWip, SeparateProductDefinition, ExternalSupply];
}

/// <summary>Normalization helpers for <see cref="PrDefBOM.AlternateGroupCode"/>.</summary>
public static class PrBomAlternateGroups
{
    public const int MaxLength = 30;

    public static string? Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return trimmed.Length == 0 ? null : trimmed;
    }

    public static bool IsValidLength(string? normalized) =>
        normalized is null || normalized.Length <= MaxLength;
}
