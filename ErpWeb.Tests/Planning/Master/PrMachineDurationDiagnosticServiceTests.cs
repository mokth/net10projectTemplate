using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Planning.Master;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class PrMachineDurationDiagnosticServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PrMachineDurationDiagnosticServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Reports_only_active_machine_options_without_a_positive_one_slot_duration()
    {
        await SeedAsync("FG-BAD", PrBomStatuses.Active, PrProcessTypes.Machine, cycleSeconds: 0m);
        await SeedAsync("FG-GOOD", PrBomStatuses.Active, PrProcessTypes.Machine, cycleSeconds: 60m);
        await SeedAsync("FG-DRAFT", PrBomStatuses.Draft, PrProcessTypes.Machine, cycleSeconds: 0m);
        await SeedAsync("FG-MANUAL", PrBomStatuses.Active, PrProcessTypes.Manual, cycleSeconds: 0m);

        var sut = new PrMachineDurationDiagnosticService(_factory);
        var findings = await sut.FindActiveMachineOptionsWithoutPositiveDurationAsync("DEMO");

        var finding = Assert.Single(findings);
        Assert.Equal("FG-BAD", finding.ProdCode);
        Assert.Equal(PrProductDefinitionCodes.Standard, finding.DefinitionCode);
        Assert.Equal("OP10", finding.OperationCode);
        Assert.Equal("MC01", finding.MachineCode);
        Assert.Contains("cannot produce a positive", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Setup_seconds_make_a_zero_cycle_machine_valid()
    {
        await SeedAsync(
            "FG-SETUP", PrBomStatuses.Active, PrProcessTypes.Machine, cycleSeconds: 0m, setupSeconds: 30m);

        var sut = new PrMachineDurationDiagnosticService(_factory);
        var findings = await sut.FindActiveMachineOptionsWithoutPositiveDurationAsync("DEMO");

        Assert.Empty(findings);
    }

    private async Task SeedAsync(
        string productCode,
        string status,
        string processType,
        decimal cycleSeconds,
        decimal setupSeconds = 0m)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var operation = new PrBomOperation
        {
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            OutputItemCode = productCode,
            CentralSequence = 10,
            OperationCode = "OP10",
            ProcessSequence = 10,
            ProcessType = processType,
            StandardDurationMinutes = 30m,
            IsFinalOperation = true,
            OutputBaseQty = 1m,
            OutputUom = "PCS",
            RowVersion = [1]
        };
        operation.Machines.Add(new PrBomMachineOption
        {
            MachineCode = "MC01",
            MachineDescription = "Diagnostic machine",
            IsPrimary = true,
            Priority = 1,
            CycleSeconds = cycleSeconds,
            SetupSeconds = setupSeconds,
            OutputPerCycle = 1m,
            ParallelMachineCount = 1,
            RowVersion = [1]
        });

        var header = new PrBomHdr
        {
            CompanyCode = "DEMO",
            ProdCode = productCode,
            DefinitionCode = PrProductDefinitionCodes.Standard,
            DefinitionName = PrProductDefinitionCodes.StandardName,
            IsDefaultDefinition = true,
            Version = 1,
            Status = status,
            EffectiveFrom = new DateTime(2026, 1, 1),
            BaseQty = 1m,
            BaseUom = "PCS",
            BranchCode = "HQ",
            LocationCode = "SITE",
            RowVersion = [1]
        };
        header.RouteSteps.Add(new PrBomRouteStep
        {
            CompanyCode = "DEMO",
            WorkCentreCode = "WC01",
            StageSequence = 10,
            OutputItemCode = productCode,
            OutputType = PrRouteOutputTypes.FinishedGoods,
            StandardOutputQty = 1m,
            OutputUom = "PCS",
            RowVersion = [1],
            Operations = { operation }
        });
        header.Operations.Add(operation);

        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync();
    }
}
