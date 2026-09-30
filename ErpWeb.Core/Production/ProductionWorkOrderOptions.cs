namespace ErpWeb.Core.Production;

/// <summary>Fail-closed switch for the version-2 Release path (plan §9.4).</summary>
public sealed class ProductionWorkOrderOptions
{
    public const string SectionName = "Production:WorkOrder";

    /// <summary>
    /// When false, a version-2 Draft cannot be released and the legacy permissive Release path
    /// is not used as a fallback.
    /// </summary>
    public bool ReleaseEnabled { get; set; } = true;
}
