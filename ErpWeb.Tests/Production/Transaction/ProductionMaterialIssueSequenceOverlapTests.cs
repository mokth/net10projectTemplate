using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using ErpWeb.Tests.Inventory.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionMaterialIssueSequenceOverlapTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionMaterialIssueSequenceOverlapTests()
    {
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        ProductionLedgerTestFixture.SeedActiveEpochAsync(db).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Next_process_material_issue_is_blocked_until_predecessor_posts_good()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-SEQ");
        await SeedBalanceAsync(50, 20m);
        var issues = CreateIssueService();

        var search = await issues.SearchEligibleOperationsAsync(new ProductionMaterialIssueOperationQuery
        {
            WorkOrderNo = "WO-IP-SEQ",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(search.Succeeded, search.Message);
        var p2Row = Assert.Single(search.Data!.Rows, x => x.WorkOrderOperationId == graph.Process2Id);
        Assert.False(p2Row.IsSequenceEligible);

        var preview = await issues.GetBomPreviewAsync(graph.Process2Id, 1m, new DateTime(2026, 10, 1));
        Assert.False(preview.Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 50, 1m));
        Assert.False(draft.Succeeded);
        Assert.Contains("must post Good qty first", draft.Message, StringComparison.OrdinalIgnoreCase);

        var outputs = CreateOutputService();
        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var opened = await issues.SearchEligibleOperationsAsync(new ProductionMaterialIssueOperationQuery
        {
            WorkOrderNo = "WO-IP-SEQ",
            ExactMatch = true,
            Take = 20,
        });
        Assert.True(Assert.Single(opened.Data!.Rows, x => x.WorkOrderOperationId == graph.Process2Id).IsSequenceEligible);

        var allowedPreview = await issues.GetBomPreviewAsync(graph.Process2Id, 1m, new DateTime(2026, 10, 1));
        Assert.True(allowedPreview.Succeeded, allowedPreview.Message);

        var saved = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 50, 1m));
        Assert.True(saved.Succeeded, saved.Message);
        var posted = await issues.PostAsync([saved.Data!.BatchNo]);
        Assert.True(posted.Succeeded && posted.Data!.SucceededCount == 1,
            posted.Data?.Batches.FirstOrDefault()?.Message ?? posted.Message);
    }

    [Fact]
    public async Task Future_issue_can_be_saved_but_cannot_post_before_business_date()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-FUTURE");
        await SeedBalanceAsync(57, 20m);
        var output = CreateOutputService();
        var predecessor = await output.CreateAsync(OutputRequest(
            graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(predecessor.Succeeded, predecessor.Message);
        Assert.True((await output.PostAsync(predecessor.Data!.Uid)).Succeeded);

        var issues = CreateIssueService();
        var request = await CreateIssueSaveAsync(graph, 57, 1m);
        request.TrxDateTime = new DateTime(2026, 10, 2, 8, 0, 0);
        var saved = await issues.CreateAsync(request);
        Assert.True(saved.Succeeded, saved.Message);

        var post = await issues.PostAsync([saved.Data!.BatchNo]);
        Assert.True(post.Succeeded, post.Message);
        Assert.Equal(0, post.Data!.SucceededCount);
        Assert.Contains("after the business date", post.Data.Batches[0].Message,
            StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New,
            (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.Data.BatchNo)).BatchStatus);
        Assert.Empty(await db.ProductionMaterialMovements
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue).ToListAsync());
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 3)]
    [InlineData(true, false, 3)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 4)]
    [InlineData(false, true, 5)]
    [InlineData(true, true, 5)]
    [InlineData(false, true, 6)]
    [InlineData(true, true, 6)]
    public Task Backdated_receipt_reprices_unconsumed_production_issue_and_rolls_back(
        bool goodsReceipt, bool receiptFirst, int consumeAt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst, consumeAt, false);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_wac_receipt_reprices_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 20 : 21, false);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_wac_receipt_reprices_partially_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 22 : 23, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_consumed_output_rejects_damaged_replay_owner(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 24, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_consumed_output_failure_rolls_back_everything(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 25, false);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public Task Backdated_wac_receipt_reprices_active_finished_good_and_restores_it_on_removal(
        bool goodsReceipt, bool finishedGoodFirst, bool receiptFirst) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, receiptFirst, finishedGoodFirst ? receiptFirst ? 43 : 45 : 44, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_can_repeat_after_finished_good_correction_is_cleared(
        bool goodsReceipt, bool lowerCost) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, true, lowerCost ? 56 : 55, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_can_repeat_when_finished_goods_were_posted_at_corrected_cost(
        bool goodsReceipt, bool lowerCost) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, true, lowerCost ? 64 : 63, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reposting_receipt_rejects_damaged_finished_good_correction_when_finished_goods_were_posted_later(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 65, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Receipt_rollback_rejects_damaged_prior_correction_after_finished_goods_were_posted_later(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 66, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Repeated_backdated_wac_receipt_can_roll_back_finished_goods_first(
        bool goodsReceipt, bool lowerCost) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, true, lowerCost ? 59 : 58, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_rejects_damaged_cleared_finished_good_correction(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 57, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Cleared_finished_good_correction_rejects_damaged_output_ownership(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 62, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Repeated_backdated_wac_receipt_rejects_damaged_earlier_correction(
        bool goodsReceipt, bool finishedGoodFirst) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, true, finishedGoodFirst ? 61 : 60, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public Task Backdated_wac_receipt_reprices_fully_received_finished_good(
        bool goodsReceipt, bool receiptFirst) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, receiptFirst, receiptFirst ? 46 : 47, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_finished_good_correction_failure_is_atomic(
        bool goodsReceipt, bool failDuringPosting) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, true, failDuringPosting ? 49 : 48, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_removal_rejects_damaged_finished_good_replay_reversal(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, false, 50, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Corrected_finished_good_output_waits_for_backdated_receipt_removal(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, false, 52, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public Task Backdated_wac_receipt_can_lower_finished_good_cost_and_roll_back(
        bool goodsReceipt, bool receiptFirst) => BackdatedReceiptAndProductionIssueAsync(
            goodsReceipt, receiptFirst, receiptFirst ? 53 : 54, false);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Backdated_wac_receipt_can_replay_after_finished_good_reversal(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 28 : 29, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task Backdated_wac_receipt_after_reversing_fully_received_partial_output(
        bool goodsReceipt, bool finishedGoodFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, !finishedGoodFirst,
            finishedGoodFirst ? 34 : 35, false);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public Task Backdated_wac_receipt_preserves_active_finished_good_value(
        bool goodsReceipt, bool finishedGoodFirst, bool receiptFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            finishedGoodFirst ? 36 : 37, false);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public Task Backdated_wac_receipt_preserves_fully_received_partial_output(
        bool goodsReceipt, bool finishedGoodFirst, bool receiptFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            finishedGoodFirst ? 39 : 40, false);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public Task Backdated_wac_receipt_rejects_damaged_active_finished_good(bool goodsReceipt, int damageKind) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, damageKind == 0 ? 38 : damageKind == 1 ? 41 : 42, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_rejects_damaged_finished_good_reversal(
        bool goodsReceipt, bool damageValuation) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, damageValuation ? 31 : 30, false);

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 7)]
    [InlineData(true, true, 7)]
    [InlineData(false, true, 8)]
    [InlineData(true, true, 8)]
    public Task Backdated_standard_receipt_preserves_production_issue_cost_and_rolls_back(
        bool goodsReceipt, bool receiptFirst, int consumeAt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst, consumeAt, true);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_standard_receipt_around_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 9 : 10, true);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_standard_receipt_around_partially_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 11 : 12, true);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    [InlineData(true, 4)]
    public Task Backdated_standard_consumed_output_rejects_damaged_evidence(
        bool goodsReceipt, int damageKind) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, false,
            damageKind == 0 ? 13 : damageKind == 1 ? 14 : damageKind == 2 ? 15 : damageKind == 3 ? 16 : 17, true);

    private async Task BackdatedReceiptAndProductionIssueAsync(
        bool goodsReceipt, bool receiptFirst, int consumeAt, bool standard)
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-REPLAY");
        await SeedBalanceAsync(58, 20m);
        var businessDate = new DateTime(2026, 10, standard || consumeAt >= 20 ? 3 : 1);
        var receiptPrice = consumeAt is 53 or 54 or 56 or 59 or 64 ? 1m
            : consumeAt is 5 or 36 or 37 or 38 or 39 or 40 or 41 or 42 ? 2m : 10m;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            db.MsUoms.Add(new() { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
            db.IvClasses.Add(new() { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
            (await db.IvStockMasters.SingleAsync(s => s.ICode == "RM001")).IClassCode = "RAW";
            if (consumeAt is 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 63 or 64 or 65 or 66)
                await ConfigureFinishedGoodRouteAsync(db, graph.Process2Id);
            if (standard)
            {
                (await db.StockCostPolicyRevisions.SingleAsync()).CostMethod = StockCostMethods.Standard;
                (await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).CostMethod = StockCostMethods.Standard;
            }
            if (goodsReceipt)
                db.PoOrders.Add(new PoOrder
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", PoNo = "PO-IP-REPLAY",
                    PoRelNo = 1, PoDate = new DateTime(2026, 9, 30), Status = "NEW",
                    VendCode = "SUP01", CurCode = "MYR", RowVersion = [1],
                    Details = [new PoOrderDetail
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ", PoNo = "PO-IP-REPLAY",
                        PoRelNo = 1, Line = 1, ICode = "RM001", PoQty = 10m,
                        PoPurQty = 10m, PoUnitPrice = receiptPrice, NetAmount = 10m * receiptPrice,
                        PurchaseUom = "EA", StdUom = "EA", PackSz = 1m,
                        CurCode = "MYR", ToWarehouse = "WH01"
                    }]
                });
            await db.SaveChangesAsync();
            if (standard)
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "RM001", 2m);
        }

        var outputs = CreateOutputService(businessDate);
        var predecessor = await outputs.CreateAsync(OutputRequest(
            graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(predecessor.Succeeded, predecessor.Message);
        Assert.True((await outputs.PostAsync(predecessor.Data!.Uid)).Succeeded);

        var issues = CreateIssueService(
            InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true),
            businessDate);
        var request = await CreateIssueSaveAsync(graph, 58, 4m);
        request.ProductionQtyThisIssue = 4m;
        request.TrxDateTime = businessDate.AddHours(12);
        var issue = await issues.CreateAsync(request);
        Assert.True(issue.Succeeded, issue.Message);
        var issued = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issued.Succeeded && issued.Data!.SucceededCount == 1,
            issued.Data?.Batches.FirstOrDefault()?.Message ?? issued.Message);

        async Task DamageProductionOwnerAsync()
        {
            await using var corrupt = await _factory.CreateDbContextAsync();
            var changed = await corrupt.StockValuationFacts
                .Where(f => f.MovementCode == "PRODUCTION_MATERIAL_OUT"
                    && f.ReversesValuationFactId == null)
                .ExecuteUpdateAsync(update => update.SetProperty(
                    f => f.WorkOrderOperationId, (long?)null));
            Assert.Equal(1, changed);
        }
        if (standard && consumeAt == 7)
            await DamageProductionOwnerAsync();

        IvMiscIssueService? preexistingIssue = null;
        var preexistingIssueBatch = 0;
        if (consumeAt == 6)
        {
            preexistingIssue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, new DateTime(2026, 10, 2), enableBackdatedReplay: true);
            var saved = await preexistingIssue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = new DateTime(2026, 10, 2),
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 58, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 1m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 2m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            preexistingIssueBatch = saved.BatchNo;
            var laterPosted = await preexistingIssue.PostAsync([preexistingIssueBatch]);
            Assert.True(laterPosted.Succeeded, laterPosted.ErrorMessage);
        }

        long consumedOutputId = 0;
        var consumedQty = consumeAt is 11 or 12 or 22 or 23 or 34 or 35 or 39 or 40 or 46 or 47 ? 2m : 4m;
        async Task ConsumeIssueAsync()
        {
            var created = await outputs.CreateAsync(OutputRequest(
                graph.Process2Id, consumedQty, businessDate.AddHours(13)));
            Assert.True(created.Succeeded, created.Message);
            var result = await outputs.PostAsync(created.Data!.Uid);
            Assert.True(result.Succeeded, result.Message);
            consumedOutputId = created.Data.Uid;
        }
        if (consumeAt is 9 or 11 or 13 or 14 or 15 or 16 or 17 or 20 or 22 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 63 or 64 or 65 or 66)
            await ConsumeIssueAsync();
        if (consumeAt == 25)
        {
            await using var inject = await _factory.CreateDbContextAsync();
            var produceId = await inject.ProductionBalLotMovements.Where(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionBalLotMovementTypes.Produce)
                .Select(m => m.Uid).SingleAsync();
            await inject.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER TestRejectProductionReplay BEFORE INSERT ON ProductionCostRestatementFact "
                + "WHEN NEW.ProductionBalLotMovementId = " + produceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " BEGIN SELECT RAISE(ABORT, 'injected replay failure'); END;");
        }
        if (consumeAt == 13)
        {
            await using var corrupt = await _factory.CreateDbContextAsync();
            var material = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var consumption = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                m.ProductionOutputId == consumedOutputId
                && m.MovementType == ProductionMaterialMovementTypes.Consume);
            var changed = await corrupt.ProductionPoolDependencyRows.Where(e =>
                    e.ContributorMovementId == material.ProductionBalLotMovementId
                    && e.ConsumerMovementId == consumption.ProductionBalLotMovementId
                    && e.ReversesDependencyId == null)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.StockPostingId, material.StockPostingId!.Value));
            Assert.Equal(1, changed);
        }
        if (consumeAt == 15)
        {
            await using var corrupt = await _factory.CreateDbContextAsync();
            var consumption = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                m.ProductionOutputId == consumedOutputId
                && m.MovementType == ProductionMaterialMovementTypes.Consume);
            var changed = await corrupt.ProductionValuationEvidenceRows
                .Where(e => e.MovementId == consumption.ProductionBalLotMovementId)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.Price, 99m));
            Assert.Equal(1, changed);
        }
        if (consumeAt == 16)
        {
            await using var corrupt = await _factory.CreateDbContextAsync();
            var changed = await corrupt.ProductionMaterialMovements
                .Where(m => m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionMaterialMovementTypes.Consume)
                .ExecuteUpdateAsync(update => update.SetProperty(m => m.ItemCode, "DAMAGED"));
            Assert.Equal(1, changed);
        }
        if (consumeAt == 17)
        {
            await using var corrupt = await _factory.CreateDbContextAsync();
            var material = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var changed = await corrupt.ProductionBalLots
                .Where(l => l.OriginalIssueMovementId == material.Uid)
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.WorkOrderMaterialId, (long?)null));
            Assert.Equal(1, changed);
        }

        var receipts = goodsReceipt ? null : InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, businessDate, enableBackdatedReplay: true);
        IvGoodsReceiptService? goods = null;
        int receiptBatchNo;
        if (goodsReceipt)
        {
            var tenant = InventoryTenantTestHelper.CreateTenantContext();
            goods = new IvGoodsReceiptService(_factory, tenant, Access(), new RunningNumberService(),
                new FixedCurrentDateService(businessDate),
                new IvStockMasterRepository(_factory), new IvStockCommonRepository(_factory),
                new IvStockTransactionRepository(), new IvStockPostingRepository(),
                InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true),
                NullLogger<IvGoodsReceiptService>.Instance);
            var saved = await goods.SaveNewAsync(new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 1),
                Lines = [new IvGoodsReceiptLineRequest
                {
                    PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                    ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            receiptBatchNo = saved.BatchNo;
        }
        else
        {
            var saved = await receipts!.SaveNewAsync(new IvMiscReceiptSaveRequest
            {
                TrxDate = new DateTime(2026, 10, 1),
                Lines = [new IvMiscReceiptLineRequest
                {
                    ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                    Quantity = 5m, UnitPrice = receiptPrice, PriceConfirmed = true,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    Reason = "ADJ"
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            receiptBatchNo = saved.BatchNo;
        }
        async Task<(bool Succeeded, string? Error)> PostReceiptAsync()
        {
            if (goods is not null)
            {
                var result = await goods.PostAsync([receiptBatchNo]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await receipts!.PostAsync([receiptBatchNo]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }
        async Task<(bool Succeeded, string? Error)> RollbackReceiptAsync()
        {
            if (goods is not null)
            {
                var result = await goods.RollbackAsync([receiptBatchNo]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await receipts!.RollbackAsync([receiptBatchNo]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }
        PostedFinishedGood? unchangedFinishedGood = null;
        if (consumeAt is 36 or 38 or 39 or 41 or 42 or 43 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62)
        {
            unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.Equal(6m, unchangedFinishedGood.Value);
            if (consumeAt is 38 or 41 or 42)
                await DamageActiveFinishedGoodAsync(unchangedFinishedGood.Document.Id,
                    consumeAt == 38 ? 0 : consumeAt == 41 ? 1 : 2);
        }
        if (consumeAt == 26)
            Assert.Equal(6m, (await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate)).Value);
        if (consumeAt == 49)
        {
            await using var inject = await _factory.CreateDbContextAsync();
            var movementId = await inject.ProductionFinishedGoodFactRows
                .Where(f => f.BatchId == unchangedFinishedGood!.Document.Id && f.ReversesFactId == null)
                .Select(f => f.ProductionMovementId).SingleAsync();
            await inject.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER TestRejectFinishedGoodReplay BEFORE INSERT ON ProductionCostRestatementFact "
                + "WHEN NEW.ProductionBalLotMovementId = " + movementId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " BEGIN SELECT RAISE(ABORT, 'injected finished-good replay failure'); END;");
        }
        if (consumeAt is 28 or 30 or 31 or 34)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.Equal(6m, finished.Value);
            await RollbackFinishedGoodAsync(finished);
            if (consumeAt is 30 or 31)
                await DamageFinishedGoodReversalAsync(finished.Document.Id, consumeAt == 31);
        }
        var posted = await PostReceiptAsync();
        if (consumeAt == 49)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var raw = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var finished = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
            Assert.Equal(16m, raw.OnHandBaseQty);
            Assert.Equal(32m, raw.InventoryValue);
            Assert.Equal(2m, finished.OnHandBaseQty);
            Assert.Equal(6m, finished.InventoryValue);
            Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Empty(await unchanged.ProductionCostRestatementFacts.ToArrayAsync());
            Assert.Empty(await unchanged.StockReplayRuns.ToArrayAsync());
            if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt is 26 or 30 or 31 or 38 or 41 or 42)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(32m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Empty(await unchanged.ProductionCostRestatementFacts.ToArrayAsync());
            if (goodsReceipt)
                Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt == 25)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(32m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Empty(await unchanged.ProductionCostRestatementFacts.ToArrayAsync());
            Assert.Empty(await unchanged.StockReplayRuns.ToArrayAsync());
            Assert.Empty(await unchanged.StockReplayEffects.ToArrayAsync());
            var consumption = await unchanged.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionOutputId == consumedOutputId && m.WorkOrderMaterialId == graph.MaterialId
                && m.MovementType == ProductionBalLotMovementTypes.Consume);
            Assert.Equal(8m, consumption.TotalCost);
            if (goodsReceipt)
                Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt is 7 or 13 or 15 or 16 or 17)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Equal(16m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).OnHandBaseQty);
            Assert.Equal(32m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        Assert.True(posted.Succeeded, posted.Error);

        if (consumeAt == 48)
        {
            Assert.NotNull(unchangedFinishedGood);
            await using (var corrupt = await _factory.CreateDbContextAsync())
            {
                var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                    v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                var delta = await corrupt.StockValuationFacts.SingleAsync(v => v.OriginalValuationFactId == receiptFact.Id
                    && v.InventoryHistoryId == null && v.ReversesValuationFactId == null);
                var deleted = await corrupt.StockReplayEffects.Where(e =>
                    e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && e.AffectedEntityId == delta.Id)
                    .ExecuteDeleteAsync();
                Assert.Equal(1, deleted);
            }
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Equal(75.6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            Assert.Equal(9.2m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt == 50)
        {
            Assert.NotNull(unchangedFinishedGood);
            await RollbackFinishedGoodAsync(unchangedFinishedGood);
            int runCount;
            int restatementCount;
            await using (var corrupt = await _factory.CreateDbContextAsync())
            {
                var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                    v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                var delta = await corrupt.StockValuationFacts.SingleAsync(v => v.OriginalValuationFactId == receiptFact.Id
                    && v.InventoryHistoryId == null && v.ReversesValuationFactId == null);
                var reverseDelta = await corrupt.StockValuationFacts.SingleAsync(v => v.ReversesValuationFactId == delta.Id);
                var deleted = await corrupt.StockReplayEffects.Where(e =>
                    e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact && e.AffectedEntityId == reverseDelta.Id)
                    .ExecuteDeleteAsync();
                Assert.Equal(1, deleted);
                runCount = await corrupt.StockReplayRuns.CountAsync();
                restatementCount = await corrupt.ProductionCostRestatementFacts.CountAsync();
            }
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Equal(75.6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            Assert.Equal(0m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
            Assert.Equal(restatementCount, await unchanged.ProductionCostRestatementFacts.CountAsync());
            if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt is 43 or 44 or 45 or 46 or 47 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 63 or 64 or 65 or 66)
        {
            if (consumeAt is 44 or 63 or 64 or 65 or 66)
                unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.NotNull(unchangedFinishedGood);
            var correctedValue = consumeAt is 53 or 54 or 56 or 59 or 64 ? 5.6m : 9.2m;
            var corrected = await unchangedFinishedGood.Service.GetAsync(unchangedFinishedGood.Document.Id);
            Assert.True(corrected.Succeeded, corrected.Message);
            Assert.Equal(correctedValue, Assert.Single(corrected.Data!.Lines).TotalValue);
            await using (var afterPost = await _factory.CreateDbContextAsync())
            {
                var finishedState = await afterPost.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
                Assert.Equal(2m, finishedState.OnHandBaseQty);
                Assert.Equal(correctedValue, finishedState.InventoryValue);
                var fact = await afterPost.ProductionFinishedGoodFactRows.SingleAsync(f =>
                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                var effective = (await ProductionEffectiveCostResolver.ResolveAsync(afterPost,
                    "DEMO", "HQ", [fact.ProductionMovementId]))[fact.ProductionMovementId];
                Assert.Equal(consumeAt is 44 or 63 or 64 or 65 or 66 ? correctedValue : 6m, fact.TotalValue);
                Assert.Equal(correctedValue, effective.TotalCost);
                if (consumeAt is not (44 or 63 or 64 or 65 or 66))
                {
                    var correction = await afterPost.ProductionCostRestatementFacts.SingleAsync(r =>
                        r.ProductionBalLotMovementId == fact.ProductionMovementId && r.ReversesRestatementFactId == null);
                    var replayRun = await afterPost.StockReplayRuns.SingleAsync(r =>
                        r.RootStockPostingId == correction.ReplayStockPostingId);
                    Assert.Equal(2, replayRun.AffectedItemCount);
                    Assert.True(replayRun.AffectedHistoryCount >= 2);
                }
            }
            if (!receiptFirst)
                await RollbackFinishedGoodAsync(unchangedFinishedGood);
            if (consumeAt == 52)
            {
                var prematureOutput = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "Attempt output rollback before source receipt"
                });
                Assert.False(prematureOutput.Succeeded);
                Assert.Contains("source receipt", prematureOutput.Message, StringComparison.OrdinalIgnoreCase);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
            {
                var restoredDetail = await unchangedFinishedGood.Service.GetAsync(unchangedFinishedGood.Document.Id);
                Assert.True(restoredDetail.Succeeded, restoredDetail.Message);
                Assert.Equal(6m, Assert.Single(restoredDetail.Data!.Lines).TotalValue);
                await using (var afterRemoval = await _factory.CreateDbContextAsync())
                    Assert.Equal(6m, (await afterRemoval.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                var reversedDuringRepeat = false;
                if (consumeAt == 65)
                {
                    int runCount;
                    await using (var corrupt = await _factory.CreateDbContextAsync())
                    {
                        var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                            f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                        var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                        var delta = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.OriginalValuationFactId == receiptFact.Id && v.ReversesValuationFactId == null);
                        Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                            && e.AffectedEntityId == delta.Id).ExecuteDeleteAsync());
                        runCount = await corrupt.StockReplayRuns.CountAsync();
                    }
                    var rejected = await PostReceiptAsync();
                    Assert.False(rejected.Succeeded);
                    await using var unchanged = await _factory.CreateDbContextAsync();
                    Assert.Equal(32m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                    Assert.Equal(6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                    Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
                    Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                if (consumeAt == 66)
                {
                    var reposted = await PostReceiptAsync();
                    Assert.True(reposted.Succeeded, reposted.Error);
                    int runCount;
                    await using (var corrupt = await _factory.CreateDbContextAsync())
                    {
                        var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                            f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                        var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                        var inverse = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.OriginalValuationFactId == receiptFact.Id && v.ReversesValuationFactId != null);
                        Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                            && e.AffectedEntityId == inverse.Id).ExecuteDeleteAsync());
                        runCount = await corrupt.StockReplayRuns.CountAsync();
                    }
                    var rejected = await RollbackReceiptAsync();
                    Assert.False(rejected.Succeeded);
                    await using var unchanged = await _factory.CreateDbContextAsync();
                    Assert.Equal(75.6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                    Assert.Equal(9.2m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                    Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
                    Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                if (consumeAt is 63 or 64)
                {
                    int otherBatch;
                    if (goods is not null)
                    {
                        var other = await goods.SaveNewAsync(new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 1),
                            Lines = [new IvGoodsReceiptLineRequest
                            {
                                PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                                ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m
                            }]
                        });
                        Assert.True(other.Succeeded, other.ErrorMessage);
                        otherBatch = other.BatchNo;
                        var rejected = await goods.PostAsync([otherBatch]);
                        Assert.False(rejected.Succeeded);
                        Assert.Contains("wider replay", rejected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        var other = await receipts!.SaveNewAsync(new IvMiscReceiptSaveRequest
                        {
                            TrxDate = new DateTime(2026, 10, 1),
                            Lines = [new IvMiscReceiptLineRequest
                            {
                                ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                                Quantity = 5m, UnitPrice = receiptPrice, PriceConfirmed = true,
                                Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                            }]
                        });
                        Assert.True(other.Succeeded, other.ErrorMessage);
                        otherBatch = other.BatchNo;
                        var rejected = await receipts.PostAsync([otherBatch]);
                        Assert.False(rejected.Succeeded);
                        Assert.Contains("wider replay", rejected.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                    }
                    await using (var unaffected = await _factory.CreateDbContextAsync())
                    {
                        Assert.Equal(IvBatchStatuses.New, (await unaffected.IvTrxBatches.SingleAsync(b => b.BatchNo == otherBatch)).BatchStatus);
                        Assert.Equal(6m, (await unaffected.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                        Assert.Equal(32m, (await unaffected.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                        if (goodsReceipt) Assert.Equal(0m, (await unaffected.PoOrderDetails.SingleAsync()).RecvQty);
                    }
                    for (var cycle = 0; cycle < 2; cycle++)
                    {
                        var reposted = await PostReceiptAsync();
                        Assert.True(reposted.Succeeded, reposted.Error);
                        var detail = await unchangedFinishedGood.Service.GetAsync(unchangedFinishedGood.Document.Id);
                        Assert.True(detail.Succeeded, detail.Message);
                        Assert.Equal(correctedValue, Assert.Single(detail.Data!.Lines).TotalValue);
                        await using (var active = await _factory.CreateDbContextAsync())
                        {
                            Assert.Equal(correctedValue, (await active.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                            var fact = await active.ProductionFinishedGoodFactRows.SingleAsync(f =>
                                f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                            Assert.Equal(correctedValue, fact.TotalValue);
                            Assert.Equal(2 * cycle + 2, await active.ProductionCostRestatementFacts.CountAsync(r =>
                                r.ProductionBalLotMovementId == fact.ProductionMovementId));
                        }
                        var early = await unchangedFinishedGood.Service.RollbackAsync(new(
                            unchangedFinishedGood.Document.Id, unchangedFinishedGood.Document.RowVersion,
                            Guid.NewGuid(), "Attempt FG rollback before the reposted source receipt"));
                        Assert.False(early.Succeeded);
                        Assert.Contains("source receipt", early.Message, StringComparison.OrdinalIgnoreCase);
                        var removedAgain = await RollbackReceiptAsync();
                        Assert.True(removedAgain.Succeeded, removedAgain.Error);
                        await using var restored = await _factory.CreateDbContextAsync();
                        Assert.Equal(6m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                        Assert.Equal(32m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                        if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
                    }
                }
                if (consumeAt == 62)
                {
                    int postingCount;
                    await using (var corrupt = await _factory.CreateDbContextAsync())
                    {
                        var produceId = await corrupt.ProductionBalLotMovements.Where(m =>
                            m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.Produce)
                            .Select(m => m.Uid).SingleAsync();
                        var correction = await corrupt.ProductionCostRestatementFacts.Where(r =>
                            r.ProductionBalLotMovementId == produceId && r.ReversesRestatementFactId == null)
                            .Select(r => r.Id).SingleAsync();
                        Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                            e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"
                            && e.AffectedEntityId == correction).ExecuteDeleteAsync());
                        postingCount = await corrupt.StockPostings.CountAsync();
                    }
                    var fgRollback = await unchangedFinishedGood.Service.RollbackAsync(new(
                        unchangedFinishedGood.Document.Id, unchangedFinishedGood.Document.RowVersion,
                        Guid.NewGuid(), "Attempt rollback with damaged output ownership"));
                    Assert.False(fgRollback.Succeeded);
                    var repost = await PostReceiptAsync();
                    Assert.False(repost.Succeeded);
                    await using var unchanged = await _factory.CreateDbContextAsync();
                    Assert.Equal(postingCount, await unchanged.StockPostings.CountAsync());
                    Assert.Equal(6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                    Assert.Equal(32m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                    Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                if (consumeAt == 57)
                {
                    int runCount;
                    await using (var corrupt = await _factory.CreateDbContextAsync())
                    {
                        var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                            f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                        var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                        var inverse = await corrupt.StockValuationFacts.SingleAsync(v =>
                            v.OriginalValuationFactId == receiptFact.Id && v.ReversesValuationFactId != null);
                        Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                            e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                            && e.AffectedEntityId == inverse.Id).ExecuteDeleteAsync());
                        runCount = await corrupt.StockReplayRuns.CountAsync();
                    }
                    var damagedRepost = await PostReceiptAsync();
                    Assert.False(damagedRepost.Succeeded);
                    await using var unchanged = await _factory.CreateDbContextAsync();
                    Assert.Equal(32m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                    Assert.Equal(6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                    Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
                    Assert.Equal(IvBatchStatuses.New, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                if (consumeAt is 55 or 56 or 58 or 59 or 60 or 61)
                {
                    var cycles = consumeAt is 55 or 56 ? 2 : 1;
                    for (var cycle = 0; cycle < cycles; cycle++)
                    {
                        var reposted = await PostReceiptAsync();
                        Assert.True(reposted.Succeeded, reposted.Error);
                        var detail = await unchangedFinishedGood.Service.GetAsync(unchangedFinishedGood.Document.Id);
                        Assert.True(detail.Succeeded, detail.Message);
                        Assert.Equal(correctedValue, Assert.Single(detail.Data!.Lines).TotalValue);
                        await using (var active = await _factory.CreateDbContextAsync())
                        {
                            Assert.Equal(correctedValue, (await active.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                            var fact = await active.ProductionFinishedGoodFactRows.SingleAsync(f =>
                                f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                            Assert.Equal(6m, fact.TotalValue);
                            Assert.Equal(2 * cycle + 3, await active.ProductionCostRestatementFacts.CountAsync(r =>
                                r.ProductionBalLotMovementId == fact.ProductionMovementId));
                        }
                        if (consumeAt is 58 or 59 or 61)
                        {
                            await RollbackFinishedGoodAsync(unchangedFinishedGood);
                            reversedDuringRepeat = true;
                        }
                        if (consumeAt is 60 or 61)
                        {
                            int runCount;
                            int correctionCount;
                            await using (var corrupt = await _factory.CreateDbContextAsync())
                            {
                                var fact = await corrupt.ProductionFinishedGoodFactRows.SingleAsync(f =>
                                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                                var receiptFact = await corrupt.StockValuationFacts.SingleAsync(v =>
                                    v.InventoryHistoryId == fact.InventoryHistoryId && v.StockPostingId == fact.StockPostingId);
                                var inverse = await corrupt.StockValuationFacts.Where(v =>
                                    v.OriginalValuationFactId == receiptFact.Id && v.ReversesValuationFactId != null)
                                    .OrderBy(v => v.Id).FirstAsync();
                                Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                                    e.AffectedEntityType == StockReplayEffectTypes.StockValuationFact
                                    && e.AffectedEntityId == inverse.Id).ExecuteDeleteAsync());
                                runCount = await corrupt.StockReplayRuns.CountAsync();
                                correctionCount = await corrupt.ProductionCostRestatementFacts.CountAsync();
                            }
                            var rejected = await RollbackReceiptAsync();
                            Assert.False(rejected.Succeeded);
                            await using var unchanged = await _factory.CreateDbContextAsync();
                            Assert.Equal(75.6m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                            Assert.Equal(reversedDuringRepeat ? 0m : 9.2m,
                                (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                            Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
                            Assert.Equal(correctionCount, await unchanged.ProductionCostRestatementFacts.CountAsync());
                            Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                            if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                            return;
                        }
                        var removedAgain = await RollbackReceiptAsync();
                        Assert.True(removedAgain.Succeeded, removedAgain.Error);
                        await using var restored = await _factory.CreateDbContextAsync();
                        Assert.Equal(reversedDuringRepeat ? 0m : 6m,
                            (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                        Assert.Equal(32m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                        if (reversedDuringRepeat)
                        {
                            var originalFg = await restored.ProductionFinishedGoodFactRows.SingleAsync(f =>
                                f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                            var reverseFg = await restored.ProductionFinishedGoodFactRows.SingleAsync(f => f.ReversesFactId == originalFg.Id);
                            var effective = await ProductionReplayEffectiveCostReader.ReadAsync(restored, "DEMO", "HQ",
                                [originalFg.ProductionMovementId, reverseFg.ProductionMovementId]);
                            Assert.Equal(6m, effective[originalFg.ProductionMovementId].TotalCost);
                            Assert.Equal(6m, effective[reverseFg.ProductionMovementId].TotalCost);
                        }
                    }
                }
                if (!reversedDuringRepeat) await RollbackFinishedGoodAsync(unchangedFinishedGood);
            }
            else
            {
                await using var afterRemoval = await _factory.CreateDbContextAsync();
                var originalFg = await afterRemoval.ProductionFinishedGoodFactRows.SingleAsync(f =>
                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                var reverseFg = await afterRemoval.ProductionFinishedGoodFactRows.SingleAsync(f => f.ReversesFactId == originalFg.Id);
                var effective = await ProductionReplayEffectiveCostReader.ReadAsync(afterRemoval, "DEMO", "HQ",
                    [originalFg.ProductionMovementId, reverseFg.ProductionMovementId]);
                Assert.Equal(6m, effective[originalFg.ProductionMovementId].TotalCost);
                Assert.Equal(6m, effective[reverseFg.ProductionMovementId].TotalCost);
                Assert.Equal(0m, (await afterRemoval.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
            }
            await using (var audit = await _factory.CreateDbContextAsync())
            {
                var originalFg = await audit.ProductionFinishedGoodFactRows.SingleAsync(f =>
                    f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                var lastCorrection = await audit.ProductionCostRestatementFacts.Where(r =>
                    r.ProductionBalLotMovementId == originalFg.ProductionMovementId)
                    .OrderByDescending(r => r.Id).FirstAsync();
                var removalRun = await audit.StockReplayRuns.SingleAsync(r => r.RootStockPostingId == lastCorrection.ReplayStockPostingId);
                Assert.Equal(2, removalRun.AffectedItemCount);
                Assert.True(removalRun.AffectedHistoryCount >= (receiptFirst && consumeAt is not (58 or 59) ? 2 : 3));
            }
            var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "Reverse corrected finished goods"
            });
            Assert.True(outputReversal.Succeeded, outputReversal.Message);
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Reverse corrected finished goods"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            await using var finishedFinal = await _factory.CreateDbContextAsync();
            Assert.Equal(20m, (await finishedFinal.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).OnHandBaseQty);
            Assert.Equal(40m, (await finishedFinal.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            if (goodsReceipt) Assert.Equal(0m, (await finishedFinal.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt is 36 or 37 or 39 or 40)
        {
            if (consumeAt is 37 or 40)
                unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.NotNull(unchangedFinishedGood);
            Assert.Equal(6m, unchangedFinishedGood.Value);
            await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            if (!receiptFirst)
                await RollbackFinishedGoodAsync(unchangedFinishedGood);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            var repeated = await PostReceiptAsync();
            Assert.True(repeated.Succeeded, repeated.Error);
            if (receiptFirst)
                await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await RollbackFinishedGoodAsync(unchangedFinishedGood);
            var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "verify unchanged finished-good cost"
            });
            Assert.True(outputReversal.Succeeded, outputReversal.Message);
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "verify unchanged finished-good cost"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            Assert.Empty(await restored.ProductionCostRestatementFacts.ToArrayAsync());
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt == 27)
        {
            Assert.Equal(9.2m, (await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate)).Value);
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(75.6m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt is 28 or 29 or 34 or 35)
        {
            if (consumeAt is 29 or 35)
            {
                var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
                Assert.Equal(9.2m, finished.Value);
                await RollbackFinishedGoodAsync(finished);
            }
            async Task RollbackOutputAsync()
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "finished goods were reversed"
                });
                Assert.True(outputReversal.Succeeded, outputReversal.Message);
            }
            if (!receiptFirst)
                await RollbackOutputAsync();
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            var reposted = await PostReceiptAsync();
            Assert.True(reposted.Succeeded, reposted.Error);
            removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await RollbackOutputAsync();
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "finished goods were reversed"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (!standard && consumeAt is 20 or 21 or 22 or 23 or 24)
        {
            if (consumeAt is 21 or 23)
                await ConsumeIssueAsync();
            async Task CheckConsumedAsync(decimal expectedIssueCost, decimal expectedStockValue)
            {
                await using var current = await _factory.CreateDbContextAsync();
                var issueMaterial = await current.ProductionMaterialMovements.SingleAsync(m =>
                    m.InventoryBatchNo == issue.Data.BatchNo
                    && m.MovementType == ProductionMaterialMovementTypes.Issue);
                var consumed = await current.ProductionBalLotMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.WorkOrderMaterialId == graph.MaterialId
                    && m.MovementType == ProductionBalLotMovementTypes.Consume);
                var produce = await current.ProductionBalLotMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionBalLotMovementTypes.Produce);
                var inputs = await current.ProductionBalLotMovements.Where(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionBalLotMovementTypes.Consume).ToArrayAsync();
                var ids = inputs.Select(m => m.Uid).Append(produce.Uid)
                    .Append(issueMaterial.ProductionBalLotMovementId!.Value).ToArray();
                var effective = await ProductionEffectiveCostResolver.ResolveAsync(current, "DEMO", "HQ", ids);
                Assert.Equal(expectedIssueCost, effective[issueMaterial.ProductionBalLotMovementId.Value].TotalCost);
                Assert.Equal(expectedIssueCost * consumedQty / 4m, effective[consumed.Uid].TotalCost);
                var conversionFacts = await current.ProductionConversionCostFacts
                    .Where(f => f.ProductionMovementId == produce.Uid && f.ReversesFactId == null)
                    .Select(f => f.CostAmount).ToArrayAsync();
                var conversion = conversionFacts.Sum();
                Assert.Equal(inputs.Sum(m => effective[m.Uid].TotalCost) + conversion,
                    effective[produce.Uid].TotalCost);
                Assert.Equal(effective[produce.Uid].TotalCost,
                    (await current.ProductionPoolValuationRows.SingleAsync(p =>
                        p.ProductionBalLotId == produce.ProductionBalLotId)).TrackedValue);
                var stock = await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(expectedStockValue, stock.InventoryValue);
            }
            async Task RollbackOutputAsync()
            {
                var reversed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "verify repriced Production output"
                });
                Assert.True(reversed.Succeeded, reversed.Message);
            }
            await CheckConsumedAsync(14.4m, 75.6m);
            if (consumeAt == 24)
            {
                await using (var corrupt = await _factory.CreateDbContextAsync())
                {
                    var source = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                        m.InventoryBatchNo == issue.Data.BatchNo
                        && m.MovementType == ProductionMaterialMovementTypes.Issue);
                    var consumed = await corrupt.ProductionBalLotMovements.SingleAsync(m =>
                        m.ProductionOutputId == consumedOutputId
                        && m.WorkOrderMaterialId == graph.MaterialId
                        && m.MovementType == ProductionBalLotMovementTypes.Consume);
                    var correction = await corrupt.ProductionCostRestatementFacts.SingleAsync(f =>
                        f.ProductionBalLotMovementId == consumed.Uid && f.ReversesRestatementFactId == null);
                    var changed = await corrupt.StockReplayEffects
                        .Where(e => e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"
                            && e.AffectedEntityId == correction.Id)
                        .ExecuteUpdateAsync(update => update.SetProperty(e => e.BusinessOwnerPostingId, source.StockPostingId!.Value));
                    Assert.Equal(1, changed);
                }
                Assert.False((await RollbackReceiptAsync()).Succeeded);
                var blockedOutput = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "damaged replay ownership"
                });
                Assert.False(blockedOutput.Succeeded);
                await using var unchanged = await _factory.CreateDbContextAsync();
                var unchangedState = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, unchangedState.OnHandBaseQty);
                Assert.Equal(75.6m, unchangedState.InventoryValue);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                if (goodsReceipt)
                    Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                return;
            }
            if (consumeAt == 20 && receiptFirst && !goodsReceipt)
            {
                await using var constraintDb = await _factory.CreateDbContextAsync();
                var correctionId = await constraintDb.ProductionCostRestatementFacts
                    .Select(f => f.Id).FirstAsync();
                await Assert.ThrowsAsync<SqliteException>(() => constraintDb.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE ProductionCostRestatementFact SET DeltaCost = DeltaCost + 0.000001 WHERE Id = {correctionId}"));
            }
            if (receiptFirst)
            {
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
                await CheckConsumedAsync(8m, 32m);
                var repeated = await PostReceiptAsync();
                Assert.True(repeated.Succeeded, repeated.Error);
                await CheckConsumedAsync(14.4m, 75.6m);
                var removedAgain = await RollbackReceiptAsync();
                Assert.True(removedAgain.Succeeded, removedAgain.Error);
                await CheckConsumedAsync(8m, 32m);
                await RollbackOutputAsync();
            }
            else
            {
                await RollbackOutputAsync();
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
            }
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "verify repriced Production issue"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (standard && consumeAt is 9 or 10 or 11 or 12 or 14)
        {
            if (consumeAt is 10 or 12)
                await ConsumeIssueAsync();
            await using (var active = await _factory.CreateDbContextAsync())
            {
                var stock = await active.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, stock.OnHandBaseQty);
                Assert.Equal(42m, stock.InventoryValue);
                var material = await active.ProductionMaterialMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionMaterialMovementTypes.Consume);
                Assert.Equal(consumedQty * 2m, material.TotalCost);
                Assert.Equal(ProductionOutputStatuses.Posted,
                    (await active.ProductionOutputs.SingleAsync(o => o.Uid == consumedOutputId)).Status);
            }
            if (receiptFirst && consumeAt == 9)
            {
                var blockedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
                {
                    PostingRequestId = Guid.NewGuid().ToString("N"),
                    InventoryBatchNo = issue.Data.BatchNo, Reason = "output is still posted"
                });
                Assert.False(blockedIssue.Succeeded);
            }
            async Task RollbackOutputAsync()
            {
                var reversed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId,
                    PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "verify Standard receipt and Production rollback"
                });
                Assert.True(reversed.Succeeded, reversed.Message);
            }
            if (receiptFirst)
            {
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
                await RollbackOutputAsync();
            }
            else
            {
                await RollbackOutputAsync();
                if (consumeAt == 14)
                {
                    await using (var corrupt = await _factory.CreateDbContextAsync())
                    {
                        var material = await corrupt.ProductionMaterialMovements.SingleAsync(m =>
                            m.InventoryBatchNo == issue.Data.BatchNo
                            && m.MovementType == ProductionMaterialMovementTypes.Issue);
                        var allocation = await corrupt.ProductionMovementAllocations.SingleAsync(a =>
                            a.ReceiptMovementId == material.ProductionBalLotMovementId
                            && a.ReversesAllocationId == null);
                        var changed = await corrupt.ProductionMovementAllocations
                            .Where(a => a.ReversesAllocationId == allocation.Id)
                            .ExecuteUpdateAsync(update => update.SetProperty(a => a.BaseQty, a => a.BaseQty + 1m));
                        Assert.Equal(1, changed);
                    }
                    Assert.False((await RollbackReceiptAsync()).Succeeded);
                    await using var unchanged = await _factory.CreateDbContextAsync();
                    var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(21m, state.OnHandBaseQty);
                    Assert.Equal(42m, state.InventoryValue);
                    Assert.Equal(IvBatchStatuses.Posted,
                        (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt)
                        Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
            }
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "verify Standard Production rollback"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            await using var finalConsumed = await _factory.CreateDbContextAsync();
            var finalState = await finalConsumed.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, finalState.OnHandBaseQty);
            Assert.Equal(40m, finalState.InventoryValue);
            if (goodsReceipt)
                Assert.Equal(0m, (await finalConsumed.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        async Task CheckAsync(decimal stockQty, decimal stockValue, decimal productionValue)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(stockQty, stock.OnHandBaseQty);
            Assert.Equal(stockValue, stock.InventoryValue);
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var lot = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == lot.Uid);
            Assert.Equal(productionValue, lot.TotalCost);
            Assert.Equal(productionValue, pool.TrackedValue);
            Assert.Equal(8m, material.TotalCost); // The original posted evidence stays immutable.
            if (productionValue > 0m)
            {
                var movementId = material.ProductionBalLotMovementId!.Value;
                var effective = await ProductionEffectiveCostResolver.ResolveAsync(
                    db, "DEMO", "HQ", [movementId]);
                Assert.Equal(productionValue, effective[movementId].TotalCost);
                var history = await db.IvTrxHistories.SingleAsync(h => h.Id == material.InventoryHistoryId);
                var active = await StockReplayValuationGenerationResolver.ResolveAsync(
                    db, "DEMO", "HQ", [history]);
                Assert.Equal(productionValue, active[history.Id].Sum(f => f.CostAmount));
            }
        }
        var postedStockQty = consumeAt == 6 ? 20m : 21m;
        var postedStockValue = standard || consumeAt == 5 ? 42m : consumeAt == 6 ? 72m : 75.6m;
        var postedProductionValue = standard || consumeAt == 5 ? 8m : 14.4m;
        await CheckAsync(postedStockQty, postedStockValue, postedProductionValue);
        if (standard && consumeAt == 8)
        {
            await DamageProductionOwnerAsync();
            Assert.False((await RollbackReceiptAsync()).Succeeded);
            var rejectedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "damaged Production owner"
            });
            Assert.False(rejectedIssue.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, stock.OnHandBaseQty);
            Assert.Equal(42m, stock.InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == issue.Data.BatchNo)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        await using (var evidenceDb = await _factory.CreateDbContextAsync())
        {
            var material = await evidenceDb.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var run = await evidenceDb.StockReplayRuns.SingleAsync();
            if (standard || consumeAt == 5)
            {
                Assert.Empty(await evidenceDb.ProductionCostRestatementFacts.ToArrayAsync());
                Assert.Empty(await evidenceDb.StockReplayEffects.ToArrayAsync());
            }
            else
            {
                var correction = await evidenceDb.ProductionCostRestatementFacts.SingleAsync();
                var effect = await evidenceDb.StockReplayEffects.SingleAsync();
                Assert.Equal(8m, correction.PriorEffectiveTotalCost);
                Assert.Equal(14.4m, correction.CorrectedEffectiveTotalCost);
                Assert.Equal(material.ProductionBalLotMovementId, correction.ProductionBalLotMovementId);
                Assert.Equal(run.RootStockPostingId, correction.ReplayStockPostingId);
                Assert.Equal(run.Id, effect.ReplayRunId);
                Assert.Equal(material.StockPostingId, effect.BusinessOwnerPostingId);
                Assert.Equal(correction.Id, effect.AffectedEntityId);
            }
            Assert.NotNull((await evidenceDb.StockPostings.SingleAsync(p => p.Id == run.RootStockPostingId)).SealedAtUtc);
        }
        if (consumeAt == 4)
        {
            await using (var corrupt = await _factory.CreateDbContextAsync())
            {
                var changed = await corrupt.ProductionCostRestatementFacts
                    .Where(f => f.ReversesRestatementFactId == null)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(f => f.CorrectedEffectiveTotalCost, 99m)
                        .SetProperty(f => f.CorrectedUnitCost, 24.75m)
                        .SetProperty(f => f.DeltaCost, 91m));
                Assert.Equal(1, changed);
            }
            var rejectedReceipt = await RollbackReceiptAsync();
            Assert.False(rejectedReceipt.Succeeded);
            var rejectedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "damaged cost evidence"
            });
            Assert.False(rejectedIssue.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, stock.OnHandBaseQty);
            Assert.Equal(75.6m, stock.InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == issue.Data.BatchNo)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt == 3)
        {
            var laterIssues = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, new DateTime(2026, 10, 2), enableBackdatedReplay: true);
            var later = await laterIssues.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = new DateTime(2026, 10, 2),
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 58, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 1m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 2m
                }]
            });
            Assert.True(later.Succeeded, later.ErrorMessage);
            var laterPosted = await laterIssues.PostAsync([later.BatchNo]);
            Assert.True(laterPosted.Succeeded, laterPosted.ErrorMessage);
            var rejected = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "later Inventory issue"
            });
            Assert.False(rejected.Succeeded);
            await CheckAsync(20m, 72m, 14.4m);
            var laterRollback = await laterIssues.RollbackAsync([later.BatchNo]);
            Assert.True(laterRollback.Succeeded, laterRollback.ErrorMessage);
            await CheckAsync(21m, postedStockValue, postedProductionValue);
            await using var afterLaterRollback = await _factory.CreateDbContextAsync();
            Assert.Equal(new DateTime(2026, 10, 1, 12, 0, 0),
                (await afterLaterRollback.IvBalLocs.SingleAsync(b => b.Id == 58)).TransDate);
        }

        async Task RollbackIssueAsync(decimal expectedValue)
        {
            var reversed = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo,
                Reason = "verify backdated receipt rollback"
            });
            Assert.True(reversed.Succeeded, reversed.Message);
            await using var db = await _factory.CreateDbContextAsync();
            var original = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var materialReversal = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.OriginalMovementId == original.Uid
                && m.MovementType == ProductionMaterialMovementTypes.IssueReversal);
            var poolReversal = await db.ProductionBalLotMovements.SingleAsync(m =>
                m.OriginalMovementId == original.ProductionBalLotMovementId
                && m.MovementType == ProductionBalLotMovementTypes.IssueReversal);
            Assert.Equal(expectedValue, materialReversal.TotalCost);
            Assert.Equal(expectedValue, poolReversal.TotalCost);
        }
        if (receiptFirst)
        {
            var reversed = await RollbackReceiptAsync();
            Assert.True(reversed.Succeeded, reversed.Error);
            await CheckAsync(consumeAt == 6 ? 15m : 16m, consumeAt == 6 ? 30m : 32m, 8m);
            var repeated = await PostReceiptAsync();
            Assert.True(repeated.Succeeded, repeated.Error);
            await CheckAsync(postedStockQty, postedStockValue, postedProductionValue);
            var removedAgain = await RollbackReceiptAsync();
            Assert.True(removedAgain.Succeeded, removedAgain.Error);
            await CheckAsync(consumeAt == 6 ? 15m : 16m, consumeAt == 6 ? 30m : 32m, 8m);
            if (preexistingIssue is not null)
            {
                var laterRollback = await preexistingIssue.RollbackAsync([preexistingIssueBatch]);
                Assert.True(laterRollback.Succeeded, laterRollback.ErrorMessage);
                await CheckAsync(16m, 32m, 8m);
            }
            await RollbackIssueAsync(8m);
        }
        else
        {
            await RollbackIssueAsync(postedProductionValue);
            await CheckAsync(25m, standard ? 50m : 90m, 0m);
            if (consumeAt == 3)
            {
                await using var afterIssueRollback = await _factory.CreateDbContextAsync();
                Assert.Equal(new DateTime(2026, 10, 1),
                    (await afterIssueRollback.IvBalLocs.SingleAsync(b => b.Id == 58)).TransDate);
            }
            var reversed = await RollbackReceiptAsync();
            Assert.True(reversed.Succeeded, reversed.Error);
        }
        await CheckAsync(20m, 40m, 0m);
        await using var final = await _factory.CreateDbContextAsync();
        if (goodsReceipt)
            Assert.Equal(0m, (await final.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 3)]
    [InlineData(true, true, 3)]
    [InlineData(false, true, 4)]
    [InlineData(true, true, 4)]
    [InlineData(false, true, 5)]
    [InlineData(true, true, 5)]
    public Task Backdated_fifo_receipt_reprices_unconsumed_production_issue_and_rolls_back(
        bool goodsReceipt, bool receiptFirst, int scenario) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst, scenario);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_fifo_receipt_reprices_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 6 : 7);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public Task Backdated_fifo_receipt_reprices_partially_consumed_production_output(
        bool goodsReceipt, bool receiptFirst, bool consumedBeforeReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            consumedBeforeReceipt ? 8 : 9);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_consumed_output_failure_rolls_back_everything(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 10);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public Task Backdated_fifo_receipt_refuses_active_finished_good_consumption(
        bool goodsReceipt, bool finishedGoodFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true,
            finishedGoodFirst ? 11 : 12);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Backdated_fifo_receipt_can_replay_after_finished_good_reversal(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 13 : 14);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public Task Backdated_fifo_receipt_after_reversing_fully_received_partial_output(
        bool goodsReceipt, bool finishedGoodFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, !finishedGoodFirst,
            finishedGoodFirst ? 19 : 20);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false)]
    public Task Backdated_fifo_receipt_preserves_active_finished_good_value(
        bool goodsReceipt, bool finishedGoodFirst, bool receiptFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            finishedGoodFirst ? 21 : 22);

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public Task Backdated_fifo_receipt_preserves_fully_received_partial_output(
        bool goodsReceipt, bool finishedGoodFirst, bool receiptFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst,
            finishedGoodFirst ? 25 : 26);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public Task Backdated_fifo_receipt_rejects_damaged_active_finished_good(bool goodsReceipt, int damageKind) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, damageKind == 0 ? 24 : damageKind == 1 ? 27 : 28);

    [Fact]
    public Task Fifo_replay_with_active_finished_goods_does_not_report_stale_cost_trace() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 23);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_fifo_receipt_rejects_damaged_finished_good_reversal(
        bool goodsReceipt, bool damageValuation) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, damageValuation ? 16 : 15);

    private async Task BackdatedFifoReceiptAndProductionIssueAsync(
        bool goodsReceipt, bool receiptFirst, int scenario)
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-FIFO");
        await SeedBalanceAsync(59, 2m);
        var receiptPrice = scenario is 3 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 ? 2m : 10m;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            db.MsUoms.Add(new() { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
            db.IvClasses.Add(new() { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
            (await db.IvStockMasters.SingleAsync(s => s.ICode == "RM001")).IClassCode = "RAW";
            if (scenario is 11 or 12 or 13 or 14 or 15 or 16 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28)
                await ConfigureFinishedGoodRouteAsync(db, graph.Process2Id);
            (await db.StockCostPolicyRevisions.SingleAsync()).CostMethod = StockCostMethods.Fifo;
            await db.StockCostStates.Where(s => s.ItemCode == "RM001").ExecuteDeleteAsync();
            if (goodsReceipt)
                db.PoOrders.Add(new PoOrder
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", PoNo = "PO-IP-FIFO",
                    PoRelNo = 1, PoDate = new DateTime(2026, 9, 30), Status = "NEW",
                    VendCode = "SUP01", CurCode = "MYR", RowVersion = [1],
                    Details = [new PoOrderDetail
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ", PoNo = "PO-IP-FIFO",
                        PoRelNo = 1, Line = 1, ICode = "RM001", PoQty = 10m,
                        PoPurQty = 10m, PoUnitPrice = receiptPrice, NetAmount = 10m * receiptPrice,
                        PurchaseUom = "EA", StdUom = "EA", PackSz = 1m,
                        CurCode = "MYR", ToWarehouse = "WH01"
                    }]
                });
            await db.SaveChangesAsync();
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "RM001",
                [new FifoLayerSeed(2m, 2m, new DateTime(2026, 9, 1))]);
        }
        if (scenario == 23)
            await SeedTraceablePredecessorAsync(graph);
        var receipts = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, new DateTime(2026, 10, 3), enableBackdatedReplay: true);
        IvMiscReceiptSaveRequest Receipt(DateTime date, decimal quantity, decimal price) => new()
        {
            TrxDate = date,
            Lines = [new IvMiscReceiptLineRequest
            {
                ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                Quantity = quantity, UnitPrice = price, PriceConfirmed = true,
                Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                Reason = "ADJ"
            }]
        };
        var laterReceipt = await receipts.SaveNewAsync(Receipt(new DateTime(2026, 10, 2), 18m, 2m));
        Assert.True(laterReceipt.Succeeded, laterReceipt.ErrorMessage);
        var laterPosted = await receipts.PostAsync([laterReceipt.BatchNo]);
        Assert.True(laterPosted.Succeeded, laterPosted.ErrorMessage);

        var outputs = CreateOutputService(new DateTime(2026, 10, 3));
        var predecessor = await outputs.CreateAsync(OutputRequest(
            graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(predecessor.Succeeded, predecessor.Message);
        Assert.True((await outputs.PostAsync(predecessor.Data!.Uid)).Succeeded);
        var issues = CreateIssueService(InventoryLedgerTestFixture.CreateInventoryPosting(
            _factory, enableBackdatedReplay: true), new DateTime(2026, 10, 3));
        var request = await CreateIssueSaveAsync(graph, 59, 4m);
        request.ProductionQtyThisIssue = 4m;
        request.TrxDateTime = new DateTime(2026, 10, 3, 12, 0, 0);
        var issue = await issues.CreateAsync(request);
        Assert.True(issue.Succeeded, issue.Message);
        var issued = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issued.Succeeded && issued.Data!.SucceededCount == 1,
            issued.Data?.Batches.FirstOrDefault()?.Message ?? issued.Message);

        long consumedOutputId = 0;
        var consumedQty = scenario is 8 or 9 or 19 or 20 or 25 or 26 ? 2m : 4m;
        async Task ConsumeIssueAsync()
        {
            var created = await outputs.CreateAsync(OutputRequest(
                graph.Process2Id, consumedQty, new DateTime(2026, 10, 3, 13, 0, 0)));
            Assert.True(created.Succeeded, created.Message);
            var result = await outputs.PostAsync(created.Data!.Uid);
            Assert.True(result.Succeeded, result.Message);
            consumedOutputId = created.Data.Uid;
        }
        if (scenario is 6 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28)
            await ConsumeIssueAsync();
        if (scenario == 10)
        {
            await using var inject = await _factory.CreateDbContextAsync();
            var produceId = await inject.ProductionBalLotMovements.Where(m =>
                    m.ProductionOutputId == consumedOutputId
                    && m.MovementType == ProductionBalLotMovementTypes.Produce)
                .Select(m => m.Uid).SingleAsync();
            await inject.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER TestRejectProductionReplay BEFORE INSERT ON ProductionCostRestatementFact "
                + "WHEN NEW.ProductionBalLotMovementId = " + produceId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " BEGIN SELECT RAISE(ABORT, 'injected replay failure'); END;");
        }
        IvMiscIssueService? laterIssue = null;
        var laterIssueBatch = 0;
        if (scenario == 4)
        {
            laterIssue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, new DateTime(2026, 10, 4), enableBackdatedReplay: true);
            var saved = await laterIssue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = new DateTime(2026, 10, 4),
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 59, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 1m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 2m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            laterIssueBatch = saved.BatchNo;
            var postedLater = await laterIssue.PostAsync([laterIssueBatch]);
            Assert.True(postedLater.Succeeded, postedLater.ErrorMessage);
        }

        IvGoodsReceiptService? goods = null;
        int receiptBatch;
        if (goodsReceipt)
        {
            var tenant = InventoryTenantTestHelper.CreateTenantContext();
            goods = new IvGoodsReceiptService(_factory, tenant, Access(), new RunningNumberService(),
                new FixedCurrentDateService(new DateTime(2026, 10, 3)),
                new IvStockMasterRepository(_factory), new IvStockCommonRepository(_factory),
                new IvStockTransactionRepository(), new IvStockPostingRepository(),
                InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true),
                NullLogger<IvGoodsReceiptService>.Instance);
            var saved = await goods.SaveNewAsync(new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 1),
                Lines = [new IvGoodsReceiptLineRequest
                {
                    PoNo = "PO-IP-FIFO", PoRelNo = 1, PoLineNo = 1,
                    ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            receiptBatch = saved.BatchNo;
        }
        else
        {
            var saved = await receipts.SaveNewAsync(Receipt(new DateTime(2026, 10, 1), 5m, receiptPrice));
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            receiptBatch = saved.BatchNo;
        }
        async Task<(bool Succeeded, string? Error)> PostReceiptAsync()
        {
            if (goods is not null)
            {
                var result = await goods.PostAsync([receiptBatch]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await receipts.PostAsync([receiptBatch]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }
        async Task<(bool Succeeded, string? Error)> RollbackReceiptAsync()
        {
            if (goods is not null)
            {
                var result = await goods.RollbackAsync([receiptBatch]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await receipts.RollbackAsync([receiptBatch]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }
        async Task CheckAsync(decimal stockQty, decimal stockValue, decimal productionValue)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(StockCostMethods.Fifo, state.CostMethod);
            Assert.Equal(stockQty, state.OnHandBaseQty);
            Assert.Equal(stockValue, state.InventoryValue);
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo
                && m.MovementType == ProductionMaterialMovementTypes.Issue);
            Assert.Equal(8m, material.TotalCost);
            var lot = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            Assert.Equal(productionValue, lot.TotalCost);
            Assert.Equal(productionValue, (await db.ProductionPoolValuationRows
                .SingleAsync(p => p.ProductionBalLotId == lot.Uid)).TrackedValue);
            if (productionValue > 0m)
            {
                var effective = await ProductionEffectiveCostResolver.ResolveAsync(
                    db, "DEMO", "HQ", [material.ProductionBalLotMovementId!.Value]);
                Assert.Equal(productionValue, effective[material.ProductionBalLotMovementId.Value].TotalCost);
                var history = await db.IvTrxHistories.SingleAsync(h => h.Id == material.InventoryHistoryId);
                var facts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                    db, "DEMO", "HQ", [history]))[history.Id];
                Assert.Equal(productionValue, facts.Sum(f => f.CostAmount));
                Assert.All(facts, f =>
                {
                    Assert.Equal(material.Uid, f.ProductionMovementId);
                    Assert.Equal(material.WorkOrderId, f.WorkOrderId);
                    Assert.Equal(material.WorkOrderOperationId, f.WorkOrderOperationId);
                    Assert.Equal(material.PostingLinkId, f.ProductionPostingLinkId);
                });
                if (productionValue == 24m)
                    Assert.Equal(2, facts.Count);
            }
        }
        async Task RollbackIssueAsync(decimal expectedValue)
        {
            var reversed = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "verify FIFO replay"
            });
            Assert.True(reversed.Succeeded, reversed.Message);
            await using var db = await _factory.CreateDbContextAsync();
            var original = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo && m.MovementType == ProductionMaterialMovementTypes.Issue);
            Assert.Equal(expectedValue, (await db.ProductionMaterialMovements.SingleAsync(m =>
                m.OriginalMovementId == original.Uid && m.MovementType == ProductionMaterialMovementTypes.IssueReversal)).TotalCost);
        }
        PostedFinishedGood? unchangedFinishedGood = null;
        if (scenario is 21 or 23 or 24 or 25 or 27 or 28)
        {
            unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
            Assert.Equal(6m, unchangedFinishedGood.Value);
            if (scenario is 24 or 27 or 28)
                await DamageActiveFinishedGoodAsync(unchangedFinishedGood.Document.Id,
                    scenario == 24 ? 0 : scenario == 27 ? 1 : 2);
            if (scenario == 23)
            {
                var originalTrace = await unchangedFinishedGood.Service.GetCostTraceAsync(
                    unchangedFinishedGood.Document.Id, unchangedFinishedGood.Document.Lines.Single().SourceId);
                Assert.True(originalTrace.Succeeded, originalTrace.Message);
                Assert.True(originalTrace.Data!.TraceStatus == "VERIFIED_WITH_UNCLASSIFIED",
                    string.Join("; ", originalTrace.Data.Warnings));
                Assert.Equal(6m, originalTrace.Data.Components.Sum(x => x.Amount));
            }
        }
        if (scenario == 11)
            Assert.Equal(6m, (await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3))).Value);
        if (scenario is 13 or 15 or 16 or 19)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
            Assert.Equal(6m, finished.Value);
            await RollbackFinishedGoodAsync(finished);
            if (scenario is 15 or 16)
                await DamageFinishedGoodReversalAsync(finished.Document.Id, scenario == 16);
        }
        var posted = await PostReceiptAsync();
        if (scenario is 11 or 15 or 16 or 24 or 27 or 28)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(32m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Empty(await unchanged.ProductionCostRestatementFacts.ToArrayAsync());
            if (goodsReceipt)
                Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (scenario == 10)
        {
            Assert.False(posted.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(32m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Empty(await unchanged.ProductionCostRestatementFacts.ToArrayAsync());
            Assert.Empty(await unchanged.StockReplayRuns.ToArrayAsync());
            Assert.Empty(await unchanged.StockReplayEffects.ToArrayAsync());
            var consumption = await unchanged.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionOutputId == consumedOutputId && m.WorkOrderMaterialId == graph.MaterialId
                && m.MovementType == ProductionBalLotMovementTypes.Consume);
            Assert.Equal(8m, consumption.TotalCost);
            if (goodsReceipt)
                Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        Assert.True(posted.Succeeded, posted.Error);
        if (scenario is 21 or 22 or 23 or 25 or 26)
        {
            if (scenario is 22 or 26)
                unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
            Assert.NotNull(unchangedFinishedGood);
            Assert.Equal(6m, unchangedFinishedGood.Value);
            await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            if (scenario == 23)
            {
                var correctedTrace = await unchangedFinishedGood.Service.GetCostTraceAsync(
                    unchangedFinishedGood.Document.Id, unchangedFinishedGood.Document.Lines.Single().SourceId);
                Assert.True(correctedTrace.Succeeded, correctedTrace.Message);
                Assert.Equal("INCONSISTENT", correctedTrace.Data!.TraceStatus);
                Assert.Contains(correctedTrace.Data.Warnings, warning =>
                    warning.Contains("replayed cost or FIFO origin", StringComparison.Ordinal));
            }
            if (!receiptFirst)
                await RollbackFinishedGoodAsync(unchangedFinishedGood);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            var repeated = await PostReceiptAsync();
            Assert.True(repeated.Succeeded, repeated.Error);
            if (receiptFirst)
                await AssertActiveFinishedGoodUnchangedAsync(unchangedFinishedGood);
            removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await RollbackFinishedGoodAsync(unchangedFinishedGood);
            var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "verify unchanged finished-good cost"
            });
            Assert.True(outputReversal.Succeeded, outputReversal.Message);
            await RollbackIssueAsync(8m);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            Assert.Empty(await restored.ProductionCostRestatementFacts.ToArrayAsync());
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (scenario == 12)
        {
            Assert.Equal(14m, (await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3))).Value);
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(66m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (scenario is 13 or 14 or 19 or 20)
        {
            if (scenario is 14 or 20)
            {
                var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
                Assert.Equal(14m, finished.Value);
                await RollbackFinishedGoodAsync(finished);
            }
            async Task RollbackOutputAsync()
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "finished goods were reversed"
                });
                Assert.True(outputReversal.Succeeded, outputReversal.Message);
            }
            if (!receiptFirst)
                await RollbackOutputAsync();
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            var reposted = await PostReceiptAsync();
            Assert.True(reposted.Succeeded, reposted.Error);
            removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            if (receiptFirst)
                await RollbackOutputAsync();
            await RollbackIssueAsync(8m);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (scenario is 6 or 7 or 8 or 9)
        {
            if (scenario is 7 or 9)
                await ConsumeIssueAsync();
            async Task CheckConsumedAsync(decimal expectedIssueCost, decimal expectedStockValue)
            {
                await using var current = await _factory.CreateDbContextAsync();
                var issueMaterial = await current.ProductionMaterialMovements.SingleAsync(m =>
                    m.InventoryBatchNo == issue.Data.BatchNo
                    && m.MovementType == ProductionMaterialMovementTypes.Issue);
                var consumed = await current.ProductionBalLotMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId && m.WorkOrderMaterialId == graph.MaterialId
                    && m.MovementType == ProductionBalLotMovementTypes.Consume);
                var produce = await current.ProductionBalLotMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
                var inputs = await current.ProductionBalLotMovements.Where(m =>
                    m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.Consume)
                    .ToArrayAsync();
                var ids = inputs.Select(m => m.Uid).Append(produce.Uid)
                    .Append(issueMaterial.ProductionBalLotMovementId!.Value).ToArray();
                var effective = await ProductionEffectiveCostResolver.ResolveAsync(current, "DEMO", "HQ", ids);
                Assert.Equal(expectedIssueCost, effective[issueMaterial.ProductionBalLotMovementId.Value].TotalCost);
                Assert.Equal(expectedIssueCost * consumedQty / 4m, effective[consumed.Uid].TotalCost);
                var history = await current.IvTrxHistories.SingleAsync(h => h.Id == issueMaterial.InventoryHistoryId);
                var issueFacts = (await StockReplayValuationGenerationResolver.ResolveAsync(
                    current, "DEMO", "HQ", [history]))[history.Id];
                Assert.Equal(expectedIssueCost, issueFacts.Sum(f => f.CostAmount));
                Assert.All(issueFacts, f =>
                {
                    Assert.Equal(issueMaterial.Uid, f.ProductionMovementId);
                    Assert.Equal(issueMaterial.WorkOrderId, f.WorkOrderId);
                    Assert.Equal(issueMaterial.WorkOrderOperationId, f.WorkOrderOperationId);
                    Assert.Equal(issueMaterial.PostingLinkId, f.ProductionPostingLinkId);
                });
                var conversion = await current.ProductionConversionCostFacts
                    .Where(f => f.ProductionMovementId == produce.Uid && f.ReversesFactId == null)
                    .Select(f => f.CostAmount).ToArrayAsync();
                Assert.Equal(inputs.Sum(m => effective[m.Uid].TotalCost) + conversion.Sum(),
                    effective[produce.Uid].TotalCost);
                Assert.Equal(effective[produce.Uid].TotalCost,
                    (await current.ProductionPoolValuationRows.SingleAsync(p =>
                        p.ProductionBalLotId == produce.ProductionBalLotId)).TrackedValue);
                Assert.Equal(expectedStockValue,
                    (await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            }
            async Task RollbackOutputAsync()
            {
                var reversed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "verify repriced FIFO Production output"
                });
                Assert.True(reversed.Succeeded, reversed.Message);
            }
            await CheckConsumedAsync(24m, 66m);
            if (receiptFirst)
            {
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
                await CheckConsumedAsync(8m, 32m);
                var repeated = await PostReceiptAsync();
                Assert.True(repeated.Succeeded, repeated.Error);
                await CheckConsumedAsync(24m, 66m);
                var removedAgain = await RollbackReceiptAsync();
                Assert.True(removedAgain.Succeeded, removedAgain.Error);
                await CheckConsumedAsync(8m, 32m);
                await RollbackOutputAsync();
            }
            else
            {
                await RollbackOutputAsync();
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
            }
            await RollbackIssueAsync(8m);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            if (goodsReceipt)
                Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        var postedQty = scenario == 4 ? 20m : 21m;
        var postedStockValue = scenario == 3 ? 42m : scenario == 4 ? 56m : 66m;
        var postedProductionValue = scenario == 3 ? 8m : 24m;
        await CheckAsync(postedQty, postedStockValue, postedProductionValue);
        if (scenario == 5)
        {
            await using (var corrupt = await _factory.CreateDbContextAsync())
            {
                var changed = await corrupt.StockValuationFacts.Where(f =>
                        f.MovementCode == "PRODUCTION_MATERIAL_OUT"
                        && f.OriginalValuationFactId != null
                        && f.ReversesValuationFactId == null)
                    .ExecuteUpdateAsync(update => update.SetProperty(
                        f => f.ProductionMovementId, (long?)null));
                Assert.Equal(2, changed);
            }
            var rejectedReceipt = await RollbackReceiptAsync();
            Assert.False(rejectedReceipt.Succeeded);
            var rejectedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "damaged FIFO ownership"
            });
            Assert.False(rejectedIssue.Succeeded);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(66m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == issue.Data.BatchNo)).BatchStatus);
            if (goodsReceipt)
                Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (receiptFirst)
        {
            var reversed = await RollbackReceiptAsync();
            Assert.True(reversed.Succeeded, reversed.Error);
            await CheckAsync(scenario == 4 ? 15m : 16m, scenario == 4 ? 30m : 32m, 8m);
            var repeated = await PostReceiptAsync();
            Assert.True(repeated.Succeeded, repeated.Error);
            await CheckAsync(postedQty, postedStockValue, postedProductionValue);
            var removedAgain = await RollbackReceiptAsync();
            Assert.True(removedAgain.Succeeded, removedAgain.Error);
            await CheckAsync(scenario == 4 ? 15m : 16m, scenario == 4 ? 30m : 32m, 8m);
            if (laterIssue is not null)
            {
                var laterRollback = await laterIssue.RollbackAsync([laterIssueBatch]);
                Assert.True(laterRollback.Succeeded, laterRollback.ErrorMessage);
                await CheckAsync(16m, 32m, 8m);
            }
            await RollbackIssueAsync(8m);
        }
        else
        {
            await RollbackIssueAsync(24m);
            await CheckAsync(25m, 90m, 0m);
            var reversed = await RollbackReceiptAsync();
            Assert.True(reversed.Succeeded, reversed.Error);
        }
        await CheckAsync(20m, 40m, 0m);
        await using var final = await _factory.CreateDbContextAsync();
        if (goodsReceipt)
            Assert.Equal(0m, (await final.PoOrderDetails.SingleAsync()).RecvQty);
    }

    [Fact]
    public async Task Stale_next_process_issue_draft_is_rejected_after_predecessor_good_returns_to_zero()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-STALE");
        await SeedBalanceAsync(51, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 51, 1m));
        Assert.True(draft.Succeeded, draft.Message);

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "clear predecessor Good",
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        var post = await issues.PostAsync([draft.Data!.BatchNo]);
        Assert.True(post.Succeeded, post.Message);
        Assert.Equal(0, post.Data!.SucceededCount);
        Assert.Equal(1, post.Data.FailedCount);
        Assert.Contains("must post Good qty first",
            post.Data.Batches[0].Message, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.Empty(await db.ProductionMaterialMovements
            .Where(x => x.MovementType == ProductionMaterialMovementTypes.Issue)
            .ToListAsync());
    }

    [Fact]
    public async Task Active_next_process_issue_blocks_predecessor_rollback_to_zero_and_clears_after_issue_rollback()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-RB");
        await SeedBalanceAsync(52, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 52, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        var posted = await issues.PostAsync([draft.Data!.BatchNo]);
        Assert.True(posted.Succeeded && posted.Data!.SucceededCount == 1, posted.Message);

        var blocked = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "active downstream issue",
        });
        Assert.False(blocked.Succeeded);
        Assert.Contains("downstream Issue-to-Production remains active", blocked.Message, StringComparison.OrdinalIgnoreCase);

        var issueRollback = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"),
            InventoryBatchNo = draft.Data.BatchNo,
            Reason = "clear issued material",
        });
        Assert.True(issueRollback.Succeeded, issueRollback.Message);

        var allowed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "issue already reversed",
        });
        Assert.True(allowed.Succeeded, allowed.Message);
    }

    [Fact]
    public async Task Fully_returned_downstream_issue_does_not_block_predecessor_rollback()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-IP-RET");
        await SeedBalanceAsync(53, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var p1 = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(p1.Succeeded, p1.Message);
        Assert.True((await outputs.PostAsync(p1.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph, 53, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        Assert.True((await issues.PostAsync([draft.Data!.BatchNo])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == graph.MaterialId);
            material.ReturnedQty = material.IssuedQty;
            await db.SaveChangesAsync();
        }

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = p1.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "returned issue has no net qty",
        });
        Assert.True(rollback.Succeeded, rollback.Message);
    }

    [Fact]
    public async Task Production_stock_return_rolls_back_after_later_return_is_reversed()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-PRTN-POST");
        await SeedBalanceAsync(56, 20m);

        var outputs = CreateOutputService();
        var firstOutput = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(firstOutput.Succeeded, firstOutput.Message);
        Assert.True((await outputs.PostAsync(firstOutput.Data!.Uid)).Succeeded);

        var issues = CreateIssueService();
        var issueRequest = await CreateIssueSaveAsync(graph, 56, 2m);
        issueRequest.ProductionQtyThisIssue = 2m;
        var issueDraft = await issues.CreateAsync(issueRequest);
        Assert.True(issueDraft.Succeeded, issueDraft.Message);
        var issuePost = await issues.PostAsync([issueDraft.Data!.BatchNo]);
        Assert.True(issuePost.Succeeded && issuePost.Data!.SucceededCount == 1,
            issuePost.Data?.Batches.FirstOrDefault()?.Message ?? issuePost.Message);

        var returns = CreateReturnService();
        var sources = await returns.SearchSourcesAsync(new() { WorkOrderId = graph.OrderId, Take = 20 });
        Assert.True(sources.Succeeded, sources.Message);
        var source = Assert.Single(sources.Data!.Rows, x => x.ItemCode == "RM001");

        var first = await SaveReturnAsync(returns, graph.OrderId, source.ProductionBalLotId);
        var second = await SaveReturnAsync(returns, graph.OrderId, source.ProductionBalLotId);
        var firstDraft = await LoadReturnDraftAsync(returns, first.Id);
        var secondDraft = await LoadReturnDraftAsync(returns, second.Id);

        var newRollback = await returns.RollbackAsync(new(first.Id, firstDraft.RowVersion, Guid.NewGuid(), "Must still be NEW"));
        Assert.False(newRollback.Succeeded);
        Assert.Contains("Only a POSTED Production Stock Return", newRollback.Message, StringComparison.OrdinalIgnoreCase);

        var deniedPost = await CreateReturnService(canPost: false).PostAsync(
            new(first.Id, firstDraft.RowVersion, Guid.NewGuid()));
        Assert.False(deniedPost.Succeeded);
        Assert.Contains("Access denied", deniedPost.Message, StringComparison.OrdinalIgnoreCase);

        var firstPost = await returns.PostAsync(new(first.Id, firstDraft.RowVersion, Guid.NewGuid()));
        Assert.True(firstPost.Succeeded, firstPost.Message);
        Assert.Equal("POSTED", firstPost.Data!.Status);
        var secondPost = await returns.PostAsync(new(second.Id, secondDraft.RowVersion, Guid.NewGuid()));
        Assert.True(secondPost.Succeeded, secondPost.Message);

        var postedAgain = await returns.PostAsync(new(first.Id, firstPost.Data.RowVersion, Guid.NewGuid()));
        Assert.False(postedAgain.Succeeded);
        Assert.Contains("Only a NEW Production Stock Return", postedAgain.Message, StringComparison.OrdinalIgnoreCase);

        var deniedRollback = await CreateReturnService(canRollback: false).RollbackAsync(
            new(first.Id, firstPost.Data.RowVersion, Guid.NewGuid(), "Permission must be rechecked"));
        Assert.False(deniedRollback.Succeeded);
        Assert.Contains("Access denied", deniedRollback.Message, StringComparison.OrdinalIgnoreCase);

        var laterMovementBlocked = await returns.RollbackAsync(
            new(first.Id, firstPost.Data.RowVersion, Guid.NewGuid(), "Later return is still active"));
        Assert.False(laterMovementBlocked.Succeeded);
        Assert.Contains("later active", laterMovementBlocked.Message, StringComparison.OrdinalIgnoreCase);

        var secondRollback = await returns.RollbackAsync(
            new(second.Id, secondPost.Data!.RowVersion, Guid.NewGuid(), "Reverse later return first"));
        Assert.True(secondRollback.Succeeded, secondRollback.Message);
        Assert.Equal("NEW", secondRollback.Data!.Status);

        var firstRollback = await returns.RollbackAsync(
            new(first.Id, firstPost.Data.RowVersion, Guid.NewGuid(), "Reverse earlier return second"));
        Assert.True(firstRollback.Succeeded, firstRollback.Message);
        Assert.Equal("NEW", firstRollback.Data!.Status);

        await using var db = await _factory.CreateDbContextAsync();
        var reversalFact = await db.ProductionStockReturnFacts.AsNoTracking()
            .SingleAsync(x => x.BatchId == second.Id && x.ReversesFactId != null);
        Assert.NotEqual(0, reversalFact.ProductionMaterialMovementId);
        Assert.True(await db.ProductionMaterialMovements.AsNoTracking()
            .AnyAsync(x => x.Uid == reversalFact.ProductionMaterialMovementId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Posted_return_rejects_legacy_rollback_after_either_ledger_is_restated(
        bool restateProduction)
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-PRTN-RESTATE");
        await SeedBalanceAsync(58, 20m);
        var outputs = CreateOutputService();
        var output = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m,
            new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(output.Succeeded, output.Message);
        Assert.True((await outputs.PostAsync(output.Data!.Uid)).Succeeded);

        var issues = CreateIssueService();
        var issueRequest = await CreateIssueSaveAsync(graph, 58, 2m);
        issueRequest.ProductionQtyThisIssue = 2m;
        var issue = await issues.CreateAsync(issueRequest);
        Assert.True(issue.Succeeded, issue.Message);
        var issuePost = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issuePost.Succeeded && issuePost.Data!.SucceededCount == 1,
            issuePost.Data?.Batches.FirstOrDefault()?.Message ?? issuePost.Message);

        var returns = CreateReturnService();
        var sources = await returns.SearchSourcesAsync(new() { WorkOrderId = graph.OrderId, Take = 20 });
        Assert.True(sources.Succeeded, sources.Message);
        var source = Assert.Single(sources.Data!.Rows, x => x.ItemCode == "RM001");
        var draft = await SaveReturnAsync(returns, graph.OrderId, source.ProductionBalLotId);
        var post = await returns.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(post.Succeeded, post.Message);
        var posted = post.Data!;
        var originalValue = Assert.Single(posted.Lines).TotalValue!.Value;
        var correctedValue = originalValue + 1m;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var fact = await db.ProductionStockReturnFacts.SingleAsync(x =>
                x.BatchId == draft.Id && x.ReversesFactId == null);
            var movement = await db.ProductionBalLotMovements.SingleAsync(x =>
                x.Uid == fact.ProductionMovementId);
            var owner = await db.StockPostings.SingleAsync(x => x.Id == fact.StockPostingId);
            var epoch = await db.StockLedgerEpochs.SingleAsync();
            var counter = await db.StockPostingBranchSequences.SingleAsync();
            var rootAt = owner.EffectiveAt.AddDays(-1);
            var root = new StockPosting
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
                PostingSequence = ++counter.LastSequence, RequestId = Guid.NewGuid(),
                CommandType = "PRTN_RESTATEMENT_TEST", RequestFingerprint = new string('C', 64),
                SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
                SourceDocumentId = "PRTN-COST", SourceDocumentNo = "PRTN-COST",
                DocumentRevision = 1, SourceSnapshotJson = "{}",
                SourceSnapshotHash = new string('D', 64), EffectiveAt = rootAt,
                BusinessDate = rootAt.Date, PeriodKey = "2026-10",
                PostedAtUtc = DateTime.UtcNow, PostedBy = "test"
            };
            db.StockPostings.Add(root);
            await db.SaveChangesAsync();
            db.StockReplayRuns.Add(new StockReplayRun
            {
                CompanyCode = "DEMO", BranchCode = "HQ", RootStockPostingId = root.Id,
                RootRequestId = root.RequestId, RootDocumentType = root.SourceDocumentType,
                RootDocumentId = root.SourceDocumentId, EarliestEffectiveAt = rootAt,
                LatestEffectiveAt = owner.EffectiveAt, PlanHash = new string('E', 64),
                CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
            });
            await db.SaveChangesAsync();
            var context = new StockPostingContext(db, epoch, root, "test");
            if (restateProduction)
            {
                var current = (await ProductionEffectiveCostResolver.ResolveAsync(db,
                    "DEMO", "HQ", [movement.Uid]))[movement.Uid];
                await ProductionCostRestatementWriter.AppendWithinRootAsync(context,
                    current, correctedValue,
                    StockLedgerPrecision.Money(correctedValue / movement.BaseQty));
            }
            else
            {
                var receiptFact = await db.StockValuationFacts.AsNoTracking().SingleAsync(x =>
                    x.InventoryHistoryId == fact.InventoryHistoryId && x.StockPostingId == owner.Id
                    && x.ReversesValuationFactId == null);
                var result = await StockReplayWacReceiptRestatementWriter.AppendAsync(context,
                    fact.InventoryHistoryId, receiptFact.Id, correctedValue,
                    StockValuationSources.ProductionActual, 10, 11);
                Assert.NotNull(result?.ActiveDelta);
            }
            root.SealedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var currentReturn = await returns.GetAsync(draft.Id);
        Assert.True(currentReturn.Succeeded, currentReturn.Message);
        Assert.Equal(restateProduction ? correctedValue : originalValue,
            Assert.Single(currentReturn.Data!.Lines).TotalValue);
        var rollback = await returns.RollbackAsync(new(draft.Id, currentReturn.Data.RowVersion,
            Guid.NewGuid(), "Restated cost requires replay-aware rollback"));
        Assert.False(rollback.Succeeded);
        Assert.Contains(restateProduction ? "corrected Production cost" : "corrected Inventory valuation",
            rollback.Message, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(originalValue, (await verify.ProductionStockReturnFacts.AsNoTracking()
            .SingleAsync(x => x.BatchId == draft.Id && x.ReversesFactId == null)).TotalValue);
        Assert.False(await verify.ProductionStockReturnFacts.AsNoTracking()
            .AnyAsync(x => x.BatchId == draft.Id && x.ReversesFactId != null));
    }

    [Fact]
    public async Task Isolated_return_derives_corrected_value_from_exact_issue_contribution()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-PRTN-SOURCE");
        await SeedBalanceAsync(59, 20m);
        var outputs = CreateOutputService();
        var output = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m,
            new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(output.Succeeded, output.Message);
        Assert.True((await outputs.PostAsync(output.Data!.Uid)).Succeeded);

        var issues = CreateIssueService();
        var issueRequest = await CreateIssueSaveAsync(graph, 59, 2m);
        issueRequest.ProductionQtyThisIssue = 2m;
        var issue = await issues.CreateAsync(issueRequest);
        Assert.True(issue.Succeeded, issue.Message);
        var issuePost = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issuePost.Succeeded && issuePost.Data!.SucceededCount == 1,
            issuePost.Data?.Batches.FirstOrDefault()?.Message ?? issuePost.Message);

        var returns = CreateReturnService();
        var sources = await returns.SearchSourcesAsync(new() { WorkOrderId = graph.OrderId, Take = 20 });
        Assert.True(sources.Succeeded, sources.Message);
        var source = Assert.Single(sources.Data!.Rows, x => x.ItemCode == "RM001");
        var draft = await SaveReturnAsync(returns, graph.OrderId, source.ProductionBalLotId);
        var posted = await returns.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        var originalReturnValue = Assert.Single(posted.Data!.Lines).TotalValue!.Value;

        await using var db = await _factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var issueMovement = await db.ProductionBalLotMovements.SingleAsync(x =>
            x.ProductionBalLotId == source.ProductionBalLotId
            && x.MovementType == ProductionBalLotMovementTypes.Issue);
        var issueOwner = await db.StockPostings.SingleAsync(x => x.Id == issueMovement.StockPostingId);
        var returnOwner = await db.StockPostings.SingleAsync(x => x.Id == posted.Data.PostingId);
        var epoch = await db.StockLedgerEpochs.SingleAsync();
        var counter = await db.StockPostingBranchSequences.SingleAsync();
        var rootAt = issueOwner.EffectiveAt.AddHours(-1);
        var root = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
            PostingSequence = ++counter.LastSequence, RequestId = Guid.NewGuid(),
            CommandType = "PRTN_SOURCE_TEST", RequestFingerprint = new string('C', 64),
            SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
            SourceDocumentId = "PRTN-SOURCE", SourceDocumentNo = "PRTN-SOURCE",
            DocumentRevision = 1, SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64), EffectiveAt = rootAt,
            BusinessDate = rootAt.Date, PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow, PostedBy = "test"
        };
        db.StockPostings.Add(root);
        await db.SaveChangesAsync();
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ", RootStockPostingId = root.Id,
            RootRequestId = root.RequestId, RootDocumentType = root.SourceDocumentType,
            RootDocumentId = root.SourceDocumentId, EarliestEffectiveAt = rootAt,
            LatestEffectiveAt = returnOwner.EffectiveAt, PlanHash = new string('E', 64),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
        });
        await db.SaveChangesAsync();
        var context = new StockPostingContext(db, epoch, root, "test");
        var originalIssue = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [issueMovement.Uid]))[issueMovement.Uid];
        var correctedIssueValue = originalIssue.TotalCost + 2m;
        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, originalIssue,
            correctedIssueValue,
            StockLedgerPrecision.Money(correctedIssueValue / issueMovement.BaseQty));

        var unmatchedLedger = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(context, draft.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, unmatchedLedger.Error.Code);
        var originalInventory = await db.StockValuationFacts.SingleAsync(x =>
            x.InventoryHistoryId == issueMovement.InventoryHistoryId
            && x.StockPostingId == issueOwner.Id && x.Direction < 0
            && x.ReversesValuationFactId == null);
        var reversedInventory = (StockValuationFact)db.Entry(originalInventory).CurrentValues.ToObject();
        reversedInventory.Id = 0;
        reversedInventory.StockPostingId = root.Id;
        reversedInventory.PostingLineNo = 10;
        reversedInventory.Direction = 1;
        reversedInventory.ValuationSource = StockValuationSources.OriginalReversal;
        reversedInventory.ValuationStatus = StockValuationStatuses.Reversed;
        reversedInventory.ReversesValuationFactId = originalInventory.Id;
        reversedInventory.CreatedAtUtc = DateTime.UtcNow;
        var replacementInventory = (StockValuationFact)db.Entry(originalInventory).CurrentValues.ToObject();
        replacementInventory.Id = 0;
        replacementInventory.StockPostingId = root.Id;
        replacementInventory.PostingLineNo = 11;
        replacementInventory.CostAmount = correctedIssueValue;
        replacementInventory.BaseCostAmount = correctedIssueValue;
        replacementInventory.UnitCost = StockLedgerPrecision.Money(
            correctedIssueValue / issueMovement.BaseQty);
        replacementInventory.OriginalValuationFactId = originalInventory.Id;
        replacementInventory.ValuationVersion = originalInventory.ValuationVersion + 1;
        replacementInventory.CreatedAtUtc = DateTime.UtcNow;
        db.StockValuationFacts.AddRange(reversedInventory, replacementInventory);
        await db.SaveChangesAsync();

        var statePlan = await ProductionStockReturnTerminalWacStatePlanner
            .PrepareWithinRootAsync(context, draft.Id);
        Assert.Equal(38m, statePlan.Permit.CurrentValue);
        Assert.Equal(37m, statePlan.Permit.FinalValue);
        var projection = await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
            context, draft.Id);
        Assert.Equal(issueMovement.Uid, projection.SourceIssueMovementId);
        Assert.Equal(correctedIssueValue, projection.CorrectedSourceValue);
        Assert.Equal(originalReturnValue + 1m, projection.CorrectedReturnValue);
        Assert.Equal(correctedIssueValue - projection.CorrectedReturnValue,
            projection.ProjectedRemainingValue);

        await transaction.CreateSavepointAsync("before_wrong_return_material");
        var returnFact = await db.ProductionStockReturnFacts.AsNoTracking()
            .SingleAsync(x => x.Id == projection.ReturnFactId);
        var returnMaterial = await db.ProductionMaterialMovements.SingleAsync(x =>
            x.Uid == returnFact.ProductionMaterialMovementId);
        returnMaterial.ItemCode = "WRONG-ITEM";
        await db.SaveChangesAsync();
        var wrongMaterial = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(context, draft.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongMaterial.Error.Code);
        await transaction.RollbackToSavepointAsync("before_wrong_return_material");
        db.ChangeTracker.Clear();

        await transaction.CreateSavepointAsync("before_missing_return_edge");
        var allocation = await db.ProductionMovementAllocations.SingleAsync(x =>
            x.OutboundMovementId == projection.ReturnMovementId
            && x.ReversesAllocationId == null);
        db.ProductionMovementAllocations.Remove(allocation);
        await db.SaveChangesAsync();
        var missingEdge = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(context, draft.Id));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingEdge.Error.Code);
        await transaction.RollbackToSavepointAsync("before_missing_return_edge");

        var expectedReturn = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [projection.ReturnMovementId]))[projection.ReturnMovementId];
        var missingProductionCorrection = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
                context, statePlan));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            missingProductionCorrection.Error.Code);
        var missingReceiptCorrection = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
                context, draft.Id, expectedReturn));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            missingReceiptCorrection.Error.Code);
        var returnReceipt = await db.StockValuationFacts.AsNoTracking().SingleAsync(x =>
            x.InventoryHistoryId == projection.ReturnHistoryId && x.Direction > 0
            && x.ReversesValuationFactId == null);
        await transaction.CreateSavepointAsync("before_wrong_return_value");
        var wrongInventoryCorrection = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            context, projection.ReturnHistoryId, returnReceipt.Id,
            projection.CorrectedReturnValue + 1m, StockValuationSources.ProductionActual,
            20, 21);
        Assert.NotNull(wrongInventoryCorrection?.ActiveDelta);
        await db.SaveChangesAsync();
        var wrongValue = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
                context, draft.Id, expectedReturn));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongValue.Error.Code);
        Assert.Empty(await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(x => x.ProductionBalLotMovementId == projection.ReturnMovementId)
            .ToArrayAsync());
        await transaction.RollbackToSavepointAsync("before_wrong_return_value");
        db.ChangeTracker.Clear();
        await transaction.CreateSavepointAsync("before_wrong_return_identity");
        var wrongIdentityCorrection = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            context, projection.ReturnHistoryId, returnReceipt.Id,
            projection.CorrectedReturnValue, StockValuationSources.ProductionActual,
            20, 21);
        Assert.NotNull(wrongIdentityCorrection?.ActiveDelta);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET SourceDocumentId = {"TAMPERED"} WHERE Id = {wrongIdentityCorrection.ActiveDelta.Id};");
        var wrongIdentity = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
                context, draft.Id, expectedReturn));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongIdentity.Error.Code);
        Assert.Empty(await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(x => x.ProductionBalLotMovementId == projection.ReturnMovementId)
            .ToArrayAsync());
        await transaction.RollbackToSavepointAsync("before_wrong_return_identity");
        db.ChangeTracker.Clear();
        var inventoryCorrection = await StockReplayWacReceiptRestatementWriter.AppendAsync(
            context, projection.ReturnHistoryId, returnReceipt.Id,
            projection.CorrectedReturnValue, StockValuationSources.ProductionActual,
            20, 21);
        Assert.NotNull(inventoryCorrection?.ActiveDelta);
        await db.SaveChangesAsync();
        var corrected = await ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
            context, draft.Id, expectedReturn);
        Assert.NotNull(corrected.ReturnCost.Replacement);
        Assert.Equal(projection.CorrectedReturnValue, corrected.CorrectedReturnValue);
        Assert.Equal(projection.ProjectedRemainingValue, corrected.RemainingPoolValue);
        var effectiveReturn = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [projection.ReturnMovementId]))[projection.ReturnMovementId];
        Assert.Equal(projection.CorrectedReturnValue, effectiveReturn.TotalCost);
        var remaining = await db.ProductionBalLots.AsNoTracking().SingleAsync(x =>
            x.Uid == source.ProductionBalLotId);
        Assert.Equal(projection.ProjectedRemainingValue, remaining.TotalCost);
        Assert.Equal(remaining.TotalCost, (await db.ProductionPoolValuationRows.AsNoTracking()
            .SingleAsync(x => x.ProductionBalLotId == remaining.Uid)).TrackedValue);
        var factCount = await db.ProductionCostRestatementFacts.CountAsync(x =>
            x.ProductionBalLotMovementId == projection.ReturnMovementId);
        var retry = await ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
            context, draft.Id, expectedReturn);
        Assert.Null(retry.ReturnCost.Replacement);
        Assert.Equal(factCount, await db.ProductionCostRestatementFacts.CountAsync(x =>
            x.ProductionBalLotMovementId == projection.ReturnMovementId));
        Assert.Equal(projection.CorrectedReturnValue,
            (await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
                context, draft.Id)).CorrectedReturnValue);
        await transaction.CreateSavepointAsync("before_wrong_issue_reversal");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET CostAmount = CostAmount + 1, BaseCostAmount = BaseCostAmount + 1 WHERE Id = {reversedInventory.Id};");
        var wrongIssueFacts = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
                context, statePlan));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongIssueFacts.Error.Code);
        Assert.Equal(38m, (await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001")).InventoryValue);
        await transaction.RollbackToSavepointAsync("before_wrong_issue_reversal");
        db.ChangeTracker.Clear();
        await transaction.CreateSavepointAsync("before_wrong_return_source");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE StockValuationFact SET SourceDocumentId = {"TAMPERED"} WHERE Id = {inventoryCorrection.ActiveDelta!.Id};");
        var wrongReturnSource = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
                context, statePlan));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongReturnSource.Error.Code);
        Assert.Equal(38m, (await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001")).InventoryValue);
        await transaction.RollbackToSavepointAsync("before_wrong_return_source");
        db.ChangeTracker.Clear();
        await ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
            context, statePlan);
        var correctedState = await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001");
        Assert.Equal(19m, correctedState.OnHandBaseQty);
        Assert.Equal(37m, correctedState.InventoryValue);
        Assert.Equal(root.Id, (await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == correctedState.LastValuationFactId)).StockPostingId);
        await ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
            context, statePlan);
        Assert.Equal(37m, (await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001")).InventoryValue);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Rounded_unchanged_return_updates_only_remaining_material_pool_and_retries()
    {
        var graph = await SeedTwoProcessWithMaterialAsync("WO-PRTN-ROUND");
        await SeedBalanceAsync(60, 50m);
        var outputs = CreateOutputService();
        var output = await outputs.CreateAsync(OutputRequest(graph.Process1Id, 40m,
            new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(output.Succeeded, output.Message);
        Assert.True((await outputs.PostAsync(output.Data!.Uid)).Succeeded);

        var issues = CreateIssueService();
        var request = await CreateIssueSaveAsync(graph, 60, 40m);
        request.ProductionQtyThisIssue = 40m;
        var issue = await issues.CreateAsync(request);
        Assert.True(issue.Succeeded, issue.Message);
        var issuePost = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issuePost.Succeeded && issuePost.Data!.SucceededCount == 1,
            issuePost.Data?.Batches.FirstOrDefault()?.Message ?? issuePost.Message);

        var returns = CreateReturnService();
        var sources = await returns.SearchSourcesAsync(new() { WorkOrderId = graph.OrderId, Take = 20 });
        Assert.True(sources.Succeeded, sources.Message);
        var source = Assert.Single(sources.Data!.Rows, x => x.ItemCode == "RM001");
        var draft = await SaveReturnAsync(returns, graph.OrderId, source.ProductionBalLotId,
            0.0001m);
        var posted = await returns.PostAsync(new(draft.Id, draft.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(0.0002m, Assert.Single(posted.Data!.Lines).TotalValue);

        await using var db = await _factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var issueMovement = await db.ProductionBalLotMovements.SingleAsync(x =>
            x.ProductionBalLotId == source.ProductionBalLotId
            && x.MovementType == ProductionBalLotMovementTypes.Issue);
        var issueOwner = await db.StockPostings.SingleAsync(x => x.Id == issueMovement.StockPostingId);
        var returnOwner = await db.StockPostings.SingleAsync(x => x.Id == posted.Data.PostingId);
        var epoch = await db.StockLedgerEpochs.SingleAsync();
        var counter = await db.StockPostingBranchSequences.SingleAsync();
        var rootAt = issueOwner.EffectiveAt.AddHours(-1);
        var root = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epoch.Id,
            PostingSequence = ++counter.LastSequence, RequestId = Guid.NewGuid(),
            CommandType = "PRTN_ROUND_TEST", RequestFingerprint = new string('C', 64),
            SourceModule = "PRODUCTION", SourceDocumentType = "TEST",
            SourceDocumentId = "PRTN-ROUND", SourceDocumentNo = "PRTN-ROUND",
            DocumentRevision = 1, SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('D', 64), EffectiveAt = rootAt,
            BusinessDate = rootAt.Date, PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow, PostedBy = "test"
        };
        db.StockPostings.Add(root);
        await db.SaveChangesAsync();
        db.StockReplayRuns.Add(new StockReplayRun
        {
            CompanyCode = "DEMO", BranchCode = "HQ", RootStockPostingId = root.Id,
            RootRequestId = root.RequestId, RootDocumentType = root.SourceDocumentType,
            RootDocumentId = root.SourceDocumentId, EarliestEffectiveAt = rootAt,
            LatestEffectiveAt = returnOwner.EffectiveAt, PlanHash = new string('E', 64),
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "test"
        });
        await db.SaveChangesAsync();
        var context = new StockPostingContext(db, epoch, root, "test");
        var originalIssue = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [issueMovement.Uid]))[issueMovement.Uid];
        Assert.Equal(80m, originalIssue.TotalCost);
        var correctedIssueValue = 80.125m;
        await ProductionCostRestatementWriter.AppendWithinRootAsync(context, originalIssue,
            correctedIssueValue,
            StockLedgerPrecision.Money(correctedIssueValue / issueMovement.BaseQty));

        var originalInventory = await db.StockValuationFacts.SingleAsync(x =>
            x.InventoryHistoryId == issueMovement.InventoryHistoryId
            && x.StockPostingId == issueOwner.Id && x.Direction < 0
            && x.ReversesValuationFactId == null);
        var reversedInventory = (StockValuationFact)db.Entry(originalInventory).CurrentValues.ToObject();
        reversedInventory.Id = 0;
        reversedInventory.StockPostingId = root.Id;
        reversedInventory.PostingLineNo = 10;
        reversedInventory.Direction = 1;
        reversedInventory.ValuationSource = StockValuationSources.OriginalReversal;
        reversedInventory.ValuationStatus = StockValuationStatuses.Reversed;
        reversedInventory.ReversesValuationFactId = originalInventory.Id;
        reversedInventory.CreatedAtUtc = DateTime.UtcNow;
        var replacementInventory = (StockValuationFact)db.Entry(originalInventory).CurrentValues.ToObject();
        replacementInventory.Id = 0;
        replacementInventory.StockPostingId = root.Id;
        replacementInventory.PostingLineNo = 11;
        replacementInventory.CostAmount = correctedIssueValue;
        replacementInventory.BaseCostAmount = correctedIssueValue;
        replacementInventory.UnitCost = StockLedgerPrecision.Money(
            correctedIssueValue / issueMovement.BaseQty);
        replacementInventory.OriginalValuationFactId = originalInventory.Id;
        replacementInventory.ValuationVersion = originalInventory.ValuationVersion + 1;
        replacementInventory.CreatedAtUtc = DateTime.UtcNow;
        db.StockValuationFacts.AddRange(reversedInventory, replacementInventory);
        await db.SaveChangesAsync();

        var statePlan = await ProductionStockReturnTerminalWacStatePlanner
            .PrepareWithinRootAsync(context, draft.Id);
        Assert.Equal(20.0002m, statePlan.Permit.CurrentValue);
        Assert.Equal(19.8752m, statePlan.Permit.FinalValue);
        var projection = await ProductionStockReturnSourceCostAuthority.ReadWithinRootAsync(
            context, draft.Id);
        Assert.Equal(0.0002m, projection.CorrectedReturnValue);
        Assert.Equal(80.1248m, projection.ProjectedRemainingValue);
        var expectedReturn = (await ProductionEffectiveCostResolver.ResolveAsync(db,
            "DEMO", "HQ", [projection.ReturnMovementId]))[projection.ReturnMovementId];
        var missingPoolCorrection = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
                context, statePlan));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch,
            missingPoolCorrection.Error.Code);
        var first = await ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
            context, draft.Id, expectedReturn);
        Assert.Null(first.ReturnCost.Replacement);
        Assert.Equal(projection.ProjectedRemainingValue, first.RemainingPoolValue);
        Assert.Equal(projection.ProjectedRemainingValue,
            (await db.ProductionBalLots.AsNoTracking().SingleAsync(x =>
                x.Uid == source.ProductionBalLotId)).TotalCost);
        Assert.Equal(projection.ProjectedRemainingValue,
            (await db.ProductionPoolValuationRows.AsNoTracking().SingleAsync(x =>
                x.ProductionBalLotId == source.ProductionBalLotId)).TrackedValue);
        Assert.Empty(await db.ProductionCostRestatementFacts.AsNoTracking()
            .Where(x => x.ProductionBalLotMovementId == projection.ReturnMovementId)
            .ToArrayAsync());
        Assert.Empty(await db.StockValuationFacts.AsNoTracking()
            .Where(x => x.InventoryHistoryId == null
                && x.OriginalValuationFactId != null
                && x.StockPostingId == root.Id)
            .ToArrayAsync());
        var retry = await ProductionStockReturnCostCorrectionWriter.AppendWithinRootAsync(
            context, draft.Id, expectedReturn);
        Assert.Null(retry.ReturnCost.Replacement);
        Assert.Equal(projection.ProjectedRemainingValue, retry.RemainingPoolValue);
        await ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
            context, statePlan);
        var correctedState = await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001");
        Assert.Equal(10.0001m, correctedState.OnHandBaseQty);
        Assert.Equal(19.8752m, correctedState.InventoryValue);
        Assert.Equal(root.Id, (await db.StockValuationFacts.AsNoTracking()
            .SingleAsync(x => x.Id == correctedState.LastValuationFactId)).StockPostingId);
        await ProductionStockReturnTerminalWacStatePlanner.ApplyWithinRootAsync(
            context, statePlan);
        Assert.Equal(19.8752m, (await db.StockCostStates.AsNoTracking()
            .SingleAsync(x => x.ItemCode == "RM001")).InventoryValue);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Later_stage_material_issue_stays_blocked_on_partial_previous_stage()
    {
        var graph = await SeedTwoStageWithLaterMaterialAsync("WO-IP-STAGE");
        await SeedBalanceAsync(54, 20m);
        var issues = CreateIssueService();
        var outputs = CreateOutputService();

        var before = await issues.GetBomPreviewAsync(graph.LaterOperationId, 1m, new DateTime(2026, 10, 1));
        Assert.False(before.Succeeded);
        Assert.Contains("Previous Stage", before.Message, StringComparison.OrdinalIgnoreCase);

        var partial = await outputs.CreateAsync(OutputRequest(graph.FirstOperationId, 4m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(partial.Succeeded, partial.Message);
        Assert.True((await outputs.PostAsync(partial.Data!.Uid)).Succeeded);

        var stillBlocked = await issues.CreateAsync(await CreateIssueSaveAsync(graph.WorkOrderNo, graph.LaterOperationId, graph.MaterialId, 54, 1m));
        Assert.False(stillBlocked.Succeeded);
        Assert.Contains("Previous Stage", stillBlocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Later_stage_active_issue_blocks_predecessor_stage_rollback()
    {
        var graph = await SeedTwoStageWithLaterMaterialAsync("WO-IP-STAGE-RB");
        await SeedBalanceAsync(55, 20m);
        var outputs = CreateOutputService();
        var issues = CreateIssueService();

        var first = await outputs.CreateAsync(OutputRequest(graph.FirstOperationId, 10m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(first.Succeeded, first.Message);
        Assert.True((await outputs.PostAsync(first.Data!.Uid)).Succeeded);

        var draft = await issues.CreateAsync(await CreateIssueSaveAsync(graph.WorkOrderNo, graph.LaterOperationId, graph.MaterialId, 55, 1m));
        Assert.True(draft.Succeeded, draft.Message);
        Assert.True((await issues.PostAsync([draft.Data!.BatchNo])).Succeeded);

        var rollback = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
        {
            OutputId = first.Data.Uid,
            PostingRequestId = Guid.NewGuid().ToString("N"),
            Reason = "later stage issue is active",
        });
        Assert.False(rollback.Succeeded);
        Assert.Contains("downstream Issue-to-Production remains active", rollback.Message, StringComparison.OrdinalIgnoreCase);
    }

    private ProductionOutputService CreateOutputService(DateTime? businessDate = null) =>
        ProductionLedgerTestFixture.CreateProductionOutputService(_factory, businessDate: businessDate);

    private async Task SeedTraceablePredecessorAsync(GraphIds graph)
    {
        long poolId;
        long materialId;
        long routeId;
        await using (var setup = await _factory.CreateDbContextAsync())
        {
            var lot = await setup.ProductionBalLots.SingleAsync(l => l.WorkOrderId == graph.OrderId
                && l.ItemCode == "RM-P1");
            poolId = lot.Uid;
            materialId = lot.WorkOrderMaterialId!.Value;
            routeId = (await setup.ProductionWorkOrderOperations.SingleAsync(o => o.Uid == graph.Process1Id)).RouteStepId!.Value;
            lot.Qty = 0m;
            lot.BaseQty = 0m;
            lot.TotalCost = 0m;
            lot.AverageUnitCost = 0m;
            var pool = await setup.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == poolId);
            pool.TrackedBaseQty = 0m;
            pool.TrackedValue = 0m;
            await setup.SaveChangesAsync();
        }
        var request = Guid.NewGuid();
        var command = new StockPostingCommand
        {
            RequestId = request, CommandType = "TRACE_FIXTURE_SEED", SourceModule = "TEST",
            SourceDocumentType = "SEED", SourceDocumentId = request.ToString(), SourceDocumentNo = "TRACE-SEED",
            DocumentRevision = 1, EffectiveAt = new DateTime(2026, 9, 30),
            Evidence = StockPostingFingerprint.Create(new { poolId, quantity = 500m, value = 500m }, new { })
        };
        var result = await ProductionLedgerTestFixture.CreateCoordinator(_factory).ExecuteAsync(command, async (context, ct) =>
        {
            var db = context.Db;
            var lot = await db.ProductionBalLots.SingleAsync(l => l.Uid == poolId, ct);
            var link = new ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderId = graph.OrderId,
                CommandType = "TEST", PostingRequestId = request.ToString("N"), CreatedBy = "TEST",
                CreatedDate = command.EffectiveAt
            };
            db.ProductionPostingLinks.Add(link);
            await db.SaveChangesAsync(ct);
            var movements = await new ProductionStockWriter(new StockMovementRegistry()).ApplyAsync(context,
                [new(lot, ProductionBalLotMovementTypes.Produce, 500m, 500m, graph.OrderId,
                    materialId, graph.Process1Id, routeId, link.Uid, "SEED", "TRACE-SEED", "1", 0,
                    ExactTotalValue: 500m, ValuationStatus: ProductionPoolValuationService.Verified)], ct);
            await db.SaveChangesAsync(ct);
            var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == poolId, ct);
            pool.Status = ProductionPoolValuationService.Verified;
            pool.Generation = 2;
            pool.TrackedBaseQty = lot.BaseQty;
            pool.TrackedValue = lot.TotalCost;
            db.ProductionValuationEvidenceRows.Add(new()
            {
                MovementId = movements[0].Uid, ProductionBalLotId = poolId, Generation = pool.Generation,
                Status = ProductionPoolValuationService.Verified, Basis = "TEST_EXPLICIT"
            });
            return true;
        });
        Assert.True(result.Succeeded, result.Error?.Message);
    }

    private static async Task ConfigureFinishedGoodRouteAsync(AppDbContext db, long operationId)
    {
        var operation = await db.ProductionWorkOrderOperations.SingleAsync(o => o.Uid == operationId);
        var route = await db.ProductionWorkOrderRouteSteps.SingleAsync(r => r.Uid == operation.RouteStepId);
        route.OutputType = PrRouteOutputTypes.FinishedGoods;
        route.OutputItemCode = "FG001";
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "FG001", StdUom = "EA",
            IsActive = true, StockControl = true, RowVersion = [1]
        });
    }

    private sealed record PostedFinishedGood(
        ProductionFinishedGoodReceiptService Service, FinishedGoodReceiptDocument Document, decimal Value);

    private async Task AssertActiveFinishedGoodUnchangedAsync(PostedFinishedGood finished)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var document = await db.ProductionFinishedGoodReceiptRows.Include(r => r.Batch)
            .SingleAsync(r => r.BatchId == finished.Document.Id);
        Assert.Equal(IvBatchStatuses.Posted, document.Batch.BatchStatus);
        var fact = await db.ProductionFinishedGoodFactRows.SingleAsync(f => f.BatchId == document.BatchId
            && f.StockPostingId == document.PostingId && f.ReversesFactId == null);
        Assert.Equal(finished.Value, fact.TotalValue);
        var outbound = await db.ProductionBalLotMovements.SingleAsync(m => m.Uid == fact.ProductionMovementId);
        var produced = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionBalLotId == outbound.ProductionBalLotId
            && m.MovementType == ProductionBalLotMovementTypes.Produce && m.OriginalMovementId == null);
        var lot = await db.ProductionBalLots.SingleAsync(l => l.Uid == outbound.ProductionBalLotId);
        var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == lot.Uid);
        Assert.Equal(produced.BaseQty - outbound.BaseQty, lot.BaseQty);
        Assert.Equal(produced.TotalCost - outbound.TotalCost, lot.TotalCost);
        Assert.Equal(lot.BaseQty, pool.TrackedBaseQty);
        Assert.Equal(lot.TotalCost, pool.TrackedValue);
        var finishedStock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
        Assert.Equal(2m, finishedStock.OnHandBaseQty);
        Assert.Equal(finished.Value, finishedStock.InventoryValue);
    }

    private static async Task RollbackFinishedGoodAsync(PostedFinishedGood finished)
    {
        var reversed = await finished.Service.RollbackAsync(new(finished.Document.Id,
            finished.Document.RowVersion, Guid.NewGuid(), "Reverse finished goods before the source receipt"));
        Assert.True(reversed.Succeeded, reversed.Message);
    }

    private async Task DamageFinishedGoodReversalAsync(int receiptId, bool damageValuation)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var original = await db.ProductionFinishedGoodFactRows.SingleAsync(f =>
            f.BatchId == receiptId && f.ReversesFactId == null);
        var reversed = await db.ProductionFinishedGoodFactRows.SingleAsync(f =>
            f.ReversesFactId == original.Id);
        var changed = damageValuation
            ? await db.StockValuationFacts.Where(v => v.InventoryHistoryId == reversed.InventoryHistoryId
                    && v.ReversesValuationFactId != null)
                .ExecuteUpdateAsync(update => update.SetProperty(v => v.Direction, 1))
            : await db.ProductionPoolDependencyRows.Where(e =>
                    e.ConsumerMovementId == reversed.ProductionMovementId && e.ReversesDependencyId != null)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.StockPostingId, original.StockPostingId));
        Assert.Equal(1, changed);
    }

    private async Task DamageActiveFinishedGoodAsync(int receiptId, int damageKind)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var fact = await db.ProductionFinishedGoodFactRows.SingleAsync(f =>
            f.BatchId == receiptId && f.ReversesFactId == null);
        var edge = await db.ProductionPoolDependencyRows.SingleAsync(e =>
            e.ConsumerMovementId == fact.ProductionMovementId && e.ReversesDependencyId == null);
        var producer = await db.ProductionBalLotMovements.SingleAsync(m => m.Uid == edge.ContributorMovementId);
        var otherOperation = damageKind == 1
            ? await db.ProductionWorkOrderOperations.Where(o => o.WorkOrderId == producer.WorkOrderId
                && o.Uid != producer.WorkOrderOperationId).Select(o => o.Uid).FirstAsync()
            : 0L;
        var changed = damageKind == 1
            ? await db.ProductionBalLots.Where(l => l.Uid == producer.ProductionBalLotId)
                .ExecuteUpdateAsync(update => update.SetProperty(l => l.WorkOrderOperationId, (long?)otherOperation))
            : damageKind == 2
                ? await db.StockValuationFacts.Where(v => v.InventoryHistoryId == fact.InventoryHistoryId)
                    .ExecuteUpdateAsync(update => update.SetProperty(v => v.CostMethod,
                        v => v.CostMethod == StockCostMethods.Fifo ? StockCostMethods.MovingAverage : StockCostMethods.Fifo))
            : await db.ProductionPoolDependencyRows.Where(e => e.Id == edge.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(e => e.StockPostingId, producer.StockPostingId!.Value));
        Assert.Equal(1, changed);
    }

    private async Task<PostedFinishedGood> PostFinishedGoodAsync(long orderId, long outputId, DateTime businessDate)
    {
        long poolId;
        await using (var db = await _factory.CreateDbContextAsync())
            poolId = await db.ProductionBalLotMovements.Where(m => m.ProductionOutputId == outputId
                    && m.MovementType == ProductionBalLotMovementTypes.Produce)
                .Select(m => m.ProductionBalLotId).SingleAsync();

        var service = new ProductionFinishedGoodReceiptService(
            _factory, InventoryTenantTestHelper.CreateTenantContext(), Access(),
            new FixedCurrentDateService(businessDate, businessDate.AddHours(16)), new TestRunningNumberService(),
            new BranchStockTransactionLock(), ProductionLedgerTestFixture.CreateCoordinator(_factory),
            new IvStockPostingRepository(), new ProductionStockWriter(new StockMovementRegistry()),
            new IvInventoryHistoryWriter());
        var saved = await service.SaveAsync(new FinishedGoodReceiptSaveRequest
        {
            WorkOrderId = orderId, EffectiveDate = businessDate.AddHours(15),
            Lines = [new() { ProductionBalLotId = poolId, Quantity = 2m, Warehouse = "WH01" }]
        });
        Assert.True(saved.Succeeded, saved.Message);
        await using (var db = await _factory.CreateDbContextAsync())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE PrFinishedGoodReceipt SET RowVersion = X'01' WHERE BatchId = {saved.Data!.Id};");
        var draft = await service.GetAsync(saved.Data!.Id);
        Assert.True(draft.Succeeded, draft.Message);
        var posted = await service.PostAsync(new(draft.Data!.Id, draft.Data.RowVersion, Guid.NewGuid()));
        Assert.True(posted.Succeeded, posted.Message);
        return new(service, posted.Data!, Assert.Single(posted.Data!.Lines).TotalValue!.Value);
    }

    private ProductionMaterialIssueService CreateIssueService(
        IIvInventoryPostingService? inventoryPosting = null, DateTime? businessDate = null)
    {
        var access = Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var date = businessDate ?? new DateTime(2026, 10, 1);
        var allocation = new ProductionMaterialAllocationService(
            _factory, tenant, access, new FixedCurrentDateService(date));
        return new ProductionMaterialIssueService(
            _factory, tenant, access, new FixedCurrentDateService(date),
            allocation, new RunningNumberService(), new IvStockPostingRepository(),
            inventoryPosting ?? ProductionLedgerTestFixture.CreateInventoryPosting(_factory));
    }

    private ProductionStockReturnService CreateReturnService(bool canPost = true, bool canRollback = true)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        return new ProductionStockReturnService(
            _factory,
            tenant,
            Access(canPost, canRollback),
            new FixedCurrentDateService(new DateTime(2026, 10, 3, 12, 0, 0)),
            new TestRunningNumberService(),
            new BranchStockTransactionLock(),
            ProductionLedgerTestFixture.CreateCoordinator(_factory),
            new IvStockPostingRepository(),
            new ProductionStockWriter(new StockMovementRegistry()),
            new IvInventoryHistoryWriter(),
            ProductionLedgerTestFixture.CreateInventoryPosting(_factory));
    }

    private async Task<ProductionStockReturnDocument> SaveReturnAsync(
        ProductionStockReturnService service, long workOrderId, long poolId,
        decimal quantity = 1m)
    {
        var saved = await service.SaveAsync(new ProductionStockReturnSaveRequest
        {
            WorkOrderId = workOrderId,
            SnapshotRevision = 1,
            SnapshotHash = new string('A', 64),
            EffectiveDate = new DateTime(2026, 10, 2, 8, 0, 0),
            Lines = [new() { ProductionBalLotId = poolId, Quantity = quantity, Reason = ProductionStockReturnReasons.Excess }],
        });
        Assert.True(saved.Succeeded, saved.Message);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE PrProductionStockReturn SET RowVersion = X'01' WHERE BatchId = {saved.Data!.Id};");
        var loaded = await service.GetAsync(saved.Data.Id);
        Assert.True(loaded.Succeeded, loaded.Message);
        return loaded.Data!;
    }

    private async Task<ProductionStockReturnDocument> LoadReturnDraftAsync(ProductionStockReturnService service, int id)
    {
        var loaded = await service.GetAsync(id);
        Assert.True(loaded.Succeeded, loaded.Message);
        return loaded.Data!;
    }

    private static IAccessRightService Access(bool canPost = true, bool canRollback = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string permission, CancellationToken _) => Task.FromResult(
                (permission != PermissionCodes.Post || canPost)
                && (permission != PermissionCodes.Rollback || canRollback)));
        return access.Object;
    }

    private static ProductionOutputCreateRequest OutputRequest(long operationId, decimal good, DateTime date) => new()
    {
        WorkOrderOperationId = operationId,
        CreateRequestId = Guid.NewGuid().ToString("N"),
        ProductionDate = date,
        GoodQty = good,
        OutputLotNo = "LOT-1",
    };

    private async Task<ProductionMaterialIssueSaveRequest> CreateIssueSaveAsync(GraphIds graph, int balanceId, decimal issueQty) =>
        await CreateIssueSaveAsync(graph.WorkOrderNo, graph.Process2Id, graph.MaterialId, balanceId, issueQty);

    private async Task<ProductionMaterialIssueSaveRequest> CreateIssueSaveAsync(
        string workOrderNo, long operationId, long materialId, int balanceId, decimal issueQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.WorkOrderNo == workOrderNo);
        return new ProductionMaterialIssueSaveRequest
        {
            WorkOrderNo = workOrderNo,
            WorkOrderOperationId = operationId,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            ProductionQtyThisIssue = 1m,
            TrxDateTime = new DateTime(2026, 10, 1),
            Lines =
            [
                new ProductionMaterialIssueLineRequest
                {
                    WorkOrderMaterialId = materialId,
                    IssueQty = issueQty,
                    Allocations =
                    [
                        new ProductionMaterialIssueAllocationRequest
                        {
                            FromBalLocId = balanceId,
                            BaseQty = issueQty,
                        }
                    ]
                }
            ]
        };
    }

    private async Task SeedBalanceAsync(int id, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            Id = id,
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "RM001",
            WhCode = "WH01",
            LocCode = "BIN-A",
            LotNo = "",
            IStatus = IvItemStatuses.Active,
            StdQty = qty,
            StdUom = "EA",
            TransDate = new DateTime(2026, 9, 1),
            UnitPrice = 2m,
            PriceEvidence = ProductionLedgerTestFixture.TestPriceEvidence,
            RowVersion = [1],
        });
        db.StockCostStates.Add(new StockCostState
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ItemCode = "RM001",
            CostMethod = StockCostMethods.MovingAverage,
            OnHandBaseQty = qty,
            InventoryValue = qty * 2m,
            AverageUnitCost = 2m,
            CurrentUnitCost = 2m,
            RowVersion = [1],
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "WH01",
            LocCode = "BIN-A",
            IsActive = true,
            RowVersion = [1],
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = "DEMO",
            IStatus = IvItemStatuses.Active,
            IsActive = true,
            RowVersion = [1],
        });
        await db.SaveChangesAsync();
    }

    private async Task<GraphIds> SeedTwoProcessWithMaterialAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        EnsureStock(db);
        var order = Order(workOrderNo, plannedQty: 100m);
        var route = Route(order, 10, "WC10", PrRouteOutputTypes.WipStocked);
        var first = Operation(order, route, "P1", 10, planned: 100m, isFinal: false);
        var later = Operation(order, route, "P2", 20, planned: 100m, isFinal: true);
        var material = Material(order, later, requiredQty: 100m);
        route.Operations.Add(first);
        route.Operations.Add(later);
        order.RouteSteps.Add(route);
        order.Operations.Add(first);
        order.Operations.Add(later);
        db.ProductionWorkOrderMaterials.Add(material);
        var p1Material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order,
            WorkOrderOperation = first,
            LineNo = 1,
            ComponentCode = "RM-P1",
            IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased,
            RequiredQty = 100m,
            RequiredUom = "EA",
            RequiredBaseQty = 100m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            WarehouseCode = "WH01",
            LocationCode = "BIN-A",
            RowVersion = [1],
        };
        db.ProductionWorkOrderMaterials.Add(p1Material);
        await db.SaveChangesAsync();
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = ProductionBalLotKinds.MaterialIn,
            ItemCode = "RM-P1",
            Qty = 500m,
            Uom = "EA",
            BaseQty = 500m,
            BaseUom = "EA",
            ConversionFactorToBase = 1m,
            TotalCost = 500m,
            AverageUnitCost = 1m,
            WorkOrderId = order.Uid,
            WorkOrderNo = workOrderNo,
            WorkOrderMaterialId = p1Material.Uid,
            WarehouseCode = "WH01",
            LocationCode = "BIN-A",
            LotNo = "P1-LOT",
            LastMovementDate = new DateTime(2026, 9, 30),
            RowVersion = [1],
        });
        await db.SaveChangesAsync();
        var p1Lot = await db.ProductionBalLots.SingleAsync(x => x.WorkOrderMaterialId == p1Material.Uid);
        db.ProductionPoolValuationRows.Add(new ProductionPoolValuation
        {
            ProductionBalLotId = p1Lot.Uid,
            Status = ProductionPoolValuationService.Verified,
            TrackedBaseQty = p1Lot.BaseQty,
            TrackedValue = p1Lot.TotalCost,
        });
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new GraphIds(workOrderNo, order.Uid, first.Uid, later.Uid, material.Uid);
    }

    private async Task<StageGraphIds> SeedTwoStageWithLaterMaterialAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        EnsureStock(db);
        var order = Order(workOrderNo, plannedQty: 10m);
        var firstRoute = Route(order, 10, "WC-A", PrRouteOutputTypes.WipNonstock);
        var laterRoute = Route(order, 20, "WC-B", PrRouteOutputTypes.WipNonstock);
        var first = Operation(order, firstRoute, "P1", 10, planned: 10m, isFinal: true);
        var later = Operation(order, laterRoute, "P2", 10, planned: 10m, isFinal: true);
        var material = Material(order, later, requiredQty: 10m);
        firstRoute.Operations.Add(first);
        laterRoute.Operations.Add(later);
        order.RouteSteps.Add(firstRoute);
        order.RouteSteps.Add(laterRoute);
        order.Operations.Add(first);
        order.Operations.Add(later);
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        return new StageGraphIds(workOrderNo, first.Uid, later.Uid, material.Uid);
    }

    private static void EnsureStock(AppDbContext db)
    {
        if (db.IvStockMasters.Any())
            return;
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "RM001",
            IDesc = "Raw material",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            RowVersion = [1],
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "WH01",
            IsActive = true,
            RowVersion = [1],
        });
    }

    private static ProductionWorkOrder Order(string workOrderNo, decimal plannedQty) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        WorkOrderNo = workOrderNo,
        ProductCode = "FG001",
        OutputUom = "EA",
        Status = ProductionWorkOrderStatuses.Released,
        PlannedQty = plannedQty,
        RemainingQty = plannedQty,
        SnapshotRevision = 1,
        SnapshotHash = new string('A', 64),
        SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
        SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
        IsLegacySnapshot = false,
        DefinitionEffectiveDate = new DateTime(2026, 10, 1),
        PlannedStartDateTime = new DateTime(2026, 10, 1),
        PlannedCompletionDateTime = new DateTime(2026, 10, 2),
        RowVersion = [1],
    };

    private static ProductionWorkOrderRouteStep Route(
        ProductionWorkOrder order, int stage, string workCentre, string outputType) => new()
    {
        WorkOrder = order,
        StageSequence = stage,
        WorkCentreCode = workCentre,
        OutputItemCode = "WIP-OUT",
        OutputType = outputType,
        YieldPercent = 100m,
        OutputUom = "EA",
        OutputBaseUom = "EA",
        OutputConversionFactorToBase = 1m,
        RowVersion = [1],
    };

    private static ProductionWorkOrderOperation Operation(
        ProductionWorkOrder order,
        ProductionWorkOrderRouteStep route,
        string code,
        int processSequence,
        decimal planned,
        bool isFinal) => new()
    {
        WorkOrder = order,
        RouteStep = route,
        OperationCode = code,
        WorkCentreCode = route.WorkCentreCode,
        ProcessSequence = processSequence,
        ProcessType = "MANUAL",
        PlannedOutputQty = planned,
        PlannedInputUom = "EA",
        PlannedOutputUom = "EA",
        IsFinalOperation = isFinal,
        RemainingQty = planned,
        RowVersion = [1],
    };

    private static ProductionWorkOrderMaterial Material(
        ProductionWorkOrder order, ProductionWorkOrderOperation operation, decimal requiredQty) => new()
    {
        WorkOrder = order,
        WorkOrderOperation = operation,
        LineNo = 2,
        ComponentCode = "RM001",
        IssueMethod = PrMaterialIssueMethods.Manual,
        SupplySource = PrMaterialSupplySources.Purchased,
        RequiredQty = requiredQty,
        RequiredUom = "EA",
        RequiredBaseQty = requiredQty,
        BaseUom = "EA",
        ConversionFactorToBase = 1m,
        WarehouseCode = "WH01",
        LocationCode = "BIN-A",
        RowVersion = [1],
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private readonly record struct GraphIds(
        string WorkOrderNo, long OrderId, long Process1Id, long Process2Id, long MaterialId);

    private readonly record struct StageGraphIds(
        string WorkOrderNo, long FirstOperationId, long LaterOperationId, long MaterialId);

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        private int _next = 900_000;
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(_next + 1);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(Interlocked.Increment(ref _next));
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        private static string NormalizeSql(string sql) => sql
            .Replace("N'", "'", StringComparison.Ordinal)
            // SQL Server compares decimal(19,6) exactly. SQLite evaluates the
            // subtraction as floating point, so round its test constraint to
            // the same six-decimal precision instead of rejecting valid deltas.
            .Replace("[DeltaCost] = [CorrectedEffectiveTotalCost] - [PriorEffectiveTotalCost]",
                "ROUND([DeltaCost], 6) = ROUND([CorrectedEffectiveTotalCost] - [PriorEffectiveTotalCost], 6)",
                StringComparison.Ordinal);

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            command.CommandText = NormalizeSql(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = NormalizeSql(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
