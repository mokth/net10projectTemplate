using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Admin;

/// <summary>
/// Generic configurable lookup service.
///
/// <para>
/// Two deliberately separate surfaces:
/// </para>
/// <list type="bullet">
///   <item><description><b>Maintenance</b> — the <c>/admin/lookups</c> screen and the per-family module
///   screens. Each approved family carries its own menu grant in
///   <see cref="AdLookupTypeDefinition.MaintenanceMenuCode"/>; <c>ADMIN_LOOKUPS</c> remains the
///   all-family superuser grant. A family with no registered menu code fails closed — it never falls
///   back to the admin grant.</description></item>
///   <item><description><b>Assignment reads</b> — populating a dropdown on an already-authorized
///   business screen. NOT gated by any lookup-maintenance grant, because a user may be allowed to
///   maintain a warehouse and still not be an administrator. This mirrors the existing
///   <c>SaCustLookupService</c> / <c>PoSupplierLookupService</c> pattern.</description></item>
/// </list>
///
/// <para>
/// Maintenance authorization is always evaluated against the PERSISTED family of the row being read or
/// changed, never a family supplied by the caller. Company scope always comes from the authenticated
/// tenant context — never from the browser.
/// </para>
/// </summary>
public interface IAdLookupService
{
    // ── Maintenance (ADMIN_LOOKUPS) ───────────────────────────────────────────────────────────────

    Task<IReadOnlyList<AdLookupRow>> ListForMaintenanceAsync(
        string lookupType,
        bool includeInactive = true,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdLookupEditVm>> GetAsync(
        long uid,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<AdLookupEditVm>> SaveAsync(
        AdLookupEditVm model,
        bool isNew,
        CancellationToken cancellationToken = default);

    Task<IvMasterOperationResult<object>> SetActiveAsync(
        IReadOnlyList<AdLookupKeyToken> items,
        bool isActive,
        CancellationToken cancellationToken = default);

    // ── Assignment reads (ungated by ADMIN_LOOKUPS) ───────────────────────────────────────────────

    /// <summary>
    /// Active values for the caller's company, ordered by SortOrder then Code, plus
    /// <paramref name="existingCode"/> when it is inactive/legacy so an edit screen can still display
    /// the value it is bound to. The legacy value is present only as the current binding — it is not
    /// an invitation to select it elsewhere.
    /// </summary>
    Task<IReadOnlyList<IvCodeLookupRow>> ListForAssignmentAsync(
        string lookupType,
        string? existingCode = null,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience alias used by business screens that have no current value.</summary>
    Task<IReadOnlyList<IvCodeLookupRow>> ListAsync(
        string lookupType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Server-side assignment gate (a dropdown is not a data-integrity boundary).
    /// A nonblank incoming code must be an ACTIVE value of the caller's company, unless it is unchanged
    /// from <paramref name="existingCode"/> — which is how a legacy/inactive value survives an
    /// unrelated edit without becoming newly selectable.
    /// </summary>
    Task<bool> ValidateAssignmentAsync(
        string lookupType,
        string? incomingCode,
        string? existingCode,
        bool allowBlank,
        CancellationToken cancellationToken = default);

    // ── Seeding ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Idempotent default seeding for one company (used by company provisioning).</summary>
    Task EnsureLookupDefaultsAsync(string companyCode, CancellationToken cancellationToken = default);
}
