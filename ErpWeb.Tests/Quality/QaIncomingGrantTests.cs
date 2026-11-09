using ErpWeb.Core.Inventory;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaIncomingGrantTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public QaIncomingGrantTests()
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
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock A",
            StdUom = "EA",
            PurUom = "EA",
            StockControl = true,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Claim_is_noop_when_incoming_gate_is_off()
    {
        var (batch, details) = await SeedReceiptAsync(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, batch, details);
        Assert.Null(error);
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_fails_closed_when_gate_is_on()
    {
        await EnableIncomingAsync();
        var (batch, details) = await SeedReceiptAsync(qty: 4m);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, batch, details);
        Assert.Equal(QaReadinessMessages.IncomingFrozen, error);
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_does_not_consume_grant_while_incoming_is_frozen()
    {
        await EnableIncomingAsync();
        await SeedGrantAsync(accepted: 85m, rejected: 15m);
        var first = await SeedReceiptAsync(qty: 85m, batchNo: 10, deliveryNote: "DN-1");
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, first.Batch, first.Details);
        Assert.Equal(QaReadinessMessages.IncomingFrozen, error);
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Restore_reverses_posted_allocation_even_when_gate_is_off()
    {
        await SeedGrantAsync(accepted: 85m, rejected: 15m);
        var receipt = await SeedReceiptAsync(qty: 85m, deliveryNote: "DN-1");
        await using var db = await _factory.CreateDbContextAsync();
        var grant = await db.QaReceiptGrants.SingleAsync();
        db.QaReceiptAllocations.Add(new QaReceiptAllocation
        {
            GrantId = grant.Uid,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Qty = 85m,
            Status = QaReceiptAllocationStatuses.Posted,
            BatchNo = receipt.Batch.BatchNo,
            BatchDetailId = receipt.Details[0].Id,
            PostedAtUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var auth = new QaReceiptAuthorizationService();
        Assert.Null(await auth.RestoreGoodsReceiptAsync(
            db, "DEMO", "HQ", receipt.Batch.BatchNo, receipt.Details));
        await db.SaveChangesAsync();

        var allocation = Assert.Single(await db.QaReceiptAllocations.ToListAsync());
        Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
        Assert.NotNull(allocation.ReversedAtUtc);
    }

    [Fact]
    public async Task Non_stock_goods_receipt_does_not_require_a_grant()
    {
        await EnableIncomingAsync();
        var (batch, details) = await SeedReceiptAsync(qty: 4m, trxType: IvTrxTypes.NonStockGoodsReceive);
        await using var db = await _factory.CreateDbContextAsync();
        var error = await new QaReceiptAuthorizationService().ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.NonStockGoodsReceive, batch, details);
        Assert.Null(error);
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
    }

    [Fact]
    public async Task Claim_stays_frozen_for_a_repeat_post_while_the_gate_is_on()
    {
        await EnableIncomingAsync();
        await SeedGrantAsync(accepted: 85m, rejected: 15m);
        var receipt = await SeedReceiptAsync(qty: 85m, deliveryNote: "DN-1");
        await using var db = await _factory.CreateDbContextAsync();
        var auth = new QaReceiptAuthorizationService();
        Assert.Equal(QaReadinessMessages.IncomingFrozen, await auth.ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, receipt.Batch, receipt.Details));
        Assert.Equal(QaReadinessMessages.IncomingFrozen, await auth.ClaimGoodsReceiptAsync(
            db, "DEMO", "HQ", IvTrxTypes.GoodsReceive, receipt.Batch, receipt.Details));
        Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
    }

    private async Task EnableIncomingAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = "DEMO",
            Version = 1,
            Enabled = true,
            IncomingInspectionEnabled = true,
            IsPublished = true,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedGrantAsync(decimal accepted, decimal rejected)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var qaCase = new QaCase
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            CaseNo = "QC00000001",
            CaseDate = DateTime.Today,
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.IncomingPreGrn,
            DeliveryNote = "DN-1",
            DeliveryInstanceId = Guid.NewGuid(),
            PoNo = "PO1",
            PoRelNo = 0,
            EligibilityNote = QaEligibilityNotes.IncomingGrant,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester"
        };
        var line = new QaCaseLine
        {
            LineNo = 1,
            ItemCode = "A100",
            Uom = "EA",
            Qty = accepted + rejected,
            PoLineNo = 1
        };
        qaCase.Lines.Add(line);
        qaCase.Grants.Add(new QaReceiptGrant
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Case = qaCase,
            CaseLine = line,
            DeliveryInstanceId = qaCase.DeliveryInstanceId.Value,
            LineKey = 1,
            PoNo = "PO1",
            PoRelNo = 0,
            PoLineNo = 1,
            DeliveryNote = "DN-1",
            ItemCode = "A100",
            Uom = "EA",
            AcceptedQty = accepted,
            RejectedQty = rejected,
            Status = QaReceiptGrantStatuses.Open,
            ApprovedAtUtc = DateTime.UtcNow
        });
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

    private async Task<(IvTrxBatch Batch, IReadOnlyList<IvTrxBatchDetail> Details)> SeedReceiptAsync(
        decimal qty,
        int batchNo = 1,
        string deliveryNote = "DN-1",
        string trxType = IvTrxTypes.GoodsReceive)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var batch = new IvTrxBatch
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxDtTime = DateTime.Today,
            TrxType = trxType,
            BatchStatus = IvBatchStatuses.New,
            RefNo = deliveryNote
        };
        batch.Details.Add(new IvTrxBatchDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            BatchNo = batchNo,
            TrxLineNo = 1,
            TrxType = trxType,
            ICode = "A100",
            ToPurQty = qty,
            ToStdQty = qty,
            ToPurUom = "EA",
            ToStdUom = "EA",
            PoNo = "PO1",
            PoRelNo = 0,
            PoLineNo = 1
        });
        db.IvTrxBatches.Add(batch);
        await db.SaveChangesAsync();
        return (batch, batch.Details.ToList());
    }
}
