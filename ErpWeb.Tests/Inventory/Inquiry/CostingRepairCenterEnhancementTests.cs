using ErpWeb.Core.Costing;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.UI.Inventory.Inquiry;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Inquiry;

public sealed partial class CostingDiagnosticCenterTests
{
    [Fact]
    public async Task Item_trace_returns_the_stock_posting_id()
    {
        var postingId = await AddPostingAsync("MR", "101", isSealed: true);
        await AddFactAsync(postingId, direction: 1, qty: 4m, value: 8m);
        var page = await new CostingTraceService(_factory, new Tenant(), new Access(viewCost: true))
            .GetItemTimelineAsync(new CostingTraceQuery("ITEM-1"));
        var line = Assert.Single(page.Lines);
        Assert.Equal(postingId, line.StockPostingId);
    }

    [Fact]
    public async Task Document_number_filter_returns_only_the_requested_source_document()
    {
        var first = await AddPostingAsync("MR", "201", isSealed: true, sourceDocumentNo: "GR-201", sequence: 201);
        var second = await AddPostingAsync("MR", "202", isSealed: true, sourceDocumentNo: "GR-202", sequence: 202);
        _ = first;
        _ = second;
        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync(new CostingHealthQuery(SourceDocumentNo: "GR-201"));
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.HistoryMissingValuation && x.SourceDocumentNo == "GR-201");
        Assert.DoesNotContain(page.Findings, x => x.SourceDocumentNo == "GR-202");
    }

    [Fact]
    public async Task Posting_finding_can_be_planned_without_typing_a_posting_id()
    {
        var postingId = await AddPostingAsync("MR", "203", isSealed: true);
        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync();
        var finding = Assert.Single(page.Findings, x => x.Code == CostingFindingCodes.HistoryMissingValuation);
        Assert.Equal(CostingRepairTargetKind.Posting, finding.RepairTargetKind);
        Assert.Equal(postingId, finding.StockPostingId);
        var plan = await Planner().PlanAsync(new CostingRepairTarget(CostingRepairTargetKind.Posting, finding.StockPostingId, finding.ItemCode, null, finding.Code));
        Assert.True(plan.CanRepair);
    }

    [Fact]
    public async Task Value_mismatch_is_a_cost_state_target()
    {
        var postingId = await AddPostingAsync("MR", "204", isSealed: true);
        await AddFactAsync(postingId, direction: 1, qty: 10m, value: 100m);
        await AddStateAsync(10m, 850m, 85m);
        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync(new CostingHealthQuery("ITEM-1"));
        var finding = Assert.Single(page.Findings, x => x.Code == CostingFindingCodes.CostStateValueMismatch);
        Assert.Equal(CostingRepairTargetKind.CostState, finding.RepairTargetKind);
        Assert.Equal(CostingRepairActions.RebuildCostState, finding.RepairAction);
        Assert.Equal(StockCostMethods.MovingAverage, finding.CostMethod);
    }

    [Fact]
    public async Task Missing_material_state_is_cd_040_and_a_zero_pool_is_not()
    {
        var material = await AddPostingAsync("MR", "205", isSealed: true);
        await AddFactAsync(material, direction: 1, qty: 10m, value: 100m);
        var zeroReceipt = await AddPostingAsync("MR", "206", isSealed: true, sequence: 206);
        var zeroIssue = await AddPostingAsync("MI", "207", isSealed: true, sequence: 207);
        var receiptFact = await AddFactAsync(zeroReceipt, direction: 1, qty: 5m, value: 20m, item: "ITEM-ZERO");
        await AddFactAsync(zeroIssue, direction: -1, qty: 5m, value: 20m, reverses: receiptFact, item: "ITEM-ZERO");

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true)).SearchAsync();
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.CostStateMissing && x.ItemCode == "ITEM-1");
        Assert.DoesNotContain(page.Findings, x => x.Code == CostingFindingCodes.CostStateMissing && x.ItemCode == "ITEM-ZERO");
    }

    [Fact]
    public async Task Cost_state_rebuild_is_blocked_by_an_unsealed_posting_and_by_cross_epoch_evidence()
    {
        var sealedId = await AddPostingAsync("MR", "208", isSealed: true);
        await AddFactAsync(sealedId, direction: 1, qty: 10m, value: 100m);
        await AddPostingAsync("MI", "209", isSealed: false, sequence: 209);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxHistories.Add(History(209, IvTrxTypes.MiscellaneousIssue));
            var history = db.IvTrxHistories.Local.Single();
            history.StockPostingId = await db.StockPostings.Where(x => x.SourceDocumentNo == "209").Select(x => x.Id).SingleAsync();
            history.ICode = "ITEM-1";
            await db.SaveChangesAsync();
        }
        var blocked = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, CostingFindingCodes.CostStateMissing);
        Assert.False(blocked.CanRepair);
        Assert.Contains("unsealed", blocked.BlockingReason, StringComparison.OrdinalIgnoreCase);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var open = await db.StockPostings.SingleAsync(x => x.SourceDocumentNo == "209");
            open.SealedAtUtc = DateTime.UtcNow;
            var retired = new StockLedgerEpoch
            {
                CompanyCode = "DEMO", BranchCode = "HQ", EffectiveFrom = new DateTime(2026, 1, 1),
                Version = 2, Status = StockLedgerEpochStatuses.Retired,
                MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('B', 64),
                ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "tester"
            };
            db.StockLedgerEpochs.Add(retired);
            await db.SaveChangesAsync();
            await AddFactAsync(sealedId, direction: 1, qty: 1m, value: 1m, epochId: retired.Id, postingLineNo: 2);
        }
        var cross = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        Assert.False(cross.CanRepair);
        Assert.Contains("Cross-epoch", cross.BlockingReason);
    }

    [Fact]
    public async Task Moving_average_rebuild_restores_quantity_value_and_average()
    {
        var postingId = await AddPostingAsync("MR", "210", isSealed: true, sequence: 210);
        await AddFactAsync(postingId, direction: 1, qty: 10m, value: 80m);
        await AddStateAsync(10m, 85m, 8.5m);
        var plan = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, CostingFindingCodes.CostStateValueMismatch);
        Assert.True(plan.CanRepair);
        var executed = await StateService(new RecordingLock()).ExecuteAsync("ITEM-1", StockCostMethods.MovingAverage, plan.PreviewHash, "state was drifted");
        Assert.True(executed.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        var state = await db.StockCostStates.SingleAsync(x => x.ItemCode == "ITEM-1");
        Assert.Equal(10m, state.OnHandBaseQty);
        Assert.Equal(80m, state.InventoryValue);
        Assert.Equal(8m, state.AverageUnitCost);
        Assert.Equal(8m, state.CurrentUnitCost);
        var repair = await db.CostingRepairCases.SingleAsync();
        Assert.Equal("REBUILD_COST_STATE", repair.Strategy);
        Assert.Null(repair.RootStockPostingId);
        Assert.Equal("STEP_COMPLETED", Assert.Single(db.CostingRepairAuditEvents).EventType);
        var health = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync(new CostingHealthQuery("ITEM-1"));
        Assert.DoesNotContain(health.Findings, x => x.Code == CostingFindingCodes.CostStateValueMismatch);
    }

    [Fact]
    public async Task State_preview_is_stale_after_a_new_posting_and_while_the_lock_is_held()
    {
        var postingId = await AddPostingAsync("MR", "211", isSealed: true, sequence: 211);
        await AddFactAsync(postingId, direction: 1, qty: 10m, value: 80m);
        await AddStateAsync(10m, 90m, 9m);
        var plan = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        var later = await AddPostingAsync("MR", "212", isSealed: true, sequence: 212);
        await AddFactAsync(later, direction: 1, qty: 1m, value: 8m);
        var stale = await StateService(new RecordingLock()).ExecuteAsync("ITEM-1", StockCostMethods.MovingAverage, plan.PreviewHash, "too late");
        Assert.Equal("REPLAN_REQUIRED", stale.Status);
        await using (var db = await _factory.CreateDbContextAsync())
            Assert.Equal(90m, (await db.StockCostStates.SingleAsync(x => x.ItemCode == "ITEM-1")).InventoryValue);

        var fresh = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        var racing = new RecordingLock
        {
            During = async (db, ct) =>
            {
                var extra = new StockPosting
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = _epochId,
                    PostingSequence = 213, RequestId = Guid.NewGuid(), CommandType = "MR",
                    RequestFingerprint = new string('C', 64), SourceModule = "INVENTORY",
                    SourceDocumentType = "MR", SourceDocumentId = "213", SourceDocumentNo = "213",
                    DocumentRevision = 1, PostingRole = "PRIMARY", SourceSnapshotJson = "{}",
                    SourceSnapshotHash = new string('D', 64), EffectiveAt = new DateTime(2026, 10, 3),
                    BusinessDate = new DateTime(2026, 10, 3), PeriodKey = "2026-10",
                    PostedAtUtc = DateTime.UtcNow, PostedBy = "tester", SealedAtUtc = DateTime.UtcNow
                };
                db.StockPostings.Add(extra);
                await db.SaveChangesAsync(ct);
                db.StockValuationFacts.Add(new StockValuationFact
                {
                    CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = _epochId,
                    StockPostingId = extra.Id, PostingLineNo = 1, SourceLineId = "1",
                    SourceDocumentType = "MR", SourceDocumentId = "213", SourceDocumentNo = "213",
                    EffectiveAt = extra.EffectiveAt, BusinessDate = extra.BusinessDate, PeriodKey = "2026-10",
                    ItemCode = "ITEM-1", BaseUom = "EA", MovementCode = "MR", Direction = 1, BaseQty = 1m,
                    CostMethod = StockCostMethods.MovingAverage, UnitCost = 8m, CostAmount = 8m, BaseCostAmount = 8m,
                    ValuationSource = StockValuationSources.MovingAverage, ValuationStatus = StockValuationStatuses.Valued,
                    CreatedAtUtc = DateTime.UtcNow, CreatedBy = "tester"
                });
                await db.SaveChangesAsync(ct);
            }
        };
        var raced = await StateService(racing).ExecuteAsync("ITEM-1", StockCostMethods.MovingAverage, fresh.PreviewHash, "during lock");
        Assert.Equal(1, racing.Calls);
        Assert.Equal("REPLAN_REQUIRED", raced.Status);
    }

    [Fact]
    public async Task Fifo_rebuild_follows_layer_proof_and_keeps_pool_unit_cost()
    {
        var postingId = await AddPostingAsync("MR", "214", isSealed: true, sequence: 214);
        var factId = await AddFactAsync(postingId, direction: 1, qty: 10m, value: 80m, costMethod: StockCostMethods.Fifo);
        await AddStateAsync(10m, 90m, 9m, costMethod: StockCostMethods.Fifo);
        var inconsistent = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.Fifo, null);
        Assert.False(inconsistent.CanRepair);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.StockFifoLayers.Add(new StockFifoLayer
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ItemCode = "ITEM-1", BaseUom = "EA",
                OriginValuationFactId = factId, OriginStockPostingId = postingId,
                ReceiptEffectiveAt = new DateTime(2026, 10, 2), OriginalQty = 10m, RemainingQty = 10m,
                OriginalValue = 80m, RemainingValue = 80m, CurrentUnitCost = 99m,
                SourceDocumentType = "MR", SourceDocumentNo = "214", Status = StockFifoLayerStatuses.Open,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }
        var plan = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.Fifo, null);
        Assert.True(plan.CanRepair);
        var executed = await StateService(new RecordingLock()).ExecuteAsync("ITEM-1", StockCostMethods.Fifo, plan.PreviewHash, "fifo projection");
        Assert.True(executed.Succeeded);
        await using var check = await _factory.CreateDbContextAsync();
        var state = await check.StockCostStates.SingleAsync();
        Assert.Equal(8m, state.CurrentUnitCost);
        Assert.Equal(8m, state.AverageUnitCost);
    }

    [Fact]
    public async Task Standard_rebuild_is_blocked_when_the_revision_is_ambiguous()
    {
        var postingId = await AddPostingAsync("MR", "215", isSealed: true, sequence: 215);
        await AddFactAsync(postingId, direction: 1, qty: 10m, value: 80m, costMethod: StockCostMethods.Standard);
        await AddStateAsync(10m, 90m, 9m, costMethod: StockCostMethods.Standard);
        var blocked = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.Standard, null);
        Assert.False(blocked.CanRepair);
        Assert.Contains("ambiguous", blocked.BlockingReason, StringComparison.OrdinalIgnoreCase);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ItemStandardCostRevisions.Add(new ItemStandardCostRevision
            {
                CompanyCode = "DEMO", BranchCode = "HQ", ItemCode = "ITEM-1",
                EffectiveFrom = new DateTime(2026, 1, 1), TotalStandardCost = 99m,
                Status = ItemStandardCostRevisionStatuses.Approved, Revision = 1,
                ApprovedBy = "tester", ApprovedAtUtc = DateTime.UtcNow, CreatedAtUtc = DateTime.UtcNow, CreatedBy = "tester",
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }
        var plan = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.Standard, null);
        Assert.True(plan.CanRepair);
    }

    [Fact]
    public async Task Repair_cost_and_source_rollback_are_both_required()
    {
        var postingId = await AddPostingAsync("MR", "216", isSealed: true, sequence: 216);
        var adapter = new RecordingAdapter();
        var planner = new CostingRepairPlanner(_factory, new Tenant(), new Access(viewCost: true, repairCost: false),
            new CostingRepairOwnershipResolver(_factory, new Tenant()), [adapter]);
        var denied = await new CostingRepairService(_factory, new Tenant(), new Access(viewCost: true, repairCost: false), planner, [adapter])
            .ExecuteReverseAsync(postingId, "hash", "reason");
        Assert.Equal("DENIED", denied.Status);
        Assert.Equal(0, adapter.Calls);

        var allowedPlanner = new CostingRepairPlanner(_factory, new Tenant(), new Access(viewCost: true),
            new CostingRepairOwnershipResolver(_factory, new Tenant()), [adapter]);
        var plan = await allowedPlanner.PlanAsync(postingId);
        var noRollback = await new CostingRepairService(_factory, new Tenant(), new Access(viewCost: true, rollback: false), allowedPlanner, [adapter])
            .ExecuteReverseAsync(postingId, plan.PreviewHash, "reason");
        Assert.Equal("DENIED", noRollback.Status);
        Assert.Contains("ROLLBACK", noRollback.Message);
    }

    [Fact]
    public async Task Successful_posting_rollback_writes_the_repair_audit()
    {
        var postingId = await AddPostingAsync("MR", "217", isSealed: true, sequence: 217);
        var adapter = new SucceedingAdapter();
        var planner = new CostingRepairPlanner(_factory, new Tenant(), new Access(viewCost: true),
            new CostingRepairOwnershipResolver(_factory, new Tenant()), [adapter]);
        var plan = await planner.PlanAsync(postingId);
        var executed = await new CostingRepairService(_factory, new Tenant(), new Access(viewCost: true), planner, [adapter])
            .ExecuteReverseAsync(postingId, plan.PreviewHash, "entered cost was wrong");
        Assert.True(executed.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("GUIDED_REVERSAL", (await db.CostingRepairCases.SingleAsync()).Strategy);
        Assert.Contains(db.CostingRepairAuditEvents, x => x.EventType == "STEP_COMPLETED");
    }

    [Fact]
    public async Task Closed_period_and_unsafe_postings_stay_blocked()
    {
        var postingId = await AddPostingAsync("MR", "218", isSealed: true, sequence: 218);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvPeriodCloseHdrs.Add(new ErpWeb.Model.Entities.Inventory.IvPeriodCloseHdr
            {
                CompanyCode = "DEMO", BranchCode = "HQ", PeriodFrom = new DateTime(2026, 10, 1),
                PeriodTo = new DateTime(2026, 10, 31), Status = IvPeriodCloseStatuses.Closed,
                ClosedOn = DateTime.UtcNow, ClosedBy = "tester"
            });
            await db.SaveChangesAsync();
        }
        var closed = await Planner().PlanAsync(postingId);
        Assert.False(closed.CanRepair);
        Assert.Contains("closed period", closed.BlockingReason, StringComparison.OrdinalIgnoreCase);

        var unsealed = await AddPostingAsync("MR", "219", isSealed: false, sequence: 219);
        var reversal = await AddPostingAsync("MR", "220", isSealed: true, sequence: 220, postingRole: "REVERSAL");
        var primary = await AddPostingAsync("MR", "221", isSealed: true, sequence: 221);
        await AddPostingAsync("MR", "222", isSealed: true, sequence: 222, reversesPostingId: primary);
        var planner = Planner();
        Assert.Contains("Unsealed", (await planner.PlanAsync(unsealed)).BlockingReason);
        Assert.Contains("PRIMARY", (await planner.PlanAsync(reversal)).BlockingReason);
        Assert.Contains("sealed reversal", (await planner.PlanAsync(primary)).BlockingReason);
    }

    [Fact]
    public async Task Inventory_batch_number_is_not_the_batch_primary_key()
    {
        int batchId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = Batch(80021, IvTrxTypes.MiscellaneousReceipt, "GR80021");
            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
        }
        Assert.NotEqual(80021, batchId);
        var postingId = await AddPostingAsync("MR", batchId.ToString(), isSealed: true, sourceDocumentNo: "80021", sequence: 80021);
        var owner = (await new CostingRepairOwnershipResolver(_factory, new Tenant()).ResolveAsync(
            new CostingRepairNode(postingId, "MR", batchId.ToString(), "80021"))).Owner;
        Assert.Equal(batchId.ToString(), owner!.PhysicalSourceDocumentId);
        Assert.Equal("80021", owner.PhysicalSourceDocumentNo);
        var posting = new CapturingPostingService();
        var adapter = new InventoryCostingRepairAdapter(_factory, new Tenant(), posting);
        var result = await adapter.ReverseAsync(
            new CostingRepairNode(postingId, "MR", owner.PhysicalSourceDocumentId, owner.PhysicalSourceDocumentNo),
            owner, new CostingRepairExecutionContext(Guid.NewGuid(), "step", "POSTED", "correct the batch"), default);
        Assert.True(result.Succeeded);
        Assert.Equal(80021, Assert.Single(posting.RolledBack));
    }

    [Fact]
    public async Task Production_owners_resolve_without_the_posting_link_foreign_key()
    {
        int batchId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.FinishedGoodReceiptWrite = true;
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            var batch = Batch(80022, IvTrxTypes.FinishedGoods, "FG80022");
            db.IvTrxBatches.Add(batch);
            await db.SaveChangesAsync();
            batchId = batch.Id;
            db.ProductionFinishedGoodReceiptRows.Add(new ProductionFinishedGoodReceipt
            {
                BatchId = batchId, CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderId = 1, DocumentRevision = 1, RowVersion = [1]
            });
            db.ProductionPostingLinks.Add(new ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", CommandType = "FG_POST", PostingRequestId = "fg-80022",
                WorkOrderId = 1, ProductionDocumentType = ProductionDocumentTypes.FinishedGoodReceipt,
                ProductionDocumentNo = "80022", InventoryBatchNo = 80022, Status = "Succeeded",
                CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
            });
            var output = new ProductionOutput
            {
                CompanyCode = "DEMO", BranchCode = "HQ", DocumentNo = "DP-1", Status = "POSTED",
                WorkOrderId = 1, RouteStepId = 1, WorkOrderOperationId = 1, ProductionDate = new DateTime(2026, 10, 2),
                OutputUom = "EA", OutputItemCode = "ITEM-1", OutputLotNo = "L1", SnapshotRevision = 1,
                SnapshotHash = new string('E', 64), PostingRequestId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                CreatedDate = DateTime.UtcNow, CreatedBy = "tester", RowVersion = [1]
            };
            db.ProductionOutputs.Add(output);
            db.ProductionPostingLinks.Add(new ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", CommandType = "OUTPUT_POST",
                PostingRequestId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WorkOrderId = 1,
                ProductionDocumentType = ProductionDocumentTypes.ProductionOutput, ProductionDocumentNo = "DP-1",
                Status = "Succeeded", CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
            });
            db.ProductionPostingLinks.Add(new ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", CommandType = "MATERIAL_ISSUE_POST",
                PostingRequestId = "ip-80023", WorkOrderId = 1, ProductionDocumentType = ProductionDocumentTypes.MaterialIssue,
                ProductionDocumentNo = "80023", InventoryBatchNo = 80023, Status = "Succeeded",
                CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
            });
            await db.SaveChangesAsync();
            var fg = await AddPostingAsync(ProductionDocumentTypes.FinishedGoodReceipt, batchId.ToString(), true, sourceDocumentNo: "80022", sequence: 80022);
            var outputId = output.Uid;
            var daily = await AddPostingAsync(ProductionDocumentTypes.ProductionOutput, outputId.ToString(), true, sourceDocumentNo: "DP-1", sequence: 80024);
            var issue = await AddPostingAsync(IvTrxTypes.IssueToProduction, "1", true, sourceDocumentNo: "80023", sequence: 80023);
            var resolver = new CostingRepairOwnershipResolver(_factory, new Tenant());
            var fgOwner = (await resolver.ResolveAsync(new CostingRepairNode(fg, "FG_RECEIPT", batchId.ToString(), "80022"))).Owner!;
            var dailyOwner = (await resolver.ResolveAsync(new CostingRepairNode(daily, "PRODUCTION_OUTPUT", outputId.ToString(), "DP-1"))).Owner!;
            var issueOwner = (await resolver.ResolveAsync(new CostingRepairNode(issue, "IP", "1", "80023"))).Owner!;
            Assert.Equal(CostingRepairOwnerTypes.ProductionFinishedGood, fgOwner.OwnerType);
            Assert.Equal(CostingRepairOwnerTypes.ProductionOutput, dailyOwner.OwnerType);
            Assert.Equal(CostingRepairOwnerTypes.ProductionMaterialIssue, issueOwner.OwnerType);
            Assert.Contains($"/{batchId}/view", CostingSourceNavigationResolver.Resolve(new CostingRepairOwnershipResult(true, null, fgOwner))!.Route);
            Assert.Contains(outputId.ToString(), CostingSourceNavigationResolver.Resolve(new CostingRepairOwnershipResult(true, null, dailyOwner))!.Route);
            var planner = new CostingRepairPlanner(_factory, new Tenant(), new Access(true), resolver,
                [new ProductionFinishedGoodReceiptRepairAdapter(), new ProductionOutputRepairAdapter(), new ProductionMaterialIssueRepairAdapter()]);
            Assert.False((await planner.PlanAsync(fg)).CanRepair);
            Assert.False((await planner.PlanAsync(daily)).CanRepair);
            Assert.False((await planner.PlanAsync(issue)).CanRepair);
        }
    }

    [Fact]
    public void New_daily_production_command_carries_the_posting_link()
    {
        var output = new ProductionOutput
        {
            Uid = 5, DocumentNo = "DP-9", SnapshotRevision = 1,
            PostingRequestId = "cccccccccccccccccccccccccccccccc"
        };
        var command = ProductionOutputService.BuildOutputPostingCommand(output, false, new DateTime(2026, 10, 2), null, null, 77);
        Assert.Equal(77, command.ProductionPostingLinkId);
        Assert.Equal("5", command.SourceDocumentId);
        Assert.Equal("DP-9", command.SourceDocumentNo);
    }

    [Fact]
    public async Task Reversal_pointer_and_snapshot_gate_follow_the_ledger()
    {
        var receipt = await AddPostingAsync("MR", "230", isSealed: true, sequence: 100);
        var issue = await AddPostingAsync("MI", "231", isSealed: true, sequence: 101);
        var reversal = await AddPostingAsync("MI", "232", isSealed: true, sequence: 102, postingRole: "REVERSAL", reversesPostingId: issue);
        await AddFactAsync(receipt, 1, 100m, 800m);
        var issueFact = await AddFactAsync(issue, -1, 20m, 160m);
        var reversalFact = await AddFactAsync(reversal, 1, 20m, 160m, reverses: issueFact);
        await AddStateAsync(100m, 850m, 8.5m);
        var plan = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        Assert.True(plan.CanRepair);
        Assert.True((await StateService(new RecordingLock()).ExecuteAsync("ITEM-1", StockCostMethods.MovingAverage, plan.PreviewHash, "restore pointer")).Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync(x => x.ItemCode == "ITEM-1");
            Assert.Equal(100m, state.OnHandBaseQty);
            Assert.Equal(800m, state.InventoryValue);
            Assert.Equal(reversalFact, state.LastValuationFactId);
            Assert.Equal(102L, state.LastPostingSequence);
            db.StockCostStates.Remove(state);
            db.StockValuationPeriodSnapshotHdrs.Add(new StockValuationPeriodSnapshotHdr
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = _epochId, PeriodKey = "2026-10",
                Revision = 1, PostingSequenceWatermark = 102, SourceDataHash = new string('F', 64),
                ValuationStatus = "SEALED", CreatedAtUtc = DateTime.UtcNow, CreatedBy = "tester",
                Lines =
                [
                    new StockValuationPeriodSnapshotLine
                    {
                        ItemCode = "ITEM-1", BaseUom = "EA", CostMethod = StockCostMethods.MovingAverage,
                        ClosingQty = 1m, ClosingValue = 1m
                    }
                ]
            });
            await db.SaveChangesAsync();
        }
        var blocked = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        Assert.False(blocked.CanRepair);
        Assert.Contains("snapshot", blocked.BlockingReason, StringComparison.OrdinalIgnoreCase);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.StockValuationPeriodSnapshotHdrs.Add(new StockValuationPeriodSnapshotHdr
            {
                CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = _epochId, PeriodKey = "2026-10",
                Revision = 2, PostingSequenceWatermark = 102, SourceDataHash = new string('A', 64),
                ValuationStatus = "SEALED", CreatedAtUtc = DateTime.UtcNow, CreatedBy = "tester",
                Lines =
                [
                    new StockValuationPeriodSnapshotLine
                    {
                        ItemCode = "ITEM-1", BaseUom = "EA", CostMethod = StockCostMethods.MovingAverage,
                        ClosingQty = 100m, ClosingValue = 800m
                    }
                ]
            });
            await db.SaveChangesAsync();
        }
        await AddStateAsync(100m, 1m, 0.01m);
        var allowed = await StatePlanner().PlanAsync("ITEM-1", StockCostMethods.MovingAverage, null);
        Assert.True(allowed.CanRepair);
        Assert.True((await StateService(new RecordingLock()).ExecuteAsync("ITEM-1", StockCostMethods.MovingAverage, allowed.PreviewHash, "snapshot matches")).Succeeded);
        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(800m, (await check.StockCostStates.SingleAsync()).InventoryValue);
        var snapshots = await check.StockValuationPeriodSnapshotHdrs.Include(x => x.Lines).OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(1m, Assert.Single(snapshots[0].Lines).ClosingQty);
        Assert.Equal(100m, Assert.Single(snapshots[1].Lines).ClosingQty);
    }

    private CostingRepairPlanner Planner() => new(
        _factory, new Tenant(), new Access(true), new CostingRepairOwnershipResolver(_factory, new Tenant()), [new SucceedingAdapter()]);

    private CostingStateRepairPlanner StatePlanner() => new(_factory, new Tenant(), new Access(true));

    private CostingStateRepairService StateService(IBranchStockTransactionLock branchLock) =>
        new(_factory, new Tenant(), new Access(true), branchLock);

    private sealed class RecordingLock : IBranchStockTransactionLock
    {
        public int Calls { get; private set; }
        public Func<AppDbContext, CancellationToken, Task>? During { get; init; }

        public async Task AcquireAsync(AppDbContext db, string companyCode, string branchCode, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (During is not null)
                await During(db, cancellationToken);
        }
    }

    private sealed class SucceedingAdapter : ICostingRepairAdapter
    {
        public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal) { CostingRepairOwnerTypes.MiscReceipt };

        public Task<CostingRepairCapability> CanHandleAsync(CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
            Task.FromResult(new CostingRepairCapability(true, null));

        public Task<CostingRepairStepResult> ReverseAsync(CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CostingRepairStepResult(true, false, null, null));

        public Task<CostingRepairStepResult> RepostAsync(CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CostingRepairStepResult(false, false, "not used", null));
    }

    private sealed class CapturingPostingService : IIvInventoryPostingService
    {
        public List<int> RolledBack { get; } = [];

        public Task<IvInventoryPostingResult> PostAsync(string trxType, IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default) =>
            Task.FromResult(IvInventoryPostingResult.Fail("not used"));

        public Task<IvInventoryPostingResult> RollbackAsync(string trxType, IReadOnlyList<int> batchNos, CancellationToken cancellationToken = default)
        {
            RolledBack.AddRange(batchNos);
            return Task.FromResult(new IvInventoryPostingResult { Succeeded = true, SucceededCount = batchNos.Count });
        }

        public Task<IvInventoryPostingBatchResult> PostStockOutInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, string expectedTrxType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ValuePostingInTransactionAsync(StockPostingContext? context, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IvInventoryPostingBatchResult> RollBackStockOutInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, string expectedTrxType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IvInventoryPostingBatchResult> PostStockInInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, string expectedTrxType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IvInventoryPostingBatchResult> RollBackStockInInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, string expectedTrxType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteNewStockInBatchInTransactionAsync(AppDbContext db, string companyCode, string branchCode, int batchNo, string expectedTrxType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IvInventoryPostingBatchResult> PostStockAdjustmentInTransactionAsync(AppDbContext db, string companyCode, string branchCode, string userId, int batchNo, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
