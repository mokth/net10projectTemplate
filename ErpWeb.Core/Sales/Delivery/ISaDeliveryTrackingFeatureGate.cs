namespace ErpWeb.Core.Sales.Delivery;

/// <summary>Fail-closed access check used by delivery services before any query or mutation.</summary>
public interface ISaDeliveryTrackingFeatureGate
{
    Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default);
}
