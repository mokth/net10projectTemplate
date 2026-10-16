using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.StockLedger;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class StockReplayWacSimulatorTests
{
    [Fact]
    public void Late_receipt_revalues_a_later_issue_at_the_replayed_average()
    {
        var receipt = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            OldSignedQuantity: 0m, OldSignedValue: 0m,
            ReplaySignedQuantity: 5m, FixedSignedValue: 20m);
        var issue = new StockReplayWacEvent(2,
            Key(new DateTime(2026, 10, 2, 8, 0, 0), 2),
            OldSignedQuantity: -5m, OldSignedValue: -10m,
            ReplaySignedQuantity: -5m);

        var result = StockReplayWacSimulator.SimulateInsertion(
            5m, 10m, receipt, [issue]);

        Assert.Equal(10m, result.HistoricalQuantityBeforeCandidate);
        Assert.Equal(20m, result.HistoricalValueBeforeCandidate);
        Assert.Equal(40m, result.Candidate.ValueAfter);
        Assert.Equal(-13.333335m, Assert.Single(result.LaterSteps).SignedValue);
        Assert.Equal(10m, result.FinalQuantity);
        Assert.Equal(26.666665m, result.FinalValue);
    }

    [Fact]
    public void Later_count_recomputes_signed_quantity_and_value_at_the_original_anchor()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var receipt = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            0m, 0m, 5m, FixedSignedValue: 10m);
        var count = new StockReplayWacEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0),
            2m, 4m, 2m, AbsoluteCountQty: 10m, CountPreQuantity: 13m);

        var result = StockReplayWacSimulator.SimulateInsertion(
            10m, 20m, receipt, [count]);

        var step = Assert.Single(result.LaterSteps);
        Assert.Equal(-3m, step.SignedQuantity);
        Assert.Equal(-6m, step.SignedValue);
        Assert.Equal(10m, result.FinalQuantity);
        Assert.Equal(20m, result.FinalValue);
    }

    [Fact]
    public void Count_on_one_pile_preserves_the_other_piles_item_quantity()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var candidate = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            0m, 0m, 5m, FixedSignedValue: 10m);
        var unboundCount = new StockReplayWacEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0),
            2m, 4m, 2m, AbsoluteCountQty: 3m);
        Assert.Throws<ArgumentException>(() =>
            StockReplayWacSimulator.SimulateInsertion(
                10m, 20m, candidate, [unboundCount]));

        var count = StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
            unboundCount, new StockReplayPhysicalStep(-10, 6m, -3m, 3m));
        var result = StockReplayWacSimulator.SimulateInsertion(
            10m, 20m, candidate, [count]);

        Assert.Equal(-3m, Assert.Single(result.LaterSteps).SignedQuantity);
        Assert.Equal(10m, result.FinalQuantity);
        Assert.Equal(20m, result.FinalValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountEventFactory.BindProjectedPhysicalStep(
                unboundCount, new StockReplayPhysicalStep(-10, 6m, -2m, 3m)));
    }

    [Fact]
    public void Item_tail_projector_binds_count_on_changed_pile_and_preserves_other_pile()
    {
        var at = new DateTime(2026, 10, 1, 8, 0, 0);
        var countAt = StockReplayEventKey.CountEndOfDay(at.AddDays(1));
        var candidatePhysical = new StockReplayPhysicalEvent(1, Key(at, 3), 5m);
        var countPhysical = new StockReplayPhysicalEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0), 2m, 3m);
        var projection = StockReplayPhysicalSimulator.Simulate(
            3m, candidatePhysical, [countPhysical]);
        var physicalPlan = new StockReplayPhysicalPlan(
            "DEMO", "HQ", 1, 1, 3, 3m, [], candidatePhysical,
            [countPhysical], projection, "hash");
        var countWac = new StockReplayWacEvent(-10, countPhysical.Key,
            2m, 4m, 2m, AbsoluteCountQty: 3m,
            CountFallbackUnitCost: 2m);
        var tail = new StockReplayWacFinancialTail(
            10m, 20m, [countWac],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [countPhysical], [2] = []
            },
            new Dictionary<int, decimal> { [1] = 3m, [2] = 7m });
        var candidateWac = new StockReplayWacEvent(1, candidatePhysical.Key,
            0m, 0m, 5m, FixedSignedValue: 10m);

        var financial = StockReplayWacTailProjector.SimulateSinglePileInsertion(
            tail, physicalPlan, candidateWac);
        Assert.Equal(-3m, Assert.Single(financial.LaterSteps).SignedQuantity);
        Assert.Equal(10m, financial.FinalQuantity);
        Assert.Equal(20m, financial.FinalValue);

        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacTailProjector.SimulateSinglePileInsertion(
                tail with { CurrentQuantityByPile =
                    new Dictionary<int, decimal> { [1] = 3m, [2] = 8m } },
                physicalPlan, candidateWac));
    }

    [Fact]
    public void Two_counted_piles_share_one_WAC_pool_before_later_receipt_and_issue()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(
            new DateTime(2026, 10, 1));
        var firstKey = new StockReplayEventKey(countAt,
            StockReplayEventKey.CountClass, 3, "C0000000001", 0);
        var secondKey = firstKey with { LineIdentity = "C0000000002" };
        var receiptKey = Key(new DateTime(2026, 10, 2, 9, 0, 0), 1);
        var issueKey = Key(new DateTime(2026, 10, 3, 9, 0, 0), 2);
        var firstCount = new StockReplayPhysicalEvent(
            -long.MaxValue + 1, firstKey, 0m, 10m);
        var secondCount = new StockReplayPhysicalEvent(
            -long.MaxValue + 2, secondKey, 0m, 5m);
        var receipt = new StockReplayPhysicalEvent(100, receiptKey, 10m);
        var issue = new StockReplayPhysicalEvent(101, issueKey, -5m);
        var firstPlan = new StockReplayPhysicalPlan(
            "DEMO", "HQ", 1, 1, 2, 5m, [], firstCount,
            [receipt, issue], StockReplayPhysicalSimulator.Simulate(
                5m, firstCount, [receipt, issue]), "first")
        {
            CountHeaderId = 1, CountLineId = 1, CountStatus = "COUNTED"
        };
        var secondPlan = new StockReplayPhysicalPlan(
            "DEMO", "HQ", 2, 1, 2, 0m, [], secondCount,
            [], StockReplayPhysicalSimulator.Simulate(
                0m, secondCount, []), "second")
        {
            CountHeaderId = 1, CountLineId = 2, CountStatus = "COUNTED"
        };
        var financialTail = new StockReplayWacFinancialTail(
            5m, 10m,
            [new StockReplayWacEvent(100, receiptKey,
                10m, 20m, 10m, FixedSignedValue: 20m),
             new StockReplayWacEvent(101, issueKey,
                -5m, -10m, -5m)],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [receipt, issue], [2] = []
            },
            new Dictionary<int, decimal> { [1] = 5m, [2] = 0m });
        var firstWac = new StockReplayWacEvent(firstCount.EventId,
            firstKey, 0m, 0m, 10m,
            AbsoluteCountQty: 10m, CountFallbackUnitCost: 4m);
        var secondWac = new StockReplayWacEvent(secondCount.EventId,
            secondKey, 0m, 0m, 5m,
            AbsoluteCountQty: 5m, CountFallbackUnitCost: 2m);

        var projected = StockReplayWacTailProjector.SimulateCountSheetInsertion(
            financialTail, [secondPlan, firstPlan], [secondWac, firstWac]);

        Assert.Equal(0m, projected.HistoricalQuantityBeforeCandidate);
        Assert.Equal(40m, projected.Candidate.SignedValue);
        Assert.Equal(new[] { 20m, 20m, -16m },
            projected.LaterSteps.Select(s => s.SignedValue));
        Assert.Equal(20m, projected.FinalQuantity);
        Assert.Equal(64m, projected.FinalValue);

        var laterCountKey = new StockReplayEventKey(
            StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 4)),
            StockReplayEventKey.CountClass, 4, "C0000000010", 0);
        var laterCount = new StockReplayPhysicalEvent(
            -10, laterCountKey, 3m, 3m);
        var secondWithLaterCount = secondPlan with
        {
            CurrentQuantity = 3m,
            LaterEvents = [laterCount],
            Projection = StockReplayPhysicalSimulator.Simulate(
                3m, secondCount, [laterCount])
        };
        var extendedTail = financialTail with
        {
            CurrentQuantity = 8m,
            CurrentValue = 16m,
            LaterEvents = financialTail.LaterEvents.Concat(
                [new StockReplayWacEvent(-10, laterCountKey,
                    3m, 6m, 3m, AbsoluteCountQty: 3m,
                    CountFallbackUnitCost: 2m)]).ToArray(),
            PhysicalByPile = new Dictionary<int,
                IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [receipt, issue], [2] = [laterCount]
            },
            CurrentQuantityByPile = new Dictionary<int, decimal>
            {
                [1] = 5m, [2] = 3m
            }
        };
        var withLaterCount = StockReplayWacTailProjector
            .SimulateCountSheetInsertion(extendedTail,
                [firstPlan, secondWithLaterCount], [firstWac, secondWac]);
        Assert.Equal(new[] { 20m, 20m, -16m, -6.4m },
            withLaterCount.LaterSteps.Select(s => s.SignedValue));
        Assert.Equal(-2m, withLaterCount.LaterSteps[^1].SignedQuantity);
        Assert.Equal(18m, withLaterCount.FinalQuantity);
        Assert.Equal(57.6m, withLaterCount.FinalValue);

        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacTailProjector.SimulateCountSheetInsertion(
                financialTail with
                {
                    CurrentQuantityByPile = new Dictionary<int, decimal>
                    {
                        [1] = 5m, [2] = 1m
                    }
                }, [firstPlan, secondPlan], [firstWac, secondWac]));
    }

    [Fact]
    public void Item_tail_projector_binds_a_count_on_an_unchanged_pile()
    {
        var at = new DateTime(2026, 10, 1, 8, 0, 0);
        var countAt = StockReplayEventKey.CountEndOfDay(at.AddDays(1));
        var candidatePhysical = new StockReplayPhysicalEvent(1, Key(at, 3), 5m);
        var countPhysical = new StockReplayPhysicalEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000002", 0), 1m, 7m);
        var plan = new StockReplayPhysicalPlan(
            "DEMO", "HQ", 1, 1, 3, 3m, [], candidatePhysical,
            [], StockReplayPhysicalSimulator.Simulate(
                3m, candidatePhysical, []), "hash");
        var tail = new StockReplayWacFinancialTail(
            10m, 20m,
            [new StockReplayWacEvent(-10, countPhysical.Key,
                1m, 2m, 1m, AbsoluteCountQty: 7m,
                CountFallbackUnitCost: 2m)],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [], [2] = [countPhysical]
            },
            new Dictionary<int, decimal> { [1] = 3m, [2] = 7m });
        var candidateWac = new StockReplayWacEvent(1, candidatePhysical.Key,
            0m, 0m, 5m, FixedSignedValue: 10m);

        var financial = StockReplayWacTailProjector.SimulateSinglePileInsertion(
            tail, plan, candidateWac);
        Assert.Equal(1m, Assert.Single(financial.LaterSteps).SignedQuantity);
        Assert.Equal(15m, financial.FinalQuantity);
        Assert.Equal(30m, financial.FinalValue);
    }

    [Fact]
    public void Item_tail_projector_removes_one_pile_count_and_replays_other_pile_issue()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1));
        var removedPhysical = new StockReplayPhysicalEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                1, "C0000000001", 0), 2m, 3m);
        var issuePhysical = new StockReplayPhysicalEvent(2,
            Key(new DateTime(2026, 10, 2), 2), -3m);
        var removalPlan = new StockReplayPhysicalRemovalPlan(
            "DEMO", "HQ", 1, 1, 2, 3m, [], removedPhysical,
            [], StockReplayPhysicalSimulator.SimulateRemoval(
                3m, removedPhysical, []), "hash");
        var tail = new StockReplayWacFinancialTail(
            7m, 14m,
            [new StockReplayWacEvent(2, issuePhysical.Key,
                -3m, -6m, -3m)],
            new Dictionary<int, IReadOnlyList<StockReplayPhysicalEvent>>
            {
                [1] = [], [2] = [issuePhysical]
            },
            new Dictionary<int, decimal> { [1] = 3m, [2] = 4m });
        var removedWac = new StockReplayWacEvent(-10, removedPhysical.Key,
            2m, 4m, 2m, AbsoluteCountQty: 3m,
            CountFallbackUnitCost: 2m);

        var financial = StockReplayWacTailProjector.SimulateSinglePileRemoval(
            tail, removalPlan, removedWac);
        Assert.Equal(8m, financial.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(16m, financial.HistoricalValueBeforeRemovedEvent);
        Assert.Equal(5m, financial.FinalQuantity);
        Assert.Equal(10m, financial.FinalValue);
    }

    [Fact]
    public void Full_depletion_uses_exact_remaining_value_after_a_cost_delta()
    {
        var costDelta = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            0m, 0m, 0m, FixedSignedValue: 0.000003m);
        var issue = new StockReplayWacEvent(2,
            Key(new DateTime(2026, 10, 2, 8, 0, 0), 2),
            -3m, -0.999999m, -3m);

        var result = StockReplayWacSimulator.SimulateInsertion(
            0m, 0m, costDelta, [issue]);

        Assert.Equal(-1.000002m, Assert.Single(result.LaterSteps).SignedValue);
        Assert.Equal(0m, result.FinalQuantity);
        Assert.Equal(0m, result.FinalValue);
    }

    [Fact]
    public void Removing_a_receipt_reprices_a_later_issue_from_the_remaining_pool()
    {
        var receipt = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1), 1),
            5m, 10m, 5m, FixedSignedValue: 10m);
        var issue = new StockReplayWacEvent(2,
            Key(new DateTime(2026, 10, 2), 2),
            -5m, -10m, -5m);

        var removed = StockReplayWacSimulator.SimulateRemoval(
            5m, 10m, receipt, [issue]);
        Assert.Equal(5m, removed.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(10m, removed.HistoricalValueBeforeRemovedEvent);
        Assert.Equal(-10m, Assert.Single(removed.LaterSteps).SignedValue);
        Assert.Equal(0m, removed.FinalQuantity);
        Assert.Equal(0m, removed.FinalValue);
    }

    [Fact]
    public void Removing_one_pile_count_preserves_other_piles_and_replays_later_issue()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 1));
        var count = new StockReplayWacEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                1, "C0000000001", 0),
            2m, 4m, 2m, AbsoluteCountQty: 3m,
            CountFallbackUnitCost: 2m, CountPreQuantity: 1m);
        var issue = new StockReplayWacEvent(2,
            Key(new DateTime(2026, 10, 2), 2),
            -3m, -6m, -3m);

        var removed = StockReplayWacSimulator.SimulateRemoval(
            7m, 14m, count, [issue]);
        Assert.Equal(8m, removed.HistoricalQuantityBeforeRemovedEvent);
        Assert.Equal(16m, removed.HistoricalValueBeforeRemovedEvent);
        Assert.Equal(5m, removed.FinalQuantity);
        Assert.Equal(10m, removed.FinalValue);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacSimulator.SimulateRemoval(
                7m, 14m, count with { CountPreQuantity = 0m }, [issue]));
    }

    [Fact]
    public void Count_receipt_from_zero_requires_frozen_cost_authority()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var candidate = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            0m, 0m, 0m, FixedSignedValue: 0m);
        var count = new StockReplayWacEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0),
            1m, 1m, 1m, AbsoluteCountQty: 1m, CountPreQuantity: 0m);

        var failure = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacSimulator.SimulateInsertion(1m, 1m, candidate, [count]));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, failure.Error.Code);

        var approved = StockReplayWacSimulator.SimulateInsertion(
            1m, 1m, candidate, [count with { CountFallbackUnitCost = 1.234567m }]);
        Assert.Equal(1.234567m, Assert.Single(approved.LaterSteps).SignedValue);
    }

    [Fact]
    public void Positive_count_uses_replayed_average_before_frozen_fallback()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var costDelta = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1, 8, 0, 0), 3),
            0m, 0m, 0m, FixedSignedValue: 10m);
        var count = new StockReplayWacEvent(-10,
            new StockReplayEventKey(countAt, StockReplayEventKey.CountClass,
                2, "C0000000001", 0),
            2m, 4m, 2m,
            AbsoluteCountQty: 12m, CountFallbackUnitCost: 100m,
            CountPreQuantity: 10m);

        var result = StockReplayWacSimulator.SimulateInsertion(
            12m, 24m, costDelta, [count]);

        Assert.Equal(6m, Assert.Single(result.LaterSteps).SignedValue);
        Assert.Equal(36m, result.FinalValue);
    }

    [Fact]
    public void Later_ordinary_effect_cannot_change_physical_quantity_without_lineage()
    {
        var candidate = new StockReplayWacEvent(1,
            Key(new DateTime(2026, 10, 1), 3),
            0m, 0m, 1m, FixedSignedValue: 2m);
        var forged = new StockReplayWacEvent(2,
            Key(new DateTime(2026, 10, 2), 2),
            -1m, -2m, -2m);

        Assert.Throws<ArgumentException>(() =>
            StockReplayWacSimulator.SimulateInsertion(0m, 0m,
                candidate, [forged]));
    }

    [Fact]
    public void Count_financial_event_requires_matching_active_generation_and_wac_fact()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var key = new StockReplayEventKey(countAt,
            StockReplayEventKey.CountClass, 2, "C0000000001", 0);
        var physical = new StockReplayPhysicalEvent(-10, key, 2m, 12m);
        var generation = new StockCountReplayGeneration
        {
            Id = 10, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingId = 2,
            WritePostingId = 3, AnchorPostingSequence = 2,
            EffectiveAt = countAt, SignedAdjustmentQty = 2m,
            AdjustmentHistoryId = 5, AdjustmentBatchNo = 50
        };
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1,
            CostBasisKind = StockCountCostBasisKinds.ManualApproved,
            FrozenFallbackUnitCost = 3.123457m,
            CostEvidenceType = StockCountCostBasisKinds.ManualApproved,
            CostOverrideReason = "Approved count basis",
            CostApprovedBy = "TEST", CostApprovedAtUtc = DateTime.UtcNow
        };
        var fact = new StockValuationFact
        {
            Id = 100,
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockPostingId = 3, EffectiveAt = countAt,
            InventoryHistoryId = 5,
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 2m, CostAmount = 4m
        };

        var replay = StockReplayWacCountEventFactory.Create(
            physical, generation, [fact], basis);
        Assert.Equal(2m, replay.OldSignedQuantity);
        Assert.Equal(4m, replay.OldSignedValue);
        Assert.Equal(12m, replay.AbsoluteCountQty);
        Assert.Equal(3.123457m, replay.CountFallbackUnitCost);

        fact.BaseQty = 3m;
        var mismatch = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountEventFactory.Create(
                physical, generation, [fact], basis));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, mismatch.Error.Code);

        fact.BaseQty = 2m;
        var corrected = new StockValuationFact
        {
            Id = 101, CompanyCode = "DEMO", BranchCode = "HQ",
            StockPostingId = 4, EffectiveAt = countAt,
            InventoryHistoryId = 5,
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 2m, CostAmount = 6m,
            OriginalValuationFactId = fact.Id
        };
        var correction = StockReplayWacCountEventFactory.Create(
            physical, generation, [corrected], basis, fact);
        Assert.Equal(6m, correction.OldSignedValue);
        var missingOrigin = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountEventFactory.Create(
                physical, generation, [corrected], basis));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, missingOrigin.Error.Code);
        fact.BaseQty = 3m;
        var alteredOrigin = Assert.Throws<StockLedgerException>(() =>
            StockReplayWacCountEventFactory.Create(
                physical, generation, [corrected], basis, fact));
        Assert.Equal(StockLedgerErrorCodes.LedgerMismatch, alteredOrigin.Error.Code);
    }

    [Fact]
    public void Zero_adjustment_count_has_no_old_wac_fact_but_keeps_frozen_fallback()
    {
        var countAt = StockReplayEventKey.CountEndOfDay(new DateTime(2026, 10, 2));
        var eventKey = new StockReplayEventKey(countAt,
            StockReplayEventKey.CountClass, 2, "C0000000001", 0);
        var physical = new StockReplayPhysicalEvent(-11, eventKey, 0m, 12m);
        var generation = new StockCountReplayGeneration
        {
            Id = 11, CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1, AnchorPostingId = 2,
            WritePostingId = 2, AnchorPostingSequence = 2,
            EffectiveAt = countAt, SignedAdjustmentQty = 0m
        };
        var basis = new StockCountCostBasis
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            StockCountLineId = 1,
            CostBasisKind = StockCountCostBasisKinds.ZeroCostApproved,
            FrozenFallbackUnitCost = 0m,
            CostEvidenceType = StockCountCostBasisKinds.ZeroCostApproved,
            CostOverrideReason = "Approved zero cost",
            CostApprovedBy = "TEST", CostApprovedAtUtc = DateTime.UtcNow
        };

        var result = StockReplayWacCountEventFactory.Create(
            physical, generation, [], basis);
        Assert.Equal(0m, result.OldSignedValue);
        Assert.Equal(0m, result.OldSignedQuantity);
        Assert.Equal(0m, result.CountFallbackUnitCost);
    }

    [Fact]
    public void Ordinary_history_factory_preserves_receipt_authority_and_reprices_an_issue()
    {
        var receiptAt = new DateTime(2026, 10, 1, 8, 0, 0);
        var issueAt = receiptAt.AddDays(1);
        var receipt = History(10, receiptAt, 0m, 5m);
        var issue = History(11, issueAt, 5m, 0m);
        var receiptEvent = StockReplayWacHistoryEventFactory.Create(
            receipt, 10, HistoryKey(receiptAt, 2, 10),
            [FactFor(receipt, 20, 1, 5m, 10m)]);
        var issueEvent = StockReplayWacHistoryEventFactory.Create(
            issue, 11, HistoryKey(issueAt, 3, 11),
            [FactFor(issue, 21, -1, 5m, 10m)]);

        var candidate = new StockReplayWacEvent(12,
            HistoryKey(receiptAt.AddHours(-1), 4, 12),
            0m, 0m, 5m, FixedSignedValue: 20m);
        var projection = StockReplayWacSimulator.SimulateInsertion(
            0m, 0m, candidate, [receiptEvent, issueEvent]);

        Assert.Equal(10m, receiptEvent.FixedSignedValue);
        Assert.Null(issueEvent.FixedSignedValue);
        Assert.Equal(-15m, projection.LaterSteps[1].SignedValue);
        Assert.Equal(5m, projection.FinalQuantity);
        Assert.Equal(15m, projection.FinalValue);
    }

    [Fact]
    public void Ordinary_transfer_is_one_net_zero_wac_event_with_two_complete_legs()
    {
        var at = new DateTime(2026, 10, 2, 8, 0, 0);
        var transfer = History(10, at, 3m, 3m);
        var result = StockReplayWacHistoryEventFactory.Create(
            transfer, 10, HistoryKey(at, 3, 10),
            [FactFor(transfer, 20, -1, 3m, 7.5m),
                FactFor(transfer, 21, 1, 3m, 7.5m)]);

        Assert.Equal(0m, result.OldSignedQuantity);
        Assert.Equal(0m, result.OldSignedValue);
        Assert.Equal(0m, result.FixedSignedValue);
    }

    [Fact]
    public void Ordinary_history_factory_rejects_incomplete_or_mispositioned_valuation()
    {
        var at = new DateTime(2026, 10, 2, 8, 0, 0);
        var history = History(10, at, 3m, 3m);
        var outbound = FactFor(history, 20, -1, 3m, 7.5m);
        var inbound = FactFor(history, 21, 1, 3m, 7.5m);
        var key = HistoryKey(at, 3, 10);

        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacHistoryEventFactory.Create(history, 10, key, [outbound]));
        inbound.EffectiveAt = at.AddDays(1);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacHistoryEventFactory.Create(history, 10, key, [outbound, inbound]));
        inbound.EffectiveAt = at;
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacHistoryEventFactory.Create(history, 10,
                key with { LineIdentity = "H0000000011" }, [outbound, inbound]));
        inbound.LedgerEpochId = 999;
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacHistoryEventFactory.Create(history, 10,
                key, [outbound, inbound]));
    }

    [Fact]
    public void Value_only_wac_adjustment_keeps_its_sealed_owner_and_amount()
    {
        var at = new DateTime(2026, 10, 2, 8, 0, 0);
        var owner = new StockPosting
        {
            Id = 4, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, PostingSequence = 5,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow
        };
        var fact = new StockValuationFact
        {
            Id = 30, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = owner.LedgerEpochId, StockPostingId = owner.Id,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = owner.PeriodKey, ItemCode = "ITEM",
            BaseQty = 0m, Direction = -1, CostAmount = 2m,
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued
        };

        var valueEvent = StockReplayWacValueEventFactory.Create(fact, owner);
        Assert.Equal(0m, valueEvent.OldSignedQuantity);
        Assert.Equal(-2m, valueEvent.FixedSignedValue);
        Assert.Equal("V00000000000000000030", valueEvent.Key.LineIdentity);
        fact.OriginalValuationFactId = 1;
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacValueEventFactory.Create(fact, owner));
        fact.OriginalValuationFactId = null;
        var correctionOwner = new StockPosting
        {
            Id = 5, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, PostingSequence = 6,
            EffectiveAt = at.AddDays(-1), BusinessDate = at.AddDays(-1).Date,
            PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow
        };
        var corrected = new StockValuationFact
        {
            Id = 31, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, StockPostingId = correctionOwner.Id,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = "2026-10", ItemCode = "ITEM",
            BaseQty = 0m, Direction = 1, CostAmount = 3m,
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            OriginalValuationFactId = fact.Id
        };
        var replacement = StockReplayWacValueEventFactory.CreateCorrected(
            corrected, fact, owner, correctionOwner);
        Assert.Equal(valueEvent.EventId, replacement.EventId);
        Assert.Equal(valueEvent.Key, replacement.Key);
        Assert.Equal(3m, replacement.FixedSignedValue);
        corrected.BusinessDate = corrected.BusinessDate.AddDays(1);
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacValueEventFactory.CreateCorrected(
                corrected, fact, owner, correctionOwner));
    }

    [Fact]
    public void Receipt_cost_delta_follows_the_unchanged_physical_receipt()
    {
        var at = new DateTime(2026, 10, 2, 8, 0, 0);
        var history = History(10, at, 0m, 5m);
        var owner = new StockPosting
        {
            Id = 2, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, PostingSequence = 4,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow
        };
        var receipt = FactFor(history, 20, 1, 5m, 10m);
        receipt.BusinessDate = at.Date;
        receipt.PeriodKey = "2026-10";
        var correctionOwner = new StockPosting
        {
            Id = 3, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, PostingSequence = 5,
            EffectiveAt = at.AddDays(-1), BusinessDate = at.AddDays(-1).Date,
            PeriodKey = "2026-10", SealedAtUtc = DateTime.UtcNow
        };
        var delta = new StockValuationFact
        {
            Id = 21, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerEpochId = 2, StockPostingId = correctionOwner.Id,
            OriginalValuationFactId = receipt.Id,
            EffectiveAt = at, BusinessDate = at.Date,
            PeriodKey = "2026-10", ItemCode = "ITEM",
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = 1, BaseQty = 0m, CostAmount = 3m
        };

        var valueEvent = StockReplayWacValueEventFactory.CreateReceiptRestatement(
            delta, receipt, history, owner, correctionOwner);
        Assert.Equal(at, valueEvent.Key.EffectiveAt);
        Assert.Equal(owner.PostingSequence, valueEvent.Key.OriginalPostingSequence);
        Assert.Equal($"H{history.Id:D10}", valueEvent.Key.LineIdentity);
        Assert.Equal(1, valueEvent.Key.AttachedSuborder);
        Assert.Equal(3m, valueEvent.FixedSignedValue);
        delta.OriginalValuationFactId = 999;
        Assert.Throws<StockLedgerException>(() =>
            StockReplayWacValueEventFactory.CreateReceiptRestatement(
                delta, receipt, history, owner, correctionOwner));
    }

    private static StockReplayEventKey HistoryKey(DateTime at, long sequence, int historyId) =>
        new(at, StockReplayEventKey.OrdinaryClass,
            sequence, $"H{historyId:D10}", 0);

    private static IvTrxHistory History(int id, DateTime at, decimal from, decimal to) =>
        new()
        {
            Id = id, CompanyCode = "DEMO", BranchCode = "HQ",
            LedgerVersion = 2, EntryRole = "ORIGINAL", BatchStatus = "POSTED",
            ValuationStatus = StockValuationStatuses.Valued,
            LedgerEpochId = 2, StockPostingId = 2,
            ICode = "ITEM", TrxDtTime = at,
            FromBalLocId = from > 0m ? 1 : null, FrStdQty = from > 0m ? from : null,
            ToBalLocId = to > 0m ? 2 : null, ToStdQty = to > 0m ? to : null
        };

    private static StockValuationFact FactFor(
        IvTrxHistory history, long id, int direction, decimal quantity, decimal amount) =>
        new()
        {
            Id = id, CompanyCode = history.CompanyCode,
            BranchCode = history.BranchCode, InventoryHistoryId = history.Id,
            LedgerEpochId = history.LedgerEpochId!.Value,
            StockPostingId = history.StockPostingId!.Value,
            ItemCode = history.ICode, EffectiveAt = history.TrxDtTime,
            CostMethod = StockCostMethods.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            Direction = direction, BaseQty = quantity, CostAmount = amount
        };

    private static StockReplayEventKey Key(DateTime at, long sequence) =>
        new(at, StockReplayEventKey.OrdinaryClass, sequence, "line-1", 0);
}
