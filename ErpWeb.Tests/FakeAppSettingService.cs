using System.Globalization;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Settings;

namespace ErpWeb.Tests;

/// <summary>
/// Settings stub for tests that must not depend on the registry.
///
/// <para>
/// By default EVERY read fails, which makes the production code fall back to its own code default — that
/// is, exactly the behaviour from before the setting existed. That default is what makes the P5b
/// regression assertion meaningful: "no setting configured" and "the old build" must agree.
/// </para>
///
/// <para>
/// Use <see cref="With"/> to make a specific setting resolve.
/// </para>
/// </summary>
internal sealed class FakeAppSettingService : IAppSettingService
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times a value was asked for — lets a test prove the read actually happened.</summary>
    public int ReadCount { get; private set; }

    public FakeAppSettingService With(string module, string key, string? value)
    {
        _values[Composite(module, key)] = value;
        return this;
    }

    public Task<IvMasterOperationResult<string?>> GetValueAsync(
        string module,
        string key,
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(module, key));

    public Task<IvMasterOperationResult<string?>> GetForCurrentUserAsync(
        string module,
        string key,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Read(module, key));

    public Task<bool> GetFlagAsync(string module, string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Equals(Read(module, key).Data, "true", StringComparison.OrdinalIgnoreCase));

    public Task<decimal?> GetNumberAsync(string module, string key, CancellationToken cancellationToken = default)
    {
        var result = Read(module, key);

        return Task.FromResult(
            result.Succeeded
            && decimal.TryParse(result.Data, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
                ? value
                : (decimal?)null);
    }

    public Task<IvMasterOperationResult<IReadOnlyList<AppSettingListRow>>> ListForModuleAsync(
        string module,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            IvMasterOperationResult<IReadOnlyList<AppSettingListRow>>.Ok(Array.Empty<AppSettingListRow>()));

    public Task<IvMasterOperationResult<AppSettingEditVm>> SaveAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            IvMasterOperationResult<AppSettingEditVm>.Fail(
                IvMasterErrorCode.AccessDenied,
                "The settings registry is not available in this test."));

    public Task<IvMasterOperationResult<AppSettingEditVm>> ClearAsync(
        AppSettingEditVm vm,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(
            IvMasterOperationResult<AppSettingEditVm>.Fail(
                IvMasterErrorCode.AccessDenied,
                "The settings registry is not available in this test."));

    private IvMasterOperationResult<string?> Read(string module, string key)
    {
        ReadCount++;

        return _values.TryGetValue(Composite(module, key), out var value)
            ? IvMasterOperationResult<string?>.Ok(value)
            : IvMasterOperationResult<string?>.Fail(
                IvMasterErrorCode.NotFound,
                "Not configured in this test.");
    }

    private static string Composite(string module, string key) => $"{module}|{key}";
}
