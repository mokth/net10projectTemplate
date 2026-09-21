namespace ErpWeb.Core.EInvoice;

/// <summary>
/// LHDN (MyInvois) codes the ERP substitutes when a master-data value is missing or blank, so a
/// submission is never rejected for a value the user could not supply. Kept in one place because the
/// same defaults are applied at master-data save time and again when the e-Invoice payload is built.
/// </summary>
public static class LhdnDefaults
{
    /// <summary>UNECE Recommendation 20 code for "piece" — the fallback unit of measure.</summary>
    public const string UneceUom = "H87";

    /// <summary>LHDN tax type <c>06</c> ("Not Applicable") — the fallback tax type.</summary>
    public const string TaxType = "06";
}
