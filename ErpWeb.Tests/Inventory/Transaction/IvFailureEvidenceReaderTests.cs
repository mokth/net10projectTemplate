using ErpWeb.Core.Inventory;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>
/// The read-only evidence lookup behind "which document would this rollback break?". It must name the
/// affected transaction only when the row is proven to exist, belong to the caller's company and branch,
/// be an original posting and still be posted. Everything else — missing, foreign, reversal, stale,
/// unreadable, cancelled — must return null so the operator gets the safe generic wording.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public class IvFailureEvidenceReaderTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IvFailureEvidenceReader _sut;

    public IvFailureEvidenceReaderTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _sut = new IvFailureEvidenceReader(_factory);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ─────────────────── Proven evidence ───────────────────

    [Fact]
    public async Task Issue_row_reports_item_and_source_location()
    {
        var id = await SeedAsync();

        var evidence = await _sut.TryReadAsync(Company, Branch, id);

        Assert.NotNull(evidence);
        Assert.Equal(ErpDocumentKind.MiscellaneousIssue, evidence!.DocumentKind);
        Assert.Equal("7", evidence.DocumentNumber);
        Assert.Equal(new DateTime(2026, 9, 12), evidence.TransactionDate);
        Assert.Equal("A100", evidence.ItemCode);
        Assert.Equal("Stock item", evidence.ItemDescription);
        Assert.Equal("MAIN", evidence.Warehouse);
        Assert.Equal("BIN1", evidence.Location);
        Assert.True(evidence.HasDocument);
        Assert.False(evidence.IsStockCountGeneration);
    }

    [Fact]
    public async Task Receipt_row_reports_destination_location()
    {
        var id = await SeedAsync(row =>
        {
            row.TrxType = IvTrxTypes.GoodsReceive;
            row.FrWarehouse = null;
            row.FrLocation = null;
            row.FrLotNo = null;
            row.FrStdQty = null;
            row.ToWarehouse = "STORE";
            row.ToLocation = "BIN9";
            row.ToLotNo = "LOT-1";
            row.ToStdQty = 4m;
        });

        var evidence = await _sut.TryReadAsync(Company, Branch, id);

        Assert.NotNull(evidence);
        Assert.Equal(ErpDocumentKind.GoodsReceipt, evidence!.DocumentKind);
        Assert.Equal("STORE", evidence.Warehouse);
        Assert.Equal("BIN9", evidence.Location);
        Assert.Equal("LOT-1", evidence.LotNo);
    }

    [Fact]
    public async Task Transfer_row_reports_the_item_but_not_an_ambiguous_location()
    {
        var id = await SeedAsync(row =>
        {
            row.TrxType = IvTrxTypes.StockTransfer;
            row.ToWarehouse = "STORE";
            row.ToLocation = "BIN9";
            row.ToStdQty = 5m;
        });

        var evidence = await _sut.TryReadAsync(Company, Branch, id);

        Assert.NotNull(evidence);
        Assert.Equal(ErpDocumentKind.StockTransfer, evidence!.DocumentKind);
        Assert.Equal("A100", evidence.ItemCode);
        Assert.Null(evidence.Warehouse);
        Assert.Null(evidence.Location);
        Assert.Null(evidence.LotNo);
    }

    // ─────────────────── Omitted evidence ───────────────────

    [Fact]
    public async Task Reversal_row_is_ignored()
    {
        var id = await SeedAsync(row => row.EntryRole = "REVERSAL");

        Assert.Null(await _sut.TryReadAsync(Company, Branch, id));
    }

    [Fact]
    public async Task Row_that_reverses_another_is_ignored()
    {
        var target = await SeedAsync();
        var id = await SeedAsync(row => row.ReversesHistoryId = target);

        Assert.Null(await _sut.TryReadAsync(Company, Branch, id));
    }

    [Fact]
    public async Task Stale_row_is_ignored()
    {
        var id = await SeedAsync(row => row.BatchStatus = IvBatchStatuses.New);

        Assert.Null(await _sut.TryReadAsync(Company, Branch, id));
    }

    [Fact]
    public async Task Missing_row_returns_null()
    {
        Assert.Null(await _sut.TryReadAsync(Company, Branch, 999_999));
    }

    [Fact]
    public async Task Foreign_company_or_branch_returns_null()
    {
        var otherCompany = await SeedAsync(company: "OTHER");
        var otherBranch = await SeedAsync(branch: "BR2");

        Assert.Null(await _sut.TryReadAsync(Company, Branch, otherCompany));
        Assert.Null(await _sut.TryReadAsync(Company, Branch, otherBranch));
        Assert.NotNull(await _sut.TryReadAsync("OTHER", Branch, otherCompany));
    }

    [Fact]
    public async Task Negative_event_id_reports_a_count_generation_without_a_document()
    {
        var evidence = await _sut.TryReadAsync(Company, Branch, -12);

        Assert.NotNull(evidence);
        Assert.True(evidence!.IsStockCountGeneration);
        Assert.Equal(ErpDocumentKind.StockCount, evidence.DocumentKind);
        Assert.Null(evidence.DocumentNumber);
        Assert.Null(evidence.TransactionDate);
        Assert.False(evidence.HasDocument);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    public async Task Out_of_range_event_id_returns_null(long eventId)
    {
        Assert.Null(await _sut.TryReadAsync(Company, Branch, eventId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_scope_returns_null(string scope)
    {
        var id = await SeedAsync();

        Assert.Null(await _sut.TryReadAsync(scope, Branch, id));
        Assert.Null(await _sut.TryReadAsync(Company, scope, id));
    }

    [Fact]
    public async Task Unreadable_database_returns_null_instead_of_throwing()
    {
        var broken = new IvFailureEvidenceReader(new ThrowingFactory());

        Assert.Null(await broken.TryReadAsync(Company, Branch, 1));
    }

    [Fact]
    public async Task Cancelled_lookup_returns_null()
    {
        var id = await SeedAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        Assert.Null(await _sut.TryReadAsync(Company, Branch, id, cancelled.Token));
    }

    // ─────────────────── helpers ───────────────────

    private int _seedSequence = 7;

    private async Task<int> SeedAsync(
        Action<IvTrxHistory>? configure = null,
        string company = Company,
        string branch = Branch)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var row = new IvTrxHistory
        {
            CompanyCode = company,
            BranchCode = branch,
            BatchNo = _seedSequence++,
            TrxLineNo = 1,
            TrxDtTime = new DateTime(2026, 9, 12),
            TrxType = IvTrxTypes.MiscellaneousIssue,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "A100",
            IDesc = "Stock item",
            EntryRole = "ORIGINAL",
            FrWarehouse = "MAIN",
            FrLocation = "BIN1",
            FrLotNo = string.Empty,
            FrStdQty = 5m
        };
        configure?.Invoke(row);
        db.IvTrxHistories.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private sealed class ThrowingFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("The database is unavailable.");
    }
}
