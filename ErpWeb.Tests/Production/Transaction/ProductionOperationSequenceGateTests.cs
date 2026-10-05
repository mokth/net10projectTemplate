using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Production.Transaction;
[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionOperationSequenceGateTests
{
    [Fact]
    public void First_stage_and_parallel_stage_operations_are_allowed()
    {
        var graph = BuildGraph(
            Step(10, 10, "WC-A"),
            Step(20, 10, "WC-B"),
            Step(30, 30, "WC-C"));
        graph.Operations.AddRange([
            Operation(101, 10, 10),
            Operation(102, 20, 10),
            Operation(103, 30, 10),
        ]);

        Assert.True(Evaluate(graph, 101).Allowed);
        Assert.True(Evaluate(graph, 102).Allowed);
        Assert.False(Evaluate(graph, 103).Allowed);
    }

    [Fact]
    public void Later_stage_waits_for_every_parallel_predecessor_operation()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"), Step(20, 10, "WC-B"), Step(30, 20, "WC-C"));
        graph.Operations.AddRange([
            Operation(101, 10, 10, good: 100m),
            Operation(102, 20, 10, good: 80m),
            Operation(103, 30, 10),
        ]);

        var blocked = Evaluate(graph, 103);

        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Stage, blocked.BlockingLevel);
        Assert.Equal(10, blocked.BlockingSequence);
        Assert.Equal("WC-B", blocked.BlockingWorkCentreCode);

        graph.Operations.Single(x => x.Uid == 102).GoodQty = 100m;
        Assert.True(Evaluate(graph, 103).Allowed);
    }

    [Fact]
    public void Sequence_gaps_use_all_lower_stage_groups()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"), Step(30, 30, "WC-B"), Step(50, 50, "WC-C"));
        graph.Operations.AddRange([
            Operation(101, 10, 10, good: 100m),
            Operation(102, 30, 10, good: 20m),
            Operation(103, 50, 10),
        ]);

        var blocked = Evaluate(graph, 103);

        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Stage, blocked.BlockingLevel);
        Assert.Equal(30, blocked.BlockingSequence);
    }

    [Fact]
    public void First_and_parallel_processes_are_allowed_and_partial_good_unlocks_next_process()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"));
        graph.Operations.AddRange([
            Operation(101, 10, 10, good: 100m),
            Operation(102, 10, 10, good: 80m),
            Operation(103, 10, 30),
        ]);

        Assert.True(Evaluate(graph, 101).Allowed);
        Assert.True(Evaluate(graph, 102).Allowed);

        var opened = Evaluate(graph, 103);
        Assert.True(opened.Allowed);

        graph.Operations.Single(x => x.Uid == 102).GoodQty = 0m;
        var blocked = Evaluate(graph, 103);
        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Process, blocked.BlockingLevel);
        Assert.Equal(10, blocked.BlockingSequence);
        Assert.Equal("P102", blocked.BlockingOperationCode);
        Assert.Equal([102], blocked.BlockingOperationIds);
        Assert.Contains("must post Good qty first", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0.0000 of 100.0000 Good posted", blocked.Message, StringComparison.Ordinal);

        graph.Operations.Single(x => x.Uid == 102).GoodQty = 1m;
        Assert.True(Evaluate(graph, 103).Allowed);
    }

    [Fact]
    public void Partial_good_unlocks_the_next_process_while_zero_good_remains_blocked()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"));
        graph.Operations.AddRange([
            Operation(101, 10, 10),
            Operation(102, 10, 20),
        ]);

        var blocked = Evaluate(graph, 102);
        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Process, blocked.BlockingLevel);
        Assert.Equal([101], blocked.BlockingOperationIds);

        graph.Operations.Single(x => x.Uid == 101).GoodQty = 80m;
        var opened = Evaluate(graph, 102);
        Assert.True(opened.Allowed);
    }

    [Fact]
    public void Parallel_predecessor_process_group_requires_every_member_to_post_good()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"));
        graph.Operations.AddRange([
            Operation(101, 10, 10, good: 10m),
            Operation(102, 10, 10),
            Operation(103, 10, 20),
        ]);

        var blocked = Evaluate(graph, 103);
        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Process, blocked.BlockingLevel);
        Assert.Equal("P102", blocked.BlockingOperationCode);
        Assert.Equal([102], blocked.BlockingOperationIds);

        graph.Operations.Single(x => x.Uid == 102).GoodQty = 1m;
        Assert.True(Evaluate(graph, 103).Allowed);
    }

    [Fact]
    public void Scrap_reject_and_hold_do_not_unlock_the_next_process()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"));
        var predecessor = Operation(101, 10, 10);
        predecessor.ScrapQty = predecessor.RejectQty = predecessor.HoldQty = 100m;
        graph.Operations.AddRange([predecessor, Operation(102, 10, 20)]);

        var result = Evaluate(graph, 102);

        Assert.False(result.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Process, result.BlockingLevel);
        Assert.Contains("must post Good qty first", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Later_stage_remains_blocked_when_prior_stage_has_only_partial_good()
    {
        var graph = BuildGraph(Step(10, 10, "WC-A"), Step(20, 20, "WC-B"));
        graph.Operations.AddRange([
            Operation(101, 10, 10, good: 80m),
            Operation(102, 20, 10),
        ]);

        var blocked = Evaluate(graph, 102);
        Assert.False(blocked.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Stage, blocked.BlockingLevel);
        Assert.Contains("Previous Stage 10 is not complete", blocked.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_snapshot_fails_closed()
    {
        var graph = BuildGraph(Step(10, 0, "WC-A"));
        graph.Operations.Add(Operation(101, 10, 10));

        var result = Evaluate(graph, 101);

        Assert.False(result.Allowed);
        Assert.Equal(ProductionSequenceBlockingLevels.Snapshot, result.BlockingLevel);
    }

    private static ProductionSequenceGateResult Evaluate(GateGraph graph, long operationId) =>
        ProductionOperationSequenceGate.Evaluate(
            graph.Operations.Single(x => x.Uid == operationId), graph.Steps, graph.Operations);

    private static GateGraph BuildGraph(params ProductionWorkOrderRouteStep[] steps) => new()
    {
        Steps = steps.ToList(),
    };

    private static ProductionWorkOrderRouteStep Step(long uid, int stageSequence, string workCentreCode) => new()
    {
        Uid = uid,
        WorkOrderId = 1,
        StageSequence = stageSequence,
        WorkCentreCode = workCentreCode,
    };

    private static ProductionWorkOrderOperation Operation(
        long uid, long routeStepId, int processSequence, decimal good = 0m) => new()
    {
        Uid = uid,
        WorkOrderId = 1,
        RouteStepId = routeStepId,
        ProcessSequence = processSequence,
        OperationCode = $"P{uid}",
        PlannedOutputQty = 100m,
        GoodQty = good,
    };

    private sealed class GateGraph
    {
        public List<ProductionWorkOrderRouteStep> Steps { get; init; } = [];
        public List<ProductionWorkOrderOperation> Operations { get; } = [];
    }
}
