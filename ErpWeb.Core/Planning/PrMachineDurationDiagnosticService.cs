using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Planning;

/// <summary>One ACTIVE Product Definition machine option that cannot produce positive duration.</summary>
public sealed class PrMachineDurationFinding
{
    public long DefinitionId { get; init; }
    public string ProdCode { get; init; } = string.Empty;
    public string DefinitionCode { get; init; } = string.Empty;
    public int Version { get; init; }
    public string OperationCode { get; init; } = string.Empty;
    public string MachineCode { get; init; } = string.Empty;
    public string ProcessType { get; init; } = string.Empty;
    public decimal SetupSeconds { get; init; }
    public decimal ConversionSeconds { get; init; }
    public decimal QueueSeconds { get; init; }
    public decimal CycleSeconds { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Read-only diagnostic for existing ACTIVE Product Definitions that violate the machine-duration
/// activation rule (plan §7). Nothing is repaired automatically: repairing an ACTIVE definition is
/// an explicit authoring action.
/// </summary>
public interface IPrMachineDurationDiagnosticService
{
    Task<IReadOnlyList<PrMachineDurationFinding>> FindActiveMachineOptionsWithoutPositiveDurationAsync(
        string companyCode,
        CancellationToken cancellationToken = default);
}

public sealed class PrMachineDurationDiagnosticService : IPrMachineDurationDiagnosticService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public PrMachineDurationDiagnosticService(IDbContextFactory<AppDbContext> dbFactory) =>
        _dbFactory = dbFactory;

    public async Task<IReadOnlyList<PrMachineDurationFinding>> FindActiveMachineOptionsWithoutPositiveDurationAsync(
        string companyCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await (
                from header in db.PrBomHdrs.AsNoTracking()
                join operation in db.PrBomOperations.AsNoTracking() on header.Uid equals operation.BomHdrId
                join machine in db.PrBomMachineOptions.AsNoTracking() on operation.Uid equals machine.OperationId
                where header.CompanyCode == companyCode
                    && header.Status == PrBomStatuses.Active
                select new
                {
                    DefinitionId = header.Uid,
                    header.ProdCode,
                    header.DefinitionCode,
                    header.Version,
                    OperationCode = operation.OperationCode,
                    operation.ProcessType,
                    machine.MachineCode,
                    machine.SetupSeconds,
                    machine.ConversionSeconds,
                    machine.QueueSeconds,
                    machine.CycleSeconds
                })
            .ToListAsync(cancellationToken);

        return rows
            .Where(row => PrProcessTypes.SupportsMachine(row.ProcessType))
            .Where(row => !ProductionMachineScheduleDuration.CanProducePositiveOneSlotDuration(
                row.SetupSeconds, row.ConversionSeconds, row.QueueSeconds, row.CycleSeconds))
            .OrderBy(row => row.ProdCode, StringComparer.Ordinal)
            .ThenBy(row => row.DefinitionCode, StringComparer.Ordinal)
            .ThenBy(row => row.Version)
            .ThenBy(row => row.OperationCode, StringComparer.Ordinal)
            .ThenBy(row => row.MachineCode, StringComparer.Ordinal)
            .Select(row => new PrMachineDurationFinding
            {
                DefinitionId = row.DefinitionId,
                ProdCode = row.ProdCode,
                DefinitionCode = row.DefinitionCode,
                Version = row.Version,
                OperationCode = row.OperationCode,
                MachineCode = row.MachineCode,
                ProcessType = row.ProcessType,
                SetupSeconds = row.SetupSeconds,
                ConversionSeconds = row.ConversionSeconds,
                QueueSeconds = row.QueueSeconds,
                CycleSeconds = row.CycleSeconds,
                Message = $"ACTIVE definition {row.ProdCode}/{row.DefinitionCode} v{row.Version}, "
                    + $"operation {row.OperationCode}, machine {row.MachineCode} cannot produce a positive "
                    + "one-slot scheduled duration. Correct the timing and save a new version before "
                    + "creating Work Orders from it."
            })
            .ToList();
    }
}
