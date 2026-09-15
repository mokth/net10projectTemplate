using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

public class SaCustServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaCustServiceTests()
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
            CustGroupDesc = "Group 1"
        });

        // TC34 / D-6: sub-group masters. Company B has its own SG1 so the reference check stays
        // tenant-scoped rather than global.
        db.SaCustSubGroups.Add(new SaCustSubGroup
        {
            CompanyCode = "DEMO",
            CustSubGroupCode = "SG1",
            CustSubGroupDesc = "Sub Group 1"
        });
        db.SaCustSubGroups.Add(new SaCustSubGroup
        {
            CompanyCode = "OTHER",
            CustSubGroupCode = "SG1",
            CustSubGroupDesc = "Sub Group 1 (other company)"
        });
        db.SaCustSubGroups.Add(new SaCustSubGroup
        {
            CompanyCode = "OTHER",
            CustSubGroupCode = "SG9",
            CustSubGroupDesc = "Other company only"
        });

        db.SaCurrencies.Add(new SaCurrency
        {
            CompanyCode = "DEMO",
            CurrCode = "MYR",
            CurrDesc = "Ringgit",
            IsActive = true
        });

        db.SaCountries.Add(new SaCountry { CountryCode = "MY", CountryName = "Malaysia" });
        db.SaCountries.Add(new SaCountry { CountryCode = "SG", CountryName = "Singapore" });

        db.IvMsCodes.AddRange(
            new IvMsCode { Code = "SEL", Name = "Selangor", CodeType = IvMsCodeTypes.State },
            new IvMsCode { Code = "SR", Name = "Standard Rated", CodeType = IvMsCodeTypes.Tax },
            new IvMsCode { Code = "NET30", Name = "Net 30 days", CodeType = IvMsCodeTypes.PayCode },
            new IvMsCode { Code = "COD", Name = "Cash on delivery", CodeType = IvMsCodeTypes.PayCode },
            new IvMsCode { Code = "ELEC", Name = "Electronics", CodeType = IvMsCodeTypes.Industry },
            new IvMsCode { Code = "OEM", Name = "OEM", CodeType = IvMsCodeTypes.Channel });

        db.SaCusts.AddRange(
            new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST01",
                CustName = "Alpha Customer",
                CustType = "RETAIL",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR001",
                Tel = "111",
                City = "KL",
                Country = "MY",
                AppShip = true,
                IsActive = true,
                BranchCode = "LEFTOVER",
                RowVersion = Rv(10)
            },
            new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST02",
                CustName = "Beta Customer",
                CustType = "RETAIL",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR002",
                Address1 = "Line 1",
                City = "PJ",
                Tel = "222",
                Country = "MY",
                AppInvoice = true,
                AppShip = true,
                InvName = "Beta Customer",
                InvAddress1 = "Line 1",
                InvCity = "PJ",
                InvTel = "222",
                ContactPerson = "Alice",
                IsActive = true,
                // Legacy free text that no master row matches: must not block an unrelated edit.
                SubGroupCode = "LEGACY",
                SalesmanCode = "LEGACYREP",
                RowVersion = Rv(11)
            },
            new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST03",
                CustName = "Inactive Customer",
                CustType = "RETAIL",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR003",
                Country = "MY",
                AppShip = true,
                IsActive = false,
                RowVersion = Rv(12)
            },
            new SaCust
            {
                CompanyCode = "OTHER",
                CustCode = "SHARED",
                CustName = "Other Company Shared Code",
                CustType = "RETAIL",
                PayCode = "NET30",
                Currency = "MYR",
                GlCode = "AR999",
                Country = "MY",
                AppShip = true,
                IsActive = true,
                RowVersion = Rv(13)
            });

        await db.SaveChangesAsync();

        db.SaCustAdds.AddRange(
            new SaCustAdd
            {
                CompanyCode = "DEMO",
                CustCode = "CUST02",
                Line = 1,
                AddName = "HQ",
                Address1 = "Addr 1",
                City = "PJ"
            },
            new SaCustAdd
            {
                CompanyCode = "DEMO",
                CustCode = "CUST02",
                Line = 2,
                AddName = "Branch",
                Address1 = "Addr 2",
                City = "KL"
            });

        db.SaCustContacts.Add(new SaCustContact
        {
            CompanyCode = "DEMO",
            CustCode = "CUST02",
            Line = 1,
            ContactPerson = "Bob",
            ContactEmail = "bob@example.com"
        });

        // Sales-master lookups: SR2 carries the legacy NULL IsActive, which means ACTIVE (the shipped
        // post-time salesman checks reject only "rep is null || rep.IsActive == false").
        db.SaSalesReps.AddRange(
            new SaSalesRep { CompanyCode = "DEMO", SrepCode = "SR1", SrepName = "Sales Rep One", IsActive = true },
            new SaSalesRep { CompanyCode = "DEMO", SrepCode = "SR2", SrepName = "Nullable Active Rep", IsActive = null },
            new SaSalesRep { CompanyCode = "DEMO", SrepCode = "SR3", SrepName = "Retired Rep", IsActive = false },
            new SaSalesRep { CompanyCode = "OTHER", SrepCode = "SR9", SrepName = "Other Company Rep", IsActive = true });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Create_StampLeftoverFromClaims()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("NEW01"), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "NEW01");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    [Fact]
    public async Task Create_SlashCode_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("300/3001"), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        var loaded = await sut.GetAsync("300/3001");
        Assert.True(loaded.Succeeded);
        Assert.Equal("300/3001", loaded.Data!.CustCode);
    }

    [Fact]
    public async Task Create_SameCodeOtherCompany_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("SHARED"), isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.SaCusts.CountAsync(x => x.CustCode == "SHARED"));
    }

    [Fact]
    public async Task DuplicateCode_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveAsync(ValidNewModel("CUST01"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("CustCode"));
    }

    [Fact]
    public async Task Update_CrossCompany_NotFound()
    {
        await using var db = await _factory.CreateDbContextAsync();
        var other = await db.SaCusts.SingleAsync(x =>
            x.CompanyCode == "OTHER" && x.CustCode == "SHARED");

        var sut = CreateSut();
        var result = await sut.SaveAsync(new SaCustEditVm
        {
            CustCode = "SHARED",
            CustName = "Tamper attempt",
            CustType = "RETAIL",
            PayCode = "NET30",
            Currency = "MYR",
            GlCode = "AR001",
            Country = "MY",
            AppShip = true,
            RowVersion = other.RowVersion
        }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task InvalidType_Rejected()    {
        var sut = CreateSut();
        var model = ValidNewModel("BADTYPE");
        model.CustType = "INVALID";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("CustType"));
    }

    [Fact]
    public async Task DisGroupLookup_DedupesGroupName_AndExposesRate()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaDisGroups.AddRange(
            new SaDisGroup { CompanyCode = "DEMO", GroupName = "GOLD", PayCode = "NET30", Discount = 5 },
            new SaDisGroup { CompanyCode = "DEMO", GroupName = "GOLD", PayCode = "COD", Discount = 3 });
        await db.SaveChangesAsync();

        var lookups = new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext());
        var rows = await lookups.ListDisGroupsForAssignmentAsync();
        var gold = Assert.Single(rows, x => x.Code == "GOLD");
        Assert.Equal(5m, gold.Rate);
    }

    [Fact]
    public async Task InvalidPayCode_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADPAY");
        model.PayCode = "NOTATERM";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("PayCode"));
    }

    [Fact]
    public async Task InvalidState_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADSTATE");
        model.State = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("State"));
    }
    [Fact]
    public async Task InvalidTaxGroup_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADTAX");
        model.TaxGrCode = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("TaxGrCode"));
    }

    [Fact]
    public async Task InvalidAddressCountry_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADADDR");
        model.Addresses =
        [
            new SaCustAddressVm { Line = 1, AddName = "HQ", Country = "XX" }
        ];

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Addresses[0].Country"));
    }

    [Fact]
    public async Task Country_Required()
    {
        var sut = CreateSut();
        var model = ValidNewModel("NOCTRY");
        model.Country = null;

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Country"));
    }

    [Fact]
    public async Task IndependentShip_RequiresShipCountry()
    {
        var sut = CreateSut();
        var model = ValidNewModel("NOSHIP");
        model.AppShip = false;
        model.ShipCountry = null;

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("ShipCountry"));
    }

    [Fact]
    public async Task IndependentShip_PersistsDestinationCountry()
    {
        var sut = CreateSut();
        var model = ValidNewModel("EXPORT1");
        model.AppShip = false;
        model.Country = "MY";
        model.City = "KL";
        model.ShipName = "SG Warehouse";
        model.ShipAddress1 = "Harbour Dr";
        model.ShipCity = "Singapore";
        model.ShipCountry = "SG";

        var result = await sut.SaveAsync(model, isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "EXPORT1");
        Assert.Equal("MY", row.Country);
        Assert.Equal("SG", row.ShipCountry);
        Assert.Equal("SG Warehouse", row.ShipName);
        Assert.Equal("Harbour Dr", row.ShipAddress1);
        Assert.Equal(false, row.AppShip);
    }

    [Fact]
    public async Task AppShip_On_CopiesMainCountryToShip()
    {
        var sut = CreateSut();
        var model = ValidNewModel("LOCAL1");
        model.AppShip = true;
        model.Country = "MY";
        model.Address1 = "Main St";
        model.City = "KL";
        model.ShipCountry = "SG";

        var result = await sut.SaveAsync(model, isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "LOCAL1");
        Assert.Equal("MY", row.Country);
        Assert.Equal("MY", row.ShipCountry);
        Assert.Equal("Main St", row.ShipAddress1);
        Assert.Equal("KL", row.ShipCity);
    }

    [Fact]
    public async Task InvalidIndustry_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADIND");
        model.IndustryCode = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("IndustryCode"));
    }

    [Fact]
    public async Task InvalidChannel_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADCH");
        model.ChannelCode = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("ChannelCode"));
    }

    [Fact]
    public async Task ValidIndustryAndChannel_Succeed()
    {
        var sut = CreateSut();
        var model = ValidNewModel("OKSEG");
        model.IndustryCode = "ELEC";
        model.ChannelCode = "OEM";

        var result = await sut.SaveAsync(model, isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "OKSEG");
        Assert.Equal("ELEC", row.IndustryCode);
        Assert.Equal("OEM", row.ChannelCode);
    }

    [Fact]
    public async Task ValidMsCodeLookups_Succeed()
    {
        var sut = CreateSut();
        var model = ValidNewModel("OKREF");
        model.State = "SEL";
        model.TaxGrCode = "SR";
        model.PayCode = "NET30";
        model.Country = "MY";
        model.Addresses =
        [
            new SaCustAddressVm { Line = 1, AddName = "HQ", State = "SEL", Country = "MY" }
        ];

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task PhoneOnly_EmptyGlCode_OnEdit_Succeeds()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST01")).Data!;
        loaded.Tel = "999-0000";

        var result = await sut.SaveAsync(loaded, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("999-0000", await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01")
            .Select(x => x.Tel)
            .SingleAsync());
    }

    [Fact]
    public async Task PaymentFieldChange_RequiresGlCode()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST01")).Data!;
        loaded.PayCode = "COD";
        loaded.GlCode = null;

        var result = await sut.SaveAsync(loaded, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("GlCode"));
    }

    [Fact]
    public async Task AppInvoice_On_CopiesGeneralToInvoice()
    {
        var sut = CreateSut();
        var model = ValidNewModel("INV01");
        model.AppInvoice = true;
        model.Address1 = "Main St";
        model.City = "KL";
        model.Tel = "555";

        var result = await sut.SaveAsync(model, isNew: true);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "INV01");
        Assert.Equal("New customer INV01", row.InvName);
        Assert.Equal("Main St", row.InvAddress1);
        Assert.Equal("KL", row.InvCity);
        Assert.Equal("555", row.InvTel);
    }

    [Fact]
    public async Task AppInvoice_Off_GeneralChange_LeavesInvoiceUnchanged()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST02")).Data!;
        loaded.AppInvoice = false;
        loaded.Address1 = "Changed General";
        loaded.City = "Changed City";
        loaded.Tel = "Changed Tel";

        var result = await sut.SaveAsync(loaded, isNew: false);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02");
        Assert.Equal("Line 1", row.InvAddress1);
        Assert.Equal("PJ", row.InvCity);
        Assert.Equal("222", row.InvTel);
        Assert.Equal("Changed General", row.Address1);
    }

    [Fact]
    public async Task Save_ReplacesChildren_TenantScoped()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST02")).Data!;
        loaded.Addresses =
        [
            new SaCustAddressVm { Line = 1, AddName = "Only", City = "Ipoh" }
        ];
        loaded.Contacts =
        [
            new SaCustContactVm { Line = 1, ContactPerson = "Alice" },
            new SaCustContactVm { Line = 2, ContactPerson = "Carol", ContactEmail = "carol@x.com" }
        ];

        var result = await sut.SaveAsync(loaded, isNew: false);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SaCustAdds.CountAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02"));
        Assert.Equal("Only", await db.SaCustAdds
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02")
            .Select(x => x.AddName)
            .SingleAsync());
        Assert.Equal(1, await db.SaCustContacts.CountAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02"));
        Assert.Equal("Carol", await db.SaCustContacts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02")
            .Select(x => x.ContactPerson)
            .SingleAsync());
        Assert.Equal("Alice", await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02")
            .Select(x => x.ContactPerson)
            .SingleAsync());
    }

    [Fact]
    public async Task ConcurrencyFail_NoChildOrHeaderPersist()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST02")).Data!;
        var originalName = loaded.CustName;
        loaded.CustName = "Should Not Persist";
        loaded.Addresses =
        [
            new SaCustAddressVm { Line = 1, AddName = "Should Not Exist" }
        ];

        var stale = (byte[])loaded.RowVersion!.Clone();
        stale[0] ^= 0xFF;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var direct = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02");
            direct.CustName = "Concurrent Writer";
            await db.SaveChangesAsync();
        }

        loaded.RowVersion = stale;
        var result = await sut.SaveAsync(loaded, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);

        await using var verify = await _factory.CreateDbContextAsync();
        var row = await verify.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02");
        Assert.Equal("Concurrent Writer", row.CustName);
        Assert.NotEqual(originalName, row.CustName);
        Assert.Equal(2, await verify.SaCustAdds.CountAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02"));
        Assert.False(await verify.SaCustAdds.AnyAsync(x => x.AddName == "Should Not Exist"));
    }

    [Fact]
    public async Task Activate_Deactivate_Works()
    {
        var sut = CreateSut();
        var c01 = (await sut.GetAsync("CUST01")).Data!;
        var c03 = (await sut.GetAsync("CUST03")).Data!;

        Assert.True((await sut.SetActiveAsync([Token(c01.CustCode, c01.RowVersion!)], false)).Succeeded);
        Assert.True((await sut.SetActiveAsync([Token(c03.CustCode, c03.RowVersion!)], true)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01")
            .Select(x => x.IsActive)
            .SingleAsync());
        Assert.True(await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST03")
            .Select(x => x.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task BulkSetActive_OneStaleToken_ChangesNone()
    {
        var sut = CreateSut();
        var c01 = (await sut.GetAsync("CUST01")).Data!;
        var c03 = (await sut.GetAsync("CUST03")).Data!;
        var stale = (byte[])c03.RowVersion!.Clone();
        stale[0] ^= 0xFF;

        var result = await sut.SetActiveAsync(
            [Token(c01.CustCode, c01.RowVersion!), Token(c03.CustCode, stale)],
            isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01")
            .Select(x => x.IsActive)
            .SingleAsync());
        Assert.False(await db.SaCusts
            .Where(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST03")
            .Select(x => x.IsActive)
            .SingleAsync());
    }

    [Fact]
    public async Task Delete_Unreferenced_Succeeds()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST01")).Data!;

        var result = await sut.DeleteAsync([Token(loaded.CustCode, loaded.RowVersion!)]);
        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaCusts.AnyAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01"));
    }

    [Fact]
    public async Task Unauthorized_Add_Denied()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveAsync(ValidNewModel("DENIED"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Export_UsesTenantCompany_NotRequest()
    {
        var sut = CreateSut();
        var result = await sut.ExportRowsAsync(new SaCustListQuery { Take = 100 });

        Assert.True(result.Succeeded);
        Assert.Contains(result.Data!.Rows, x => x.CustCode == "CUST01");
        Assert.DoesNotContain(result.Data.Rows, x => x.CustCode == "SHARED");
    }

    [Fact]
    public async Task SortField_Invalid_FallsBackToCustCode()
    {
        var sut = CreateSut();
        var result = await sut.SearchAsync(new SaCustListQuery { SortField = "HACK", Take = 50 });

        Assert.True(result.Succeeded);
        var codes = result.Data!.Rows.Select(x => x.CustCode).ToList();
        Assert.Equal(codes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), codes);
    }

    [Fact]
    public async Task Update_PreservesLeftoverBranchCode()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST01")).Data!;
        loaded.CustName = "Alpha Updated";

        var result = await sut.SaveAsync(loaded, isNew: false);
        Assert.True(result.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST01");
        Assert.Equal("LEFTOVER", row.BranchCode);
        Assert.Equal("Alpha Updated", row.CustName);
    }

    // ─────────────── TC34: SaCust.SubGroupCode is validated server-side (D-6 §10.7) ───────────────

    [Fact]
    public async Task SubGroup_UnknownCode_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADSUB");
        model.SubGroupCode = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("SubGroupCode"));
        Assert.Contains("NOPE", result.ValidationErrors["SubGroupCode"]);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaCusts.AnyAsync(x => x.CustCode == "BADSUB"));
    }

    [Fact]
    public async Task SubGroup_Blank_StoredAsNull_AndSaves()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BLANKSUB");
        model.SubGroupCode = "   ";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CustCode == "BLANKSUB");
        Assert.Null(row.SubGroupCode);
    }

    [Fact]
    public async Task SubGroup_KnownCode_Accepted()
    {
        var sut = CreateSut();
        var model = ValidNewModel("GOODSUB");
        model.SubGroupCode = "SG1";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CustCode == "GOODSUB");
        Assert.Equal("SG1", row.SubGroupCode);
    }

    [Fact]
    public async Task SubGroup_OtherCompanysCode_Rejected_SameCompanyOnly()
    {
        var sut = CreateSut();
        var model = ValidNewModel("CROSSSUB");
        // "OTHER" has an SG9; DEMO does not. The check must be scoped to the caller's company.
        model.SubGroupCode = "SG9";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("SubGroupCode"));
    }

    [Fact]
    public async Task SubGroup_LegacyValue_ToleratedOnUnrelatedEdit()
    {
        var sut = CreateSut();
        // CUST02 carries a legacy free-text sub-group that no master row matches.
        var loaded = (await sut.GetAsync("CUST02")).Data!;
        Assert.Equal("LEGACY", loaded.SubGroupCode);
        loaded.CustName = "Beta Renamed";

        var result = await sut.SaveAsync(loaded, isNew: false);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02");
        Assert.Equal("LEGACY", row.SubGroupCode);
        Assert.Equal("Beta Renamed", row.CustName);
    }

    // ─────────── Sales master lookups (plans/sales-master-lookup-fixes.md §3.1–§3.3) ───────────

    [Fact]
    public async Task CustomerSearch_IsCompanyScoped_ActiveOnly_AndMatchesCodeOrName()
    {
        var lookups = CreateLookups();

        var all = await lookups.SearchCustomersAsync();
        Assert.Contains(all, x => x.Code == "CUST01");
        Assert.Contains(all, x => x.Code == "CUST02");
        // Another company's customer, and the caller's own inactive one, are both absent.
        Assert.DoesNotContain(all, x => x.Code == "SHARED");
        Assert.DoesNotContain(all, x => x.Code == "CUST03");

        var byName = await lookups.SearchCustomersAsync("Alpha");
        Assert.Single(byName);
        Assert.Equal("CUST01", byName[0].Code);
        Assert.Equal("Alpha Customer", byName[0].Desc);

        var byCode = await lookups.SearchCustomersAsync("CUST02");
        Assert.Single(byCode);
        Assert.Equal("CUST02", byCode[0].Code);

        var unknown = await lookups.SearchCustomersAsync("no-such-customer");
        Assert.Empty(unknown);
    }

    [Fact]
    public async Task CustomerSearch_NeverReturnsAnotherCompanysCustomer_EvenOnAnExactCodeMatch()
    {
        var lookups = CreateLookups();

        var rows = await lookups.SearchCustomersAsync("SHARED");

        Assert.Empty(rows);
    }

    [Fact]
    public async Task CustomerSearch_IsBounded()
    {
        var lookups = CreateLookups();

        var rows = await lookups.SearchCustomersAsync(maxRows: 1);

        Assert.Single(rows);
        Assert.Equal("CUST01", rows[0].Code);
    }

    [Fact]
    public async Task SalesRepAssignment_IsCompanyScoped_AndTreatsNullIsActiveAsActive()
    {
        var lookups = CreateLookups();

        var rows = await lookups.ListSalesRepsForAssignmentAsync();

        Assert.Equal(["SR1", "SR2"], rows.Select(x => x.Code));
        Assert.Equal("Sales Rep One", rows[0].Desc);
        // NULL IsActive is active; IsActive == false is retired; another company is out of scope.
        Assert.DoesNotContain(rows, x => x.Code == "SR3");
        Assert.DoesNotContain(rows, x => x.Code == "SR9");
    }

    [Fact]
    public async Task Salesman_UnknownCode_Rejected()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BADSALES");
        model.SalesmanCode = "NOPE";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("SalesmanCode"));
        Assert.Contains("NOPE", result.ValidationErrors["SalesmanCode"]);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaCusts.AnyAsync(x => x.CustCode == "BADSALES"));
    }

    [Fact]
    public async Task Salesman_Blank_StoredAsNull_AndSaves()
    {
        var sut = CreateSut();
        var model = ValidNewModel("BLANKSALES");
        model.SalesmanCode = "   ";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CustCode == "BLANKSALES");
        Assert.Null(row.SalesmanCode);
    }

    [Fact]
    public async Task Salesman_KnownCode_Accepted()
    {
        var sut = CreateSut();
        var model = ValidNewModel("GOODSALES");
        model.SalesmanCode = "SR1";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CustCode == "GOODSALES");
        Assert.Equal("SR1", row.SalesmanCode);
    }

    [Fact]
    public async Task Salesman_NullIsActiveRep_IsAccepted_ButRetiredRep_IsNot()
    {
        var sut = CreateSut();
        var nullableActive = ValidNewModel("NULLACTIVE");
        nullableActive.SalesmanCode = "SR2";
        var retired = ValidNewModel("RETIREDREP");
        retired.SalesmanCode = "SR3";

        var ok = await sut.SaveAsync(nullableActive, isNew: true);
        var rejected = await sut.SaveAsync(retired, isNew: true);

        Assert.True(ok.Succeeded, ok.Message);
        Assert.False(rejected.Succeeded);
        Assert.True(rejected.ValidationErrors.ContainsKey("SalesmanCode"));
    }

    [Fact]
    public async Task Salesman_OtherCompanysCode_Rejected_SameCompanyOnly()
    {
        var sut = CreateSut();
        var model = ValidNewModel("CROSSSALES");
        // "OTHER" has an SR9; DEMO does not. A NEW foreign value fails clause 2 (company scope).
        model.SalesmanCode = "SR9";

        var result = await sut.SaveAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("SalesmanCode"));
    }

    [Fact]
    public async Task Salesman_LegacyValue_ToleratedOnUnrelatedEdit()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetAsync("CUST02")).Data!;
        Assert.Equal("LEGACYREP", loaded.SalesmanCode);
        loaded.CustName = "Beta Renamed Again";

        var result = await sut.SaveAsync(loaded, isNew: false);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaCusts.SingleAsync(x => x.CompanyCode == "DEMO" && x.CustCode == "CUST02");
        Assert.Equal("LEGACYREP", row.SalesmanCode);
        Assert.Equal("Beta Renamed Again", row.CustName);
    }

    private static SaCustEditVm ValidNewModel(string code) =>
        new()
        {
            CustCode = code,
            CustName = $"New customer {code}",
            CustType = "RETAIL",
            PayCode = "NET30",
            Currency = "MYR",
            GlCode = "AR001",
            Country = "MY",
            AppShip = true,
            IsActive = true,
            Contacts = [new SaCustContactVm { Line = 1 }]
        };

    private static IvMasterKeyToken Token(string code, byte[] rowVersion) =>
        new() { Code = code, RowVersion = rowVersion };
    private static byte[] Rv(byte marker) => [marker, 0, 0, 0, 0, 0, 0, 0];

    private SaCustLookupService CreateLookups() =>
        new(_factory, InventoryTenantTestHelper.CreateTenantContext());

    private SaCustService CreateSut(
        bool canAccess = true,
        bool canAdd = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canExport = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Edit, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Delete, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(MenuCodes.SalesCustomerProfile, PermissionCodes.Export, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canExport);

        return new SaCustService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustRepository(_factory),
            new SaCustLookupService(_factory, InventoryTenantTestHelper.CreateTenantContext()));
    }
}
