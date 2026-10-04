using ErpWeb.Core.Production;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;

namespace ErpWeb.Tests.Planning.Transaction;
[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class WorkOrderScheduleCalculatorTests
{
    private static readonly Guid StepA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid StepB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly WorkOrderScheduleCalculator _scheduler = new(new AlwaysOpenWorkOrderCalendarProvider());

    private static ProductionWorkOrder Order(params ProductionWorkOrderRouteStep[] steps)
    {
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProductCode = "FG001",
            PlannedQty = 10m,
            BomBaseQty = 1m,
            SchedulingDirection = ProductionSchedulingDirections.Forward,
            ScheduleAnchorDateTime = new DateTime(2026, 10, 5, 8, 0, 0),
            PlannedStartDateTime = new DateTime(2026, 10, 5, 8, 0, 0),
            PlannedCompletionDateTime = new DateTime(2026, 10, 6, 17, 0, 0)
        };
        foreach (var step in steps)
        {
            order.RouteSteps.Add(step);
            foreach (var operation in step.Operations)
            {
                order.Operations.Add(operation);
            }
        }

        return order;
    }

    private static ProductionWorkOrderRouteStep Step(Guid key, int sequence, string centre, string output, ProductionWorkOrderOperation operation)
    {
        var step = new ProductionWorkOrderRouteStep
        {
            SourceRouteStepKey = key,
            StageSequence = sequence,
            WorkCentreCode = centre,
            OutputItemCode = output,
            OutputBaseQty = 1m,
            PlannedQty = 10m,
            OutputUom = "PCS"
        };
        step.Operations.Add(operation);
        return step;
    }

    private static ProductionWorkOrderOperation Manual(string code, int sequence, decimal minutes, bool final = false) => new()
    {
        SourceOperationKey = Guid.NewGuid(),
        OperationCode = code,
        ProcessSequence = sequence,
        ProcessType = PrProcessTypes.Manual,
        StandardDurationMinutes = minutes,
        IsFinalOperation = final,
        PlannedOutputQty = 10m,
        PlannedOutputUom = "PCS"
    };

    [Fact]
    public async Task Sequential_steps_wait_for_the_previous_group()
    {
        var first = Manual("OP10", 10, 60m);
        var second = Manual("OP20", 10, 60m, final: true);
        var order = Order(
            Step(StepA, 10, "WC01", "WIP01", first),
            Step(StepB, 20, "WC02", "FG001", second));

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.True(second.PlannedStartDateTime >= first.PlannedCompletionDateTime);
        Assert.True(order.PlannedCompletionDateTime >= second.PlannedCompletionDateTime);
        Assert.Equal(64, first.ScheduleSourceHash!.Length);
        Assert.Equal(ProductionCalendarSourceTypes.PlantDefault, first.CalendarSourceType);
    }

    [Fact]
    public async Task Parallel_steps_share_the_anchor()
    {
        var left = Manual("OP10", 10, 30m);
        var right = Manual("OP20", 10, 90m);
        var order = Order(
            Step(StepA, 10, "WC01", "WIP01", left),
            Step(StepB, 10, "WC02", "WIP02", right));

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.Equal(left.PlannedStartDateTime, right.PlannedStartDateTime);
        Assert.True(right.PlannedCompletionDateTime > left.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Selected_machine_is_the_only_machine_that_is_placed()
    {
        var operation = new ProductionWorkOrderOperation
        {
            SourceOperationKey = Guid.NewGuid(),
            OperationCode = "CUT",
            ProcessSequence = 10,
            ProcessType = PrProcessTypes.Machine,
            IsFinalOperation = true
        };
        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            MachineCode = "MC01",
            IsSelected = true,
            IsDefault = true,
            CycleSeconds = 60m,
            SetupSeconds = 0m,
            ConversionSeconds = 0m,
            QueueSeconds = 0m,
            PlannedRunMinutes = 60m,
            OutputPerCycle = 1m
        });
        operation.Machines.Add(new ProductionWorkOrderMachine
        {
            MachineCode = "MC02",
            IsSelected = false,
            PlannedRunMinutes = 999m
        });
        var order = Order(Step(StepA, 10, "WC01", "FG001", operation));

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.NotNull(operation.Machines.Single(m => m.IsSelected).PlannedStartDateTime);
        Assert.Null(operation.Machines.Single(m => !m.IsSelected).PlannedStartDateTime);
        Assert.Equal(ProductionCalendarSourceTypes.Machine, operation.CalendarSourceType);
    }

    [Fact]
    public async Task Same_sequence_wip_dependency_is_rejected()
    {
        var producer = Manual("MAKE", 10, 30m);
        var consumer = Manual("USE", 10, 30m, final: true);
        var producerStep = Step(StepA, 10, "WC01", "WIP01", producer);
        var consumerStep = Step(StepB, 10, "WC02", "FG001", consumer);
        consumer.Materials.Add(new ProductionWorkOrderMaterial
        {
            ComponentCode = "WIP01",
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStep = producerStep
        });
        var order = Order(producerStep, consumerStep);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.SequenceDependencyConflict, result.FailureCode);
    }

    [Fact]
    public async Task A_later_wip_consumer_starts_after_its_producer()
    {
        var producer = Manual("MAKE", 10, 60m);
        var consumer = Manual("USE", 10, 30m, final: true);
        var producerStep = Step(StepA, 10, "WC01", "WIP01", producer);
        var consumerStep = Step(StepB, 20, "WC02", "FG001", consumer);
        consumer.Materials.Add(new ProductionWorkOrderMaterial
        {
            ComponentCode = "WIP01",
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStep = producerStep
        });
        var order = Order(producerStep, consumerStep);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.True(consumer.PlannedStartDateTime >= producer.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Three_centers_run_in_stage_order()
    {
        var first = Manual("OP10", 10, 60m);
        var second = Manual("OP20", 10, 60m);
        var third = Manual("OP30", 10, 30m, final: true);
        var stepC = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var order = Order(
            Step(StepA, 10, "WC01", "WIP01", first),
            Step(StepB, 20, "WC02", "WIP02", second),
            Step(stepC, 30, "WC03", "FG001", third));

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.True(second.PlannedStartDateTime >= first.PlannedCompletionDateTime);
        Assert.True(third.PlannedStartDateTime >= second.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Parallel_centers_finish_before_the_next_stage()
    {
        var left = Manual("OP10", 10, 30m);
        var right = Manual("OP20", 10, 90m);
        var next = Manual("PACK", 10, 30m, final: true);
        var stepC = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var order = Order(
            Step(StepA, 10, "WC01", "WIP01", left),
            Step(StepB, 10, "WC02", "WIP02", right),
            Step(stepC, 20, "WC03", "FG001", next));

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.Equal(left.PlannedStartDateTime, right.PlannedStartDateTime);
        Assert.True(next.PlannedStartDateTime >= right.PlannedCompletionDateTime);
        Assert.True(next.PlannedStartDateTime >= left.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Parallel_processes_in_one_center_share_the_step_anchor()
    {
        var left = Manual("CUT", 10, 30m);
        var right = Manual("DRILL", 10, 90m, final: true);
        var step = Step(StepA, 10, "WC01", "FG001", left);
        step.Operations.Add(right);
        var order = Order(step);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.Equal(left.PlannedStartDateTime, right.PlannedStartDateTime);
        Assert.True(right.PlannedCompletionDateTime > left.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Sequential_processes_wait_inside_one_center()
    {
        var first = Manual("CUT", 10, 60m);
        var second = Manual("PACK", 20, 30m, final: true);
        var step = Step(StepA, 10, "WC01", "FG001", first);
        step.Operations.Add(second);
        var order = Order(step);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.True(second.PlannedStartDateTime >= first.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task Backward_schedule_finishes_at_the_anchor()
    {
        var operation = Manual("FIN", 10, 60m, final: true);
        var order = Order(Step(StepA, 10, "WC01", "FG001", operation));
        order.SchedulingDirection = ProductionSchedulingDirections.Backward;
        order.ScheduleAnchorDateTime = new DateTime(2026, 10, 6, 17, 0, 0);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.True(result.Succeeded, result.FailureMessage);
        Assert.Equal(order.ScheduleAnchorDateTime, operation.PlannedCompletionDateTime);
        Assert.True(operation.PlannedStartDateTime < operation.PlannedCompletionDateTime);
    }

    [Fact]
    public async Task A_consumer_sequenced_before_its_producer_is_rejected()
    {
        var make = Manual("MAKE", 10, 30m);
        var use = Manual("USE", 10, 30m, final: true);
        var producer = Step(StepA, 20, "WC01", "WIP01", make);
        var consumer = Step(StepB, 10, "WC02", "FG001", use);
        use.Materials.Add(new ProductionWorkOrderMaterial
        {
            ComponentCode = "WIP01",
            SupplySource = PrMaterialSupplySources.InternalRouteWip,
            ProducingRouteStep = producer
        });
        var order = Order(producer, consumer);

        var result = await _scheduler.ScheduleAsync(order);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.SequenceDependencyConflict, result.FailureCode);
    }

    [Fact]
    public async Task Missing_calendar_coverage_is_reported()
    {
        var scheduler = new WorkOrderScheduleCalculator(new EmptyWorkOrderCalendarProvider());
        var order = Order(Step(StepA, 10, "WC01", "FG001", Manual("OP10", 10, 30m, final: true)));

        var result = await scheduler.ScheduleAsync(order);

        Assert.False(result.Succeeded);
        Assert.Equal(ProductionReadinessErrorCodes.CalendarCoverageMissing, result.FailureCode);
    }

    private sealed class EmptyWorkOrderCalendarProvider : IWorkOrderCalendarProvider
    {
        public Task<WorkOrderCalendarSlice> LoadMachineAsync(
            string companyCode, string machineCode, DateOnly horizonStart, DateOnly horizonEnd, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkOrderCalendarSlice
            {
                Data = ProductionCalendarScheduler.BuildScheduleData(machineCode, new Dictionary<DateOnly, CalendarDayRow>(), new Dictionary<string, ShiftGroupSource>(), [])
            });

        public Task<WorkOrderCalendarSlice> LoadPlantAsync(
            string companyCode, string? branchCode, DateOnly horizonStart, DateOnly horizonEnd, CancellationToken cancellationToken = default)
            => LoadMachineAsync(companyCode, "PLANT", horizonStart, horizonEnd, cancellationToken);
    }
}
