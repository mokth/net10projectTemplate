namespace ErpWeb.Model.Entities;

/// <summary>
/// Dynamic parameter registry — storage for module settings that have no typed column of their own.
///
/// <para>
/// A row is an OVERRIDE only: absence means the code default applies, which is why an empty table is
/// a valid state and why applying <c>scripts/create-adsmparam.sql</c> changes no behaviour. The set of
/// legal <see cref="ModuleCode"/>/<see cref="ParamKey"/> pairs, the value type, the allowed scopes and
/// the allowed tokens all live in <c>ErpWeb.Core.Settings.AppSettingCatalogue</c>. This entity carries
/// columns only, because the model layer must not take a dependency on Core.
/// </para>
///
/// <para>
/// Two rules are enforced by the database and mirrored in the service layer: exactly one of the four
/// value columns is populated, and the scope columns agree with <see cref="ScopeCode"/>.
/// </para>
/// </summary>
public class AdSmParam
{
    public int ParamId { get; set; }

    /// <summary>
    /// Module that owns the setting — see <c>AppSettingModules</c>. Deliberately not constrained by a
    /// CHECK, so adding a module later costs no DDL.
    /// </summary>
    public string ModuleCode { get; set; } = "";

    /// <summary>Key within the module, e.g. <c>PRICE_METHOD</c>. Must exist in the catalogue.</summary>
    public string ParamKey { get; set; } = "";

    /// <summary><c>GLOBAL</c>, <c>COMPANY</c> or <c>BRANCH</c>.</summary>
    public string ScopeCode { get; set; } = "";

    /// <summary>Set for COMPANY and BRANCH scopes; NULL for GLOBAL.</summary>
    public string? CompanyCode { get; set; }

    /// <summary>Set for BRANCH scope only.</summary>
    public string? BranchCode { get; set; }

    /// <summary>
    /// Text and token values. Tokens are persisted in canonical UPPER CASE so a stored value can never
    /// fail to match its definition because of casing.
    /// </summary>
    public string? ValueText { get; set; }

    public decimal? ValueNumber { get; set; }

    public DateTime? ValueDate { get; set; }

    public bool? ValueFlag { get; set; }

    /// <summary>
    /// Reserved for a future soft-disable. Clearing a value DELETEs the row rather than deactivating
    /// it, so <c>CK_AdSmParam_OneValue</c> can never be violated and no inactive rows accumulate
    /// behind the filtered unique index.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public string? Remark { get; set; }

    public DateTime? CreatedDate { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>
    /// Optimistic concurrency token. The edit view model must carry the value it loaded and the update
    /// path must set it as the original value; without that a concurrent update is undetectable and the
    /// second write silently wins.
    /// </summary>
    public byte[]? RowVersion { get; set; }
}
