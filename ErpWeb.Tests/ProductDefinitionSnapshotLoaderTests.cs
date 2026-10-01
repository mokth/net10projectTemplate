using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

/// <summary>
/// Plan §7.2 — revision resolution must be cardinality-safe. Zero matches and overlapping
/// matches both fail; a tie is never broken by picking a revision.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductDefinitionSnapshotLoaderTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly ProductDefinitionSnapshotLoader _loader;

    public ProductDefinitionSnapshotLoaderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _loader = new ProductDefinitionSnapshotLoader(_factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private static PrBomHdr Revision(
        int version,
        DateTime? from,
        DateTime? to,
        string status = PrBomStatuses.Active) => new()
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            DefinitionName = PrProductDefinitionCodes.StandardName,
            IsDefaultDefinition = true,
            Version = version,
            Status = status,
            EffectiveFrom = from,
            EffectiveTo = to,
            BaseQty = 1m,
            BaseUom = "PCS",
            RowVersion = [1],
        };

    private async Task AddAsync(params PrBomHdr[] headers)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PrBomHdrs.AddRange(headers);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Resolves_single_active_revision_covering_the_effective_date()
    {
        await AddAsync(
            Revision(1, new DateTime(2026, 1, 1), new DateTime(2026, 6, 1), PrBomStatuses.Superseded),
            Revision(2, new DateTime(2026, 6, 1), null));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Revision!.Version);
        Assert.Null(result.FailureCode);
    }

    [Fact]
    public async Task Boundary_is_half_open_inclusive_from_exclusive_to()
    {
        await AddAsync(
            Revision(1, new DateTime(2026, 6, 1), new DateTime(2026, 9, 1)),
            Revision(2, new DateTime(2026, 9, 1), null));

        var onFrom = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");
        var onTo = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.Equal(1, onFrom.Revision!.Version);
        Assert.Equal(2, onTo.Revision!.Version);
    }

    [Fact]
    public async Task Open_ended_bounds_cover_any_date()
    {
        await AddAsync(Revision(1, null, null));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.Revision!.Version);
    }

    [Fact]
    public async Task No_active_revision_reports_not_found()
    {
        await AddAsync(
            Revision(1, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1)),
            Revision(2, new DateTime(2026, 3, 1), new DateTime(2026, 4, 1)));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
        Assert.Equal(0, result.MatchingRevisionCount);
    }

    [Fact]
    public async Task Draft_revision_is_never_selected()
    {
        await AddAsync(Revision(4, new DateTime(2026, 1, 1), null, PrBomStatuses.Draft));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Overlapping_active_revisions_are_ambiguous_and_do_not_pick_a_winner()
    {
        await AddAsync(
            Revision(1, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31)),
            Revision(2, new DateTime(2026, 6, 1), null));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.False(result.Succeeded);
        Assert.Null(result.Revision);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionAmbiguous, result.FailureCode);
        Assert.Equal(2, result.MatchingRevisionCount);
    }

    [Fact]
    public async Task Company_and_product_are_matched_case_insensitively_on_normalized_input()
    {
        await AddAsync(Revision(1, null, null));

        var result = await _loader.ResolveActiveRevisionAsync(" demo ", "fg001", "STANDARD");

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Unknown_product_reports_not_found()
    {
        await AddAsync(Revision(1, null, null));

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG999", "STANDARD");

        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Resolved_revision_is_loaded_with_its_route_and_material_graph()
    {
        var header = Revision(1, new DateTime(2026, 1, 1), null);
        var step = new PrBomRouteStep
        {
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            StageSequence = 10,
            OutputItemCode = "WIP01",
            StandardOutputQty = 1m,
            OutputUom = "PCS",
            RowVersion = [1],
        };
        var operation = new PrBomOperation
        {
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            OutputItemCode = "WIP01",
            CentralSequence = 10,
            OperationCode = "OP10",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Machine,
            StandardDurationMinutes = 12m,
            IsFinalOperation = true,
            RowVersion = [1],
        };
        operation.Machines.Add(new PrBomMachineOption
        {
            MachineCode = "MC01",
            Priority = 1,
            OutputPerCycle = 10m,
            CycleSeconds = 30m,
            RowVersion = [1],
        });
        step.Operations.Add(operation);
        header.Operations.Add(operation);
        header.RouteSteps.Add(step);
        header.Lines.Add(new PrDefBOM
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "RM001",
            StdQty = 2m,
            StdUom = "KG",
            SeqNo = 1,
            ScrapPercent = 5m,
            RowVersion = [1],
        });
        operation.Materials.Add(header.Lines.First());

        await AddAsync(header);

        var result = await _loader.ResolveActiveRevisionAsync("DEMO", "FG001", "STANDARD");

        Assert.True(result.Succeeded);
        var loadedStep = Assert.Single(result.Revision!.RouteSteps);
        var loadedOperation = Assert.Single(loadedStep.Operations);
        Assert.Equal("OP10", loadedOperation.OperationCode);
        Assert.Single(loadedOperation.Machines);
        Assert.Single(loadedOperation.Materials);
        Assert.Single(result.Revision.Lines);
    }

    [Fact]
    public async Task Load_revision_by_id_returns_the_exact_revision()
    {
        var header = Revision(7, null, null, PrBomStatuses.Superseded);
        await AddAsync(header);

        var result = await _loader.LoadRevisionAsync(header.Uid);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Revision!.Version);
    }

    [Fact]
    public async Task Load_revision_by_missing_id_reports_not_found()
    {
        var result = await _loader.LoadRevisionAsync(4242);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
    }

    [Fact]
    public async Task Blank_company_or_product_is_rejected_without_querying()
    {
        var result = await _loader.ResolveActiveRevisionAsync("  ", "FG001", "STANDARD");

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.DefinitionRevisionNotFound, result.FailureCode);
    }
}
