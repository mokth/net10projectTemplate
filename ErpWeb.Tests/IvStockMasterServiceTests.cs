using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class IvStockMasterServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockMasterServiceTests()
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

        db.IvWarehouses.AddRange(
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                WarehouseDesc = "Main WH",
                IsActive = true,
                RowVersion = Rv(1)
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "INACTIVE",
                WarehouseDesc = "Inactive WH",
                IsActive = false,
                RowVersion = Rv(2)
            });

        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            LocDesc = "Bin 1",
            IsActive = true,
            RowVersion = Rv(3)
        });

        db.MsUoms.Add(new MsUom
        {
            CompanyCode = "DEMO",
            UomCode = "KG",
            UomDesc = "Kilogram",
            IsActive = true,
            RowVersion = Rv(4)
        });

        db.IvClasses.AddRange(
            new IvClass
            {
                CompanyCode = "DEMO",
                IClassCode = "FG",
                IDesc = "Finished",
                IsActive = true,
                RowVersion = Rv(5)
            },
            new IvClass
            {
                CompanyCode = "DEMO",
                IClassCode = "RAW",
                IDesc = "Raw",
                IsActive = true,
                RowVersion = Rv(6)
            });

        db.IvSubClasses.AddRange(
            new IvSubClass
            {
                CompanyCode = "DEMO",
                IClassCode = "FG",
                ISubClassCode = "ASH",
                ISubClassName = "Ash",
                IsActive = true,
                RowVersion = Rv(7)
            },
            new IvSubClass
            {
                CompanyCode = "DEMO",
                IClassCode = "RAW",
                ISubClassCode = "ORE",
                ISubClassName = "Ore",
                IsActive = true,
                RowVersion = Rv(8)
            });

        db.IvTypes.Add(new IvType
        {
            CompanyCode = "DEMO",
            TypeCode = "FG",
            TypeName = "Finished Goods",
            KeepStock = true,
            IsActive = true,
            RowVersion = Rv(9)
        });

        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = "DEMO",
            IStatus = "ACTIVE",
            StatusDesc = "Active",
            IsActive = true,
            RowVersion = Rv(10)
        });

        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Unused item",
                IClassCode = "FG",
                ISubClassCode = "ASH",
                IType = "FG",
                StdUom = "KG",
                StockControl = true,
                LotControl = false,
                IsActive = true,
                BranchCode = "LEFTOVER",
                RowVersion = Rv(11)
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "B200",
                IDesc = "Used item",
                IClassCode = "FG",
                ISubClassCode = "ASH",
                IType = "FG",
                StdUom = "KG",
                StockControl = true,
                LotControl = true,
                IsActive = true,
                RowVersion = Rv(12)
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "C300",
                IDesc = "Inactive item",
                IClassCode = "FG",
                ISubClassCode = "ASH",
                IType = "FG",
                StdUom = "KG",
                StockControl = true,
                IsActive = false,
                RowVersion = Rv(13)
            },
            new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "OTHER1",
                IDesc = "Other company item",
                IClassCode = "FG",
                StdUom = "KG",
                IsActive = true,
                RowVersion = Rv(14)
            });

        db.IvLots.Add(new IvLot
        {
            CompanyCode = "DEMO",
            ICode = "B200",
            LotNo = "LOT-B200-1",
            IsActive = true
        });

        await db.SaveChangesAsync();

        var lotId = await db.IvLots
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "B200")
            .Select(x => x.Id)
            .SingleAsync();

        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "B200",
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = "LOT-B200-1",
            LotId = lotId,
            IStatus = "ACTIVE",
            StdQty = 5,
            StdUom = "KG",
            RowVersion = Rv(15)
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Create_StampLeftoverFromClaims()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("D400"), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "D400");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
        Assert.Equal("New item", row.IDesc);
    }

    [Fact]
    public async Task Update_PreservesLeftoverBranchCode()
    {
        var sut = CreateSut();
        var loaded = await sut.GetAsync("A100");
        Assert.True(loaded.Succeeded, loaded.Message);

        var model = loaded.Data!;
        model.IDesc = "Updated unused";
        var result = await sut.SaveAsync(model, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100");
        Assert.Equal("LEFTOVER", row.BranchCode);
        Assert.Equal("Updated unused", row.IDesc);
    }

    [Fact]
    public async Task Create_SameCodeOtherCompany_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("OTHER1"), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IvStockMasters.CountAsync(x => x.ICode == "OTHER1"));
    }

    [Fact]
    public async Task Update_CrossCompany_NotFound()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var other = await db.IvStockMasters.SingleAsync(x =>
            x.CompanyCode == "OTHER" && x.ICode == "OTHER1");

        var sut = CreateSut();
        var result = await sut.SaveAsync(new IvStockMasterEditVm
        {
            ICode = "OTHER1",
            IDesc = "Tamper attempt",
            IClassCode = "FG",
            IType = "FG",
            StdUom = "KG",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            RowVersion = other.RowVersion
        }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task DuplicateCode_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("A100"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("ICode"));
    }

    [Fact]
    public async Task Get_WrongCompany_NotFound()
    {
        var sut = CreateSut();
        var result = await sut.GetAsync("OTHER1");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task LotControl_WithoutStockControl_Fails()
    {
        var sut = CreateSut();
        var model = ValidNewModel("E500");
        model.StockControl = false;
        model.LotControl = true;

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("LotControl"));
    }

    [Fact]
    public async Task MinGreaterThanMax_Fails()
    {
        var sut = CreateSut();
        var model = ValidNewModel("E501");
        model.MinStock = 20;
        model.MaxStock = 10;

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("MinStock"));
    }

    [Fact]
    public async Task SubclassNotInClass_Fails()
    {
        var sut = CreateSut();
        var model = ValidNewModel("E502");
        model.IClassCode = "FG";
        model.ISubClassCode = "ORE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("ISubClassCode"));
    }

    [Fact]
    public async Task InactiveLookup_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("E503");
        model.DefWarehouse = "INACTIVE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("DefWarehouse"));
    }

    [Fact]
    public async Task Activate_Deactivate_Works()
    {
        var sut = CreateSut();
        var a100 = (await sut.GetAsync("A100")).Data!;
        var c300 = (await sut.GetAsync("C300")).Data!;

        var deactivate = await sut.SetActiveAsync(
            [Token(a100.ICode, a100.RowVersion!)],
            isActive: false);
        Assert.True(deactivate.Succeeded, deactivate.Message);

        var activate = await sut.SetActiveAsync(
            [Token(c300.ICode, c300.RowVersion!)],
            isActive: true);
        Assert.True(activate.Succeeded, activate.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "A100")
            .Select(x => x.IsActive)
            .SingleAsync());
        Assert.True(await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "C300")
            .Select(x => x.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task Delete_Unused_Succeeds()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("A100")).Data!;

        var result = await sut.DeleteAsync([Token(loaded.ICode, loaded.RowVersion!)]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100"));
    }

    [Fact]
    public async Task Delete_Used_Blocked_WithReferences()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("B200")).Data!;

        var result = await sut.DeleteAsync([Token(loaded.ICode, loaded.RowVersion!)]);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, result.ErrorCode);
        Assert.NotNull(result.DeleteCheck);
        Assert.False(result.DeleteCheck!.CanDelete);
        Assert.NotEmpty(result.DeleteCheck.References);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "B200"));
    }

    [Fact]
    public async Task BulkSetActive_OneStaleToken_ChangesNone()
    {
        var sut = CreateSut();
        var a100 = (await sut.GetAsync("A100")).Data!;
        var c300 = (await sut.GetAsync("C300")).Data!;

        var stale = (byte[])c300.RowVersion!.Clone();
        stale[0] ^= 0xFF;

        var result = await sut.SetActiveAsync(
            [
                Token(a100.ICode, a100.RowVersion!),
                Token(c300.ICode, stale)
            ],
            isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "A100")
            .Select(x => x.IsActive)
            .SingleAsync());
        Assert.False(await db.IvStockMasters
            .Where(x => x.CompanyCode == "DEMO" && x.ICode == "C300")
            .Select(x => x.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task BulkDelete_OneInUse_DeletesNone()
    {
        var sut = CreateSut();
        var a100 = (await sut.GetAsync("A100")).Data!;
        var b200 = (await sut.GetAsync("B200")).Data!;

        var result = await sut.DeleteAsync(
        [
            Token(a100.ICode, a100.RowVersion!),
            Token(b200.ICode, b200.RowVersion!)
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100"));
        Assert.True(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "B200"));
    }

    [Fact]
    public async Task Unauthorized_Add_Denied()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveAsync(ValidNewModel("E504"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
        Assert.Equal("Not authorized.", result.Message);
    }

    [Fact]
    public async Task Copy_IsNormalInsert()
    {
        var sut = CreateSut();
        var source = (await sut.GetAsync("A100")).Data!;

        var copy = new IvStockMasterEditVm
        {
            ICode = "COPY1",
            IDesc = source.IDesc,
            Barcode = source.Barcode,
            Brand = source.Brand,
            IsActive = true,
            IType = source.IType,
            IClassCode = source.IClassCode,
            ISubClassCode = source.ISubClassCode,
            StdUom = source.StdUom,
            SellingUom = source.SellingUom,
            PurUom = source.PurUom,
            StockControl = source.StockControl,
            LotControl = source.LotControl,
            DefWarehouse = source.DefWarehouse,
            DefLocation = source.DefLocation,
            MinStock = source.MinStock,
            MaxStock = source.MaxStock,
            SellingPrice = source.SellingPrice,
            PurchasePrice = source.PurchasePrice,
            RowVersion = null
        };

        var result = await sut.SaveAsync(copy, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("COPY1", result.Data!.ICode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "COPY1"));
        Assert.True(await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100"));
    }

    [Fact]
    public async Task SortField_Invalid_FallsBackToICode()
    {
        var sut = CreateSut();
        var result = await sut.SearchAsync(new IvStockMasterListQuery
        {
            SortField = "HACK",
            Take = 50
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(result.Data);
        Assert.True(result.Data!.TotalCount >= 3);
        var codes = result.Data.Rows.Select(x => x.ICode).ToList();
        Assert.Equal(codes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), codes);
    }

    [Fact]
    public async Task CanDeleteBulk_ReportsUsedItem()
    {
        var sut = CreateSut();
        var check = await sut.CanDeleteBulkAsync(["A100", "B200"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, r => r.Detail == "B200");
        Assert.DoesNotContain(check.References, r => r.Detail == "A100");
    }

    private static IvStockMasterEditVm ValidNewModel(string code) =>
        new()
        {
            ICode = code,
            IDesc = "New item",
            IsActive = true,
            IType = "FG",
            IClassCode = "FG",
            ISubClassCode = "ASH",
            StdUom = "KG",
            StockControl = true,
            LotControl = false,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1"
        };

    private static IvMasterKeyToken Token(string code, byte[] rowVersion) =>
        new() { Code = code, RowVersion = rowVersion };

    private static byte[] Rv(byte marker) => [marker, 0, 0, 0, 0, 0, 0, 0];

    private IvStockMasterService CreateSut(
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canExport = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Add,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Edit,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Delete,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryItemMaster,
                PermissionCodes.Export,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canExport);

        return new IvStockMasterService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory));
    }
}
