using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class SaSalesRefServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesRefServiceTests()
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
        db.SaCustTypes.Add(new SaCustType
        {
            CompanyCode = "DEMO",
            CustTypeCode = "RETAIL",
            CustTypeDesc = "Retail",
            IsActive = true,
            RowVersion = Rv(1)
        });
        db.SaCustGroups.Add(new SaCustGroup
        {
            CompanyCode = "DEMO",
            CustGroupCode = "GRP1",
            CustGroupDesc = "Group 1",
            RowVersion = Rv(2)
        });
        db.IvAreaCodes.Add(new()
        {
            CompanyCode = "DEMO",
            AreaCode = "KL",
            AreaDesc = "Kuala Lumpur"
        });
        db.SaCountries.Add(new() { CountryCode = "MY", CountryName = "Malaysia" });
        db.SaCurrencies.Add(new()
        {
            CompanyCode = "DEMO",
            CurrCode = "MYR",
            CurrDesc = "Ringgit",
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CustType_Create_StampLeftoverSite()
    {
        var sut = CreateSut();
        var result = await sut.SaveCustTypeAsync(new SaCustTypeEditVm
        {
            Code = "WHOLE",
            Desc = "Wholesale",
            IsActive = true
        }, isNew: true);

        Assert.True(result.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCustTypes.SingleAsync(x => x.CustTypeCode == "WHOLE");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task CustType_Duplicate_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveCustTypeAsync(new SaCustTypeEditVm
        {
            Code = "RETAIL",
            Desc = "Dup",
            IsActive = true
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    [Fact]
    public async Task CustType_Delete_Blocked_WhenCustomerReferences()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "C1",
                CustName = "Test",
                CustType = "RETAIL",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR",
                IsActive = true,
                RowVersion = Rv(10)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var check = await sut.CanDeleteCustTypesAsync(["RETAIL"]);
        Assert.False(check.CanDelete);
    }

    [Fact]
    public async Task CustType_Unauthorized_Add_Denied()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveCustTypeAsync(new SaCustTypeEditVm
        {
            Code = "X",
            Desc = "X",
            IsActive = true
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Area_Create_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveAreaAsync(new SaAreaEditVm
        {
            Code = "PJ",
            Desc = "Petaling Jaya"
        }, isNew: true);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Country_List_NotFilteredByCompany()
    {
        var sut = CreateSut(company: "OTHER");
        var result = await sut.ListCountriesAsync();
        Assert.True(result.Succeeded);
        Assert.Contains(result.Data!, x => x.Code == "MY");
    }

    [Fact]
    public async Task CurrencyRate_Overlap_Rejected()
    {
        var sut = CreateSut();
        var start = new DateTime(2026, 1, 1);
        var end = new DateTime(2026, 1, 31);
        var first = await sut.SaveCurrRateAsync(new SaCurrRateEditVm
        {
            CurrCode = "MYR",
            StartDate = start,
            EndDate = end,
            HomeCurPerUnit = 1.0,
            Status = true
        }, isNew: true);
        Assert.True(first.Succeeded);

        var overlap = await sut.SaveCurrRateAsync(new SaCurrRateEditVm
        {
            CurrCode = "MYR",
            StartDate = new DateTime(2026, 1, 31),
            EndDate = new DateTime(2026, 2, 28),
            HomeCurPerUnit = 1.1,
            Status = true
        }, isNew: true);

        Assert.False(overlap.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, overlap.ErrorCode);
    }

    [Fact]
    public async Task DisGroup_Save_WithMembers_RollsBack_OnInvalidCustomer()
    {
        var sut = CreateSut();
        var result = await sut.SaveDisGroupAsync(new SaDisGroupEditVm
        {
            GroupName = "G1",
            PayCode = "NET30",
            Members =
            [
                new SaDisGroupMemberVm { CustCode = "NOPE", CustName = "Missing" }
            ]
        }, isNew: true);

        Assert.False(result.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaDisGroups.AnyAsync(x => x.GroupName == "G1"));
    }

    private SaSalesRefService CreateSut(
        string company = "DEMO",
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true)
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

        return new SaSalesRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, "HQ", "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }

    private static byte[] Rv(int seed) => [0, 0, 0, 0, 0, 0, 0, (byte)seed];
}
