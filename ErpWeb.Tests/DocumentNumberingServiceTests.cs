using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

public class DocumentNumberingServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public DocumentNumberingServiceTests()
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

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Monthly_first_and_second_and_new_month_reset()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "INV",
                Prefix = "INV",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var sut = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"));
        await using var db2 = await _factory.CreateDbContextAsync();
        var first = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV2609-0001", first.DocumentNumber);
        Assert.Equal("INV", first.PrefixUsed);

        var second = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV2609-0002", second.DocumentNumber);

        var oct = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 10, 1), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV2610-0001", oct.DocumentNumber);

        var sepRow = await db2.AdSmNumDates.SingleAsync(x => x.Year == 2026 && x.Month == 9);
        Assert.Equal(3, sepRow.Seq);
        var octRow = await db2.AdSmNumDates.SingleAsync(x => x.Year == 2026 && x.Month == 10);
        Assert.Equal(2, octRow.Seq);
    }

    [Fact]
    public async Task Continuous_adsmnum_when_no_date_rows()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                NumCd = "INV",
                Prefix = "INV",
                TotLength = 10,
                Seq = 1
            });
            await db.SaveChangesAsync();
        }

        var sut = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"));
        await using var db2 = await _factory.CreateDbContextAsync();
        var first = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV0000001", first.DocumentNumber);
        var second = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV0000002", second.DocumentNumber);
    }

    [Fact]
    public async Task Missing_config_throws()
    {
        var sut = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext());
        await using var db = await _factory.CreateDbContextAsync();
        await Assert.ThrowsAsync<DocumentNumberingNotConfiguredException>(() =>
            sut.NextAsync(db, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default));
    }

    [Fact]
    public async Task Edit_does_not_allocate()
    {
        var sut = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext());
        await using var db = await _factory.CreateDbContextAsync();
        var result = await sut.NextAsync(db, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.Edit, "INV2609-0009", default);
        Assert.Equal("INV2609-0009", result.DocumentNumber);
    }

    [Fact]
    public async Task Branches_independent()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO", BranchCode = "HQ", Year = 2026, Month = 9, NumCd = "INV",
                Prefix = "INV", TotLength = 4, NumberingDelimeter = "-", Seq = 1, RowVersion = [1]
            });
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO", BranchCode = "BR2", Year = 2026, Month = 9, NumCd = "INV",
                Prefix = "INV", TotLength = 4, NumberingDelimeter = "-", Seq = 1, RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var hq = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext(branch: "HQ", location: "MAIN"));
        var br2 = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext(branch: "BR2", location: "MAIN"));
        await using var db2 = await _factory.CreateDbContextAsync();
        var a = await hq.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        var b = await br2.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV2609-0001", a.DocumentNumber);
        Assert.Equal("INV2609-0001", b.DocumentNumber);
    }

    [Fact]
    public async Task Date_rows_win_over_continuous()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO", BranchCode = "HQ", NumCd = "INV", Prefix = "C", TotLength = 5, Seq = 1
            });
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO", BranchCode = "HQ", Year = 2026, Month = 9, NumCd = "INV",
                Prefix = "INV", TotLength = 4, NumberingDelimeter = "-", Seq = 1, RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var sut = new DocumentNumberingService(InventoryTenantTestHelper.CreateTenantContext(location: "MAIN"));
        await using var db2 = await _factory.CreateDbContextAsync();
        var result = await sut.NextAsync(db2, "INV", "", new DateTime(2026, 9, 2), DocumentNumberRequestMode.New, "AUTO", default);
        Assert.Equal("INV2609-0001", result.DocumentNumber);
    }
}
