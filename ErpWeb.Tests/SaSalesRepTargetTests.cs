using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Sales-rep monthly targets (sales-analysis Phase 1). These guard the M3 contract: the row key is
/// company-wide (no branch), save is an upsert, and the month / year / amount validations hold.
/// </summary>
public class SaSalesRepTargetTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesRepTargetTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO",
            SrepCode = "SM1",
            SrepName = "Sales One",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveTarget_UpsertsTheSameCompanyWideMonth()
    {
        var sut = CreateSut();

        var first = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1",
            Year = 2026,
            Month = 9,
            TargetAmount = 1000m
        });
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(1000m, first.Data!.TargetAmount);

        // M3: re-saving the same month updates it. It must never fail as a duplicate logical target.
        var second = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1",
            Year = 2026,
            Month = 9,
            TargetAmount = 1500m
        });
        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(1500m, second.Data!.TargetAmount);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.SaSalesRepTargets.ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(1500m, row.TargetAmount);
        // R2: the key has no branch part, so one row serves every branch of the company.
        Assert.Equal("DEMO", row.CompanyCode);
    }

    [Fact]
    public async Task SaveTarget_ValidatesMonthYearAmountAndTheSalesRep()
    {
        var sut = CreateSut();

        var badMonth = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1", Year = 2026, Month = 13, TargetAmount = 10m
        });
        Assert.False(badMonth.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, badMonth.ErrorCode);
        Assert.Contains("Month", badMonth.ValidationErrors.Keys);

        var badYear = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1", Year = 0, Month = 1, TargetAmount = 10m
        });
        Assert.False(badYear.Succeeded);
        Assert.Contains("Year", badYear.ValidationErrors.Keys);

        var negative = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1", Year = 2026, Month = 1, TargetAmount = -1m
        });
        Assert.False(negative.Succeeded);
        Assert.Contains("TargetAmount", negative.ValidationErrors.Keys);

        var blankCode = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "  ", Year = 2026, Month = 1, TargetAmount = 10m
        });
        Assert.False(blankCode.Succeeded);
        Assert.Contains("Code", blankCode.ValidationErrors.Keys);

        // A code that no rep owns must not silently create an orphan target.
        var unknown = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "GHOST", Year = 2026, Month = 1, TargetAmount = 10m
        });
        Assert.False(unknown.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, unknown.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.SaSalesRepTargets.ToListAsync());
    }

    [Fact]
    public async Task ZeroTargetIsALegitimateExplicitValue()
    {
        var sut = CreateSut();

        var result = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1",
            Year = 2026,
            Month = 9,
            TargetAmount = 0m
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(0m, result.Data!.TargetAmount);
    }

    [Fact]
    public async Task ListTargets_ReturnsTheYearOrderedByMonth()
    {
        var sut = CreateSut();
        await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm { Code = "SM1", Year = 2026, Month = 10, TargetAmount = 300m });
        await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm { Code = "SM1", Year = 2026, Month = 8, TargetAmount = 100m });
        await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm { Code = "SM1", Year = 2027, Month = 1, TargetAmount = 999m });

        var result = await sut.ListSalesRepTargetsAsync("SM1", 2026);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal([8, 10], result.Data!.Select(x => x.Month).ToArray());

        var unknownRep = await sut.ListSalesRepTargetsAsync("GHOST", 2026);
        Assert.False(unknownRep.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, unknownRep.ErrorCode);
    }

    [Fact]
    public async Task DeleteTarget_RemovesOnlyThatMonth()
    {
        var sut = CreateSut();
        await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm { Code = "SM1", Year = 2026, Month = 9, TargetAmount = 100m });
        await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm { Code = "SM1", Year = 2026, Month = 10, TargetAmount = 200m });

        var deleted = await sut.DeleteSalesRepTargetAsync("SM1", 2026, 9);
        Assert.True(deleted.Succeeded, deleted.Message);

        var rows = await sut.ListSalesRepTargetsAsync("SM1", 2026);
        Assert.Equal([10], rows.Data!.Select(x => x.Month).ToArray());

        var missing = await sut.DeleteSalesRepTargetAsync("SM1", 2026, 9);
        Assert.False(missing.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, missing.ErrorCode);
    }

    [Fact]
    public async Task TargetWritesRequireTheEditRight()
    {
        var sut = CreateSut(canEdit: false);

        var save = await sut.SaveSalesRepTargetAsync(new SaSalesRepTargetEditVm
        {
            Code = "SM1", Year = 2026, Month = 9, TargetAmount = 100m
        });
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, save.ErrorCode);

        var delete = await sut.DeleteSalesRepTargetAsync("SM1", 2026, 9);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, delete.ErrorCode);
    }

    private SaSalesRefService CreateSut(
        string company = "DEMO",
        bool canAccess = true,
        bool canEdit = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);

        var tenant = InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE");
        return new SaSalesRefService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustLookupService(_factory, tenant));
    }
}
