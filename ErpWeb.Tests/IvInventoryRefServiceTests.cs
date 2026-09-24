using ErpWeb.Core.EInvoice;
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

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryMasters)]
public class IvInventoryRefServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvInventoryRefServiceTests()
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
            WarehouseDesc = "Main",
            IsActive = true,
            RowVersion = [1]
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "EMPTY",
            WarehouseDesc = "Empty",
            IsActive = true,
            RowVersion = [2]
        });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "BR2",
            WarehouseCode = "OTHER",
            WarehouseDesc = "Other branch",
            IsActive = true,
            RowVersion = [9]
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            LocDesc = "Bin 1",
            IsActive = true,
            RowVersion = [3]
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = "DEMO",
            IClassCode = "FG",
            IDesc = "Finished",
            IsActive = true,
            RowVersion = [4]
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Warehouse_Duplicate_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm { Code = "MAIN", Desc = "Dup" },
            isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    [Fact]
    public async Task Warehouse_Create_StampLeftoverAndActiveFalse()
    {
        var sut = CreateSut();
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm
            {
                Code = "WHNEW",
                Desc = "New warehouse",
                IsActive = false
            },
            isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvWarehouses.SingleAsync(x =>
            x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "WHNEW");
        Assert.False(row.IsActive);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task Warehouse_Create_BlankLocation_InvalidScope()
    {
        var sut = CreateSut(location: null);
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm { Code = "X1", Desc = "X" },
            isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvWarehouses.AnyAsync(x => x.WarehouseCode == "X1"));
    }

    [Fact]
    public async Task Warehouse_Update_ImmutableCodeRejected()
    {
        var sut = CreateSut();
        var loaded = await sut.GetWarehouseAsync("MAIN");
        Assert.True(loaded.Succeeded);

        var model = loaded.Data!;
        model.Desc = "Changed desc";
        model.Code = "EMPTY";

        var result = await sut.SaveWarehouseAsync(model, isNew: false);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Warehouse_Delete_WithLocation_Blocked()
    {
        var sut = CreateSut();
        var list = await sut.ListWarehousesAsync();
        var main = list.Data!.Single(x => x.Code == "MAIN");
        var check = await sut.CanDeleteWarehousesAsync(["MAIN"]);
        Assert.False(check.CanDelete);

        var del = await sut.DeleteWarehousesAsync(
        [
            new IvMasterKeyToken { Code = "MAIN", RowVersion = main.RowVersion }
        ]);
        Assert.False(del.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, del.ErrorCode);
    }

    [Fact]
    public async Task Warehouse_Delete_Empty_Succeeds()
    {
        var sut = CreateSut();
        var list = await sut.ListWarehousesAsync();
        var empty = list.Data!.Single(x => x.Code == "EMPTY");
        var del = await sut.DeleteWarehousesAsync(
        [
            new IvMasterKeyToken { Code = "EMPTY", RowVersion = empty.RowVersion }
        ]);
        Assert.True(del.Succeeded, del.Message);
    }

    [Fact]
    public async Task Class_Create_WithSubclasses_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveClassAsync(
            new IvClassEditVm
            {
                Code = "RAW",
                Desc = "Raw",
                IsActive = true,
                SubClasses =
                [
                    new IvSubClassEditVm { Code = "RM1", Desc = "Steel", IsActive = false }
                ]
            },
            isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var sub = await db.IvSubClasses.SingleAsync(x =>
            x.CompanyCode == "DEMO" && x.IClassCode == "RAW" && x.ISubClassCode == "RM1");
        Assert.False(sub.IsActive);
        Assert.Equal("HQ", sub.BranchCode);
        Assert.Equal("SITE", sub.LocationCode);
    }

    [Fact]
    public async Task Location_MissingWarehouse_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveLocationAsync(
            new IvLocationEditVm
            {
                WarehouseCode = "NOSUCH",
                Code = "X1",
                Desc = "X"
            },
            isNew: true);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Status_Create_StampLeftoverSite()
    {
        var sut = CreateSut();
        var result = await sut.SaveStatusAsync(
            new IvStatusEditVm { Code = "QC", Desc = "QC Hold", IsActive = true },
            isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.IvStatuses.SingleAsync(x => x.CompanyCode == "DEMO" && x.IStatus == "QC");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task OversizedCompanyClaim_InvalidScope()
    {
        var sut = CreateSut(company: "COMPANY123");
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm { Code = "X", Desc = "X" },
            isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task Warehouse_Create_SameCodeOtherBranch_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm { Code = "OTHER", Desc = "HQ copy of other-branch code" },
            isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IvWarehouses.CountAsync(x =>
            x.CompanyCode == "DEMO" && x.WarehouseCode == "OTHER"));
    }

    [Fact]
    public async Task Uom_Create_DefaultsUneceUomToH87_WhenBlank()
    {
        var sut = CreateSut();
        var result = await sut.SaveUomAsync(
            new IvUomEditVm { Code = "PCS", Desc = "Piece", IsActive = true },
            isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(LhdnDefaults.UneceUom, result.Data!.UneceUom);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.MsUoms.SingleAsync(x => x.CompanyCode == "DEMO" && x.UomCode == "PCS");
        Assert.Equal("H87", row.UneceUom);
    }

    [Fact]
    public async Task Uom_Create_AcceptsACodeFromTheLhdnUnitList()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MsLhdnUoms.Add(new MsLhdnUom { Code = "KGM", Measurement = "kilogram" });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.SaveUomAsync(
            new IvUomEditVm { Code = "KG", Desc = "Kilogram", UneceUom = "kgm", IsActive = true },
            isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("kgm", result.Data!.UneceUom);
    }

    [Fact]
    public async Task Uom_Create_RejectsACodeOutsideTheLhdnUnitList_WithoutTruncating()
    {
        var sut = CreateSut();
        var result = await sut.SaveUomAsync(
            new IvUomEditVm { Code = "BOX", Desc = "Box", UneceUom = "BOX", IsActive = true },
            isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("UneceUom"));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.MsUoms.AnyAsync(x => x.CompanyCode == "DEMO" && x.UomCode == "BOX"));
    }

    [Fact]
    public async Task Uom_Create_RejectsAnUneceCodeLongerThanTheLhdnColumn()
    {
        var sut = CreateSut();
        var result = await sut.SaveUomAsync(
            new IvUomEditVm { Code = "LONG", Desc = "Too long", UneceUom = "ABCD", IsActive = true },
            isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("UneceUom"));
    }

    [Fact]
    public async Task Uom_List_OnlyOffersCodesFromTheLhdnTable()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.MsLhdnUoms.Add(new MsLhdnUom { Code = "C62", Measurement = "one" });
            db.MsLhdnUoms.Add(new MsLhdnUom { Code = "C62", Measurement = "duplicate" });
            db.MsLhdnUoms.Add(new MsLhdnUom { Code = "", Measurement = "blank" });
            await db.SaveChangesAsync();
        }

        var lookups = new IvInventoryLookupService(
            Mock.Of<ICurrentUserService>(x =>
                x.IsAuthenticated == true && x.CompanyCode == "DEMO" && x.BranchCode == "HQ"),
            Mock.Of<IIvStockMasterRepository>(),
            new IvStockCommonRepository(_factory),
            _factory);

        var result = await lookups.ListLhdnUomsAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("C62", Assert.Single(result.Rows).Code);
        Assert.Equal("one", result.Rows[0].Desc);
    }

    [Fact]
    public async Task Warehouse_Update_CrossBranch_NotFound()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var otherBranch = await db.IvWarehouses.SingleAsync(x =>
            x.CompanyCode == "DEMO" && x.BranchCode == "BR2" && x.WarehouseCode == "OTHER");

        var sut = CreateSut();
        var result = await sut.SaveWarehouseAsync(
            new IvWarehouseEditVm
            {
                Code = "OTHER",
                Desc = "Tamper attempt",
                RowVersion = otherBranch.RowVersion
            },
            isNew: false);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Warehouse_Delete_CrossBranch_NotFound()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var otherBranch = await db.IvWarehouses.SingleAsync(x =>
            x.CompanyCode == "DEMO" && x.BranchCode == "BR2" && x.WarehouseCode == "OTHER");

        var sut = CreateSut();
        var del = await sut.DeleteWarehousesAsync(
        [
            new IvMasterKeyToken { Code = "OTHER", RowVersion = otherBranch.RowVersion }
        ]);
        Assert.False(del.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, del.ErrorCode);
    }

    private IvInventoryRefService CreateSut(
        string company = "DEMO",
        string branch = "HQ",
        string? location = "SITE")
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new IvInventoryRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, branch, location),
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new IvStockCommonRepository(_factory));
    }
}
