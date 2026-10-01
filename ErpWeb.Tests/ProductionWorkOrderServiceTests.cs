using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Planning;
using ErpWeb.Core.Production;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionWorkOrderServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionWorkOrderServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvStockMasters.AddRange(
            Stock("DEMO", "FG001", "Finished Good", "PCS", PrMfgTypes.Make),
            Stock("DEMO", "RM001", "Raw Material", "KG", PrMfgTypes.Buy));

        var header = new PrBomHdr
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            DefinitionName = PrProductDefinitionCodes.StandardName,
            IsDefaultDefinition = true,
            Version = 3,
            Status = PrBomStatuses.Active,
            EffectiveFrom = new DateTime(2026, 1, 1),
            BaseQty = 5m,
            BaseUom = "PCS",
            BranchCode = "HQ",
            LocationCode = "SITE",
            RowVersion = [1]
        };
        header.Lines.Add(new PrDefBOM
        {
            CompanyCode = "DEMO",
            ProdCode = "FG001",
            ICode = "RM001",
            IName = "Raw Material",
            StdQty = 2m,
            StdUom = "KG",
            SeqNo = 1,
            ScrapPercent = 10m,
            Tolerance = 0.25m,
            Warehouse = "WH01",
            BranchCode = "HQ",
            LocationCode = "SITE",
            RowVersion = [1]
        });
        db.PrBomHdrs.Add(header);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Process_preview_uses_active_bom_and_does_not_persist()
    {
        await SeedManualCurrentRouteAsync();
        var sut = CreateSut();

        var result = await sut.ProcessPreviewAsync(Request(10m));

        Assert.True(result.Succeeded, result.Message);
        var preview = Assert.IsType<ProductionWorkOrderPreview>(result.Data);
        Assert.Equal(3, preview.SourceBomVersion);
        Assert.Equal(5m, preview.BomBaseQty);
        Assert.Equal(64, preview.SnapshotHash.Length);
        var material = Assert.Single(preview.Materials);
        Assert.Equal("RM001", material.ComponentCode);
        Assert.Equal(3, material.SourceBomVersion);
        Assert.Equal(5m, material.BomOutputQty);
        Assert.Equal("PCS", material.BomOutputUom);
        Assert.Equal(0.88m, material.RequiredQty);
        Assert.Equal(0.25m, material.Tolerance);
        Assert.Single(preview.RouteSteps);
        Assert.Single(preview.Operations);
        Assert.Equal(new DateTime(2026, 10, 1), preview.ScheduleAnchorDateTime);
        Assert.Equal(ProductionSchedulingDirections.Forward, preview.SchedulingDirection);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.ProductionWorkOrders.AnyAsync());
    }

    [Fact]
    public async Task Preview_snapshots_routing_from_the_selected_bom_revision()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PrWorkCentres.Add(new PrWorkCentre
            {
                WrkCtrCd = "WC01", WrkCtrDes = "Mixing", CompCode = "DEMO"
            });
            db.PrProcesses.Add(new PrProcess
            {
                ProcessCd = "MIX", ProcessDes = "Mix", WorkCentre = "WC01", CompCode = "DEMO"
            });
            var header = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "FG001");
            var seededOperation = new PrBomOperation
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                CentralSequence = 10,
                OutputBaseQty = 5m,
                OutputUom = "PCS",
                OperationCode = "MIX",
                ProcessSequence = 10,
                IsFinalOperation = true,
                RowVersion = [1],
                Machines =
                [
                    new PrBomMachineOption
                    {
                        MachineCode = "MX01",
                        MachineDescription = "Mixer snapshot",
                        ResourceSequence = 10,
                        IsPrimary = true,
                        CycleSeconds = 30m,
                        SetupSeconds = 60m,
                        QueueSeconds = 30m,
                        ParallelMachineCount = 1,
                        RowVersion = [1],
                        Labours =
                        [
                            new PrBomLabourStandard
                            {
                                LabourCode = "LAB01",
                                LabourDescription = "Labour snapshot",
                                CostPerOutputUnit = 2m,
                                RowVersion = [1]
                            }
                        ]
                    }
                ]
            };
            header.RouteSteps.Add(new PrBomRouteStep
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                StageSequence = 10,
                OutputItemCode = "FG001",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                StandardOutputQty = 1m,
                OutputUom = "PCS",
                RowVersion = [1],
                Operations = { seededOperation }
            });
            header.Operations.Add(seededOperation);
            var material = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == header.Uid);
            material.Operation = seededOperation;
            await db.SaveChangesAsync();
        }

        var preview = await CreateSut().ProcessPreviewAsync(Request(10m));

        Assert.True(preview.Succeeded, preview.Message);
        var operation = Assert.Single(preview.Data!.Operations);
        Assert.Equal("WC01", operation.WorkCentreCode);
        Assert.Equal("MIX", operation.OperationCode);
        Assert.True(operation.IsFinalOperation);
        Assert.Equal(2m, operation.PlannedOutputQty);
        var machine = Assert.Single(operation.Machines);
        Assert.Equal(60m, machine.SetupSeconds);
        Assert.Equal(1m, machine.PlannedRunMinutes);
        Assert.Equal(30m, machine.QueueSeconds);
        var labour = Assert.Single(machine.Labours);
        Assert.Equal(2m, labour.Rate);
        Assert.Equal(4m, labour.PlannedAmount);
        Assert.Single(preview.Data.RouteSteps);
    }

    [Fact]
    public async Task Preview_and_create_use_the_same_current_snapshot_pipeline()
    {
        await SeedManualCurrentRouteAsync();
        var sut = CreateSut();
        var request = Request(10m);

        var preview = await sut.ProcessPreviewAsync(request);
        var created = await sut.CreateDraftAsync(request);

        Assert.True(preview.Succeeded, preview.Message);
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(preview.Data!.SourceBomHdrId, created.Data!.SourceBomHdrId);
        Assert.Equal(preview.Data.SourceBomVersion, created.Data.SourceBomVersion);
        Assert.Equal(preview.Data.ScheduleAnchorDateTime, created.Data.ScheduleAnchorDateTime);
        Assert.Equal(preview.Data.PlannedStartDate, created.Data.PlannedStartDate);
        Assert.Equal(preview.Data.PlannedCompletionDate, created.Data.PlannedCompletionDate);
        Assert.Equal(preview.Data.SnapshotHash, created.Data.SnapshotHash);
        Assert.Equal(
            preview.Data.Materials.Select(x => (x.ComponentCode, x.RequiredQty)),
            created.Data.Materials.Select(x => (x.ComponentCode, x.RequiredQty)));
        Assert.Equal(
            preview.Data.Operations.Select(x => (x.OperationCode, x.PlannedStartDate, x.PlannedCompletionDate)),
            created.Data.Operations.Select(x => (x.OperationCode, x.PlannedStartDate, x.PlannedCompletionDate)));
    }

    [Fact]
    public async Task Refresh_definition_preserves_the_stored_planner_anchor()
    {
        await SeedManualCurrentRouteAsync();
        var sut = CreateSut();
        var request = Request(10m);
        request.SchedulingDirection = ProductionSchedulingDirections.Backward;
        request.PlannedCompletionDate = new DateTime(2026, 10, 3, 14, 30, 0);
        var expectedAnchor = new DateTime(2026, 10, 4).AddTicks(-1);

        var created = await sut.CreateDraftAsync(request);
        Assert.True(created.Succeeded, created.Message);
        Assert.Equal(expectedAnchor, created.Data!.ScheduleAnchorDateTime);

        var preview = await sut.PreviewRefreshFromDefinitionAsync(created.Data.WorkOrderNo);
        Assert.True(preview.Succeeded, preview.Message);

        var refreshed = await sut.RefreshDraftFromDefinitionAsync(new ProductionWorkOrderRefreshConfirm
        {
            WorkOrderNo = created.Data.WorkOrderNo,
            RowVersion = preview.Data!.RowVersion,
            SourceProductDefinitionRevisionId = preview.Data.SourceProductDefinitionRevisionId ?? 0,
            DefinitionSourceHashVersion = preview.Data.DefinitionSourceHashVersion,
            DefinitionSourceHash = preview.Data.DefinitionSourceHash,
            Reason = "Refresh anchor regression"
        });

        Assert.True(refreshed.Succeeded, refreshed.Message);
        Assert.Equal(expectedAnchor, refreshed.Data!.ScheduleAnchorDateTime);
    }

    [Theory]
    [InlineData(ProductionSchedulingDirections.Forward, 0, 0, 0, 0)]
    [InlineData(ProductionSchedulingDirections.Backward, 23, 59, 59, 999)]
    public void Planner_date_anchor_is_a_direction_aware_neutral_boundary(
        string direction,
        int hour,
        int minute,
        int second,
        int millisecond)
    {
        var date = new DateTime(2026, 10, 3, 12, 34, 56);

        var anchor = ProductionSchedulingDirections.NormalizePlannerDateAnchor(date, direction);

        Assert.Equal(date.Date, anchor.Date);
        Assert.Equal(hour, anchor.Hour);
        Assert.Equal(minute, anchor.Minute);
        Assert.Equal(second, anchor.Second);
        Assert.Equal(millisecond, anchor.Millisecond);
        if (direction == ProductionSchedulingDirections.Backward)
        {
            Assert.Equal(9_999, anchor.Ticks % TimeSpan.TicksPerMillisecond);
        }
    }

    [Fact]
    public async Task Select_draft_machine_flips_selection_and_preserves_anchor()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PrWorkCentres.Add(new PrWorkCentre
            {
                WrkCtrCd = "WC01", WrkCtrDes = "Mixing", CompCode = "DEMO"
            });
            var header = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "FG001");
            var seededOperation = new PrBomOperation
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                CentralSequence = 10,
                OutputBaseQty = 5m,
                OutputUom = "PCS",
                OperationCode = "MIX",
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Machine,
                IsFinalOperation = true,
                RowVersion = [1],
                Machines =
                [
                    new PrBomMachineOption
                    {
                        MachineCode = "MX01",
                        IsPrimary = true,
                        Priority = 1,
                        CycleSeconds = 30m,
                        OutputPerCycle = 1m,
                        ParallelMachineCount = 1,
                        RowVersion = [1]
                    },
                    new PrBomMachineOption
                    {
                        MachineCode = "MX02",
                        IsPrimary = false,
                        Priority = 2,
                        CycleSeconds = 45m,
                        OutputPerCycle = 1m,
                        ParallelMachineCount = 1,
                        RowVersion = [1]
                    }
                ]
            };
            header.RouteSteps.Add(new PrBomRouteStep
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                StageSequence = 10,
                OutputItemCode = "FG001",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                StandardOutputQty = 1m,
                OutputUom = "PCS",
                RowVersion = [1],
                Operations = { seededOperation }
            });
            header.Operations.Add(seededOperation);
            var material = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == header.Uid);
            material.Operation = seededOperation;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var created = await sut.CreateDraftAsync(Request(10m));
        Assert.True(created.Succeeded, created.Message);
        var operation = Assert.Single(created.Data!.Operations);
        Assert.Equal(2, operation.Machines.Count);
        var selected = Assert.Single(operation.Machines, m => m.IsSelected);
        Assert.Equal("MX01", selected.MachineCode);
        var alternate = Assert.Single(operation.Machines, m => !m.IsSelected);
        var anchor = created.Data.ScheduleAnchorDateTime;

        var switched = await sut.SelectDraftMachineAsync(new ProductionWorkOrderMachineSelectRequest
        {
            WorkOrderNo = created.Data.WorkOrderNo,
            RowVersion = created.Data.RowVersion,
            SnapshotRevision = created.Data.SnapshotRevision,
            SnapshotHash = created.Data.SnapshotHash,
            WorkOrderOperationId = alternate.WorkOrderOperationId,
            WorkOrderMachineId = alternate.Uid
        });

        Assert.True(switched.Succeeded, switched.Message);
        var after = Assert.Single(switched.Data!.Operations);
        Assert.Equal("MX02", Assert.Single(after.Machines, m => m.IsSelected).MachineCode);
        Assert.False(Assert.Single(after.Machines, m => m.MachineCode == "MX01").IsSelected);
        Assert.Equal(anchor, switched.Data.ScheduleAnchorDateTime);
        Assert.Equal(created.Data.SnapshotRevision + 1, switched.Data.SnapshotRevision);
        Assert.Contains(switched.Data.AuditEvents, e => e.EventType == ProductionAuditEventTypes.MachineSelected);

        var noop = await sut.SelectDraftMachineAsync(new ProductionWorkOrderMachineSelectRequest
        {
            WorkOrderNo = switched.Data.WorkOrderNo,
            RowVersion = switched.Data.RowVersion,
            SnapshotRevision = switched.Data.SnapshotRevision,
            SnapshotHash = switched.Data.SnapshotHash,
            WorkOrderOperationId = alternate.WorkOrderOperationId,
            WorkOrderMachineId = Assert.Single(after.Machines, m => m.IsSelected).Uid
        });
        Assert.True(noop.Succeeded, noop.Message);
        Assert.Equal(switched.Data.SnapshotRevision, noop.Data!.SnapshotRevision);
    }

    [Fact]
    public async Task Substitute_draft_material_uses_exact_source_revision_alternate()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(Stock("DEMO", "RM001A", "Alternate resin", "KG", PrMfgTypes.Buy));
            var header = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "FG001");
            var primary = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == header.Uid);
            primary.BomDefault = true;
            primary.AlternateGroupCode = "RESIN";
            await SeedManualCurrentRouteAsync(db, header);
            var operation = header.Operations.Single();
            primary.Operation = operation;
            db.PrDefBOMs.Add(new PrDefBOM
            {
                BomHdrId = header.Uid,
                CompanyCode = "DEMO",
                ProdCode = "FG001",
                ICode = "RM001A",
                IName = "Alternate resin",
                StdQty = 2.5m,
                StdUom = "KG",
                SeqNo = 2,
                ScrapPercent = 5m,
                Warehouse = "WH01",
                BomDefault = false,
                AlternateGroupCode = "RESIN",
                Operation = operation,
                BranchCode = "HQ",
                LocationCode = "SITE",
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var created = await sut.CreateDraftAsync(Request(10m));
        Assert.True(created.Succeeded, created.Message);
        var material = Assert.Single(created.Data!.Materials);
        Assert.Equal("RM001", material.ComponentCode);
        Assert.Equal("RESIN", material.AlternateGroupCode);
        var anchor = created.Data.ScheduleAnchorDateTime;

        var alternates = await sut.GetDraftMaterialAlternatesAsync(created.Data.WorkOrderNo, material.Uid);
        Assert.True(alternates.Succeeded, alternates.Message);
        var candidate = Assert.Single(alternates.Data!);
        Assert.Equal("RM001A", candidate.ComponentCode);

        var substituted = await sut.SubstituteDraftMaterialAsync(new ProductionWorkOrderMaterialSubstituteRequest
        {
            WorkOrderNo = created.Data.WorkOrderNo,
            RowVersion = created.Data.RowVersion,
            SnapshotRevision = created.Data.SnapshotRevision,
            SnapshotHash = created.Data.SnapshotHash,
            WorkOrderMaterialId = material.Uid,
            ReplacementSourceBomLineId = candidate.SourceBomLineId
        });

        Assert.True(substituted.Succeeded, substituted.Message);
        var replaced = Assert.Single(substituted.Data!.Materials);
        Assert.Equal("RM001A", replaced.ComponentCode);
        Assert.Equal("RESIN", replaced.AlternateGroupCode);
        Assert.Equal(anchor, substituted.Data.ScheduleAnchorDateTime);
        Assert.Equal(created.Data.SnapshotRevision + 1, substituted.Data.SnapshotRevision);
        Assert.Contains(substituted.Data.AuditEvents, e => e.EventType == ProductionAuditEventTypes.MaterialSubstituted);
    }

    [Fact]
    public async Task Snapshot_excludes_non_default_bom_alternates()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(Stock("DEMO", "RM001A", "Alternate resin", "KG", PrMfgTypes.Buy));
            var header = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "FG001");
            var primary = await db.PrDefBOMs.SingleAsync(x => x.BomHdrId == header.Uid);
            primary.BomDefault = true;
            primary.AlternateGroupCode = "RESIN";
            db.PrDefBOMs.Add(new PrDefBOM
            {
                BomHdrId = header.Uid,
                CompanyCode = "DEMO",
                ProdCode = "FG001",
                ICode = "RM001A",
                IName = "Alternate resin",
                StdQty = 2.5m,
                StdUom = "KG",
                SeqNo = 2,
                Warehouse = "WH01",
                BomDefault = false,
                AlternateGroupCode = "RESIN",
                BranchCode = "HQ",
                LocationCode = "SITE",
                RowVersion = [1]
            });
            await SeedManualCurrentRouteAsync(db, header);
            await db.SaveChangesAsync();
        }

        var preview = await CreateSut().ProcessPreviewAsync(Request(10m));
        Assert.True(preview.Succeeded, preview.Message);
        Assert.Equal("RM001", Assert.Single(preview.Data!.Materials).ComponentCode);
    }

    [Fact]
    public async Task Save_reload_and_master_change_preserve_snapshot()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));

        Assert.True(save.Succeeded, save.Message);
        Assert.Equal("WO00000001", save.Data!.WorkOrderNo);
        Assert.Equal(ProductionWorkOrderStatuses.Draft, save.Data.Status);
        Assert.Equal(4.4m, Assert.Single(save.Data.Materials).RequiredQty);
        Assert.Equal(ProductionAuditEventTypes.Created, Assert.Single(save.Data.AuditEvents).EventType);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var source = await db.PrDefBOMs.SingleAsync(x => x.ICode == "RM001");
            source.StdQty = 99m;
            source.RowVersion = Guid.NewGuid().ToByteArray();
            await db.SaveChangesAsync();
        }

        var reload = await sut.GetAsync(save.Data.WorkOrderNo);
        Assert.True(reload.Succeeded, reload.Message);
        Assert.Equal(4.4m, Assert.Single(reload.Data!.Materials).RequiredQty);
        Assert.Equal(save.Data.SnapshotHash, reload.Data.SnapshotHash);
    }

    [Fact]
    public async Task Nested_phantom_material_keeps_its_own_bom_revision_and_line_snapshots()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.AddRange(
                Stock("DEMO", "PH001", "Current phantom", "BOX", PrMfgTypes.Phantom),
                Stock("DEMO", "RM002", "Current nested material", "EA", PrMfgTypes.Buy));

            var root = await db.PrBomHdrs.SingleAsync(x => x.ProdCode == "FG001");
            db.PrDefBOMs.Add(new PrDefBOM
            {
                BomHdrId = root.Uid,
                CompanyCode = "DEMO",
                ProdCode = "FG001",
                ICode = "PH001",
                IName = "Phantom snapshot",
                StdQty = 1m,
                StdUom = "BOX",
                SeqNo = 2,
                Warehouse = "WH01",
                BranchCode = "HQ",
                LocationCode = "SITE",
                RowVersion = [1]
            });

            var phantom = new PrBomHdr
            {
                CompanyCode = "DEMO",
                ProdCode = "PH001",
                Version = 7,
                Status = PrBomStatuses.Active,
                EffectiveFrom = new DateTime(2026, 1, 1),
                BaseQty = 2m,
                BaseUom = "BOX",
                BranchCode = "HQ",
                LocationCode = "SITE",
                RowVersion = [1]
            };
            phantom.Lines.Add(new PrDefBOM
            {
                CompanyCode = "DEMO",
                ProdCode = "PH001",
                ICode = "RM002",
                IName = "Nested material snapshot",
                StdQty = 3m,
                StdUom = "L",
                SeqNo = 1,
                ScrapPercent = 5m,
                Tolerance = 0.1m,
                Warehouse = "WH02",
                BranchCode = "HQ",
                LocationCode = "BIN-A",
                RowVersion = [1]
            });
            db.PrBomHdrs.Add(phantom);
            await db.SaveChangesAsync();
        }

        var workOrders = CreateSut();
        var save = await workOrders.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var nested = Assert.Single(save.Data!.Materials, x => x.ComponentCode == "RM002");
        Assert.Equal(7, nested.SourceBomVersion);
        Assert.Equal(2m, nested.BomOutputQty);
        Assert.Equal("BOX", nested.BomOutputUom);
        Assert.Equal("PH001", nested.ParentProductCode);
        Assert.Equal("Nested material snapshot", nested.ComponentDescription);
        Assert.Equal("L", nested.RequiredUom);
        Assert.Equal("WH02", nested.WarehouseCode);
        Assert.Equal("BIN-A", nested.LocationCode);
        Assert.Equal(3.15m, nested.RequiredQty);

        var productDefinitions = CreateProductDefinitionSut();
        var deleteCheck = await productDefinitions.CanDeleteAsync([new PrProductDefinitionKey { ProdCode = "PH001", DefinitionCode = PrProductDefinitionCodes.Standard }]);
        Assert.True(deleteCheck.Succeeded, deleteCheck.Message);
        Assert.False(deleteCheck.Data!.CanDelete);
    }

    [Fact]
    public async Task Release_freezes_snapshot_and_has_no_inventory_side_effect()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var release = await sut.ReleaseAsync(save.Data!.WorkOrderNo, save.Data.RowVersion);

        Assert.True(release.Succeeded, release.Message);
        Assert.Equal(ProductionWorkOrderStatuses.Released, release.Data!.Status);
        Assert.NotNull(release.Data.ReleasedDate);
        Assert.Contains(release.Data.AuditEvents, x => x.EventType == ProductionAuditEventTypes.Released);

        var editRequest = Request(12m);
        editRequest.WorkOrderNo = release.Data.WorkOrderNo;
        editRequest.RowVersion = release.Data.RowVersion;
        var edit = await sut.SaveDraftAsync(editRequest);
        Assert.False(edit.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, edit.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvTrxBatches.AnyAsync());
        Assert.False(await db.IvTrxHistories.AnyAsync());
    }

    [Fact]
    public async Task Draft_cancel_requires_reason_and_blocks_release()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var noReason = await sut.CancelDraftAsync(save.Data!.WorkOrderNo, save.Data.RowVersion, " ");
        Assert.False(noReason.Succeeded);
        Assert.Contains("CancellationReason", noReason.ValidationErrors.Keys);

        var cancel = await sut.CancelDraftAsync(save.Data.WorkOrderNo, save.Data.RowVersion, "Demand withdrawn");
        Assert.True(cancel.Succeeded, cancel.Message);
        Assert.Equal(ProductionWorkOrderStatuses.Cancelled, cancel.Data!.Status);
        Assert.Equal("Demand withdrawn", cancel.Data.CancellationReason);

        var release = await sut.ReleaseAsync(cancel.Data.WorkOrderNo, cancel.Data.RowVersion);
        Assert.False(release.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, release.ErrorCode);
    }

    [Fact]
    public async Task Stale_row_version_is_rejected()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);
        var staleToken = save.Data!.RowVersion.ToArray();

        var firstEdit = Request(12m);
        firstEdit.WorkOrderNo = save.Data.WorkOrderNo;
        firstEdit.RowVersion = staleToken;
        var first = await sut.SaveDraftAsync(firstEdit);
        Assert.True(first.Succeeded, first.Message);

        var staleEdit = Request(14m);
        staleEdit.WorkOrderNo = save.Data.WorkOrderNo;
        staleEdit.RowVersion = staleToken;
        var second = await sut.SaveDraftAsync(staleEdit);

        Assert.False(second.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, second.ErrorCode);
    }

    [Fact]
    public async Task Release_detects_snapshot_tampering_even_when_header_token_is_current()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync();
            material.RequiredQty = 999m;
            material.RowVersion = Guid.NewGuid().ToByteArray();
            await db.SaveChangesAsync();
        }

        var release = await sut.ReleaseAsync(save.Data!.WorkOrderNo, save.Data.RowVersion);
        Assert.False(release.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, release.ErrorCode);
        Assert.Contains("integrity hash", release.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Release_detects_case_only_snapshot_text_tampering()
    {
        var sut = CreateSut();
        var save = await sut.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.ProductionWorkOrders.SingleAsync();
            order.ProductDescription = order.ProductDescription!.ToUpperInvariant();
            await db.SaveChangesAsync();
        }

        var release = await sut.ReleaseAsync(save.Data!.WorkOrderNo, save.Data.RowVersion);
        Assert.False(release.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, release.ErrorCode);
        Assert.Contains("integrity hash", release.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Branch_scope_cannot_read_another_branch_work_order()
    {
        var hq = CreateSut(branch: "HQ");
        var save = await hq.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var other = CreateSut(branch: "B02");
        var result = await other.GetAsync(save.Data!.WorkOrderNo);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Missing_action_permission_is_denied_server_side()
    {
        var sut = CreateSut(deniedPermission: PermissionCodes.Add);
        var result = await sut.SaveDraftAsync(Request(10m));

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Save_normalizes_quantity_and_rejects_unimplemented_source_types()
    {
        var sut = CreateSut();
        var rounded = await sut.SaveDraftAsync(Request(1.23456m));
        Assert.True(rounded.Succeeded, rounded.Message);
        Assert.Equal(1.2346m, rounded.Data!.PlannedQty);

        var unsupported = Request(1m);
        unsupported.SourceType = ProductionSourceTypes.SalesOrder;
        var rejected = await sut.SaveDraftAsync(unsupported);

        Assert.False(rejected.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, rejected.ErrorCode);
        Assert.Contains("SourceType", rejected.ValidationErrors.Keys);
    }

    [Fact]
    public async Task Release_requires_approve_permission_server_side()
    {
        var creator = CreateSut();
        var save = await creator.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var withoutApproval = CreateSut(deniedPermission: PermissionCodes.Approve);
        var release = await withoutApproval.ReleaseAsync(save.Data!.WorkOrderNo, save.Data.RowVersion);

        Assert.False(release.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, release.ErrorCode);
    }

    [Fact]
    public async Task Create_header_update_and_release_use_the_version2_snapshot()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PrBomHdrs.Include(x => x.Lines).SingleAsync(x => x.ProdCode == "FG001");
            var operation = new PrBomOperation
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                CentralSequence = 10,
                OperationCode = "FIN",
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Manual,
                StandardDurationMinutes = 30m,
                IsFinalOperation = true,
                OutputBaseQty = 5m,
                OutputUom = "PCS",
                RowVersion = [1]
            };
            var step = new PrBomRouteStep
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                StageSequence = 10,
                OutputItemCode = "FG001",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                StandardOutputQty = 1m,
                OutputUom = "PCS",
                RowVersion = [1],
                Operations = { operation }
            };
            header.RouteSteps.Add(step);
            header.Operations.Add(operation);
            header.Lines.Single().Operation = operation;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var created = await sut.CreateDraftAsync(Request(10m));
        Assert.True(created.Succeeded, created.Message);
        var detail = created.Data!;
        Assert.Equal(ProductionSnapshotFormatVersions.Current, detail.SnapshotFormatVersion);
        Assert.False(detail.IsLegacySnapshot);
        var route = Assert.Single(detail.RouteSteps);
        Assert.Equal("FG001", route.OutputItemCode);
        Assert.NotNull(route.PlannedStartDateTime);
        var firstHash = detail.SnapshotHash;
        Assert.Equal(1, detail.SnapshotRevision);

        var updated = await sut.UpdateDraftHeaderAsync(new ProductionWorkOrderHeaderUpdate
        {
            WorkOrderNo = detail.WorkOrderNo,
            PlannedQty = detail.PlannedQty,
            SchedulingDirection = detail.SchedulingDirection,
            ScheduleAnchorDateTime = detail.ScheduleAnchorDateTime,
            Remark = "Remark only",
            RowVersion = detail.RowVersion
        });
        Assert.True(updated.Succeeded, updated.Message);
        Assert.Equal(2, updated.Data!.SnapshotRevision);
        Assert.NotEqual(firstHash, updated.Data.SnapshotHash);
        Assert.Equal(detail.PlannedQty, updated.Data.PlannedQty);

        var released = await sut.ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
        {
            WorkOrderNo = updated.Data.WorkOrderNo,
            RowVersion = updated.Data.RowVersion,
            SnapshotRevision = updated.Data.SnapshotRevision,
            SnapshotHash = updated.Data.SnapshotHash,
            SourceProductDefinitionRevisionId = updated.Data.SourceProductDefinitionRevisionId
        });
        Assert.True(released.Succeeded, released.Message);
        Assert.Equal(ProductionWorkOrderStatuses.Released, released.Data!.Status);

        var refresh = await sut.PreviewRefreshFromDefinitionAsync(updated.Data.WorkOrderNo);
        Assert.False(refresh.Succeeded);
    }

    [Fact]
    public async Task Release_stays_blocked_when_the_feature_is_disabled()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PrBomHdrs.Include(x => x.Lines).SingleAsync(x => x.ProdCode == "FG001");
            var operation = new PrBomOperation
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                CentralSequence = 10,
                OperationCode = "FIN",
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Manual,
                StandardDurationMinutes = 30m,
                IsFinalOperation = true,
                OutputBaseQty = 5m,
                OutputUom = "PCS",
                RowVersion = [1]
            };
            var step = new PrBomRouteStep
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                StageSequence = 10,
                OutputItemCode = "FG001",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                StandardOutputQty = 1m,
                OutputUom = "PCS",
                RowVersion = [1],
                Operations = { operation }
            };
            header.RouteSteps.Add(step);
            header.Operations.Add(operation);
            header.Lines.Single().Operation = operation;
            await db.SaveChangesAsync();
        }

        var sut = CreateSut(releaseEnabled: false);
        var created = await sut.CreateDraftAsync(Request(10m));
        Assert.True(created.Succeeded, created.Message);

        var released = await sut.ReleaseCurrentAsync(new ProductionWorkOrderReleaseRequest
        {
            WorkOrderNo = created.Data!.WorkOrderNo,
            RowVersion = created.Data.RowVersion,
            SnapshotRevision = created.Data.SnapshotRevision,
            SnapshotHash = created.Data.SnapshotHash,
            SourceProductDefinitionRevisionId = created.Data.SourceProductDefinitionRevisionId
        });

        Assert.False(released.Succeeded);
        Assert.Contains(ProductionReadinessErrorCodes.ReleaseDisabled, released.Message, StringComparison.Ordinal);
        var reload = await sut.GetAsync(created.Data.WorkOrderNo);
        Assert.Equal(ProductionWorkOrderStatuses.Draft, reload.Data!.Status);
    }

    [Fact]
    public async Task Work_order_snapshot_blocks_product_definition_delete()
    {
        var workOrders = CreateSut();
        var save = await workOrders.SaveDraftAsync(Request(10m));
        Assert.True(save.Succeeded, save.Message);

        var productDefinitions = CreateProductDefinitionSut();

        var check = await productDefinitions.CanDeleteAsync([new PrProductDefinitionKey { ProdCode = "FG001", DefinitionCode = PrProductDefinitionCodes.Standard }]);
        Assert.True(check.Succeeded, check.Message);
        Assert.False(check.Data!.CanDelete);
        Assert.Contains(check.Data.References, x => x.ReferenceType == "Production Work Order" && x.Count == 1);

        var delete = await productDefinitions.DeleteAsync([new PrProductDefinitionKey { ProdCode = "FG001", DefinitionCode = PrProductDefinitionCodes.Standard }]);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delete.ErrorCode);
    }

    private PrProductDefService CreateProductDefinitionSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PlanningProductDef,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new PrProductDefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new IvUomConversionService(_factory));
    }

    private ProductionWorkOrderService CreateSut(
        string branch = "HQ",
        string? deniedPermission = null,
        bool releaseEnabled = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PlanningWorkOrder,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string permission, CancellationToken _) =>
                !string.Equals(permission, deniedPermission, StringComparison.OrdinalIgnoreCase));

        var productAccess = new Mock<IAccessRightService>();
        productAccess.Setup(x => x.CanAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext(branch: branch, location: "SITE");
        var explosion = new BomExplosionService(_factory, tenant, productAccess.Object);
        var uom = new IvUomConversionService(_factory);
        var quantities = new WorkOrderQuantityCalculator(uom);
        return new ProductionWorkOrderService(
            _factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            explosion,
            new FakeProductionCalendarScheduleDataLoader(),
            new WorkOrderSnapshotBuilder(_factory, new ProductDefinitionSnapshotLoader(_factory), quantities),
            quantities,
            new WorkOrderScheduleCalculator(new AlwaysOpenWorkOrderCalendarProvider()),
            new WorkOrderReadinessValidator(),
            Microsoft.Extensions.Options.Options.Create(new ProductionWorkOrderOptions { ReleaseEnabled = releaseEnabled }));
    }

    private async Task SeedManualCurrentRouteAsync(AppDbContext? db = null, PrBomHdr? header = null)
    {
        var ownsContext = db is null;
        db ??= await _factory.CreateDbContextAsync();
        try
        {
            header ??= await db.PrBomHdrs
                .Include(x => x.Lines)
                .Include(x => x.RouteSteps)
                .Include(x => x.Operations)
                .SingleAsync(x => x.ProdCode == "FG001");
            if (header.RouteSteps.Count > 0)
            {
                return;
            }

            var operation = new PrBomOperation
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                OutputItemCode = "FG001",
                CentralSequence = 10,
                OperationCode = "FIN",
                ProcessSequence = 10,
                ProcessType = PrProcessTypes.Manual,
                StandardDurationMinutes = 30m,
                IsFinalOperation = true,
                OutputBaseQty = 5m,
                OutputUom = "PCS",
                RowVersion = [1]
            };
            header.RouteSteps.Add(new PrBomRouteStep
            {
                CompanyCode = "DEMO",
                WorkCentreCode = "WC01",
                StageSequence = 10,
                OutputItemCode = "FG001",
                OutputType = PrRouteOutputTypes.FinishedGoods,
                StandardOutputQty = 1m,
                OutputUom = "PCS",
                RowVersion = [1],
                Operations = { operation }
            });
            header.Operations.Add(operation);
            foreach (var line in header.Lines.Where(l => l.Operation is null && l.OperationId is null))
            {
                line.Operation = operation;
            }

            await db.SaveChangesAsync();
        }
        finally
        {
            if (ownsContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    private static ProductionWorkOrderDraftRequest Request(decimal quantity) => new()
    {
        ProductCode = "FG001",
        PlannedQty = quantity,
        DefinitionCode = PrProductDefinitionCodes.Standard,
        PlannedStartDate = new DateTime(2026, 10, 1),
        PlannedCompletionDate = new DateTime(2026, 10, 3),
        SchedulingDirection = ProductionSchedulingDirections.Forward,
        SourceType = ProductionSourceTypes.Manual,
        Remark = "Phase 1 test"
    };

    private static IvStockMaster Stock(
        string company,
        string code,
        string description,
        string uom,
        string mfgType) => new()
    {
        CompanyCode = company,
        ICode = code,
        IDesc = description,
        StdUom = uom,
        DefWarehouse = "WH01",
        MfgType = mfgType,
        IsActive = true,
        RowVersion = [1]
    };
}

