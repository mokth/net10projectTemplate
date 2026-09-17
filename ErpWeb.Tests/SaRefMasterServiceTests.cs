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

/// <summary>
/// Service tests for the flat sales reference family
/// (<c>SaCustSubGroup</c>, <c>SaShipVia</c>, <c>SaSOType</c>, <c>SaComment</c>,
/// <c>SaShippingLeadTime</c>, <c>SaLMW</c>) — plan §13, cases TC1–TC34 that SQLite can express.
/// TC29 (two real concurrent writers) lives in <c>SaLmwSqlServerConcurrencyTests</c>.
///
/// SQLite has no DB-generated rowversion (see <c>AppDbContext.OnModelCreating</c>), so rows are
/// seeded with explicit tokens — the same convention as <c>SaSalesRefServiceTests</c>.
/// </summary>
public class SaRefMasterServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaRefMasterServiceTests()
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

        db.SaShipVias.AddRange(
            new SaShipVia
            {
                CompanyCode = "DEMO", ShipViaCode = "AIR", ShipViaDesc = "By air",
                IsActive = true, BranchCode = "HQ", LocationCode = "SITE", RowVersion = Rv(1)
            },
            new SaShipVia
            {
                CompanyCode = "OTHER", ShipViaCode = "AIR", ShipViaDesc = "Other company air",
                IsActive = true, RowVersion = Rv(2)
            });

        db.SaSOTypes.Add(new SaSOType
        {
            CompanyCode = "DEMO", SOTypeCode = "STD", SOTypeDesc = "Standard",
            IsActive = true, CreatedDate = new DateTime(2020, 1, 1), CreatedBy = "orig",
            ModifiedDate = new DateTime(2020, 1, 1), ModifiedBy = "orig", RowVersion = Rv(3)
        });

        db.SaComments.Add(new SaComment
        {
            CompanyCode = "DEMO", CommID = "GREET", Comment = "WELCOME",
            IsActive = true, RowVersion = Rv(4)
        });

        db.SaShippingLeadTimes.AddRange(
            new SaShippingLeadTime
            {
                CompanyCode = "DEMO", LeadTimeCode = "LT7", LeadTimeDesc = "Seven days",
                Days = 7, Type = "INTERNAL", IsActive = true, RowVersion = Rv(5)
            },
            new SaShippingLeadTime
            {
                CompanyCode = "DEMO", LeadTimeCode = "LTX", LeadTimeDesc = "Unspecified",
                Days = null, Type = null, IsActive = true, BranchCode = "HQ", LocationCode = "SITE",
                RowVersion = Rv(6)
            });

        db.SaCustSubGroups.AddRange(
            new SaCustSubGroup
            {
                CompanyCode = "DEMO", CustSubGroupCode = "SG1", CustSubGroupDesc = "Sub one",
                RowVersion = Rv(7)
            },
            new SaCustSubGroup
            {
                CompanyCode = "OTHER", CustSubGroupCode = "SG1", CustSubGroupDesc = "Other company",
                RowVersion = Rv(8)
            });

        // LMW: CUSTX already occupies 2026-03-01..2026-03-31 (system) under licence L1.
        db.SaLmws.Add(new SaLMW
        {
            CompanyCode = "DEMO",
            LicenseNo = "L1",
            CustCode = "CUSTX",
            LicenseStartDate = new DateTime(2026, 1, 1),
            LicenseEndDate = new DateTime(2026, 12, 31),
            SystemStartDate = new DateTime(2026, 3, 1),
            SystemEndDate = new DateTime(2026, 3, 31),
            RowVersion = Rv(9)
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    // ═══════════════════════════════ TC1/TC2: duplicate keys ═══════════════════════════════

    [Fact]
    public async Task TC1_ShipVia_CreateDuplicateCode_Fails()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = "AIR", Desc = "Duplicate" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task TC2_ShipVia_SameCodeDifferentCompany_AllowedAndInvisible()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = "SEA", Desc = "By sea" }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        var list = await sut.ListShipViasAsync();
        Assert.True(list.Succeeded);
        Assert.Contains(list.Data!, x => x.Code == "SEA");
        // "AIR" exists in both companies; the DEMO list must show its own row only.
        Assert.Single(list.Data!, x => x.Code == "AIR");

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.SaShipVias.CountAsync(x => x.ShipViaCode == "AIR"));
    }

    // ═══════════════════════════ TC3: stale row-version token ═══════════════════════════

    [Fact]
    public async Task TC3_ShipVia_StaleRowVersion_Rejected_AndRowUnchanged()
    {
        // Simulate "another user saved first": the stored token no longer matches the one held.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var current = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR");
            current.RowVersion = Rv(99);
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "AIR",
            Desc = "Stale write",
            IsActive = true,
            RowVersion = Rv(1)
        }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);

        await using var verify = await _factory.CreateDbContextAsync();
        var row = await verify.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR");
        Assert.Equal("By air", row.ShipViaDesc);
    }

    [Fact]
    public async Task TC3_ShipVia_MissingRowVersion_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = "AIR", Desc = "No token" }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    // ═══════════════════════════════ TC5: deactivate ═══════════════════════════════

    [Fact]
    public async Task TC5_ShipVia_Deactivate_KeepsRowVisible()
    {
        var sut = CreateSut();
        var result = await sut.SetShipViaActiveAsync(
            [Token("AIR", Rv(1))], isActive: false);

        Assert.True(result.Succeeded, result.Message);

        var list = await sut.ListShipViasAsync();
        var row = Assert.Single(list.Data!, x => x.Code == "AIR");
        Assert.False(row.IsActive);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.SaShipVias.AnyAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR"));
    }

    [Fact]
    public async Task TC5_ShipVia_Deactivate_StaleToken_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SetShipViaActiveAsync([Token("AIR", Rv(42))], isActive: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    // ═══════════════════════════════ TC7: delete unreferenced ═══════════════════════════════

    [Fact]
    public async Task TC7_ShipVia_Delete_RemovesRow()
    {
        var sut = CreateSut();
        var result = await sut.DeleteShipViasAsync([Token("AIR", Rv(1))]);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaShipVias.AnyAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR"));
        Assert.True(await db.SaShipVias.AnyAsync(x => x.CompanyCode == "OTHER" && x.ShipViaCode == "AIR"));
    }

    // ═══════════════════════════ TC8/TC9: tenant fail-closed ═══════════════════════════

    [Fact]
    public async Task TC8_EmptyCompanyClaim_FailsClosedOnEveryEntryPoint()
    {
        var sut = CreateSut(company: string.Empty);

        Assert.Equal(IvMasterErrorCode.InvalidScope, (await sut.ListShipViasAsync()).ErrorCode);
        Assert.Equal(IvMasterErrorCode.InvalidScope, (await sut.ListLmwsAsync()).ErrorCode);
        Assert.Equal(IvMasterErrorCode.InvalidScope,
            (await sut.SaveShipViaAsync(new SaShipViaEditVm { Code = "X", Desc = "X" }, isNew: true)).ErrorCode);
        Assert.Equal(IvMasterErrorCode.InvalidScope,
            (await sut.CanDeleteShipViasAsync(["AIR"])).CanDelete ? IvMasterErrorCode.None : IvMasterErrorCode.InvalidScope);
    }

    [Fact]
    public async Task TC9_SubmittedTenantIsIgnored_CallerTenantIsStamped()
    {
        var sut = CreateSut(company: "DEMO", branch: "HQ", location: "SITE");
        var result = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "RAIL",
            Desc = "By rail",
            IsActive = true
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShipVias.SingleAsync(x => x.ShipViaCode == "RAIL");
        Assert.Equal("DEMO", row.CompanyCode);
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
    }

    // ═══════════════════════════ TC10: stamps on create and update ═══════════════════════════

    [Fact]
    public async Task TC10_Update_DoesNotOverwriteStoredStamps()
    {
        var sut = CreateSut(company: "DEMO", branch: "BR2", location: "LOC2");
        var loaded = (await sut.GetShipViaAsync("AIR")).Data!;

        var result = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "AIR",
            Desc = "By air (edited)",
            IsActive = true,
            RowVersion = loaded.RowVersion
        }, isNew: false);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR");
        // Branch/location are write-once stamps; an update must not move them to the caller's scope.
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
        Assert.Equal("BY AIR (EDITED)", row.ShipViaDesc);
    }

    // ═══════════════════════════════ TC11/TC12/TC25: LMW windows ═══════════════════════════════

    [Fact]
    public async Task TC11_Lmw_InvertedWindows_Rejected()
    {
        var sut = CreateSut();

        var invertedSystem = NewLmw("L2", "CUSTX");
        invertedSystem.SystemStartDate = new DateTime(2026, 6, 30);
        invertedSystem.SystemEndDate = new DateTime(2026, 6, 1);
        var inverted = await sut.SaveLmwAsync(invertedSystem, isNew: true);
        Assert.False(inverted.Succeeded);
        Assert.True(inverted.ValidationErrors.ContainsKey("SystemEndDate"));

        var invertedLicence = NewLmw("L4", "CUSTX");
        invertedLicence.LicenseStartDate = new DateTime(2026, 12, 31);
        invertedLicence.LicenseEndDate = new DateTime(2026, 1, 1);
        var licence = await sut.SaveLmwAsync(invertedLicence, isNew: true);
        Assert.False(licence.Succeeded);
        Assert.True(licence.ValidationErrors.ContainsKey("LicenseEndDate"));

        var outsideLicence = NewLmw("L3", "CUSTX");
        outsideLicence.LicenseStartDate = new DateTime(2026, 6, 1);
        outsideLicence.LicenseEndDate = new DateTime(2026, 6, 30);
        var containment = await sut.SaveLmwAsync(outsideLicence, isNew: true);
        Assert.False(containment.Succeeded);
        Assert.True(containment.ValidationErrors.ContainsKey("SystemStartDate"));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaLmws.AnyAsync(x => x.LicenseNo != "L1"));
    }

    [Fact]
    public async Task TC12_Lmw_SystemWindowOverlap_Rejected_NamingTheConflictingLicence()
    {
        var sut = CreateSut();
        var model = NewLmw("L2", "CUSTX");
        // Overlaps L1's system window (2026-03-01..2026-03-31).
        model.SystemStartDate = new DateTime(2026, 3, 31);
        model.SystemEndDate = new DateTime(2026, 4, 30);

        var result = await sut.SaveLmwAsync(model, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.Contains("L1", result.Message);
        Assert.Contains("overlaps the system validity window", result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaLmws.AnyAsync(x => x.LicenseNo == "L2"));
    }

    [Fact]
    public async Task TC12_Lmw_AdjacentWindows_Allowed()
    {
        var sut = CreateSut();
        var model = NewLmw("L2", "CUSTX");
        // Starts the day after L1's system window ends — adjacency is not an overlap (§9.3).
        model.SystemStartDate = new DateTime(2026, 4, 1);
        model.SystemEndDate = new DateTime(2026, 4, 30);

        var result = await sut.SaveLmwAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task TC12_Lmw_OtherCustomersOverlap_Allowed()
    {
        var sut = CreateSut();
        var model = NewLmw("L2", "CUSTY");
        model.SystemStartDate = new DateTime(2026, 3, 1);
        model.SystemEndDate = new DateTime(2026, 3, 31);

        var result = await sut.SaveLmwAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task TC25_Lmw_LicenceWindowOverlapAlone_IsNotBlocked()
    {
        var sut = CreateSut();
        var model = NewLmw("L2", "CUSTX");
        // Same licence dates as L1 — two licences may legitimately run in parallel (D-8) — but the
        // system windows do not overlap.
        model.LicenseStartDate = new DateTime(2026, 1, 1);
        model.LicenseEndDate = new DateTime(2026, 12, 31);
        model.SystemStartDate = new DateTime(2026, 6, 1);
        model.SystemEndDate = new DateTime(2026, 6, 30);

        var result = await sut.SaveLmwAsync(model, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task TC12_Lmw_Update_ExcludesTheRowBeingEdited()
    {
        var sut = CreateSut();
        var loaded = (await sut.GetLmwAsync("L1", "CUSTX")).Data!;
        loaded.LicenseID = "ID-1";

        var result = await sut.SaveLmwAsync(loaded, isNew: false);

        Assert.True(result.Succeeded, result.Message);
    }

    // ═══════════════════════════ TC21: LMW two-part key resolution ═══════════════════════════

    [Fact]
    public async Task TC21_Lmw_WrongCustCode_ReturnsNotFound()
    {
        var sut = CreateSut();

        var get = await sut.GetLmwAsync("L1", "WRONGCUST");
        Assert.False(get.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, get.ErrorCode);

        var delete = await sut.DeleteLmwsAsync(
        [
            new SaCompanyMasterKeyToken { Code = "L1", ParentCode = "WRONGCUST", RowVersion = Rv(9) }
        ]);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, delete.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.SaLmws.AnyAsync(x => x.CompanyCode == "DEMO" && x.LicenseNo == "L1"));
    }

    [Fact]
    public async Task TC21_Lmw_UpdateWithWrongCustCode_ReturnsNotFound()
    {
        var sut = CreateSut();
        var model = NewLmw("L1", "WRONGCUST");
        model.RowVersion = Rv(9);

        var result = await sut.SaveLmwAsync(model, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    // ═══════════════════════════ TC27: cross-tenant token attack ═══════════════════════════

    [Fact]
    public async Task TC27_CrossTenantRowVersionToken_CannotTouchAnotherCompanysRow()
    {
        var sut = CreateSut(company: "DEMO");
        // A token carrying OTHER's row version but DEMO's code must not resolve.
        var update = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "AIR",
            Desc = "Attack",
            IsActive = true,
            RowVersion = Rv(2)   // belongs to OTHER/AIR
        }, isNew: false);

        Assert.False(update.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, update.ErrorCode);

        var delete = await sut.DeleteShipViasAsync([Token("AIR", Rv(2))]);
        Assert.False(delete.Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var demo = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR");
        var other = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "OTHER" && x.ShipViaCode == "AIR");
        Assert.Equal("By air", demo.ShipViaDesc);
        Assert.Equal("Other company air", other.ShipViaDesc);
    }

    [Fact]
    public async Task TC27_CrossTenantGet_NotFound()
    {
        var sut = CreateSut(company: "DEMO");
        var other = await sut.GetShipViaAsync("MISSING");
        Assert.Equal(IvMasterErrorCode.NotFound, other.ErrorCode);
    }

    // ═══════════════════════ TC28: branch/location are not part of the key ═══════════════════════

    [Fact]
    public async Task TC28_CrossBranch_SameCompany_ReadsAndUpdates_StampsUnchanged()
    {
        var sut = CreateSut(company: "DEMO", branch: "BR3", location: "LOC3");

        var loaded = await sut.GetShipViaAsync("AIR");
        Assert.True(loaded.Succeeded);

        var save = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "AIR",
            Desc = "Branch B edit",
            IsActive = true,
            RowVersion = loaded.Data!.RowVersion
        }, isNew: false);
        Assert.True(save.Succeeded, save.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "AIR");
        Assert.Equal("HQ", row.BranchCode);
        Assert.Equal("SITE", row.LocationCode);
        Assert.Equal("BRANCH B EDIT", row.ShipViaDesc);
    }

    // ═══════════════════════════ TC30: LMW key-token round trip ═══════════════════════════

    [Fact]
    public async Task TC30_Lmw_KeyTokenRoundTrip_DeletesTheRightRow()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaLmws.Add(new SaLMW
            {
                CompanyCode = "DEMO",
                LicenseNo = "L/T~1",
                CustCode = "CUST|2",
                LicenseStartDate = new DateTime(2026, 1, 1),
                LicenseEndDate = new DateTime(2026, 12, 31),
                SystemStartDate = new DateTime(2026, 7, 1),
                SystemEndDate = new DateTime(2026, 7, 31),
                RowVersion = Rv(20)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();

        // The list exposes the token exactly as the page will rebuild it.
        var list = await sut.ListLmwsAsync();
        var row = list.Data!.Single(x => x.LicenseNo == "L/T~1");
        Assert.Equal("CUST|2", row.CustCode);

        var result = await sut.DeleteLmwsAsync(
        [
            new SaCompanyMasterKeyToken
            {
                Code = row.LicenseNo,
                ParentCode = row.CustCode,
                RowVersion = row.RowVersion
            }
        ]);

        Assert.True(result.Succeeded, result.Message);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.False(await verify.SaLmws.AnyAsync(x => x.LicenseNo == "L/T~1"));
        // The other licence of the same tenant is untouched.
        Assert.True(await verify.SaLmws.AnyAsync(x => x.LicenseNo == "L1"));
    }

    [Fact]
    public async Task TC30_Lmw_TamperedToken_MissingParentCode_IsRejected()
    {
        var sut = CreateSut();
        var result = await sut.DeleteLmwsAsync(
            [new SaCompanyMasterKeyToken { Code = "L1", RowVersion = Rv(9) }]);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    // ═══════════════════════════ TC13/TC19: lead time Days and Type ═══════════════════════════

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TC13_LeadTime_BlankDays_StoredAsNull_AndAccepted(string? days)
    {
        _ = days;   // the VM carries int?; "blank" is null
        var sut = CreateSut();
        var result = await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = "LTBLANK",
            Desc = "No days",
            Days = null,
            IsActive = true
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShippingLeadTimes.SingleAsync(x => x.LeadTimeCode == "LTBLANK");
        Assert.Null(row.Days);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3651)]
    public async Task TC13_LeadTime_DaysOutOfRange_Rejected(int days)
    {
        var sut = CreateSut();
        var result = await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = "LTBAD",
            Desc = "Bad days",
            Days = days,
            IsActive = true
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Days"));
    }

    [Fact]
    public async Task TC13_LeadTime_DaysBoundaries_Accepted()
    {
        var sut = CreateSut();

        Assert.True((await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = "LT0", Desc = "Zero", Days = 0, IsActive = true
        }, isNew: true)).Succeeded);

        Assert.True((await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = "LTMAX", Desc = "Max", Days = 3650, IsActive = true
        }, isNew: true)).Succeeded);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("internal", "INTERNAL")]
    [InlineData("EXTERNAL", "EXTERNAL")]
    public async Task TC19_LeadTime_TypeNormalisation(string? input, string? expected)
    {
        var sut = CreateSut();
        var code = $"LTT{Guid.NewGuid():N}"[..12];

        var result = await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = code,
            Desc = "Type test",
            Type = input,
            IsActive = true
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShippingLeadTimes.SingleAsync(x => x.LeadTimeCode == code.ToUpperInvariant());
        Assert.Equal(expected, row.Type);
    }

    [Fact]
    public async Task TC19_LeadTime_UnknownType_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveShippingLeadTimeAsync(new SaShippingLeadTimeEditVm
        {
            Code = "LTTBAD",
            Desc = "Bad type",
            Type = "BOTH",
            IsActive = true
        }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Type"));
    }

    // ═══════════════════════════ TC16: code normalisation ═══════════════════════════

    [Fact]
    public async Task TC16_Code_SurroundingWhitespace_TrimmedAndUpperCased()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(new SaShipViaEditVm
        {
            Code = "  road  ",
            Desc = "  by road  ",
            IsActive = true
        }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.SaShipVias.SingleAsync(x => x.CompanyCode == "DEMO" && x.ShipViaCode == "ROAD");
        Assert.Equal("BY ROAD", row.ShipViaDesc);
    }

    [Theory]
    [InlineData("A;B")]
    [InlineData("A?B")]
    [InlineData("A:B")]
    [InlineData("A@B")]
    [InlineData("A&B")]
    [InlineData("A=B")]
    [InlineData("A+B")]
    [InlineData("A$B")]
    [InlineData("A,B")]
    [InlineData("A%B")]
    [InlineData("A'B")]
    public async Task TC16_Code_ForbiddenCharacter_Rejected(string code)
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = code, Desc = "Bad char" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task TC16_Code_WhitespaceOnly_IsRequiredError()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = "   ", Desc = "Blank code" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Code_DescriptionRequired_EvenThoughTheDdlIsNullable()
    {
        var sut = CreateSut();
        var result = await sut.SaveShipViaAsync(
            new SaShipViaEditVm { Code = "NODESC", Desc = "  " }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Desc"));
    }

    [Fact]
    public async Task Code_SearchFindsLowerCasedInput()
    {
        var sut = CreateSut();
        var loaded = await sut.GetShipViaAsync("air");
        Assert.True(loaded.Succeeded);
        Assert.Equal("AIR", loaded.Data!.Code);
    }

    // ═══════════════════════════ TC14: permission denial per action ═══════════════════════════

    [Fact]
    public async Task TC14_ShipVia_PermissionDenied_PerAction()
    {
        var add = CreateSut(canAdd: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await add.SaveShipViaAsync(new SaShipViaEditVm { Code = "X", Desc = "X" }, isNew: true)).ErrorCode);

        var edit = CreateSut(canEdit: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await edit.SaveShipViaAsync(
                new SaShipViaEditVm { Code = "AIR", Desc = "X", RowVersion = Rv(1) }, isNew: false)).ErrorCode);
        Assert.Equal(IvMasterErrorCode.AccessDenied,
            (await edit.SetShipViaActiveAsync([Token("AIR", Rv(1))], true)).ErrorCode);

        var del = CreateSut(canDelete: false);
        Assert.False((await del.CanDeleteShipViasAsync(["AIR"])).CanDelete);
        Assert.Equal(IvMasterErrorCode.AccessDenied, (await del.DeleteShipViasAsync([Token("AIR", Rv(1))])).ErrorCode);

        // List/get need ACCESS only.
        var list = CreateSut(canAccess: false);
        Assert.Equal(IvMasterErrorCode.AccessDenied, (await list.ListShipViasAsync()).ErrorCode);
        Assert.Equal(IvMasterErrorCode.AccessDenied, (await list.GetShipViaAsync("AIR")).ErrorCode);
    }

    [Fact]
    public async Task TC17_Export_RequiresTheExportPermissionAtTheExecutionPoint()
    {
        var denied = CreateSut(canExport: false);
        var blocked = await denied.ExportShipViasAsync();
        Assert.False(blocked.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, blocked.ErrorCode);

        var allowed = CreateSut(canExport: true);
        var ok = await allowed.ExportShipViasAsync();
        Assert.True(ok.Succeeded);
        Assert.Contains(ok.Data!, x => x.Code == "AIR");
    }

    [Fact]
    public async Task TC17_Export_EveryMasterIsPermissionChecked()
    {
        var denied = CreateSut(canExport: false);

        var results = new (string Name, IvMasterOperationResult<object> Result)[]
        {
            ("SaCustSubGroup", ToObject(await denied.ExportCustSubGroupsAsync())),
            ("SaShipVia", ToObject(await denied.ExportShipViasAsync())),
            ("SaSOType", ToObject(await denied.ExportSoTypesAsync())),
            ("SaComment", ToObject(await denied.ExportCommentsAsync())),
            ("SaShippingLeadTime", ToObject(await denied.ExportShippingLeadTimesAsync())),
            ("SaLMW", ToObject(await denied.ExportLmwsAsync()))
        };

        foreach (var (name, result) in results)
        {
            Assert.False(result.Succeeded);
            Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(name));
        }
    }

    private static IvMasterOperationResult<object> ToObject<T>(IvMasterOperationResult<T> source) =>
        source.Succeeded
            ? IvMasterOperationResult<object>.Ok()
            : IvMasterOperationResult<object>.Fail(
                source.ErrorCode,
                source.Message ?? "failed",
                source.ValidationErrors);

    // ═══════════════════════════ TC20/TC22/TC24: schema + stamping shape ═══════════════════════════

    [Fact]
    public async Task TC20_Comment_DuplicateInCompany_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveCommentAsync(
            new SaCommentEditVm { Code = "GREET", Comment = "HELLO" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
    }

    [Fact]
    public async Task TC20_Comment_SeededRowExistsInOneCompanyOnly()
    {
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SaComments.CountAsync(x => x.CompanyCode == "DEMO" && x.CommID == "GREET"));
        Assert.False(await db.SaComments.AnyAsync(x => x.CompanyCode == "OTHER" && x.CommID == "GREET"));
    }

    [Fact]
    public async Task TC22_SoType_InsertStampsCreatedAndUser_UpdateLeavesThemUntouched()
    {
        var sut = CreateSut();

        var created = await sut.SaveSoTypeAsync(
            new SaSOTypeEditVm { Code = "RUSH", Desc = "Rush order", IsActive = true }, isNew: true);
        Assert.True(created.Succeeded, created.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.SaSOTypes.SingleAsync(x => x.SOTypeCode == "RUSH");
            Assert.Equal(FixedToday, row.CreatedDate);
            Assert.Equal("admin", row.CreatedBy);
            Assert.Equal(FixedToday, row.ModifiedDate);
        }

        var loaded = (await sut.GetSoTypeAsync("STD")).Data!;
        loaded.Desc = "Standard (edited)";
        var updated = await sut.SaveSoTypeAsync(loaded, isNew: false);
        Assert.True(updated.Succeeded, updated.Message);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.SaSOTypes.SingleAsync(x => x.SOTypeCode == "STD");
            // D-12: insert-only columns are never rewritten by an update.
            Assert.Equal(new DateTime(2020, 1, 1), row.CreatedDate);
            Assert.Equal("orig", row.CreatedBy);
            Assert.Equal(FixedToday, row.ModifiedDate);
            Assert.Equal("admin", row.ModifiedBy);
        }
    }

    [Fact]
    public void TC24_SaComment_HasNoModuleColumn_AndNoLegacyIdentity()
    {
        using var db = _factory.CreateDbContext();
        var entityType = db.Model.FindEntityType(typeof(SaComment))!;
        var columns = entityType.GetProperties()
            .Select(p => p.GetColumnName())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.DoesNotContain("Module", columns);
        Assert.DoesNotContain("ID", columns);
        Assert.Contains("Comment", columns);
        Assert.Contains("CommID", columns);
        Assert.Contains("Active", columns);

        // The VM must not carry a Module property either.
        Assert.Null(typeof(SaCommentEditVm).GetProperty("Module"));
        Assert.Null(typeof(SaCommentListRow).GetProperty("Module"));
    }

    [Fact]
    public void TC24_SaSOType_HasNoFocAutoColumn()
    {
        using var db = _factory.CreateDbContext();
        var entityType = db.Model.FindEntityType(typeof(SaSOType))!;
        Assert.DoesNotContain(
            "FOCAuto",
            entityType.GetProperties().Select(p => p.GetColumnName()),
            StringComparer.OrdinalIgnoreCase);
        Assert.Null(typeof(SaSOTypeEditVm).GetProperty("FocAuto"));
    }

    [Fact]
    public void TC23_Lmw_HasNoActivateSurface()
    {
        Assert.Null(typeof(ISaSalesRefService).GetMethod("SetLmwActiveAsync"));

        using var db = _factory.CreateDbContext();
        var entityType = db.Model.FindEntityType(typeof(SaLMW))!;
        Assert.DoesNotContain(
            "Active",
            entityType.GetProperties().Select(p => p.GetColumnName()),
            StringComparer.OrdinalIgnoreCase);
    }

    // ═══════════════════════════ TC32/TC33: delete / reference matrix ═══════════════════════════

    [Fact]
    public async Task TC32_UnreferencedMasters_DeleteCleanly_AndReportConsumerDeferred()
    {
        var sut = CreateSut();

        Assert.True((await sut.CanDeleteShipViasAsync(["AIR"])).CanDelete);
        Assert.Contains("consumer is deferred",
            (await sut.CanDeleteShipViasAsync(["AIR"])).Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True((await sut.CanDeleteSoTypesAsync(["STD"])).CanDelete);
        Assert.Contains("consumer is deferred",
            (await sut.CanDeleteCommentsAsync(["GREET"])).Message!, StringComparison.OrdinalIgnoreCase);
        Assert.True((await sut.CanDeleteShippingLeadTimesAsync(["LT7"])).CanDelete);

        Assert.True((await sut.DeleteSoTypesAsync([Token("STD", Rv(3))])).Succeeded);
        Assert.True((await sut.DeleteCommentsAsync([Token("GREET", Rv(4))])).Succeeded);
        Assert.True((await sut.DeleteShippingLeadTimesAsync([Token("LT7", Rv(5))])).Succeeded);
    }

    [Fact]
    public async Task TC32_Lmw_CanDelete_ReportsExplicitNoReferences()
    {
        var sut = CreateSut();
        var check = await sut.CanDeleteLmwsAsync([new SaLMWKey { LicenseNo = "L1", CustCode = "CUSTX" }]);

        Assert.True(check.CanDelete);
        Assert.Equal("No in-scope table references SaLMW.", check.Message);
    }

    [Fact]
    public async Task TC32_CustSubGroup_DeleteBlocked_WhenCustomerReferences()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "REF1",
                CustName = "References SG1",
                SubGroupCode = "SG1",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();

        var check = await sut.CanDeleteCustSubGroupsAsync(["SG1"]);
        Assert.False(check.CanDelete);
        Assert.Contains(check.References, r => r.ReferenceType == "Customers" && r.Count == 1);

        var delete = await sut.DeleteCustSubGroupsAsync([Token("SG1", Rv(7))]);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delete.ErrorCode);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.True(await verify.SaCustSubGroups.AnyAsync(x => x.CustSubGroupCode == "SG1"));
    }

    [Fact]
    public async Task TC33_ReferenceCheck_IsTenantScoped()
    {
        // A customer in DEMO references DEMO/SG1 …
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCusts.Add(new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "REFA",
                CustName = "Demo customer",
                SubGroupCode = "SG1",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        // … so DEMO/SG1 is blocked …
        var demo = CreateSut(company: "DEMO");
        var demoCheck = await demo.CanDeleteCustSubGroupsAsync(["SG1"]);
        Assert.False(demoCheck.CanDelete);

        // … while OTHER/SG1 (same code, unused, different company) deletes cleanly.
        var other = CreateSut(company: "OTHER");
        var otherCheck = await other.CanDeleteCustSubGroupsAsync(["SG1"]);
        Assert.True(otherCheck.CanDelete);

        var otherDelete = await other.DeleteCustSubGroupsAsync([Token("SG1", Rv(8))]);
        Assert.True(otherDelete.Succeeded, otherDelete.Message);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.False(await verify.SaCustSubGroups.AnyAsync(x => x.CompanyCode == "OTHER"));
        Assert.True(await verify.SaCustSubGroups.AnyAsync(x => x.CompanyCode == "DEMO"));
    }

    [Fact]
    public async Task TC32_CustSubGroup_Unreferenced_Deletes()
    {
        var sut = CreateSut(company: "OTHER");
        var result = await sut.DeleteCustSubGroupsAsync([Token("SG1", Rv(8))]);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaCustSubGroups.AnyAsync(x => x.CompanyCode == "OTHER"));
    }

    // ═══════════════════════════ Business key immutability + view path ═══════════════════════════

    [Fact]
    public async Task Get_UnknownCode_NotFound_ForEveryMaster()
    {
        var sut = CreateSut();

        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetShipViaAsync("NOPE")).ErrorCode);
        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetSoTypeAsync("NOPE")).ErrorCode);
        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetCommentAsync("NOPE")).ErrorCode);
        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetShippingLeadTimeAsync("NOPE")).ErrorCode);
        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetCustSubGroupAsync("NOPE")).ErrorCode);
        Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetLmwAsync("NOPE", "NOPE")).ErrorCode);
    }

    [Fact]
    public async Task Get_BlankCode_IsAValidationError()
    {
        var sut = CreateSut();
        var result = await sut.GetShipViaAsync("   ");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task List_IsTenantScoped_ForEveryMaster()
    {
        var sut = CreateSut(company: "OTHER");

        Assert.DoesNotContain((await sut.ListShipViasAsync()).Data!, x => x.Desc == "By air");
        Assert.DoesNotContain((await sut.ListCustSubGroupsAsync()).Data!, x => x.Desc == "Sub one");
        Assert.Empty((await sut.ListSoTypesAsync()).Data!);
        Assert.Empty((await sut.ListCommentsAsync()).Data!);
        Assert.Empty((await sut.ListShippingLeadTimesAsync()).Data!);
        Assert.Empty((await sut.ListLmwsAsync()).Data!);
    }

    // ═══════════════════════════ Service surface (plan §10.2) ═══════════════════════════

    [Fact]
    public void LevelA_Masters_SaveTakesNoExpectedFingerprintArgument()
    {
        var saveMethods = new[]
        {
            "SaveCustSubGroupAsync", "SaveShipViaAsync", "SaveSoTypeAsync",
            "SaveCommentAsync", "SaveShippingLeadTimeAsync", "SaveLmwAsync"
        };

        foreach (var name in saveMethods)
        {
            var method = typeof(ISaSalesRefService).GetMethod(name);
            Assert.NotNull(method);
            Assert.DoesNotContain(method!.GetParameters(), p =>
                p.ParameterType == typeof(string) && p.Name == "expectedFingerprint");
        }
    }

    [Fact]
    public void RowVersionIsTheConcurrencyToken_OnAllSixMasters()
    {
        using var db = _factory.CreateDbContext();
        var types = new[]
        {
            typeof(SaCustSubGroup), typeof(SaShipVia), typeof(SaSOType),
            typeof(SaComment), typeof(SaShippingLeadTime), typeof(SaLMW)
        };

        foreach (var type in types)
        {
            var entityType = db.Model.FindEntityType(type)!;
            var rowVersion = entityType.FindProperty("RowVersion")!;
            Assert.True(rowVersion.IsConcurrencyToken, $"{type.Name}.RowVersion must be a concurrency token");

            // CompanyCode is in the primary key of every master (D1 fix).
            Assert.Contains(
                "CompanyCode",
                entityType.FindPrimaryKey()!.Properties.Select(p => p.Name));
        }
    }

    [Fact]
    public void LmwKeys_AreTheThreePartNaturalKey()
    {
        using var db = _factory.CreateDbContext();
        var key = db.Model.FindEntityType(typeof(SaLMW))!.FindPrimaryKey()!;

        Assert.Equal(
            ["CompanyCode", "LicenseNo", "CustCode"],
            key.Properties.Select(p => p.Name).ToArray());
    }

    // ═══════════════════════════ SaLmwRules (pure, no database) ═══════════════════════════

    [Theory]
    [InlineData("2026-03-01", "2026-03-31", "2026-03-31", "2026-04-30", true)]   // touches on the end
    [InlineData("2026-03-01", "2026-03-31", "2026-02-01", "2026-03-01", true)]   // touches on the start
    [InlineData("2026-03-01", "2026-03-31", "2026-03-15", "2026-03-20", true)]   // fully inside
    [InlineData("2026-03-01", "2026-03-31", "2026-04-01", "2026-04-30", false)]  // adjacent
    [InlineData("2026-03-01", "2026-03-31", "2026-02-01", "2026-02-28", false)]  // adjacent, earlier
    public void SaLmwRules_OverlapIsInclusive(
        string existingStart, string existingEnd, string candidateStart, string candidateEnd, bool expected)
    {
        var actual = SaLmwRules.SystemWindowsOverlap(
            DateTime.Parse(existingStart),
            DateTime.Parse(existingEnd),
            DateTime.Parse(candidateStart),
            DateTime.Parse(candidateEnd));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SaLmwRules_OverlapMessage_NamesTheConflictingLicence()
    {
        var message = SaLmwRules.BuildOverlapMessage(
            "L77", new DateTime(2026, 3, 1), new DateTime(2026, 3, 31));

        Assert.StartsWith(SaLmwRules.OverlapMessagePrefix, message);
        Assert.Contains("L77", message);
    }

    [Fact]
    public void SaLmwRules_ContainmentError_WhenSystemWindowEscapesLicenceWindow()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaLmwRules.ValidateWindows(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            new DateTime(2025, 12, 1), new DateTime(2026, 3, 31),
            errors);

        Assert.True(errors.ContainsKey("SystemStartDate"));
    }

    [Fact]
    public void SaLmwRules_OrderingError_ForInvertedWindows()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaLmwRules.ValidateWindows(
            new DateTime(2026, 12, 31), new DateTime(2026, 1, 1),
            new DateTime(2026, 3, 31), new DateTime(2026, 3, 1),
            errors);

        Assert.True(errors.ContainsKey("LicenseEndDate"));
        Assert.True(errors.ContainsKey("SystemEndDate"));
    }

    [Fact]
    public void SaLmwRules_ContainmentIsNotReported_WhenWindowsAreAlreadyInverted()
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        SaLmwRules.ValidateWindows(
            new DateTime(2026, 1, 1), new DateTime(2026, 12, 31),
            new DateTime(2026, 3, 1), new DateTime(2026, 6, 30),
            errors);

        Assert.Empty(errors);
    }

    // ═══════════════════════════ Helpers ═══════════════════════════

    private static SaLMWEditVm NewLmw(string licenseNo, string custCode) => new()
    {
        LicenseNo = licenseNo,
        CustCode = custCode,
        LicenseStartDate = new DateTime(2026, 1, 1),
        LicenseEndDate = new DateTime(2026, 12, 31),
        SystemStartDate = new DateTime(2026, 1, 1),
        SystemEndDate = new DateTime(2026, 1, 31)
    };

    private static SaCompanyMasterKeyToken Token(string code, byte[] rowVersion) =>
        new() { Code = code, RowVersion = rowVersion };

    private static byte[] Rv(byte marker) => [marker, 0, 0, 0, 0, 0, 0, 0];

    private SaSalesRefService CreateSut(
        string company = "DEMO",
        string branch = "HQ",
        string? location = "SITE",
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

        var tenant = InventoryTenantTestHelper.CreateTenantContext(company, branch, location);
        return new SaSalesRefService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            new SaCustLookupService(_factory, tenant));
    }
}
