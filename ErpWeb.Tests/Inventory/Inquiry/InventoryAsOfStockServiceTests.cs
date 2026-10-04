using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class InventoryAsOfStockServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor()).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        _factory = new TestDbContextFactory(options);
    }

    [Fact]
    public async Task Reverses_post_time_inbound_and_caps_at_current_quantity()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.IvBalLocs.Add(new IvBalLoc { Id = 1, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "A", WhCode = "W", LocCode = "L", LotNo = "", IStatus = "ACTIVE", StdQty = 100m, RowVersion = [1] });
            db.IvTrxHistories.Add(new IvTrxHistory { CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = 1, TrxLineNo = 1,
                TrxDtTime = new DateTime(2026, 1, 1, 15, 0, 0), TrxType = "MR", BatchStatus = "POSTED", ICode = "A",
                ToBalLocId = 1, ToStdQty = 80m });
            await db.SaveChangesAsync();
        }
        await using var read = await _factory.CreateDbContextAsync();
        var result = await new InventoryAsOfStockService().GetAsync(read, "DEMO", "HQ", [1], new DateTime(2026, 1, 1, 10, 30, 0));
        Assert.Equal(100m, result[1].CurrentBaseQty);
        Assert.Equal(20m, result[1].AsOfBaseQty);
        Assert.Equal(20m, result[1].UsableBaseQty);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
