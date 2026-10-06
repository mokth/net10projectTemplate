using ErpWeb.Core.Inventory;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Other;

[Trait(TestCategories.Name, TestCategories.Shared)]
public sealed class TransactionDeleteRetentionTests : IDisposable
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";

    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;

    public TransactionDeleteRetentionTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _db.StockLedgerEpochs.Add(new StockLedgerEpoch
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
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task True_draft_is_physically_removed()
    {
        var batch = await AddBatchAsync(41);
        var removed = await InventoryBatchRetention.TryRemoveTrueDraftAsync(_db, batch);
        await _db.SaveChangesAsync();

        Assert.True(removed);
        Assert.False(await _db.IvTrxBatches.AnyAsync(x => x.BatchNo == 41));
    }

    [Fact]
    public async Task Historical_header_is_kept_and_archive_does_not_change_postings()
    {
        var batch = await AddBatchAsync(42, postedCount: 1, rollbackCount: 1);
        var posting = new StockPosting
        {
            CompanyCode = Company,
            BranchCode = Branch,
            LedgerEpochId = _db.StockLedgerEpochs.Single().Id,
            PostingSequence = 1,
            RequestId = Guid.NewGuid(),
            CommandType = "IV_PRIMARY_MR_1",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY",
            SourceDocumentType = batch.TrxType,
            SourceDocumentId = batch.Id.ToString(),
            SourceDocumentNo = batch.BatchNo.ToString(),
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64),
            EffectiveAt = new DateTime(2026, 10, 2),
            BusinessDate = new DateTime(2026, 10, 2),
            PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "tester",
            SealedAtUtc = DateTime.UtcNow
        };
        var reversal = new StockPosting
        {
            CompanyCode = Company,
            BranchCode = Branch,
            LedgerEpochId = posting.LedgerEpochId,
            PostingSequence = 2,
            RequestId = Guid.NewGuid(),
            CommandType = "IV_REVERSAL_MR_1",
            RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY",
            SourceDocumentType = batch.TrxType,
            SourceDocumentId = batch.Id.ToString(),
            SourceDocumentNo = batch.BatchNo.ToString(),
            DocumentRevision = 1,
            PostingRole = "REVERSAL",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('E', 64),
            EffectiveAt = new DateTime(2026, 10, 3),
            BusinessDate = new DateTime(2026, 10, 3),
            PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "tester",
            SealedAtUtc = DateTime.UtcNow,
            ReversesPostingId = 0
        };
        _db.StockPostings.Add(posting);
        await _db.SaveChangesAsync();
        reversal.ReversesPostingId = posting.Id;
        _db.StockPostings.Add(reversal);
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
        var hash = posting.SourceSnapshotHash;
        var count = await _db.StockPostings.CountAsync();

        var removed = await InventoryBatchRetention.ReleaseOwnedNewBatchAsync(_db, batch);
        var error = await TransactionDeleteApplicator.ApplyInventoryBatchAsync(
            _db, batch, TransactionDeleteOwnerTypes.InventoryBatch, "tester", null);
        await _db.SaveChangesAsync();

        Assert.False(removed);
        Assert.Null(error);
        Assert.NotNull(batch.DeletedAtUtc);
        Assert.Equal(hash, (await _db.StockPostings.SingleAsync(x => x.Id == posting.Id)).SourceSnapshotHash);
        Assert.Equal(count, await _db.StockPostings.CountAsync());
        Assert.True(await _db.IvTrxBatches.AnyAsync(x => x.Id == batch.Id && x.DeletedAtUtc != null));
    }

    [Fact]
    public async Task Repository_refuses_to_delete_a_historical_new_batch()
    {
        var batch = await AddBatchAsync(43);
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

        var deleted = await new IvStockTransactionRepository().DeleteNewAsync(_db, Company, Branch, batch.BatchNo);

        Assert.False(deleted);
        Assert.True(await _db.IvTrxBatches.AnyAsync(x => x.BatchNo == 43));
    }

    [Fact]
    public async Task Archived_new_batch_is_excluded_from_the_reservation_filter()
    {
        var live = await AddBatchAsync(44);
        var archived = await AddBatchAsync(45);
        archived.DeletedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var reserved = await _db.IvTrxBatches.CountAsync(x =>
            x.CompanyCode == Company && x.BranchCode == Branch
            && x.BatchStatus == IvBatchStatuses.New
            && x.DeletedAtUtc == null);

        Assert.Equal(44, live.BatchNo);
        Assert.Equal(1, reserved);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private async Task<IvTrxBatch> AddBatchAsync(int batchNo, int postedCount = 0, int rollbackCount = 0)
    {
        var batch = new IvTrxBatch
        {
            CompanyCode = Company,
            BranchCode = Branch,
            BatchNo = batchNo,
            TrxDtTime = new DateTime(2026, 10, 1),
            TrxType = IvTrxTypes.MiscellaneousReceipt,
            BatchStatus = IvBatchStatuses.New,
            PostedCount = postedCount,
            RollbackCount = rollbackCount
        };
        _db.IvTrxBatches.Add(batch);
        await _db.SaveChangesAsync();
        return batch;
    }
}
