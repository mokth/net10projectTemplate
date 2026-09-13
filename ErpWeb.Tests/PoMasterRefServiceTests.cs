using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class PoMasterRefServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 11);
    private static readonly byte[] Rv1 = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Rv2 = [2, 2, 3, 4, 5, 6, 7, 8];

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoMasterRefServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PoCategories.Add(new PoCategory
        {
            CompanyCode = "DEMO",
            Category = "RAW",
            Description = "Raw material",
            IsActive = true,
            RowVersion = Rv1
        });
        db.PoBuyers.Add(new PoBuyer
        {
            CompanyCode = "DEMO",
            BuyerCode = "B1",
            BuyerName = "Buyer One",
            IsActive = true,
            RowVersion = Rv1
        });
        db.PoBuyers.Add(new PoBuyer
        {
            CompanyCode = "OTHER",
            BuyerCode = "OX",
            BuyerName = "Other Co Buyer",
            IsActive = true,
            RowVersion = Rv1
        });
        db.PoBuyingTerms.Add(new PoBuyingTerm
        {
            CompanyCode = "DEMO",
            BuyingTerm = "FOB",
            Description = "Free on board",
            IsActive = true,
            RowVersion = Rv1
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP-FOB",
            SuppName = "Uses FOB",
            Currency = "MYR",
            BuyingTerm = "FOB",
            IsActive = true,
            RowVersion = Rv1
        });
        db.PoPurItems.Add(new PoPurItem
        {
            CompanyCode = "DEMO",
            ICode = "ITEM1",
            Vendor = "V001",
            VendName = "Vendor One",
            Moq = 1,
            RowVersion = Rv1
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Category_List_ReturnsOnlyCurrentCompany()
    {
        var sut = CreateSut();
        var result = await sut.ListCategoriesAsync();
        Assert.True(result.Succeeded);
        Assert.Single(result.Data!);
        Assert.Equal("RAW", result.Data![0].Code);
    }

    [Fact]
    public async Task Category_Create_StampsTenant_AndPreservesCasing()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(
            new PoCategoryEditVm { Code = "fin", Description = "Finished", IsActive = true }, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PoCategories.SingleAsync(x => x.CompanyCode == "DEMO" && x.Category == "fin");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
        Assert.Equal("fin", row.Category);
    }

    [Fact]
    public async Task Category_Create_Duplicate_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(new PoCategoryEditVm { Code = "RAW" }, isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    // Case-insensitive duplicate ("raw" vs "RAW") is asserted on SQL Server (CI collation).
    // SQLite default BINARY collation treats them as distinct via EF ==.

    [Fact]
    public async Task Category_Update_StaleRowVersion_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(
            new PoCategoryEditVm { Code = "RAW", Description = "Changed", RowVersion = [9, 9] }, isNew: false);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Category_Update_WithToken_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveCategoryAsync(
            new PoCategoryEditVm { Code = "RAW", Description = "Changed", IsActive = true, RowVersion = Rv1 },
            isNew: false);
        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Changed", result.Data!.Description);
    }

    [Fact]
    public async Task Buyer_Crud_RoundTrip()
    {
        var sut = CreateSut();
        var created = await sut.SaveBuyerAsync(
            new PoBuyerEditVm { Code = "B2", Name = "Two", Desc = "D", IsActive = true }, isNew: true);
        Assert.True(created.Succeeded, created.Message);

        var loaded = await sut.GetBuyerAsync("B2");
        Assert.True(loaded.Succeeded);
        Assert.Equal("Two", loaded.Data!.Name);

        loaded.Data.Name = "Two Updated";
        var updated = await sut.SaveBuyerAsync(loaded.Data, isNew: false);
        Assert.True(updated.Succeeded, updated.Message);
        Assert.Equal("Two Updated", updated.Data!.Name);

        var deactivate = await sut.SetBuyerActiveAsync(
            [new IvMasterKeyToken { Code = "B2", RowVersion = updated.Data.RowVersion! }], isActive: false);
        Assert.True(deactivate.Succeeded, deactivate.Message);

        var after = await sut.GetBuyerAsync("B2");
        Assert.False(after.Data!.IsActive);
    }

    [Theory]
    [InlineData("", "V1", "ICode")]
    [InlineData("   ", "V1", "ICode")]
    [InlineData("I1", "", "Vendor")]
    [InlineData("I1", "   ", "Vendor")]
    public async Task PurItem_RequiredFields_Rejected(string iCode, string vendor, string field)
    {
        var sut = CreateSut();
        var result = await sut.SavePurItemAsync(
            new PoPurItemEditVm { ICode = iCode, Vendor = vendor }, isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey(field));
    }

    [Fact]
    public async Task PurItem_DuplicateCreate_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SavePurItemAsync(
            new PoPurItemEditVm { ICode = "ITEM1", Vendor = "V001" }, isNew: true);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("ICode"));
    }

    [Fact]
    public async Task PurItem_DuplicateEdit_Rejected_SelfSaveOk()
    {
        var sut = CreateSut();
        var createB = await sut.SavePurItemAsync(
            new PoPurItemEditVm { ICode = "ITEM2", Vendor = "V002", Moq = 1 }, isNew: true);
        Assert.True(createB.Succeeded, createB.Message);

        var b = createB.Data!;
        b.ICode = "ITEM1";
        b.Vendor = "V001";
        var conflict = await sut.SavePurItemAsync(b, isNew: false);
        Assert.False(conflict.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, conflict.ErrorCode);

        var itemA = (await sut.ListPurItemsAsync()).Data!.Single(x => x.ICode == "ITEM1" && x.Vendor == "V001");
        var editA = (await sut.GetPurItemAsync(itemA.Id.ToString())).Data!;
        var self = await sut.SavePurItemAsync(editA, isNew: false);
        Assert.True(self.Succeeded, self.Message);
    }

    [Fact]
    public async Task BuyingTerm_Delete_Phase1_IgnoresSupplierReference()
    {
        // Dependency checks are deferred — supplier still references FOB.
        var sut = CreateSut();
        var can = await sut.CanDeleteBuyingTermsAsync(["FOB"]);
        Assert.True(can.CanDelete);

        var deleted = await sut.DeleteBuyingTermsAsync(
            [new IvMasterKeyToken { Code = "FOB", RowVersion = Rv1 }]);
        Assert.True(deleted.Succeeded, deleted.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.PoBuyingTerms.AnyAsync(x => x.CompanyCode == "DEMO" && x.BuyingTerm == "FOB"));
        Assert.True(await db.PoSuppliers.AnyAsync(x => x.BuyingTerm == "FOB"));
    }

    [Fact]
    public async Task ExportBuyers_WithoutPermission_AccessDenied()
    {
        var sut = CreateSut(canExport: false);
        var result = await sut.ExportBuyersAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task ExportBuyers_WithPermission_ExcludesOtherCompany()
    {
        var sut = CreateSut();
        var result = await sut.ExportBuyersAsync();
        Assert.True(result.Succeeded, result.Message);
        Assert.Contains(result.Data!, x => x.Code == "B1");
        Assert.DoesNotContain(result.Data!, x => x.Code == "OX");
    }

    [Fact]
    public async Task ExportCategories_WrongMenuExport_AccessDenied()
    {
        // Only PO_BUYER EXPORT granted — PO_CATEGORY EXPORT must still fail.
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseBuyer, PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseCategory, PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var sut = new PoMasterRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));

        var result = await sut.ExportCategoriesAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public void ExportWorkbook_Buyers_HeadersAndCompanyScope()
    {
        var bytes = PoMasterRefExportWorkbooks.BuildBuyers(
        [
            new PoBuyerListRow { Code = "B1", Name = "Buyer One", Desc = "D", IsActive = true },
        ]);
        var headers = PoMasterRefExportWorkbooks.ReadHeaderRow(bytes);
        Assert.Equal(PoMasterRefExportWorkbooks.BuyerHeaders, headers);

        var data = PoMasterRefExportWorkbooks.ReadDataRows(bytes);
        Assert.Single(data);
        Assert.Equal("B1", data[0][0]);
        Assert.DoesNotContain(data, row => row[0] == "OX");
    }

    [Fact]
    public void ExportWorkbook_PurItems_Headers()
    {
        var bytes = PoMasterRefExportWorkbooks.BuildPurItems(
        [
            new PoPurItemListRow
            {
                Id = 1,
                ICode = "ITEM1",
                IDesc = "Desc",
                Category = "RAW",
                Vendor = "V001",
                VendName = "Vendor One",
                Currency = "MYR",
                UnitPrice = 1.5m,
                Moq = 2,
                Status = "OK"
            }
        ]);
        Assert.Equal(PoMasterRefExportWorkbooks.PurItemHeaders, PoMasterRefExportWorkbooks.ReadHeaderRow(bytes));
    }

    [Fact]
    public async Task Delete_RequiresPermission()
    {
        var sut = CreateSut(canDelete: false);
        var result = await sut.DeleteCategoriesAsync(
            [new IvMasterKeyToken { Code = "RAW", RowVersion = Rv1 }]);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    private PoMasterRefService CreateSut(
        string company = "DEMO",
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canExport = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canExport);

        return new PoMasterRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }
}
