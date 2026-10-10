using ErpWeb.Core.Settings;

namespace ErpWeb.Core.Sales.Delivery;

public sealed class SaDeliveryTrackingFeatureGate : ISaDeliveryTrackingFeatureGate
{
    private readonly IAppSettingService _settings;

    public SaDeliveryTrackingFeatureGate(IAppSettingService settings)
    {
        _settings = settings;
    }

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        _settings.GetFlagAsync(
            AppSettingModules.Sales,
            AppSettingCatalogue.SalesKeys.DeliveryTrackingEnabled,
            cancellationToken);
}
