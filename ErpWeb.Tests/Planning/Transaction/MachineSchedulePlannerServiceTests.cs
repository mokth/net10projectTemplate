using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Tests.Infrastructure.Helpers;
using ErpWeb.UI.Planning.WorkOrders;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Planning.Transaction;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineSchedulePlannerServiceTests
{
    private static readonly DateTime WindowStart = new(2026, 10, 1);
    private static readonly DateTime WindowEnd = new(2026, 10, 31);

    // ── Read model / late semantics ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_order_planned_completion_is_distinct_from_operation_completion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0),
            headerCompletion: new DateTime(2026, 10, 5, 17, 0, 0),
            machineStart: new DateTime(2026, 10, 2, 8, 0, 0),
            machineCompletion: new DateTime(2026, 10, 2, 10, 0, 0));

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.Equal(seeded.WorkOrderId, job.WorkOrderId);
        Assert.Equal(new DateTime(2026, 10, 5, 17, 0, 0), job.WorkOrderPlannedCompletion);
        Assert.Equal(new DateTime(2026, 10, 2, 10, 0, 0), job.PlannedCompletion);
    }

    [Fact]
    public async Task Linked_delivery_request_fields_and_enrichment_mutate_after_materialization()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync();
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            deliveryRequestNo: "DR-1001",
            requiredDate: new DateTime(2026, 10, 4),
            priority: SaDeliveryRequestPriorities.High,
            status: SaDeliveryRequestStatuses.Released,
            projectCode: "PRJ-A",
            warehouseCode: "WH01");

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.NotNull(job.DeliveryRequestId);
        Assert.Equal("DR-1001", job.DeliveryRequestNo);
        Assert.Equal(SaDeliveryRequestStatuses.Released, job.DeliveryRequestStatus);
        Assert.Equal(new DateTime(2026, 10, 4), job.RequiredDate);
        Assert.Equal(SaDeliveryRequestPriorities.High, job.Priority);
        Assert.Equal("PRJ-A", job.ProjectCode);
        Assert.Equal("WH01", job.WarehouseCode);
        Assert.True(job.DemandAllocationIsActive);
        Assert.True(job.HasActiveDemandContext);
    }

    [Fact]
    public async Task Manual_work_order_has_no_late_flag_or_fake_due_date()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 20, 17, 0, 0));

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.Null(job.DeliveryRequestNo);
        Assert.False(job.HasActiveDemandContext);
        Assert.False(job.IsLateToRequiredDate);
        Assert.Equal(0, job.LateDays);
        Assert.Equal(0, board.LateWorkOrderCount);
    }

    [Fact]
    public async Task Inactive_demand_allocation_does_not_drive_late_attention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 20, 17, 0, 0));
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            deliveryRequestNo: "DR-INACTIVE",
            requiredDate: new DateTime(2026, 10, 2),
            priority: SaDeliveryRequestPriorities.Urgent,
            status: SaDeliveryRequestStatuses.Released,
            isActive: false);

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.False(job.DemandAllocationIsActive);
        Assert.False(job.HasActiveDemandContext);
        Assert.False(job.IsLateToRequiredDate);
        Assert.Equal(0, board.LateWorkOrderCount);
    }

    [Fact]
    public async Task Cancelled_delivery_request_does_not_drive_attention()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 20, 17, 0, 0));
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            deliveryRequestNo: "DR-CANCELLED",
            requiredDate: new DateTime(2026, 10, 2),
            priority: SaDeliveryRequestPriorities.Urgent,
            status: SaDeliveryRequestStatuses.Cancelled);

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.True(job.DemandAllocationIsActive);
        Assert.False(job.HasActiveDemandContext);
        Assert.False(job.IsLateToRequiredDate);
        Assert.Equal(0, board.LateWorkOrderCount);
    }

    [Fact]
    public async Task Same_completion_and_required_date_is_not_late()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 5, 17, 0, 0));
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 5));

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.False(job.IsLateToRequiredDate);
        Assert.Equal(0, job.LateDays);
    }

    [Fact]
    public async Task Next_day_completion_is_late_by_one_calendar_day()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 6, 9, 0, 0));
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 5));

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.True(job.IsLateToRequiredDate);
        Assert.Equal(1, job.LateDays);
    }

    [Fact]
    public async Task Late_uses_work_order_completion_not_operation_completion()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Operation finishes before the DR date; the Work Order header does not.
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 9, 17, 0, 0),
            machineStart: new DateTime(2026, 10, 2, 8, 0, 0),
            machineCompletion: new DateTime(2026, 10, 2, 9, 0, 0));
        await fixture.SeedDeliveryRequestAsync(
            seeded.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 5));

        var board = await fixture.GetBoardAsync();

        var job = Assert.Single(board.Jobs);
        Assert.True(job.IsLateToRequiredDate);
        Assert.Equal(4, job.LateDays);
    }

    // ── Distinct attention counts ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Late_count_is_distinct_by_work_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerCompletion: new DateTime(2026, 10, 9, 17, 0, 0));
        await fixture.SeedDeliveryRequestAsync(seeded.WorkOrderId, requiredDate: new DateTime(2026, 10, 5));
        await fixture.AddMachineOperationAsync(seeded, machineCode: "MC2", operationCode: "OP2");

        var board = await fixture.GetBoardAsync();

        Assert.Equal(2, board.Jobs.Count);
        Assert.Equal(1, board.LateWorkOrderCount);
    }

    [Fact]
    public async Task Due_count_is_distinct_by_work_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync();
        await fixture.SeedDeliveryRequestAsync(seeded.WorkOrderId, requiredDate: new DateTime(2026, 10, 8));
        await fixture.AddMachineOperationAsync(seeded, machineCode: "MC2", operationCode: "OP2");

        var board = await fixture.GetBoardAsync();

        Assert.Equal(2, board.Jobs.Count);
        Assert.Equal(1, board.DueThisWindowWorkOrderCount);
    }

    // ── Filters are server-side ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Priority_filter_excludes_non_matching_work_orders()
    {
        await using var fixture = await Fixture.CreateAsync();
        var normal = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-NORMAL", machineCode: "MC1");
        await fixture.SeedDeliveryRequestAsync(normal.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 20), priority: SaDeliveryRequestPriorities.Normal);
        var urgent = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-URGENT", machineCode: "MC2");
        await fixture.SeedDeliveryRequestAsync(urgent.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 20), priority: SaDeliveryRequestPriorities.Urgent);

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            Priority = SaDeliveryRequestPriorities.Urgent
        });

        var job = Assert.Single(board.Jobs);
        Assert.Equal("WO-URGENT", job.WorkOrderNo);
    }

    [Fact]
    public async Task Priority_filter_is_applied_before_the_board_limit()
    {
        await using var fixture = await Fixture.CreateAsync();
        // 3,001 non-matching jobs sort before the only matching job on machine Z-MC.
        await fixture.SeedManyMinimalWorkOrdersAsync(3001, "A-MC");
        var urgent = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-Z-URGENT", machineCode: "Z-MC");
        await fixture.SeedDeliveryRequestAsync(urgent.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 20), priority: SaDeliveryRequestPriorities.Urgent);

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            Priority = SaDeliveryRequestPriorities.Urgent
        });

        var job = Assert.Single(board.Jobs);
        Assert.Equal("WO-Z-URGENT", job.WorkOrderNo);
        Assert.False(board.AttentionCountsArePartial);
    }

    [Fact]
    public async Task Late_filter_excludes_rows_that_meet_the_dr_date()
    {
        await using var fixture = await Fixture.CreateAsync();
        var onTime = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-ONTIME",
            headerCompletion: new DateTime(2026, 10, 5, 10, 0, 0));
        await fixture.SeedDeliveryRequestAsync(onTime.WorkOrderId, requiredDate: new DateTime(2026, 10, 5));
        var late = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-LATE",
            headerCompletion: new DateTime(2026, 10, 9, 10, 0, 0));
        await fixture.SeedDeliveryRequestAsync(late.WorkOrderId, requiredDate: new DateTime(2026, 10, 5));

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            LateOnly = true
        });

        var job = Assert.Single(board.Jobs);
        Assert.Equal("WO-LATE", job.WorkOrderNo);
        Assert.True(job.IsLateToRequiredDate);
    }

    [Fact]
    public async Task Delivery_request_number_search_runs_in_the_query()
    {
        await using var fixture = await Fixture.CreateAsync();
        var other = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-OTHER");
        await fixture.SeedDeliveryRequestAsync(other.WorkOrderId, deliveryRequestNo: "DR-AAA");
        var target = await fixture.SeedWorkOrderAsync(workOrderNo: "WO-TARGET");
        await fixture.SeedDeliveryRequestAsync(target.WorkOrderId, deliveryRequestNo: "DR-ZZZ");

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            SearchText = "DR-ZZZ"
        });

        var job = Assert.Single(board.Jobs);
        Assert.Equal("WO-TARGET", job.WorkOrderNo);
    }

    // ── Planner lookup consistency ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_centre_lookup_excludes_operations_without_scheduled_machine_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(workCentreCode: "WC-SCHEDULED");
        await fixture.SeedWorkOrderAsync(
            workOrderNo: "WO-NO-DATES",
            workCentreCode: "WC-NO-DATES",
            machineScheduled: false);

        var page = await fixture.LookupAsync(MachineSchedulePlannerLookupKind.WorkCentre);

        Assert.Contains(page.Items, x => x.Key == "WC-SCHEDULED");
        Assert.DoesNotContain(page.Items, x => x.Key == "WC-NO-DATES");
    }

    [Fact]
    public async Task Process_lookup_excludes_operations_without_scheduled_machine_rows()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(operationCode: "OP-SCHEDULED");
        await fixture.SeedWorkOrderAsync(
            workOrderNo: "WO-NO-DATES",
            operationCode: "OP-NO-DATES",
            machineScheduled: false);

        var page = await fixture.LookupAsync(MachineSchedulePlannerLookupKind.Process);

        Assert.Contains(page.Items, x => x.Key == "OP-SCHEDULED");
        Assert.DoesNotContain(page.Items, x => x.Key == "OP-NO-DATES");
    }

    [Fact]
    public async Task Work_order_lookup_excludes_active_orders_without_a_scheduled_selected_machine()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(workOrderNo: "WO-SCHEDULED");
        await fixture.SeedWorkOrderAsync(
            workOrderNo: "WO-NO-DATES",
            machineScheduled: false);

        var page = await fixture.LookupAsync(MachineSchedulePlannerLookupKind.WorkOrder);

        Assert.Contains(page.Items, x => x.Key == "WO-SCHEDULED");
        Assert.DoesNotContain(page.Items, x => x.Key == "WO-NO-DATES");
    }

    [Fact]
    public async Task Product_lookup_excludes_products_that_cannot_appear_on_the_machine_board()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(productCode: "FG-SCHEDULED");
        await fixture.SeedWorkOrderAsync(
            workOrderNo: "WO-NO-DATES",
            productCode: "FG-UNSCHEDULED",
            machineScheduled: false);

        var page = await fixture.LookupAsync(MachineSchedulePlannerLookupKind.Product);

        Assert.Contains(page.Items, x => x.Key == "FG-SCHEDULED");
        Assert.DoesNotContain(page.Items, x => x.Key == "FG-UNSCHEDULED");
    }

    [Fact]
    public async Task Machine_lookup_remains_master_driven_and_allows_an_idle_machine()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedMachineMasterAsync("MC-IDLE");

        var page = await fixture.LookupAsync(MachineSchedulePlannerLookupKind.Machine);

        Assert.Contains(page.Items, x => x.Key == "MC-IDLE");
    }

    // ── Idle-machine behavior ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Idle_machines_are_absent_when_show_idle_is_off()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(machineCode: "MC1");
        await fixture.SeedMachineMasterAsync("MC-IDLE");

        var board = await fixture.GetBoardAsync();

        Assert.Contains(board.Machines, x => x.MachineCode == "MC1");
        Assert.DoesNotContain(board.Machines, x => x.MachineCode == "MC-IDLE");
        Assert.False(board.ShowIdleMachines);
    }

    [Fact]
    public async Task Selected_idle_machine_appears_with_zero_jobs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedMachineMasterAsync("MC-IDLE");

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            MachineCode = "MC-IDLE"
        });

        Assert.Empty(board.Jobs);
        Assert.Contains(board.Machines, x => x.MachineCode == "MC-IDLE");
    }

    [Fact]
    public async Task Idle_machine_keeps_maintenance_visible_with_zero_jobs()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedMachineMasterAsync("MC-IDLE");
        await fixture.SeedPreventiveAsync("MC-IDLE", new DateTime(2026, 10, 8), "08:00", "12:00");

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            MachineCode = "MC-IDLE"
        });

        Assert.Empty(board.Jobs);
        var machine = Assert.Single(board.Machines);
        Assert.NotEmpty(machine.MaintenanceIntervals);
        Assert.Equal(1, board.VisibleMaintenanceMachineCount);
        Assert.Contains(board.Appointments, x => x.IsMaintenance && x.ResourceId == "MC-IDLE");
    }

    [Fact]
    public async Task Show_idle_on_preserves_the_cap_and_keeps_scheduled_machines()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SeedWorkOrderAsync(machineCode: "MC-SCHEDULED");
        await fixture.SeedMachineMastersAsync(505, "IDLE-");

        var board = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            ShowIdleMachines = true
        });

        Assert.True(board.HasMoreIdleMachinesThanLimit);
        // 500 idle masters plus the scheduled machine that sorts beyond the cap.
        Assert.Equal(501, board.Machines.Count);
        Assert.Contains(board.Machines, x => x.MachineCode == "MC-SCHEDULED");
    }

    // ── External capacity overlay ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Allocation_schema_not_ready_never_queries_allocations_or_crashes()
    {
        await using var fixture = await Fixture.CreateAsync(allocationSchemaReady: false);
        await fixture.SeedWorkOrderAsync(machineCode: "MC1");
        await fixture.SeedExternalAllocationAsync("MC1", "BR2", new DateTime(2026, 10, 3, 8, 0, 0),
            new DateTime(2026, 10, 3, 10, 0, 0));

        var board = await fixture.GetBoardAsync();

        Assert.DoesNotContain(board.Appointments, x => x.IsExternalCapacity);
        Assert.Equal(0, board.ExternalCapacityMachineCount);
        Assert.True(board.NeedsCapacityAttention);
    }

    [Fact]
    public async Task Current_external_allocation_is_shown_anonymously_with_label_five()
    {
        await using var fixture = await Fixture.CreateAsync(allocationSchemaReady: true);
        await fixture.SeedWorkOrderAsync(machineCode: "MC1");
        await fixture.SeedExternalAllocationAsync("MC1", "BR2", new DateTime(2026, 10, 3, 8, 0, 0),
            new DateTime(2026, 10, 3, 10, 0, 0));

        var board = await fixture.GetBoardAsync();

        var external = Assert.Single(board.Appointments, x => x.IsExternalCapacity);
        Assert.Equal("Reserved · other branch", external.Subject);
        Assert.Equal("This physical machine capacity is already reserved outside the current branch.", external.Description);
        Assert.StartsWith("external-capacity:", external.Location);
        Assert.Equal(MachineSchedulePlannerLabelIds.ReservedOtherBranch, external.LabelId);
        Assert.Equal("MC1", external.ResourceId);
        Assert.Equal(1, board.ExternalCapacityMachineCount);
    }

    [Fact]
    public async Task External_capacity_bar_leaks_no_work_order_product_or_delivery_request_identity()
    {
        await using var fixture = await Fixture.CreateAsync(allocationSchemaReady: true);
        await fixture.SeedWorkOrderAsync(workOrderNo: "HQ-WO", productCode: "FG-HQ", machineCode: "MC1");
        await fixture.SeedExternalAllocationAsync("MC1", "BR2", new DateTime(2026, 10, 3, 8, 0, 0),
            new DateTime(2026, 10, 3, 10, 0, 0));

        var board = await fixture.GetBoardAsync();
        var external = Assert.Single(board.Appointments, x => x.IsExternalCapacity);

        var rendered = string.Join(" ", external.Subject, external.Description, external.Location);
        Assert.DoesNotContain("BR2-WO", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FG-BR2", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DR-BR2", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CUST", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_revision_external_allocation_is_not_shown_as_authoritative()
    {
        await using var fixture = await Fixture.CreateAsync(allocationSchemaReady: true);
        await fixture.SeedWorkOrderAsync(machineCode: "MC1");
        await fixture.SeedExternalAllocationAsync("MC1", "BR2", new DateTime(2026, 10, 3, 8, 0, 0),
            new DateTime(2026, 10, 3, 10, 0, 0), scheduleRevisionOffset: 5);

        var board = await fixture.GetBoardAsync();

        Assert.DoesNotContain(board.Appointments, x => x.IsExternalCapacity);
        Assert.Equal(0, board.ExternalCapacityMachineCount);
    }

    [Fact]
    public async Task External_capacity_survives_product_and_priority_filters()
    {
        await using var fixture = await Fixture.CreateAsync(allocationSchemaReady: true);
        var seeded = await fixture.SeedWorkOrderAsync(productCode: "FG-FILTER", machineCode: "MC1");
        await fixture.SeedDeliveryRequestAsync(seeded.WorkOrderId,
            requiredDate: new DateTime(2026, 10, 20), priority: SaDeliveryRequestPriorities.Normal);
        await fixture.SeedExternalAllocationAsync("MC1", "BR2", new DateTime(2026, 10, 3, 8, 0, 0),
            new DateTime(2026, 10, 3, 10, 0, 0));

        var productFiltered = await fixture.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            ProductCode = "FG-FILTER",
            Priority = SaDeliveryRequestPriorities.Normal
        });

        Assert.Contains(productFiltered.Appointments, x => x.IsExternalCapacity);
    }

    [Fact]
    public void External_appointment_cannot_open_or_change_work_order_schedule()
    {
        var location = $"{MachineSchedulePlannerAppointmentRules.ExternalCapacityPrefix}42";

        Assert.True(MachineSchedulePlannerAppointmentRules.IsExternalCapacity(location));
        Assert.False(MachineSchedulePlannerAppointmentRules.IsMaintenance(location));
        Assert.False(MachineSchedulePlannerAppointmentRules.CanOpenWorkOrder(location));
        Assert.False(MachineSchedulePlannerAppointmentRules.CanChangeSchedule(location));
        Assert.False(MachineSchedulePlannerAppointmentRules.TryGetOperationId(location, out _));
    }

    [Fact]
    public void External_drag_is_cancelled_with_the_branch_message()
    {
        Assert.Equal(
            "This reserved capacity belongs outside the current branch and cannot be moved here.",
            MachineSchedulePlannerAppointmentRules.ExternalCapacityDragMessage);
    }

    // ── Work Order navigation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_order_window_prefers_machine_time_over_the_work_order_header()
    {
        await using var fixture = await Fixture.CreateAsync();
        var seeded = await fixture.SeedWorkOrderAsync(
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0),
            headerCompletion: new DateTime(2026, 10, 30, 17, 0, 0),
            machineStart: new DateTime(2026, 10, 20, 8, 0, 0),
            machineCompletion: new DateTime(2026, 10, 20, 10, 0, 0));

        var result = await fixture.Service.GetWorkOrderMachineScheduleWindowAsync(seeded.WorkOrderNo);

        Assert.True(result.Succeeded, result.Message);
        var window = result.Data!;
        Assert.True(window.IsMachineScheduled);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), window.EarliestMachineStart);
        Assert.Equal(new DateTime(2026, 10, 20, 10, 0, 0), window.LatestMachineCompletion);
        Assert.Equal(new DateTime(2026, 10, 20, 8, 0, 0), window.AnchorStart);
    }

    [Fact]
    public async Task Work_order_with_labour_before_machine_does_not_jump_to_the_header_start_week()
    {
        await using var fixture = await Fixture.CreateAsync();
        // Header and a labour-only operation start 1 Oct; the machine operation runs 20 Oct.
        var seeded = await fixture.SeedWorkOrderAsync(
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0),
            headerCompletion: new DateTime(2026, 10, 1, 17, 0, 0),
            machineStart: new DateTime(2026, 10, 20, 8, 0, 0),
            machineCompletion: new DateTime(2026, 10, 20, 9, 0, 0));

        var result = await fixture.Service.GetWorkOrderMachineScheduleWindowAsync(seeded.WorkOrderNo);

        Assert.True(result.Succeeded, result.Message);
        var start = MachineSchedulePlannerPeriodNavigation.PeriodStartFor(
            result.Data!.AnchorStart!.Value, PlannerView.Week);
        Assert.Equal(new DateTime(2026, 10, 19), start);
        Assert.NotEqual(MachineSchedulePlannerPeriodNavigation.PeriodStartFor(
            result.Data.WorkOrderPlannedStart, PlannerView.Week), start);
    }

    [Fact]
    public void Period_navigation_helpers_keep_manual_periods_and_resolve_anchors()
    {
        var anchor = new DateTime(2026, 10, 21, 9, 0, 0);
        var weekStart = MachineSchedulePlannerPeriodNavigation.PeriodStartFor(anchor, PlannerView.Week);
        Assert.Equal(new DateTime(2026, 10, 19), weekStart);
        Assert.True(MachineSchedulePlannerPeriodNavigation.IsInPeriod(weekStart, TimeSpan.FromDays(7), anchor));
        Assert.False(MachineSchedulePlannerPeriodNavigation.IsInPeriod(
            weekStart.AddDays(7), TimeSpan.FromDays(7), anchor));

        Assert.Equal(new DateTime(2026, 10, 1),
            MachineSchedulePlannerPeriodNavigation.PeriodStartFor(anchor, PlannerView.Month));
        Assert.Equal(new DateTime(2026, 10, 21),
            MachineSchedulePlannerPeriodNavigation.PeriodStartFor(anchor, PlannerView.Day));

        var headerStart = new DateTime(2026, 10, 1, 8, 0, 0);
        Assert.Equal(headerStart, MachineSchedulePlannerPeriodNavigation.ResolveAnchor(null, headerStart));
        Assert.Equal(anchor, MachineSchedulePlannerPeriodNavigation.ResolveAnchor(anchor, headerStart));
    }

    // ── Work Order selection plan (single reload, machine-anchored) ────────────────────────

    [Fact]
    public void Selection_inside_the_current_period_keeps_the_period_and_requests_one_reload()
    {
        var window = Window("WO-INSIDE",
            machineStart: new DateTime(2026, 10, 21, 8, 0, 0),
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0));
        var currentPeriodStart = new DateTime(2026, 10, 19);

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            "WO-INSIDE", true, window, currentPeriodStart, TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldReload);
        Assert.False(plan.ShouldMovePeriod);
        Assert.Equal(currentPeriodStart, plan.PeriodStart);
        Assert.Null(plan.StatusMessage);
        Assert.False(plan.ClearStatus);
    }

    [Fact]
    public void Selection_outside_the_current_period_moves_to_the_machine_week_once()
    {
        var window = Window("WO000123",
            machineStart: new DateTime(2026, 10, 21, 8, 0, 0),
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0));

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            "WO000123", true, window, new DateTime(2026, 10, 5), TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldReload);
        Assert.True(plan.ShouldMovePeriod);
        Assert.Equal(new DateTime(2026, 10, 19), plan.PeriodStart);
        Assert.Equal("Moved to WO000123 scheduled period.", plan.StatusMessage);
    }

    [Fact]
    public void Selection_uses_machine_time_when_labour_starts_earlier_in_the_period()
    {
        // The WO header starts inside the visible week; only the selected machine start is outside.
        var window = Window("WO-LABOUR-FIRST",
            machineStart: new DateTime(2026, 10, 20, 8, 0, 0),
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0));

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            "WO-LABOUR-FIRST", true, window, new DateTime(2026, 9, 28), TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldMovePeriod);
        Assert.Equal(new DateTime(2026, 10, 19), plan.PeriodStart);
    }

    [Fact]
    public void Clearing_the_selection_keeps_the_period_and_clears_the_move_message()
    {
        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            null, false, null, new DateTime(2026, 10, 19), TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldReload);
        Assert.False(plan.ShouldMovePeriod);
        Assert.Equal(new DateTime(2026, 10, 19), plan.PeriodStart);
        Assert.True(plan.ClearStatus);
    }

    [Fact]
    public void Failed_window_lookup_keeps_the_period_and_still_reloads_once()
    {
        var plan = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            "WO-UNRESOLVED", false, null, new DateTime(2026, 10, 19), TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldReload);
        Assert.False(plan.ShouldMovePeriod);
        Assert.Equal(new DateTime(2026, 10, 19), plan.PeriodStart);
        Assert.Null(plan.StatusMessage);
    }

    [Fact]
    public void Go_to_work_order_schedule_returns_to_the_machine_period_without_repeating_the_message()
    {
        var window = Window("WO000123",
            machineStart: new DateTime(2026, 10, 21, 8, 0, 0),
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0));

        var plan = MachineSchedulePlannerSelectionPlanner.ResolveGoToSchedule(
            true, window, new DateTime(2026, 10, 5), TimeSpan.FromDays(7), PlannerView.Week);

        Assert.True(plan.ShouldReload);
        Assert.True(plan.ShouldMovePeriod);
        Assert.Equal(new DateTime(2026, 10, 19), plan.PeriodStart);
        Assert.Null(plan.StatusMessage);
    }

    [Fact]
    public void Manual_period_navigation_never_produces_a_selection_plan()
    {
        // Manual Today/Previous/Next only move PeriodStart; they never call the selection planner,
        // so a previously selected Work Order cannot force the planner back to its own period.
        var manualStart = new DateTime(2026, 10, 5);
        var window = Window("WO000123",
            machineStart: new DateTime(2026, 10, 21, 8, 0, 0),
            headerStart: new DateTime(2026, 10, 1, 8, 0, 0));

        var planWithoutSelection = MachineSchedulePlannerSelectionPlanner.ResolveSelection(
            null, false, window, manualStart, TimeSpan.FromDays(7), PlannerView.Week);

        Assert.False(planWithoutSelection.ShouldMovePeriod);
        Assert.Equal(manualStart, planWithoutSelection.PeriodStart);
    }

    private static MachineSchedulePlannerWorkOrderWindow Window(
        string workOrderNo,
        DateTime machineStart,
        DateTime headerStart) => new()
    {
        WorkOrderId = 1,
        WorkOrderNo = workOrderNo,
        WorkOrderStatus = ProductionWorkOrderStatuses.Released,
        EarliestMachineStart = machineStart,
        LatestMachineCompletion = machineStart.AddHours(2),
        WorkOrderPlannedStart = headerStart,
        WorkOrderPlannedCompletion = headerStart.AddDays(19)
    };

    [Fact]
    public async Task Unknown_work_order_window_is_not_found()
    {
        await using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Service.GetWorkOrderMachineScheduleWindowAsync("WO-MISSING");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    // ── Planner diagnostics authorization ──────────────────────────────────────────────────

    [Theory]
    [InlineData(true, false)]   // Planner Edit without Admin Settings Edit
    [InlineData(false, true)]   // Admin Settings Edit without Planner Edit
    [InlineData(false, false)]  // neither elevated permission
    public async Task Backfill_requires_planner_access_edit_and_admin_settings_edit(
        bool plannerEdit,
        bool adminSettingsEdit)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateBackfillService(
            plannerAccess: true,
            plannerEdit: plannerEdit,
            adminSettingsEdit: adminSettingsEdit);

        var preview = await service.PreviewBackfillAsync();
        var apply = await service.ApplyVerifiedBackfillAsync("token");

        Assert.False(preview.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, preview.ErrorCode);
        Assert.False(apply.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, apply.ErrorCode);
    }

    [Fact]
    public async Task Backfill_with_all_three_permissions_passes_authorization_with_the_feature_flag_off()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateBackfillService(
            plannerAccess: true,
            plannerEdit: true,
            adminSettingsEdit: true);

        var preview = await service.PreviewBackfillAsync();

        // SQLite is not a supported allocation-schema provider, so this stops at the schema gate.
        // Reaching that gate proves authorization and the disabled write setting did not block it.
        Assert.False(preview.Succeeded);
        Assert.NotEqual(IvMasterErrorCode.AccessDenied, preview.ErrorCode);
        Assert.Contains("schema", preview.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(fixture.WriteSettingEnabled);
    }

    [Fact]
    public async Task Readiness_evidence_keeps_planner_access_and_admin_settings_edit_contract()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateReadinessService(
            plannerAccess: true,
            plannerEdit: false,
            adminSettingsEdit: true);

        var result = await service.RefreshEvidenceAsync();

        Assert.NotEqual(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Readiness_evidence_still_requires_admin_settings_edit()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = fixture.CreateReadinessService(
            plannerAccess: true,
            plannerEdit: true,
            adminSettingsEdit: false);

        var result = await service.RefreshEvidenceAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    // ── Fixture ────────────────────────────────────────────────────────────────────────────

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private long _bomHdrId;

        private Fixture(SqliteConnection connection, TestDbContextFactory factory, bool allocationSchemaReady)
        {
            _connection = connection;
            Factory = factory;
            AllocationSchemaReady = allocationSchemaReady;
            WriteSettingEnabled = false;
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var readiness = new Mock<IMachineSchedulePlannerReadinessService>();
            readiness.Setup(x => x.GetReadinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MachineSchedulePlannerReadiness
                {
                    AllocationSchemaReady = AllocationSchemaReady,
                    WriteSettingEnabled = WriteSettingEnabled
                });
            Access = access.Object;
            Service = new MachineSchedulePlannerService(factory, InventoryTenantTestHelper.CreateTenantContext(),
                access.Object, readiness.Object);
        }

        public TestDbContextFactory Factory { get; }
        public MachineSchedulePlannerService Service { get; }
        public IAccessRightService Access { get; }
        public bool AllocationSchemaReady { get; }
        public bool WriteSettingEnabled { get; }

        public static async Task<Fixture> CreateAsync(bool allocationSchemaReady = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            return new Fixture(connection, factory, allocationSchemaReady);
        }

        public async Task<MachineSchedulePlannerBoard> GetBoardAsync(MachineSchedulePlannerQuery? query = null)
        {
            var result = await Service.GetBoardAsync(query ?? new MachineSchedulePlannerQuery
            {
                WindowStart = WindowStart,
                WindowEnd = WindowEnd
            });
            Assert.True(result.Succeeded, result.Message);
            return result.Data!;
        }

        public async Task<MachineSchedulePlannerLookupPage> LookupAsync(MachineSchedulePlannerLookupKind kind)
        {
            var result = await Service.GetFilterLookupAsync(new MachineSchedulePlannerLookupQuery
            {
                Kind = kind,
                Take = 100
            });
            Assert.True(result.Succeeded, result.Message);
            return result.Data!;
        }

        public MachineScheduleAllocationBackfillService CreateBackfillService(
            bool plannerAccess,
            bool plannerEdit,
            bool adminSettingsEdit)
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plannerAccess);
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plannerEdit);
            access.Setup(x => x.CanAsync(
                    MenuCodes.AdminSettings, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(adminSettingsEdit);
            return new MachineScheduleAllocationBackfillService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                new Mock<IProductionCalendarScheduleDataLoader>().Object);
        }

        public MachineSchedulePlannerReadinessService CreateReadinessService(
            bool plannerAccess,
            bool plannerEdit,
            bool adminSettingsEdit)
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Access, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plannerAccess);
            access.Setup(x => x.CanAsync(
                    MenuCodes.PlanningMachineSchedulePlanner, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(plannerEdit);
            access.Setup(x => x.CanAsync(
                    MenuCodes.AdminSettings, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
                .ReturnsAsync(adminSettingsEdit);
            var settings = new Mock<IAppSettingService>();
            settings.Setup(x => x.GetFlagAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
            return new MachineSchedulePlannerReadinessService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                settings.Object);
        }

        public async Task<SeededWorkOrder> SeedWorkOrderAsync(
            string? workOrderNo = null,
            DateTime? headerStart = null,
            DateTime? headerCompletion = null,
            DateTime? machineStart = null,
            DateTime? machineCompletion = null,
            string machineCode = "MC1",
            string operationCode = "OP1",
            string workCentreCode = "WC1",
            string productCode = "FG-1",
            string branchCode = "HQ",
            string status = ProductionWorkOrderStatuses.Released,
            bool machineScheduled = true)
        {
            var start = headerStart ?? new DateTime(2026, 10, 1, 8, 0, 0);
            var completion = headerCompletion ?? new DateTime(2026, 10, 1, 9, 0, 0);
            // machineScheduled:false models an operation whose selected machine row has no planned dates.
            var mStart = machineStart ?? start;
            var mCompletion = machineCompletion ?? completion;
            var hasMachineDates = machineScheduled;

            EnsureBomSeeded();
            var order = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = branchCode,
                LocationCode = "SITE",
                WorkOrderNo = workOrderNo ?? $"WO-{Guid.NewGuid():N}"[..18],
                SnapshotRevision = 1,
                SnapshotHash = new string('A', 64),
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                IsLegacySnapshot = false,
                SourceDefinitionCode = PrProductDefinitionCodes.Standard,
                DefinitionEffectiveDate = new DateTime(2026, 9, 1),
                ProductCode = productCode,
                OutputUom = "EA",
                SourceBomHdrId = _bomHdrId,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "EA",
                PlannedQty = 10m,
                RemainingQty = 10m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = completion,
                SchedulingDirection = ProductionSchedulingDirections.Forward,
                ScheduleRevision = 0,
                Status = status,
                SourceType = ProductionSourceTypes.Manual,
                RowVersion = [1]
            };
            var step = new ProductionWorkOrderRouteStep
            {
                WorkOrder = order,
                StageSequence = 10,
                WorkCentreCode = workCentreCode,
                OutputItemCode = productCode,
                OutputType = PrRouteOutputTypes.FinishedGoods,
                OutputBaseQty = 1m,
                OutputUom = "EA",
                OutputBaseUom = "EA",
                OutputConversionFactorToBase = 1m,
                PlannedQty = 10m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = completion,
                RowVersion = [2]
            };
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrder = order,
                RouteStep = step,
                SequenceNo = 10,
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Machine,
                OperationCode = operationCode,
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                PlannedOutputUom = "EA",
                PlannedStartDateTime = hasMachineDates ? mStart : null,
                PlannedCompletionDateTime = hasMachineDates ? mCompletion : null,
                RowVersion = [3]
            };
            var machine = new ProductionWorkOrderMachine
            {
                Operation = operation,
                MachineCode = machineCode,
                IsDefault = true,
                IsSelected = true,
                Priority = 1,
                ParallelMachineCount = 1,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                PlannedRunMinutes = 60m,
                PlannedStartDateTime = hasMachineDates ? mStart : null,
                PlannedCompletionDateTime = hasMachineDates ? mCompletion : null,
                ScheduleRevision = 0,
                RowVersion = [4]
            };
            operation.Machines.Add(machine);
            step.Operations.Add(operation);
            order.RouteSteps.Add(step);
            order.Operations.Add(operation);

            await using var db = Factory.CreateDbContext();
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();
            await SeedMachineMasterAsync(machineCode);
            return new SeededWorkOrder(order.Uid, order.WorkOrderNo);
        }

        public async Task AddMachineOperationAsync(
            SeededWorkOrder seeded,
            string machineCode,
            string operationCode)
        {
            await using var db = Factory.CreateDbContext();
            var order = await db.ProductionWorkOrders.SingleAsync(x => x.Uid == seeded.WorkOrderId);
            var start = new DateTime(2026, 10, 1, 13, 0, 0);
            var step = new ProductionWorkOrderRouteStep
            {
                WorkOrderId = order.Uid,
                StageSequence = 20,
                WorkCentreCode = "WC1",
                OutputItemCode = order.ProductCode,
                OutputType = PrRouteOutputTypes.FinishedGoods,
                OutputBaseQty = 1m,
                OutputUom = "EA",
                OutputBaseUom = "EA",
                OutputConversionFactorToBase = 1m,
                PlannedQty = 10m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = start.AddHours(2),
                RowVersion = [5]
            };
            db.ProductionWorkOrderRouteSteps.Add(step);
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrderId = order.Uid,
                RouteStep = step,
                SequenceNo = 20,
                ProcessSequence = 20,
                ProcessType = PrProcessTypes.Machine,
                OperationCode = operationCode,
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                PlannedOutputUom = "EA",
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = start.AddHours(2),
                RowVersion = [6]
            };
            operation.Machines.Add(new ProductionWorkOrderMachine
            {
                MachineCode = machineCode,
                IsDefault = true,
                IsSelected = true,
                Priority = 1,
                ParallelMachineCount = 1,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                PlannedRunMinutes = 120m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = start.AddHours(2),
                ScheduleRevision = 0,
                RowVersion = [7]
            });
            db.ProductionWorkOrderOperations.Add(operation);
            await db.SaveChangesAsync();
            await SeedMachineMasterAsync(machineCode);
        }

        /// <summary>Seeds minimal scheduled Work Orders used to exercise the 3,000-row guard.</summary>
        public async Task SeedManyMinimalWorkOrdersAsync(int count, string machinePrefix)
        {
            EnsureBomSeeded();
            await using var db = Factory.CreateDbContext();
            var start = new DateTime(2026, 10, 1, 8, 0, 0);
            var orders = new List<ProductionWorkOrder>(count);
            for (var i = 0; i < count; i++)
            {
                var order = new ProductionWorkOrder
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    LocationCode = "SITE",
                    WorkOrderNo = $"BULK-{i:D5}",
                    SnapshotRevision = 1,
                    SnapshotHash = new string('B', 64),
                    SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                    SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                    IsLegacySnapshot = false,
                    SourceDefinitionCode = PrProductDefinitionCodes.Standard,
                    DefinitionEffectiveDate = new DateTime(2026, 9, 1),
                    ProductCode = "FG-BULK",
                    OutputUom = "EA",
                    SourceBomHdrId = _bomHdrId,
                    SourceBomVersion = 1,
                    BomBaseQty = 1m,
                    BomBaseUom = "EA",
                    PlannedQty = 1m,
                    RemainingQty = 1m,
                    PlannedStartDateTime = start,
                    PlannedCompletionDateTime = start.AddHours(1),
                    SchedulingDirection = ProductionSchedulingDirections.Forward,
                    ScheduleRevision = 0,
                    Status = ProductionWorkOrderStatuses.Released,
                    SourceType = ProductionSourceTypes.Manual,
                    RowVersion = [1]
                };
                var step = new ProductionWorkOrderRouteStep
                {
                    WorkOrder = order,
                    StageSequence = 10,
                    WorkCentreCode = "WC-BULK",
                    OutputItemCode = "FG-BULK",
                    OutputType = PrRouteOutputTypes.FinishedGoods,
                    OutputBaseQty = 1m,
                    OutputUom = "EA",
                    OutputBaseUom = "EA",
                    OutputConversionFactorToBase = 1m,
                    PlannedQty = 1m,
                    PlannedStartDateTime = start,
                    PlannedCompletionDateTime = start.AddHours(1),
                    RowVersion = [2]
                };
                var operation = new ProductionWorkOrderOperation
                {
                    WorkOrder = order,
                    RouteStep = step,
                    SequenceNo = 10,
                    ProcessSequence = 10,
                    ProcessType = PrProcessTypes.Machine,
                    OperationCode = "OP-BULK",
                    IsFinalOperation = true,
                    PlannedQty = 1m,
                    PlannedOutputQty = 1m,
                    PlannedOutputUom = "EA",
                    PlannedStartDateTime = start,
                    PlannedCompletionDateTime = start.AddHours(1),
                    RowVersion = [3]
                };
                operation.Machines.Add(new ProductionWorkOrderMachine
                {
                    MachineCode = $"{machinePrefix}-{i:D5}",
                    IsDefault = true,
                    IsSelected = true,
                    Priority = 1,
                    ParallelMachineCount = 1,
                    CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                    OutputPerCycle = 1m,
                    PlannedRunMinutes = 60m,
                    PlannedStartDateTime = start,
                    PlannedCompletionDateTime = start.AddHours(1),
                    ScheduleRevision = 0,
                    RowVersion = [4]
                });
                step.Operations.Add(operation);
                order.RouteSteps.Add(step);
                order.Operations.Add(operation);
                orders.Add(order);
            }
            db.ProductionWorkOrders.AddRange(orders);
            await db.SaveChangesAsync();
        }

        public async Task SeedDeliveryRequestAsync(
            long workOrderId,
            string? deliveryRequestNo = null,
            DateTime? requiredDate = null,
            string priority = SaDeliveryRequestPriorities.Normal,
            string status = SaDeliveryRequestStatuses.Released,
            bool isActive = true,
            string projectCode = "PRJ-1",
            string warehouseCode = "WH01")
        {
            await using var db = Factory.CreateDbContext();
            var order = await db.ProductionWorkOrders.SingleAsync(x => x.Uid == workOrderId);
            var request = new SaDeliveryRequest
            {
                CompanyCode = order.CompanyCode,
                BranchCode = order.BranchCode,
                DeliveryRequestNo = deliveryRequestNo ?? $"DR-{Guid.NewGuid():N}"[..18],
                ProductCode = order.ProductCode,
                ProductDescription = order.ProductDescription,
                ProductionUom = "EA",
                RequestedQty = 10m,
                RequiredDate = requiredDate ?? new DateTime(2026, 10, 20),
                ProjectCode = projectCode,
                WarehouseCode = warehouseCode,
                Priority = priority,
                Status = status,
                RowVersion = [9]
            };
            db.SaDeliveryRequests.Add(request);
            await db.SaveChangesAsync();
            db.PrWorkOrderDemandAllocations.Add(new PrWorkOrderDemandAllocation
            {
                CompanyCode = order.CompanyCode,
                BranchCode = order.BranchCode,
                WorkOrderId = order.Uid,
                DeliveryRequestId = request.Uid,
                AllocatedQty = 10m,
                IsActive = isActive,
                CreatedDate = new DateTime(2026, 9, 1),
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedExternalAllocationAsync(
            string machineCode,
            string branchCode,
            DateTime start,
            DateTime end,
            int scheduleRevisionOffset = 0)
        {
            await using var db = Factory.CreateDbContext();
            // The external branch owns its own active Work Order + selected machine row.
            var order = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = branchCode,
                LocationCode = "SITE",
                WorkOrderNo = $"{branchCode}-WO-{Guid.NewGuid():N}"[..18],
                SnapshotRevision = 1,
                SnapshotHash = new string('C', 64),
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                IsLegacySnapshot = false,
                SourceDefinitionCode = PrProductDefinitionCodes.Standard,
                DefinitionEffectiveDate = new DateTime(2026, 9, 1),
                ProductCode = "FG-BR2",
                OutputUom = "EA",
                SourceBomHdrId = _bomHdrId,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "EA",
                PlannedQty = 5m,
                RemainingQty = 5m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = end,
                SchedulingDirection = ProductionSchedulingDirections.Forward,
                ScheduleRevision = 0,
                Status = ProductionWorkOrderStatuses.Released,
                SourceType = ProductionSourceTypes.Manual,
                RowVersion = [1]
            };
            var step = new ProductionWorkOrderRouteStep
            {
                WorkOrder = order,
                StageSequence = 10,
                WorkCentreCode = "WC2",
                OutputItemCode = "FG-BR2",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                OutputBaseQty = 1m,
                OutputUom = "EA",
                OutputBaseUom = "EA",
                OutputConversionFactorToBase = 1m,
                PlannedQty = 5m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = end,
                RowVersion = [2]
            };
            var operation = new ProductionWorkOrderOperation
            {
                WorkOrder = order,
                RouteStep = step,
                SequenceNo = 10,
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Machine,
                OperationCode = "OP-BR2",
                IsFinalOperation = true,
                PlannedQty = 5m,
                PlannedOutputQty = 5m,
                PlannedOutputUom = "EA",
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = end,
                RowVersion = [3]
            };
            var machine = new ProductionWorkOrderMachine
            {
                Operation = operation,
                MachineCode = machineCode,
                IsDefault = true,
                IsSelected = true,
                Priority = 1,
                ParallelMachineCount = 1,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                PlannedRunMinutes = 60m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = end,
                ScheduleRevision = 0,
                RowVersion = [4]
            };
            operation.Machines.Add(machine);
            step.Operations.Add(operation);
            order.RouteSteps.Add(step);
            order.Operations.Add(operation);
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();

            db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
            {
                CompanyCode = "DEMO",
                BranchCode = branchCode,
                MachineCode = machineCode,
                WorkOrderId = order.Uid,
                WorkOrderOperationId = operation.Uid,
                WorkOrderMachineId = machine.Uid,
                ScheduleRevision = machine.ScheduleRevision + scheduleRevisionOffset,
                FragmentNo = 1,
                StartDateTime = start,
                EndDateTime = end,
                ScheduleSourceHash = new string('D', 64),
                CreatedDate = new DateTime(2026, 9, 30),
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
            // A delivery request supplies the identity that must never leak through the overlay.
            var request = new SaDeliveryRequest
            {
                CompanyCode = "DEMO",
                BranchCode = branchCode,
                DeliveryRequestNo = $"DR-{branchCode}-{Guid.NewGuid():N}"[..14],
                ProductCode = "FG-BR2",
                ProductionUom = "EA",
                RequestedQty = 5m,
                RequiredDate = end,
                Priority = SaDeliveryRequestPriorities.Urgent,
                Status = SaDeliveryRequestStatuses.Released,
                RowVersion = [9]
            };
            db.SaDeliveryRequests.Add(request);
            await db.SaveChangesAsync();
            db.PrWorkOrderDemandAllocations.Add(new PrWorkOrderDemandAllocation
            {
                CompanyCode = "DEMO",
                BranchCode = branchCode,
                WorkOrderId = order.Uid,
                DeliveryRequestId = request.Uid,
                AllocatedQty = 5m,
                IsActive = true,
                CreatedDate = new DateTime(2026, 9, 1),
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedMachineMasterAsync(string machineCode)
        {
            await using var db = Factory.CreateDbContext();
            if (await db.PrMachines.AnyAsync(x => x.CompCode == "DEMO" && x.MachineCd == machineCode))
                return;
            db.PrMachines.Add(new PrMachine
            {
                MachineCd = machineCode,
                MachineDes = machineCode,
                ProcessCd = "PROC1",
                CompCode = "DEMO",
                BranchCode = "HQ",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        public async Task SeedMachineMastersAsync(int count, string prefix)
        {
            await using var db = Factory.CreateDbContext();
            for (var i = 0; i < count; i++)
            {
                var code = $"{prefix}{i:D4}";
                db.PrMachines.Add(new PrMachine
                {
                    MachineCd = code,
                    MachineDes = code,
                    ProcessCd = "PROC1",
                    CompCode = "DEMO",
                    BranchCode = "HQ",
                    Active = true
                });
            }
            await db.SaveChangesAsync();
        }

        public async Task SeedPreventiveAsync(string machineCode, DateTime downDate, string startTime, string endTime)
        {
            await using var db = Factory.CreateDbContext();
            db.PrPreventives.Add(new PrPreventive
            {
                CompCode = "DEMO",
                MachineCd = machineCode,
                DownDt = downDate,
                StartTm = downDate.Date.Add(TimeSpan.Parse(startTime)),
                EndTm = downDate.Date.Add(TimeSpan.Parse(endTime)),
                Status = PreventiveStatuses.Planned,
                ReasonCd = "PM",
                Remark = "Scheduled service"
            });
            await db.SaveChangesAsync();
        }

        private void EnsureBomSeeded()
        {
            if (_bomHdrId != 0)
                return;
            using var db = Factory.CreateDbContext();
            var source = new PrBomHdr
            {
                CompanyCode = "DEMO",
                ProdCode = "FG-1",
                DefinitionCode = PrProductDefinitionCodes.Standard,
                Version = 1,
                Status = PrBomStatuses.Active,
                EffectiveFrom = new DateTime(2026, 1, 1),
                BaseQty = 1m,
                BaseUom = "EA",
                BranchCode = "HQ",
                LocationCode = "SITE"
            };
            db.PrBomHdrs.Add(source);
            db.SaveChanges();
            _bomHdrId = source.Uid;
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    private sealed record SeededWorkOrder(long WorkOrderId, string WorkOrderNo);
}
