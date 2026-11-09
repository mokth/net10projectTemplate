using ErpWeb.Model.Data;
using ErpWeb.Tests.Inventory.Transaction;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Production.Transaction;

/// <summary>Each case requires its own empty, isolated scratch database for the public workflow.</summary>
[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class ProductionNeutralReversedIssueSqlServerTests
{
    [Fact]
    public Task Cost_neutral_backdated_misc_receipt_posts_and_rolls_back_after_issue_reversal() =>
        RunAsync(goodsReceipt: false);

    [Fact]
    public Task Cost_neutral_backdated_goods_receipt_posts_and_rolls_back_after_issue_reversal() =>
        RunAsync(goodsReceipt: true);

    [Fact]
    public Task Concurrent_cost_neutral_misc_receipt_posts_only_once_after_issue_reversal() =>
        RunAsync(goodsReceipt: false, concurrent: true);

    [Fact]
    public Task Same_day_issue_rejects_later_restored_stock_with_replay_enabled() =>
        RunAsync(goodsReceipt: false, sameDay: true);

    [Fact]
    public Task Same_day_issue_rejects_later_restored_stock_with_replay_disabled() =>
        RunAsync(goodsReceipt: false, sameDay: true, replayEnabled: false);

    [Fact]
    public Task Misc_receipt_rollback_rejects_reversed_issue_quantity_with_replay_enabled() =>
        RunAsync(goodsReceipt: false, rollbackCostChanged: false);

    [Fact]
    public Task Goods_receipt_rollback_rejects_reversed_issue_quantity_with_replay_disabled() =>
        RunAsync(goodsReceipt: true, rollbackCostChanged: false, replayEnabled: false);

    [Fact]
    public Task Misc_receipt_rollback_rejects_reversed_issue_cost_with_replay_disabled() =>
        RunAsync(goodsReceipt: false, rollbackCostChanged: true, replayEnabled: false);

    [Fact]
    public Task Goods_receipt_rollback_replays_reversed_issue_cost_with_replay_enabled() =>
        RunAsync(goodsReceipt: true, rollbackCostChanged: true);

    [Fact]
    public Task Issue_detail_shows_replayed_misc_receipt_cost_on_sql_server() =>
        RunAsync(goodsReceipt: false, issueDetailDamage: false);

    [Fact]
    public Task Issue_detail_rejects_damaged_goods_receipt_cost_on_sql_server() =>
        RunAsync(goodsReceipt: true, issueDetailDamage: true);

    [Fact]
    public Task Cross_pile_misc_receipt_rejects_reversed_issue_with_replay_disabled() =>
        RunAsync(goodsReceipt: false, crossPileReplay: false);

    [Fact]
    public Task Cross_pile_goods_receipt_rejects_reversed_issue_with_replay_enabled() =>
        RunAsync(goodsReceipt: true, crossPileReplay: true);

    [Fact]
    public Task Reversed_issue_misc_receipt_reprices_and_reposts_with_higher_cost() =>
        RunAsync(goodsReceipt: false, correctedIssueLower: false);

    [Fact]
    public Task Reversed_issue_goods_receipt_reprices_and_reposts_with_lower_cost() =>
        RunAsync(goodsReceipt: true, correctedIssueLower: true);

    [Fact]
    public Task Concurrent_reversed_issue_misc_receipt_posts_only_once() =>
        RunAsync(goodsReceipt: false, concurrent: true, correctedIssueLower: false);

    [Fact]
    public Task Reversed_issue_post_failure_clears_sql_session_and_can_retry() =>
        RunAsync(goodsReceipt: false, correctedIssueLower: false, correctedIssueFailure: true);

    [Fact]
    public Task Earlier_misc_receipt_rolls_back_and_reposts_after_reversed_issue() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false);

    [Fact]
    public Task Earlier_goods_receipt_rolls_back_and_reposts_after_reversed_issue() =>
        RunAsync(goodsReceipt: true, earlierReceiptLower: true);

    [Fact]
    public Task Concurrent_earlier_receipt_rollback_writes_only_one_reversal() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptConcurrent: true);

    [Fact]
    public Task Earlier_receipt_rollback_failure_clears_sql_session_and_can_retry() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptFailure: true);

    [Fact]
    public Task Earlier_misc_receipt_corrected_price_reposts_and_rolls_back() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptEdited: true);

    [Fact]
    public Task Earlier_goods_receipt_corrected_price_reposts_and_rolls_back() =>
        RunAsync(goodsReceipt: true, earlierReceiptLower: false, earlierReceiptEdited: true);

    [Fact]
    public Task Earlier_receipt_edited_repost_failure_clears_session_and_can_retry() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptEditedFailure: true);

    [Fact]
    public Task Earlier_misc_receipt_reposts_cost_neutral_price_then_changes_again() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptNeutralEdited: true);

    [Fact]
    public Task Earlier_goods_receipt_reposts_cost_neutral_price_then_changes_again() =>
        RunAsync(goodsReceipt: true, earlierReceiptLower: false, earlierReceiptNeutralEdited: true);

    [Fact]
    public Task Earlier_misc_receipt_reposts_changed_quantity_and_price() =>
        RunAsync(goodsReceipt: false, earlierReceiptLower: false, earlierReceiptQuantityEdited: true);

    [Fact]
    public Task Earlier_goods_receipt_reposts_changed_quantity_and_price() =>
        RunAsync(goodsReceipt: true, earlierReceiptLower: false, earlierReceiptQuantityEdited: true);

    [Fact]
    public Task Earlier_goods_receipt_rejects_unreconciled_quantity_rounding() =>
        RunAsync(goodsReceipt: true, earlierReceiptLower: false, earlierReceiptQuantityRounding: true);

    [Fact]
    public Task Reversed_issue_and_output_misc_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false);

    [Fact]
    public Task Reversed_issue_and_output_goods_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: true, reversedOutputLower: true);

    [Fact]
    public Task Reversed_issue_and_output_failure_rolls_back_and_clears_session() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, reversedOutputFailure: true);

    [Fact]
    public Task Concurrent_reversed_issue_and_output_receipt_posts_once() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, concurrent: true);

    [Fact]
    public Task Earlier_misc_receipt_with_reversed_output_rolls_back_and_reposts() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, earlierOutput: true);

    [Fact]
    public Task Earlier_goods_receipt_with_reversed_output_reposts_price_edits() =>
        RunAsync(goodsReceipt: true, reversedOutputLower: false, earlierOutput: true, outputEdited: true);

    [Fact]
    public Task Earlier_receipt_output_failure_rolls_back_and_clears_session() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, earlierOutput: true, reversedOutputFailure: true);

    [Fact]
    public Task Concurrent_earlier_receipt_with_reversed_output_rolls_back_once() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, earlierOutput: true, concurrent: true);

    [Fact]
    public Task Earlier_misc_receipt_with_reversed_output_reposts_quantity_edits() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, earlierOutput: true, outputQuantityEdited: true);

    [Fact]
    public Task Earlier_goods_receipt_with_reversed_output_reposts_quantity_edits() =>
        RunAsync(goodsReceipt: true, reversedOutputLower: false, earlierOutput: true, outputQuantityEdited: true);

    [Fact]
    public Task Partial_reversed_output_misc_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, partialOutput: true);

    [Fact]
    public Task Earlier_goods_receipt_with_partial_reversed_output_reposts_price_edits() =>
        RunAsync(goodsReceipt: true, reversedOutputLower: false, partialOutput: true, earlierOutput: true, outputEdited: true);

    [Fact]
    public Task Partial_output_failure_rolls_back_and_clears_session() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, partialOutput: true, reversedOutputFailure: true);

    [Fact]
    public Task Concurrent_partial_output_receipt_posts_once() =>
        RunAsync(goodsReceipt: false, reversedOutputLower: false, partialOutput: true, concurrent: true);

    [Fact]
    public Task Fifo_reversed_issue_replays_consumed_misc_receipt_rollback() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true);

    [Fact]
    public Task Fifo_reversed_issue_replays_consumed_goods_receipt_rollback() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true);

    [Fact]
    public Task Fifo_reversed_issue_allows_unconsumed_earlier_receipt_rollback() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoGuardSafe: true);

    [Fact]
    public Task Fifo_neutral_reversed_misc_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoNeutral: true);

    [Fact]
    public Task Fifo_neutral_reversed_goods_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoNeutral: true);

    [Fact]
    public Task Fifo_neutral_reversed_receipt_failure_clears_session() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoNeutral: true, fifoNeutralFailure: true);

    [Fact]
    public Task Concurrent_fifo_neutral_reversed_receipt_posts_once() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoNeutral: true, concurrent: true);

    [Fact]
    public Task Fifo_neutral_reversed_goods_receipt_respects_qa_hold() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoNeutral: true, fifoNeutralQa: true);

    [Fact]
    public Task Fifo_neutral_reversed_goods_receipt_reposts_quantity_and_price_edit() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoNeutral: true, fifoNeutralEdited: true);

    [Fact]
    public Task Fifo_neutral_reversed_goods_receipt_accepts_exact_split_rounding() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoNeutral: true, fifoNeutralRounding: true);

    [Fact]
    public Task Fifo_reversed_issue_projects_changed_misc_receipt_cost() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoProjectionInsertion: true);

    [Fact]
    public Task Fifo_reversed_issue_projects_changed_goods_receipt_cost() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoProjectionInsertion: true);

    [Fact]
    public Task Fifo_same_time_reversed_issue_misc_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 1);

    [Fact]
    public Task Fifo_same_time_reversed_issue_goods_receipt_posts_and_rolls_back() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 1);

    [Fact]
    public Task Fifo_same_time_reversed_issue_replays_consumed_misc_receipt() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 2);

    [Fact]
    public Task Fifo_same_time_reversed_issue_replays_consumed_goods_receipt() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 2);

    [Fact]
    public Task Fifo_same_time_reversed_issue_goods_receipt_respects_qa_hold() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 3);

    [Fact]
    public Task Fifo_changed_reversed_issue_misc_receipt_posts_and_reposts() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 4);

    [Fact]
    public Task Fifo_changed_reversed_issue_goods_receipt_posts_and_reposts() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 4);

    [Fact]
    public Task Fifo_changed_reversed_issue_failure_clears_session_and_retries() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 5);

    [Fact]
    public Task Concurrent_fifo_changed_reversed_issue_receipt_posts_once() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 6);

    [Fact]
    public Task Fifo_changed_reversed_issue_goods_receipt_respects_qa_hold() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 7);

    [Fact]
    public Task Fifo_changed_reversed_issue_split_is_immutable() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 8);

    [Fact]
    public Task Fifo_changed_reversed_issue_rejects_concurrent_draft_edit() =>
        RunAsync(goodsReceipt: false, fifoGuardReplay: true, fifoSameTimeCase: 9);

    [Fact]
    public Task Fifo_changed_reversed_issue_goods_receipt_reposts_edited_quantity_and_price() =>
        RunAsync(goodsReceipt: true, fifoGuardReplay: true, fifoSameTimeCase: 10);

    [Fact]
    public Task Wac_reversed_issue_receipt_serializes_edit_through_post_and_repost() =>
        RunAsync(goodsReceipt: false, wacReceiptEditRace: true);

    private static async Task RunAsync(bool goodsReceipt, bool concurrent = false,
        bool sameDay = false, bool replayEnabled = true, bool? rollbackCostChanged = null,
        bool? issueDetailDamage = null, bool? crossPileReplay = null, bool? correctedIssueLower = null,
        bool? earlierReceiptLower = null,
        bool earlierReceiptConcurrent = false, bool earlierReceiptFailure = false,
        bool earlierReceiptEdited = false, bool earlierReceiptEditedFailure = false,
        bool earlierReceiptNeutralEdited = false,
        bool earlierReceiptQuantityEdited = false, bool earlierReceiptQuantityRounding = false,
        bool correctedIssueFailure = false, bool? reversedOutputLower = null,
        bool reversedOutputFailure = false, bool earlierOutput = false, bool outputEdited = false,
        bool outputQuantityEdited = false, bool partialOutput = false,
        bool? fifoGuardReplay = null, bool fifoGuardSafe = false, bool fifoNeutral = false,
        bool fifoNeutralFailure = false, bool fifoNeutralQa = false, bool fifoNeutralEdited = false,
        bool fifoNeutralRounding = false, bool fifoProjectionInsertion = false, int fifoSameTimeCase = 0,
        bool wacReceiptEditRace = false)
    {
        var connection = Environment.GetEnvironmentVariable(correctedIssueLower.HasValue || earlierReceiptLower.HasValue || reversedOutputLower.HasValue || fifoGuardReplay.HasValue || wacReceiptEditRace
            ? "ERPWEB_REPLAY_ISOLATED_REVERSED_COST_TEST_CONNECTION" : crossPileReplay.HasValue
            ? "ERPWEB_REPLAY_ISOLATED_CROSS_PILE_TEST_CONNECTION" : issueDetailDamage.HasValue
            ? "ERPWEB_REPLAY_ISOLATED_DETAIL_TEST_CONNECTION" : rollbackCostChanged.HasValue
            ? "ERPWEB_REPLAY_ISOLATED_ROLLBACK_TEST_CONNECTION" : sameDay
            ? replayEnabled ? "ERPWEB_REPLAY_ISOLATED_SAMEDAY_TEST_CONNECTION"
                : "ERPWEB_REPLAY_ISOLATED_SAMEDAY_OFF_TEST_CONNECTION"
            : concurrent
            ? "ERPWEB_REPLAY_ISOLATED_RACE_TEST_CONNECTION" : goodsReceipt
            ? "ERPWEB_REPLAY_ISOLATED_GR_TEST_CONNECTION"
            : "ERPWEB_REPLAY_ISOLATED_MR_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection))
            return;

        Assert.Contains("test", new SqlConnectionStringBuilder(connection).InitialCatalog,
            StringComparison.OrdinalIgnoreCase);
        var barrier = fifoSameTimeCase == 9 ? new FifoReceiptPostingBarrier() : null;
        var wacBarrier = wacReceiptEditRace ? new WacReceiptPostingBarrier() : null;
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection);
        if (barrier is not null) options.AddInterceptors(barrier);
        if (wacBarrier is not null) options.AddInterceptors(wacBarrier);
        IDbContextFactory<AppDbContext> factory = new TestDbContextFactory(options.Options);
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            Assert.False(await db.IvStockMasters.AnyAsync(s => s.CompanyCode == "DEMO"));
            Assert.False(await db.StockLedgerEpochs.AnyAsync(e => e.CompanyCode == "DEMO"));
            await ProductionLedgerTestFixture.SeedActiveEpochAsync(db);
        }
        if (wacBarrier is not null)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerWacReceiptEditRaceAsync(factory, wacBarrier);
        else if (fifoGuardReplay is bool fifoReplay)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerFifoReceiptReversalGuardAsync(
                factory, goodsReceipt, fifoReplay, fifoGuardSafe, fifoNeutral, concurrent,
                fifoNeutralFailure, fifoNeutralQa, fifoNeutralEdited, fifoNeutralRounding, fifoProjectionInsertion,
                fifoSameTimeCase, barrier);
        else if (reversedOutputLower is bool outputLower)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerReversedIssueAndOutputAsync(
                factory, goodsReceipt, outputLower, concurrent, reversedOutputFailure, earlierOutput, outputEdited,
                outputQuantityEdited, partialOutput);
        else if (earlierReceiptLower is bool earlierLower)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerEarlierReceiptReversedIssueAsync(
                factory, goodsReceipt, earlierLower, earlierReceiptConcurrent, earlierReceiptFailure,
                earlierReceiptEdited, earlierReceiptEditedFailure, earlierReceiptNeutralEdited,
                earlierReceiptQuantityEdited, earlierReceiptQuantityRounding);
        else if (correctedIssueLower is bool lowerCost)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerReversedIssueCostAsync(
                factory, goodsReceipt, lowerCost, concurrent, correctedIssueFailure);
        else if (crossPileReplay is bool crossPileEnabled)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerCrossPileReversedIssuePostAsync(
                factory, goodsReceipt, crossPileEnabled);
        else if (issueDetailDamage is bool damage)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerReplayedIssueDetailAsync(
                factory, goodsReceipt, damage);
        else if (rollbackCostChanged is bool costChanged)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerReversedIssueReceiptRollbackAsync(
                factory, goodsReceipt, costChanged, replayEnabled);
        else if (sameDay)
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerSameDayIssueAsync(factory, replayEnabled);
        else
            await ProductionMaterialIssueSequenceOverlapTests.VerifySqlServerNeutralIssueReceiptAsync(
                factory, goodsReceipt, concurrent);
    }
}
