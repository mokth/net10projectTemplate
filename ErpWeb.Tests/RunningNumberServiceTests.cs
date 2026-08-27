using ErpWeb.Core.Numbering;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests;

public class RunningNumberServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly RunningNumberService _sut = new();

    public RunningNumberServiceTests()
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
    public async Task Peek_does_not_consume_GetNext_increments()
    {
        await using var db = await _factory.CreateDbContextAsync();

        var peek1 = await _sut.PeekNextAsync(db, "DEMO", RunningNumberKeys.IvBatch);
        var first = await _sut.GetNextAsync(db, "DEMO", RunningNumberKeys.IvBatch);
        var peek2 = await _sut.PeekNextAsync(db, "DEMO", RunningNumberKeys.IvBatch);
        var second = await _sut.GetNextAsync(db, "DEMO", RunningNumberKeys.IvBatch);

        Assert.Equal(1, peek1);
        Assert.Equal(1, first);
        Assert.Equal(2, peek2);
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task Numbers_are_isolated_by_company_and_doc_key()
    {
        await using var db = await _factory.CreateDbContextAsync();

        var demoBatch = await _sut.GetNextAsync(db, "DEMO", RunningNumberKeys.IvBatch);
        var otherBatch = await _sut.GetNextAsync(db, "OTH", RunningNumberKeys.IvBatch);
        var demoOtherKey = await _sut.GetNextAsync(db, "DEMO", "PO");

        Assert.Equal(1, demoBatch);
        Assert.Equal(1, otherBatch);
        Assert.Equal(1, demoOtherKey);

        var row = await db.MsRunningNos.SingleAsync(x =>
            x.CompanyCode == "DEMO" && x.DocKey == RunningNumberKeys.IvBatch);
        Assert.Equal(1, row.LastNo);
    }

    [Fact]
    public async Task Continues_from_existing_LastNo()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MsRunningNos.Add(new MsRunningNo
            {
                CompanyCode = "DEMO",
                DocKey = RunningNumberKeys.IvBatch,
                LastNo = 29255
            });
            await db.SaveChangesAsync();
        }

        await using var next = await _factory.CreateDbContextAsync();
        var allocated = await _sut.GetNextAsync(next, "demo", RunningNumberKeys.IvBatch);
        Assert.Equal(29256, allocated);
    }
}
