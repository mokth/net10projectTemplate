using ErpWeb.Core.Inventory;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Other;

[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class TransactionDeletePolicyTests : IDisposable
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly TransactionDeletePolicyService _policy = new();
    private readonly long _epochId;
    private int _batchNo = 100;
    private long _sequence = 1;

    public TransactionDeletePolicyTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        var epoch = new StockLedgerEpoch
        {
            CompanyCode = Company,
            BranchCode = Branch,
            EffectiveFrom = new DateTime(2026, 1, 1),
            CutoverPostingSequence = 1,
            Version = 2,
            Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(),
            ReconciliationManifestHash = new string('A', 64),
            ActivatedAtUtc = DateTime.UtcNow,
            ActivatedBy = "tester"
        };
        _db.StockLedgerEpochs.Add(epoch);
        _db.SaveChanges();
        _epochId = epoch.Id;
    }

    [Fact]
    public async Task No_history_is_a_hard_delete()
    {
        var batch = await AddBatchAsync(IvTrxTypes.MiscellaneousReceipt);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.HardDeleteDraft, decision.Mode);
        Assert.False(decision.HasHistoricalPosting);
        Assert.False(decision.HasActivePosting);
        Assert.Null(decision.BlockingReason);
    }

    [Fact]
    public async Task Sealed_primary_without_reversal_blocks()
    {
        var batch = await AddBatchAsync(IvTrxTypes.GoodsReceive, IvBatchStatuses.Posted, postedCount: 1);
        await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: true);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.Block, decision.Mode);
        Assert.True(decision.HasActivePosting);
        Assert.Equal(TransactionDeleteMessages.RollbackFirst, decision.BlockingReason);
    }

    [Fact]
    public async Task Sealed_primary_and_sealed_reversal_archives()
    {
        var batch = await AddBatchAsync(IvTrxTypes.MiscellaneousIssue, postedCount: 1, rollbackCount: 1);
        var primary = await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: true);
        await AddPostingAsync(batch, revision: 1, role: "REVERSAL", isSealed: true, reverses: primary);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.ArchiveHistorical, decision.Mode);
        Assert.True(decision.HasHistoricalPosting);
        Assert.False(decision.HasActivePosting);
        Assert.Null(decision.BlockingReason);
    }

    [Fact]
    public async Task Unsealed_primary_blocks_for_costing_center()
    {
        var batch = await AddBatchAsync(IvTrxTypes.StockAdjustment);
        await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: false);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.Block, decision.Mode);
        Assert.True(decision.HasUnsealedPosting);
        Assert.Equal(TransactionDeleteMessages.IncompletePosting, decision.BlockingReason);
    }

    [Fact]
    public async Task Fully_reversed_generations_archive()
    {
        var batch = await AddBatchAsync(IvTrxTypes.StockTransfer, postedCount: 2, rollbackCount: 2);
        var first = await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: true);
        await AddPostingAsync(batch, revision: 1, role: "REVERSAL", isSealed: true, reverses: first);
        var second = await AddPostingAsync(batch, revision: 2, role: "PRIMARY", isSealed: true);
        await AddPostingAsync(batch, revision: 2, role: "REVERSAL", isSealed: true, reverses: second);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.ArchiveHistorical, decision.Mode);
        Assert.False(decision.HasActivePosting);
    }

    [Fact]
    public async Task Latest_active_generation_blocks_even_when_older_generations_reversed()
    {
        var batch = await AddBatchAsync(IvTrxTypes.Scrap, IvBatchStatuses.Posted, postedCount: 2, rollbackCount: 1);
        var first = await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: true);
        await AddPostingAsync(batch, revision: 1, role: "REVERSAL", isSealed: true, reverses: first);
        await AddPostingAsync(batch, revision: 2, role: "PRIMARY", isSealed: true);
        var decision = await EvaluateAsync(batch);

        Assert.Equal(TransactionDeleteMode.Block, decision.Mode);
        Assert.True(decision.HasActivePosting);
    }

    [Fact]
    public async Task History_without_stock_posting_is_fail_safe_historical()
    {
        var batch = await AddBatchAsync(IvTrxTypes.CustomerReturn);
        _db.IvTrxHistories.Add(new IvTrxHistory
        {
            CompanyCode = Company,
            BranchCode = Branch,
            BatchNo = batch.BatchNo,
            TrxType = batch.TrxType,
            BatchStatus = IvBatchStatuses.Posted,
            ICode = "ITEM1",
            TrxDtTime = new DateTime(2026, 10, 1)
        });
        await _db.SaveChangesAsync();

        var decision = await EvaluateAsync(batch);
        Assert.Equal(TransactionDeleteMode.ArchiveHistorical, decision.Mode);
        Assert.False(decision.HasHistoricalPosting);
        Assert.True(decision.HasHistoricalMovement);
    }

    [Fact]
    public async Task Production_movement_without_ledger_source_is_fail_safe_historical()
    {
        var batch = await AddBatchAsync(IvTrxTypes.IssueToProduction);
        await _db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        _db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
        {
            CompanyCode = Company,
            BranchCode = Branch,
            WorkOrderId = 1,
            WorkOrderMaterialId = 1,
            WorkOrderOperationId = 1,
            MovementType = "ISSUE",
            MovementDate = new DateTime(2026, 10, 1),
            ItemCode = "RM1",
            Qty = 1,
            Uom = "EA",
            BaseQty = 1,
            BaseUom = "EA",
            ConversionFactorToBase = 1,
            WarehouseCode = "WH",
            LocationCode = "LOC",
            LotNo = "L1",
            ItemStatus = "OK",
            InventoryBatchId = batch.Id,
            InventoryBatchNo = batch.BatchNo,
            PostingLinkId = 1,
            CreatedDate = new DateTime(2026, 10, 1),
            CreatedBy = "tester"
        });
        await _db.SaveChangesAsync();

        var decision = await _policy.EvaluateAsync(_db, Subject(batch, TransactionDeleteOwnerTypes.ProductionMaterialIssue));
        Assert.Equal(TransactionDeleteMode.ArchiveHistorical, decision.Mode);
        Assert.False(decision.HasHistoricalPosting);
        Assert.False(decision.HasActivePosting);
    }

    [Fact]
    public async Task Already_archived_batch_is_idempotent()
    {
        var batch = await AddBatchAsync(IvTrxTypes.VendorReturn, postedCount: 1, rollbackCount: 1);
        var primary = await AddPostingAsync(batch, revision: 1, role: "PRIMARY", isSealed: true);
        await AddPostingAsync(batch, revision: 1, role: "REVERSAL", isSealed: true, reverses: primary);
        batch.DeletedAtUtc = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
        batch.DeletedBy = "tester";
        batch.DeleteReason = TransactionDeleteMessages.DefaultArchiveReason;
        await _db.SaveChangesAsync();

        var first = await EvaluateAsync(batch);
        var second = await EvaluateAsync(batch);
        Assert.Equal(TransactionDeleteMode.ArchiveHistorical, first.Mode);
        Assert.Equal(first, second);
        Assert.Null(first.BlockingReason);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<TransactionDeleteDecision> EvaluateAsync(IvTrxBatch batch) =>
        _policy.EvaluateAsync(_db, Subject(batch, TransactionDeleteOwnerTypes.InventoryBatch));

    private static TransactionDeleteSubject Subject(IvTrxBatch batch, string ownerType) =>
        new(Company, Branch, ownerType, batch.Id.ToString(), batch.BatchNo.ToString());

    private async Task<IvTrxBatch> AddBatchAsync(
        string trxType,
        string status = IvBatchStatuses.New,
        int postedCount = 0,
        int rollbackCount = 0)
    {
        var batch = new IvTrxBatch
        {
            CompanyCode = Company,
            BranchCode = Branch,
            BatchNo = ++_batchNo,
            TrxDtTime = new DateTime(2026, 10, 1),
            TrxType = trxType,
            BatchStatus = status,
            PostedCount = postedCount,
            RollbackCount = rollbackCount
        };
        _db.IvTrxBatches.Add(batch);
        await _db.SaveChangesAsync();
        return batch;
    }

    private async Task<long> AddPostingAsync(
        IvTrxBatch batch, int revision, string role, bool isSealed, long? reverses = null)
    {
        var posting = new StockPosting
        {
            CompanyCode = Company,
            BranchCode = Branch,
            LedgerEpochId = _epochId,
            PostingSequence = _sequence++,
            RequestId = Guid.NewGuid(),
            CommandType = $"IV_{role}_{batch.TrxType}_{revision}",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY",
            SourceDocumentType = batch.TrxType,
            SourceDocumentId = batch.Id.ToString(),
            SourceDocumentNo = batch.BatchNo.ToString(),
            DocumentRevision = revision,
            PostingRole = role,
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64),
            EffectiveAt = new DateTime(2026, 10, 2),
            BusinessDate = new DateTime(2026, 10, 2),
            PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "tester",
            SealedAtUtc = isSealed ? DateTime.UtcNow : null,
            ReversesPostingId = reverses
        };
        _db.StockPostings.Add(posting);
        await _db.SaveChangesAsync();
        return posting.Id;
    }
}
