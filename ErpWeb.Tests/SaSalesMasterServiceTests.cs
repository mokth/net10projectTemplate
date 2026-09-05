using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class SaSalesMasterServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 4);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSalesMasterServiceTests()
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
        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "DEMO",
            PayCode = "NET30",
            PayDesc = "Net 30",
            Days = 30,
            IsActive = true
        });
        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "OTHER",
            PayCode = "NET30",
            PayDesc = "Other company",
            Days = 15,
            IsActive = true
        });
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO",
            SrepCode = "SM1",
            SrepName = "Rep One",
            CommissionRate = 5.5m,
            IsActive = true
        });
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "OTHER",
            SrepCode = "SM1",
            SrepName = "Other Rep",
            IsActive = true
        });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "STD",
            TaxGrDesc = "Standard",
            Percentage = 6m
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task PaymentTerm_Create_Succeeds_And_StampsLeftover()
    {
        var sut = CreateSut();
        var result = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "cod",
            Desc = "Cash on delivery",
            Days = 0,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        Assert.True(result.Succeeded);
        Assert.Equal("COD", result.Data!.Code);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaPaymentTerms.SingleAsync(x => x.CompanyCode == "DEMO" && x.PayCode == "COD");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task PaymentTerm_Create_Duplicate_AnyAsync_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "net30",
            Desc = "Dup",
            Days = 30,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_Create_NormalizedDuplicate_Rejected()
    {
        var sut = CreateSut();
        var first = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "abc",
            Desc = "First",
            Days = 1,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);
        Assert.True(first.Succeeded);

        var second = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "ABC",
            Desc = "Second",
            Days = 2,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        Assert.False(second.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, second.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_EmptyCompany_InvalidScope()
    {
        var sut = CreateSut(company: "");
        var result = await sut.ListPaymentTermsAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task TaxGroup_EmptyCompany_InvalidScope()
    {
        var sut = CreateSut(company: "");
        var result = await sut.ListTaxGroupsAsync();
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_CrossCompany_Get_NotFound()
    {
        var sut = CreateSut(company: "DEMO");
        // OTHER company row exists with NET30; DEMO also has NET30 — cross-company means
        // looking for a code that only exists in OTHER under a different key context.
        // Use a code only on OTHER via Get after switching company.
        var other = CreateSut(company: "OTHER");
        await other.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "ONLYO",
            Desc = "Only other",
            Days = 7,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        var get = await sut.GetPaymentTermAsync("ONLYO");
        Assert.False(get.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, get.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_CrossCompany_Activate_NoMutate()
    {
        var sut = CreateSut(company: "DEMO");
        var result = await sut.SetPaymentTermActiveAsync(["ONLYO"], isActive: false);
        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        var other = await db.SaPaymentTerms.AsNoTracking()
            .SingleAsync(x => x.CompanyCode == "OTHER" && x.PayCode == "NET30");
        Assert.True(other.IsActive);
    }

    [Fact]
    public async Task PaymentTerm_Update_MutableFields_CodeUnchanged()
    {
        var sut = CreateSut();
        var get = await sut.GetPaymentTermAsync("NET30");
        Assert.True(get.Succeeded);
        var fp = SaMasterFingerprint.PaymentTerm(get.Data!);

        var save = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "RENAMED",
            Desc = "Updated desc",
            Days = 45,
            IsActive = false
        }, isNew: false, expectedFingerprint: fp);

        // Lookup uses normalized Code from VM — RENAMED not found
        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, save.ErrorCode);

        // Correct update keeps code
        var saveOk = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "NET30",
            Desc = "Updated desc",
            Days = 45,
            IsActive = false
        }, isNew: false, expectedFingerprint: fp);
        Assert.True(saveOk.Succeeded);
        Assert.Equal("NET30", saveOk.Data!.Code);
        Assert.Equal("Updated desc", saveOk.Data.Desc);
        Assert.Equal(45, saveOk.Data.Days);
        Assert.False(saveOk.Data.IsActive);
    }

    [Fact]
    public async Task PaymentTerm_StaleFingerprint_Concurrency()
    {
        var sut = CreateSut();
        var get = await sut.GetPaymentTermAsync("NET30");
        var fp = SaMasterFingerprint.PaymentTerm(get.Data!);

        // Concurrent change
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.SaPaymentTerms.SingleAsync(x => x.CompanyCode == "DEMO" && x.PayCode == "NET30");
            row.PayDesc = "Changed elsewhere";
            await db.SaveChangesAsync();
        }

        var save = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "NET30",
            Desc = "My change",
            Days = 30,
            IsActive = true
        }, isNew: false, expectedFingerprint: fp);

        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_MissingFingerprint_Concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "NET30",
            Desc = "X",
            Days = 30,
            IsActive = true
        }, isNew: false, expectedFingerprint: null);

        Assert.False(save.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, save.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_Fingerprint_Matches_GetAndTrackedMap()
    {
        var sut = CreateSut();
        var get = await sut.GetPaymentTermAsync("NET30");
        Assert.True(get.Succeeded);
        var fromGet = SaMasterFingerprint.PaymentTerm(get.Data!);

        await using var db = await _factory.CreateDbContextAsync();
        var tracked = await db.SaPaymentTerms.SingleAsync(x => x.CompanyCode == "DEMO" && x.PayCode == "NET30");
        var fromTracked = SaMasterFingerprint.PaymentTerm(new SaPaymentTermEditVm
        {
            Code = tracked.PayCode,
            Desc = tracked.PayDesc,
            Days = tracked.Days,
            IsActive = tracked.IsActive != false
        });
        Assert.Equal(fromGet, fromTracked);
    }

    [Fact]
    public async Task PaymentTerm_Delete_Succeeds_WhenUnreferenced()
    {
        var sut = CreateSut();
        await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "TEMP",
            Desc = "Temp",
            Days = 1,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        var check = await sut.CanDeletePaymentTermsAsync(["TEMP"]);
        Assert.True(check.CanDelete);

        var del = await sut.DeletePaymentTermsAsync(["TEMP"]);
        Assert.True(del.Succeeded);
    }

    [Fact]
    public async Task PaymentTerm_Delete_InUse_WhenReferenced()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "C1",
                CustName = "Cust",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR",
                IsActive = true,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var check = await sut.CanDeletePaymentTermsAsync(["NET30"]);
        Assert.False(check.CanDelete);

        var del = await sut.DeletePaymentTermsAsync(["NET30"]);
        Assert.False(del.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, del.ErrorCode);
    }

    [Fact]
    public async Task PaymentTerm_DaysNegative_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SavePaymentTermAsync(new SaPaymentTermEditVm
        {
            Code = "NEG",
            Desc = "Bad",
            Days = -1,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Days"));
    }

    [Fact]
    public async Task PaymentTerm_List_ReturnsIReadOnlyList_NoPaging()
    {
        var sut = CreateSut();
        var result = await sut.ListPaymentTermsAsync();
        Assert.True(result.Succeeded);
        Assert.IsAssignableFrom<IReadOnlyList<SaPaymentTermListRow>>(result.Data);
        Assert.Contains(result.Data!, x => x.Code == "NET30");
    }

    [Fact]
    public async Task SalesRep_CommissionScale_ExcessRejected_ExactAllowed()
    {
        var sut = CreateSut();
        var bad = await sut.SaveSalesRepAsync(new SaSalesRepEditVm
        {
            Code = "SMX",
            Name = "Scale Bad",
            CommissionRate = 1.1234567m,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);
        Assert.False(bad.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, bad.ErrorCode);

        var good = await sut.SaveSalesRepAsync(new SaSalesRepEditVm
        {
            Code = "SMY",
            Name = "Scale Good",
            CommissionRate = 1.123456m,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);
        Assert.True(good.Succeeded);
    }

    [Fact]
    public async Task SalesRep_CommissionOutOfRange_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveSalesRepAsync(new SaSalesRepEditVm
        {
            Code = "SMZ",
            Name = "Too high",
            CommissionRate = 100.1m,
            IsActive = true
        }, isNew: true, expectedFingerprint: null);
        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("CommissionRate"));
    }

    [Fact]
    public async Task TaxGroup_PercentageScaleAndRange()
    {
        var sut = CreateSut();
        var excess = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "TX1",
            Desc = "Bad scale",
            Percentage = 1.1234567m
        }, isNew: true, expectedFingerprint: null);
        Assert.False(excess.Succeeded);

        var over = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "TX2",
            Desc = "Over",
            Percentage = 101m
        }, isNew: true, expectedFingerprint: null);
        Assert.False(over.Succeeded);

        var ok = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "TX3",
            Desc = "Zero ok",
            Percentage = 0m
        }, isNew: true, expectedFingerprint: null);
        Assert.True(ok.Succeeded);
    }

    [Fact]
    public async Task TaxGroup_Create_Succeeds_And_StampsLeftover()
    {
        var sut = CreateSut();
        var result = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "gst",
            Desc = "GST 6",
            Percentage = 6m
        }, isNew: true, expectedFingerprint: null);

        Assert.True(result.Succeeded);
        Assert.Equal("GST", result.Data!.Code);
        Assert.Equal("DEMO", result.Data.CompanyCode);
        Assert.Equal("HQ", result.Data.BranchCode);
        Assert.Equal("SITE", result.Data.LocationCode);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaTaxGroups.SingleAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "GST");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task TaxGroup_List_FilteredByCompany()
    {
        var demo = CreateSut(company: "DEMO");
        var other = CreateSut(company: "OTHER");

        await demo.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "GLOB",
            Desc = "Demo only",
            Percentage = 8m
        }, isNew: true, expectedFingerprint: null);

        var otherList = await other.ListTaxGroupsAsync();
        Assert.True(otherList.Succeeded);
        Assert.DoesNotContain(otherList.Data!, x => x.Code == "GLOB");
        Assert.DoesNotContain(otherList.Data!, x => x.Code == "STD");

        var demoList = await demo.ListTaxGroupsAsync();
        Assert.True(demoList.Succeeded);
        Assert.Contains(demoList.Data!, x => x.Code == "GLOB");
        Assert.Contains(demoList.Data!, x => x.Code == "STD");
    }

    [Fact]
    public async Task TaxGroup_CrossCompany_Get_NotFound()
    {
        var other = CreateSut(company: "OTHER");
        await other.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "ONLYO",
            Desc = "Only other",
            Percentage = 5m
        }, isNew: true, expectedFingerprint: null);

        var sut = CreateSut(company: "DEMO");
        var get = await sut.GetTaxGroupAsync("ONLYO");
        Assert.False(get.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, get.ErrorCode);
    }

    [Fact]
    public async Task TaxGroup_Update_FingerprintAndImmutableCode()
    {
        var sut = CreateSut();
        var get = await sut.GetTaxGroupAsync("STD");
        var fp = SaMasterFingerprint.TaxGroup(get.Data!);

        var save = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "STD",
            Desc = "Updated tax",
            Percentage = 10m
        }, isNew: false, expectedFingerprint: fp);
        Assert.True(save.Succeeded);
        Assert.Equal("STD", save.Data!.Code);
        Assert.Equal(10m, save.Data.Percentage);

        var stale = await sut.SaveTaxGroupAsync(new SaTaxGroupEditVm
        {
            Code = "STD",
            Desc = "Again",
            Percentage = 11m
        }, isNew: false, expectedFingerprint: fp);
        Assert.False(stale.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, stale.ErrorCode);
    }

    [Fact]
    public async Task SalesRep_Delete_InUse_WhenCustomerReferences()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "C2",
                CustName = "Cust2",
                SalesmanCode = "SM1",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR",
                IsActive = true,
                RowVersion = [2]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var del = await sut.DeleteSalesRepsAsync(["SM1"]);
        Assert.False(del.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, del.ErrorCode);
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
}
