namespace ErpWeb.Model.Entities;

/// <summary>
/// Company-scoped configurable classification value (plan: Generic Configurable Lookup Master).
///
/// <para>
/// This table exists for <b>descriptive</b> business classifications that users must be able to add,
/// rename, deactivate and report on without a code change — for example Warehouse Type, Machine Type,
/// Work Centre Class, Supplier Type and Supplier Subgroup.
/// </para>
/// <para>
/// It is deliberately NOT a home for workflow/system states (NEW / POSTED / CANCELLED, document
/// statuses, posting types, costing methods, production issue/supply modes). Those are logic-bearing
/// and stay compile-time controlled.
/// </para>
/// <para>
/// Consumer tables keep storing the stable <see cref="Code"/>; the lookup supplies the description and
/// the lifecycle. There is intentionally no physical foreign key from consumer columns — validity is
/// enforced by the owning service so a cross-domain migration is not required.
/// </para>
/// </summary>
public sealed class AdLookupValue
{
    public long Uid { get; set; }

    public string CompanyCode { get; set; } = string.Empty;

    /// <summary>Controlled family name — see <c>AdLookupTypes</c>. Never freely typed by a user.</summary>
    public string LookupType { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
