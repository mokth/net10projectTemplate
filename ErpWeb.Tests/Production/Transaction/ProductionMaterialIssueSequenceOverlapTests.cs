using System.Data.Common;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Quality;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using ErpWeb.Tests.Inventory.Transaction;
using ErpWeb.Tests.Procurement.Transaction;
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
    private readonly FifoReceiptPostingBarrier? _fifoPostingBarrier;
    private readonly WacReceiptPostingBarrier? _wacPostingBarrier;

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

    private ProductionMaterialIssueSequenceOverlapTests(IDbContextFactory<AppDbContext> factory,
        FifoReceiptPostingBarrier? fifoPostingBarrier = null, WacReceiptPostingBarrier? wacPostingBarrier = null)
    {
        _factory = factory;
        _fifoPostingBarrier = fifoPostingBarrier;
        _wacPostingBarrier = wacPostingBarrier;
    }

    internal static async Task VerifySqlServerWacReceiptEditRaceAsync(
        IDbContextFactory<AppDbContext> factory, WacReceiptPostingBarrier barrier, bool goodsReceipt)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory, wacPostingBarrier: barrier);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, receiptFirst: true, 181, standard: false);
    }

    internal static async Task VerifySqlServerNeutralIssueReceiptAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool concurrent = false)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, concurrent ? 99 : 92, false);
    }

    internal static async Task VerifySqlServerSameDayIssueAsync(
        IDbContextFactory<AppDbContext> factory, bool replayEnabled)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(false, true, replayEnabled ? 104 : 105, false);
    }

    internal static async Task VerifySqlServerReversedIssueReceiptRollbackAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool costChanged, bool replayEnabled)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        var scenario = costChanged ? replayEnabled ? 111 : 112 : replayEnabled ? 109 : 110;
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, scenario, false);
    }

    internal static async Task VerifySqlServerReplayedIssueDetailAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool damage)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, damage ? 116 : 115, false);
    }

    internal static async Task VerifySqlServerCrossPileReversedIssuePostAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool replayEnabled)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 125 : 124, false);
    }

    internal static async Task VerifySqlServerReversedIssueCostAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool lowerCost,
        bool concurrent = false, bool injectFailure = false)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true,
            injectFailure ? 138 : concurrent ? 137 : lowerCost ? 130 : 129, false);
    }

    internal static async Task VerifySqlServerReversedIssueAndOutputAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool lowerCost,
        bool concurrent = false, bool injectFailure = false, bool earlier = false, bool edited = false,
        bool quantityEdited = false, bool partial = false)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true,
            partial ? injectFailure ? 178 : concurrent ? 179 : edited ? 180 : earlier ? lowerCost ? 177 : 176 : lowerCost ? 175 : 174
                : earlier ? injectFailure ? 168 : concurrent ? 169 : quantityEdited ? 173 : edited ? 167 : lowerCost ? 166 : 165
                : injectFailure ? 159 : concurrent ? 160 : lowerCost ? 158 : 157, false);
    }

    internal static async Task VerifySqlServerFifoReceiptReversalGuardAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool replayEnabled, bool safe,
        bool neutral = false, bool concurrent = false, bool injectFailure = false, bool qa = false,
        bool edited = false, bool rounding = false, bool projectionInsertion = false, int sameTimeCase = 0,
        FifoReceiptPostingBarrier? postingBarrier = null)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory, postingBarrier);
        await fixture.BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true,
            sameTimeCase is 1 ? 72 : sameTimeCase is 2 ? 73 : sameTimeCase is 3 ? 75 : sameTimeCase is 4 ? 77 :
                sameTimeCase is 5 ? 78 : sameTimeCase is 6 ? 80 : sameTimeCase is 7 ? 79 : sameTimeCase is 8 ? 82 : sameTimeCase is 9 ? 83 :
                sameTimeCase is 10 ? 84 :
            neutral ? injectFailure ? 63 : concurrent ? 62 : qa ? 60 : edited ? 64 : rounding ? 67 : replayEnabled ? 54 : 55
                : projectionInsertion ? 46 : safe ? 49 : replayEnabled ? 51 : 52);
    }

    internal static async Task VerifySqlServerEarlierReceiptReversedIssueAsync(
        IDbContextFactory<AppDbContext> factory, bool goodsReceipt, bool lowerCost,
        bool concurrent = false, bool injectFailure = false, bool edited = false, bool editedFailure = false,
        bool neutralEdited = false, bool quantityEdited = false, bool quantityRounding = false)
    {
        await using var fixture = new ProductionMaterialIssueSequenceOverlapTests(factory);
        await fixture.BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true,
            quantityRounding ? 156 : quantityEdited ? 153
                : editedFailure ? 152 : neutralEdited ? goodsReceipt ? 155 : 154 : edited ? goodsReceipt ? 151 : 149
                : injectFailure ? 147 : concurrent ? 146 : lowerCost ? 142 : 141, false);
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
    public Task Backdated_wac_receipt_can_replay_after_finished_good_reversal_when_cost_is_unchanged(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 28 : 29, false);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Backdated_wac_receipt_protects_cost_when_finished_goods_may_be_reversed(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 67 : 68, false);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public Task Backdated_wac_receipt_replays_changed_cost_after_output_reversal_without_finished_goods(
        bool goodsReceipt, bool lowerCost, bool partialOutput) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true,
            partialOutput ? (lowerCost ? 79 : 78) : (lowerCost ? 75 : 69), false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_after_reversed_output_rejects_damage_or_failure(
        bool goodsReceipt, bool injectedFailure) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, injectedFailure ? 77 : 76, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_replays_output_rolled_back_in_a_later_open_month(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 80, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_rollback_rejects_damaged_reversed_output_correction(
        bool goodsReceipt, bool damageMovement) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, damageMovement ? 82 : 81, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_corrected_issue_rollback_requires_source_first_after_reversed_output(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 83, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_refuses_damaged_reversed_issue_and_output(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 84, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_reprices_reversed_issue_and_output_and_rolls_back(
        bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 158 : 157, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_and_output_correction_failure_rolls_back_all_changes(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 159, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_and_output_refuses_damaged_correction_on_receipt_rollback(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 161, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_and_output_receipt_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 162, false);

    [Fact]
    public Task Reversed_issue_and_output_goods_receipt_rollback_reverses_qa_allocation() =>
        BackdatedReceiptAndProductionIssueAsync(true, true, 163, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Earlier_receipt_rolls_back_and_reposts_after_issue_and_output_reversal(
        bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 166 : 165, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_price_edits_repost_after_issue_and_output_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 167, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_output_correction_failure_rolls_back_all_changes(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 168, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_with_reversed_output_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 170, false);

    [Fact]
    public Task Earlier_receipt_with_reversed_output_refuses_closed_period() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 171, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_with_reversed_output_refuses_damaged_correction(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 172, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_quantity_edits_repost_after_issue_and_output_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 173, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_receipt_replays_partially_consumed_reversed_issue_and_output(bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 175 : 174, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Earlier_receipt_replays_partially_consumed_reversed_issue_and_output(bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 177 : 176, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Partial_output_correction_failure_rolls_back_all_changes(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 178, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_price_edits_replay_partially_consumed_reversed_issue_and_output(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 180, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_reprices_already_reversed_issue_without_output(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 85, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_receipt_reprices_reversed_issue_and_rolls_back(
        bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 130 : 129, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Reversed_issue_receipt_rollback_is_atomic_on_damage_or_write_failure(
        bool goodsReceipt, bool injectFailure) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, injectFailure ? 132 : 131, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_receipt_rollback_respects_qa_stock_holds(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 133, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Reversed_issue_receipt_rejects_damaged_repost_or_failed_post_atomically(
        bool goodsReceipt, bool injectFailure) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, injectFailure ? 135 : 134, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_same_day_receipt_reprices_reversed_issue_and_rolls_back(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 136, false);

    [Fact]
    public Task Reversed_issue_goods_receipt_respects_qa_approval_and_restores_allocation() =>
        BackdatedReceiptAndProductionIssueAsync(true, true, 139, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_receipt_post_and_rollback_require_open_period(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 140, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Receipt_posted_before_reversed_issue_rolls_back_and_reposts_with_correct_costs(
        bool goodsReceipt, bool lowerCost) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, lowerCost ? 142 : 141, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Earlier_receipt_replay_rejects_damaged_history_and_write_failure(bool goodsReceipt, bool injectFailure) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, injectFailure ? 144 : 143, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_replay_respects_qa_stock_hold(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 145, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_replay_requires_open_month_for_rollback_and_repost(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 148, false);

    [Fact]
    public Task Earlier_receipt_replay_allows_approved_changed_price_on_repost() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 149, false);

    [Fact]
    public Task Earlier_goods_receipt_replay_allows_changed_po_price_on_repost() =>
        BackdatedReceiptAndProductionIssueAsync(true, true, 151, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_reposts_at_cost_neutral_price_and_can_be_changed_again(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, goodsReceipt ? 155 : 154, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_replay_allows_changed_quantity_and_price(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 153, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_replay_rejects_unreconciled_quantity_rounding(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 156, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Earlier_receipt_replay_rejects_damaged_repost_history_on_next_rollback(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 150, false);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Reversed_issue_receipt_projection_checks_both_costs_and_replays_when_valid(
        bool goodsReceipt, bool lowerCost, bool damaged) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true,
            damaged ? 121 : lowerCost ? 120 : 119, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Reversed_issue_receipt_with_rounded_equal_issue_cost_remains_blocked(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 122, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Receipt_post_refuses_unreplayed_reversed_issue_on_another_pile(bool goodsReceipt, bool replayEnabled) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 125 : 124, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Receipt_post_with_replay_disabled_refuses_a_later_active_issue(
        bool goodsReceipt, bool sameDay) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, sameDay ? 127 : 126, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_standard_receipt_allows_reversed_issue_with_unchanged_cost(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 85, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_standard_receipt_refuses_damaged_reversed_issue(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 91, true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Later_wac_receipt_allows_reversed_production_issue(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 87, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wac_cost_neutral_receipt_can_roll_back_after_issue_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 88, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_cost_neutral_receipt_posts_and_rolls_back_after_issue_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 92, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task As_of_stock_keeps_the_issue_until_its_later_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 100, false);

    [Fact]
    public Task Backdated_issue_cannot_use_stock_restored_by_a_later_production_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 101, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_issue_before_a_later_receipt_cannot_use_a_future_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 102, false);

    [Fact]
    public Task Backdated_production_draft_cannot_allocate_stock_from_a_later_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 103, false);

    [Fact]
    public Task Same_day_issue_cannot_use_stock_from_a_later_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 104, false);

    [Fact]
    public Task Same_day_issue_cannot_use_a_future_reversal_when_replay_is_disabled() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 105, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Same_day_transfer_cannot_use_stock_from_a_later_reversal(bool replayEnabled) =>
        BackdatedReceiptAndProductionIssueAsync(false, true, replayEnabled ? 108 : 106, false);

    [Fact]
    public Task Same_day_adjustment_cannot_use_stock_from_a_later_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 107, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task Receipt_rollback_rejects_negative_stock_during_a_later_reversed_issue(bool goodsReceipt, bool replayEnabled) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 109 : 110, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task Receipt_rollback_handles_changed_cost_of_a_later_reversed_issue(bool goodsReceipt, bool replayEnabled) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 111 : 112, false);

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task Receipt_rollback_rejects_changed_reversed_issue_cost_on_another_pile(bool goodsReceipt, bool replayEnabled) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 113 : 114, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Production_issue_detail_shows_replayed_cost_and_restores_it_on_receipt_rollback(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 115, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Production_issue_detail_shows_replayed_fifo_splits_and_restores_cost(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 51);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Production_issue_detail_refuses_damaged_replayed_cost_without_leaking_it(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 116, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Production_issue_detail_refuses_mismatched_inventory_and_production_cost(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 117, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Production_issue_detail_refuses_missing_corrected_movement_link(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 118, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_cost_neutral_receipt_refuses_damaged_issue_reversal(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 93, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wac_cost_neutral_reversed_issue_receipt_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 94, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wac_cost_neutral_reversed_issue_receipt_refuses_additional_stock_history(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 95, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wac_cost_neutral_reversed_issue_receipt_refuses_another_stock_pile(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 96, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Wac_cost_neutral_reversed_issue_receipt_rollback_rejects_negative_history(bool goodsReceipt) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 97, false);

    [Fact]
    public Task Wac_cost_neutral_reversed_issue_goods_receipt_respects_incoming_qa() =>
        BackdatedReceiptAndProductionIssueAsync(true, true, 98, false);

    [Fact]
    public Task Wac_earlier_posted_receipt_can_roll_back_after_normal_issue_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 89, false);

    [Fact]
    public Task Wac_earlier_receipt_refuses_damaged_issue_reversal() =>
        BackdatedReceiptAndProductionIssueAsync(false, true, 90, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_receipt_allows_unchanged_cost_after_output_reversal_without_finished_goods(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 70, false);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_wac_and_standard_receipt_rollback_respects_qa_stock_holds(
        bool goodsReceipt, bool standard) =>
        BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 72, standard);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_corrected_finished_good_rollback_respects_qa_stock_holds(
        bool goodsReceipt) => BackdatedReceiptAndProductionIssueAsync(goodsReceipt, true, 74, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_wac_and_standard_goods_receipt_respects_qa_gate_and_restores_allocation(
        bool standard) => BackdatedReceiptAndProductionIssueAsync(true, true, 73, standard);

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
        var businessDate = consumeAt is 104 or 105 or 106 or 107 or 108 ? DateTime.Now.Date.AddDays(-1)
            : new DateTime(2026, 10, standard || consumeAt >= 20 ? 3 : 1);
        var receiptPrice = consumeAt == 122 ? 2.0001m
            : consumeAt is 53 or 54 or 56 or 59 or 64 or 75 or 79 or 120 or 130 or 142 or 158 or 166 or 175 or 177 ? 1m
            : consumeAt is 5 or 28 or 29 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 70 or 88 or 92 or 93 or 94 or 95 or 96 or 97 or 98 or 99 or 100 or 102 or 109 or 110 ? 2m : 10m;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            db.MsUoms.Add(new() { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
            db.IvClasses.Add(new() { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
            (await db.IvStockMasters.SingleAsync(s => s.ICode == "RM001")).IClassCode = "RAW";
            if (consumeAt is 113 or 114 or 124 or 125 or 126)
            {
                (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty = 10m;
                db.IvLocations.Add(new IvLocation
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH01",
                    LocCode = "BIN-B", IsActive = true
                });
                db.IvBalLocs.Add(new IvBalLoc
                {
                    Id = 59, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM001",
                    WhCode = "WH01", LocCode = "BIN-B", LotNo = "", IStatus = IvItemStatuses.Active,
                    StdQty = 10m, StdUom = "EA", TransDate = new DateTime(2026, 9, 1),
                    UnitPrice = 2m, Cost = 2m, PriceEvidence = ProductionLedgerTestFixture.TestPriceEvidence,
                    RowVersion = [1]
                });
            }
            if (consumeAt is 73 or 98 or 139)
                db.QaPolicies.Add(new QaPolicy
                {
                    CompanyCode = "DEMO", Version = 1, Enabled = true, IsPublished = true,
                    IncomingInspectionEnabled = true, CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
                });
            if (consumeAt is 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 63 or 64 or 65 or 66 or 67 or 68 or 74)
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
            var explicitBalance = db.Database.IsSqlServer()
                && consumeAt is 113 or 114 or 124 or 125 or 126;
            if (explicitBalance)
            {
                await db.Database.OpenConnectionAsync();
                await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT dbo.IvBalLoc ON");
            }
            try
            {
                await db.SaveChangesAsync();
            }
            finally
            {
                if (explicitBalance)
                    await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT dbo.IvBalLoc OFF");
            }
            if (standard)
                await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "RM001", 2m);
        }

        IvMiscReceiptService? earlierReceipt = null;
        IvGoodsReceiptService? earlierGoods = null;
        var earlierReceiptBatch = 0;
        if (consumeAt is 89 or 90 or 109 or 110 or 111 or 112 or 113 or 114 or >= 141 and <= 156 or >= 165 and <= 173 or 176 or 177 or 180)
        {
            var earlierQaPhysical = consumeAt is 145 or 170 ? new QaPhysicalHoldAuthorizationService() : null;
            if (goodsReceipt)
            {
                var tenant = InventoryTenantTestHelper.CreateTenantContext();
                earlierGoods = new IvGoodsReceiptService(_factory, tenant, Access(), new RunningNumberService(),
                    new FixedCurrentDateService(businessDate), new IvStockMasterRepository(_factory),
                    new IvStockCommonRepository(_factory), new IvStockTransactionRepository(),
                    new IvStockPostingRepository(), InventoryLedgerTestFixture.CreateInventoryPosting(
                        _factory, enableBackdatedReplay: consumeAt is not (110 or 112 or 114), qaPhysical: earlierQaPhysical),
                    NullLogger<IvGoodsReceiptService>.Instance, approval: TestDisabledProcurementApprovalGuard.Instance);
                var saved = await earlierGoods.SaveNewAsync(new IvGoodsReceiptSaveRequest
                {
                    TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 2),
                    Lines = [new IvGoodsReceiptLineRequest
                    {
                        PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                        ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m
                    }]
                });
                Assert.True(saved.Succeeded, saved.ErrorMessage);
                earlierReceiptBatch = saved.BatchNo;
                var postedEarlier = await earlierGoods.PostAsync([earlierReceiptBatch]);
                Assert.True(postedEarlier.Succeeded, postedEarlier.ErrorMessage);
            }
            else
            {
                earlierReceipt = InventoryLedgerTestFixture.CreateMiscReceipt(
                    _factory, businessDate, enableBackdatedReplay: consumeAt is not (110 or 112 or 114), qaPhysical: earlierQaPhysical);
                var saved = await earlierReceipt.SaveNewAsync(new IvMiscReceiptSaveRequest
                {
                    TrxDate = new DateTime(2026, 10, 2),
                    Lines = [new IvMiscReceiptLineRequest
                    {
                        ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                        Quantity = 5m, UnitPrice = consumeAt is >= 141 and <= 156 or >= 165 and <= 173 or 176 or 177 or 180 ? receiptPrice : consumeAt is 111 or 112 or 113 or 114 ? 10m : 2m, PriceConfirmed = true,
                        Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                    }]
                });
                Assert.True(saved.Succeeded, saved.ErrorMessage);
                earlierReceiptBatch = saved.BatchNo;
                var postedEarlier = await earlierReceipt.PostAsync([earlierReceiptBatch]);
                Assert.True(postedEarlier.Succeeded, postedEarlier.ErrorMessage);
            }
            if (consumeAt is 111 or 112 or >= 141 and <= 156 or >= 165 and <= 173 or 176 or 177 or 180)
            {
                // The receipt clears the legacy pile price evidence when its
                // price differs. Restore approved evidence at the actual WAC.
                await using var evidence = await _factory.CreateDbContextAsync();
                var balance = await evidence.IvBalLocs.SingleAsync(b => b.Id == 58);
                var state = await evidence.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(consumeAt is 142 or 166 or 177 ? 1.8m : 3.6m, state.AverageUnitCost);
                balance.UnitPrice = state.AverageUnitCost;
                balance.Cost = state.AverageUnitCost;
                balance.PriceEvidence = ProductionLedgerTestFixture.TestPriceEvidence;
                await evidence.SaveChangesAsync();
            }
        }

        async Task<(bool Succeeded, string? Error)> RollbackEarlierReceiptAsync()
        {
            if (earlierGoods is not null)
            {
                var result = await earlierGoods.RollbackAsync([earlierReceiptBatch]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await earlierReceipt!.RollbackAsync([earlierReceiptBatch]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }

        async Task<(bool Succeeded, string? Error)> PostEarlierReceiptAsync()
        {
            if (earlierGoods is not null)
            {
                var result = await earlierGoods.PostAsync([earlierReceiptBatch]);
                return (result.Succeeded, result.ErrorMessage);
            }
            var resultMr = await earlierReceipt!.PostAsync([earlierReceiptBatch]);
            return (resultMr.Succeeded, resultMr.ErrorMessage);
        }

        var outputs = CreateOutputService(businessDate);
        var predecessor = await outputs.CreateAsync(OutputRequest(
            graph.Process1Id, 40m, new DateTime(2026, 10, 1, 8, 0, 0)));
        Assert.True(predecessor.Succeeded, predecessor.Message);
        Assert.True((await outputs.PostAsync(predecessor.Data!.Uid)).Succeeded);

        var issues = CreateIssueService(
            InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true),
            businessDate);
        var issueQuantity = consumeAt is 109 or 110 ? 21m : 4m;
        var request = await CreateIssueSaveAsync(graph, consumeAt is 113 or 114 or 124 or 125 or 126 ? 59 : 58, issueQuantity);
        request.ProductionQtyThisIssue = issueQuantity;
        request.TrxDateTime = businessDate.AddHours(12);
        var issue = await issues.CreateAsync(request);
        Assert.True(issue.Succeeded, issue.Message);
        var issued = await issues.PostAsync([issue.Data!.BatchNo]);
        Assert.True(issued.Succeeded && issued.Data!.SucceededCount == 1,
            issued.Data?.Batches.FirstOrDefault()?.Message ?? issued.Message);

        if (consumeAt is 101 or 103 or 104 or 105 or 106 or 107 or 108)
        {
            var reversed = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify historical stock before the reversal"
            });
            Assert.True(reversed.Succeeded, reversed.Message);
            if (consumeAt == 103)
            {
                var reopened = CreateIssueService(
                    InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true),
                    DateTime.UtcNow.Date);
                var changed = await CreateIssueSaveAsync(graph, 58, 17m);
                changed.ProductionQtyThisIssue = 17m;
                changed.TrxDateTime = new DateTime(2026, 10, 4);
                var beforeDraft = await CaptureReplayCountsAsync();
                var rejected = await reopened.UpdateAsync(issue.Data.BatchNo, changed);
                Assert.False(rejected.Succeeded);
                Assert.Contains("as-of stock", rejected.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(beforeDraft, await CaptureReplayCountsAsync());
                await using var db = await _factory.CreateDbContextAsync();
                var draft = await db.IvTrxBatches.SingleAsync(b => b.BatchNo == issue.Data.BatchNo);
                Assert.Equal(IvBatchStatuses.New, draft.BatchStatus);
                Assert.Equal(4m, (await db.IvTrxBatchDetails.SingleAsync(d => d.BatchId == draft.Id)).FrStdQty);
                Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                return;
            }
            DateTime candidateDate;
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var reversal = await db.IvTrxHistories.SingleAsync(h =>
                    h.BatchNo == issue.Data.BatchNo && h.EntryRole == "REVERSAL");
                candidateDate = consumeAt is 104 or 105 or 106 or 107 or 108
                    ? reversal.TrxDtTime.Date : new DateTime(2026, 10, 4);
                Assert.True(reversal.TrxDtTime > candidateDate);
            }
            if (consumeAt is 106 or 108)
            {
                await using (var setup = await _factory.CreateDbContextAsync())
                {
                    setup.IvLocations.Add(new IvLocation
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH01",
                        LocCode = "BIN-B", IsActive = true
                    });
                    await setup.SaveChangesAsync();
                }
                var transfer = InventoryLedgerTestFixture.CreateStockTransfer(
                    _factory, candidateDate, enableBackdatedReplay: consumeAt == 108);
                var savedTransfer = await transfer.SaveNewAsync(new IvStockTransferSaveRequest
                {
                    TrxDate = candidateDate,
                    Lines = [new IvStockTransferLineRequest
                    {
                        FromBalLocId = 58, ICode = "RM001", FrWarehouse = "WH01", FrLocation = "BIN-A",
                        FrLotNo = string.Empty, ToWarehouse = "WH01", ToLocation = "BIN-B",
                        Quantity = 17m, Uom = "EA", IClassCode = "RAW",
                        IStatus = IvItemStatuses.Active, UnitPrice = 2m
                    }]
                });
                Assert.True(savedTransfer.Succeeded, savedTransfer.ErrorMessage);
                var beforeTransfer = await CaptureReplayCountsAsync();
                var transferResult = await transfer.PostAsync([savedTransfer.BatchNo]);
                Assert.False(transferResult.Succeeded);
                Assert.Equal(beforeTransfer, await CaptureReplayCountsAsync());
                await using var transferState = await _factory.CreateDbContextAsync();
                Assert.Equal(20m, (await transferState.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.False(await transferState.IvBalLocs.AnyAsync(b => b.LocCode == "BIN-B"));
                Assert.Equal(40m, (await transferState.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                Assert.Equal(IvBatchStatuses.New,
                    (await transferState.IvTrxBatches.SingleAsync(b => b.BatchNo == savedTransfer.BatchNo)).BatchStatus);
                return;
            }
            if (consumeAt == 107)
            {
                var adjustment = InventoryLedgerTestFixture.CreateStockAdjustment(_factory, candidateDate);
                var savedAdjustment = await adjustment.SaveNewAsync(new IvStockAdjustmentSaveRequest
                {
                    TrxDate = candidateDate,
                    Lines = [new IvStockAdjustmentLineRequest
                    {
                        BalLocId = 58, ICode = "RM001", Warehouse = "WH01", Location = "BIN-A",
                        LotNo = string.Empty, AdjustQty = -17m, Uom = "EA", IClassCode = "RAW",
                        IStatus = IvItemStatuses.Active, UnitPrice = 2m, Reason = IvAdjustmentReasons.Found
                    }]
                });
                Assert.True(savedAdjustment.Succeeded, savedAdjustment.ErrorMessage);
                var beforeAdjustment = await CaptureReplayCountsAsync();
                var adjustmentResult = await adjustment.PostAsync([savedAdjustment.BatchNo]);
                Assert.False(adjustmentResult.Succeeded);
                Assert.Equal(beforeAdjustment, await CaptureReplayCountsAsync());
                await using var adjustmentState = await _factory.CreateDbContextAsync();
                Assert.Equal(20m, (await adjustmentState.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(40m, (await adjustmentState.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                Assert.Equal(IvBatchStatuses.New,
                    (await adjustmentState.IvTrxBatches.SingleAsync(b => b.BatchNo == savedAdjustment.BatchNo)).BatchStatus);
                return;
            }
            var laterIssue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, candidateDate, enableBackdatedReplay: consumeAt != 105);
            var saved = await laterIssue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = candidateDate,
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 58, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 17m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 2m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            var before = await CaptureReplayCountsAsync();
            var attempted = await laterIssue.PostAsync([saved.BatchNo]);
            Assert.False(attempted.Succeeded, "The issue must not use stock restored at a later date.");
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Equal(20m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, stock.OnHandBaseQty);
            Assert.Equal(40m, stock.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == saved.BatchNo)).BatchStatus);
            return;
        }

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
        var consumedQty = consumeAt is 11 or 12 or 22 or 23 or 34 or 35 or 39 or 40 or 46 or 47 or 78 or 79 or >= 174 and <= 180 ? 2m : 4m;
        async Task ConsumeIssueAsync()
        {
            var created = await outputs.CreateAsync(OutputRequest(
                graph.Process2Id, consumedQty, businessDate.AddHours(13)));
            Assert.True(created.Succeeded, created.Message);
            var result = await outputs.PostAsync(created.Data!.Uid);
            Assert.True(result.Succeeded, result.Message);
            consumedOutputId = created.Data.Uid;
        }
        if (consumeAt is 9 or 11 or 13 or 14 or 15 or 16 or 17 or 20 or 22 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 34 or 35 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 63 or 64 or 65 or 66 or 67 or 68 or 69 or 70 or 74 or 75 or 76 or 77 or 78 or 79 or 80 or 81 or 82 or 83 or 84 or 157 or 158 or 159 or 160 or 161 or 162 or 163 or >= 165 and <= 180)
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

        var qaPhysical = consumeAt is 72 or 94 or 133 or 162 ? new QaPhysicalHoldAuthorizationService() : null;
        var qaReceipt = consumeAt is 73 or 98 or 139 or 163 ? new QaReceiptAuthorizationService() : null;
        var laterReceiptDate = DateTime.UtcNow.Date.AddDays(2);
        var receiptBusinessDate = consumeAt == 102 ? DateTime.UtcNow.Date.AddDays(1)
            : consumeAt == 80 ? new DateTime(2026, 11, 3)
            : consumeAt == 87 ? laterReceiptDate : businessDate;
        var receiptDate = consumeAt == 102 ? DateTime.UtcNow.Date.AddDays(1)
            : consumeAt == 87 ? laterReceiptDate : consumeAt is 127 or 136 ? businessDate.Date : new DateTime(2026, 10, 1);
        var receipts = goodsReceipt ? null : InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, receiptBusinessDate, enableBackdatedReplay: consumeAt is not (124 or 126 or 127), qaPhysical: qaPhysical);
        IvGoodsReceiptService? goods = null;
        int receiptBatchNo;
        if (goodsReceipt)
        {
            var tenant = InventoryTenantTestHelper.CreateTenantContext();
            goods = new IvGoodsReceiptService(_factory, tenant, Access(), new RunningNumberService(),
                new FixedCurrentDateService(receiptBusinessDate),
                new IvStockMasterRepository(_factory), new IvStockCommonRepository(_factory),
                new IvStockTransactionRepository(), new IvStockPostingRepository(),
                InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: consumeAt is not (124 or 126 or 127),
                    qaReceipt: qaReceipt, qaPhysical: qaPhysical),
                NullLogger<IvGoodsReceiptService>.Instance, approval: TestDisabledProcurementApprovalGuard.Instance);
            var saved = await goods.SaveNewAsync(new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive, TrxDate = receiptDate,
                Lines = [new IvGoodsReceiptLineRequest
                {
                    PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                    ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = consumeAt == 122 ? 0.01m : 5m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            receiptBatchNo = saved.BatchNo;
        }
        else
        {
            var saved = await receipts!.SaveNewAsync(new IvMiscReceiptSaveRequest
            {
                TrxDate = receiptDate,
                Lines = [new IvMiscReceiptLineRequest
                {
                    ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                    Quantity = consumeAt == 122 ? 0.01m : 5m, UnitPrice = receiptPrice, PriceConfirmed = true,
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
        if (consumeAt is 36 or 38 or 39 or 41 or 42 or 43 or 45 or 46 or 47 or 48 or 49 or 50 or 52 or 53 or 54 or 55 or 56 or 57 or 58 or 59 or 60 or 61 or 62 or 74)
        {
            unchangedFinishedGood = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate,
                consumeAt == 74 ? new QaPhysicalHoldAuthorizationService() : null);
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
        if (consumeAt is 28 or 30 or 31 or 34 or 67)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.Equal(6m, finished.Value);
            await RollbackFinishedGoodAsync(finished);
            if (consumeAt is 30 or 31)
                await DamageFinishedGoodReversalAsync(finished.Document.Id, consumeAt == 31);
            if (consumeAt == 67 && !receiptFirst)
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "Verify later backdated receipt is rejected atomically"
                });
                Assert.True(outputReversal.Succeeded, outputReversal.Message);
            }
        }
        if (consumeAt is 69 or 70 or 75 or 76 or 77 or 78 or 79 or 80 or 81 or 82 or 83 or 84 or 157 or 158 or 159 or 160 or 161 or 162 or 163 or >= 165 and <= 180)
        {
            var rollbackOutputs = consumeAt == 80 ? CreateOutputService(receiptBusinessDate) : outputs;
            var reversed = await rollbackOutputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "Verify replay after output reversal without finished goods"
            });
            Assert.True(reversed.Succeeded, reversed.Message);
            if (consumeAt is 76 or 77)
                await DamageOrRejectReversedOutputAsync(consumedOutputId, consumeAt == 77);
        }
        if (consumeAt is 84 or 85 or 87 or 91 or 92 or 93 or 94 or 95 or 96 or 98 or 99 or 100 or 102 or 119 or 120 or 121 or 122 or 124 or 125 or 129 or 130 or 131 or 132 or 133 or 134 or 135 or 136 or 137 or 138 or 139 or 140 or 157 or 158 or 159 or 160 or 161 or 162 or 163 or >= 165 and <= 181)
        {
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify a receipt after issue and output reversal"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            if (consumeAt is >= 174 and <= 180)
            {
                var initial = consumeAt is 176 or 177 or 180 ? consumeAt == 177 ? 7.2m : 14.4m : 8m;
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo,
                    initial, initial / 2m + 2m, initial / 2m);
            }
            else if (consumeAt is >= 165 and <= 173)
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo,
                    consumeAt == 166 ? 7.2m : 14.4m, consumeAt == 166 ? 11.2m : 18.4m);
            else if (consumeAt is 84 or 157 or 158 or 159 or 160 or 161 or 162 or 163)
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            else
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            if (consumeAt == 84)
                await DamageOrRejectReversedOutputAsync(consumedOutputId, injectFailure: false);
            if (consumeAt is 91 or 93 or 121)
                await DamageIssueReversalCostAsync(issue.Data.BatchNo);
            if (consumeAt is 129 or 130 or 137 or 138 or 157 or 158 or 159 or 160 or >= 165 and <= 181)
                await InstallReversedIssueSqlGuardsAsync();
        }
        if (consumeAt == 181)
        {
            Assert.NotNull(_wacPostingBarrier);
            IvMiscReceiptSaveRequest EditedReceipt() => new()
            {
                TrxDate = receiptDate,
                Lines = [new IvMiscReceiptLineRequest
                {
                    ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                    Quantity = 5m, UnitPrice = 11m, PriceConfirmed = true,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                }]
            };
            async Task<(bool Succeeded, string? Error)> EditReceiptAsync()
            {
                if (goods is not null)
                {
                    var result = await goods.UpdateAsync(receiptBatchNo, new IvGoodsReceiptSaveRequest
                    {
                        TrxType = IvTrxTypes.GoodsReceive, TrxDate = receiptDate, Remark = "Edited after rollback",
                        Lines = [new IvGoodsReceiptLineRequest { PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                            ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m }]
                    });
                    return (result.Succeeded, result.ErrorMessage);
                }
                var updated = await receipts!.UpdateAsync(receiptBatchNo, EditedReceipt());
                return (updated.Succeeded, updated.ErrorMessage);
            }
            _wacPostingBarrier.Arm();
            var posting = PostReceiptAsync();
            Task<(bool Succeeded, string? Error)>? editing = null;
            var editedBeforePostFinished = false;
            try
            {
                var first = await Task.WhenAny(_wacPostingBarrier.Reached, posting, Task.Delay(TimeSpan.FromSeconds(15)));
                if (first == posting)
                {
                    var early = await posting;
                    Assert.Fail($"Posting ended before the race point: {early.Succeeded}; {early.Error}\n{_wacPostingBarrier.Diagnostics}");
                }
                Assert.True(first == _wacPostingBarrier.Reached, $"The WAC receipt race point was not reached.\n{_wacPostingBarrier.Diagnostics}");
                editing = EditReceiptAsync();
                await _wacPostingBarrier.EditAttempt.WaitAsync(TimeSpan.FromSeconds(10));
                editedBeforePostFinished = await Task.WhenAny(editing, Task.Delay(500)) == editing;
            }
            finally
            {
                _wacPostingBarrier.Release();
            }
            var postResult = await posting;
            Assert.NotNull(editing);
            var editResult = await editing;
            Assert.False(editedBeforePostFinished, "The receipt draft was editable during the posting transaction.");
            Assert.False(editResult.Succeeded, "The edit must not overwrite a posted receipt.");
            Assert.True(postResult.Succeeded, postResult.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 14.4m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, state.OnHandBaseQty);
                Assert.Equal(90m, state.InventoryValue);
                Assert.Equal(25m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(10m, (await db.IvTrxHistories.SingleAsync(h => h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL")).UnitPrice);
                if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var firstRollback = await RollbackReceiptAsync();
            Assert.True(firstRollback.Succeeded, firstRollback.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            var acceptedEdit = await EditReceiptAsync();
            Assert.True(acceptedEdit.Succeeded, acceptedEdit.Error);
            var repost = await PostReceiptAsync();
            Assert.True(repost.Succeeded, repost.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, goodsReceipt ? 14.4m : 15.2m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, goodsReceipt ? 14.4m : 15.2m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, state.OnHandBaseQty);
                Assert.Equal(goodsReceipt ? 90m : 95m, state.InventoryValue);
                Assert.Equal(25m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var finalRollback = await RollbackReceiptAsync();
            Assert.True(finalRollback.Succeeded, finalRollback.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, state.OnHandBaseQty);
                Assert.Equal(40m, state.InventoryValue);
                Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(2, await db.IvTrxHistories.CountAsync(h => h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL"));
                Assert.Equal(2, await db.IvTrxHistories.CountAsync(h => h.BatchNo == receiptBatchNo && h.EntryRole == "REVERSAL"));
                if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (consumeAt is >= 174 and <= 180)
        {
            var earlier = consumeAt is 176 or 177 or 180;
            var issueWithReceipt = consumeAt is 175 or 177 ? 7.2m : 14.4m;
            var stockValueWithReceipt = consumeAt is 175 or 177 ? 45m : 90m;
            var alreadyPosted = false;
            if (consumeAt == 178)
            {
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var reversalId = await db.ProductionBalLotMovements.Where(m =>
                            m.ProductionOutputId == consumedOutputId
                            && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal)
                        .Select(m => m.Uid).SingleAsync();
                    var numericId = reversalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var trigger = db.Database.IsSqlServer()
                        ? "CREATE OR ALTER TRIGGER dbo.TestRejectPartialOutput ON dbo.ProductionCostRestatementFact AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM inserted WHERE ProductionBalLotMovementId = " + numericId + ") THROW 51998, 'injected partial output failure', 1; END"
                        : "CREATE TRIGGER TestRejectPartialOutput BEFORE INSERT ON ProductionCostRestatementFact WHEN NEW.ProductionBalLotMovementId = " + numericId + " BEGIN SELECT RAISE(ABORT, 'injected partial output failure'); END;";
                    await db.Database.ExecuteSqlRawAsync(trigger);
                }
                var before = await CaptureReplayCountsAsync();
                var refused = await PostReceiptAsync();
                Assert.False(refused.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertPartialAsync(false);
                await using var cleanupDb = await _factory.CreateDbContextAsync();
                if (cleanupDb.Database.IsSqlServer())
                {
                    await cleanupDb.Database.OpenConnectionAsync();
                    await using var command = cleanupDb.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                    Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                }
                await cleanupDb.Database.ExecuteSqlRawAsync(cleanupDb.Database.IsSqlServer()
                    ? "DROP TRIGGER dbo.TestRejectPartialOutput" : "DROP TRIGGER TestRejectPartialOutput");
            }
            if (consumeAt == 179)
            {
                var attempts = await Task.WhenAll(PostReceiptAsync(), PostReceiptAsync());
                Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
                await using var db = await _factory.CreateDbContextAsync();
                Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL"));
                alreadyPosted = true;
            }
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (!earlier && !(alreadyPosted && cycle == 0))
                {
                    var placed = await PostReceiptAsync();
                    Assert.True(placed.Succeeded, $"Post cycle {cycle}: {placed.Error}");
                }
                await AssertPartialAsync(true);
                var removed = earlier ? await RollbackEarlierReceiptAsync() : await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertPartialAsync(false);
                if (earlier)
                {
                    if (consumeAt == 180)
                    {
                        var price = goodsReceipt ? cycle == 0 ? 6m : 4m : cycle == 0 ? 11m : 12m;
                        if (goodsReceipt)
                        {
                            await using (var db = await _factory.CreateDbContextAsync())
                            {
                                var poLine = await db.PoOrderDetails.SingleAsync();
                                poLine.PoUnitPrice = price;
                                poLine.NetAmount = 10m * price;
                                await db.SaveChangesAsync();
                            }
                            var edited = await earlierGoods!.UpdateAsync(earlierReceiptBatch, new IvGoodsReceiptSaveRequest
                            {
                                TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 2),
                                Lines = [new IvGoodsReceiptLineRequest { PoNo = "PO-IP-REPLAY", PoRelNo = 1,
                                    PoLineNo = 1, ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m }]
                            });
                            Assert.True(edited.Succeeded, edited.ErrorMessage);
                        }
                        else
                        {
                            var edited = await earlierReceipt!.UpdateAsync(earlierReceiptBatch, new IvMiscReceiptSaveRequest
                            {
                                TrxDate = new DateTime(2026, 10, 2), Lines = [new IvMiscReceiptLineRequest
                                {
                                    ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                                    Quantity = 5m, UnitPrice = price, PriceConfirmed = true,
                                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                                }]
                            });
                            Assert.True(edited.Succeeded, edited.ErrorMessage);
                        }
                        stockValueWithReceipt = 40m + 5m * price;
                        issueWithReceipt = goodsReceipt ? cycle == 0 ? 11.2m : 9.6m : cycle == 0 ? 15.2m : 16m;
                    }
                    var replaced = await PostEarlierReceiptAsync();
                    Assert.True(replaced.Succeeded, $"Repost cycle {cycle}: {replaced.Error}");
                    await AssertPartialAsync(true);
                }
            }
            return;

            async Task AssertPartialAsync(bool postedReceipt)
            {
                var issueValue = postedReceipt ? issueWithReceipt : 8m;
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo,
                    issueValue, issueValue / 2m + 2m, issueValue / 2m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, issueValue);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo,
                    postedReceipt ? 25m : 20m, postedReceipt ? 21m : 16m, postedReceipt ? 25m : 20m);
                await using var stateDb = await _factory.CreateDbContextAsync();
                var stock = await stateDb.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(postedReceipt ? 25m : 20m, stock.OnHandBaseQty);
                Assert.Equal(postedReceipt ? stockValueWithReceipt : 40m, stock.InventoryValue);
                if (goodsReceipt) Assert.Equal(postedReceipt ? 5m : 0m,
                    (await stateDb.PoOrderDetails.SingleAsync()).RecvQty);
            }
        }
        if (consumeAt is >= 165 and <= 173)
        {
            var initialIssue = consumeAt == 166 ? 7.2m : 14.4m;
            var initialValue = consumeAt == 166 ? 45m : 90m;
            var receiptQuantity = 5m;
            if (consumeAt == 170)
            {
                await SeedQaPhysicalHoldAsync(58, 21m);
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackEarlierReceiptAsync();
                Assert.False(blocked.Succeeded);
                Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
                await using var db = await _factory.CreateDbContextAsync();
                (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                await db.SaveChangesAsync();
            }
            if (consumeAt == 171)
            {
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr
                    {
                        CompanyCode = "DEMO", BranchCode = "HQ",
                        PeriodFrom = new DateTime(2026, 10, 1), PeriodTo = new DateTime(2026, 10, 31),
                        Status = IvPeriodCloseStatuses.Closed, RowVersion = [1]
                    });
                    await db.SaveChangesAsync();
                }
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackEarlierReceiptAsync();
                Assert.False(blocked.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
                await using var reopenDb = await _factory.CreateDbContextAsync();
                (await reopenDb.IvPeriodCloseHdrs.SingleAsync()).Status = IvPeriodCloseStatuses.Reopened;
                await reopenDb.SaveChangesAsync();
            }
            if (consumeAt == 168)
            {
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var reversalId = await db.ProductionBalLotMovements.Where(m =>
                            m.ProductionOutputId == consumedOutputId
                            && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal)
                        .Select(m => m.Uid).SingleAsync();
                    var numericId = reversalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var trigger = db.Database.IsSqlServer()
                        ? "CREATE OR ALTER TRIGGER dbo.TestRejectEarlierOutputPair ON dbo.ProductionCostRestatementFact AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM inserted WHERE ProductionBalLotMovementId = " + numericId + ") THROW 51998, 'injected output rollback failure', 1; END"
                        : "CREATE TRIGGER TestRejectEarlierOutputPair BEFORE INSERT ON ProductionCostRestatementFact WHEN NEW.ProductionBalLotMovementId = " + numericId + " BEGIN SELECT RAISE(ABORT, 'injected output rollback failure'); END;";
                    await db.Database.ExecuteSqlRawAsync(trigger);
                }
                var before = await CaptureReplayCountsAsync();
                var refused = await RollbackEarlierReceiptAsync();
                Assert.False(refused.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var unchanged = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(25m, unchanged.OnHandBaseQty);
                    Assert.Equal(90m, unchanged.InventoryValue);
                    if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                    if (db.Database.IsSqlServer())
                    {
                        await db.Database.OpenConnectionAsync();
                        await using var command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                        Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                    }
                    await db.Database.ExecuteSqlRawAsync(db.Database.IsSqlServer()
                        ? "DROP TRIGGER dbo.TestRejectEarlierOutputPair" : "DROP TRIGGER TestRejectEarlierOutputPair");
                }
            }
            if (consumeAt == 169)
            {
                var attempts = await Task.WhenAll(RollbackEarlierReceiptAsync(), RollbackEarlierReceiptAsync());
                Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
                await using (var db = await _factory.CreateDbContextAsync())
                    Assert.Equal(1, await db.IvTrxHistories.CountAsync(h =>
                        h.BatchNo == earlierReceiptBatch && h.EntryRole == "REVERSAL"));
                var reposted = await PostEarlierReceiptAsync();
                Assert.True(reposted.Succeeded, reposted.Error);
            }
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var removed = await RollbackEarlierReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 20m, 16m, 20m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m, stock.OnHandBaseQty);
                    Assert.Equal(40m, stock.InventoryValue);
                    if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                if (consumeAt == 172 && cycle == 0)
                {
                    long effectId;
                    long correctOwner;
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        var output = await db.ProductionOutputs.SingleAsync(o => o.Uid == consumedOutputId);
                        var reversedProduce = await db.ProductionBalLotMovements.SingleAsync(m =>
                            m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
                        var fact = await db.ProductionCostRestatementFacts.SingleAsync(f =>
                            f.ProductionBalLotMovementId == reversedProduce.Uid && f.ReversesRestatementFactId == null);
                        var effect = await db.StockReplayEffects.AsNoTracking().SingleAsync(e =>
                            e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT" && e.AffectedEntityId == fact.Id);
                        effectId = effect.Id;
                        correctOwner = effect.BusinessOwnerPostingId;
                        Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                            .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, output.PostingId!.Value)));
                    }
                    var before = await CaptureReplayCountsAsync();
                    var blocked = await PostEarlierReceiptAsync();
                    Assert.False(blocked.Succeeded);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
                    await using var repairDb = await _factory.CreateDbContextAsync();
                    Assert.Equal(1, await repairDb.StockReplayEffects.Where(e => e.Id == effectId)
                        .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, correctOwner)));
                }
                if (consumeAt is 167 or 173)
                {
                    var price = consumeAt == 173 ? cycle == 0 ? 8m : 5m
                        : goodsReceipt ? cycle == 0 ? 6m : 4m : cycle == 0 ? 11m : 12m;
                    receiptQuantity = consumeAt == 173 ? cycle == 0 ? 4m : 10m : 5m;
                    if (goodsReceipt)
                    {
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var poLine = await db.PoOrderDetails.SingleAsync();
                            poLine.PoUnitPrice = price;
                            poLine.NetAmount = 10m * price;
                            await db.SaveChangesAsync();
                        }
                        var edited = await earlierGoods!.UpdateAsync(earlierReceiptBatch, new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvGoodsReceiptLineRequest
                            {
                                PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                                ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = receiptQuantity
                            }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                    else
                    {
                        var edited = await earlierReceipt!.UpdateAsync(earlierReceiptBatch, new IvMiscReceiptSaveRequest
                        {
                            TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvMiscReceiptLineRequest
                            {
                                ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                                Quantity = receiptQuantity, UnitPrice = price, PriceConfirmed = true,
                                Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                            }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                    initialValue = 40m + receiptQuantity * price;
                    initialIssue = consumeAt == 173 ? 12m
                        : goodsReceipt ? cycle == 0 ? 11.2m : 9.6m : cycle == 0 ? 15.2m : 16m;
                }
                var repostedResult = await PostEarlierReceiptAsync();
                Assert.True(repostedResult.Succeeded, $"Repost cycle {cycle}: {repostedResult.Error}");
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo,
                    initialIssue, initialIssue + 4m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, initialIssue);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo,
                    20m + receiptQuantity, 16m + receiptQuantity, 20m + receiptQuantity);
                await using var reposted = await _factory.CreateDbContextAsync();
                var repostedStock = await reposted.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m + receiptQuantity, repostedStock.OnHandBaseQty);
                Assert.Equal(initialValue, repostedStock.InventoryValue);
                if (goodsReceipt) Assert.Equal(receiptQuantity, (await reposted.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (consumeAt == 95)
        {
            var laterDate = DateTime.UtcNow.Date.AddDays(1);
            var laterReceipt = InventoryLedgerTestFixture.CreateMiscReceipt(
                _factory, laterDate, enableBackdatedReplay: true);
            var savedLater = await laterReceipt.SaveNewAsync(new IvMiscReceiptSaveRequest
            {
                TrxDate = laterDate,
                Lines = [new IvMiscReceiptLineRequest
                {
                    ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                    Quantity = 1m, UnitPrice = 2m, PriceConfirmed = true,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                }]
            });
            Assert.True(savedLater.Succeeded, savedLater.ErrorMessage);
            var postedLater = await laterReceipt.PostAsync([savedLater.BatchNo]);
            Assert.True(postedLater.Succeeded, postedLater.ErrorMessage);
        }
        if (consumeAt == 96)
        {
            await using var db = await _factory.CreateDbContextAsync();
            db.IvBalLocs.Add(new IvBalLoc
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM001",
                WhCode = "WH01", LocCode = "BIN-A", LotNo = "",
                IStatus = "HOLD", StdQty = 0m, StdUom = "EA"
            });
            await db.SaveChangesAsync();
        }
        if (consumeAt is 89 or 90 or 109 or 110 or 111 or 112 or 113 or 114 or >= 141 and <= 156)
        {
            var reversedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify normal receipt rollback after issue reversal"
            });
            Assert.True(reversedIssue.Succeeded, reversedIssue.Message);
            if (consumeAt is >= 141 and <= 156)
            {
                await InstallReversedIssueSqlGuardsAsync();
                var receiptCost = consumeAt == 142 ? 7.2m : 14.4m;
                var receiptValue = consumeAt == 142 ? 45m : 90m;
                var expectedReceiptQty = 5m;
                if (consumeAt == 148)
                {
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr
                        {
                            CompanyCode = "DEMO", BranchCode = "HQ",
                            PeriodFrom = new DateTime(2026, 10, 1), PeriodTo = new DateTime(2026, 10, 31),
                            Status = IvPeriodCloseStatuses.Closed, RowVersion = [1]
                        });
                        await db.SaveChangesAsync();
                    }
                    var before = await CaptureReplayCountsAsync();
                    var blocked = await RollbackEarlierReceiptAsync();
                    Assert.False(blocked.Succeeded);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                    await SetEarlierPeriodStatusAsync(IvPeriodCloseStatuses.Reopened);
                }
                if (consumeAt == 146)
                {
                    var attempts = await Task.WhenAll(RollbackEarlierReceiptAsync(), RollbackEarlierReceiptAsync());
                    Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.BatchNo == earlierReceiptBatch
                            && h.EntryRole == "REVERSAL"));
                        var concurrentState = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(20m, concurrentState.OnHandBaseQty);
                        Assert.Equal(40m, concurrentState.InventoryValue);
                    }
                    var reposted = await PostEarlierReceiptAsync();
                    Assert.True(reposted.Succeeded, reposted.Error);
                }
                if (consumeAt == 147)
                {
                    await using (var db = await _factory.CreateDbContextAsync())
                        await db.Database.ExecuteSqlRawAsync("""
                            CREATE OR ALTER TRIGGER dbo.TestRejectEarlierReceiptSql
                            ON dbo.ProductionCostRestatementFact AFTER INSERT AS
                            BEGIN
                                SET NOCOUNT ON;
                                THROW 51997, 'injected earlier receipt replay failure', 1;
                            END
                            """);
                    var before = await CaptureReplayCountsAsync();
                    var refused = await RollbackEarlierReceiptAsync();
                    Assert.False(refused.Succeeded);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        var unchanged = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(25m, unchanged.OnHandBaseQty);
                        Assert.Equal(receiptValue, unchanged.InventoryValue);
                        await db.Database.OpenConnectionAsync();
                        await using var command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                        Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TestRejectEarlierReceiptSql");
                    }
                }
                if (consumeAt == 144)
                {
                    await using (var db = await _factory.CreateDbContextAsync())
                        await db.Database.ExecuteSqlRawAsync(
                            "CREATE TRIGGER TestRejectEarlierReceiptCorrection BEFORE INSERT ON ProductionCostRestatementFact "
                            + "BEGIN SELECT RAISE(ABORT, 'injected earlier receipt failure'); END;");
                    var before = await CaptureReplayCountsAsync();
                    var refused = await RollbackEarlierReceiptAsync();
                    Assert.False(refused.Succeeded);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        var unchanged = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(25m, unchanged.OnHandBaseQty);
                        Assert.Equal(receiptValue, unchanged.InventoryValue);
                        if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER TestRejectEarlierReceiptCorrection;");
                    }
                }
                if (consumeAt == 145)
                {
                    await SeedQaPhysicalHoldAsync(58, 21m);
                    var before = await CaptureReplayCountsAsync();
                    var blocked = await RollbackEarlierReceiptAsync();
                    Assert.False(blocked.Succeeded);
                    Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                    await using var db = await _factory.CreateDbContextAsync();
                    (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                    await db.SaveChangesAsync();
                }
                for (var cycle = 0; cycle < 2; cycle++)
                {
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                    await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, receiptCost);
                    await AssertReversedIssueAsOfAsync(issue.Data.BatchNo,
                        20m + expectedReceiptQty, 16m + expectedReceiptQty, 20m + expectedReceiptQty);
                    var cycleRemoved = await RollbackEarlierReceiptAsync();
                    Assert.True(cycleRemoved.Succeeded, $"Rollback cycle {cycle}: {cycleRemoved.Error}");
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                    await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
                    await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 20m, 16m, 20m);
                    await using (var db = await _factory.CreateDbContextAsync())
                    {
                        var removedState = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(20m, removedState.OnHandBaseQty);
                        Assert.Equal(40m, removedState.InventoryValue);
                        if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                    }
                    if (consumeAt == 143)
                    {
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var sourceOwner = await db.IvTrxHistories.Where(h => h.BatchNo == earlierReceiptBatch
                                && h.EntryRole == "ORIGINAL").Select(h => h.StockPostingId!.Value).SingleAsync();
                            var effectId = await db.StockReplayEffects.Where(e => e.EffectKind == "PRODUCTION_ISSUE_REVERSAL_VALUE")
                                .Select(e => e.Id).FirstAsync();
                            Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                                .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, sourceOwner)));
                        }
                        var before = await CaptureReplayCountsAsync();
                        var blocked = await PostEarlierReceiptAsync();
                        Assert.False(blocked.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                        await using var unchanged = await _factory.CreateDbContextAsync();
                        var stateAfterRefusal = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(20m, stateAfterRefusal.OnHandBaseQty);
                        Assert.Equal(40m, stateAfterRefusal.InventoryValue);
                        if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                        return;
                    }
                    if (consumeAt == 148 && cycle == 0)
                    {
                        await SetEarlierPeriodStatusAsync(IvPeriodCloseStatuses.Closed);
                        var before = await CaptureReplayCountsAsync();
                        var blocked = await PostEarlierReceiptAsync();
                        Assert.False(blocked.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                        await SetEarlierPeriodStatusAsync(IvPeriodCloseStatuses.Reopened);
                    }
                    if (consumeAt is 149 or 152 or 154)
                    {
                        var amendedPrice = consumeAt == 154 ? cycle == 0 ? 2m : 11m : cycle == 0 ? 11m : 12m;
                        var edited = await earlierReceipt!.UpdateAsync(earlierReceiptBatch, new IvMiscReceiptSaveRequest
                        {
                            TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvMiscReceiptLineRequest
                            {
                                ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                                Quantity = 5m, UnitPrice = amendedPrice, PriceConfirmed = true,
                                Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                            }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                        receiptCost = consumeAt == 154 ? cycle == 0 ? 8m : 15.2m : cycle == 0 ? 15.2m : 16m;
                        receiptValue = consumeAt == 154 ? cycle == 0 ? 50m : 95m : cycle == 0 ? 95m : 100m;
                    }
                    if (consumeAt is 151 or 155)
                    {
                        var amendedPrice = consumeAt == 155 ? cycle == 0 ? 2m : 6m : cycle == 0 ? 6m : 4m;
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var poLine = await db.PoOrderDetails.SingleAsync();
                            poLine.PoUnitPrice = amendedPrice;
                            poLine.NetAmount = 10m * amendedPrice;
                            await db.SaveChangesAsync();
                        }
                        var edited = await earlierGoods!.UpdateAsync(earlierReceiptBatch, new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvGoodsReceiptLineRequest
                            {
                                PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                                ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m
                            }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                        receiptCost = consumeAt == 155 ? cycle == 0 ? 8m : 11.2m : cycle == 0 ? 11.2m : 9.6m;
                        receiptValue = consumeAt == 155 ? cycle == 0 ? 50m : 70m : cycle == 0 ? 70m : 60m;
                    }
                    if (consumeAt == 153)
                    {
                        var amendedQuantity = cycle == 0 ? 4m : 10m;
                        var amendedPrice = cycle == 0 ? 8m : 5m;
                        await UpdateQuantityAsync(amendedQuantity, amendedPrice);
                        expectedReceiptQty = amendedQuantity;
                        receiptCost = 12m;
                        receiptValue = cycle == 0 ? 72m : 90m;
                    }
                    if (consumeAt == 156 && cycle == 0)
                    {
                        await UpdateQuantityAsync(6m, 10m);
                        var before = await CaptureReplayCountsAsync();
                        var blocked = await PostEarlierReceiptAsync();
                        Assert.False(blocked.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var unchanged = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                            Assert.Equal(20m, unchanged.OnHandBaseQty);
                            Assert.Equal(40m, unchanged.InventoryValue);
                            if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                        }
                        await UpdateQuantityAsync(5m, 10m);
                    }
                    if (consumeAt == 152 && cycle == 0)
                    {
                        await using (var db = await _factory.CreateDbContextAsync())
                            await db.Database.ExecuteSqlRawAsync("""
                                CREATE OR ALTER TRIGGER dbo.TestRejectEditedReceiptSql
                                ON dbo.ProductionCostRestatementFact AFTER INSERT AS
                                BEGIN
                                    SET NOCOUNT ON;
                                    THROW 51996, 'injected edited receipt replay failure', 1;
                                END
                                """);
                        var before = await CaptureReplayCountsAsync();
                        var refused = await PostEarlierReceiptAsync();
                        Assert.False(refused.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var failedState = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                            Assert.Equal(20m, failedState.OnHandBaseQty);
                            Assert.Equal(40m, failedState.InventoryValue);
                            await db.Database.OpenConnectionAsync();
                            await using var command = db.Database.GetDbConnection().CreateCommand();
                            command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                            Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TestRejectEditedReceiptSql");
                        }
                    }
                    var postedAgain = await PostEarlierReceiptAsync();
                    Assert.True(postedAgain.Succeeded, $"Repost cycle {cycle}: {postedAgain.Error}");
                    await using var postedDb = await _factory.CreateDbContextAsync();
                    var postedState = await postedDb.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m + expectedReceiptQty, postedState.OnHandBaseQty);
                    Assert.Equal(receiptValue, postedState.InventoryValue);
                    if (goodsReceipt) Assert.Equal(expectedReceiptQty, (await postedDb.PoOrderDetails.SingleAsync()).RecvQty);
                    if (consumeAt == 150)
                    {
                        var repostOwner = await postedDb.IvTrxHistories.Where(h => h.BatchNo == earlierReceiptBatch
                                && h.EntryRole == "ORIGINAL").OrderByDescending(h => h.Id)
                            .Select(h => h.StockPostingId!.Value).FirstAsync();
                        var effectId = await postedDb.StockReplayEffects.Where(e => e.RootStockPostingId == repostOwner
                                && e.EffectKind == "PRODUCTION_ISSUE_REVERSAL_VALUE")
                            .Select(e => e.Id).FirstAsync();
                        Assert.Equal(1, await postedDb.StockReplayEffects.Where(e => e.Id == effectId)
                            .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, repostOwner)));
                        var before = await CaptureReplayCountsAsync();
                        var blocked = await RollbackEarlierReceiptAsync();
                        Assert.False(blocked.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                        await using var unchanged = await _factory.CreateDbContextAsync();
                        var stateAfterRefusal = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        Assert.Equal(25m, stateAfterRefusal.OnHandBaseQty);
                        Assert.Equal(receiptValue, stateAfterRefusal.InventoryValue);
                        if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                        return;
                    }
                }
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, receiptCost);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, receiptCost);
                return;

                async Task SetEarlierPeriodStatusAsync(string status)
                {
                    await using var db = await _factory.CreateDbContextAsync();
                    (await db.IvPeriodCloseHdrs.SingleAsync()).Status = status;
                    await db.SaveChangesAsync();
                }

                async Task UpdateQuantityAsync(decimal quantity, decimal price)
                {
                    if (goodsReceipt)
                    {
                        await using (var db = await _factory.CreateDbContextAsync())
                        {
                            var poLine = await db.PoOrderDetails.SingleAsync();
                            poLine.PoUnitPrice = price;
                            poLine.NetAmount = 10m * price;
                            await db.SaveChangesAsync();
                        }
                        var result = await earlierGoods!.UpdateAsync(earlierReceiptBatch, new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvGoodsReceiptLineRequest
                            {
                                PoNo = "PO-IP-REPLAY", PoRelNo = 1, PoLineNo = 1,
                                ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = quantity
                            }]
                        });
                        Assert.True(result.Succeeded, result.ErrorMessage);
                    }
                    else
                    {
                        var result = await earlierReceipt!.UpdateAsync(earlierReceiptBatch, new IvMiscReceiptSaveRequest
                        {
                            TrxDate = new DateTime(2026, 10, 2),
                            Lines = [new IvMiscReceiptLineRequest
                            {
                                ICode = "RM001", ToWarehouse = "WH01", ToLocation = "BIN-A",
                                Quantity = quantity, UnitPrice = price, PriceConfirmed = true,
                                Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active, Reason = "ADJ"
                            }]
                        });
                        Assert.True(result.Succeeded, result.ErrorMessage);
                    }
                }
            }
            if (consumeAt is 111 or 112 or 113 or 114)
            {
                var before = await CaptureReplayCountsAsync();
                await using (var db = await _factory.CreateDbContextAsync())
                await using (var tx = await db.Database.BeginTransactionAsync())
                {
                    await new BranchStockTransactionLock().AcquireAsync(db, "DEMO", "HQ", default);
                    var batch = await db.IvTrxBatches.SingleAsync(b => b.BatchNo == earlierReceiptBatch);
                    var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true);
                    var proof = await posting.PlanIsolatedWacReversedIssueAsync(
                        db, "DEMO", "HQ", batch, ["RM001"], rollback: true, default);
                    if (consumeAt is 113 or 114)
                        Assert.Null(proof);
                    else
                    {
                        Assert.NotNull(proof);
                        Assert.Equal(14.4m, proof.OriginalIssueValue);
                        Assert.Equal(8m, proof.ProjectedIssueValue);
                        Assert.Equal(25m, proof.CurrentQuantity);
                        Assert.Equal(90m, proof.CurrentValue);
                        Assert.Equal(20m, proof.ProjectedQuantity);
                        Assert.Equal(40m, proof.ProjectedValue);
                        Assert.False(proof.CanOmitReversedPair);
                    }
                    await tx.RollbackAsync();
                }
                Assert.Equal(before, await CaptureReplayCountsAsync());
                if (consumeAt == 111)
                {
                    var correctedRemoval = await RollbackEarlierReceiptAsync();
                    Assert.True(correctedRemoval.Succeeded, correctedRemoval.Error);
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                    await using var corrected = await _factory.CreateDbContextAsync();
                    var correctedState = await corrected.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m, correctedState.OnHandBaseQty);
                    Assert.Equal(40m, correctedState.InventoryValue);
                    if (goodsReceipt) Assert.Equal(0m, (await corrected.PoOrderDetails.SingleAsync()).RecvQty);
                    return;
                }
                var blocked = await RollbackEarlierReceiptAsync();
                Assert.False(blocked.Succeeded,
                    "Removing the receipt would change both historical issue costs.");
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await using var unchanged = await _factory.CreateDbContextAsync();
                var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(90m, stock.InventoryValue);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == earlierReceiptBatch)).BatchStatus);
                if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
                return;
            }
            if (consumeAt is 109 or 110)
            {
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackEarlierReceiptAsync();
                Assert.False(blocked.Succeeded,
                    "Removing the receipt would leave the later 21-unit issue short by one unit.");
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await using var unchanged = await _factory.CreateDbContextAsync();
                var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(50m, stock.InventoryValue);
                Assert.Equal(25m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == earlierReceiptBatch)).BatchStatus);
                if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 42m);
                return;
            }
            if (consumeAt == 90)
            {
                await DamageIssueReversalCostAsync(issue.Data.BatchNo);
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackEarlierReceiptAsync();
                Assert.False(blocked.Succeeded);
                Assert.Contains("reversed Production issue", blocked.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await using var unchanged = await _factory.CreateDbContextAsync();
                var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(50m, stock.InventoryValue);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == earlierReceiptBatch)).BatchStatus);
                return;
            }
            var removed = await RollbackEarlierReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            return;
        }
        if (consumeAt == 99)
        {
            var simultaneous = await Task.WhenAll(PostReceiptAsync(), PostReceiptAsync());
            Assert.True(simultaneous.Any(result => result.Succeeded),
                string.Join("; ", simultaneous.Select(result => result.Error)));
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.CompanyCode == "DEMO"
                    && h.BranchCode == "HQ" && h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL"));
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 138)
        {
            await using (var db = await _factory.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE OR ALTER TRIGGER dbo.TestRejectReversedIssuePost
                    ON dbo.ProductionCostRestatementFact AFTER INSERT AS
                    BEGIN
                        SET NOCOUNT ON;
                        THROW 51998, 'injected reversed issue replay failure', 1;
                    END
                    """);
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, state.OnHandBaseQty);
                Assert.Equal(40m, state.InventoryValue);
                Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                await db.Database.OpenConnectionAsync();
                await using var command = db.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                var marker = await command.ExecuteScalarAsync();
                Assert.True(marker is null or DBNull, "The SQL posting session was not cleared after failure.");
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER dbo.TestRejectReversedIssuePost");
            }
            var retried = await PostReceiptAsync();
            Assert.True(retried.Succeeded, retried.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 137)
        {
            var attempts = await Task.WhenAll(PostReceiptAsync(), PostReceiptAsync());
            Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 14.4m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.CompanyCode == "DEMO"
                    && h.BranchCode == "HQ" && h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL"));
                var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(90m, stock.InventoryValue);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt is 134 or 135)
        {
            if (consumeAt == 134)
            {
                var postedReceipt = await PostReceiptAsync();
                Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
                await using var db = await _factory.CreateDbContextAsync();
                var receiptOwner = await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatchNo
                    && h.EntryRole == "ORIGINAL").Select(h => h.StockPostingId!.Value).SingleAsync();
                var effectId = await db.StockReplayEffects.Where(e =>
                        e.EffectKind == "PRODUCTION_ISSUE_REVERSAL_VALUE")
                    .OrderBy(e => e.Id).Select(e => e.Id).FirstAsync();
                Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                    .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, receiptOwner)));
            }
            else
            {
                await using var db = await _factory.CreateDbContextAsync();
                await db.Database.ExecuteSqlRawAsync(
                    "CREATE TRIGGER TestRejectIssueCorrectionPost BEFORE INSERT ON ProductionCostRestatementFact "
                    + "WHEN NEW.ReversesRestatementFactId IS NULL BEGIN SELECT RAISE(ABORT, 'injected issue replay failure'); END;");
            }
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, stock.OnHandBaseQty);
            Assert.Equal(40m, stock.InventoryValue);
            Assert.Equal(20m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt == 140)
        {
            await using (var db = await _factory.CreateDbContextAsync())
            {
                db.IvPeriodCloseHdrs.Add(new IvPeriodCloseHdr
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    PeriodFrom = new DateTime(2026, 10, 1), PeriodTo = new DateTime(2026, 10, 31),
                    Status = IvPeriodCloseStatuses.Closed, RowVersion = [1]
                });
                await db.SaveChangesAsync();
            }
            var beforePost = await CaptureReplayCountsAsync();
            var blockedPost = await PostReceiptAsync();
            Assert.False(blockedPost.Succeeded);
            Assert.Equal(beforePost, await CaptureReplayCountsAsync());
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await SetPeriodStatusAsync(IvPeriodCloseStatuses.Reopened);

            var postedReceipt = await PostReceiptAsync();
            Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await SetPeriodStatusAsync(IvPeriodCloseStatuses.Closed);
            var beforeRollback = await CaptureReplayCountsAsync();
            var blockedRollback = await RollbackReceiptAsync();
            Assert.False(blockedRollback.Succeeded);
            Assert.Equal(beforeRollback, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, state.OnHandBaseQty);
                Assert.Equal(90m, state.InventoryValue);
                if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            }
            await SetPeriodStatusAsync(IvPeriodCloseStatuses.Reopened);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;

            async Task SetPeriodStatusAsync(string status)
            {
                await using var db = await _factory.CreateDbContextAsync();
                (await db.IvPeriodCloseHdrs.SingleAsync()).Status = status;
                await db.SaveChangesAsync();
            }
        }
        if (consumeAt == 133)
        {
            var postedReceipt = await PostReceiptAsync();
            Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
            await SeedQaPhysicalHoldAsync(58, 21m);
            var before = await CaptureReplayCountsAsync();
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, state.OnHandBaseQty);
                Assert.Equal(90m, state.InventoryValue);
                (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                await db.SaveChangesAsync();
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt is 131 or 132)
        {
            var postedReceipt = await PostReceiptAsync();
            Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                if (consumeAt == 131)
                {
                    var receiptOwner = await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatchNo
                        && h.EntryRole == "ORIGINAL").Select(h => h.StockPostingId!.Value).SingleAsync();
                    var effectId = await db.StockReplayEffects.Where(e =>
                            e.EffectKind == "PRODUCTION_ISSUE_REVERSAL_VALUE")
                        .OrderBy(e => e.Id).Select(e => e.Id).FirstAsync();
                    Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                        .ExecuteUpdateAsync(u => u.SetProperty(e => e.BusinessOwnerPostingId, receiptOwner)));
                }
                else
                    await db.Database.ExecuteSqlRawAsync(
                        "CREATE TRIGGER TestRejectIssueCorrection BEFORE INSERT ON ProductionCostRestatementFact "
                        + "WHEN NEW.ReversesRestatementFactId IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected issue replay failure'); END;");
            }
            var before = await CaptureReplayCountsAsync();
            var refused = await RollbackReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(25m, stock.OnHandBaseQty);
            Assert.Equal(90m, stock.InventoryValue);
            Assert.Equal(25m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt is 162 or 163)
        {
            var postedReceipt = await PostReceiptAsync();
            Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
            if (consumeAt == 162)
            {
                await SeedQaPhysicalHoldAsync(58, 21m);
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackReceiptAsync();
                Assert.False(blocked.Succeeded);
                Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
                await using var db = await _factory.CreateDbContextAsync();
                var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(90m, stock.InventoryValue);
                (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                await db.SaveChangesAsync();
            }
            else
                await SeedQaReceiptAllocationAsync(receiptBatchNo);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, stock.OnHandBaseQty);
                Assert.Equal(40m, stock.InventoryValue);
                if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                if (consumeAt == 163)
                {
                    var allocation = await db.QaReceiptAllocations.SingleAsync();
                    Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
                    Assert.NotNull(allocation.ReversedAtUtc);
                    Assert.Equal(QaCaseStatuses.Approved, (await db.QaCases.SingleAsync()).Status);
                }
            }
            return;
        }
        if (consumeAt == 161)
        {
            var postedReceipt = await PostReceiptAsync();
            Assert.True(postedReceipt.Succeeded, postedReceipt.Error);
            long effectId;
            long correctOwner;
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var output = await db.ProductionOutputs.SingleAsync(o => o.Uid == consumedOutputId);
                var reversedProduce = await db.ProductionBalLotMovements.SingleAsync(m =>
                    m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
                var fact = await db.ProductionCostRestatementFacts.SingleAsync(f =>
                    f.ProductionBalLotMovementId == reversedProduce.Uid && f.ReversesRestatementFactId == null);
                var effect = await db.StockReplayEffects.AsNoTracking().SingleAsync(e =>
                    e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT" && e.AffectedEntityId == fact.Id);
                effectId = effect.Id;
                correctOwner = effect.BusinessOwnerPostingId;
                Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                    .ExecuteUpdateAsync(update => update.SetProperty(e => e.BusinessOwnerPostingId, output.PostingId!.Value)));
            }
            var before = await CaptureReplayCountsAsync();
            var refused = await RollbackReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, stock.OnHandBaseQty);
                Assert.Equal(90m, stock.InventoryValue);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                Assert.Equal(1, await db.StockReplayEffects.Where(e => e.Id == effectId)
                    .ExecuteUpdateAsync(update => update.SetProperty(e => e.BusinessOwnerPostingId, correctOwner)));
            }
            var retry = await RollbackReceiptAsync();
            Assert.True(retry.Succeeded, retry.Error);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            return;
        }
        if (consumeAt is 159 or 160)
        {
            if (consumeAt == 159)
            {
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var reversalId = await db.ProductionBalLotMovements.Where(m =>
                            m.ProductionOutputId == consumedOutputId
                            && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal)
                        .Select(m => m.Uid).SingleAsync();
                    var numericId = reversalId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    var trigger = db.Database.IsSqlServer()
                        ? "CREATE OR ALTER TRIGGER dbo.TestRejectReversedOutputPair ON dbo.ProductionCostRestatementFact AFTER INSERT AS BEGIN SET NOCOUNT ON; IF EXISTS (SELECT 1 FROM inserted WHERE ProductionBalLotMovementId = " + numericId + ") THROW 51998, 'injected output replay failure', 1; END"
                        : "CREATE TRIGGER TestRejectReversedOutputPair BEFORE INSERT ON ProductionCostRestatementFact WHEN NEW.ProductionBalLotMovementId = " + numericId + " BEGIN SELECT RAISE(ABORT, 'injected output replay failure'); END;";
                    await db.Database.ExecuteSqlRawAsync(trigger);
                }
                var before = await CaptureReplayCountsAsync();
                var refused = await PostReceiptAsync();
                Assert.False(refused.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m, stock.OnHandBaseQty);
                    Assert.Equal(40m, stock.InventoryValue);
                    Assert.Equal(IvBatchStatuses.New,
                        (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                    if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                    if (db.Database.IsSqlServer())
                    {
                        await db.Database.OpenConnectionAsync();
                        await using var command = db.Database.GetDbConnection().CreateCommand();
                        command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                        var marker = await command.ExecuteScalarAsync();
                        Assert.True(marker is null or DBNull, "The SQL posting session was not cleared after failure.");
                    }
                    await db.Database.ExecuteSqlRawAsync(db.Database.IsSqlServer()
                        ? "DROP TRIGGER dbo.TestRejectReversedOutputPair" : "DROP TRIGGER TestRejectReversedOutputPair");
                }
                var retry = await PostReceiptAsync();
                Assert.True(retry.Succeeded, retry.Error);
            }
            else
            {
                var attempts = await Task.WhenAll(PostReceiptAsync(), PostReceiptAsync());
                Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
                await using var db = await _factory.CreateDbContextAsync();
                Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.CompanyCode == "DEMO"
                    && h.BranchCode == "HQ" && h.BatchNo == receiptBatchNo && h.EntryRole == "ORIGINAL"));
            }
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 14.4m, 18.4m);
            await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 14.4m);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            return;
        }
        if (consumeAt is 157 or 158)
        {
            var expectedIssue = consumeAt == 158 ? 7.2m : 14.4m;
            var expectedValue = consumeAt == 158 ? 45m : 90m;
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var postedReceipt = await PostReceiptAsync();
                Assert.True(postedReceipt.Succeeded, $"Post cycle {cycle}: {postedReceipt.Error}");
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo,
                    expectedIssue, expectedIssue + 4m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, expectedIssue);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 25m, 21m, 25m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var postedState = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(25m, postedState.OnHandBaseQty);
                    Assert.Equal(expectedValue, postedState.InventoryValue);
                    if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 20m, 16m, 20m);
                await using var rollbackDb = await _factory.CreateDbContextAsync();
                var rollbackState = await rollbackDb.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, rollbackState.OnHandBaseQty);
                Assert.Equal(40m, rollbackState.InventoryValue);
                if (goodsReceipt) Assert.Equal(0m, (await rollbackDb.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (consumeAt is 129 or 130 or 136)
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle > 0)
                {
                    await using var proofDb = await _factory.CreateDbContextAsync();
                    await using var proofTx = await proofDb.Database.BeginTransactionAsync();
                    await new BranchStockTransactionLock().AcquireAsync(proofDb, "DEMO", "HQ", default);
                    var draft = await proofDb.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo);
                    var proofPosting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true);
                    var nextProof = await proofPosting.PlanIsolatedWacReversedIssueAsync(
                        proofDb, "DEMO", "HQ", draft, ["RM001"], rollback: false, default);
                    Assert.NotNull(nextProof);
                    await proofTx.RollbackAsync();
                }
                var postedReceipt = await PostReceiptAsync();
                Assert.True(postedReceipt.Succeeded, $"Cycle {cycle}: {postedReceipt.Error}");
                var expectedCost = consumeAt == 130 ? 7.2m : 14.4m;
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, expectedCost);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, expectedCost);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 25m, 21m, 25m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(25m, stock.OnHandBaseQty);
                    Assert.Equal(consumeAt == 130 ? 45m : 90m, stock.InventoryValue);
                    Assert.Equal(25m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                    if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
                await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 20m, 16m, 20m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m, stock.OnHandBaseQty);
                    Assert.Equal(40m, stock.InventoryValue);
                    Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                    if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
            }
            return;
        }
        if (consumeAt is 126 or 127)
        {
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded,
                "The earlier receipt would change the cost of the later Production issue.");
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(16m, state.OnHandBaseQty);
            Assert.Equal(32m, state.InventoryValue);
            Assert.Equal(consumeAt == 126 ? 10m : 16m,
                (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            if (consumeAt == 126)
                Assert.Equal(6m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
            if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            var movement = await unchanged.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issue.Data.BatchNo && m.MovementType == ProductionMaterialMovementTypes.Issue);
            Assert.Equal(8m, movement.TotalCost);
            return;
        }
        if (consumeAt is 124 or 125)
        {
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded,
                "The earlier receipt would change the historical WAC issue and reversal costs.");
            Assert.Contains("reversed Production issue", refused.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            Assert.Equal(10m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            Assert.Equal(10m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
            if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt is 119 or 120 or 121 or 122)
        {
            var before = await CaptureReplayCountsAsync();
            await using (var db = await _factory.CreateDbContextAsync())
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await new BranchStockTransactionLock().AcquireAsync(db, "DEMO", "HQ", default);
                var batch = await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo);
                var posting = InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: true);
                var proof = await posting.PlanIsolatedWacReversedIssueAsync(
                    db, "DEMO", "HQ", batch, ["RM001"], rollback: false, default);
                // A rounded-equal issue cost is still unsafe when the projected
                // stock value cannot reconcile to its rounded unit cost.
                if (consumeAt is 121 or 122)
                    Assert.Null(proof);
                else
                {
                    Assert.NotNull(proof);
                    Assert.Equal(4m, proof.IssueQuantity);
                    Assert.Equal(8m, proof.OriginalIssueValue);
                    Assert.Equal(consumeAt == 120 ? 7.2m : 14.4m,
                        proof.ProjectedIssueValue);
                    Assert.Equal(20m, proof.CurrentQuantity);
                    Assert.Equal(40m, proof.CurrentValue);
                    Assert.Equal(25m, proof.ProjectedQuantity);
                    Assert.Equal(consumeAt == 120 ? 45m : 90m,
                        proof.ProjectedValue);
                    Assert.False(proof.IsCostNeutral);
                    Assert.False(proof.CanOmitReversedPair);
                }
                await tx.RollbackAsync();
            }
            Assert.Equal(before, await CaptureReplayCountsAsync());
            var receiptResult = await PostReceiptAsync();
            if (consumeAt is 121 or 122)
            {
                Assert.False(receiptResult.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
            }
            else
            {
                Assert.True(receiptResult.Succeeded, receiptResult.Error);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, consumeAt == 120 ? 7.2m : 14.4m);
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            }
            return;
        }
        var beforeRejectedReceipt = consumeAt is 84 or 91 or 93 or 95 or 96 or 98 or 139 ? await CaptureReplayCountsAsync() : [];
        var posted = await PostReceiptAsync();
        if (consumeAt == 102)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            var backdatedIssue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, DateTime.UtcNow.Date, enableBackdatedReplay: true);
            var saved = await backdatedIssue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = new DateTime(2026, 10, 4),
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 58, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 17m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 2m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            var before = await CaptureReplayCountsAsync();
            var attempted = await backdatedIssue.PostAsync([saved.BatchNo]);
            Assert.False(attempted.Succeeded,
                "The issue must not use stock restored by the later reversal or receipt.");
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt is 98 or 139)
        {
            Assert.False(posted.Succeeded);
            Assert.Contains(QaReadinessMessages.IncomingFrozen, posted.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(beforeRejectedReceipt, await CaptureReplayCountsAsync());
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                (await db.QaPolicies.SingleAsync()).IncomingInspectionEnabled = false;
                await db.SaveChangesAsync();
            }
            var retried = await PostReceiptAsync();
            Assert.True(retried.Succeeded, retried.Error);
            if (consumeAt == 98)
                await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            else
            {
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
                await using var corrected = await _factory.CreateDbContextAsync();
                var state = await corrected.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, state.OnHandBaseQty);
                Assert.Equal(90m, state.InventoryValue);
            }
            await SeedQaReceiptAllocationAsync(receiptBatchNo);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using var qaAfterRollback = await _factory.CreateDbContextAsync();
            var allocation = await qaAfterRollback.QaReceiptAllocations.SingleAsync();
            Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
            Assert.NotNull(allocation.ReversedAtUtc);
            Assert.Equal(QaCaseStatuses.Approved, (await qaAfterRollback.QaCases.SingleAsync()).Status);
            Assert.Single(await qaAfterRollback.QaAuditEvents.Where(e => e.EventType == QaAuditEvents.ReopenedForReissue).ToArrayAsync());
            return;
        }
        if (consumeAt == 73)
        {
            await AssertQaIncomingReplayGuardAsync(posted, 58, receiptBatchNo, issue.Data.BatchNo,
                standard ? 42m : 75.6m, standard ? 8m : 14.4m, PostReceiptAsync, RollbackReceiptAsync);
            return;
        }
        if (consumeAt == 72)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertQaReceiptHoldGuardAsync(58, receiptBatchNo, issue.Data.BatchNo,
                goodsReceipt, standard ? 42m : 75.6m, standard ? 8m : 14.4m, RollbackReceiptAsync);
            return;
        }
        if (consumeAt == 74)
        {
            Assert.True(posted.Succeeded, posted.Error);
            Assert.NotNull(unchangedFinishedGood);
            await AssertQaFinishedGoodHoldGuardAsync(unchangedFinishedGood, consumedOutputId,
                receiptBatchNo, goodsReceipt, RollbackReceiptAsync);
            return;
        }
        if (consumeAt == 84)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                58, receiptBatchNo, goodsReceipt, fifo: false);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            return;
        }
        if (consumeAt == 85)
        {
            if (standard)
            {
                Assert.True(posted.Succeeded, posted.Error);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
            }
            else
            {
                Assert.True(posted.Succeeded, posted.Error);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 14.4m);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 14.4m);
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
                await AssertReversedIssueInventoryCostAsync(issue.Data.BatchNo, 8m);
            }
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 87)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 91)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                58, receiptBatchNo, goodsReceipt, fifo: false);
            return;
        }
        if (consumeAt is 88 or 97)
        {
            Assert.True(posted.Succeeded, posted.Error);
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify receipt rollback after cost-neutral issue reversal"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            if (consumeAt == 97)
            {
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty = 8m;
                    var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    state.OnHandBaseQty = 8m;
                    state.InventoryValue = 16m;
                    await db.SaveChangesAsync();
                }
                var before = await CaptureReplayCountsAsync();
                var blocked = await RollbackReceiptAsync();
                Assert.False(blocked.Succeeded);
                Assert.Contains("reversed Production issue", blocked.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await using var unchanged = await _factory.CreateDbContextAsync();
                var afterState = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(8m, afterState.OnHandBaseQty);
                Assert.Equal(16m, afterState.InventoryValue);
                Assert.Equal(8m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                return;
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt is 92 or 100)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 25m, 21m, 25m);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await AssertReversedIssueAsOfAsync(issue.Data.BatchNo, 20m, 16m, 20m);
            return;
        }
        if (consumeAt == 93)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                58, receiptBatchNo, goodsReceipt, fifo: false);
            return;
        }
        if (consumeAt == 94)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await SeedQaPhysicalHoldAsync(58, 21m);
            var before = await CaptureReplayCountsAsync();
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var hold = await db.QaPhysicalHolds.SingleAsync();
                Assert.Equal(21m, hold.ReservedBaseQty);
                hold.ReservedBaseQty = 20m;
                await db.SaveChangesAsync();
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertWacNeutralReversedIssueStateAsync(receiptBatchNo, goodsReceipt, posted: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 95)
        {
            Assert.False(posted.Succeeded);
            Assert.NotNull(posted.Error);
            Assert.Equal(beforeRejectedReceipt, await CaptureReplayCountsAsync());
            await using var db = await _factory.CreateDbContextAsync();
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(42m, state.InventoryValue);
            Assert.Equal(21m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
            Assert.Equal(IvBatchStatuses.New,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
            if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (consumeAt == 96)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                58, receiptBatchNo, goodsReceipt, fifo: false);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (consumeAt == 83)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertCorrectedIssueRequiresReceiptFirstAsync(issues, consumedOutputId,
                issue.Data.BatchNo, 58, receiptBatchNo, goodsReceipt, fifo: false, RollbackReceiptAsync);
            return;
        }
        if (consumeAt is 81 or 82)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertDamagedOutputCorrectionRollbackAsync(consumedOutputId, issue.Data.BatchNo,
                58, receiptBatchNo, goodsReceipt, fifo: false, RollbackReceiptAsync,
                damageMovement: consumeAt == 82);
            return;
        }
        if (consumeAt is 76 or 77)
        {
            Assert.False(posted.Succeeded);
            await AssertFailedReversedOutputReplayAsync(58, receiptBatchNo, goodsReceipt);
            if (consumeAt == 77)
                await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            return;
        }
        if (consumeAt is 69 or 75 or 78 or 79 or 80)
        {
            Assert.True(posted.Succeeded, posted.Error);
            var lower = consumeAt is 75 or 79;
            var partial = consumeAt is 78 or 79;
            var issueValue = lower ? 7.2m : 14.4m;
            var consumeValue = partial ? issueValue / 2m : issueValue;
            var outputValue = consumeValue + (partial ? 2m : 4m);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo, consumeValue, outputValue, issueValue);
            await using (var current = await _factory.CreateDbContextAsync())
            {
                var stock = await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, stock.OnHandBaseQty);
                Assert.Equal(lower ? 37.8m : 75.6m, stock.InventoryValue);
                Assert.Equal(21m, (await current.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                if (goodsReceipt) Assert.Equal(5m, (await current.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo,
                partial ? 4m : 8m, partial ? 6m : 12m, 8m);
            var reposted = await PostReceiptAsync();
            Assert.True(reposted.Succeeded, reposted.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo, consumeValue, outputValue, issueValue);
            var removedAgain = await RollbackReceiptAsync();
            Assert.True(removedAgain.Succeeded, removedAgain.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo,
                partial ? 4m : 8m, partial ? 6m : 12m, 8m);
            var reversedIssue = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify issue rollback after repeated reversed-output replay"
            });
            Assert.True(reversedIssue.Succeeded, reversedIssue.Message);
            return;
        }
        if (consumeAt == 70)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            await using (var current = await _factory.CreateDbContextAsync())
            {
                var stock = await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, stock.OnHandBaseQty);
                Assert.Equal(42m, stock.InventoryValue);
                if (goodsReceipt) Assert.Equal(5m, (await current.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            var issueReversal = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify unchanged output after receipt rollback"
            });
            Assert.True(issueReversal.Succeeded, issueReversal.Message);
            return;
        }
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
        if (consumeAt is 26 or 30 or 31 or 38 or 41 or 42 or 67)
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
            if (consumeAt == 67)
            {
                Assert.Contains("reversed finished-good cost", posted.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(16m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(0m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                Assert.Empty(await unchanged.StockReplayRuns.ToArrayAsync());
            }
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

        if (consumeAt == 68)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, businessDate);
            Assert.Equal(9.2m, finished.Value);
            int runCount;
            int postingCount;
            await using (var before = await _factory.CreateDbContextAsync())
            {
                runCount = await before.StockReplayRuns.CountAsync();
                postingCount = await before.StockPostings.CountAsync();
            }
            var finishedFirst = await finished.Service.RollbackAsync(new(finished.Document.Id,
                finished.Document.RowVersion, Guid.NewGuid(), "Attempt finished-goods-first rollback"));
            Assert.False(finishedFirst.Succeeded);
            Assert.Contains("source receipt", finishedFirst.Message, StringComparison.OrdinalIgnoreCase);
            if (!receiptFirst)
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "Attempt output rollback with finished goods still posted"
                });
                Assert.False(outputReversal.Succeeded);
            }
            await using (var unchanged = await _factory.CreateDbContextAsync())
            {
                var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, state.OnHandBaseQty);
                Assert.Equal(75.6m, state.InventoryValue);
                Assert.Equal(21m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
                Assert.Equal(9.2m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
                Assert.Equal(postingCount, await unchanged.StockPostings.CountAsync());
                Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatchNo)).BatchStatus);
                if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await RollbackFinishedGoodAsync(finished);
            var outputReversed = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "Verify source-first rollback"
            });
            Assert.True(outputReversed.Succeeded, outputReversed.Message);
            var issueReversed = await issues.RollbackAsync(new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issue.Data.BatchNo,
                Reason = "Verify source-first rollback"
            });
            Assert.True(issueReversed.Succeeded, issueReversed.Message);
            await using var restored = await _factory.CreateDbContextAsync();
            Assert.Equal(20m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).OnHandBaseQty);
            Assert.Equal(40m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
            if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }

        if (consumeAt == 28)
        {
            await using var checkedDb = await _factory.CreateDbContextAsync();
            var outputPoolId = await checkedDb.ProductionBalLotMovements.Where(m =>
                m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.Produce
                && m.OriginalMovementId == null).Select(m => m.ProductionBalLotId).SingleAsync();
            var originalFg = await checkedDb.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionBalLotId == outputPoolId && m.MovementType == ProductionBalLotMovementTypes.FgReceiptOut
                && m.OriginalMovementId == null);
            var reverseFg = await checkedDb.ProductionBalLotMovements.SingleAsync(m =>
                m.OriginalMovementId == originalFg.Uid && m.MovementType == ProductionBalLotMovementTypes.FgReceiptReversal);
            var effective = await ProductionReplayEffectiveCostReader.ReadAsync(checkedDb, "DEMO", "HQ",
                [originalFg.Uid, reverseFg.Uid]);
            Assert.Equal(6m, effective[originalFg.Uid].TotalCost);
            Assert.Equal(6m, effective[reverseFg.Uid].TotalCost);
        }

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
                    async Task AssertStockAndWipAsync(decimal rawQty, decimal rawValue, decimal fgValue)
                    {
                        await using var checkedDb = await _factory.CreateDbContextAsync();
                        var fact = await checkedDb.ProductionFinishedGoodFactRows.SingleAsync(f =>
                            f.BatchId == unchangedFinishedGood.Document.Id && f.ReversesFactId == null);
                        var rawState = await checkedDb.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                        var fgState = await checkedDb.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
                        var rawPile = await checkedDb.IvBalLocs.SingleAsync(b => b.Id == 58);
                        var fgPile = await checkedDb.IvBalLocs.SingleAsync(b => b.Id == fact.DestinationBalanceId);
                        var fgHistory = await checkedDb.IvTrxHistories.SingleAsync(h => h.Id == fact.InventoryHistoryId);
                        var outputPoolId = await checkedDb.ProductionBalLotMovements.Where(m =>
                            m.ProductionOutputId == consumedOutputId && m.MovementType == ProductionBalLotMovementTypes.Produce
                            && m.OriginalMovementId == null).Select(m => m.ProductionBalLotId).SingleAsync();
                        var wip = await checkedDb.ProductionBalLots.SingleAsync(l => l.Uid == outputPoolId);
                        var wipValue = await checkedDb.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == outputPoolId);
                        Assert.Equal(rawQty, rawState.OnHandBaseQty);
                        Assert.Equal(rawValue, rawState.InventoryValue);
                        Assert.Equal(("DEMO", "HQ", "RM001", "WH01", "BIN-A", "", IvItemStatuses.Active, "EA"),
                            (rawPile.CompanyCode, rawPile.BranchCode, rawPile.ICode, rawPile.WhCode,
                                rawPile.LocCode, rawPile.LotNo, rawPile.IStatus, rawPile.StdUom));
                        Assert.Equal(rawQty, rawPile.StdQty);
                        Assert.Equal(("DEMO", "HQ", "FG001", "WH01"),
                            (fgPile.CompanyCode, fgPile.BranchCode, fgPile.ICode, fgPile.WhCode));
                        Assert.Equal(fgHistory.ToLocation ?? "", fgPile.LocCode);
                        Assert.Equal(fgHistory.ToLotNo ?? "", fgPile.LotNo);
                        Assert.Equal(fgHistory.IStatus, fgPile.IStatus);
                        Assert.Equal(fgHistory.ToStdUom, fgPile.StdUom);
                        Assert.Equal(2m, fgState.OnHandBaseQty);
                        Assert.Equal(fgValue, fgState.InventoryValue);
                        Assert.Equal(2m, fgPile.StdQty);
                        var rawPiles = await checkedDb.IvBalLocs.Where(b =>
                            b.CompanyCode == "DEMO" && b.BranchCode == "HQ" && b.ICode == "RM001").Select(b => b.StdQty).ToArrayAsync();
                        var fgPiles = await checkedDb.IvBalLocs.Where(b =>
                            b.CompanyCode == "DEMO" && b.BranchCode == "HQ" && b.ICode == "FG001").Select(b => b.StdQty).ToArrayAsync();
                        Assert.Equal(rawState.OnHandBaseQty, rawPiles.Sum());
                        Assert.Equal(fgState.OnHandBaseQty, fgPiles.Sum());
                        Assert.Equal(2m, wip.BaseQty);
                        Assert.Equal(fgValue, wip.TotalCost);
                        Assert.Equal(wip.BaseQty, wipValue.TrackedBaseQty);
                        Assert.Equal(wip.TotalCost, wipValue.TrackedValue);
                    }
                    await AssertStockAndWipAsync(16m, 32m, 6m);
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
                    await AssertStockAndWipAsync(16m, 32m, 6m);
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
                        await AssertStockAndWipAsync(21m, consumeAt == 64 ? 37.8m : 75.6m, correctedValue);
                        var removedAgain = await RollbackReceiptAsync();
                        Assert.True(removedAgain.Succeeded, removedAgain.Error);
                        await using var restored = await _factory.CreateDbContextAsync();
                        Assert.Equal(6m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                        Assert.Equal(32m, (await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001")).InventoryValue);
                        if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
                        await AssertStockAndWipAsync(16m, 32m, 6m);
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
                Assert.Equal(6m, finished.Value);
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
        if (consumeAt is 115 or 116 or 117 or 118)
        {
            var maskedService = CreateIssueService(businessDate: businessDate,
                accessOverride: Access(canViewCost: false));
            if (consumeAt is 116 or 117 or 118)
            {
                await using (var corrupt = await _factory.CreateDbContextAsync())
                {
                    if (consumeAt == 116)
                    {
                        var correction = await corrupt.ProductionCostRestatementFacts.SingleAsync();
                        Assert.Equal(1, await corrupt.StockReplayEffects.Where(e =>
                            e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"
                            && e.AffectedEntityId == correction.Id).ExecuteDeleteAsync());
                    }
                    else if (consumeAt == 117)
                    {
                        Assert.Equal(1, await corrupt.StockValuationFacts.Where(f =>
                            f.MovementCode == "PRODUCTION_MATERIAL_OUT"
                            && f.OriginalValuationFactId != null && f.ReversesValuationFactId == null)
                            .ExecuteUpdateAsync(update => update
                                .SetProperty(f => f.CostAmount, 20m)
                                .SetProperty(f => f.BaseCostAmount, 20m)
                                .SetProperty(f => f.UnitCost, 5m)));
                    }
                    else
                    {
                        Assert.Equal(1, await corrupt.ProductionMaterialMovements.Where(m =>
                                m.InventoryBatchNo == issue.Data.BatchNo
                                && m.MovementType == ProductionMaterialMovementTypes.Issue)
                            .ExecuteUpdateAsync(update => update.SetProperty(
                                m => m.ProductionBalLotMovementId, (long?)null)));
                    }
                }
                var invalid = await issues.GetAsync(issue.Data.BatchNo);
                Assert.False(invalid.Succeeded);
                Assert.Contains("cost could not be verified", invalid.Message, StringComparison.OrdinalIgnoreCase);
                var maskedDamaged = await maskedService.GetAsync(issue.Data.BatchNo);
                Assert.True(maskedDamaged.Succeeded, maskedDamaged.Message);
                Assert.Null(Assert.Single(maskedDamaged.Data!.Lines).UnitCost);
                Assert.Null(Assert.Single(maskedDamaged.Data.Lines).TotalCost);
                return;
            }
            var detail = await issues.GetAsync(issue.Data.BatchNo);
            Assert.True(detail.Succeeded, detail.Message);
            var line = Assert.Single(detail.Data!.Lines);
            Assert.Equal(3.6m, line.UnitCost);
            Assert.Equal(14.4m, line.TotalCost);
            var masked = await maskedService.GetAsync(issue.Data.BatchNo);
            Assert.True(masked.Succeeded, masked.Message);
            Assert.Null(Assert.Single(masked.Data!.Lines).UnitCost);
            Assert.Null(Assert.Single(masked.Data.Lines).TotalCost);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            var restored = await issues.GetAsync(issue.Data.BatchNo);
            Assert.True(restored.Succeeded, restored.Message);
            var restoredLine = Assert.Single(restored.Data!.Lines);
            Assert.Equal(2m, restoredLine.UnitCost);
            Assert.Equal(8m, restoredLine.TotalCost);
            return;
        }
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

        async Task RollbackIssueAsync(decimal expectedValue, ProductionMaterialIssueRollbackRequest? rollbackRequest = null)
        {
            var reversed = await issues.RollbackAsync(rollbackRequest ?? new ProductionMaterialIssueRollbackRequest
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
            if (postedProductionValue != 8m)
            {
                var before = await CaptureReplayCountsAsync();
                var rollbackRequest = new ProductionMaterialIssueRollbackRequest
                {
                    PostingRequestId = Guid.NewGuid().ToString("N"),
                    InventoryBatchNo = issue.Data.BatchNo, Reason = "verify safe rollback order"
                };
                var blocked = await issues.RollbackAsync(rollbackRequest);
                Assert.False(blocked.Succeeded);
                Assert.Contains("source receipt", blocked.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await CheckAsync(postedStockQty, postedStockValue, postedProductionValue);
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, removed.Error);
                await CheckAsync(16m, 32m, 8m);
                await RollbackIssueAsync(8m, rollbackRequest);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            }
            else
            {
                await RollbackIssueAsync(postedProductionValue);
                await CheckAsync(25m, standard ? 50m : 90m, 0m);
                var reversed = await RollbackReceiptAsync();
                Assert.True(reversed.Succeeded, reversed.Error);
            }
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
    public Task Backdated_fifo_receipt_can_replay_after_finished_good_reversal_when_cost_is_unchanged(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 13 : 14);

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public Task Backdated_fifo_receipt_rejects_cost_change_after_finished_good_reversal(
        bool goodsReceipt, bool finishedGoodFirst, bool outputFirst) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, !outputFirst,
            finishedGoodFirst ? 29 : 30);

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public Task Backdated_fifo_receipt_replays_changed_cost_after_output_reversal_without_finished_goods(
        bool goodsReceipt, bool lowerCost, bool partialOutput) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true,
            partialOutput ? (lowerCost ? 40 : 39) : (lowerCost ? 36 : 31));

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_fifo_receipt_after_reversed_output_rejects_damage_or_failure(
        bool goodsReceipt, bool injectedFailure) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, injectedFailure ? 38 : 37);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_receipt_replays_output_rolled_back_in_a_later_open_month(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 41);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Backdated_fifo_receipt_rollback_rejects_damaged_reversed_output_correction(
        bool goodsReceipt, bool damageMovement) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, damageMovement ? 43 : 42);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_corrected_issue_rollback_requires_source_first_after_reversed_output(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 44);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_receipt_refuses_already_reversed_issue_and_output(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 45);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_receipt_without_correction_store_refuses_reversed_issue(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 46);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Later_fifo_receipt_allows_reversed_production_issue(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 47);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_receipt_rollback_refuses_reversed_issue_even_when_initially_cost_neutral(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 48);

    [Fact]
    public Task Fifo_earlier_posted_receipt_can_roll_back_after_normal_issue_reversal() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 49);

    [Fact]
    public Task Fifo_earlier_receipt_refuses_damaged_issue_reversal() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 50);

    [Fact]
    public Task Fifo_earlier_receipt_refuses_mismatched_issue_cost_evidence() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 53);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public Task Fifo_backdated_receipt_with_unchanged_reversed_issue_posts_and_rolls_back(
        bool goodsReceipt, bool replayEnabled) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 54 : 55);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_neutral_reversed_issue_does_not_bypass_reversed_output_guard(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 56);

    [Fact]
    public Task Fifo_neutral_reversed_issue_rejects_damaged_cost_evidence() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 58);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Fifo_neutral_reversed_issue_rejects_unsealed_owner(bool goodsReceipt, bool reversalOwner) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, reversalOwner ? 66 : 65);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_neutral_reversed_issue_receipt_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 60);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_neutral_reversed_issue_receipt_failure_rolls_back_all_changes(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 63);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_neutral_reversed_issue_receipt_reposts_quantity_and_price_edit(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 64);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_neutral_reversed_issue_accepts_exact_split_rounding(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 67);

    [Fact]
    public Task Fifo_neutral_reversed_issue_rejects_wrong_rounded_unit_cost() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 68);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_reversed_issue_preserves_earlier_same_time_layers(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 72);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_reversed_issue_replays_consumed_same_time_receipt(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 73);

    [Fact]
    public Task Fifo_reversed_issue_rejects_unproven_same_time_layer_owner() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 74);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_reversed_issue_same_time_receipt_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 75);

    [Fact]
    public Task Fifo_reversed_issue_correction_writes_complete_layer_and_cost_evidence() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 76);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_backdated_receipt_reprices_reversed_issue_and_reposts(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 77);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_changed_reversed_issue_failure_is_atomic_and_retryable(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 78);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_changed_reversed_issue_rollback_respects_qa_hold(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 79);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_changed_reversed_issue_respects_closed_period(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 81);

    [Fact]
    public Task Fifo_changed_reversed_issue_rejects_damaged_split() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 82);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Fifo_changed_reversed_issue_reposts_edited_receipt(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 84);

    [Fact]
    public Task Fifo_reversed_receipt_plan_refuses_damaged_reversal_cost() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 69);

    [Fact]
    public Task Fifo_reversed_receipt_plan_refuses_a_closed_period() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 70);

    [Fact]
    public Task Fifo_reversed_receipt_plan_refuses_a_damaged_production_pool() =>
        BackdatedFifoReceiptAndProductionIssueAsync(false, true, 71);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Fifo_earlier_receipt_reversed_issue_respects_replay_setting(
        bool goodsReceipt, bool replayEnabled) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, replayEnabled ? 51 : 52);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_receipt_allows_unchanged_cost_after_output_reversal_without_finished_goods(
        bool goodsReceipt) => BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 32);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Backdated_fifo_receipt_rollback_respects_qa_stock_holds(bool goodsReceipt) =>
        BackdatedFifoReceiptAndProductionIssueAsync(goodsReceipt, true, 34);

    [Fact]
    public Task Backdated_fifo_goods_receipt_respects_qa_gate_and_restores_allocation() =>
        BackdatedFifoReceiptAndProductionIssueAsync(true, true, 35);

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
        var openingQty = scenario is 67 or 68 ? 5.5m
            : scenario is 49 or 50 or 53 or 54 or 55 or 56 or 58 or 60 or 62 or 63 or 64 or 65 or 66 ? 5m : 2m;
        await SeedBalanceAsync(59, openingQty);
        var receiptPrice = scenario is 36 or 40 ? 1m
            : scenario is 3 or 13 or 14 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 32 or 48 ? 2m : 10m;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedCompanyAsync(db);
            db.MsUoms.Add(new() { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
            db.IvClasses.Add(new() { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
            (await db.IvStockMasters.SingleAsync(s => s.ICode == "RM001")).IClassCode = "RAW";
            if (scenario == 35)
                db.QaPolicies.Add(new QaPolicy
                {
                    CompanyCode = "DEMO", Version = 1, Enabled = true, IsPublished = true,
                    IncomingInspectionEnabled = true, CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
                });
            if (scenario is 11 or 12 or 13 or 14 or 15 or 16 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30)
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
                [new FifoLayerSeed(openingQty, scenario is 67 or 68 ? 0.333333m : 2m, new DateTime(2026, 9, 1))]);
        }
        if (scenario is 67 or 68)
        {
            var earlierIssue = InventoryLedgerTestFixture.CreateMiscIssue(
                _factory, new DateTime(2026, 10, 3), enableBackdatedReplay: true);
            var saved = await earlierIssue.SaveNewAsync(new IvMiscIssueSaveRequest
            {
                TrxDate = new DateTime(2026, 9, 25),
                Lines = [new IvMiscIssueLineRequest
                {
                    FromBalLocId = 59, ICode = "RM001", FrWarehouse = "WH01",
                    FrLocation = "BIN-A", FrLotNo = string.Empty, Quantity = 0.1m,
                    Uom = "EA", IClassCode = "RAW", IStatus = IvItemStatuses.Active,
                    UnitPrice = 0.333333m
                }]
            });
            Assert.True(saved.Succeeded, saved.ErrorMessage);
            var firstIssue = await earlierIssue.PostAsync([saved.BatchNo]);
            Assert.True(firstIssue.Succeeded, firstIssue.ErrorMessage);
        }
        if (scenario == 23)
            await SeedTraceablePredecessorAsync(graph);
        var qaPhysical = scenario is 34 or 60 or 75 or 79 ? new QaPhysicalHoldAuthorizationService() : null;
        var qaReceipt = scenario == 35 ? new QaReceiptAuthorizationService() : null;
        var laterReceiptDate = DateTime.UtcNow.Date.AddDays(2);
        var receiptBusinessDate = scenario == 41 ? new DateTime(2026, 11, 3)
            : scenario == 47 ? laterReceiptDate : new DateTime(2026, 10, 3);
        var receiptDate = scenario == 47 ? laterReceiptDate : new DateTime(2026, 10, 1);
        var receipts = InventoryLedgerTestFixture.CreateMiscReceipt(
            _factory, receiptBusinessDate, enableBackdatedReplay: scenario is not (52 or 55), qaPhysical: qaPhysical);
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
        var sourceBatch = 0;
        IvGoodsReceiptService? sourceGoods = null;
        if ((scenario is 51 or 52 or 73) && goodsReceipt)
        {
            var tenant = InventoryTenantTestHelper.CreateTenantContext();
            sourceGoods = new IvGoodsReceiptService(_factory, tenant, Access(), new RunningNumberService(),
                new FixedCurrentDateService(receiptBusinessDate), new IvStockMasterRepository(_factory),
                new IvStockCommonRepository(_factory), new IvStockTransactionRepository(),
                new IvStockPostingRepository(), InventoryLedgerTestFixture.CreateInventoryPosting(
                    _factory, enableBackdatedReplay: scenario != 52), NullLogger<IvGoodsReceiptService>.Instance,
                approval: TestDisabledProcurementApprovalGuard.Instance);
            var source = await sourceGoods.SaveNewAsync(new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive, TrxDate = new DateTime(2026, 10, 1),
                Lines = [new IvGoodsReceiptLineRequest { PoNo = "PO-IP-FIFO", PoRelNo = 1, PoLineNo = 1,
                    ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 5m }]
            });
            Assert.True(source.Succeeded, source.ErrorMessage);
            sourceBatch = source.BatchNo;
            var sourcePost = await sourceGoods.PostAsync([sourceBatch]);
            Assert.True(sourcePost.Succeeded, sourcePost.ErrorMessage);
        }
        else if (scenario is 51 or 52 or 73)
        {
            var source = await receipts.SaveNewAsync(Receipt(new DateTime(2026, 10, 1), 5m, 10m));
            Assert.True(source.Succeeded, source.ErrorMessage);
            sourceBatch = source.BatchNo;
            var sourcePost = await receipts.PostAsync([sourceBatch]);
            Assert.True(sourcePost.Succeeded, sourcePost.ErrorMessage);
        }
        var laterReceipt = await receipts.SaveNewAsync(Receipt(
            new DateTime(2026, 10, scenario is 72 or 73 or 74 or 75 ? 1 : 2), 18m, 2m));
        Assert.True(laterReceipt.Succeeded, laterReceipt.ErrorMessage);
        var laterPosted = await receipts.PostAsync([laterReceipt.BatchNo]);
        Assert.True(laterPosted.Succeeded, laterPosted.ErrorMessage);
        if (scenario is 51 or 52 or 73)
        {
            await using var evidence = await _factory.CreateDbContextAsync();
            var balance = await evidence.IvBalLocs.SingleAsync(b => b.Id == 59);
            balance.UnitPrice = 2m;
            balance.Cost = 2m;
            balance.PriceEvidence = ProductionLedgerTestFixture.TestPriceEvidence;
            await evidence.SaveChangesAsync();
        }

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
        var consumedQty = scenario is 8 or 9 or 19 or 20 or 25 or 26 or 39 or 40 ? 2m : 4m;
        async Task ConsumeIssueAsync()
        {
            var created = await outputs.CreateAsync(OutputRequest(
                graph.Process2Id, consumedQty, new DateTime(2026, 10, 3, 13, 0, 0)));
            Assert.True(created.Succeeded, created.Message);
            var result = await outputs.PostAsync(created.Data!.Uid);
            Assert.True(result.Succeeded, result.Message);
            consumedOutputId = created.Data.Uid;
        }
        if (scenario is 6 or 8 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 56)
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
                new FixedCurrentDateService(receiptBusinessDate),
                new IvStockMasterRepository(_factory), new IvStockCommonRepository(_factory),
                new IvStockTransactionRepository(), new IvStockPostingRepository(),
                InventoryLedgerTestFixture.CreateInventoryPosting(_factory, enableBackdatedReplay: scenario != 55,
                    qaReceipt: qaReceipt, qaPhysical: qaPhysical),
                NullLogger<IvGoodsReceiptService>.Instance, approval: TestDisabledProcurementApprovalGuard.Instance);
            var saved = await goods.SaveNewAsync(new IvGoodsReceiptSaveRequest
            {
                TrxType = IvTrxTypes.GoodsReceive, TrxDate = receiptDate,
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
            var saved = await receipts.SaveNewAsync(Receipt(receiptDate, 5m, receiptPrice));
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
        async Task SetFifoCorrectionFailureAsync(bool enabled)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var sql = enabled
                ? db.Database.IsSqlServer()
                    ? "CREATE OR ALTER TRIGGER dbo.TestRejectFifoReversedSplit ON dbo.StockFifoReversedIssueSplit AFTER INSERT AS BEGIN SET NOCOUNT ON; THROW 51997, 'injected FIFO correction failure', 1; END"
                    : "CREATE TRIGGER TestRejectFifoReversedSplit BEFORE INSERT ON StockFifoReversedIssueSplit BEGIN SELECT RAISE(ABORT, 'injected FIFO correction failure'); END;"
                : db.Database.IsSqlServer() ? "DROP TRIGGER dbo.TestRejectFifoReversedSplit" : "DROP TRIGGER TestRejectFifoReversedSplit";
            await db.Database.ExecuteSqlRawAsync(sql);
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
        async Task RollbackIssueAsync(decimal expectedValue, ProductionMaterialIssueRollbackRequest? rollbackRequest = null)
        {
            var reversed = await issues.RollbackAsync(rollbackRequest ?? new ProductionMaterialIssueRollbackRequest
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
        async Task AssertIsolatedFifoReversedProjectionAsync(int sourceReceiptBatch, bool removal)
        {
            await using var db = await _factory.CreateDbContextAsync();
            var prepared = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                db, "DEMO", "HQ", sourceReceiptBatch, removal, requireTransaction: false);
            Assert.NotNull(prepared);
            Assert.Null(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                db, "DEMO", "HQ", sourceReceiptBatch, removal));
            await using var tx = await db.Database.BeginTransactionAsync();
            await new BranchStockTransactionLock().AcquireAsync(db, "DEMO", "HQ", CancellationToken.None);
            var locked = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                db, "DEMO", "HQ", sourceReceiptBatch, removal);
            Assert.NotNull(locked);
            Assert.Equal(prepared.PlanHash, locked.PlanHash);
            var permit = await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(db, prepared);
            Assert.NotNull(permit);
            if (!removal)
            {
                Assert.Equal(1, await db.IvTrxBatchDetails.Where(d => d.BatchNo == sourceReceiptBatch
                        && d.CompanyCode == "DEMO" && d.BranchCode == "HQ")
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.UnitPrice, 11m)));
                var changed = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    db, "DEMO", "HQ", sourceReceiptBatch, removal);
                Assert.True(changed is null || changed.PlanHash != prepared.PlanHash,
                    "A changed receipt must not retain the prepared FIFO plan.");
                Assert.Null(await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(db, prepared));
                Assert.Equal(1, await db.IvTrxBatchDetails.Where(d => d.BatchNo == sourceReceiptBatch
                        && d.CompanyCode == "DEMO" && d.BranchCode == "HQ")
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.UnitPrice, 10m)));
            }
            if (goodsReceipt)
            {
                Assert.Equal(1, await db.PoOrderDetails.Where(d => d.CompanyCode == "DEMO" && d.BranchCode == "HQ"
                        && d.PoNo == "PO-IP-FIFO" && d.Line == 1)
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.RecvQty, removal ? 6m : 1m)));
                var changedOrder = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    db, "DEMO", "HQ", sourceReceiptBatch, removal);
                Assert.True(changedOrder is null || changedOrder.PlanHash != prepared.PlanHash,
                    "A changed purchase order must not retain the prepared FIFO plan.");
                Assert.Null(await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(db, prepared));
                Assert.Equal(1, await db.PoOrderDetails.Where(d => d.CompanyCode == "DEMO" && d.BranchCode == "HQ"
                        && d.PoNo == "PO-IP-FIFO" && d.Line == 1)
                    .ExecuteUpdateAsync(update => update.SetProperty(d => d.RecvQty, removal ? 5m : 0m)));
            }
            permit = await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(db, prepared);
            Assert.NotNull(permit);
            var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var command = await InventoryStockPostingCommandFactory.CreateAsync(db, "DEMO", "HQ",
                sourceReceiptBatch, removal, prepared.ReceiptEffectiveAt, CancellationToken.None);
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.NotNull(begin.Context);
            var context = begin.Context!;
            try
            {
                context.CostMethod = StockCostMethods.Fifo;
                await permit.ValidateForRootAsync(context);
                var originalNo = context.Posting.SourceDocumentNo;
                context.Posting.SourceDocumentNo = "FOREIGN";
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    (await Assert.ThrowsAsync<StockLedgerException>(() => permit.ValidateForRootAsync(context))).Error.Code);
                context.Posting.SourceDocumentNo = originalNo;
                if (!removal)
                {
                    Assert.Equal(1, await db.IvTrxBatchDetails.Where(d => d.BatchNo == sourceReceiptBatch
                            && d.CompanyCode == "DEMO" && d.BranchCode == "HQ")
                        .ExecuteUpdateAsync(update => update.SetProperty(d => d.UnitPrice, 11m)));
                    Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                        (await Assert.ThrowsAsync<StockLedgerException>(() => permit.ValidateForRootAsync(context))).Error.Code);
                    Assert.Equal(1, await db.IvTrxBatchDetails.Where(d => d.BatchNo == sourceReceiptBatch
                            && d.CompanyCode == "DEMO" && d.BranchCode == "HQ")
                        .ExecuteUpdateAsync(update => update.SetProperty(d => d.UnitPrice, 10m)));
                }
                if (goodsReceipt)
                {
                    Assert.Equal(1, await db.PoOrderDetails.Where(d => d.CompanyCode == "DEMO" && d.BranchCode == "HQ"
                            && d.PoNo == "PO-IP-FIFO" && d.Line == 1)
                        .ExecuteUpdateAsync(update => update.SetProperty(d => d.RecvQty, removal ? 6m : 1m)));
                    Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                        (await Assert.ThrowsAsync<StockLedgerException>(() => permit.ValidateForRootAsync(context))).Error.Code);
                    Assert.Equal(1, await db.PoOrderDetails.Where(d => d.CompanyCode == "DEMO" && d.BranchCode == "HQ"
                            && d.PoNo == "PO-IP-FIFO" && d.Line == 1)
                        .ExecuteUpdateAsync(update => update.SetProperty(d => d.RecvQty, removal ? 5m : 0m)));
                }
                await permit.ValidateForRootAsync(context);
            }
            finally
            {
                await coordinator.AbortInTransactionAsync(context);
            }
            if (db.Database.IsSqlServer())
                Assert.Equal(StockLedgerErrorCodes.StaleDocument,
                    (await Assert.ThrowsAsync<StockLedgerException>(() => permit.ValidateForRootAsync(context))).Error.Code);
            await tx.RollbackAsync();
            Assert.NotNull(await Record.ExceptionAsync(() => permit.ValidateForRootAsync(context)));
            await tx.DisposeAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => permit.ValidateForRootAsync(context));
            var projection = locked.Projection;
            Assert.Equal(removal ? 24m : 8m, projection.OriginalValue);
            Assert.Equal(removal ? 8m : 24m, projection.ProjectedValue);
            Assert.Equal(4m, projection.OriginalSplits.Sum(s => s.Quantity));
            Assert.Equal(4m, projection.ProjectedSplits.Sum(s => s.Quantity));
            Assert.Equal(2, projection.OriginalSplits.Count);
            Assert.Equal(2, projection.ProjectedSplits.Count);
        }
        async Task AssertFifoCorrectionWithinRolledBackRootAsync()
        {
            await using var db = await _factory.CreateDbContextAsync();
            var prepared = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                db, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false);
            Assert.NotNull(prepared);
            var physical = await StockReplayInventoryCandidatePlanner.BuildSingleAsync(db, "DEMO", "HQ", 59,
                prepared.ReceiptEffectiveAt, prepared.ReceiptQuantity, CancellationToken.None);
            var financial = await StockReplayFifoInsertionPlanner.BuildMiscReceiptAsync(db, "DEMO", "HQ",
                receiptBatch, physical);
            Assert.Equal(prepared.PhysicalPlanHash, physical.PlanHash);
            Assert.Equal(prepared.FinancialPlanHash, financial.PlanHash);
            await using var tx = await db.Database.BeginTransactionAsync();
            await new BranchStockTransactionLock().AcquireAsync(db, "DEMO", "HQ", CancellationToken.None);
            var permit = await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(db, prepared);
            Assert.NotNull(permit);
            var coordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var command = await InventoryStockPostingCommandFactory.CreateAsync(db, "DEMO", "HQ",
                receiptBatch, false, prepared.ReceiptEffectiveAt, CancellationToken.None);
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.NotNull(begin.Context);
            var context = begin.Context!;
            context.CostMethod = StockCostMethods.Fifo;
            await permit.ActivateForCorrectionAsync(context);
            var fifoPermit = await StockReplayFifoInsertionPlanner.PrepareMiscReceiptWithinRootAsync(
                context, financial, receiptBatch);
            var receipt = await StockReplayFifoMrRootWriter.InsertAsync(context, financial, fifoPermit, receiptBatch);
            await IvInventoryPostingService.AppendFifoReversedIssueCorrectionAsync(context, permit, receipt.Receipt.LayerId,
                CancellationToken.None);
            await Assert.ThrowsAsync<StockLedgerException>(() => IvInventoryPostingService.AppendFifoReversedIssueCorrectionAsync(
                context, permit, receipt.Receipt.LayerId, CancellationToken.None));
            await coordinator.CompleteReplayInTransactionAsync(context, receipt.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(context, financial, fifoPermit, receipt.Receipt);

            var issueHistory = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == prepared.IssueHistoryId);
            var reverseHistory = await db.IvTrxHistories.AsNoTracking().SingleAsync(h => h.Id == prepared.ReversalHistoryId);
            var issueMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == prepared.IssueMovementId);
            var reverseMovement = await db.ProductionBalLotMovements.AsNoTracking().SingleAsync(m => m.Uid == prepared.ReversalMovementId);
            var issueFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.InventoryHistoryId == issueHistory.Id).ToArrayAsync();
            var factIds = issueFacts.Select(f => f.Id).ToArray();
            var rows = await db.StockFifoLayerConsumptions.AsNoTracking().Where(c => factIds.Contains(c.IssueValuationFactId)).ToArrayAsync();
            var baseline = rows.Select(c => new StockReplayFifoConsumptionPosition(c.Id, c.IssueValuationFactId,
                c.FifoLayerId, c.ConsumedQty, c.ConsumedValue, c.SplitOrdinal, true, prepared.Projection.IssueKey)).ToArray();
            var persistedGeneration = await db.StockFifoReversedIssueGenerations.AsNoTracking().SingleAsync();
            var persistedSplits = await db.StockFifoReversedIssueSplits.AsNoTracking().ToArrayAsync();
            Assert.Equal(persistedGeneration.SplitHash,
                StockReplayFifoReversedIssueGenerationReader.SplitHash(persistedGeneration, persistedSplits));
            var effective = await StockReplayFifoReversedIssueGenerationReader.ReadAsync(db, "DEMO", "HQ",
                issueHistory, reverseHistory, issueMovement, reverseMovement, baseline, CancellationToken.None);
            Assert.NotNull(effective);
            Assert.Equal(24m, effective.Generation.TotalCost);
            Assert.Equal(4m, effective.Splits.Sum(s => s.ConsumedQty));
            Assert.Equal(24m, effective.Splits.Sum(s => s.ConsumedValue));
            Assert.Contains(effective.Splits, s => s.FifoLayerId == receipt.Receipt.LayerId && s.ConsumedQty == 2m && s.ConsumedValue == 20m);
            var state = await db.StockCostStates.AsNoTracking().SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(25m, state.OnHandBaseQty);
            Assert.Equal(90m, state.InventoryValue);
            var asOfFacts = await db.StockValuationFacts.AsNoTracking().Where(f => f.ItemCode == "RM001"
                && f.EffectiveAt <= issueHistory.TrxDtTime && f.StockPosting!.SealedAtUtc != null).ToArrayAsync();
            Assert.Equal(21m, asOfFacts.Sum(f => f.SignedQty));
            Assert.Equal(66m, asOfFacts.Sum(f => f.SignedValue));
            await tx.CommitAsync();
            await tx.DisposeAsync();

            await using var removalDb = await _factory.CreateDbContextAsync();
            var removal = await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                removalDb, "DEMO", "HQ", receiptBatch, removal: true, requireTransaction: false);
            Assert.NotNull(removal);
            Assert.Equal(24m, removal.Projection.OriginalValue);
            Assert.Equal(8m, removal.Projection.ProjectedValue);
            Assert.Equal(effective.Generation.Id, removal.PreviousGenerationId);
            var source = await removalDb.IvTrxHistories.AsNoTracking().SingleAsync(h =>
                h.BatchNo == receiptBatch && h.EntryRole == "ORIGINAL");
            var sourceOwner = await removalDb.StockPostings.AsNoTracking().SingleAsync(p => p.Id == source.StockPostingId);
            var key = new StockReplayEventKey(source.TrxDtTime, StockReplayEventKey.OrdinaryClass,
                sourceOwner.PostingSequence, $"H{source.Id:D10}", 0);
            var physicalRemoval = await StockReplayPhysicalPlanner.BuildRemovalAsync(removalDb, "DEMO", "HQ", 59,
                new StockReplayPhysicalEvent(source.Id, key, source.ToStdQty!.Value));
            var financialRemoval = await StockReplayFifoRemovalPlanner.BuildAsync(removalDb, "DEMO", "HQ", "RM001", physicalRemoval);
            Assert.Equal(removal.FinancialPlanHash, financialRemoval.PlanHash);
            await using var removalTx = await removalDb.Database.BeginTransactionAsync();
            await new BranchStockTransactionLock().AcquireAsync(removalDb, "DEMO", "HQ", CancellationToken.None);
            var removalPermit = await IvInventoryPostingService.FifoReversedReceiptPermit.PrepareAsync(removalDb, removal);
            Assert.NotNull(removalPermit);
            var removalCoordinator = InventoryLedgerTestFixture.CreateCoordinator(_factory);
            var removalCommand = await InventoryStockPostingCommandFactory.CreateAsync(removalDb, "DEMO", "HQ",
                receiptBatch, true, removal.ReceiptEffectiveAt, CancellationToken.None);
            var removalBegin = await removalCoordinator.BeginInTransactionAsync(removalDb, removalCommand);
            Assert.NotNull(removalBegin.Context);
            var removalContext = removalBegin.Context!;
            removalContext.CostMethod = StockCostMethods.Fifo;
            await removalPermit.ActivateForCorrectionAsync(removalContext);
            var removalFifoPermit = await StockReplayFifoRemovalPlanner.PrepareWithinRootAsync(removalContext, financialRemoval);
            var removed = await StockReplayFifoMrRemovalRootWriter.RemoveAsync(removalContext, financialRemoval,
                removalFifoPermit, receiptBatch);
            await IvInventoryPostingService.AppendFifoReversedIssueCorrectionAsync(removalContext, removalPermit,
                insertedReceiptLayerId: null, CancellationToken.None);
            await removalCoordinator.CompleteReplayInTransactionAsync(removalContext, removed.RequiredActiveHistoryIds);
            await StockReplayFifoReconciliationGate.VerifySealedWithinRootAsync(removalContext, financialRemoval, removalFifoPermit);
            var restored = await StockReplayFifoReversedIssueGenerationReader.ReadAsync(removalDb, "DEMO", "HQ",
                issueHistory, reverseHistory, issueMovement, reverseMovement, baseline, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.Equal(8m, restored.Generation.TotalCost);
            Assert.Equal(4m, restored.Splits.Sum(s => s.ConsumedQty));
            Assert.Equal(8m, restored.Splits.Sum(s => s.ConsumedValue));
            Assert.Equal(effective.Generation.Id, restored.Generation.PreviousGenerationId);
            var restoredStock = await removalDb.StockCostStates.AsNoTracking().SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, restoredStock.OnHandBaseQty);
            Assert.Equal(40m, restoredStock.InventoryValue);
            var restoredFacts = await removalDb.StockValuationFacts.AsNoTracking().Where(f => f.ItemCode == "RM001"
                && f.EffectiveAt <= issueHistory.TrxDtTime && f.StockPosting!.SealedAtUtc != null).ToArrayAsync();
            Assert.Equal(16m, restoredFacts.Sum(f => f.SignedQty));
            Assert.Equal(32m, restoredFacts.Sum(f => f.SignedValue));
            await removalTx.RollbackAsync();
        }
        if (scenario is 51 or 52 or 73)
        {
            await RollbackIssueAsync(24m);
            await InstallReversedIssueSqlGuardsAsync();
            await AssertIsolatedFifoReversedProjectionAsync(sourceBatch, removal: true);
            if (scenario == 52)
            {
                var before = await CaptureReplayCountsAsync();
                var succeeded = sourceGoods is not null
                    ? (await sourceGoods.RollbackAsync([sourceBatch])).Succeeded
                    : (await receipts.RollbackAsync([sourceBatch])).Succeeded;
                Assert.False(succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 24m);
                return;
            }
            async Task<(bool Succeeded, string? Error)> ChangeSourceAsync(bool post)
            {
                if (sourceGoods is not null)
                {
                    var result = post ? await sourceGoods.PostAsync([sourceBatch]) : await sourceGoods.RollbackAsync([sourceBatch]);
                    return (result.Succeeded, result.ErrorMessage);
                }
                var resultMr = post ? await receipts.PostAsync([sourceBatch]) : await receipts.RollbackAsync([sourceBatch]);
                return (resultMr.Succeeded, resultMr.ErrorMessage);
            }
            for (var cycle = 0; cycle < 2; cycle++)
            {
                var removed = await ChangeSourceAsync(post: false);
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using (var restored = await _factory.CreateDbContextAsync())
                {
                    var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(20m, state.OnHandBaseQty);
                    Assert.Equal(40m, state.InventoryValue);
                    Assert.Equal(20m, (await restored.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
                }
                var reposted = await ChangeSourceAsync(post: true);
                Assert.True(reposted.Succeeded, $"Repost cycle {cycle}: {reposted.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, scenario == 73 ? 8m : 24m);
                await using var postedAgain = await _factory.CreateDbContextAsync();
                var postedState = await postedAgain.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(25m, postedState.OnHandBaseQty);
                Assert.Equal(90m, postedState.InventoryValue);
                if (goodsReceipt) Assert.Equal(5m, (await postedAgain.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (scenario is 49 or 50 or 53)
        {
            await RollbackIssueAsync(8m);
            await InstallReversedIssueSqlGuardsAsync();
            if (scenario is 50 or 53)
            {
                if (scenario == 50)
                    await DamageIssueReversalCostAsync(issue.Data.BatchNo);
                else
                {
                    await using var corrupt = await _factory.CreateDbContextAsync();
                    Assert.Equal(1, await corrupt.ProductionMaterialMovements.Where(m =>
                            m.InventoryBatchNo == issue.Data.BatchNo
                            && m.MovementType == ProductionMaterialMovementTypes.Issue)
                        .ExecuteUpdateAsync(update => update.SetProperty(m => m.TotalCost, 9m)));
                }
                var before = await CaptureReplayCountsAsync();
                var blocked = await receipts.RollbackAsync([laterReceipt.BatchNo]);
                Assert.False(blocked.Succeeded);
                Assert.Contains("reversed Production issue", blocked.ErrorMessage, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                await using var unchanged = await _factory.CreateDbContextAsync();
                var stock = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(23m, stock.OnHandBaseQty);
                Assert.Equal(46m, stock.InventoryValue);
                Assert.Equal(IvBatchStatuses.Posted,
                    (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == laterReceipt.BatchNo)).BatchStatus);
                return;
            }
            var removed = await receipts.RollbackAsync([laterReceipt.BatchNo]);
            Assert.True(removed.Succeeded, removed.ErrorMessage);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using var restored = await _factory.CreateDbContextAsync();
            var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(5m, state.OnHandBaseQty);
            Assert.Equal(10m, state.InventoryValue);
            return;
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
        if (scenario is 13 or 15 or 16 or 19 or 29)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
            Assert.Equal(6m, finished.Value);
            await RollbackFinishedGoodAsync(finished);
            if (scenario is 15 or 16)
                await DamageFinishedGoodReversalAsync(finished.Document.Id, scenario == 16);
            if (scenario == 29 && !receiptFirst)
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "Verify later backdated receipt is rejected atomically"
                });
                Assert.True(outputReversal.Succeeded, outputReversal.Message);
            }
        }
        if (scenario is 31 or 32 or 36 or 37 or 38 or 39 or 40 or 41 or 42 or 43 or 44 or 45 or 56)
        {
            var rollbackOutputs = scenario == 41 ? CreateOutputService(receiptBusinessDate) : outputs;
            var reversed = await rollbackOutputs.RollbackAsync(new ProductionOutputRollbackRequest
            {
                OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                Reason = "Verify replay after output reversal without finished goods"
            });
            Assert.True(reversed.Succeeded, reversed.Message);
            if (scenario is 37 or 38)
                await DamageOrRejectReversedOutputAsync(consumedOutputId, scenario == 38);
        }
        if (scenario is 45 or 46 or 47 or 54 or 55 or 56 or 58 or 60 or 62 or 63 or 64 or 65 or 66 or 67 or 68 or 69 or 70 or 71 or 72 or 74 or 75 or 76 or 77 or 78 or 79 or 80 or 81 or 82 or 83 or 84)
        {
            await RollbackIssueAsync(scenario is 67 or 68 ? 1.333333m : 8m);
            if (scenario is 46 or 54 or 55 or 60 or 62 or 63 or 64 or 67 or 72 or 75 or 76 or 77 or 78 or 79 or 80 or 81 or 82 or 83 or 84) await InstallReversedIssueSqlGuardsAsync();
            if (scenario is 45 or 56)
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            else
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, scenario is 67 or 68 ? 1.333333m : 8m);
            if (scenario == 58) await DamageIssueReversalCostAsync(issue.Data.BatchNo);
            if (scenario == 46)
            {
                await AssertIsolatedFifoReversedProjectionAsync(receiptBatch, removal: false);
                await using var noStore = await _factory.CreateDbContextAsync();
                await noStore.Database.ExecuteSqlRawAsync("DROP TABLE StockFifoReversedIssueSplit");
                await noStore.Database.ExecuteSqlRawAsync("DROP TABLE StockFifoReversedIssueGeneration");
            }
            if (scenario == 76)
            {
                await AssertFifoCorrectionWithinRolledBackRootAsync();
                return;
            }
            if (scenario == 69)
            {
                await using var evidence = await _factory.CreateDbContextAsync();
                Assert.NotNull(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
                await DamageIssueReversalCostAsync(issue.Data.BatchNo);
                Assert.Null(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
            }
            if (scenario == 70)
            {
                await using var evidence = await _factory.CreateDbContextAsync();
                Assert.NotNull(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
                var period = new IvPeriodCloseHdr
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", PeriodFrom = new DateTime(2026, 10, 1),
                    PeriodTo = new DateTime(2026, 10, 31), Status = IvPeriodCloseStatuses.Closed, RowVersion = [1]
                };
                evidence.IvPeriodCloseHdrs.Add(period);
                await evidence.SaveChangesAsync();
                Assert.Null(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
                var before = await CaptureReplayCountsAsync();
                var denied = await PostReceiptAsync();
                Assert.False(denied.Succeeded);
                Assert.Equal(before, await CaptureReplayCountsAsync());
                period.Status = IvPeriodCloseStatuses.Reopened;
                await evidence.SaveChangesAsync();
            }
            if (scenario == 71)
            {
                await using var evidence = await _factory.CreateDbContextAsync();
                Assert.NotNull(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
                var material = await evidence.ProductionMaterialMovements.SingleAsync(m =>
                    m.InventoryBatchNo == issue.Data.BatchNo && m.MovementType == ProductionMaterialMovementTypes.Issue);
                var pool = await evidence.ProductionPoolValuationRows.SingleAsync(p =>
                    p.ProductionBalLotId == material.ProductionBalLotId);
                pool.TrackedValue = 1m;
                await evidence.SaveChangesAsync();
                Assert.Null(await IvInventoryPostingService.PlanIsolatedFifoReversedReceiptAsync(
                    evidence, "DEMO", "HQ", receiptBatch, removal: false, requireTransaction: false));
            }
            if (scenario == 68)
            {
                await using var corrupt = await _factory.CreateDbContextAsync();
                var historyIds = await corrupt.IvTrxHistories.Where(h => h.BatchNo == issue.Data.BatchNo
                        && h.TrxType == IvTrxTypes.IssueToProduction)
                    .Select(h => h.Id).ToArrayAsync();
                Assert.Equal(2, await corrupt.StockValuationFacts.Where(f => f.InventoryHistoryId.HasValue
                        && historyIds.Contains(f.InventoryHistoryId.Value))
                    .ExecuteUpdateAsync(p => p.SetProperty(f => f.UnitCost, 0.333334m)));
            }
            if (scenario is 65 or 66)
            {
                await using var corrupt = await _factory.CreateDbContextAsync();
                var ownerId = await corrupt.IvTrxHistories.Where(h => h.BatchNo == issue.Data.BatchNo
                        && h.EntryRole == (scenario == 65 ? "ORIGINAL" : "REVERSAL"))
                    .Select(h => h.StockPostingId).SingleAsync();
                Assert.Equal(1, await corrupt.StockPostings.Where(p => p.Id == ownerId)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.SealedAtUtc, (DateTime?)null)));
            }
            if (scenario == 74)
            {
                await using var corrupt = await _factory.CreateDbContextAsync();
                var ownerId = await corrupt.IvTrxHistories.Where(h => h.BatchNo == laterReceipt.BatchNo
                        && h.EntryRole == "ORIGINAL")
                    .Select(h => h.StockPostingId).SingleAsync();
                Assert.Equal(1, await corrupt.StockPostings.Where(p => p.Id == ownerId)
                    .ExecuteUpdateAsync(p => p.SetProperty(x => x.SourceDocumentId, "FOREIGN")));
            }
        }
        var beforeRejectedReceipt = scenario is 45 or 46 or 56 or 58 or 65 or 66 or 68 or 69 or 71 or 74 ? await CaptureReplayCountsAsync() : [];
        if (scenario == 63)
        {
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var trigger = db.Database.IsSqlServer()
                    ? "CREATE OR ALTER TRIGGER dbo.TestRejectNeutralFifoLayer ON dbo.StockFifoLayer AFTER INSERT AS BEGIN SET NOCOUNT ON; THROW 51998, 'injected FIFO receipt failure', 1; END"
                    : "CREATE TRIGGER TestRejectNeutralFifoLayer BEFORE INSERT ON StockFifoLayer BEGIN SELECT RAISE(ABORT, 'injected FIFO receipt failure'); END;";
                await db.Database.ExecuteSqlRawAsync(trigger);
            }
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(23m, state.OnHandBaseQty);
                Assert.Equal(46m, state.InventoryValue);
                Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatch).ToArrayAsync());
                if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                if (db.Database.IsSqlServer())
                {
                    await db.Database.OpenConnectionAsync();
                    await using var command = db.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                    Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                }
                await db.Database.ExecuteSqlRawAsync(db.Database.IsSqlServer()
                    ? "DROP TRIGGER dbo.TestRejectNeutralFifoLayer" : "DROP TRIGGER TestRejectNeutralFifoLayer");
            }
        }
        if (scenario == 78)
        {
            await SetFifoCorrectionFailureAsync(enabled: true);
            var before = await CaptureReplayCountsAsync();
            var failed = await PostReceiptAsync();
            Assert.False(failed.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using (var unchanged = await _factory.CreateDbContextAsync())
            {
                Assert.Empty(await unchanged.StockFifoReversedIssueGenerations.ToArrayAsync());
                Assert.Empty(await unchanged.StockFifoReversedIssueSplits.ToArrayAsync());
                Assert.Equal(20m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                if (unchanged.Database.IsSqlServer())
                {
                    await unchanged.Database.OpenConnectionAsync();
                    await using var command = unchanged.Database.GetDbConnection().CreateCommand();
                    command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                    Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                }
            }
            await SetFifoCorrectionFailureAsync(enabled: false);
        }
        if (scenario == 81)
        {
            await using var periodDb = await _factory.CreateDbContextAsync();
            var closed = new IvPeriodCloseHdr
            {
                CompanyCode = "DEMO", BranchCode = "HQ", PeriodFrom = new DateTime(2026, 10, 1),
                PeriodTo = new DateTime(2026, 10, 31), Status = IvPeriodCloseStatuses.Closed, RowVersion = [1]
            };
            periodDb.IvPeriodCloseHdrs.Add(closed);
            await periodDb.SaveChangesAsync();
            var before = await CaptureReplayCountsAsync();
            var refused = await PostReceiptAsync();
            Assert.False(refused.Succeeded);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            closed.Status = IvPeriodCloseStatuses.Reopened;
            await periodDb.SaveChangesAsync();
        }
        if (scenario == 83)
        {
            Assert.False(goodsReceipt);
            Assert.NotNull(_fifoPostingBarrier);
            _fifoPostingBarrier.Arm();
            var stalePost = PostReceiptAsync();
            try
            {
                await _fifoPostingBarrier.Reached.WaitAsync(TimeSpan.FromSeconds(15));
                var edited = await receipts.UpdateAsync(receiptBatch, Receipt(receiptDate, 5m, 11m));
                Assert.True(edited.Succeeded, edited.ErrorMessage);
            }
            finally
            {
                _fifoPostingBarrier.Release();
            }
            var staleResult = await stalePost;
            Assert.False(staleResult.Succeeded);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            await using var unchanged = await _factory.CreateDbContextAsync();
            Assert.Empty(await unchanged.StockFifoReversedIssueGenerations.ToArrayAsync());
            Assert.Equal(20m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
        }
        (bool Succeeded, string? Error) posted;
        if (scenario is 62 or 80)
        {
            var attempts = await Task.WhenAll(PostReceiptAsync(), PostReceiptAsync());
            Assert.True(attempts.Any(r => r.Succeeded), string.Join("; ", attempts.Select(r => r.Error)));
            posted = attempts.First(r => r.Succeeded);
            await using var db = await _factory.CreateDbContextAsync();
            Assert.Equal(1, await db.IvTrxHistories.CountAsync(h => h.BatchNo == receiptBatch && h.EntryRole == "ORIGINAL"));
        }
        else
            posted = await PostReceiptAsync();
        if (scenario is 70 or 77 or 78 or 79 or 80 or 81 or 82 or 83 or 84)
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle != 0) posted = await PostReceiptAsync();
                var amended = scenario == 84 && cycle == 1;
                var expectedIssueCost = scenario == 83 || amended ? 26m : 24m;
                var expectedStockQty = amended ? 24m : 25m;
                var expectedStockValue = amended ? 84m : scenario == 83 ? 95m : 90m;
                Assert.True(posted.Succeeded, $"Post cycle {cycle}: {posted.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, expectedIssueCost);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                    Assert.Equal(expectedStockQty, state.OnHandBaseQty);
                    Assert.Equal(expectedStockValue, state.InventoryValue);
                    Assert.Equal(expectedStockQty, (await db.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    Assert.Equal(expectedStockQty, layers.Sum(l => l.RemainingQty));
                    Assert.Equal(expectedStockValue, layers.Sum(l => l.RemainingValue));
                    var latest = await db.StockFifoReversedIssueGenerations.OrderByDescending(g => g.Id).FirstAsync();
                    var splits = await db.StockFifoReversedIssueSplits.Where(s => s.GenerationId == latest.Id).ToArrayAsync();
                    Assert.Equal(expectedIssueCost, latest.TotalCost);
                    Assert.Equal(4m, splits.Sum(s => s.ConsumedQty));
                    Assert.Equal(expectedIssueCost, splits.Sum(s => s.ConsumedValue));
                    Assert.Null(await StockValuationSnapshotBuilder.AppendAsync(db, "DEMO", "HQ",
                        new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "TEST", CancellationToken.None));
                    var snapshot = db.ChangeTracker.Entries<StockValuationPeriodSnapshotHdr>()
                        .Single(e => e.State == EntityState.Added).Entity.Lines.Single(l => l.ItemCode == "RM001");
                    Assert.Equal(expectedStockQty - 4m, snapshot.ClosingQty);
                    Assert.Equal(expectedStockValue - expectedIssueCost, snapshot.ClosingValue);
                    Assert.Equal(0m, snapshot.AdjustmentQty);
                    if (goodsReceipt) Assert.Equal(amended ? 4m : 5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                if (cycle == 0 && scenario == 78)
                {
                    await SetFifoCorrectionFailureAsync(enabled: true);
                    var before = await CaptureReplayCountsAsync();
                    var failed = await RollbackReceiptAsync();
                    Assert.False(failed.Succeeded);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 24m);
                    await using (var unchanged = await _factory.CreateDbContextAsync())
                    {
                        Assert.Equal(1, await unchanged.StockFifoReversedIssueGenerations.CountAsync());
                        Assert.Equal(25m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                        if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
                        if (unchanged.Database.IsSqlServer())
                        {
                            await unchanged.Database.OpenConnectionAsync();
                            await using var command = unchanged.Database.GetDbConnection().CreateCommand();
                            command.CommandText = "SELECT TRY_CONVERT(bigint, SESSION_CONTEXT(N'STOCK_LEDGER_V2_POSTING_ID'))";
                            Assert.True(await command.ExecuteScalarAsync() is null or DBNull);
                        }
                    }
                    await SetFifoCorrectionFailureAsync(enabled: false);
                }
                if (cycle == 0 && scenario == 79)
                {
                    await SeedQaPhysicalHoldAsync(59, 21m);
                    var before = await CaptureReplayCountsAsync();
                    var held = await RollbackReceiptAsync();
                    Assert.False(held.Succeeded);
                    Assert.Contains("quality hold", held.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 24m);
                    await using var qa = await _factory.CreateDbContextAsync();
                    Assert.Equal(25m, (await qa.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    if (goodsReceipt) Assert.Equal(5m, (await qa.PoOrderDetails.SingleAsync()).RecvQty);
                    (await qa.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                    await qa.SaveChangesAsync();
                }
                if (cycle == 0 && scenario == 82)
                {
                    await using var corrupt = await _factory.CreateDbContextAsync();
                    var splitId = await corrupt.StockFifoReversedIssueSplits.Select(s => s.Id).FirstAsync();
                    if (corrupt.Database.IsSqlServer())
                        await Assert.ThrowsAnyAsync<Exception>(() => corrupt.StockFifoReversedIssueSplits.Where(s => s.Id == splitId)
                            .ExecuteUpdateAsync(p => p.SetProperty(s => s.ConsumedValue, s => s.ConsumedValue + 1m)));
                    else
                    {
                        Assert.Equal(1, await corrupt.StockFifoReversedIssueSplits.Where(s => s.Id == splitId)
                            .ExecuteUpdateAsync(p => p.SetProperty(s => s.ConsumedValue, s => s.ConsumedValue + 1m)));
                        var closeError = await StockValuationSnapshotBuilder.AppendAsync(corrupt, "DEMO", "HQ",
                            new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "TEST", CancellationToken.None);
                        Assert.Contains("incomplete correction evidence", closeError, StringComparison.OrdinalIgnoreCase);
                        var before = await CaptureReplayCountsAsync();
                        var refused = await RollbackReceiptAsync();
                        Assert.False(refused.Succeeded);
                        Assert.Equal(before, await CaptureReplayCountsAsync());
                        Assert.Equal(25m, (await corrupt.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                        Assert.Equal(1, await corrupt.StockFifoReversedIssueSplits.Where(s => s.Id == splitId)
                            .ExecuteUpdateAsync(p => p.SetProperty(s => s.ConsumedValue, s => s.ConsumedValue - 1m)));
                    }
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using var restored = await _factory.CreateDbContextAsync();
                var stateAfter = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, stateAfter.OnHandBaseQty);
                Assert.Equal(40m, stateAfter.InventoryValue);
                Assert.Equal(20m, (await restored.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                Assert.Null(await StockValuationSnapshotBuilder.AppendAsync(restored, "DEMO", "HQ",
                    new DateTime(2026, 10, 1), new DateTime(2026, 10, 31), "TEST", CancellationToken.None));
                var restoredSnapshot = restored.ChangeTracker.Entries<StockValuationPeriodSnapshotHdr>()
                    .Single(e => e.State == EntityState.Added).Entity.Lines.Single(l => l.ItemCode == "RM001");
                Assert.Equal(16m, restoredSnapshot.ClosingQty);
                Assert.Equal(32m, restoredSnapshot.ClosingValue);
                Assert.Equal(0m, restoredSnapshot.AdjustmentQty);
                if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
                if (scenario == 84 && cycle == 0)
                {
                    if (goods is not null)
                    {
                        await using (var poDb = await _factory.CreateDbContextAsync())
                        {
                            var poLine = await poDb.PoOrderDetails.SingleAsync();
                            poLine.PoUnitPrice = 11m;
                            poLine.NetAmount = 110m;
                            await poDb.SaveChangesAsync();
                        }
                        var edited = await goods.UpdateAsync(receiptBatch, new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = receiptDate,
                            Lines = [new IvGoodsReceiptLineRequest { PoNo = "PO-IP-FIFO", PoRelNo = 1,
                                PoLineNo = 1, ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 4m }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                    else
                    {
                        var edited = await receipts.UpdateAsync(receiptBatch, Receipt(receiptDate, 4m, 11m));
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                }
            }
            return;
        }
        if (scenario is 72 or 75)
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle != 0) posted = await PostReceiptAsync();
                Assert.True(posted.Succeeded, $"Post cycle {cycle}: {posted.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                    Assert.Equal(25m, state.OnHandBaseQty);
                    Assert.Equal(90m, state.InventoryValue);
                    Assert.Equal(25m, (await db.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    Assert.Equal(25m, layers.Sum(l => l.RemainingQty));
                    Assert.Equal(90m, layers.Sum(l => l.RemainingValue));
                    var asOf = await StockReplayPhysicalAsOfReader.ReadAsync(db, "DEMO", "HQ", [59],
                        new DateTime(2026, 10, 3, 12, 0, 0));
                    Assert.Equal(21m, asOf[59].AsOfBaseQty);
                    var asOfFacts = await db.StockValuationFacts.Where(f => f.CompanyCode == "DEMO"
                        && f.BranchCode == "HQ" && f.ItemCode == "RM001"
                        && f.EffectiveAt <= new DateTime(2026, 10, 3, 12, 0, 0)
                        && f.StockPosting!.SealedAtUtc != null).ToArrayAsync();
                    Assert.Equal(21m, asOfFacts.Sum(f => f.SignedQty));
                    Assert.Equal(82m, asOfFacts.Sum(f => f.SignedValue));
                    var history = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issue.Data.BatchNo
                        && h.TrxType == IvTrxTypes.IssueToProduction && h.EntryRole == "ORIGINAL");
                    var facts = await db.StockValuationFacts.Where(f => f.InventoryHistoryId == history.Id).ToArrayAsync();
                    Assert.Equal(2, facts.Length);
                    Assert.Equal(4m, facts.Sum(f => f.BaseQty));
                    Assert.Equal(8m, facts.Sum(f => f.CostAmount));
                    var ids = facts.Select(f => f.Id).ToArray();
                    var consumption = await db.StockFifoLayerConsumptions.Where(c => ids.Contains(c.IssueValuationFactId)).ToArrayAsync();
                    Assert.Equal(2, consumption.Length);
                    var olderReceipt = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == laterReceipt.BatchNo
                        && h.EntryRole == "ORIGINAL");
                    var candidate = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == receiptBatch
                        && h.EntryRole == "ORIGINAL" && !db.IvTrxHistories.Any(r => r.ReversesHistoryId == h.Id));
                    var olderOwner = await db.StockPostings.SingleAsync(p => p.Id == olderReceipt.StockPostingId);
                    var candidateOwner = await db.StockPostings.SingleAsync(p => p.Id == candidate.StockPostingId);
                    Assert.Equal(candidate.TrxDtTime, olderReceipt.TrxDtTime);
                    Assert.True(olderOwner.PostingSequence < candidateOwner.PostingSequence);
                    Assert.Contains(consumption, c => layers.Any(l => l.Id == c.FifoLayerId
                        && l.OriginStockPostingId == olderOwner.Id));
                    Assert.DoesNotContain(consumption, c => layers.Any(l => l.Id == c.FifoLayerId
                        && l.OriginStockPostingId == candidateOwner.Id));
                    if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                if (scenario == 75 && cycle == 0)
                {
                    await SeedQaPhysicalHoldAsync(59, 21m);
                    var before = await CaptureReplayCountsAsync();
                    var held = await RollbackReceiptAsync();
                    Assert.False(held.Succeeded);
                    Assert.Contains("quality hold", held.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                    await using var qa = await _factory.CreateDbContextAsync();
                    Assert.Equal(25m, (await qa.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    if (goodsReceipt) Assert.Equal(5m, (await qa.PoOrderDetails.SingleAsync()).RecvQty);
                    (await qa.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 20m;
                    await qa.SaveChangesAsync();
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using var restored = await _factory.CreateDbContextAsync();
                var restoredState = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(20m, restoredState.OnHandBaseQty);
                Assert.Equal(40m, restoredState.InventoryValue);
                var restoredAsOf = await StockReplayPhysicalAsOfReader.ReadAsync(restored, "DEMO", "HQ", [59],
                    new DateTime(2026, 10, 3, 12, 0, 0));
                Assert.Equal(16m, restoredAsOf[59].AsOfBaseQty);
                var restoredFacts = await restored.StockValuationFacts.Where(f => f.CompanyCode == "DEMO"
                    && f.BranchCode == "HQ" && f.ItemCode == "RM001"
                    && f.EffectiveAt <= new DateTime(2026, 10, 3, 12, 0, 0)
                    && f.StockPosting!.SealedAtUtc != null).ToArrayAsync();
                Assert.Equal(16m, restoredFacts.Sum(f => f.SignedQty));
                Assert.Equal(32m, restoredFacts.Sum(f => f.SignedValue));
                if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (scenario == 67)
        {
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle != 0) posted = await PostReceiptAsync();
                Assert.True(posted.Succeeded, $"Post cycle {cycle}: {posted.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 1.333333m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var issueHistory = await db.IvTrxHistories.SingleAsync(h => h.BatchNo == issue.Data.BatchNo
                        && h.TrxType == IvTrxTypes.IssueToProduction && h.EntryRole == "ORIGINAL");
                    var fact = await db.StockValuationFacts.SingleAsync(f => f.InventoryHistoryId == issueHistory.Id);
                    Assert.Equal(1.333333m, fact.CostAmount);
                    Assert.Equal(0.333333m, fact.UnitCost);
                    Assert.NotEqual(fact.CostAmount, StockLedgerPrecision.Money(fact.BaseQty * fact.UnitCost));
                    var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    Assert.Equal(28.4m, stock.OnHandBaseQty);
                    Assert.Equal(87.799999m, stock.InventoryValue);
                    Assert.Equal(28.4m, (await db.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                    var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                    Assert.Equal(28.4m, layers.Sum(l => l.RemainingQty));
                    Assert.Equal(87.799999m, layers.Sum(l => l.RemainingValue));
                    if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 1.333333m);
                await using var restored = await _factory.CreateDbContextAsync();
                var state = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(23.4m, state.OnHandBaseQty);
                Assert.Equal(37.799999m, state.InventoryValue);
                var restoredLayers = await restored.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                Assert.Equal(23.4m, restoredLayers.Sum(l => l.RemainingQty));
                Assert.Equal(37.799999m, restoredLayers.Sum(l => l.RemainingValue));
                if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
            }
            return;
        }
        if (scenario is 56 or 58 or 65 or 66 or 68 or 69 or 71 or 74)
        {
            Assert.False(posted.Succeeded);
            Assert.Equal(beforeRejectedReceipt, await CaptureReplayCountsAsync());
            if (scenario == 56)
                await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(scenario == 68 ? 23.4m : scenario is 69 or 70 or 71 or 74 ? 20m : 23m, state.OnHandBaseQty);
            Assert.Equal(scenario == 68 ? 37.799999m : scenario is 69 or 70 or 71 or 74 ? 40m : 46m, state.InventoryValue);
            Assert.Equal(IvBatchStatuses.New,
                (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            if (goodsReceipt) Assert.Equal(0m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
        if (scenario is 54 or 55 or 60 or 62 or 63 or 64)
        {
            if (scenario == 55)
            {
                Assert.False(posted.Succeeded);
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                return;
            }
            for (var cycle = 0; cycle < 2; cycle++)
            {
                if (cycle != 0) posted = await PostReceiptAsync();
                Assert.True(posted.Succeeded, $"Post cycle {cycle}: {posted.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using (var db = await _factory.CreateDbContextAsync())
                {
                    var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                    var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                    var amended = scenario == 64 && cycle == 1;
                    Assert.Equal(amended ? 27m : 28m, state.OnHandBaseQty);
                    Assert.Equal(amended ? 90m : 96m, state.InventoryValue);
                    Assert.Equal(amended ? 27m : 28m, layers.Sum(l => l.RemainingQty));
                    Assert.Equal(amended ? 90m : 96m, layers.Sum(l => l.RemainingValue));
                    if (goodsReceipt) Assert.Equal(amended ? 4m : 5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                }
                if (scenario == 60 && cycle == 0)
                {
                    await SeedQaPhysicalHoldAsync(59, 24m);
                    var before = await CaptureReplayCountsAsync();
                    var held = await RollbackReceiptAsync();
                    Assert.False(held.Succeeded);
                    Assert.Contains("quality hold", held.Error, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(before, await CaptureReplayCountsAsync());
                    await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                    await using var qa = await _factory.CreateDbContextAsync();
                    (await qa.QaPhysicalHolds.SingleAsync()).ReservedBaseQty = 23m;
                    await qa.SaveChangesAsync();
                }
                var removed = await RollbackReceiptAsync();
                Assert.True(removed.Succeeded, $"Rollback cycle {cycle}: {removed.Error}");
                await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
                await using var restored = await _factory.CreateDbContextAsync();
                var restoredState = await restored.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(23m, restoredState.OnHandBaseQty);
                Assert.Equal(46m, restoredState.InventoryValue);
                if (goodsReceipt) Assert.Equal(0m, (await restored.PoOrderDetails.SingleAsync()).RecvQty);
                if (scenario == 64 && cycle == 0)
                {
                    if (goods is not null)
                    {
                        var poLine = await restored.PoOrderDetails.SingleAsync();
                        poLine.PoUnitPrice = 11m;
                        poLine.NetAmount = 110m;
                        await restored.SaveChangesAsync();
                        var edited = await goods.UpdateAsync(receiptBatch, new IvGoodsReceiptSaveRequest
                        {
                            TrxType = IvTrxTypes.GoodsReceive, TrxDate = receiptDate,
                            Lines = [new IvGoodsReceiptLineRequest { PoNo = "PO-IP-FIFO", PoRelNo = 1,
                                PoLineNo = 1, ToWarehouse = "WH01", ToLocation = "BIN-A", ToRecvQty = 4m }]
                        });
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                    else
                    {
                        var edited = await receipts.UpdateAsync(receiptBatch, Receipt(receiptDate, 4m, 11m));
                        Assert.True(edited.Succeeded, edited.ErrorMessage);
                    }
                }
            }
            return;
        }
        if (scenario == 35)
        {
            await AssertQaIncomingReplayGuardAsync(posted, 59, receiptBatch, issue.Data.BatchNo,
                66m, 24m, PostReceiptAsync, RollbackReceiptAsync);
            return;
        }
        if (scenario == 34)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertQaReceiptHoldGuardAsync(59, receiptBatch, issue.Data.BatchNo,
                goodsReceipt, 66m, 24m, RollbackReceiptAsync);
            return;
        }
        if (scenario == 45)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                59, receiptBatch, goodsReceipt, fifo: true);
            await AssertReversedIssueAndOutputCostsAsync(consumedOutputId, issue.Data.BatchNo, 8m, 12m);
            return;
        }
        if (scenario == 46)
        {
            await AssertReversedReceiptRefusedAsync(posted, beforeRejectedReceipt,
                59, receiptBatch, goodsReceipt, fifo: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (scenario == 47)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (scenario == 48)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await RollbackIssueAsync(8m);
            var before = await CaptureReplayCountsAsync();
            await AssertReversedReceiptRollbackRefusedAsync(await RollbackReceiptAsync(), before,
                59, receiptBatch, goodsReceipt, fifo: true);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
            return;
        }
        if (scenario == 44)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertCorrectedIssueRequiresReceiptFirstAsync(issues, consumedOutputId,
                issue.Data.BatchNo, 59, receiptBatch, goodsReceipt, fifo: true, RollbackReceiptAsync);
            return;
        }
        if (scenario is 42 or 43)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertDamagedOutputCorrectionRollbackAsync(consumedOutputId, issue.Data.BatchNo,
                59, receiptBatch, goodsReceipt, fifo: true, RollbackReceiptAsync,
                damageMovement: scenario == 43);
            return;
        }
        if (scenario is 37 or 38)
        {
            Assert.False(posted.Succeeded);
            await AssertFailedReversedOutputReplayAsync(59, receiptBatch, goodsReceipt, fifo: true);
            if (scenario == 38)
                await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            return;
        }
        if (scenario is 31 or 36 or 39 or 40 or 41)
        {
            Assert.True(posted.Succeeded, posted.Error);
            var lower = scenario is 36 or 40;
            var partial = scenario is 39 or 40;
            var issueValue = lower ? 6m : 24m;
            var consumeValue = partial ? issueValue / 2m : issueValue;
            var outputValue = consumeValue + (partial ? 2m : 4m);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo, consumeValue, outputValue, issueValue);
            await using (var current = await _factory.CreateDbContextAsync())
            {
                var state = await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, state.OnHandBaseQty);
                Assert.Equal(lower ? 39m : 66m, state.InventoryValue);
                Assert.Equal(21m, (await current.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                var layers = await current.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                Assert.Equal(21m, layers.Sum(l => l.RemainingQty));
                Assert.Equal(lower ? 39m : 66m, layers.Sum(l => l.RemainingValue));
                if (goodsReceipt) Assert.Equal(5m, (await current.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo,
                partial ? 4m : 8m, partial ? 6m : 12m, 8m);
            var reposted = await PostReceiptAsync();
            Assert.True(reposted.Succeeded, reposted.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo, consumeValue, outputValue, issueValue);
            var removedAgain = await RollbackReceiptAsync();
            Assert.True(removedAgain.Succeeded, removedAgain.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo,
                partial ? 4m : 8m, partial ? 6m : 12m, 8m);
            await RollbackIssueAsync(8m);
            return;
        }
        if (scenario == 32)
        {
            Assert.True(posted.Succeeded, posted.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            await using (var current = await _factory.CreateDbContextAsync())
            {
                var stock = await current.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                Assert.Equal(21m, stock.OnHandBaseQty);
                Assert.Equal(42m, stock.InventoryValue);
                if (goodsReceipt) Assert.Equal(5m, (await current.PoOrderDetails.SingleAsync()).RecvQty);
            }
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await AssertReversedOutputUnchangedAsync(consumedOutputId, issue.Data.BatchNo);
            await RollbackIssueAsync(8m);
            return;
        }
        if (scenario is 11 or 15 or 16 or 24 or 27 or 28 or 29)
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
            if (scenario == 29)
            {
                Assert.Contains("reversed finished-good cost", posted.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(16m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
                var layers = await unchanged.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                Assert.Equal(16m, layers.Sum(l => l.RemainingQty));
                Assert.Equal(32m, layers.Sum(l => l.RemainingValue));
                Assert.Equal(0m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
                Assert.Empty(await unchanged.StockReplayRuns.ToArrayAsync());
            }
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
        if (scenario == 51)
        {
            var detail = await issues.GetAsync(issue.Data.BatchNo);
            Assert.True(detail.Succeeded, detail.Message);
            var line = Assert.Single(detail.Data!.Lines);
            Assert.Equal(6m, line.UnitCost);
            Assert.Equal(24m, line.TotalCost);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            var restored = await issues.GetAsync(issue.Data.BatchNo);
            Assert.True(restored.Succeeded, restored.Message);
            var restoredLine = Assert.Single(restored.Data!.Lines);
            Assert.Equal(2m, restoredLine.UnitCost);
            Assert.Equal(8m, restoredLine.TotalCost);
            return;
        }
        if (scenario == 30)
        {
            var finished = await PostFinishedGoodAsync(graph.OrderId, consumedOutputId, new DateTime(2026, 10, 3));
            Assert.Equal(14m, finished.Value);
            await RollbackFinishedGoodAsync(finished);
            if (!receiptFirst)
            {
                var outputReversal = await outputs.RollbackAsync(new ProductionOutputRollbackRequest
                {
                    OutputId = consumedOutputId, PostingRequestId = Guid.NewGuid().ToString("N"),
                    Reason = "Verify reversed output replay is rejected atomically"
                });
                Assert.True(outputReversal.Succeeded, outputReversal.Message);
            }
            int runCount;
            await using (var before = await _factory.CreateDbContextAsync())
                runCount = await before.StockReplayRuns.CountAsync();
            var blocked = await RollbackReceiptAsync();
            Assert.False(blocked.Succeeded);
            Assert.Contains("reversed finished-good cost", blocked.Error, StringComparison.OrdinalIgnoreCase);
            await using var unchanged = await _factory.CreateDbContextAsync();
            var state = await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(66m, state.InventoryValue);
            Assert.Equal(21m, (await unchanged.IvBalLocs.SingleAsync(b => b.Id == 59)).StdQty);
            var layers = await unchanged.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
            Assert.Equal(21m, layers.Sum(l => l.RemainingQty));
            Assert.Equal(66m, layers.Sum(l => l.RemainingValue));
            Assert.Equal(0m, (await unchanged.StockCostStates.SingleAsync(s => s.ItemCode == "FG001")).InventoryValue);
            Assert.Equal(runCount, await unchanged.StockReplayRuns.CountAsync());
            Assert.Equal(IvBatchStatuses.Posted, (await unchanged.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            if (goodsReceipt) Assert.Equal(5m, (await unchanged.PoOrderDetails.SingleAsync()).RecvQty);
            return;
        }
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
                Assert.Equal(6m, finished.Value);
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
            var before = await CaptureReplayCountsAsync();
            var rollbackRequest = new ProductionMaterialIssueRollbackRequest
            {
                PostingRequestId = Guid.NewGuid().ToString("N"),
                InventoryBatchNo = issue.Data.BatchNo, Reason = "verify safe FIFO rollback order"
            };
            var blocked = await issues.RollbackAsync(rollbackRequest);
            Assert.False(blocked.Succeeded);
            Assert.Contains("source receipt", blocked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await CaptureReplayCountsAsync());
            await CheckAsync(postedQty, postedStockValue, postedProductionValue);
            var removed = await RollbackReceiptAsync();
            Assert.True(removed.Succeeded, removed.Error);
            await CheckAsync(16m, 32m, 8m);
            await RollbackIssueAsync(8m, rollbackRequest);
            await AssertReversedIssueCostsAsync(issue.Data.BatchNo, 8m);
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

    private async Task<PostedFinishedGood> PostFinishedGoodAsync(long orderId, long outputId, DateTime businessDate,
        IQaPhysicalHoldAuthorization? qaPhysical = null)
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
            new IvInventoryHistoryWriter(),
            qaFg: qaPhysical is null ? null : new QaFgAuthorizationService(), qaPhysical: qaPhysical);
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
        IIvInventoryPostingService? inventoryPosting = null, DateTime? businessDate = null,
        IAccessRightService? accessOverride = null)
    {
        var access = accessOverride ?? Access();
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

    private static IAccessRightService Access(bool canPost = true, bool canRollback = true, bool canViewCost = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string _, string permission, CancellationToken _) => Task.FromResult(
                (permission != PermissionCodes.Post || canPost)
                && (permission != PermissionCodes.Rollback || canRollback)
                && (permission != PermissionCodes.ViewCost || canViewCost)));
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
        if (db.Database.IsSqlServer())
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT dbo.IvBalLoc ON");
        }
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
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("SET IDENTITY_INSERT dbo.IvBalLoc OFF");
    }

    private async Task<GraphIds> SeedTwoProcessWithMaterialAsync(string workOrderNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (db.Database.IsSqlite())
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        EnsureStock(db);
        var order = Order(workOrderNo, plannedQty: 100m);
        if (db.Database.IsSqlServer())
        {
            var bom = new PrBomHdr
            {
                CompanyCode = "DEMO", ProdCode = "FG001", DefinitionCode = "FG001",
                BaseQty = 1m, BaseUom = "EA"
            };
            db.PrBomHdrs.Add(bom);
            await db.SaveChangesAsync();
            order.SourceBomHdrId = bom.Uid;
        }
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
        if (db.Database.IsSqlite())
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

    private async Task AssertQaIncomingReplayGuardAsync(
        (bool Succeeded, string? Error) initial, int balanceId, int receiptBatch, int issueBatch,
        decimal postedStockValue, decimal postedWipValue,
        Func<Task<(bool Succeeded, string? Error)>> postReceipt,
        Func<Task<(bool Succeeded, string? Error)>> rollbackReceipt)
    {
        Assert.False(initial.Succeeded);
        Assert.Contains(QaReadinessMessages.IncomingFrozen, initial.Error, StringComparison.OrdinalIgnoreCase);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            Assert.Equal(16m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(16m, stock.OnHandBaseQty);
            Assert.Equal(32m, stock.InventoryValue);
            Assert.Equal(4m, wip.BaseQty);
            Assert.Equal(8m, wip.TotalCost);
            Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            Assert.Equal(IvBatchStatuses.New,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatch).ToArrayAsync());
            Assert.Empty(await db.StockReplayRuns.ToArrayAsync());
            Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
            Assert.Empty(await db.QaReceiptAllocations.ToArrayAsync());
            (await db.QaPolicies.SingleAsync()).IncomingInspectionEnabled = false;
            await db.SaveChangesAsync();
        }

        var posted = await postReceipt();
        Assert.True(posted.Succeeded, posted.Error);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            Assert.Equal(21m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(21m, stock.OnHandBaseQty);
            Assert.Equal(postedStockValue, stock.InventoryValue);
            Assert.Equal(postedWipValue, wip.TotalCost);
            Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
        await SeedQaReceiptAllocationAsync(receiptBatch);

        for (var cycle = 0; cycle < 2; cycle++)
        {
            var removed = await rollbackReceipt();
            Assert.True(removed.Succeeded, removed.Error);
            await using (var db = await _factory.CreateDbContextAsync())
            {
                var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
                var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                    m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
                var wip = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
                Assert.Equal(16m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
                Assert.Equal(16m, stock.OnHandBaseQty);
                Assert.Equal(32m, stock.InventoryValue);
                Assert.Equal(8m, wip.TotalCost);
                Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
                var allocation = await db.QaReceiptAllocations.SingleAsync();
                Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
                Assert.NotNull(allocation.ReversedAtUtc);
                Assert.Equal(QaCaseStatuses.Approved, (await db.QaCases.SingleAsync()).Status);
                Assert.Single(await db.QaAuditEvents.Where(e => e.EventType == QaAuditEvents.ReopenedForReissue).ToArrayAsync());
            }
            if (cycle == 0)
            {
                var repeated = await postReceipt();
                Assert.True(repeated.Succeeded, repeated.Error);
            }
        }
    }

    private async Task SeedQaReceiptAllocationAsync(int batchNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(b => b.BatchNo == batchNo);
        var detail = await db.IvTrxBatchDetails.SingleAsync(d => d.BatchId == batch.Id);
        var delivery = Guid.NewGuid();
        var line = new QaCaseLine { LineNo = 1, ItemCode = "RM001", Uom = "EA", Qty = 5m };
        var qaCase = new QaCase
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseNo = "QA-INCOMING-GRANT",
            CaseDate = new DateTime(2026, 10, 3), Status = QaCaseStatuses.Closed,
            SourceType = QaSourceTypes.IncomingPreGrn, DeliveryInstanceId = delivery,
            PoNo = detail.PoNo, PoRelNo = detail.PoRelNo, EligibilityNote = QaEligibilityNotes.IncomingGrant,
            CreatedDate = DateTime.UtcNow, CreatedBy = "tester", Lines = [line]
        };
        var grant = new QaReceiptGrant
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseLine = line, DeliveryInstanceId = delivery,
            LineKey = 1, PoNo = detail.PoNo, PoRelNo = detail.PoRelNo, PoLineNo = detail.PoLineNo,
            ItemCode = "RM001", Uom = "EA", AcceptedQty = 5m,
            Status = QaReceiptGrantStatuses.Open, ApprovedAtUtc = DateTime.UtcNow
        };
        grant.Allocations.Add(new QaReceiptAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", Qty = 5m,
            Status = QaReceiptAllocationStatuses.Posted, BatchNo = batchNo,
            BatchDetailId = detail.Id, DocumentRevision = detail.DocumentRevision, PostedAtUtc = DateTime.UtcNow
        });
        qaCase.Grants.Add(grant);
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

    private async Task AssertQaReceiptHoldGuardAsync(
        int balanceId, int receiptBatch, int issueBatch, bool goodsReceipt,
        decimal postedStockValue, decimal postedWipValue,
        Func<Task<(bool Succeeded, string? Error)>> rollbackReceipt)
    {
        await SeedQaPhysicalHoldAsync(balanceId, 17m);
        int originalRuns;
        int originalPostings;
        int originalRestatements;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var balance = await db.IvBalLocs.SingleAsync(b => b.Id == balanceId);
            Assert.Equal(21m, balance.StdQty);
            originalRuns = await db.StockReplayRuns.CountAsync();
            originalPostings = await db.StockPostings.CountAsync();
            originalRestatements = await db.ProductionCostRestatementFacts.CountAsync();
        }

        var blocked = await rollbackReceipt();
        Assert.False(blocked.Succeeded);
        Assert.Contains("quality hold", blocked.Error, StringComparison.OrdinalIgnoreCase);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var balance = await db.IvBalLocs.SingleAsync(b => b.Id == balanceId);
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            var pool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == wip.Uid);
            Assert.Equal(21m, balance.StdQty);
            Assert.Equal(21m, stock.OnHandBaseQty);
            Assert.Equal(postedStockValue, stock.InventoryValue);
            Assert.Equal(4m, wip.BaseQty);
            Assert.Equal(postedWipValue, wip.TotalCost);
            Assert.Equal(wip.TotalCost, pool.TrackedValue);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Equal(originalRuns, await db.StockReplayRuns.CountAsync());
            Assert.Equal(originalPostings, await db.StockPostings.CountAsync());
            Assert.Equal(originalRestatements, await db.ProductionCostRestatementFacts.CountAsync());
            var hold = await db.QaPhysicalHolds.SingleAsync();
            Assert.Equal(17m, hold.ReservedBaseQty);
            Assert.Equal(QaLogicalHoldStatuses.Active, hold.Status);
            if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            hold.ReservedBaseQty = 16m; // Simulate a hold reduced to the exact post-rollback balance.
            await db.SaveChangesAsync();
        }

        var removed = await rollbackReceipt();
        Assert.True(removed.Succeeded, removed.Error);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var balance = await db.IvBalLocs.SingleAsync(b => b.Id == balanceId);
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var material = await db.ProductionMaterialMovements.SingleAsync(m =>
                m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
            Assert.Equal(16m, balance.StdQty);
            Assert.Equal(16m, stock.OnHandBaseQty);
            Assert.Equal(32m, stock.InventoryValue);
            Assert.Equal(8m, wip.TotalCost);
            Assert.Equal(8m, (await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == wip.Uid)).TrackedValue);
            Assert.Equal(16m, (await db.QaPhysicalHolds.SingleAsync()).ReservedBaseQty);
            Assert.Equal(IvBatchStatuses.New,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
    }

    private async Task AssertQaFinishedGoodHoldGuardAsync(
        PostedFinishedGood finished, long outputId, int receiptBatch, bool goodsReceipt,
        Func<Task<(bool Succeeded, string? Error)>> rollbackReceipt)
    {
        int balanceId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            balanceId = (await db.ProductionFinishedGoodFactRows.SingleAsync(f =>
                f.BatchId == finished.Document.Id && f.ReversesFactId == null)).DestinationBalanceId;
        }
        await SeedQaPhysicalHoldAsync(balanceId, 1m);
        await SeedQaFinishedGoodAllocationAsync(finished.Document.Id, outputId);
        int originalRuns;
        int originalPostings;
        int originalRestatements;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            originalRuns = await db.StockReplayRuns.CountAsync();
            originalPostings = await db.StockPostings.CountAsync();
            originalRestatements = await db.ProductionCostRestatementFacts.CountAsync();
        }
        var requestId = Guid.NewGuid();
        var current = await finished.Service.GetAsync(finished.Document.Id);
        Assert.True(current.Succeeded, current.Message);
        var blocked = await finished.Service.RollbackAsync(new(
            current.Data!.Id, current.Data.RowVersion, requestId, "Verify QA hold"));
        Assert.False(blocked.Succeeded);
        Assert.Contains("quality hold", blocked.Message, StringComparison.OrdinalIgnoreCase);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var fg = await db.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
            var raw = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var produce = await db.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.Uid == produce.ProductionBalLotId);
            Assert.Equal(2m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(2m, fg.OnHandBaseQty);
            Assert.Equal(9.2m, fg.InventoryValue);
            Assert.Equal(21m, raw.OnHandBaseQty);
            Assert.Equal(75.6m, raw.InventoryValue);
            Assert.Equal(2m, wip.BaseQty);
            Assert.Equal(9.2m, wip.TotalCost);
            Assert.Equal(originalRuns, await db.StockReplayRuns.CountAsync());
            Assert.Equal(originalPostings, await db.StockPostings.CountAsync());
            Assert.Equal(originalRestatements, await db.ProductionCostRestatementFacts.CountAsync());
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            var hold = await db.QaPhysicalHolds.SingleAsync();
            Assert.Equal(QaLogicalHoldStatuses.Active, hold.Status);
            Assert.Equal(1m, hold.ReservedBaseQty);
            var allocation = await db.QaFgAllocations.SingleAsync();
            Assert.Equal(QaReceiptAllocationStatuses.Posted, allocation.Status);
            Assert.Null(allocation.ReversedAtUtc);
            Assert.Equal(QaCaseStatuses.Closed, (await db.QaCases.SingleAsync(c => c.CaseNo == "QA-FG-GRANT")).Status);
            Assert.Empty(await db.QaAuditEvents.ToArrayAsync());
            if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            hold.Status = QaLogicalHoldStatuses.Released;
            hold.ReleasedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        current = await finished.Service.GetAsync(finished.Document.Id);
        Assert.True(current.Succeeded, current.Message);
        var reversed = await finished.Service.RollbackAsync(new(
            current.Data!.Id, current.Data.RowVersion, requestId, "Verify QA hold"));
        Assert.True(reversed.Succeeded, reversed.Message);
        var removed = await rollbackReceipt();
        Assert.True(removed.Succeeded, removed.Error);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var fg = await db.StockCostStates.SingleAsync(s => s.ItemCode == "FG001");
            var raw = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            var produce = await db.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
            var wip = await db.ProductionBalLots.SingleAsync(l => l.Uid == produce.ProductionBalLotId);
            Assert.Equal(0m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(0m, fg.OnHandBaseQty);
            Assert.Equal(0m, fg.InventoryValue);
            Assert.Equal(16m, raw.OnHandBaseQty);
            Assert.Equal(32m, raw.InventoryValue);
            Assert.Equal(4m, wip.BaseQty);
            Assert.Equal(12m, wip.TotalCost);
            Assert.Equal(12m, (await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == wip.Uid)).TrackedValue);
            Assert.Equal(QaLogicalHoldStatuses.Released, (await db.QaPhysicalHolds.SingleAsync()).Status);
            var allocation = await db.QaFgAllocations.SingleAsync();
            Assert.Equal(QaReceiptAllocationStatuses.Reversed, allocation.Status);
            Assert.NotNull(allocation.ReversedAtUtc);
            Assert.Equal(QaCaseStatuses.Approved, (await db.QaCases.SingleAsync(c => c.CaseNo == "QA-FG-GRANT")).Status);
            Assert.Single(await db.QaAuditEvents.Where(e => e.EventType == QaAuditEvents.ReopenedForReissue).ToArrayAsync());
            if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
    }

    private async Task SeedQaFinishedGoodAllocationAsync(int batchId, long outputId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var source = await db.ProductionFinishedGoodSourceRows.SingleAsync(s => s.BatchId == batchId);
        var produce = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
        var line = new QaCaseLine { LineNo = 1, ItemCode = produce.ItemCode, Uom = produce.BaseUom, Qty = 2m };
        var qaCase = new QaCase
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseNo = "QA-FG-GRANT",
            CaseDate = new DateTime(2026, 10, 3), Status = QaCaseStatuses.Closed,
            SourceType = QaSourceTypes.FgStagingLot, ProductionBalLotId = produce.ProductionBalLotId,
            EligibilityNote = QaEligibilityNotes.FgGrant, CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester", Lines = [line]
        };
        var grant = new QaFgGrant
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseLine = line,
            ProductionBalLotId = produce.ProductionBalLotId, LineKey = 1,
            ItemCode = produce.ItemCode, Uom = produce.BaseUom, AcceptedQty = 2m,
            Status = QaReceiptGrantStatuses.Open, ApprovedAtUtc = DateTime.UtcNow
        };
        grant.Allocations.Add(new QaFgAllocation
        {
            CompanyCode = "DEMO", BranchCode = "HQ", Qty = 2m,
            Status = QaReceiptAllocationStatuses.Posted, BatchId = batchId,
            SourceId = source.Id, DocumentRevision = source.DocumentRevision, PostedAtUtc = DateTime.UtcNow
        });
        qaCase.FgGrants.Add(grant);
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

    private async Task SeedQaPhysicalHoldAsync(int balanceId, decimal quantity)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var balance = await db.IvBalLocs.SingleAsync(b => b.Id == balanceId);
        Assert.True(quantity > 0m && quantity <= balance.StdQty);
        db.QaPolicies.Add(new QaPolicy
        {
            CompanyCode = "DEMO", Version = 1, Enabled = true, IsPublished = true,
            PhysicalHoldEnabled = true, CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
        });
        var line = new QaCaseLine { LineNo = 1, ItemCode = balance.ICode, Uom = "EA", Qty = quantity };
        var qaCase = new QaCase
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseNo = "QA-REPLAY-HOLD",
            CaseDate = new DateTime(2026, 10, 3), Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.InventoryStock, CreatedDate = DateTime.UtcNow,
            CreatedBy = "tester", Lines = [line]
        };
        qaCase.PhysicalHolds.Add(new QaPhysicalHold
        {
            CompanyCode = "DEMO", BranchCode = "HQ", CaseLine = line,
            BalLocId = balance.Id, ItemCode = balance.ICode,
            WarehouseCode = balance.WhCode, LocationCode = balance.LocCode,
            LotNo = balance.LotNo ?? string.Empty, ItemStatus = balance.IStatus,
            ReservedBaseQty = quantity, Status = QaLogicalHoldStatuses.Active,
            Cause = QaLogicalHoldCauses.KeepHold, CreatedAtUtc = DateTime.UtcNow
        });
        db.QaCases.Add(qaCase);
        await db.SaveChangesAsync();
    }

    private async Task AssertReversedIssueAsOfAsync(
        int issueBatch, decimal beforeIssue, decimal betweenIssueAndReversal, decimal afterReversal)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var original = await db.IvTrxHistories.SingleAsync(h =>
            h.CompanyCode == "DEMO" && h.BranchCode == "HQ"
            && h.BatchNo == issueBatch && h.TrxType == IvTrxTypes.IssueToProduction
            && h.EntryRole == "ORIGINAL");
        var reversal = await db.IvTrxHistories.SingleAsync(h => h.ReversesHistoryId == original.Id);
        Assert.True(reversal.TrxDtTime > original.TrxDtTime.AddTicks(1));
        var asOf = new InventoryAsOfStockService();
        Assert.Equal(beforeIssue, (await asOf.GetAsync(db, "DEMO", "HQ", [58],
            original.TrxDtTime.AddTicks(-1)))[58].AsOfBaseQty);
        Assert.Equal(betweenIssueAndReversal, (await asOf.GetAsync(db, "DEMO", "HQ", [58],
            original.TrxDtTime.AddTicks(1)))[58].AsOfBaseQty);
        Assert.Equal(afterReversal, (await asOf.GetAsync(db, "DEMO", "HQ", [58],
            reversal.TrxDtTime))[58].AsOfBaseQty);
        if (reversal.TrxDtTime.Date > original.TrxDtTime.Date)
        {
            var (countRows, countTotal) = await StockReplayCountCandidateReader.ReadAsync(
                db, "DEMO", "HQ", new IvStockCountScope { ICodes = ["RM001"], WHCode = "WH01" },
                original.TrxDtTime.Date, 0, 10);
            Assert.Equal(1, countTotal);
            Assert.Equal(betweenIssueAndReversal, Assert.Single(countRows).HistoricalQty);
        }
    }

    private async Task AssertWacNeutralReversedIssueStateAsync(int receiptBatch, bool goodsReceipt, bool posted)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
        Assert.Equal(posted ? 25m : 20m, state.OnHandBaseQty);
        Assert.Equal(posted ? 50m : 40m, state.InventoryValue);
        Assert.Equal(posted ? 25m : 20m, (await db.IvBalLocs.SingleAsync(b => b.Id == 58)).StdQty);
        Assert.Equal(posted ? IvBatchStatuses.Posted : IvBatchStatuses.New,
            (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
        if (goodsReceipt) Assert.Equal(posted ? 5m : 0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
    }

    private async Task<int[]> CaptureReplayCountsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return [await db.StockPostings.CountAsync(), await db.StockValuationFacts.CountAsync(),
            await db.IvTrxHistories.CountAsync(), await db.ProductionBalLotMovements.CountAsync(),
            await db.ProductionCostRestatementFacts.CountAsync(), await db.StockReplayRuns.CountAsync(),
            await db.StockReplayEffects.CountAsync(), await db.QaReceiptAllocations.CountAsync()];
    }

    private async Task AssertReversedReceiptRefusedAsync(
        (bool Succeeded, string? Error) result, int[] before, int balanceId,
        int receiptBatch, bool goodsReceipt, bool fifo)
    {
        Assert.False(result.Succeeded);
        Assert.Contains("reversed Production issue", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await CaptureReplayCountsAsync());
        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
        Assert.Equal(20m, state.OnHandBaseQty);
        Assert.Equal(40m, state.InventoryValue);
        Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
        Assert.Equal(IvBatchStatuses.New,
            (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
        Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatch).ToArrayAsync());
        if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        if (fifo)
        {
            var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
            Assert.Equal(20m, layers.Sum(l => l.RemainingQty));
            Assert.Equal(40m, layers.Sum(l => l.RemainingValue));
        }
    }

    private async Task AssertReversedReceiptRollbackRefusedAsync(
        (bool Succeeded, string? Error) result, int[] before, int balanceId,
        int receiptBatch, bool goodsReceipt, bool fifo)
    {
        Assert.False(result.Succeeded);
        Assert.Contains("reversed Production issue", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await CaptureReplayCountsAsync());
        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
        Assert.Equal(25m, state.OnHandBaseQty);
        Assert.Equal(50m, state.InventoryValue);
        Assert.Equal(25m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
        Assert.Equal(IvBatchStatuses.Posted,
            (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
        if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        if (fifo)
        {
            var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
            Assert.Equal(25m, layers.Sum(l => l.RemainingQty));
            Assert.Equal(50m, layers.Sum(l => l.RemainingValue));
        }
    }

    private async Task AssertCorrectedIssueRequiresReceiptFirstAsync(
        ProductionMaterialIssueService issues, long outputId, int issueBatch,
        int balanceId, int receiptBatch, bool goodsReceipt, bool fifo,
        Func<Task<(bool Succeeded, string? Error)>> rollbackReceipt)
    {
        var issueValue = fifo ? 24m : 14.4m;
        var outputValue = fifo ? 28m : 18.4m;
        var inventoryValue = fifo ? 66m : 75.6m;
        var request = new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = issueBatch,
            Reason = "Verify corrected issue and reversed output rollback"
        };
        int postingCount;
        int valuationCount;
        int restatementCount;
        int historyCount;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            postingCount = await db.StockPostings.CountAsync();
            valuationCount = await db.StockValuationFacts.CountAsync();
            restatementCount = await db.ProductionCostRestatementFacts.CountAsync();
            historyCount = await db.IvTrxHistories.CountAsync();
        }
        var blocked = await issues.RollbackAsync(request);
        Assert.False(blocked.Succeeded);
        Assert.Contains("source receipt", blocked.Message, StringComparison.OrdinalIgnoreCase);
        await AssertReversedOutputUnchangedAsync(outputId, issueBatch, issueValue, outputValue);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, state.OnHandBaseQty);
            Assert.Equal(inventoryValue, state.InventoryValue);
            Assert.Equal(21m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == issueBatch)).BatchStatus);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Equal(postingCount, await db.StockPostings.CountAsync());
            Assert.Equal(valuationCount, await db.StockValuationFacts.CountAsync());
            Assert.Equal(restatementCount, await db.ProductionCostRestatementFacts.CountAsync());
            Assert.Equal(historyCount, await db.IvTrxHistories.CountAsync());
            if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
        var removed = await rollbackReceipt();
        Assert.True(removed.Succeeded, removed.Error);
        await AssertReversedOutputUnchangedAsync(outputId, issueBatch);
        var retried = await issues.RollbackAsync(request);
        Assert.True(retried.Succeeded, retried.Message);
        await AssertReversedIssueAndOutputCostsAsync(outputId, issueBatch, 8m, 12m);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(20m, state.OnHandBaseQty);
            Assert.Equal(40m, state.InventoryValue);
            Assert.Equal(20m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        }
    }

    private async Task DamageIssueReversalCostAsync(int issueBatch)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var movementId = await db.ProductionMaterialMovements.Where(m =>
                m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue)
            .Select(m => m.ProductionBalLotMovementId).SingleAsync();
        var changed = await db.ProductionBalLotMovements.Where(m =>
                m.OriginalMovementId == movementId && m.MovementType == ProductionBalLotMovementTypes.IssueReversal)
            .ExecuteUpdateAsync(update => update.SetProperty(m => m.TotalCost, 9m));
        Assert.Equal(1, changed);
    }

    private async Task AssertReversedIssueCostsAsync(int issueBatch, decimal expectedValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var material = await db.ProductionMaterialMovements.SingleAsync(m =>
            m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
        var issue = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.Uid == material.ProductionBalLotMovementId);
        var reverse = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == issue.Uid && m.MovementType == ProductionBalLotMovementTypes.IssueReversal);
        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db, "DEMO", "HQ", [issue.Uid, reverse.Uid]);
        Assert.Equal(expectedValue, costs[issue.Uid].TotalCost);
        Assert.Equal(expectedValue, costs[reverse.Uid].TotalCost);
    }

    private async Task AssertReversedIssueInventoryCostAsync(int issueBatch, decimal expectedValue)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var issue = await db.IvTrxHistories.SingleAsync(h => h.CompanyCode == "DEMO"
            && h.BranchCode == "HQ" && h.BatchNo == issueBatch
            && h.TrxType == IvTrxTypes.IssueToProduction && h.EntryRole == "ORIGINAL");
        var reversal = await db.IvTrxHistories.SingleAsync(h => h.ReversesHistoryId == issue.Id);
        var issueFact = await db.StockValuationFacts.SingleAsync(f => f.InventoryHistoryId == issue.Id);
        var reversalFact = await db.StockValuationFacts.SingleAsync(f => f.InventoryHistoryId == reversal.Id);
        var deltas = await db.StockValuationFacts.AsNoTracking().Where(f =>
            f.CompanyCode == "DEMO" && f.BranchCode == "HQ" && f.InventoryHistoryId == null
            && (f.OriginalValuationFactId == issueFact.Id || f.OriginalValuationFactId == reversalFact.Id)
            && f.StockPosting!.SealedAtUtc != null).ToArrayAsync();
        Assert.All(deltas, f =>
        {
            var original = f.OriginalValuationFactId == issueFact.Id ? issueFact : reversalFact;
            Assert.Equal(0m, f.BaseQty);
            Assert.Equal(original.EffectiveAt, f.EffectiveAt);
            Assert.Equal(original.PeriodKey, f.PeriodKey);
            Assert.True(new StockValuationMovementClassifier().IsValueOnlyAdjustment(f.MovementCode));
        });
        Assert.Equal(-expectedValue, issueFact.SignedValue + deltas
            .Where(f => f.OriginalValuationFactId == issueFact.Id).Sum(f => f.SignedValue));
        Assert.Equal(expectedValue, reversalFact.SignedValue + deltas
            .Where(f => f.OriginalValuationFactId == reversalFact.Id).Sum(f => f.SignedValue));
    }

    private async Task InstallReversedIssueSqlGuardsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (!db.Database.IsSqlServer()) return;
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ErpWeb.slnx")))
            root = root.Parent;
        Assert.NotNull(root);
        var guard = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "create-stock-ledger-write-guard.sql"));
        await db.Database.ExecuteSqlRawAsync(guard);
        var replay = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "alter-open-period-backdated-replay.sql"));
        var batches = System.Text.RegularExpressions.Regex.Split(replay, @"(?im)^[\t ]*GO[\t ]*(?:\r?\n|$)");
        foreach (var name in new[] { "TR_StockReplayRun_Identity", "TR_StockReplayEffect_Ownership", "TR_ProductionCostRestatementFact_Tenant" })
        {
            var trigger = batches.Single(b => b.Contains("CREATE OR ALTER TRIGGER dbo." + name, StringComparison.OrdinalIgnoreCase));
            await db.Database.ExecuteSqlRawAsync(trigger);
        }
        var fifoGuards = await File.ReadAllTextAsync(Path.Combine(root.FullName, "scripts", "create-fifo-reversed-issue-guards.sql"));
        foreach (var trigger in System.Text.RegularExpressions.Regex.Split(fifoGuards, @"(?im)^[\t ]*GO[\t ]*(?:\r?\n|$)")
            .Where(b => !string.IsNullOrWhiteSpace(b)))
            await db.Database.ExecuteSqlRawAsync(trigger);
    }

    private async Task AssertReversedIssueAndOutputCostsAsync(
        long outputId, int issueBatch, decimal issueValue, decimal outputValue, decimal? consumptionValue = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var material = await db.ProductionMaterialMovements.SingleAsync(m =>
            m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
        var issue = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.Uid == material.ProductionBalLotMovementId);
        var issueReverse = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == issue.Uid && m.MovementType == ProductionBalLotMovementTypes.IssueReversal);
        var consume = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Consume
            && m.ProductionBalLotId == issue.ProductionBalLotId);
        var produce = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
        var consumeReverse = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == consume.Uid && m.MovementType == ProductionBalLotMovementTypes.ConsumeReversal);
        var produceReverse = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == produce.Uid && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db, "DEMO", "HQ",
            [issue.Uid, issueReverse.Uid, consume.Uid, consumeReverse.Uid, produce.Uid, produceReverse.Uid]);
        Assert.Equal(issueValue, costs[issue.Uid].TotalCost);
        Assert.Equal(issueValue, costs[issueReverse.Uid].TotalCost);
        Assert.Equal(consumptionValue ?? issueValue, costs[consume.Uid].TotalCost);
        Assert.Equal(consumptionValue ?? issueValue, costs[consumeReverse.Uid].TotalCost);
        Assert.Equal(outputValue, costs[produce.Uid].TotalCost);
        Assert.Equal(outputValue, costs[produceReverse.Uid].TotalCost);
        var source = await db.ProductionBalLots.SingleAsync(l => l.Uid == issue.ProductionBalLotId);
        var output = await db.ProductionBalLots.SingleAsync(l => l.Uid == produce.ProductionBalLotId);
        Assert.Equal(0m, source.BaseQty);
        Assert.Equal(0m, source.TotalCost);
        Assert.Equal(0m, output.BaseQty);
        Assert.Equal(0m, output.TotalCost);
        var pools = await db.ProductionPoolValuationRows.Where(p =>
            p.ProductionBalLotId == source.Uid || p.ProductionBalLotId == output.Uid).ToArrayAsync();
        Assert.Equal(2, pools.Length);
        Assert.All(pools, p => { Assert.Equal(0m, p.TrackedBaseQty); Assert.Equal(0m, p.TrackedValue); });
    }

    private async Task AssertDamagedOutputCorrectionRollbackAsync(long outputId, int issueBatch,
        int balanceId, int receiptBatch, bool goodsReceipt, bool fifo,
        Func<Task<(bool Succeeded, string? Error)>> rollback, bool damageMovement)
    {
        var issueValue = fifo ? 24m : 14.4m;
        var outputValue = fifo ? 28m : 18.4m;
        var inventoryValue = fifo ? 66m : 75.6m;
        await AssertReversedOutputUnchangedAsync(outputId, issueBatch, issueValue, outputValue);
        int postingCount;
        int factCount;
        int replayCount;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var output = await db.ProductionOutputs.SingleAsync(o => o.Uid == outputId);
            var reversedProduce = await db.ProductionBalLotMovements.SingleAsync(m =>
                m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
            var updated = damageMovement
                ? await db.ProductionBalLotMovements.Where(m => m.Uid == reversedProduce.Uid)
                    .ExecuteUpdateAsync(update => update.SetProperty(m => m.UnitCost, 99m))
                : await db.StockReplayEffects.Where(e =>
                        e.AffectedEntityType == "PRODUCTION_COST_RESTATEMENT_FACT"
                        && db.ProductionCostRestatementFacts.Any(f => f.Id == e.AffectedEntityId
                            && f.ProductionBalLotMovementId == reversedProduce.Uid && f.ReversesRestatementFactId == null))
                    .ExecuteUpdateAsync(update => update.SetProperty(e => e.BusinessOwnerPostingId, output.PostingId!.Value));
            Assert.Equal(1, updated);
            postingCount = await db.StockPostings.CountAsync();
            factCount = await db.StockValuationFacts.CountAsync();
            replayCount = await db.ProductionCostRestatementFacts.CountAsync();
        }
        var refused = await rollback();
        Assert.False(refused.Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stock = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
            Assert.Equal(21m, stock.OnHandBaseQty);
            Assert.Equal(inventoryValue, stock.InventoryValue);
            Assert.Equal(21m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
            Assert.Equal(IvBatchStatuses.Posted,
                (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
            Assert.Equal(postingCount, await db.StockPostings.CountAsync());
            Assert.Equal(factCount, await db.StockValuationFacts.CountAsync());
            Assert.Equal(replayCount, await db.ProductionCostRestatementFacts.CountAsync());
            if (goodsReceipt) Assert.Equal(5m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
            if (fifo)
            {
                var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
                Assert.Equal(21m, layers.Sum(l => l.RemainingQty));
                Assert.Equal(inventoryValue, layers.Sum(l => l.RemainingValue));
            }
        }
        await AssertReversedOutputUnchangedAsync(outputId, issueBatch, issueValue, outputValue);
    }

    private async Task DamageOrRejectReversedOutputAsync(long outputId, bool injectFailure)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var reversal = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
        if (injectFailure)
        {
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER TestRejectOutputPairReplay BEFORE INSERT ON ProductionCostRestatementFact "
                + "WHEN NEW.ProductionBalLotMovementId = " + reversal.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " BEGIN SELECT RAISE(ABORT, 'injected reversed-output replay failure'); END;");
        }
        else
        {
            reversal.UnitCost = 99m;
            await db.SaveChangesAsync();
        }
    }

    private async Task AssertFailedReversedOutputReplayAsync(
        int balanceId, int receiptBatch, bool goodsReceipt, bool fifo = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.StockCostStates.SingleAsync(s => s.ItemCode == "RM001");
        Assert.Equal(16m, state.OnHandBaseQty);
        Assert.Equal(32m, state.InventoryValue);
        Assert.Equal(16m, (await db.IvBalLocs.SingleAsync(b => b.Id == balanceId)).StdQty);
        Assert.Equal(IvBatchStatuses.New,
            (await db.IvTrxBatches.SingleAsync(b => b.BatchNo == receiptBatch)).BatchStatus);
        Assert.Empty(await db.IvTrxHistories.Where(h => h.BatchNo == receiptBatch).ToArrayAsync());
        Assert.Empty(await db.ProductionCostRestatementFacts.ToArrayAsync());
        Assert.Empty(await db.StockReplayRuns.ToArrayAsync());
        if (goodsReceipt) Assert.Equal(0m, (await db.PoOrderDetails.SingleAsync()).RecvQty);
        if (fifo)
        {
            var layers = await db.StockFifoLayers.Where(l => l.ItemCode == "RM001").ToArrayAsync();
            Assert.Equal(16m, layers.Sum(l => l.RemainingQty));
            Assert.Equal(32m, layers.Sum(l => l.RemainingValue));
        }
    }

    private async Task AssertReversedOutputUnchangedAsync(long outputId, int issueBatch,
        decimal consumeValue = 8m, decimal produceValue = 12m, decimal? issueValue = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var material = await db.ProductionMaterialMovements.SingleAsync(m =>
            m.InventoryBatchNo == issueBatch && m.MovementType == ProductionMaterialMovementTypes.Issue);
        var consume = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Consume
            && m.ProductionBalLotId == material.ProductionBalLotId);
        var produce = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.ProductionOutputId == outputId && m.MovementType == ProductionBalLotMovementTypes.Produce);
        var reversedConsume = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == consume.Uid && m.MovementType == ProductionBalLotMovementTypes.ConsumeReversal);
        var reversedProduce = await db.ProductionBalLotMovements.SingleAsync(m =>
            m.OriginalMovementId == produce.Uid && m.MovementType == ProductionBalLotMovementTypes.ProduceReversal);
        var costs = await ProductionEffectiveCostResolver.ResolveAsync(db, "DEMO", "HQ",
            [consume.Uid, produce.Uid, reversedConsume.Uid, reversedProduce.Uid]);
        Assert.Equal(consumeValue, costs[consume.Uid].TotalCost);
        Assert.Equal(consumeValue, costs[reversedConsume.Uid].TotalCost);
        Assert.Equal(produceValue, costs[produce.Uid].TotalCost);
        Assert.Equal(produceValue, costs[reversedProduce.Uid].TotalCost);
        var source = await db.ProductionBalLots.SingleAsync(l => l.OriginalIssueMovementId == material.Uid);
        var sourcePool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == source.Uid);
        Assert.Equal(4m, source.BaseQty);
        Assert.Equal(issueValue ?? consumeValue, source.TotalCost);
        Assert.Equal(source.BaseQty, sourcePool.TrackedBaseQty);
        Assert.Equal(source.TotalCost, sourcePool.TrackedValue);
        var target = await db.ProductionBalLots.SingleAsync(l => l.Uid == produce.ProductionBalLotId);
        var targetPool = await db.ProductionPoolValuationRows.SingleAsync(p => p.ProductionBalLotId == target.Uid);
        Assert.Equal(0m, target.BaseQty);
        Assert.Equal(0m, target.TotalCost);
        Assert.Equal(target.BaseQty, targetPool.TrackedBaseQty);
        Assert.Equal(target.TotalCost, targetPool.TrackedValue);
    }

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
