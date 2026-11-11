using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Subcon;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Subcon;

internal sealed class SubconTestHarness : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public SubconTestHarness()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        Factory = new TestDbContextFactory(options);
        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
        Scope = new InventoryTenantScope
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            UserId = "tester"
        };
        BindTenant();
        GrantAll();
    }

    public IDbContextFactory<AppDbContext> Factory { get; }
    public Mock<IInventoryTenantContext> Tenant { get; } = new();
    public Mock<IAccessRightService> Access { get; } = new();
    public InventoryTenantScope Scope { get; set; }

    public ISubconApplicationService CreateService() =>
        new SubconApplicationService(
            Factory,
            Tenant.Object,
            Access.Object,
            new RunningNumberService(),
            new SubconBlockedInventorySourceAdapter(),
            new SubconBlockedProductionSourceAdapter());

    public void BindTenant()
    {
        Tenant.Setup(x => x.TryCompanyScope()).Returns(Scope);
        Tenant.Setup(x => x.TryBranchScope()).Returns(Scope);
        Tenant.Setup(x => x.TryWriteScope()).Returns(Scope);
    }

    public void GrantAll()
    {
        Access.Reset();
        Access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public void Deny(params (string Menu, string Permission)[] denied)
    {
        Access.Reset();
        Access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menu, string permission, CancellationToken _) =>
                !denied.Any(d =>
                    string.Equals(d.Menu, menu, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Permission, permission, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task SeedMastersAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            SuppName = "Paint Co"
        });
        db.IvStockMasters.Add(Stock("RM1", "Metal plate", lot: false, stock: true));
        db.IvStockMasters.Add(Stock("LOT1", "Lot plate", lot: true, stock: true));
        db.IvStockMasters.Add(Stock("NS1", "Non-stock", lot: false, stock: false));
        await db.SaveChangesAsync();
    }

    public async Task SeedBalancesAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "RM1",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 100m,
            StdUom = "PCS"
        });
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = "WIP",
            ItemCode = "FG1",
            Qty = 10m,
            Uom = "EA",
            BaseQty = 10m,
            BaseUom = "EA",
            WorkOrderId = 1,
            WorkOrderNo = "WO-1",
            WarehouseCode = "MAIN",
            LocationCode = "SITE",
            LotNo = "L1"
        });
        await db.SaveChangesAsync();
    }

    public static SubconDispatchSaveCommand GoodDispatch(
        string item = "RM1",
        decimal qty = 5m,
        string mode = SubconModes.Inventory,
        string? workOrderNo = null) => new()
    {
        TrxDate = DateTime.Today,
        Mode = mode,
        SupplierCode = "SUP01",
        PromisedReturnDate = DateTime.Today.AddDays(7),
        Lines =
        [
            new SubconDispatchLineSaveCommand
            {
                LineNo = 1,
                ItemCode = item,
                Qty = qty,
                WarehouseCode = "MAIN",
                LocationCode = "BIN1",
                Uom = "PCS",
                WorkOrderNo = workOrderNo
            }
        ]
    };

    public async Task<StockSnapshot> SnapshotAsync()
    {
        await using var db = await Factory.CreateDbContextAsync();
        return new StockSnapshot(
            await db.IvBalLocs.CountAsync(),
            await db.IvBalLocs.SumAsync(x => (decimal?)x.StdQty) ?? 0m,
            await db.IvTrxHistories.CountAsync(),
            await db.IvTrxBatches.CountAsync(),
            await db.IvTrxBatchDetails.CountAsync(),
            await db.StockPostings.CountAsync(),
            await db.ProductionBalLots.CountAsync(),
            await db.ProductionBalLots.SumAsync(x => (decimal?)x.BaseQty) ?? 0m,
            await db.SubconDispatches.CountAsync(),
            await db.SubconReceipts.CountAsync());
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private static IvStockMaster Stock(string code, string name, bool lot, bool stock) => new()
    {
        CompanyCode = "DEMO",
        ICode = code,
        IDesc = name,
        StdUom = "PCS",
        StockControl = stock,
        LotControl = lot,
        DefWarehouse = "MAIN",
        DefLocation = "BIN1",
        IsActive = true
    };
}

internal sealed record StockSnapshot(
    int BalLocCount,
    decimal BalLocQty,
    int HistoryCount,
    int BatchCount,
    int BatchDetailCount,
    int StockPostingCount,
    int WipCount,
    decimal WipQty,
    int DispatchCount,
    int ReceiptCount);
