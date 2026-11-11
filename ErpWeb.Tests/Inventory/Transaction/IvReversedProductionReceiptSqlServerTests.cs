using ErpWeb.Core.Inventory;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

/// <summary>Exercises the public receipt safeguard against an isolated SQL Server database.</summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class IvReversedProductionReceiptSqlServerTests
{
    [Fact]
    public async Task Concurrent_receipt_post_and_rollback_refuse_unproven_reversed_issue_atomically()
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.False(Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS") == "1",
                "This test requires an isolated SQL Server test connection.");
            return;
        }
        Assert.Contains("test", new SqlConnectionStringBuilder(connectionString).InitialCatalog,
            StringComparison.OrdinalIgnoreCase);

        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(connectionString).Options);
        var company = "T" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        const string branch = "HQ";
        const string item = "REPLAY-ITEM";
        var receiptDate = new DateTime(2026, 10, 1);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            foreach (var (batchNo, status) in new[] { (1, IvBatchStatuses.New), (2, IvBatchStatuses.Posted) })
            {
                db.IvTrxBatches.Add(new IvTrxBatch
                {
                    CompanyCode = company, BranchCode = branch, BatchNo = batchNo,
                    TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = status, TrxDtTime = receiptDate,
                    Details = [new IvTrxBatchDetail
                    {
                        CompanyCode = company, BranchCode = branch, BatchNo = batchNo,
                        TrxType = IvTrxTypes.MiscellaneousReceipt, TrxLineNo = 1,
                        ICode = item, ToStdQty = 5m, ToStdUom = "EA"
                    }]
                });
            }
            var issue = new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, BatchNo = 3, TrxLineNo = 1,
                TrxType = IvTrxTypes.IssueToProduction, BatchStatus = IvBatchStatuses.Posted,
                ICode = item, TrxDtTime = receiptDate.AddDays(2)
            };
            db.IvTrxHistories.AddRange(issue, new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, BatchNo = 2, TrxLineNo = 1,
                TrxType = IvTrxTypes.MiscellaneousReceipt, BatchStatus = IvBatchStatuses.Posted,
                ICode = item, TrxDtTime = receiptDate
            });
            await db.SaveChangesAsync();
            db.IvTrxHistories.Add(new IvTrxHistory
            {
                CompanyCode = company, BranchCode = branch, BatchNo = 4, TrxLineNo = 1,
                TrxType = IvTrxTypes.IssueToProduction, BatchStatus = IvBatchStatuses.Posted,
                ICode = item, TrxDtTime = receiptDate.AddDays(3), ReversesHistoryId = issue.Id
            });
            await db.SaveChangesAsync();
        }

        var posting = InventoryLedgerTestFixture.CreateInventoryPosting(factory,
            company: company, branch: branch, enableBackdatedReplay: true);
        var results = await Task.WhenAll(
            posting.PostAsync(IvTrxTypes.MiscellaneousReceipt, [1]),
            posting.RollbackAsync(IvTrxTypes.MiscellaneousReceipt, [2]));
        Assert.All(results, result =>
        {
            Assert.False(result.Succeeded);
            Assert.Contains("reversed Production issue", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        });
        await using var unchanged = await factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == 1)).BatchStatus);
        Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b =>
            b.CompanyCode == company && b.BranchCode == branch && b.BatchNo == 2)).BatchStatus);
        Assert.Equal(3, await unchanged.IvTrxHistories.CountAsync(h => h.CompanyCode == company));
        Assert.False(await unchanged.StockPostings.AnyAsync(p => p.CompanyCode == company));
    }
}
