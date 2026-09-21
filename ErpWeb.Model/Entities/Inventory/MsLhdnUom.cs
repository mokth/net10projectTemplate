namespace ErpWeb.Model.Entities.Inventory;

/// <summary>
/// LHDN (MyInvois) UNECE unit-of-measure code list. Global reference table — no company column,
/// so the same codes are shared by every tenant. <c>MsUOM.UNECE_UOM</c> stores only a
/// <see cref="Code"/> from this table, defaulting to <c>H87</c> (piece).
/// </summary>
public class MsLhdnUom
{
    public int Uid { get; set; }

    /// <summary>UNECE Recommendation 20 unit code (for example <c>H87</c>, <c>KGM</c>, <c>C62</c>).</summary>
    public string? Code { get; set; }

    /// <summary>Human-readable measurement, for example "piece" or "kilogram".</summary>
    public string? Measurement { get; set; }
}
