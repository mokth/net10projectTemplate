using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Data.Common;

namespace ErpWeb.Tests;

[Trait(TestCategories.Name, TestCategories.Planning)]
public sealed class ProductionMaterialAllocationTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public ProductionMaterialAllocationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new SqliteUnicodeLiteralInterceptor())
            .Options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Lot_control_uses_fefo_excludes_invalid_stock_and_splits_shortage()
    {
        var materialId = await SeedAsync(lotControl: true, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.AddRange(
                Lot("A", new DateTime(2026, 10, 20)),
                Lot("B", new DateTime(2026, 10, 10)),
                Lot("EXPIRED", new DateTime(2026, 9, 30)),
                Lot("INACTIVE", new DateTime(2026, 10, 5), active: false));
            await db.SaveChangesAsync();
            var lots = await db.IvLots.ToDictionaryAsync(x => x.LotNo);
            db.IvBalLocs.AddRange(
                Balance(1, "A", 4m, new DateTime(2026, 9, 1), lots["A"].Id, "BIN-A", unitPrice: 8m),
                Balance(2, "B", 3m, new DateTime(2026, 9, 20), lots["B"].Id, "BIN-B", unitPrice: 9m),
                Balance(3, "EXPIRED", 99m, new DateTime(2026, 8, 1), lots["EXPIRED"].Id, "BIN-A"),
                Balance(4, "INACTIVE", 99m, new DateTime(2026, 8, 1), lots["INACTIVE"].Id, "BIN-A"),
                Balance(5, "B", 99m, new DateTime(2026, 8, 1), lots["B"].Id, "BIN-X", warehouse: "OTHER"));
            await db.SaveChangesAsync();
        }

        var result = await CreateSut(canViewCost: false).AutoAllocateAsync(new ProductionMaterialAllocationRequest
        {
            WorkOrderMaterialId = materialId,
            IssueDate = new DateTime(2026, 10, 1),
            RequestedQty = 10m
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(10m, result.Data!.RequestedBaseQty);
        Assert.Equal(7m, result.Data.AllocatedBaseQty);
        Assert.Equal(3m, result.Data.ShortBaseQty);
        Assert.Equal(["B", "A"], result.Data.Allocations.Select(x => x.LotNo));
        Assert.All(result.Data.Allocations, x => Assert.Null(x.UnitPrice));
    }

    [Fact]
    public async Task Lot_without_expiry_falls_back_to_fifo_across_locations_and_cost_requires_permission()
    {
        var materialId = await SeedAsync(lotControl: true, location: "SITE");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.AddRange(Lot("OLD", null), Lot("NEW", null));
            await db.SaveChangesAsync();
            var lots = await db.IvLots.ToDictionaryAsync(x => x.LotNo);
            db.IvBalLocs.AddRange(
                Balance(10, "NEW", 5m, new DateTime(2026, 9, 20), lots["NEW"].Id, "BIN-A", unitPrice: 12m),
                Balance(11, "OLD", 5m, new DateTime(2026, 9, 1), lots["OLD"].Id, "BIN-A", unitPrice: 7m),
                Balance(12, "OLD", 50m, new DateTime(2026, 8, 1), lots["OLD"].Id, "BIN-B", unitPrice: 1m));
            await db.SaveChangesAsync();
        }

        var result = await CreateSut(canViewCost: true).AutoAllocateAsync(new ProductionMaterialAllocationRequest
        {
            WorkOrderMaterialId = materialId,
            IssueDate = new DateTime(2026, 10, 1),
            RequestedQty = 6m
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(["OLD"], result.Data!.Allocations.Select(x => x.LotNo));
        Assert.Equal(["BIN-B"], result.Data.Allocations.Select(x => x.Location));
        Assert.Equal([6m], result.Data.Allocations.Select(x => x.SuggestedBaseQty));
        Assert.Equal(1m, result.Data.Allocations[0].UnitPrice);
    }

    [Fact]
    public async Task Auto_allocate_subtracts_document_level_reserved_balance()
    {
        var materialId = await SeedAsync(lotControl: false, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(Balance(40, "", 100m, new DateTime(2026, 9, 1), null, "BIN-A"));
            await db.SaveChangesAsync();
        }

        var first = await CreateSut(canViewCost: false).AutoAllocateAsync(new ProductionMaterialAllocationRequest
        {
            WorkOrderMaterialId = materialId,
            IssueDate = new DateTime(2026, 10, 1),
            RequestedQty = 80m
        });
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(80m, first.Data!.AllocatedBaseQty);

        var reserved = first.Data.Allocations.ToDictionary(x => x.FromBalLocId, x => x.SuggestedBaseQty);
        var second = await CreateSut(canViewCost: false).AutoAllocateAsync(new ProductionMaterialAllocationRequest
        {
            WorkOrderMaterialId = materialId,
            IssueDate = new DateTime(2026, 10, 1),
            RequestedQty = 50m,
            ReservedBaseQtyByBalance = reserved
        });

        Assert.True(second.Succeeded, second.Message);
        Assert.Equal(20m, second.Data!.AllocatedBaseQty);
        Assert.Equal(30m, second.Data.ShortBaseQty);
    }

    [Fact]
    public async Task Bom_preview_rejects_zero_and_over_planned_and_scales_with_tolerance_max()
    {
        var materialId = await SeedAsync(lotControl: false, location: "SITE");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync(x => x.Uid == materialId);
            material.Tolerance = 5m;
            material.RequiredQty = 100m;
            db.IvBalLocs.Add(Balance(41, "", 200m, new DateTime(2026, 9, 1), null, "BIN-B"));
            await db.SaveChangesAsync();
            var operationId = material.WorkOrderOperationId!.Value;
            var access = Access(canViewCost: false);
            var allocation = new ProductionMaterialAllocationService(
                _factory, InventoryTenantTestHelper.CreateTenantContext(), access.Object,
                new FixedCurrentDateService(new DateTime(2026, 10, 1)));
            var sut = new ProductionMaterialIssueService(
                _factory, InventoryTenantTestHelper.CreateTenantContext(), access.Object,
                new FixedCurrentDateService(new DateTime(2026, 10, 1)), allocation);

            var zero = await sut.GetBomPreviewAsync(operationId, 0m, new DateTime(2026, 10, 1));
            Assert.False(zero.Succeeded);

            var over = await sut.GetBomPreviewAsync(operationId, 11m, new DateTime(2026, 10, 1));
            Assert.False(over.Succeeded);

            var ok = await sut.GetBomPreviewAsync(operationId, 2m, new DateTime(2026, 10, 1));
            Assert.True(ok.Succeeded, ok.Message);
            var line = Assert.Single(ok.Data!.Lines);
            Assert.Equal(20m, line.RequestedMaterialQty);
            Assert.Equal(21m, line.MaxIssueQty);
            Assert.Equal(20m, line.SuggestedIssueQty);
        }
    }

    [Fact]
    public async Task Future_issue_date_and_unsupported_execution_mode_are_blocked()
    {
        var materialId = await SeedAsync(lotControl: false, location: null);
        var sut = CreateSut(canViewCost: true);

        var future = await sut.GetStockCandidatesAsync(materialId, new DateTime(2026, 10, 2));
        Assert.False(future.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, future.ErrorCode);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync();
            material.IssueMethod = PrMaterialIssueMethods.Backflush;
            await db.SaveChangesAsync();
        }

        var blocked = await sut.GetStockCandidatesAsync(materialId, new DateTime(2026, 10, 1));
        Assert.False(blocked.Succeeded);
        Assert.Contains("manual", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Workspace_uses_movement_facts_and_live_eligible_availability()
    {
        var materialId = await SeedAsync(lotControl: false, location: "SITE");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync();
            db.IvBalLocs.AddRange(
                Balance(20, "", 6m, new DateTime(2026, 9, 1), null, "BIN-A"),
                Balance(21, "", 1m, new DateTime(2026, 9, 2), null, "BIN-B"));
            db.ProductionMaterialMovements.AddRange(
                Movement(material, 1, ProductionMaterialMovementTypes.Issue, 4m),
                Movement(material, 2, ProductionMaterialMovementTypes.IssueReversal, 1m),
                Movement(material, 3, ProductionMaterialMovementTypes.Return, 0.5m),
                Movement(material, 4, ProductionMaterialMovementTypes.Consume, 2m));
            await db.SaveChangesAsync();
        }

        var access = Access(canViewCost: false);
        var allocation = new ProductionMaterialAllocationService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)));
        var sut = new ProductionMaterialIssueService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            allocation);

        var result = await sut.GetWorkspaceAsync("WO-1");

        Assert.True(result.Succeeded, result.Message);
        var line = Assert.Single(result.Data!.Materials);
        Assert.Equal(3m, line.IssuedQty);
        Assert.Equal(0.5m, line.ReturnedQty);
        Assert.Equal(2.5m, line.NetIssuedQty);
        Assert.Equal(7.5m, line.OutstandingQty);
        Assert.Equal(2m, line.ConsumedQty);
        Assert.Equal(7m, line.AvailableQty);
        Assert.Equal(0.5m, line.ShortageQty);
        Assert.True(line.CanManualIssue);
        Assert.Equal(new DateTime(2026, 10, 1), result.Data.IssueDate);
    }

    [Fact]
    public async Task Post_creates_inventory_and_production_facts_and_replay_is_idempotent()
    {
        var materialId = await SeedAsync(lotControl: false, location: "SITE");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(Balance(30, "", 10m, new DateTime(2026, 9, 1), null, "BIN-A", unitPrice: 2m));
            await db.SaveChangesAsync();
        }

        var request = await CreatePostRequestAsync(materialId, 30, issueQty: 4m);
        var sut = CreatePostingSut(CreateInventoryPosting());

        var first = await sut.PostAsync(request);
        var replay = await sut.PostAsync(request);

        Assert.True(first.Succeeded, first.Message);
        Assert.True(replay.Succeeded, replay.Message);
        Assert.Equal(first.Data!.BatchNo, replay.Data!.BatchNo);
        Assert.Equal(ProductionWorkOrderStatuses.InProgress, first.Data.WorkOrderStatus);
        var postedMaterial = Assert.Single(first.Data.Materials);
        Assert.Equal(materialId, postedMaterial.WorkOrderMaterialId);
        Assert.Equal(4m, postedMaterial.IssuedQty);
        Assert.Equal(6m, postedMaterial.OutstandingQty);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(6m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 30)).StdQty);
        Assert.Equal(IvBatchStatuses.Posted, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
        var history = Assert.Single(await verify.IvTrxHistories.ToListAsync());
        Assert.Equal("BIN-A", history.FrLocation);
        var movement = Assert.Single(await verify.ProductionMaterialMovements.ToListAsync());
        Assert.Equal(4m, movement.Qty);
        Assert.Equal(4m, movement.BaseQty);
        Assert.Equal("BIN-A", movement.LocationCode);
        Assert.Equal(2m, movement.UnitCost);
        Assert.Equal(8m, movement.TotalCost);
        Assert.Equal(4m, (await verify.ProductionWorkOrderMaterials.SingleAsync()).IssuedQty);
        Assert.Equal(ProductionWorkOrderStatuses.InProgress, (await verify.ProductionWorkOrders.SingleAsync()).Status);
        Assert.Equal(ProductionPostingLinkStatuses.Succeeded, (await verify.ProductionPostingLinks.SingleAsync()).Status);
        Assert.Single(await verify.ProductionAuditEvents.Where(x => x.EventType == ProductionAuditEventTypes.MaterialIssued).ToListAsync());

        var list = await sut.SearchAsync(new ProductionMaterialIssueListQuery { WorkOrderNo = "WO-1" });
        Assert.True(list.Succeeded, list.Message);
        var listRow = Assert.Single(list.Data!.Rows);
        Assert.Equal(first.Data.BatchNo, listRow.BatchNo);
        Assert.Equal(1, listRow.LineCount);

        var document = await sut.GetAsync(first.Data.BatchNo);
        Assert.True(document.Succeeded, document.Message);
        Assert.Equal("WO-1", document.Data!.WorkOrderNo);
        Assert.Equal(10m, document.Data.ProductionQtyThisIssue);
        var documentLine = Assert.Single(document.Data.Lines);
        Assert.Equal(4m, documentLine.IssueQty);
        Assert.Equal(2m, documentLine.UnitCost);

        var rollbackRequest = new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"),
            InventoryBatchNo = first.Data.BatchNo,
            Reason = "Incorrect production issue"
        };
        var rollback = await sut.RollbackAsync(rollbackRequest);
        var rollbackReplay = await sut.RollbackAsync(rollbackRequest);

        Assert.True(rollback.Succeeded, rollback.Message);
        Assert.True(rollbackReplay.Succeeded, rollbackReplay.Message);
        Assert.Equal(rollback.Data!.RollbackOperationId, rollbackReplay.Data!.RollbackOperationId);
        Assert.Equal(0m, Assert.Single(rollback.Data.Materials).IssuedQty);

        verify.ChangeTracker.Clear();
        Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 30)).StdQty);
        Assert.Equal(IvBatchStatuses.New, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.Empty(await verify.IvTrxHistories.ToListAsync());
        var movements = await verify.ProductionMaterialMovements.OrderBy(x => x.Uid).ToListAsync();
        Assert.Equal(2, movements.Count);
        Assert.Equal(ProductionMaterialMovementTypes.Issue, movements[0].MovementType);
        Assert.Equal(ProductionMaterialMovementTypes.IssueReversal, movements[1].MovementType);
        Assert.Equal(movements[0].Uid, movements[1].OriginalMovementId);
        Assert.Equal(0m, (await verify.ProductionWorkOrderMaterials.SingleAsync()).IssuedQty);
        Assert.Equal(ProductionWorkOrderStatuses.InProgress, (await verify.ProductionWorkOrders.SingleAsync()).Status);
        var links = await verify.ProductionPostingLinks.OrderBy(x => x.Uid).ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(ProductionPostingLinkStatuses.Draft, links[0].Status);
        Assert.Equal(ProductionPostingLinkStatuses.Succeeded, links[1].Status);
        Assert.Equal(links[0].Uid, links[1].OriginalPostingLinkId);
        Assert.Single(await verify.ProductionAuditEvents.Where(x => x.EventType == ProductionAuditEventTypes.MaterialIssueRolledBack).ToListAsync());

        // Re-post after correction: stock and issued qty must tally again without creating a new batch.
        var repost = await sut.PostAsync([first.Data.BatchNo]);
        Assert.True(
            repost.Succeeded && repost.Data!.SucceededCount == 1,
            repost.Data?.Batches.FirstOrDefault()?.Message ?? repost.Message ?? "Re-post failed.");
        Assert.Equal(1, repost.Data!.SucceededCount);

        verify.ChangeTracker.Clear();
        Assert.Equal(6m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 30)).StdQty);
        Assert.Equal(IvBatchStatuses.Posted, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.Equal(4m, (await verify.ProductionWorkOrderMaterials.SingleAsync()).IssuedQty);
        var afterRepostMovements = await verify.ProductionMaterialMovements.OrderBy(x => x.Uid).ToListAsync();
        Assert.Equal(3, afterRepostMovements.Count);
        Assert.Equal(ProductionMaterialMovementTypes.Issue, afterRepostMovements[2].MovementType);
        Assert.Equal(4m, afterRepostMovements[2].Qty);
    }

    [Fact]
    public async Task Rollback_then_edit_qty_and_repost_reuses_batch_details()
    {
        var materialId = await SeedAsync(lotControl: false, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(Balance(33, "", 10m, new DateTime(2026, 9, 1), null, "BIN-A", unitPrice: 2m));
            await db.SaveChangesAsync();
        }

        var sut = CreatePostingSut(CreateInventoryPosting());
        var posted = await sut.PostAsync(await CreatePostRequestAsync(materialId, 33, issueQty: 4m));
        Assert.True(posted.Succeeded, posted.Message);
        var batchNo = posted.Data!.BatchNo;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var originalDetailId = (await db.IvTrxBatchDetails.SingleAsync()).Id;
            Assert.True(await db.ProductionMaterialMovements.AnyAsync(x => x.InventoryBatchDetailId == originalDetailId));
        }

        var rollback = await sut.RollbackAsync(new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"),
            InventoryBatchNo = batchNo,
            Reason = "Correct quantity"
        });
        Assert.True(rollback.Succeeded, rollback.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync();
            var operationId = await db.ProductionWorkOrderOperations.AsNoTracking()
                .Where(x => x.WorkOrderId == order.Uid).Select(x => x.Uid).SingleAsync();
            var detailIdBeforeEdit = (await db.IvTrxBatchDetails.SingleAsync()).Id;

            var update = await sut.UpdateAsync(batchNo, new ProductionMaterialIssueSaveRequest
            {
                WorkOrderNo = order.WorkOrderNo,
                WorkOrderOperationId = operationId,
                SnapshotRevision = order.SnapshotRevision,
                SnapshotHash = order.SnapshotHash,
                ProductionQtyThisIssue = 10m,
                TrxDateTime = new DateTime(2026, 10, 1),
                RefNo = "AUTO",
                Remark = "Corrected after rollback",
                Lines =
                [
                    new ProductionMaterialIssueLineRequest
                    {
                        WorkOrderMaterialId = materialId,
                        IssueQty = 3m,
                        Allocations =
                        [
                            new ProductionMaterialIssueAllocationRequest { FromBalLocId = 33, BaseQty = 3m }
                        ]
                    }
                ]
            });
            Assert.True(update.Succeeded, update.Message);

            db.ChangeTracker.Clear();
            var detailAfterEdit = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(detailIdBeforeEdit, detailAfterEdit.Id);
            Assert.Equal(3m, detailAfterEdit.FrStdQty);
            Assert.Equal(3m, (await db.ProductionMaterialIssueLines.SingleAsync()).IssueQty);
        }

        var deleteBlocked = await sut.DeleteAsync([batchNo]);
        Assert.True(deleteBlocked.Succeeded);
        Assert.Equal(0, deleteBlocked.Data!.SucceededCount);
        Assert.Equal(1, deleteBlocked.Data.FailedCount);
        Assert.Contains("cancel", deleteBlocked.Data.Batches[0].Message, StringComparison.OrdinalIgnoreCase);

        var repost = await sut.PostAsync([batchNo]);
        Assert.True(
            repost.Succeeded && repost.Data!.SucceededCount == 1,
            repost.Data?.Batches.FirstOrDefault()?.Message ?? repost.Message ?? "Re-post failed.");

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(7m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 33)).StdQty);
        Assert.Equal(3m, (await verify.ProductionWorkOrderMaterials.SingleAsync()).IssuedQty);
        var movements = await verify.ProductionMaterialMovements.OrderBy(x => x.Uid).ToListAsync();
        Assert.Equal(3, movements.Count);
        Assert.Equal(ProductionMaterialMovementTypes.Issue, movements[2].MovementType);
        Assert.Equal(3m, movements[2].Qty);
        Assert.Equal(movements[0].InventoryBatchDetailId, movements[2].InventoryBatchDetailId);
    }

    [Fact]
    public async Task Rollback_is_blocked_when_a_later_movement_depends_on_the_issue()
    {
        var materialId = await SeedAsync(lotControl: false, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(Balance(32, "", 10m, new DateTime(2026, 9, 1), null, "BIN-A", unitPrice: 2m));
            await db.SaveChangesAsync();
        }
        var sut = CreatePostingSut(CreateInventoryPosting());
        var posted = await sut.PostAsync(await CreatePostRequestAsync(materialId, 32, issueQty: 4m));
        Assert.True(posted.Succeeded, posted.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var issue = await db.ProductionMaterialMovements.SingleAsync();
            db.ProductionMaterialMovements.Add(new ProductionMaterialMovement
            {
                CompanyCode = issue.CompanyCode, BranchCode = issue.BranchCode,
                WorkOrderId = issue.WorkOrderId, WorkOrderMaterialId = issue.WorkOrderMaterialId,
                WorkOrderOperationId = issue.WorkOrderOperationId, MovementType = ProductionMaterialMovementTypes.Consume,
                MovementDate = issue.MovementDate.AddMinutes(1), ItemCode = issue.ItemCode,
                Qty = 1m, Uom = issue.Uom, BaseQty = 1m, BaseUom = issue.BaseUom,
                ConversionFactorToBase = issue.ConversionFactorToBase, WarehouseCode = issue.WarehouseCode,
                LocationCode = issue.LocationCode, LotNo = issue.LotNo, LotId = issue.LotId,
                FromBalLocId = issue.FromBalLocId, ItemStatus = issue.ItemStatus,
                InventoryBatchId = issue.InventoryBatchId, InventoryBatchNo = issue.InventoryBatchNo,
                InventoryBatchDetailId = issue.InventoryBatchDetailId, InventoryTrxLineNo = issue.InventoryTrxLineNo,
                UnitCost = issue.UnitCost, TotalCost = issue.UnitCost, PostingLinkId = issue.PostingLinkId,
                OriginalMovementId = issue.Uid, CreatedDate = issue.CreatedDate.AddMinutes(1), CreatedBy = "admin"
            });
            await db.SaveChangesAsync();
        }

        var result = await sut.RollbackAsync(new ProductionMaterialIssueRollbackRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"), InventoryBatchNo = posted.Data!.BatchNo,
            Reason = "Should be blocked"
        });

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, result.ErrorCode);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(6m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 32)).StdQty);
        Assert.Equal(IvBatchStatuses.Posted, (await verify.IvTrxBatches.SingleAsync()).BatchStatus);
        Assert.DoesNotContain(await verify.ProductionMaterialMovements.ToListAsync(), x => x.MovementType == ProductionMaterialMovementTypes.IssueReversal);
    }

    [Fact]
    public async Task Inventory_failure_rolls_back_batch_stock_link_and_production_changes()
    {
        var materialId = await SeedAsync(lotControl: false, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Add(Balance(31, "", 10m, new DateTime(2026, 9, 1), null, "BIN-A", unitPrice: 2m));
            await db.SaveChangesAsync();
        }

        var inner = CreateInventoryPosting();
        var failedPosting = new Mock<IIvInventoryPostingService>();
        failedPosting
            .Setup(x => x.PostStockOutInTransactionAsync(
                It.IsAny<AppDbContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async (AppDbContext db, string company, string branch, string user, int batchNo, string trxType, CancellationToken ct) =>
            {
                var staged = await inner.PostStockOutInTransactionAsync(db, company, branch, user, batchNo, trxType, ct);
                Assert.True(staged.Succeeded, staged.ErrorMessage);
                return IvInventoryPostingBatchResult.Fail(batchNo, "Forced failure after inventory changes were staged.");
            });

        var result = await CreatePostingSut(failedPosting.Object)
            .PostAsync(await CreatePostRequestAsync(materialId, 31, issueQty: 4m));

        Assert.False(result.Succeeded);
        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(10m, (await verify.IvBalLocs.SingleAsync(x => x.Id == 31)).StdQty);
        var savedBatch = await verify.IvTrxBatches.SingleAsync();
        Assert.Equal(IvBatchStatuses.New, savedBatch.BatchStatus);
        Assert.Empty(await verify.IvTrxHistories.ToListAsync());
        Assert.Empty(await verify.ProductionMaterialMovements.ToListAsync());
        Assert.Equal(ProductionPostingLinkStatuses.Draft, (await verify.ProductionPostingLinks.SingleAsync()).Status);
        Assert.Empty(await verify.ProductionAuditEvents.ToListAsync());
        Assert.Single(await verify.MsRunningNos.ToListAsync());
        Assert.Equal(0m, (await verify.ProductionWorkOrderMaterials.SingleAsync()).IssuedQty);
        Assert.Equal(ProductionWorkOrderStatuses.Released, (await verify.ProductionWorkOrders.SingleAsync()).Status);
    }

    private ProductionMaterialAllocationService CreateSut(bool canViewCost)
    {
        var access = Access(canViewCost);
        return new ProductionMaterialAllocationService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)));
    }

    private ProductionMaterialIssueService CreatePostingSut(IIvInventoryPostingService posting)
    {
        var access = Access(canViewCost: true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Post, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Rollback, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var allocation = new ProductionMaterialAllocationService(
            _factory, tenant, access.Object, new FixedCurrentDateService(new DateTime(2026, 10, 1)));
        return new ProductionMaterialIssueService(
            _factory, tenant, access.Object, new FixedCurrentDateService(new DateTime(2026, 10, 1)),
            allocation, new RunningNumberService(), new IvStockPostingRepository(), posting);
    }

    private IIvInventoryPostingService CreateInventoryPosting() =>
        new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access(canViewCost: true).Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

    private async Task<ProductionMaterialIssuePostRequest> CreatePostRequestAsync(
        long materialId,
        int balanceId,
        decimal issueQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var order = await db.ProductionWorkOrders.AsNoTracking().SingleAsync();
        return new ProductionMaterialIssuePostRequest
        {
            PostingRequestId = Guid.NewGuid().ToString("N"),
            WorkOrderNo = order.WorkOrderNo,
            SnapshotRevision = order.SnapshotRevision,
            SnapshotHash = order.SnapshotHash,
            ProductionQtyThisIssue = 10m,
            IssueDate = new DateTime(2026, 10, 1),
            Remark = "Production issue test",
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
                            BaseQty = issueQty
                        }
                    ]
                }
            ]
        };
    }

    private static Mock<IAccessRightService> Access(bool canViewCost)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canViewCost);
        return access;
    }

    private async Task<long> SeedAsync(bool lotControl, string? location)
    {
        await using var db = await _factory.CreateDbContextAsync();
        // This focused read-layer fixture does not need a Product Definition source graph; the
        // released Work Order snapshot is deliberately the only production input under test.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "RM001", IDesc = "Raw material", StdUom = "KG",
            StockControl = true, LotControl = lotControl, IsActive = true, RowVersion = [1]
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "WH01", IsActive = true, RowVersion = [1]
        });
        var order = new ProductionWorkOrder
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WorkOrderNo = "WO-1", SnapshotHash = new string('A', 64),
            ProductCode = "FG001", Status = ProductionWorkOrderStatuses.Released,
            PlannedStartDateTime = new DateTime(2026, 10, 1), PlannedCompletionDateTime = new DateTime(2026, 10, 2),
            DefinitionEffectiveDate = new DateTime(2026, 10, 1),
            SnapshotFormatVersion = ProductionSnapshotFormatVersions.Current, IsLegacySnapshot = false, RowVersion = [1]
        };
        var route = new ProductionWorkOrderRouteStep
        {
            WorkOrder = order, WorkCentreCode = "WC", OutputItemCode = "FG001", RowVersion = [1]
        };
        var operation = new ProductionWorkOrderOperation
        {
            WorkOrder = order, RouteStep = route, OperationCode = "OP", ProcessType = "MANUAL",
            PlannedOutputQty = 10m, PlannedOutputUom = "KG", RowVersion = [1]
        };
        var material = new ProductionWorkOrderMaterial
        {
            WorkOrder = order, WorkOrderOperation = operation, ComponentCode = "RM001", MfgType = "BUY",
            BomPath = "RM001", IssueMethod = PrMaterialIssueMethods.Manual,
            SupplySource = PrMaterialSupplySources.Purchased, RequiredQty = 10m, RequiredUom = "KG",
            RequiredBaseQty = 10m, BaseUom = "KG", ConversionFactorToBase = 1m,
            WarehouseCode = "WH01", LocationCode = location, RowVersion = [1]
        };
        db.ProductionWorkOrderMaterials.Add(material);
        await db.SaveChangesAsync();
        return material.Uid;
    }

    private static IvLot Lot(string lotNo, DateTime? expiry, bool active = true) => new()
    {
        CompanyCode = "DEMO", ICode = "RM001", LotNo = lotNo, ExpiryDate = expiry, IsActive = active
    };

    private static IvBalLoc Balance(
        int id, string lotNo, decimal qty, DateTime date, int? lotId, string location,
        decimal unitPrice = 0m, string warehouse = "WH01") => new()
    {
        Id = id, CompanyCode = "DEMO", BranchCode = "HQ", ICode = "RM001", WhCode = warehouse,
        LocCode = location, LotNo = lotNo, LotId = lotId, IStatus = IvItemStatuses.Active,
        StdQty = qty, StdUom = "KG", TransDate = date, UnitPrice = unitPrice, RowVersion = [1]
    };

    private static ProductionMaterialMovement Movement(
        ProductionWorkOrderMaterial material,
        int line,
        string type,
        decimal qty) => new()
    {
        CompanyCode = "DEMO",
        BranchCode = "HQ",
        WorkOrderId = material.WorkOrderId,
        WorkOrderMaterialId = material.Uid,
        WorkOrderOperationId = material.WorkOrderOperationId!.Value,
        MovementType = type,
        MovementDate = new DateTime(2026, 10, 1),
        ItemCode = material.ComponentCode,
        Qty = qty,
        Uom = "KG",
        BaseQty = qty,
        BaseUom = "KG",
        ConversionFactorToBase = 1m,
        WarehouseCode = "WH01",
        LocationCode = "BIN-A",
        LotNo = "",
        FromBalLocId = 20,
        ItemStatus = IvItemStatuses.Active,
        InventoryBatchId = 1,
        InventoryBatchNo = 1,
        InventoryBatchDetailId = line,
        InventoryTrxLineNo = (short)line,
        UnitCost = 0m,
        TotalCost = 0m,
        PostingLinkId = line,
        CreatedDate = new DateTime(2026, 10, 1),
        CreatedBy = "admin"
    };

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private sealed class SqliteUnicodeLiteralInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            command.CommandText = command.CommandText.Replace("N'", "'", StringComparison.Ordinal);
            return ValueTask.FromResult(result);
        }
    }
}
