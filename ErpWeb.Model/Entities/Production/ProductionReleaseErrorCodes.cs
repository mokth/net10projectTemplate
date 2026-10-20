namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Stable reason codes raised by the Release gate (and the lock-time revalidation it performs).
/// <para>
/// These are deliberately kept separate from <see cref="ProductionReadinessErrorCodes"/>: readiness
/// codes are <c>WO_</c>-prefixed Draft validation findings, whereas these report a release-time
/// rejection of the saved Draft. The strings must not be reworded once released.
/// </para>
/// </summary>
public static class ProductionReleaseErrorCodes
{
    /// <summary>
    /// Release revalidation produced a different placement than the saved Draft under the current
    /// verified physical capacity. The Work Order stays Draft and the user must recalculate and
    /// review the new dates (plan §14).
    /// </summary>
    public const string MachineCapacityStale = "MACHINE_CAPACITY_STALE";
}
