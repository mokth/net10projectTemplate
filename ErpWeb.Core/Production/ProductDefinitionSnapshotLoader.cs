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
    /// Resolves the single ACTIVE revision for <paramref name="companyCode"/> +
    /// <paramref name="productCode"/> + <paramref name="definitionCode"/>.
    /// </summary>
    Task<ProductDefinitionLoadResult> ResolveActiveRevisionAsync(
        string companyCode,
        string productCode,
        string definitionCode,
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

    public async Task<ProductDefinitionLoadResult> ResolveActiveRevisionAsync(
        string companyCode,
        string productCode,
        string definitionCode,
        CancellationToken cancellationToken = default)
    {
        var company = Normalize(companyCode);
        var product = Normalize(productCode);
        var definition = PrProductDefinitionCodes.Normalize(definitionCode);
        if (company.Length == 0 || product.Length == 0)
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                "Company code and product code are required to resolve a Product Definition revision.");
        }

        if (definition.Length == 0 || !PrProductDefinitionCodes.IsValidFormat(definition))
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                "A valid Product Definition code is required to resolve an ACTIVE revision.");
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var matches = await Graph(db.PrBomHdrs.AsNoTracking())
            .Where(h => h.CompanyCode == company
                        && h.ProdCode == product
                        && h.DefinitionCode == definition
                        && h.Status == PrBomStatuses.Active)
            .ToListAsync(cancellationToken);

        return Interpret(matches, company, product, definition);
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
        string definition)
    {
        if (matches.Count == 0)
        {
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionNotFound,
                $"No ACTIVE Product Definition revision for {company}/{product}/{definition}.");
        }

        if (matches.Count > 1)
        {
            // Never break a multi-ACTIVE collision by choosing a revision. Activation must reject
            // the overlap before a Work Order can encounter it.
            var versions = string.Join(", ", matches.Select(m => m.Version).OrderBy(v => v));
            return ProductDefinitionLoadResult.Fail(
                ProductionReadinessErrorCodes.DefinitionRevisionAmbiguous,
                $"{matches.Count} ACTIVE Product Definition revisions for {company}/{product}/{definition} "
                + $"(versions {versions}). Resolve the multi-ACTIVE collision before creating or "
                + "refreshing a Work Order.",
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
