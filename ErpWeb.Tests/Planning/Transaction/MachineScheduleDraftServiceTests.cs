using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Tests.Infrastructure.Fakes;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Planning.Transaction;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class MachineScheduleDraftServiceTests
{
    [Fact]
    public void Draft_workspace_allows_temporary_overlap_and_clears_after_resolution()
    {
        var wo1Job = new MachineSchedulePlannerJob
        {
            WorkOrderId = 1,
            OperationId = 11,
            WorkOrderMachineId = 101,
            WorkOrderNo = "WO-001",
            ProductCode = "FG-1",
            MachineCode = "MC1",
            PlannedStart = new DateTime(2026, 10, 1, 8, 0, 0),
            PlannedCompletion = new DateTime(2026, 10, 1, 10, 0, 0),
            PlannedQuantity = 10m,
            OutputUom = "EA"
        };
        var wo2Job = new MachineSchedulePlannerJob
        {
            WorkOrderId = 2,
            OperationId = 22,
            WorkOrderMachineId = 202,
            WorkOrderNo = "WO-002",
            ProductCode = "FG-2",
            MachineCode = "MC1",
            PlannedStart = new DateTime(2026, 10, 1, 10, 0, 0),
            PlannedCompletion = new DateTime(2026, 10, 1, 12, 0, 0),
            PlannedQuantity = 10m,
            OutputUom = "EA"
        };

        var appointments = new List<MachineSchedulePlannerAppointment>
        {
            new()
            {
                Id = "seg-1",
                Subject = "WO-001 · FG-1",
                Location = "operation:11",
                Start = wo1Job.PlannedStart,
                End = wo1Job.PlannedCompletion,
                ResourceId = "MC1",
                MachineCode = "MC1",
                WorkOrderId = 1,
                OperationId = 11,
                WorkOrderMachineId = 101,
                WorkOrderNo = "WO-001",
                LabelId = MachineSchedulePlannerLabelIds.Scheduled
            },
            new()
            {
                Id = "seg-2",
                Subject = "WO-002 · FG-2",
                Location = "operation:22",
                Start = wo2Job.PlannedStart,
                End = wo2Job.PlannedCompletion,
                ResourceId = "MC1",
                MachineCode = "MC1",
                WorkOrderId = 2,
                OperationId = 22,
                WorkOrderMachineId = 202,
                WorkOrderNo = "WO-002",
                LabelId = MachineSchedulePlannerLabelIds.Scheduled
            }
        };

        var workspace = new MachineScheduleDraftWorkspace();
        workspace.Initialize("fp-base", appointments, [], enterDraftMode: true);

        Assert.True(workspace.IsActive);
        Assert.Equal(0, workspace.PendingCount);
        Assert.Equal(0, workspace.ConflictCount);

        // Step 1: Move WO-001 onto WO-002's 10:00–12:00 slot (allowed temporarily with conflict marker)
        workspace.StageMove(wo1Job, new DateTime(2026, 10, 1, 10, 0, 0), "Move WO-001");
        Assert.Equal(1, workspace.PendingCount);
        Assert.Equal(1, workspace.ConflictCount);
        Assert.True(workspace.CanUndo);
        Assert.All(workspace.DraftAppointments, a => Assert.True(a.IsConflict));

        // Step 2: Shift WO-002 to 13:00 (after lunch break) -> resolves conflict cleanly
        workspace.StageMove(wo2Job, new DateTime(2026, 10, 1, 13, 0, 0), "Shift WO-002");
        Assert.Equal(2, workspace.PendingCount);
        Assert.Equal(0, workspace.ConflictCount);
        Assert.All(workspace.DraftAppointments, a => Assert.False(a.IsConflict));

        // Undo returns the temporary overlap; Redo clears it again; Reset restores baseline
        Assert.True(workspace.Undo());
        Assert.Equal(1, workspace.PendingCount);
        Assert.Equal(1, workspace.ConflictCount);
        Assert.True(workspace.CanRedo);

        Assert.True(workspace.Redo());
        Assert.Equal(2, workspace.PendingCount);
        Assert.Equal(0, workspace.ConflictCount);

        workspace.ResetAll();
        Assert.Equal(0, workspace.PendingCount);
        Assert.Equal(0, workspace.ConflictCount);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.CanRedo);
    }

    [Fact]
    public async Task Draft_workspace_and_server_detect_off_filter_hidden_conflict()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var wo1 = await fixture.SeedScheduledWorkOrderAsync(
            workOrderNo: "WO-FILTER-1",
            start: new DateTime(2026, 10, 1, 8, 0, 0),
            completion: new DateTime(2026, 10, 1, 9, 0, 0));
        var wo2 = await fixture.SeedScheduledWorkOrderAsync(
            workOrderNo: "WO-FILTER-2",
            start: new DateTime(2026, 10, 1, 10, 0, 0),
            completion: new DateTime(2026, 10, 1, 11, 0, 0));
        await fixture.BackfillSegmentsAsync();

        var planner = fixture.CreatePlannerService();
        var boardResult = await planner.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = new DateTime(2026, 10, 1),
            WindowEnd = new DateTime(2026, 10, 3),
            WorkOrderNo = "WO-FILTER-1"
        });

        Assert.True(boardResult.Succeeded, boardResult.Message);
        var board = boardResult.Data!;
        var visibleJob = Assert.Single(board.Jobs);
        Assert.Equal(wo1.WorkOrderNo, visibleJob.WorkOrderNo);
        Assert.Contains(board.HiddenOccupiedIntervals,
            x => x.WorkOrderNo == wo2.WorkOrderNo && x.MachineCode == "MC1");

        var workspace = new MachineScheduleDraftWorkspace();
        workspace.Initialize(board, enterDraftMode: true);
        workspace.StageMove(visibleJob, new DateTime(2026, 10, 1, 10, 0, 0), "Collide with hidden WO2");

        Assert.Equal(1, workspace.ConflictCount);
        Assert.True(workspace.Conflicts[0].IsOffFilterConflict);
        Assert.Equal(wo2.WorkOrderNo, workspace.Conflicts[0].WorkOrderNoB);

        // Server PreviewDraft also sees hidden WO2 on MC1 and avoids silent double-booking
        var advanced = fixture.CreateAdvancedService();
        var serverPreview = await advanced.PreviewDraftAsync(new MachineScheduleDraftRequest
        {
            BoardBaseFingerprint = board.BoardFingerprint,
            Commands = workspace.PendingCommands.ToList(),
            ExpectedVersions =
            [
                new MachineScheduleDraftExpectedVersion
                {
                    WorkOrderId = visibleJob.WorkOrderId,
                    WorkOrderMachineId = visibleJob.WorkOrderMachineId,
                    ExpectedWorkOrderScheduleRevision = visibleJob.ScheduleRevision,
                    ExpectedWorkOrderRowVersion = visibleJob.WorkOrderRowVersion,
                    ExpectedMachineScheduleRevision = visibleJob.MachineScheduleRevision,
                    ExpectedMachineRowVersion = visibleJob.MachineRowVersion
                }
            ]
        });

        Assert.True(serverPreview.Succeeded, serverPreview.Message);
        // Server PreviewDraft evaluates the whole draft including hidden off-filter WO-FILTER-2
        // and blocks committing with an explicit conflict on MC1.
        Assert.False(serverPreview.Data!.CanCommit);
        Assert.Contains(serverPreview.Data.Conflicts,
            c => c.MachineCode == "MC1" && c.OverlapStart == new DateTime(2026, 10, 1, 10, 0, 0));
    }

    [Fact]
    public async Task Draft_preview_has_no_db_side_effects_and_commit_applies_atomically()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var wo1 = await fixture.SeedScheduledWorkOrderAsync(
            workOrderNo: "WO-DRAFT-1",
            start: new DateTime(2026, 10, 1, 8, 0, 0),
            completion: new DateTime(2026, 10, 1, 9, 0, 0));
        var wo2 = await fixture.SeedScheduledWorkOrderAsync(
            workOrderNo: "WO-DRAFT-2",
            start: new DateTime(2026, 10, 1, 10, 0, 0),
            completion: new DateTime(2026, 10, 1, 11, 0, 0));
        await fixture.BackfillSegmentsAsync();

        // Reload expected revisions after backfill
        var planner = fixture.CreatePlannerService();
        var board = (await planner.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = new DateTime(2026, 10, 1),
            WindowEnd = new DateTime(2026, 10, 3)
        })).Data!;

        var job1 = board.Jobs.Single(x => x.WorkOrderId == wo1.WorkOrderId);
        var job2 = board.Jobs.Single(x => x.WorkOrderId == wo2.WorkOrderId);

        var request = new MachineScheduleDraftRequest
        {
            BoardBaseFingerprint = board.BoardFingerprint,
            Commands =
            [
                // Step 1 moves WO1 into WO2's slot (temporary overlap!)
                new MachineScheduleDraftCommand
                {
                    Kind = MachineScheduleDraftCommandKind.Move,
                    WorkOrderId = job1.WorkOrderId,
                    OperationId = job1.OperationId,
                    WorkOrderMachineId = job1.WorkOrderMachineId,
                    MachineCode = job1.MachineCode,
                    WorkOrderNo = job1.WorkOrderNo,
                    RequestedStart = new DateTime(2026, 10, 1, 10, 0, 0),
                    Reason = "Move WO1 to 10:00"
                },
                // Step 2 moves WO2 out of 10:00 to 13:00
                new MachineScheduleDraftCommand
                {
                    Kind = MachineScheduleDraftCommandKind.Move,
                    WorkOrderId = job2.WorkOrderId,
                    OperationId = job2.OperationId,
                    WorkOrderMachineId = job2.WorkOrderMachineId,
                    MachineCode = job2.MachineCode,
                    WorkOrderNo = job2.WorkOrderNo,
                    RequestedStart = new DateTime(2026, 10, 1, 13, 0, 0),
                    Reason = "Shift WO2 to 13:00"
                }
            ],
            ExpectedVersions =
            [
                new MachineScheduleDraftExpectedVersion
                {
                    WorkOrderId = job1.WorkOrderId,
                    WorkOrderMachineId = job1.WorkOrderMachineId,
                    ExpectedWorkOrderScheduleRevision = job1.ScheduleRevision,
                    ExpectedWorkOrderRowVersion = job1.WorkOrderRowVersion,
                    ExpectedMachineScheduleRevision = job1.MachineScheduleRevision,
                    ExpectedMachineRowVersion = job1.MachineRowVersion
                },
                new MachineScheduleDraftExpectedVersion
                {
                    WorkOrderId = job2.WorkOrderId,
                    WorkOrderMachineId = job2.WorkOrderMachineId,
                    ExpectedWorkOrderScheduleRevision = job2.ScheduleRevision,
                    ExpectedWorkOrderRowVersion = job2.WorkOrderRowVersion,
                    ExpectedMachineScheduleRevision = job2.MachineScheduleRevision,
                    ExpectedMachineRowVersion = job2.MachineRowVersion
                }
            ]
        };

        var advanced = fixture.CreateAdvancedService();
        var preview = await advanced.PreviewDraftAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.NotNull(preview.Data);
        Assert.True(preview.Data!.CanCommit, string.Join(" ", preview.Data.Blockers.Concat(preview.Data.Conflicts.Select(c => c.Message))));
        Assert.Empty(preview.Data.Conflicts);
        Assert.NotEmpty(preview.Data.PreviewToken);

        // Preview MUST leave DB completely untouched
        await using (var checkDb = fixture.Factory.CreateDbContext())
        {
            var dbWo1 = await checkDb.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == wo1.WorkOrderId);
            var dbWo2 = await checkDb.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == wo2.WorkOrderId);
            Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), dbWo1.PlannedStartDateTime);
            Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), dbWo2.PlannedStartDateTime);
            Assert.Equal(job1.ScheduleRevision, dbWo1.ScheduleRevision);
            Assert.Equal(job2.ScheduleRevision, dbWo2.ScheduleRevision);
        }

        // Atomic Commit
        var committed = await advanced.CommitDraftAsync(request with { PreviewToken = preview.Data.PreviewToken });
        Assert.True(committed.Succeeded, committed.Message);
        Assert.Equal(2, committed.Data!.AppliedCommandCount);
        Assert.Equal(2, committed.Data.AffectedWorkOrderCount);

        await using (var verifyDb = fixture.Factory.CreateDbContext())
        {
            var dbWo1 = await verifyDb.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == wo1.WorkOrderId);
            var dbWo2 = await verifyDb.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == wo2.WorkOrderId);
            Assert.Equal(new DateTime(2026, 10, 1, 10, 0, 0), dbWo1.PlannedStartDateTime);
            Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0), dbWo1.PlannedCompletionDateTime);
            Assert.Equal(new DateTime(2026, 10, 1, 13, 0, 0), dbWo2.PlannedStartDateTime);
            Assert.Equal(new DateTime(2026, 10, 1, 14, 0, 0), dbWo2.PlannedCompletionDateTime);

            var capacity = await new ProductionMachineCapacityService(fixture.Factory)
                .LoadAsync(verifyDb, "DEMO", ["MC1"]);
            Assert.False(capacity.HasBlockers, string.Join(" ", capacity.Blockers));
        }
    }

    [Fact]
    public async Task Draft_commit_rejects_stale_token_without_partial_writes()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var wo1 = await fixture.SeedScheduledWorkOrderAsync(
            workOrderNo: "WO-STALE-1",
            start: new DateTime(2026, 10, 1, 8, 0, 0),
            completion: new DateTime(2026, 10, 1, 9, 0, 0));
        await fixture.BackfillSegmentsAsync();

        var planner = fixture.CreatePlannerService();
        var board = (await planner.GetBoardAsync(new MachineSchedulePlannerQuery
        {
            WindowStart = new DateTime(2026, 10, 1),
            WindowEnd = new DateTime(2026, 10, 3)
        })).Data!;
        var job1 = Assert.Single(board.Jobs);

        var request = new MachineScheduleDraftRequest
        {
            BoardBaseFingerprint = board.BoardFingerprint,
            Commands =
            [
                new MachineScheduleDraftCommand
                {
                    Kind = MachineScheduleDraftCommandKind.Move,
                    WorkOrderId = job1.WorkOrderId,
                    OperationId = job1.OperationId,
                    WorkOrderMachineId = job1.WorkOrderMachineId,
                    MachineCode = job1.MachineCode,
                    WorkOrderNo = job1.WorkOrderNo,
                    RequestedStart = new DateTime(2026, 10, 1, 10, 0, 0),
                    Reason = "Move WO-STALE-1"
                }
            ],
            ExpectedVersions =
            [
                new MachineScheduleDraftExpectedVersion
                {
                    WorkOrderId = job1.WorkOrderId,
                    WorkOrderMachineId = job1.WorkOrderMachineId,
                    ExpectedWorkOrderScheduleRevision = job1.ScheduleRevision,
                    ExpectedWorkOrderRowVersion = job1.WorkOrderRowVersion,
                    ExpectedMachineScheduleRevision = job1.MachineScheduleRevision,
                    ExpectedMachineRowVersion = job1.MachineRowVersion
                }
            ]
        };

        var advanced = fixture.CreateAdvancedService();
        var preview = await advanced.PreviewDraftAsync(request);
        Assert.True(preview.Succeeded && preview.Data!.CanCommit);

        // Concurrent external edit bumps ScheduleRevision before draft commit
        await using (var concurrentDb = fixture.Factory.CreateDbContext())
        {
            var order = await concurrentDb.ProductionWorkOrders.SingleAsync(x => x.Uid == wo1.WorkOrderId);
            order.ScheduleRevision++;
            await concurrentDb.SaveChangesAsync();
        }

        var commit = await advanced.CommitDraftAsync(request with { PreviewToken = preview.Data.PreviewToken });
        Assert.False(commit.Succeeded);

        await using var verifyDb = fixture.Factory.CreateDbContext();
        var persistedOrder = await verifyDb.ProductionWorkOrders.AsNoTracking().SingleAsync(x => x.Uid == wo1.WorkOrderId);
        Assert.Equal(new DateTime(2026, 10, 1, 8, 0, 0), persistedOrder.PlannedStartDateTime);
    }

    [Fact]
    public void Draft_ui_contract_exposes_controls_and_preserves_theme_variables()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var razor = File.ReadAllText(Path.Combine(repoRoot, "ErpWeb.UI", "Planning", "WorkOrders", "PrMachineSchedulePlanner.razor"));
        var codeBehind = File.ReadAllText(Path.Combine(repoRoot, "ErpWeb.UI", "Planning", "WorkOrders", "PrMachineSchedulePlanner.razor.cs"));
        var css = File.ReadAllText(Path.Combine(repoRoot, "ErpWeb.UI", "Planning", "WorkOrders", "PrMachineSchedulePlanner.razor.css"));

        Assert.Contains("RESCHEDULING DRAFT", razor, StringComparison.Ordinal);
        Assert.Contains("Preview Changes", razor, StringComparison.Ordinal);
        Assert.Contains("Commit All Changes", razor, StringComparison.Ordinal);
        Assert.Contains("Stage in draft", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("<style", razor, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("MachineScheduleDraftWorkspace", codeBehind, StringComparison.Ordinal);
        Assert.Contains("StageRescheduleDraft", codeBehind, StringComparison.Ordinal);
        Assert.Contains("StageAdvancedDraft", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PreviewDraftAsync", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CommitDraftAsync", codeBehind, StringComparison.Ordinal);

        Assert.Contains(".pr-machine-schedule-draft-bar", css, StringComparison.Ordinal);
        Assert.Contains("var(--surface)", css, StringComparison.Ordinal);
        Assert.Contains("var(--danger)", css, StringComparison.Ordinal);
    }

    private sealed class DraftFixture : IAsyncDisposable
    {
        private static readonly DateOnly HorizonStart = new(2026, 10, 1);
        private static readonly DateOnly HorizonEnd = new(2026, 10, 15);
        private readonly SqliteConnection _connection;
        private readonly FakeProductionCalendarScheduleDataLoader _calendars = new();

        private DraftFixture(SqliteConnection connection, TestDbContextFactory factory)
        {
            _connection = connection;
            Factory = factory;
        }

        public TestDbContextFactory Factory { get; }

        public static async Task<DraftFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var factory = new TestDbContextFactory(options);
            await using (var db = factory.CreateDbContext())
                await db.Database.EnsureCreatedAsync();
            return new DraftFixture(connection, factory);
        }

        public async Task<(long WorkOrderId, string WorkOrderNo)> SeedScheduledWorkOrderAsync(
            string workOrderNo,
            DateTime start,
            DateTime completion)
        {
            var data = await _calendars.LoadAsync("DEMO", "MC1", HorizonStart, HorizonEnd);
            var sourceHash = WorkOrderScheduleSourceHasher.Compute(data, HorizonStart, HorizonEnd);

            await using var db = Factory.CreateDbContext();
            var bom = await db.PrBomHdrs.FirstOrDefaultAsync(x => x.CompanyCode == "DEMO" && x.ProdCode == "FG-1");
            if (bom is null)
            {
                bom = new PrBomHdr
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
                db.PrBomHdrs.Add(bom);
                await db.SaveChangesAsync();
            }

            var order = new ProductionWorkOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "SITE",
                WorkOrderNo = workOrderNo,
                SnapshotRevision = 1,
                SnapshotHash = new string('D', 64),
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
                IsLegacySnapshot = false,
                SourceDefinitionCode = PrProductDefinitionCodes.Standard,
                DefinitionEffectiveDate = new DateTime(2026, 9, 1),
                ProductCode = "FG-1",
                OutputUom = "EA",
                SourceBomHdrId = bom.Uid,
                SourceBomVersion = 1,
                BomBaseQty = 1m,
                BomBaseUom = "EA",
                PlannedQty = 10m,
                RemainingQty = 10m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = completion,
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
                WorkCentreCode = "WC1",
                OutputItemCode = "FG-1",
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
            var op = new ProductionWorkOrderOperation
            {
                WorkOrder = order,
                RouteStep = step,
                SequenceNo = 10,
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Machine,
                OperationCode = "OP1",
                IsFinalOperation = true,
                PlannedQty = 10m,
                PlannedOutputQty = 10m,
                RemainingQty = 10m,
                PlannedOutputUom = "EA",
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = completion,
                ScheduleSourceHash = sourceHash,
                CalendarSourceType = ProductionCalendarSourceTypes.Machine,
                CalendarHorizonStart = HorizonStart.ToDateTime(TimeOnly.MinValue),
                CalendarHorizonEnd = HorizonEnd.ToDateTime(TimeOnly.MinValue),
                RowVersion = [3]
            };
            var machine = new ProductionWorkOrderMachine
            {
                Operation = op,
                MachineCode = "MC1",
                IsDefault = true,
                IsSelected = true,
                Priority = 1,
                ParallelMachineCount = 1,
                CycleQuantityMode = ProductionMachineCycleQuantityModes.Discrete,
                OutputPerCycle = 1m,
                PlannedCycleCount = 10m,
                PlannedCycleSlots = 10,
                CycleSeconds = 360m,
                PlannedRunMinutes = 60m,
                PlannedStartDateTime = start,
                PlannedCompletionDateTime = completion,
                ScheduleRevision = 0,
                ScheduleSourceHash = sourceHash,
                CalendarHorizonStart = HorizonStart.ToDateTime(TimeOnly.MinValue),
                CalendarHorizonEnd = HorizonEnd.ToDateTime(TimeOnly.MinValue),
                RowVersion = [4]
            };
            op.Machines.Add(machine);
            step.Operations.Add(op);
            order.RouteSteps.Add(step);
            order.Operations.Add(op);

            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();
            db.ProductionMachineScheduleAllocations.Add(new ProductionMachineScheduleAllocation
            {
                CompanyCode = order.CompanyCode,
                BranchCode = order.BranchCode,
                MachineCode = machine.MachineCode,
                WorkOrderId = order.Uid,
                WorkOrderOperationId = op.Uid,
                WorkOrderMachineId = machine.Uid,
                ScheduleRevision = machine.ScheduleRevision,
                FragmentNo = 1,
                StartDateTime = start,
                EndDateTime = completion,
                ScheduleSourceHash = sourceHash,
                CreatedDate = new DateTime(2026, 9, 30),
                CreatedBy = "test"
            });
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");

            return (order.Uid, order.WorkOrderNo);
        }

        public async Task BackfillSegmentsAsync()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var backfill = new MachineScheduleSegmentBackfillService(
                Factory, InventoryTenantTestHelper.CreateTenantContext(), access.Object);
            var preview = await backfill.PreviewAsync();
            Assert.True(preview.Succeeded, preview.Message);
            var apply = await backfill.ApplyAsync(preview.Data!.PreviewToken);
            Assert.True(apply.Succeeded, apply.Message);
        }

        public MachineSchedulePlannerService CreatePlannerService()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var readiness = new Mock<IMachineSchedulePlannerReadinessService>();
            readiness.Setup(x => x.GetReadinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MachineSchedulePlannerReadiness
                {
                    IsWriteReady = true,
                    WriteSettingEnabled = true,
                    AllocationSchemaReady = true,
                    ActiveAllocationBackfillComplete = true
                });
            return new MachineSchedulePlannerService(
                Factory, InventoryTenantTestHelper.CreateTenantContext(), access.Object, readiness.Object);
        }

        public MachineScheduleAdvancedPlanningService CreateAdvancedService()
        {
            var access = new Mock<IAccessRightService>();
            access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var settings = new Mock<IAppSettingService>();
            settings.Setup(x => x.GetFlagAsync(
                    AppSettingModules.Planning,
                    AppSettingCatalogue.PlanningKeys.MachineScheduleSegmentWriteEnabled,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            var readiness = new Mock<IMachineSchedulePlannerReadinessService>();
            readiness.Setup(x => x.GetReadinessAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new MachineSchedulePlannerReadiness
                {
                    IsWriteReady = true,
                    WriteSettingEnabled = true,
                    AllocationSchemaReady = true,
                    ActiveAllocationBackfillComplete = true
                });
            return new MachineScheduleAdvancedPlanningService(
                Factory,
                InventoryTenantTestHelper.CreateTenantContext(),
                access.Object,
                settings.Object,
                readiness.Object,
                _calendars,
                new AlwaysOpenWorkOrderCalendarProvider(),
                new ProductionMachineCapacityService(Factory),
                new IvUomConversionService(Factory));
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
