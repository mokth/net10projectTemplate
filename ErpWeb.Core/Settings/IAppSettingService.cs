using ErpWeb.Core.Inventory;

namespace ErpWeb.Core.Settings;

/// <summary>
/// Reads and writes dynamic settings. Reads are a server capability and are NOT menu-gated — a pricing
/// or posting decision must be able to ask for a setting without the caller holding a settings-screen
/// permission. List, save and clear ARE gated, because they are the admin screen's operations.
/// </summary>
public interface IAppSettingService
{
    /// <summary>
    /// Resolves one setting. <paramref name="scope"/> limits how far up the ladder the caller may go:
    /// <c>Global</c> reads only the global value, <c>Company</c> adds the company value, <c>Branch</c>
    /// adds the branch value. The definition's own allowed scopes always win.
    /// </summary>
    Task<IvMasterOperationResult<string?>> GetValueAsync(
        string module,
        string key,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves one setting for the signed-in user's company and branch.</summary>
    Task<IvMasterOperationResult<string?>> GetForCurrentUserAsync(
        string module,
        string key,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience read: true only when the resolved value is exactly "true". A failed read is false.</summary>
    Task<bool> GetFlagAsync(string module, string key, CancellationToken cancellationToken = default);

    /// <summary>Convenience read: the resolved value as a decimal, or null when it is missing or unusable.</summary>
    Task<decimal?> GetNumberAsync(string module, string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every definition in a module, left-joined with the stored rows for this tenant, so an unset
    /// setting is visible as its default rather than absent. Also reports the orphan-row count.
    /// </summary>
    Task<IvMasterOperationResult<IReadOnlyList<AppSettingListRow>>> ListForModuleAsync(
        string module,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default);

    /// <summary>Creates or updates the row for one setting at one scope. Requires the EDIT permission.</summary>
    Task<IvMasterOperationResult<AppSettingEditVm>> SaveAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// DELETES the row, returning the setting to its code default. Deleting rather than blanking is
    /// required by the exactly-one-value constraint, and keeps no inactive rows behind the unique key.
    /// </summary>
    Task<IvMasterOperationResult<AppSettingEditVm>> ClearAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default);
}
