using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Core.Production;

/// <summary>
/// The exact Product Definition revision resolved for a Work Order snapshot, loaded with its whole
/// route / operation / material / machine / labour graph under one coherent read.
/// </summary>
public sealed class ProductDefinitionLoadResult
{
    public PrBomHdr? Revision { get; init; }
    public int MatchingRevisionCount { get; init; }
    public string? FailureCode { get; init; }
    public string? FailureMessage { get; init; }

    public bool Succeeded => Revision is not null;

    public static ProductDefinitionLoadResult Ok(PrBomHdr revision) =>
        new() { Revision = revision, MatchingRevisionCount = 1 };

    public static ProductDefinitionLoadResult Fail(string code, string message, int matches = 0) =>
        new() { FailureCode = code, FailureMessage = message, MatchingRevisionCount = matches };
}

/// <summary>
/// Resolves one Product Definition revision and loads its version-owned graph (plan §7.2).
/// <para>
/// Resolution is deliberately cardinality-safe: zero matches and more than one match are both
/// hard failures. The loader never breaks a tie by choosing the highest version, the latest
/// creation date, or the first row, because that would silently manufacture a different snapshot
/// than the one the planner saw.
/// </para>
/// </summary>
public interface IProductDefinitionSnapshotLoader
{
    /// <summary>
    /// Resolves the single ACTIVE revision whose half-open effective interval
    /// <c>[EffectiveFrom, EffectiveTo)</c> contains <paramref name="definitionEffectiveDate"/>.
    /// Null bounds are unbounded.
    /// </summary>
    Task<ProductDefinitionLoadResult> ResolveRevisionAsync(
        string companyCode,
        string productCode,
        DateTime definitionEffectiveDate,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a specific revision (by physical ID) with the same graph.</summary>
    Task<ProductDefinitionLoadResult> LoadRevisionAsync(
        long revisionId,
        CancellationToken cancellationToken = default);
}

public sealed class ProductDefinitionSnapshotLoader : IProductDefinitionSnapshotLoader
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public ProductDefinitionSnapshotLoader(IDbContextFactory<AppDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<ProductDefinitionLoadResult> ResolveRevisionAsync(
        string companyCode,
        string productCode,
        DateTime definitionEffectiveDate,
        CancellationToken cancellationToken = default)
    {
        var company = Normalize(companyCode);
        var product = Normalize(productCode);
        if (company.Length == 0 || product.Length == 0)
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                "Company code and product code are required to resolve a Product Definition revision.");
        }

        var effectiveDate = definitionEffectiveDate.Date;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var matches = await Graph(db.PrBomHdrs.AsNoTracking())
            .Where(h => h.CompanyCode == company
                        && h.ProdCode == product
                        && h.Status == PrBomStatuses.Active
                        && (h.EffectiveFrom == null || h.EffectiveFrom <= effectiveDate)
                        && (h.EffectiveTo == null || h.EffectiveTo > effectiveDate))
            .ToListAsync(cancellationToken);

        return Interpret(matches, company, product, effectiveDate);
    }

    public async Task<ProductDefinitionLoadResult> LoadRevisionAsync(
        long revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var matches = await Graph(db.PrBomHdrs.AsNoTracking())
            .Where(h => h.Uid == revisionId)
            .ToListAsync(cancellationToken);

        if (matches.Count == 0)
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                $"Product Definition revision {revisionId} was not found.");
        }

        return ProductDefinitionLoadResult.Ok(matches[0]);
    }

    private static ProductDefinitionLoadResult Interpret(
        List<PrBomHdr> matches,
        string company,
        string product,
        DateTime effectiveDate)
    {
        if (matches.Count == 0)
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                $"No active Product Definition revision for {company}/{product} covers {effectiveDate:yyyy-MM-dd}.");
        }

        if (matches.Count > 1)
        {
            // Never break an overlap by choosing a revision. Activation must reject the overlap
            // before a Work Order can encounter it.
            var versions = string.Join(", ", matches.Select(m => m.Version).OrderBy(v => v));
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionAmbiguous,
                $"{matches.Count} active Product Definition revisions for {company}/{product} cover "
                + $"{effectiveDate:yyyy-MM-dd} (versions {versions}). Resolve the effective-date overlap "
                + "before creating or refreshing a Work Order.",
                matches.Count);
        }

        return ProductDefinitionLoadResult.Ok(matches[0]);
    }

    /// <summary>
    /// One coherent read of the whole revision graph. Split queries avoid the cartesian explosion
    /// from several sibling collections.
    /// </summary>
    private static IQueryable<PrBomHdr> Graph(IQueryable<PrBomHdr> source) =>
        source
            .AsSplitQuery()
            .Include(h => h.Lines)
            .Include(h => h.Operations)
            .Include(h => h.RouteSteps)
                .ThenInclude(rs => rs.Operations)
                    .ThenInclude(o => o.Machines)
                        .ThenInclude(m => m.Labours)
            .Include(h => h.RouteSteps)
                .ThenInclude(rs => rs.Operations)
                    .ThenInclude(o => o.LabourRequirements)
            .Include(h => h.RouteSteps)
                .ThenInclude(rs => rs.Operations)
                    .ThenInclude(o => o.Materials);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();
}
