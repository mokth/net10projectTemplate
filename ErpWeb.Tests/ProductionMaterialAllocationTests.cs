using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

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
            .UseSqlite(_connection).Options);
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
    public async Task Lot_without_expiry_falls_back_to_fifo_and_cost_requires_permission()
    {
        var materialId = await SeedAsync(lotControl: true, location: "BIN-A");
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
        Assert.Equal(["OLD", "NEW"], result.Data!.Allocations.Select(x => x.LotNo));
        Assert.Equal([5m, 1m], result.Data.Allocations.Select(x => x.SuggestedBaseQty));
        Assert.Equal(7m, result.Data.Allocations[0].UnitPrice);
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
        var materialId = await SeedAsync(lotControl: false, location: null);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var material = await db.ProductionWorkOrderMaterials.SingleAsync();
            db.IvBalLocs.Add(Balance(20, "", 6m, new DateTime(2026, 9, 1), null, "BIN-A"));
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
            access,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)));
        var sut = new ProductionMaterialIssueService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access,
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
        Assert.Equal(6m, line.AvailableQty);
        Assert.Equal(1.5m, line.ShortageQty);
        Assert.True(line.CanManualIssue);
        Assert.Equal(new DateTime(2026, 10, 1), result.Data.IssueDate);
    }

    private ProductionMaterialAllocationService CreateSut(bool canViewCost)
    {
        var access = Access(canViewCost);
        return new ProductionMaterialAllocationService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access,
            new FixedCurrentDateService(new DateTime(2026, 10, 1)));
    }

    private static IAccessRightService Access(bool canViewCost)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PlanningMaterialIssue, PermissionCodes.ViewCost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canViewCost);
        return access.Object;
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
            WorkOrder = order, RouteStep = route, OperationCode = "OP", ProcessType = "MANUAL", RowVersion = [1]
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
}
