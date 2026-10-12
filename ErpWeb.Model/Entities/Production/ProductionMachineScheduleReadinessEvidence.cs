namespace ErpWeb.Model.Entities.Production;

/// <summary>
/// Durable certificate that the current machine-calendar sources and release-allocation contract
/// passed the planner preflight. The calendar hash is recomputed at runtime, so source changes make
/// the certificate stale without requiring every writer to update this row.
/// </summary>
public sealed class ProductionMachineScheduleReadinessEvidence
{
    public string CompanyCode { get; set; } = string.Empty;
    public string CalendarSourceFingerprint { get; set; } = string.Empty;
    public string SchemaContractVersion { get; set; } = string.Empty;
    public string ReleaseContractVersion { get; set; } = string.Empty;
    public DateTime VerifiedAtUtc { get; set; }
    public string VerifiedBy { get; set; } = string.Empty;
    public byte[] RowVersion { get; set; } = [];
}
