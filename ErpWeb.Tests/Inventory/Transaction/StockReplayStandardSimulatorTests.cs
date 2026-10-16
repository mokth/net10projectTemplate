using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayStandardSimulatorTests
{
    [Fact]
    public void Historical_count_changes_later_revaluation_but_issue_uses_its_event_revision()
    {
        var count = new StockReplayStandardEvent(-1,
            CountKey(new DateTime(2026, 10, 1), 3, 1),
            StockReplayStandardEventKinds.Count,
            0m, 0m, 2m, 4m, 1,
            AbsoluteCountQty: 12m, CountPreQuantity: 10m);
        var revaluation = new StockReplayStandardEvent(2,
            OrdinaryKey(new DateTime(2026, 10, 2, 8, 0, 0), 4, "V2"),
            StockReplayStandardEventKinds.Revaluation,
            0m, 20m, 0m, 6m, 2);
        var issue = new StockReplayStandardEvent(3,
            OrdinaryKey(new DateTime(2026, 10, 3, 9, 0, 0), 5, "H3"),
            StockReplayStandardEventKinds.Movement,
            -3m, -18m, -3m, 6m, 2);

        var result = StockReplayStandardSimulator.SimulateInsertion(
            7m, 42m, count, [issue, revaluation]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(40m, result.HistoricalValueBeforeCandidate);
        Assert.Equal(2m, result.Candidate.SignedQuantity);
        Assert.Equal(8m, result.Candidate.SignedValue);
        Assert.Equal(24m, result.LaterSteps[0].SignedValue);
        Assert.Equal(-18m, result.LaterSteps[1].SignedValue);
        Assert.Equal(9m, result.FinalQuantity);
        Assert.Equal(54m, result.FinalValue);
    }

    [Fact]
    public void Later_count_resets_its_pile_delta_at_its_approved_revision()
    {
        var receipt = new StockReplayStandardEvent(1,
            OrdinaryKey(new DateTime(2026, 10, 1, 8, 0, 0), 3, "H1"),
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 5m, 4m, 1);
        var laterCount = new StockReplayStandardEvent(-2,
            CountKey(new DateTime(2026, 10, 2), 4, 2),
            StockReplayStandardEventKinds.Count,
            2m, 8m, -3m, 4m, 1,
            AbsoluteCountQty: 10m, CountPreQuantity: 13m);

        var result = StockReplayStandardSimulator.SimulateInsertion(
            10m, 40m, receipt, [laterCount]);

        Assert.Equal(8m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(32m, result.HistoricalValueBeforeCandidate);
        Assert.Equal(20m, result.Candidate.SignedValue);
        Assert.Equal(-3m, Assert.Single(result.LaterSteps).SignedQuantity);
        Assert.Equal(-12m, Assert.Single(result.LaterSteps).SignedValue);
        Assert.Equal(10m, result.FinalQuantity);
        Assert.Equal(40m, result.FinalValue);
    }

    [Fact]
    public void Movement_with_value_other_than_its_event_revision_is_rejected()
    {
        var candidate = new StockReplayStandardEvent(1,
            OrdinaryKey(new DateTime(2026, 10, 1, 8, 0, 0), 3, "H1"),
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 2m, 4m, 1);
        var wrongIssue = new StockReplayStandardEvent(2,
            OrdinaryKey(new DateTime(2026, 10, 2, 8, 0, 0), 4, "H2"),
            StockReplayStandardEventKinds.Movement,
            -1m, -5m, -1m, 4m, 1);

        Assert.Throws<ArgumentException>(() =>
            StockReplayStandardSimulator.SimulateInsertion(
                1m, 4m, candidate, [wrongIssue]));
    }

    [Fact]
    public void Removing_a_historical_count_retargets_the_later_revaluation()
    {
        var count = new StockReplayStandardEvent(-1,
            CountKey(new DateTime(2026, 10, 1), 3, 1),
            StockReplayStandardEventKinds.Count,
            2m, 8m, 2m, 4m, 1,
            AbsoluteCountQty: 12m, CountPreQuantity: 10m);
        var revaluation = new StockReplayStandardEvent(2,
            OrdinaryKey(new DateTime(2026, 10, 2, 8, 0, 0), 4, "V2"),
            StockReplayStandardEventKinds.Revaluation,
            0m, 24m, 0m, 6m, 2);
        var issue = new StockReplayStandardEvent(3,
            OrdinaryKey(new DateTime(2026, 10, 3, 8, 0, 0), 5, "H3"),
            StockReplayStandardEventKinds.Movement,
            -3m, -18m, -3m, 6m, 2);

        var result = StockReplayStandardSimulator.SimulateRemoval(
            9m, 54m, count, [issue, revaluation]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(40m, result.HistoricalValueBeforeRemovedEvent);
        Assert.Equal(20m, result.LaterSteps[0].SignedValue);
        Assert.Equal(7m, result.FinalQuantity);
        Assert.Equal(42m, result.FinalValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardSimulator.SimulateRemoval(
                9m, 55m, count, [issue, revaluation]));
    }

    [Fact]
    public void Removing_a_count_fails_if_a_later_issue_would_overdraw()
    {
        var count = new StockReplayStandardEvent(-1,
            CountKey(new DateTime(2026, 10, 1), 3, 1),
            StockReplayStandardEventKinds.Count,
            2m, 8m, 2m, 4m, 1,
            AbsoluteCountQty: 12m, CountPreQuantity: 10m);
        var issue = new StockReplayStandardEvent(2,
            OrdinaryKey(new DateTime(2026, 10, 2, 8, 0, 0), 4, "H2"),
            StockReplayStandardEventKinds.Movement,
            -11m, -44m, -11m, 4m, 1);

        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardSimulator.SimulateRemoval(
                1m, 4m, count, [issue]));
    }

    [Fact]
    public void Initial_count_uses_frozen_revision_and_exact_pile_projection()
    {
        var key = CountKey(new DateTime(2026, 10, 1), 3, 1);
        var physical = new StockReplayPhysicalEvent(-long.MaxValue + 1,
            key, 2m, 12m);
        var projection = StockReplayPhysicalSimulator.Simulate(10m,
            physical, []);
        var plan = new StockReplayPhysicalPlan("DEMO", "HQ", 1, 1, 2,
            10m, [1], physical, [], projection, "HASH")
        {
            CountHeaderId = 1, CountLineId = 1, CountStatus = "COUNTED"
        };
        var revision = new EffectiveStandardCost("A100",
            new DateTime(2026, 10, 1), null, 4m, 1, 11);
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = 1,
            CostBasisKind = StockCountCostBasisKinds.StandardRevision,
            StandardCostRevisionId = 11, FrozenFallbackUnitCost = 4m
        };

        var count = StockReplayStandardCountEventFactory.CreateInitial(
            plan, "A100", revision, basis);
        var result = StockReplayStandardSimulator.SimulateInsertion(
            10m, 40m, count, []);
        Assert.Equal(2m, result.Candidate.SignedQuantity);
        Assert.Equal(8m, result.Candidate.SignedValue);
        Assert.Equal(12m, result.FinalQuantity);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardCountEventFactory.CreateInitial(
                plan, "A100", revision,
                new StockCountCostBasis
                {
                    CompanyCode = "DEMO", BranchCode = "HQ",
                    StockCountLineId = 1,
                    CostBasisKind = StockCountCostBasisKinds.StandardRevision,
                    StandardCostRevisionId = 12,
                    FrozenFallbackUnitCost = 4m
                }));

        var rebound = StockReplayStandardCountEventFactory
            .BindProjectedPhysicalStep(count,
                new StockReplayPhysicalStep(count.EventId, 13m, -1m, 12m));
        Assert.Equal(-1m, rebound.ReplaySignedQuantity);
        Assert.Equal(13m, rebound.CountPreQuantity);
    }

    [Fact]
    public void Standard_transfer_requires_both_active_legs_at_the_event_revision()
    {
        var at = new DateTime(2026, 10, 1, 8, 0, 0);
        var history = new IvTrxHistory
        {
            Id = 5, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerVersion = 2, LedgerEpochId = 1,
            StockPostingId = 10, EntryRole = "ORIGINAL",
            BatchStatus = "POSTED", ValuationStatus = "VALUED",
            ICode = "A100", TrxDtTime = at,
            FromBalLocId = 1, FrStdQty = 5m,
            ToBalLocId = 2, ToStdQty = 5m
        };
        var key = OrdinaryKey(at, 10, "H0000000005");
        var revision = new EffectiveStandardCost("A100",
            at.Date, null, 4m, 1, 11);
        var outgoing = Fact(1, -1, 5m, 20m, at);
        var incoming = Fact(2, 1, 5m, 20m, at);

        var movement = StockReplayStandardHistoryEventFactory.Create(
            history, 5, key, [outgoing, incoming], revision);
        Assert.Equal(0m, movement.OldSignedQuantity);
        Assert.Equal(0m, movement.OldSignedValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardHistoryEventFactory.Create(
                history, 5, key, [outgoing], revision));
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardHistoryEventFactory.Create(
                history, 5, key,
                [outgoing, Fact(2, 1, 5m, 19m, at)], revision));
    }

    [Fact]
    public void Active_standard_count_rebinds_its_delta_after_an_earlier_receipt()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(
            new DateTime(2026, 10, 2));
        var physical = new StockReplayPhysicalEvent(-7,
            new StockReplayEventKey(countAt,
                StockReplayEventKey.CountClass, 4, "C0000000001", 0),
            2m, 12m);
        var generation = new StockCountReplayGeneration
        {
            Id = 7, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingSequence = 4,
            WritePostingId = 10, EffectiveAt = countAt,
            SignedAdjustmentQty = 2m, AdjustmentHistoryId = 50
        };
        var revision = new EffectiveStandardCost("A100",
            new DateTime(2026, 10, 1), null, 4m, 1, 11);
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ", StockCountLineId = 1,
            CostBasisKind = StockCountCostBasisKinds.StandardRevision,
            StandardCostRevisionId = 11, FrozenFallbackUnitCost = 4m
        };
        var active = StockReplayStandardCountEventFactory.CreateActive(
            physical, generation, "A100", revision,
            [Fact(1, 1, 2m, 8m, countAt, historyId: 50)], basis);
        var rebound = StockReplayStandardCountEventFactory
            .BindProjectedPhysicalStep(active,
                new StockReplayPhysicalStep(-7, 15m, -3m, 12m));
        var receipt = new StockReplayStandardEvent(1,
            OrdinaryKey(new DateTime(2026, 10, 1, 8, 0, 0), 3, "H1"),
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 5m, 4m, 11);

        var result = StockReplayStandardSimulator.SimulateInsertion(
            12m, 48m, receipt, [rebound]);
        Assert.Equal(-3m, Assert.Single(result.LaterSteps).SignedQuantity);
        Assert.Equal(-12m, Assert.Single(result.LaterSteps).SignedValue);
        Assert.Equal(12m, result.FinalQuantity);
        Assert.Equal(48m, result.FinalValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardCountEventFactory.CreateActive(
                physical, generation, "A100", revision,
                [Fact(1, 1, 2m, 9m, countAt, historyId: 50)], basis));
    }

    [Fact]
    public void Zero_effect_active_count_can_reset_a_later_replayed_receipt()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(
            new DateTime(2026, 10, 2));
        var physical = new StockReplayPhysicalEvent(-7,
            new StockReplayEventKey(countAt,
                StockReplayEventKey.CountClass, 4, "C0000000001", 0),
            0m, 0m);
        var generation = new StockCountReplayGeneration
        {
            Id = 7, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingSequence = 4,
            WritePostingId = 10, EffectiveAt = countAt,
            SignedAdjustmentQty = 0m
        };
        var revision = new EffectiveStandardCost("A100",
            new DateTime(2026, 10, 1), null, 4m, 1, 11);
        var active = StockReplayStandardCountEventFactory.CreateActive(
            physical, generation, "A100", revision, [], null);
        var rebound = StockReplayStandardCountEventFactory
            .BindProjectedPhysicalStep(active,
                new StockReplayPhysicalStep(-7, 5m, -5m, 0m));
        var receipt = new StockReplayStandardEvent(1,
            OrdinaryKey(new DateTime(2026, 10, 1, 8, 0, 0), 3, "H1"),
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 5m, 4m, 11);

        var result = StockReplayStandardSimulator.SimulateInsertion(
            0m, 0m, receipt, [rebound]);
        Assert.Equal(-5m, Assert.Single(result.LaterSteps).SignedQuantity);
        Assert.Equal(0m, result.FinalQuantity);
        Assert.Equal(0m, result.FinalValue);
    }

    [Fact]
    public void Corrected_standard_revaluation_keeps_its_original_business_key()
    {
        var at = new DateTime(2026, 10, 2, 8, 0, 0);
        var owner = new StockPosting
        {
            Id = 10, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 1, PostingSequence = 4,
            SourceDocumentType = "STANDARD_COST_REVALUATION",
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = "202610", SealedAtUtc = DateTime.UtcNow
        };
        var correctionOwner = new StockPosting
        {
            Id = 12, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 1, PostingSequence = 6,
            SealedAtUtc = DateTime.UtcNow
        };
        var original = RevaluationFact(8, 10, 20m, at);
        var corrected = RevaluationFact(9, 12, 24m, at);
        corrected.OriginalValuationFactId = original.Id;
        var revision = new EffectiveStandardCost("A100", at.Date,
            null, 6m, 2, 22);

        var originalEvent = StockReplayStandardRevaluationEventFactory
            .Create(original, owner, revision);
        var correctedEvent = StockReplayStandardRevaluationEventFactory
            .CreateCorrected(corrected, original, owner,
                correctionOwner, revision);
        Assert.Equal(originalEvent.Key, correctedEvent.Key);
        Assert.Equal(20m, originalEvent.OldSignedValue);
        Assert.Equal(24m, correctedEvent.OldSignedValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayStandardRevaluationEventFactory.CreateCorrected(
                RevaluationFact(9, 12, 24m, at), original, owner,
                correctionOwner, revision));
    }

    [Fact]
    public void Item_projector_binds_another_piles_count_and_retargets_revaluation()
    {
        var receiptKey = OrdinaryKey(
            new DateTime(2026, 10, 1, 8, 0, 0), 3, "H1");
        var physicalReceipt = new StockReplayPhysicalEvent(1,
            receiptKey, 5m);
        var physicalProjection = StockReplayPhysicalSimulator.Simulate(
            10m, physicalReceipt, []);
        var plan = new StockReplayPhysicalPlan("DEMO", "HQ", 1, 1, 2,
            10m, [1], physicalReceipt, [], physicalProjection, "HASH");
        var countKey = CountKey(new DateTime(2026, 10, 2), 4, 1);
        var physicalCount = new StockReplayPhysicalEvent(-7,
            countKey, 2m, 7m);
        var count = new StockReplayStandardEvent(-7, countKey,
            StockReplayStandardEventKinds.Count,
            2m, 8m, 2m, 4m, 11,
            AbsoluteCountQty: 7m, CountPreQuantity: 5m);
        var revaluation = new StockReplayStandardEvent(
            long.MaxValue - 8,
            OrdinaryKey(new DateTime(2026, 10, 3, 8, 0, 0),
                5, $"V{8L:D20}"),
            StockReplayStandardEventKinds.Revaluation,
            0m, 34m, 0m, 6m, 22);
        var tail = new StockReplayStandardFinancialTail(
            17m, 102m, [count, revaluation],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [], [2] = [physicalCount]
            },
            new Dictionary<int, decimal> { [1] = 10m, [2] = 7m });
        var receipt = new StockReplayStandardEvent(1, receiptKey,
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 5m, 4m, 11);

        var result = StockReplayStandardTailProjector
            .SimulateSinglePileInsertion(tail, plan, receipt);

        Assert.Equal(15m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(44m, result.LaterSteps[1].SignedValue);
        Assert.Equal(22m, result.FinalQuantity);
        Assert.Equal(132m, result.FinalValue);
    }

    [Fact]
    public void Item_projector_removes_count_before_later_issue()
    {
        var countKey = CountKey(new DateTime(2026, 10, 1), 3, 1);
        var physicalCount = new StockReplayPhysicalEvent(-7,
            countKey, 2m, 12m);
        var issueKey = OrdinaryKey(
            new DateTime(2026, 10, 2, 8, 0, 0), 4, "H3");
        var physicalIssue = new StockReplayPhysicalEvent(3,
            issueKey, -3m);
        var physicalProjection = StockReplayPhysicalSimulator
            .SimulateRemoval(9m, physicalCount, [physicalIssue]);
        var plan = new StockReplayPhysicalRemovalPlan(
            "DEMO", "HQ", 1, 1, 4, 9m, [1],
            physicalCount, [physicalIssue], physicalProjection, "HASH");
        var count = new StockReplayStandardEvent(-7, countKey,
            StockReplayStandardEventKinds.Count,
            2m, 8m, 2m, 4m, 11,
            AbsoluteCountQty: 12m, CountPreQuantity: 10m);
        var issue = new StockReplayStandardEvent(3, issueKey,
            StockReplayStandardEventKinds.Movement,
            -3m, -12m, -3m, 4m, 11);
        var tail = new StockReplayStandardFinancialTail(
            9m, 36m, [issue],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [physicalIssue]
            },
            new Dictionary<int, decimal> { [1] = 9m });

        var result = StockReplayStandardTailProjector
            .SimulateSinglePileRemoval(tail, plan, count);

        Assert.Equal(10m, result.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(7m, result.FinalQuantity);
        Assert.Equal(28m, result.FinalValue);
    }

    [Fact]
    public void Two_pile_standard_count_sheet_retargets_later_revaluation_once()
    {
        var countAt = new DateTime(2026, 10, 1);
        var plans = new List<StockReplayPhysicalPlan>();
        var candidates = new List<StockReplayStandardEvent>();
        foreach (var (lineId, pileId, current, observation) in new[]
            {
                (1, 1, 10m, 12m),
                (2, 2, 5m, 7m)
            })
        {
            var key = CountKey(countAt, 3, lineId);
            var id = -long.MaxValue + lineId;
            var physical = new StockReplayPhysicalEvent(id,
                key, observation - current, observation);
            plans.Add(new StockReplayPhysicalPlan(
                "DEMO", "HQ", pileId, 1, 2, current, [1],
                physical, [], StockReplayPhysicalSimulator.Simulate(
                    current, physical, []), "HASH")
            {
                CountHeaderId = 1, CountLineId = lineId,
                CountStatus = "COUNTED"
            });
            candidates.Add(new StockReplayStandardEvent(id, key,
                StockReplayStandardEventKinds.Count,
                0m, 0m, observation - current,
                4m, 11, observation, current));
        }
        var revaluation = new StockReplayStandardEvent(
            long.MaxValue - 8,
            OrdinaryKey(new DateTime(2026, 10, 2, 8, 0, 0),
                4, $"V{8L:D20}"),
            StockReplayStandardEventKinds.Revaluation,
            0m, 30m, 0m, 6m, 22);
        var tail = new StockReplayStandardFinancialTail(
            15m, 90m, [revaluation],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [], [2] = []
            },
            new Dictionary<int, decimal> { [1] = 10m, [2] = 5m });

        var result = StockReplayStandardCountSheetProjector
            .SimulateInsertion(tail, plans, candidates);

        Assert.Equal(15m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(8m, result.Candidate.SignedValue);
        Assert.Equal(8m, result.LaterSteps[0].SignedValue);
        Assert.Equal(38m, result.LaterSteps[1].SignedValue);
        Assert.Equal(19m, result.FinalQuantity);
        Assert.Equal(114m, result.FinalValue);
    }

    private static StockValuationFact RevaluationFact(
        long id, long postingId, decimal amount, DateTime at) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ",
        LedgerEpochId = 1, StockPostingId = postingId,
        EffectiveAt = at, BusinessDate = at.Date, PeriodKey = "202610",
        SourceDocumentType = "STANDARD_COST_REVALUATION",
        SourceDocumentId = "R1", SourceDocumentNo = "R1",
        SourceDocumentLine = "1", SourceLineId = "R1:1",
        ItemCode = "A100", BaseQty = 0m,
        CostMethod = StockCostMethods.Standard,
        ValuationSource = StockValuationSources.StandardRevaluation,
        ValuationStatus = StockValuationStatuses.Valued,
        MovementCode = "STANDARD_REVALUE_IN",
        Direction = 1, CostAmount = amount
    };

    private static StockValuationFact Fact(long id, int direction,
        decimal quantity, decimal amount, DateTime at,
        int historyId = 5) => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ",
        LedgerEpochId = 1, StockPostingId = 10,
        InventoryHistoryId = historyId, ItemCode = "A100",
        EffectiveAt = at, CostMethod = StockCostMethods.Standard,
        ValuationStatus = StockValuationStatuses.Valued,
        Direction = direction, BaseQty = quantity, CostAmount = amount
    };

    private static StockReplayEventKey OrdinaryKey(
        DateTime at, long sequence, string identity) =>
        new(at, StockReplayEventKey.OrdinaryClass, sequence, identity, 0);

    private static StockReplayEventKey CountKey(
        DateTime day, long sequence, int lineId) =>
        new(StockReplayEventKey.CountEndOfDay(day),
            StockReplayEventKey.CountClass, sequence,
            $"C{lineId:D10}", 0);
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayStandardRevisionAuthorityTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
        await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 4m,
            effectiveFrom: new DateTime(2026, 10, 1));
        var first = await db.ItemStandardCostRevisions.SingleAsync();
        first.EffectiveTo = new DateTime(2026, 10, 2);
        db.ItemStandardCostRevisions.Add(new ItemStandardCostRevision
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            ItemCode = "A100", EffectiveFrom = new DateTime(2026, 10, 2),
            MaterialCost = 6m, TotalStandardCost = 6m,
            Revision = 2, Status = ItemStandardCostRevisionStatuses.Approved,
            ApprovedBy = "TEST", ApprovedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST", CreatedAtUtc = DateTime.UtcNow,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Every_event_uses_its_approved_event_date_revision()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var revisions = await db.ItemStandardCostRevisions.AsNoTracking()
            .OrderBy(x => x.Revision).ToArrayAsync();
        var candidate = Movement(-1, new DateTime(2026, 10, 1, 8, 0, 0),
            4m, revisions[0].Id);
        var later = Movement(2, new DateTime(2026, 10, 2, 8, 0, 0),
            6m, revisions[1].Id);

        await ValidateAsync(db, candidate, [later]);
        var wrongId = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ValidateAsync(db, candidate with { StandardCostRevisionId = revisions[1].Id },
                [later]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongId.Error.Code);
        var wrongCost = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ValidateAsync(db, candidate,
                [later with { UnitCost = 4m }]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, wrongCost.Error.Code);
    }

    [Fact]
    public async Task Method_cutover_between_events_is_rejected_even_without_an_event_on_that_date()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var policy = await db.StockCostPolicyRevisions.SingleAsync();
        policy.EffectiveTo = new DateTime(2026, 10, 2);
        db.StockCostPolicyRevisions.AddRange(
            Policy(StockCostMethods.Fifo, new DateTime(2026, 10, 2),
                new DateTime(2026, 10, 3)),
            Policy(StockCostMethods.Standard, new DateTime(2026, 10, 3), null));
        await db.SaveChangesAsync();
        var revisions = await db.ItemStandardCostRevisions.AsNoTracking()
            .OrderBy(x => x.Revision).ToArrayAsync();

        var error = await Assert.ThrowsAsync<StockLedgerException>(() =>
            ValidateAsync(db,
                Movement(-1, new DateTime(2026, 10, 1, 8, 0, 0),
                    4m, revisions[0].Id),
                [Movement(2, new DateTime(2026, 10, 3, 8, 0, 0),
                    6m, revisions[1].Id)]));
        Assert.Equal(StockLedgerErrorCodes.CostPolicyInvalid, error.Error.Code);
    }

    private static StockCostPolicyRevision Policy(
        string method, DateTime from, DateTime? to) => new()
    {
        CompanyCode = InventoryLedgerTestFixture.CompanyCode,
        BranchCode = InventoryLedgerTestFixture.BranchCode,
        CostMethod = method, EffectiveFrom = from, EffectiveTo = to,
        Status = StockCostPolicyStatuses.Active,
        ApprovedBy = "TEST", ApprovedAtUtc = DateTime.UtcNow,
        CreatedBy = "TEST", CreatedAtUtc = DateTime.UtcNow,
        RowVersion = [1]
    };

    private static StockReplayStandardEvent Movement(
        long id, DateTime at, decimal unitCost, long revisionId) =>
        new(id, new StockReplayEventKey(at,
                StockReplayEventKey.OrdinaryClass, Math.Abs(id) + 1, "H1", 0),
            StockReplayStandardEventKinds.Movement,
            0m, 0m, 1m, unitCost, revisionId);

    private static Task ValidateAsync(
        AppDbContext db,
        StockReplayStandardEvent candidate,
        IReadOnlyCollection<StockReplayStandardEvent> later) =>
        StockReplayStandardRevisionAuthority.ValidateAsync(db,
            InventoryLedgerTestFixture.CompanyCode,
            InventoryLedgerTestFixture.BranchCode, "A100", candidate, later);
}
