namespace ErpWeb.Core.Settings;

/// <summary>
/// Supplies the live value of a definition that is backed by an existing typed column.
///
/// <para>
/// A provider knows how to READ its owning column and nothing else. It returns the RAW stored value,
/// not a normalised one, so the resolver can still report an unusable value as rejected instead of
/// silently correcting it. The catalogue decides that the setting exists; the provider only fetches it.
/// </para>
///
/// <para>
/// A column-backed setting is never stored in <c>AdSmParam</c> and never written through the settings
/// service, so the registry cannot become a second authority for a value that already has a home.
/// </para>
/// </summary>
public interface IAppSettingValueProvider
{
    /// <summary>Module of the definition this provider serves.</summary>
    string Module { get; }

    /// <summary>Key of the definition this provider serves.</summary>
    string Key { get; }

    /// <summary>
    /// Reads the owning column for the supplied tenant. Returns null when there is no value to project.
    /// </summary>
    Task<string?> ReadRawAsync(
        AppSettingScope scope,
        string? companyCode,
        string? branchCode,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Indexes the registered providers by <c>(Module, Key)</c>.
///
/// <para>
/// Two providers claiming the same setting is a startup error, not a last-one-wins: which column a
/// setting reads from must never depend on DI registration order.
/// </para>
/// </summary>
public sealed class AppSettingProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IAppSettingValueProvider> _byKey;

    public AppSettingProviderRegistry(IEnumerable<IAppSettingValueProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var map = new Dictionary<string, IAppSettingValueProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            var lookupKey = AppSettingDefinition.BuildLookupKey(provider.Module, provider.Key);

            if (!map.TryAdd(lookupKey, provider))
            {
                throw new InvalidOperationException(
                    $"Two IAppSettingValueProvider implementations claim {provider.Module}.{provider.Key}. "
                    + "Exactly one provider is allowed per column-backed setting.");
            }
        }

        _byKey = map;
    }

    public bool TryGet(string? module, string? key, out IAppSettingValueProvider provider)
    {
        if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(key))
        {
            provider = null!;
            return false;
        }

        return _byKey.TryGetValue(AppSettingDefinition.BuildLookupKey(module, key), out provider!);
    }

    public IReadOnlyDictionary<string, IAppSettingValueProvider> All => _byKey;
}
