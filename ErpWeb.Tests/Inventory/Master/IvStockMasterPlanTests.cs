using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Inventory.Master;

/// <summary>
/// Plan coverage: mandatory stock-master validation, structural field locking, primary UOM
/// reconciliation with <c>IvItemUomConversion</c>, and snapshot hardening for downstream documents.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMasters)]
public class IvStockMasterPlanTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockMasterPlanTests()
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

        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            WarehouseDesc = "Main WH",
            IsActive = true,
            RowVersion = Rv(1)
        });

        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            LocDesc = "Bin 1",
            IsActive = true,
            RowVersion = Rv(2)
        });

        db.MsUoms.AddRange(
            new MsUom { CompanyCode = "DEMO", UomCode = "KG", UomDesc = "Kilogram", IsActive = true, RowVersion = Rv(3) },
            new MsUom { CompanyCode = "DEMO", UomCode = "BOX", UomDesc = "Box", IsActive = true, RowVersion = Rv(4) },
            new MsUom { CompanyCode = "DEMO", UomCode = "PCS", UomDesc = "Piece", IsActive = true, RowVersion = Rv(5) });

        db.IvClassifications.Add(new IvClassification { Code = "001", Description = "Classification A" });

        db.IvClasses.Add(new IvClass
        {
            CompanyCode = "DEMO",
            IClassCode = "FG",
            IDesc = "Finished",
            IsActive = true,
            RowVersion = Rv(6)
        });

        db.IvTypes.Add(new IvType
        {
            CompanyCode = "DEMO",
            TypeCode = "FG",
            TypeName = "Finished Goods",
            KeepStock = true,
            IsActive = true,
            RowVersion = Rv(7)
        });

        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = "DEMO",
            IStatus = "ACTIVE",
            StatusDesc = "Active",
            IsActive = true,
            RowVersion = Rv(8)
        });

        // Conversion baselines.
        db.IvStockMasters.AddRange(
            Item("S500"),
            // Case A, forward row: BOX->KG 1:24, primary factor 24.
            Item("S510", sellingUom: "BOX", stdPackSize: 24m),
            // Case A, reverse row: KG->BOX 18:1, primary factor 18.
            Item("S520", sellingUom: "BOX", stdPackSize: 18m),
            // Case B: approved conversion disagrees with the old primary factor.
            Item("S530", sellingUom: "BOX", stdPackSize: 12m),
            // Logical duplicates: two active rows describing the same pair.
            Item("S540"),
            // Purchase-side pair.
            Item("S550", purUom: "BOX", purStdPackSize: 10m));

        var bom = new PrBomHdr
        {
            CompanyCode = "DEMO",
            ProdCode = "L140",
            DefinitionCode = PrProductDefinitionCodes.Standard,
            DefinitionName = PrProductDefinitionCodes.StandardName,
            IsDefaultDefinition = true,
            Version = 1,
            Status = PrBomStatuses.Active,
            EffectiveFrom = new DateTime(2026, 1, 1),
            BaseQty = 1m,
            BaseUom = "KG",
            BranchCode = "HQ",
            LocationCode = "SITE",
            RowVersion = Rv(9)
        };
        db.PrBomHdrs.Add(bom);

        // Structural-lock fixtures.
        db.IvStockMasters.AddRange(
            Item("L100"),
            Item("L110"),
            Item("L120"),
            Item("L121"),
            Item("L130"),
            Item("L131"),
            Item("L140"),
            Item("L141"),
            Item("L150"),
            Item("L160"));

        await db.SaveChangesAsync();

        db.IvItemUomConversions.AddRange(
            Conversion("S510", "BOX", "KG", 1m, 24m),
            Conversion("S520", "KG", "BOX", 18m, 1m),
            Conversion("S530", "BOX", "KG", 1m, 7m),
            Conversion("S540", "BOX", "KG", 1m, 12m),
            Conversion("S540", "KG", "BOX", 12m, 1m));

        // §5.1 inventory footprint: a balance row alone (zero quantity) locks the item.
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "L100",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = 0m,
            StdUom = "KG",
            RowVersion = Rv(20)
        });

        // §5.2 open purchase request.
        db.PoPrs.Add(new PoPr
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PrNo = "PR-L110",
            CreateDt = FixedToday,
            Status = PoPrStatuses.Approved
        });
        db.PoPrDetails.Add(new PoPrDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PrNo = "PR-L110",
            Line = 1,
            ICode = "L110",
            Qty = 5m,
            Status = PoPrStatuses.Open
        });

        // §5.2 latest operational PO revision with remaining quantity.
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO-L120",
            PoRelNo = 1,
            Status = PoOrderStatuses.Open,
            FinClosed = false
        });
        db.PoOrderDetails.Add(new PoOrderDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO-L120",
            PoRelNo = 1,
            Line = 1,
            ICode = "L120",
            PoQty = 10m,
            BalanceQty = 10m
        });

        // §5.2 superseded revision 1 keeps a balance, but revision 2 is the live one and is complete.
        db.PoOrders.AddRange(
            new PoOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                PoNo = "PO-L121",
                PoRelNo = 1,
                Status = PoOrderStatuses.Open,
                FinClosed = false
            },
            new PoOrder
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                PoNo = "PO-L121",
                PoRelNo = 2,
                Status = PoOrderStatuses.Open,
                FinClosed = false
            });
        db.PoOrderDetails.AddRange(
            new PoOrderDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                PoNo = "PO-L121",
                PoRelNo = 1,
                Line = 1,
                ICode = "L121",
                PoQty = 10m,
                BalanceQty = 10m
            },
            new PoOrderDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                PoNo = "PO-L121",
                PoRelNo = 2,
                Line = 1,
                ICode = "L121",
                PoQty = 10m,
                RecvQty = 10m,
                BalanceQty = 0m
            });

        // §5.3 current sales order with remaining balance.
        db.SaSos.AddRange(
            new SaSo
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SoNo = "SO-L130",
                CustRel = 1,
                IsCurrent = true,
                SoDate = FixedToday,
                Status = SaSoStatuses.New,
                CustCode = "CUST1"
            },
            new SaSo
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SoNo = "SO-L131",
                CustRel = 1,
                IsCurrent = true,
                SoDate = FixedToday,
                Status = SaSoStatuses.Closed,
                CustCode = "CUST1"
            });
        db.SaSoDetails.AddRange(
            new SaSoDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SoNo = "SO-L130",
                CustRel = 1,
                Line = 1,
                ICode = "L130",
                OrderQty = 5m,
                BalanceQty = 5m
            },
            new SaSoDetail
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SoNo = "SO-L131",
                CustRel = 1,
                Line = 1,
                ICode = "L131",
                OrderQty = 5m,
                BalanceQty = 5m
            });

        await db.SaveChangesAsync();

        var bomUid = bom.Uid;

        // §5.4 production work orders: one productive, one terminal.
        db.ProductionWorkOrders.AddRange(
            WorkOrder("WO-L140", "L140", ProductionWorkOrderStatuses.Draft, bomUid),
            WorkOrder("WO-L141", "L141", ProductionWorkOrderStatuses.Closed, bomUid),
            WorkOrder("WO-L160", "L160", ProductionWorkOrderStatuses.Closed, bomUid));
        await db.SaveChangesAsync();

        var openWorkOrderId = await db.ProductionWorkOrders
            .Where(x => x.WorkOrderNo == "WO-L140")
            .Select(x => x.Uid)
            .SingleAsync();
        var balanceWorkOrderId = await db.ProductionWorkOrders
            .Where(x => x.WorkOrderNo == "WO-L160")
            .Select(x => x.Uid)
            .SingleAsync();

        db.ProductionWorkOrderMaterials.Add(new ProductionWorkOrderMaterial
        {
            WorkOrderId = openWorkOrderId,
            ComponentCode = "L150",
            BomPath = "L140",
            MfgType = "MAKE",
            IssueMethod = "MANUAL",
            SupplySource = "STOCK"
        });

        // §5.4 live production balance.
        db.ProductionBalLots.Add(new ProductionBalLot
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Kind = "FG",
            ItemCode = "L160",
            Qty = 2m,
            Uom = "KG",
            BaseQty = 2m,
            BaseUom = "KG",
            WorkOrderId = balanceWorkOrderId,
            WorkOrderNo = "WO-L160",
            WarehouseCode = "MAIN",
            LocationCode = "BIN1",
            LotNo = "PB-L160"
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ---------------------------------------------------------------- §3.2 mandatory fields

    [Fact]
    public async Task StockItem_WithoutClass_Fails()
    {
        var model = NewModel("D700");
        model.IClassCode = null;
        model.ISubClassCode = null;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("IClassCode"));
    }

    [Fact]
    public async Task StockItem_WithoutStdUom_Fails()
    {
        var model = NewModel("D701");
        model.StdUom = " ";

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("StdUom"));
    }

    [Fact]
    public async Task BlankSellingAndPurchaseUom_DefaultToStandardUom()
    {
        var model = NewModel("D702");
        model.SellingUom = null;
        model.PurUom = null;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("KG", result.Data!.SellingUom);
        Assert.Equal("KG", result.Data!.PurUom);
        Assert.Equal(1m, result.Data!.StdPackSize);
        Assert.Equal(1m, result.Data!.PurStdPackSize);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvItemUomConversions.AnyAsync(x => x.ItemCode == "D702"));
    }

    [Fact]
    public async Task SellingUomDifferent_WithoutFactor_Fails()
    {
        var model = NewModel("D703");
        model.SellingUom = "BOX";
        model.StdPackSize = null;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("StdPackSize"));
    }

    [Fact]
    public async Task SellingUomDifferent_WithZeroFactor_Fails()
    {
        var model = NewModel("D704");
        model.SellingUom = "BOX";
        model.StdPackSize = 0m;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("StdPackSize"));
    }

    [Fact]
    public async Task PurchaseUomDifferent_WithoutFactor_Fails()
    {
        var model = NewModel("D705");
        model.PurUom = "BOX";
        model.PurStdPackSize = null;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("PurStdPackSize"));
    }

    [Fact]
    public async Task IdenticalSellingAndPurchaseUom_WithDifferentFactors_Fails()
    {
        var model = NewModel("D706");
        model.SellingUom = "BOX";
        model.StdPackSize = 5m;
        model.PurUom = "BOX";
        model.PurStdPackSize = 6m;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("PurStdPackSize"));
    }

    [Fact]
    public async Task NonStockItem_KeepsLenientFactorBehaviour()
    {
        var model = NewModel("D707");
        model.StockControl = false;
        model.SellingUom = "BOX";
        model.StdPackSize = null;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    // ---------------------------------------------------------------- §6 conversion reconciliation

    [Fact]
    public async Task NewPrimaryPair_CreatesCanonicalConversionRow()
    {
        var model = NewModel("D710");
        model.SellingUom = "BOX";
        model.StdPackSize = 25m;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvItemUomConversions
            .SingleAsync(x => x.CompanyCode == "DEMO" && x.ItemCode == "D710");
        Assert.Equal("BOX", row.FromUom);
        Assert.Equal("KG", row.ToUom);
        Assert.Equal(1m, row.FromQty);
        Assert.Equal(25m, row.ToQty);
        Assert.True(row.IsActive);
        Assert.Equal(IvUomRoundingModes.AwayFromZero, row.RoundingMode);
    }

    [Fact]
    public async Task PurchasePrimaryPair_CreatesCanonicalConversionRow()
    {
        var model = NewModel("D711");
        model.PurUom = "BOX";
        model.PurStdPackSize = 10m;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvItemUomConversions
            .SingleAsync(x => x.CompanyCode == "DEMO" && x.ItemCode == "D711");
        Assert.Equal("BOX", row.FromUom);
        Assert.Equal("KG", row.ToUom);
        Assert.Equal(10m, row.ToQty);
    }

    [Fact]
    public async Task MultipleActiveRowsForSamePair_FailSave()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("S540")).Data!;
        model.SellingUom = "BOX";
        model.StdPackSize = 20m;

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("multiple active", result.Message, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IvItemUomConversions.CountAsync(x => x.ItemCode == "S540"));
    }

    [Fact]
    public async Task ConflictWithApprovedConversion_DoesNotOverwriteRow()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("S530")).Data!;
        model.StdPackSize = 20m;

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("conflicts with the existing approved", result.Message, StringComparison.Ordinal);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvItemUomConversions.SingleAsync(x => x.ItemCode == "S530");
        Assert.Equal(1m, row.FromQty);
        Assert.Equal(7m, row.ToQty);
        Assert.Equal(12m, await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "S530")
            .Select(x => x.StdPackSize)
            .SingleAsync());
    }

    [Fact]
    public async Task ChangingPrimaryFactor_UpdatesForwardRowInPlace()
    {
        var sut = CreateSut();
        long uid;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            uid = await db.IvItemUomConversions
                .Where(x => x.ItemCode == "S510")
                .Select(x => x.Uid)
                .SingleAsync();
        }

        var model = (await sut.GetAsync("S510")).Data!;
        model.StdPackSize = 30m;

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var verify = await _factory.CreateDbContextAsync();
        var row = await verify.IvItemUomConversions.SingleAsync(x => x.ItemCode == "S510");
        Assert.Equal(uid, row.Uid);
        Assert.Equal(1m, row.FromQty);
        Assert.Equal(30m, row.ToQty);
        Assert.Equal(30m, await verify.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "S510")
            .Select(x => x.StdPackSize)
            .SingleAsync());
    }

    [Fact]
    public async Task ChangingPrimaryFactor_UpdatesReverseRowInPlace()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("S520")).Data!;
        model.StdPackSize = 33m;

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var verify = await _factory.CreateDbContextAsync();
        var row = await verify.IvItemUomConversions.SingleAsync(x => x.ItemCode == "S520");
        Assert.Equal("KG", row.FromUom);
        Assert.Equal(33m, row.FromQty);
        Assert.Equal(1m, row.ToQty);
    }

    [Fact]
    public async Task ExistingConversionAlreadyConsistent_IsAccepted()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("S510")).Data!;
        model.IDesc = "Renamed";

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var verify = await _factory.CreateDbContextAsync();
        var row = await verify.IvItemUomConversions.SingleAsync(x => x.ItemCode == "S510");
        Assert.Equal(24m, row.ToQty);
    }

    [Fact]
    public async Task IdentityPair_DoesNotCreateConversionRow()
    {
        var model = NewModel("D712");
        model.SellingUom = "BOX";
        model.StdUom = "BOX";
        model.PurUom = "BOX";
        model.StdPackSize = 7m;
        model.PurStdPackSize = 7m;

        var result = await CreateSut().SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1m, result.Data!.StdPackSize);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvItemUomConversions.AnyAsync(x => x.ItemCode == "D712"));
    }

    // ---------------------------------------------------------------- §5 structural locking

    [Fact]
    public async Task InventoryBalanceRow_ReportsStructuralLock()
    {
        var result = await CreateSut().GetAsync("L100");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains("inventory balance rows", result.Data.StructuralLockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockedItem_RejectsStdUomChange()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("L100")).Data!;
        model.StdUom = "PCS";
        model.SellingUom = "PCS";
        model.PurUom = "PCS";
        model.StdPackSize = null;
        model.PurStdPackSize = null;

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("inventory balance rows", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockedItem_AllowsUnrelatedEdit()
    {
        var sut = CreateSut();
        var model = (await sut.GetAsync("L100")).Data!;
        model.IDesc = "Renamed while locked";

        var result = await sut.SaveAsync(model, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("Renamed while locked", await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "L100")
            .Select(x => x.IDesc)
            .SingleAsync());
    }

    [Fact]
    public async Task LockedItem_RejectsStockAndLotControlChange()
    {
        var sut = CreateSut();

        var stockModel = (await sut.GetAsync("L100")).Data!;
        stockModel.StockControl = false;
        var stockResult = await sut.SaveAsync(stockModel, isNew: false);
        Assert.False(stockResult.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, stockResult.ErrorCode);

        var lotModel = (await sut.GetAsync("L120")).Data!;
        lotModel.LotControl = true;
        var lotResult = await sut.SaveAsync(lotModel, isNew: false);
        Assert.False(lotResult.Succeeded);
        Assert.Contains("operational purchase order", lotResult.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenPurchaseRequest_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L110");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains("open purchase request", result.Data.StructuralLockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LatestOperationalPo_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L120");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains(
            "operational purchase order with remaining quantity",
            result.Data.StructuralLockReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SupersededPoRevisionWithoutBalance_DoesNotLock()
    {
        var sut = CreateSut();
        var result = await sut.GetAsync("L121");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.StructuralFieldsLocked);

        var model = result.Data;
        model.StdUom = "PCS";
        model.SellingUom = "PCS";
        model.PurUom = "PCS";
        model.StdPackSize = null;
        model.PurStdPackSize = null;

        var save = await sut.SaveAsync(model, isNew: false);

        Assert.True(save.Succeeded, save.Message);
    }

    [Fact]
    public async Task OpenSalesOrder_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L130");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains(
            "open sales order with remaining quantity",
            result.Data.StructuralLockReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedSalesOrder_DoesNotLock()
    {
        var result = await CreateSut().GetAsync("L131");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.StructuralFieldsLocked);
    }

    [Fact]
    public async Task NonTerminalWorkOrderProduct_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L140");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains(
            "non-terminal production work order",
            result.Data.StructuralLockReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClosedWorkOrderProduct_DoesNotLock()
    {
        var result = await CreateSut().GetAsync("L141");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.StructuralFieldsLocked);
    }

    [Fact]
    public async Task NonTerminalWorkOrderMaterial_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L150");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains(
            "material of a non-terminal production work order",
            result.Data.StructuralLockReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PositiveProductionBalance_LocksStructure()
    {
        var result = await CreateSut().GetAsync("L160");

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.Data!.StructuralFieldsLocked);
        Assert.Contains("positive production balance", result.Data.StructuralLockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnusedItem_IsNotStructurallyLocked()
    {
        var result = await CreateSut().GetAsync("S500");

        Assert.True(result.Succeeded, result.Message);
        Assert.False(result.Data!.StructuralFieldsLocked);
        Assert.Null(result.Data!.StructuralLockReason);
    }

    // ---------------------------------------------------------------- helpers

    private static IvStockMaster Item(
        string code,
        string stdUom = "KG",
        string? sellingUom = null,
        string? purUom = null,
        bool stockControl = true,
        bool lotControl = false,
        decimal? stdPackSize = null,
        decimal? purStdPackSize = null) =>
        new()
        {
            CompanyCode = "DEMO",
            ICode = code,
            IDesc = code,
            IClassCode = "FG",
            IType = "FG",
            StdUom = stdUom,
            SellingUom = sellingUom ?? stdUom,
            PurUom = purUom ?? stdUom,
            StockControl = stockControl,
            LotControl = lotControl,
            StdPackSize = stdPackSize,
            PurStdPackSize = purStdPackSize,
            IsActive = true,
            SellingGlCode = "GLSALE",
            Classification = "001",
            RowVersion = RowVersionFor(code)
        };

    private static byte[] RowVersionFor(string code)
    {
        var seed = 0;
        foreach (var c in code)
        {
            seed = unchecked((seed * 31) + c);
        }

        return [(byte)(seed & 0xFF), 1, 2, 3, 4, 5, 6, 7];
    }

    private static ProductionWorkOrder WorkOrder(string number, string product, string status, long bomUid) =>
        new()
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WorkOrderNo = number,
            ProductCode = product,
            Status = status,
            SourceDefinitionCode = PrProductDefinitionCodes.Standard,
            SourceBomHdrId = bomUid,
            SourceBomVersion = 1,
            SourceEffectiveFrom = new DateTime(2026, 1, 1),
            SchedulingDirection = ProductionSchedulingDirections.Forward,
            PlannedStartDateTime = FixedToday,
            PlannedCompletionDateTime = FixedToday.AddDays(1),
            CreatedDate = FixedToday
        };

    private static IvItemUomConversion Conversion(
        string itemCode,
        string from,
        string to,
        decimal fromQty,
        decimal toQty) =>
        new()
        {
            CompanyCode = "DEMO",
            ItemCode = itemCode,
            FromUom = from,
            ToUom = to,
            FromQty = fromQty,
            ToQty = toQty,
            RoundingScale = 4,
            RoundingMode = IvUomRoundingModes.AwayFromZero,
            IsActive = true,
            RowVersion = RowVersionFor($"{itemCode}{from}{to}")
        };

    private static IvStockMasterEditVm NewModel(string code) =>
        new()
        {
            ICode = code,
            IDesc = "New item",
            IsActive = true,
            IType = "FG",
            IClassCode = "FG",
            StdUom = "KG",
            StockControl = true,
            LotControl = false,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1",
            SellingGlCode = "GLSALE",
            Classification = "001"
        };

    private static byte[] Rv(byte marker) => [marker, 0, 0, 0, 0, 0, 0, 0];

    private IvStockMasterService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new IvStockMasterService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory));
    }
}
