namespace ErpWeb.Model.Entities.Planning;

/// <summary>
/// BOM header / Product Definition version (<c>dbo.PrBomHdr</c>).
/// Ownership is <see cref="CompanyCode"/> + <see cref="ProdCode"/> + <see cref="Version"/> only.
/// Branch/Location are leftover write stamps — not BOM selection keys.
/// </summary>
public class PrBomHdr
{
    public long Uid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string ProdCode { get; set; } = string.Empty;
    public int Version { get; set; } = 1;

    /// <summary>DRAFT | ACTIVE | SUPERSEDED | INACTIVE</summary>
    public string Status { get; set; } = PrBomStatuses.Draft;

    public DateTime? EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }

    /// <summary>Parent output quantity this BOM is defined for. Must be &gt; 0.</summary>
    public decimal BaseQty { get; set; } = 1m;

    public string? BaseUom { get; set; }

    /// <summary>Work-order number prefix (legacy <c>PrDefMas.Prefix</c>).</summary>
    public string? Prefix { get; set; }

    /// <summary>Free-form remark (legacy <c>PrDefMas.Remark</c>).</summary>
    public string? Remark { get; set; }

    public string? BranchCode { get; set; }
    public string? LocationCode { get; set; }

    /// <summary>Version of the centralized validation rules last applied to this revision.</summary>
    public string? ValidationRuleVersion { get; set; }

    /// <summary>UNVERIFIED | VALID | INVALID.</summary>
    public string ValidationStatus { get; set; } = PrBomValidationStatuses.Unverified;

    public DateTime? ValidatedDate { get; set; }
    public string? ValidatedBy { get; set; }
    public DateTime? ActivatedDate { get; set; }
    public string? ActivatedBy { get; set; }

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public ICollection<PrDefBOM> Lines { get; set; } = new List<PrDefBOM>();
    public ICollection<PrBomRouteStep> RouteSteps { get; set; } = new List<PrBomRouteStep>();
    public ICollection<PrBomOperation> Operations { get; set; } = new List<PrBomOperation>();
}

public static class PrBomValidationStatuses
{
    public const string Unverified = "UNVERIFIED";
    public const string Valid = "VALID";
    public const string Invalid = "INVALID";
}

public static class PrBomStatuses
{
    public const string Draft = "DRAFT";
    public const string Active = "ACTIVE";
    public const string Superseded = "SUPERSEDED";
    public const string Inactive = "INACTIVE";
}

public static class PrMfgTypes
{
    public const string Buy = "BUY";
    public const string Make = "MAKE";
    public const string Phantom = "PHANTOM";

    public static bool IsValid(string? value) =>
        string.Equals(value, Buy, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Make, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, Phantom, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) =>
        (value ?? Buy).Trim().ToUpperInvariant();
}
