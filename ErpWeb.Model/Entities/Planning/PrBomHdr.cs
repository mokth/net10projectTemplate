namespace ErpWeb.Model.Entities.Planning;

/// <summary>
/// BOM header / Product Definition revision (<c>dbo.PrBomHdr</c>).
/// Logical identity is <see cref="CompanyCode"/> + <see cref="ProdCode"/> + <see cref="DefinitionCode"/>.
/// Revision identity adds <see cref="Version"/>.
/// Branch/Location are leftover write stamps — not BOM selection keys.
/// </summary>
public class PrBomHdr
{
    public long Uid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;
    public string ProdCode { get; set; } = string.Empty;

    /// <summary>Manufacturing-method code within the product (e.g. STANDARD, LINE-B). Immutable after first save.</summary>
    public string DefinitionCode { get; set; } = string.Empty;

    /// <summary>Human-readable name for the logical definition.</summary>
    public string? DefinitionName { get; set; }

    /// <summary>
    /// Whether this ACTIVE revision is the product's default definition for Work Order auto-select.
    /// Meaningful on ACTIVE rows; enforced by a filtered unique index (one default ACTIVE per product).
    /// </summary>
    public bool IsDefaultDefinition { get; set; }

    public int Version { get; set; } = 1;

    /// <summary>DRAFT | ACTIVE | SUPERSEDED | INACTIVE</summary>
    public string Status { get; set; } = PrBomStatuses.Draft;

    /// <summary>
    /// Deprecated: retained for historical hash-version-1 / snapshot-format-2 compatibility only.
    /// New selection uses <see cref="DefinitionCode"/> + ACTIVE status, not date windows.
    /// </summary>
    public DateTime? EffectiveFrom { get; set; }

    /// <summary>
    /// Deprecated: retained for historical hash-version-1 / snapshot-format-2 compatibility only.
    /// </summary>
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

/// <summary>Normalization helpers for <see cref="PrBomHdr.DefinitionCode"/>.</summary>
public static class PrProductDefinitionCodes
{
    public const int MaxLength = 30;
    public const string Standard = "STANDARD";
    public const string StandardName = "Standard Production";

    public static string Normalize(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().ToUpperInvariant();
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }

    public static bool IsValidFormat(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized) || normalized.Length > MaxLength)
            return false;

        foreach (var ch in normalized)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_')
                continue;
            return false;
        }

        return true;
    }
}
