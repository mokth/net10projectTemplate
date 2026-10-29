using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Security;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Admin;

/// <summary>
/// Generic configurable lookup master (plan §7, §25). SQLite-backed, tenant-scoped, no SQL Server needed.
/// </summary>
[Trait(TestCategories.Name, TestCategories.Admin)]
public class AdLookupServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<ICurrentUserService> _currentUser = new();
    private string _company = "DEMO";

    public AdLookupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);

        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        _currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        _currentUser.SetupGet(x => x.SubjectUid).Returns("1");
        _currentUser.SetupGet(x => x.UserId).Returns("admin");
        _currentUser.SetupGet(x => x.CompanyCode).Returns(() => _company);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Creation / validation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_NewValue_Succeeds_AndStoresValue()
    {
        var sut = CreateSut();

        var result = await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "general", "General Warehouse"), isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("GENERAL", result.Data!.Code);
        Assert.True(result.Data.IsActive);

        // SQLite has no DB-generated rowversion, so the test provider pattern stamps one before the
        // token is used for an edit (the same reason the repository's other SQLite tests seed explicit
        // RowVersion bytes). SQL Server generates it in the database on INSERT.
        var stamped = await StampAndLoadAsync(sut, result.Data.Uid);
        Assert.NotEmpty(stamped.RowVersion!);
    }

    [Fact]
    public async Task Create_DuplicateCompanyTypeCode_Rejected()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Succeeded);

        var duplicate = await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "general", "General again"), isNew: true);

        Assert.False(duplicate.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, duplicate.ErrorCode);
    }

    [Fact]
    public async Task Create_SameCode_UnderDifferentType_Succeeds()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Succeeded);

        var otherType = await sut.SaveAsync(Vm(AdLookupTypes.MachineType, "GENERAL", "General machine"), isNew: true);

        Assert.True(otherType.Succeeded, otherType.Message);
    }

    [Fact]
    public async Task Create_SameTypeAndCode_ForOtherCompany_Succeeds()
    {
        Assert.True((await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Succeeded);

        _company = "OTHER";
        var otherCompany = await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true);

        Assert.True(otherCompany.Succeeded, otherCompany.Message);
    }

    [Fact]
    public async Task Create_BlankCode_Rejected()
    {
        var result = await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "  ", "Desc"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Create_BlankDescription_Rejected()
    {
        var result = await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "  "), isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Description"));
    }

    [Fact]
    public async Task Create_UnknownLookupType_Rejected()
    {
        var result = await CreateSut().SaveAsync(Vm("NOT_A_REAL_FAMILY", "X", "X"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("LookupType"));
    }

    [Fact]
    public async Task Create_ReservedCustomerFamily_IsNotUserMaintainable()
    {
        var result = await CreateSut().SaveAsync(Vm(AdLookupTypes.CustomerIndustry, "X", "X"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("LookupType"));
    }

    [Theory]
    [InlineData(AdLookupTypes.WarehouseType, 21)]
    [InlineData(AdLookupTypes.MachineType, 31)]
    [InlineData(AdLookupTypes.WorkCentreClass, 11)]
    [InlineData(AdLookupTypes.SupplierType, 21)]
    [InlineData(AdLookupTypes.SupplierSubGroup, 21)]
    public async Task Create_CodeLongerThanConsumerColumn_Rejected(string lookupType, int length)
    {
        var code = new string('X', length);

        var result = await CreateSut().SaveAsync(Vm(lookupType, code, "Desc"), isNew: true);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    // ── Edit / lifecycle ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_CodeIsImmutable()
    {
        var sut = CreateSut();
        var createdRaw = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;
        var created = await StampAndLoadAsync(sut, createdRaw.Uid);

        var edit = new AdLookupEditVm
        {
            Uid = created.Uid,
            LookupType = created.LookupType,
            Code = "RENAMED",
            Description = created.Description,
            IsActive = true,
            RowVersion = created.RowVersion
        };

        var result = await sut.SaveAsync(edit, isNew: false);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Edit_DescriptionAndSortOrder_AreEditable()
    {
        var sut = CreateSut();
        var createdRaw = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;
        var created = await StampAndLoadAsync(sut, createdRaw.Uid);

        var edit = new AdLookupEditVm
        {
            Uid = created.Uid,
            LookupType = created.LookupType,
            Code = created.Code,
            Description = "General purpose warehouse",
            SortOrder = 55,
            IsActive = true,
            RowVersion = created.RowVersion
        };

        var result = await sut.SaveAsync(edit, isNew: false);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.AdLookupValues.SingleAsync(x => x.Uid == created.Uid);
        Assert.Equal("General purpose warehouse", stored.Description);
        Assert.Equal(55, stored.SortOrder);
    }

    [Fact]
    public async Task Edit_StaleRowVersion_Rejected()
    {
        var sut = CreateSut();
        var created = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;

        var edit = new AdLookupEditVm
        {
            Uid = created.Uid,
            LookupType = created.LookupType,
            Code = created.Code,
            Description = "Changed",
            IsActive = true,
            RowVersion = [9, 9, 9, 9, 9, 9, 9, 9]
        };

        var result = await sut.SaveAsync(edit, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task SetActive_Deactivates_AndInactiveIsExcludedFromAssignment()
    {
        var sut = CreateSut();
        var createdRaw = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;
        var created = await StampAndLoadAsync(sut, createdRaw.Uid);

        var deactivate = await sut.SetActiveAsync(
            [new AdLookupKeyToken { Uid = created.Uid, RowVersion = created.RowVersion! }],
            isActive: false);

        Assert.True(deactivate.Succeeded, deactivate.Message);

        var assignment = await sut.ListForAssignmentAsync(AdLookupTypes.WarehouseType);
        Assert.DoesNotContain(assignment, x => x.Code == "GENERAL");
    }

    [Fact]
    public async Task InactiveValue_IsToleratedWhenUnchanged_ButRejectedWhenNewlyAssigned()
    {
        var sut = CreateSut();
        var createdRaw = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;
        var created = await StampAndLoadAsync(sut, createdRaw.Uid);
        await sut.SetActiveAsync([new AdLookupKeyToken { Uid = created.Uid, RowVersion = created.RowVersion! }], false);

        Assert.True(await sut.ValidateAssignmentAsync(AdLookupTypes.WarehouseType, "GENERAL", "GENERAL", allowBlank: true));
        Assert.False(await sut.ValidateAssignmentAsync(AdLookupTypes.WarehouseType, "GENERAL", null, allowBlank: true));
    }

    [Fact]
    public async Task ListForAssignment_IncludesStoredLegacyValueOnlyForItsCurrentBinding()
    {
        var sut = CreateSut();
        var createdRaw = (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "LEGACY", "Legacy warehouse"), isNew: true)).Data!;
        var created = await StampAndLoadAsync(sut, createdRaw.Uid);
        await sut.SetActiveAsync([new AdLookupKeyToken { Uid = created.Uid, RowVersion = created.RowVersion! }], false);

        var withBinding = await sut.ListForAssignmentAsync(AdLookupTypes.WarehouseType, existingCode: "LEGACY");
        var withoutBinding = await sut.ListForAssignmentAsync(AdLookupTypes.WarehouseType);

        Assert.Contains(withBinding, x => x.Code == "LEGACY");
        Assert.DoesNotContain(withoutBinding, x => x.Code == "LEGACY");
    }

    [Fact]
    public async Task AssignmentList_IsSortedBySortOrderThenCode()
    {
        var sut = CreateSut();
        await SeedAsync(AdLookupTypes.WarehouseType, ("B", "Bee", 20));
        await SeedAsync(AdLookupTypes.WarehouseType, ("A", "Ay", 20));
        await SeedAsync(AdLookupTypes.WarehouseType, ("C", "See", 10));

        var rows = await sut.ListForAssignmentAsync(AdLookupTypes.WarehouseType);

        Assert.Equal(["C", "A", "B"], rows.Select(x => x.Code).ToArray());
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RowFromAnotherCompany_IsInvisibleAndNotEditable()
    {
        var createdRaw = (await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;

        _company = "OTHER";
        var other = CreateSut();

        var token = Guid.NewGuid().ToByteArray();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.AdLookupValues
                .Where(x => x.Uid == createdRaw.Uid)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RowVersion, token));
        }

        Assert.Equal(IvMasterErrorCode.NotFound, (await other.GetAsync(createdRaw.Uid)).ErrorCode);
        Assert.Empty(await other.ListForMaintenanceAsync(AdLookupTypes.WarehouseType));

        var edit = new AdLookupEditVm
        {
            Uid = createdRaw.Uid,
            LookupType = AdLookupTypes.WarehouseType,
            Code = "GENERAL",
            Description = "Hijack",
            IsActive = true,
            RowVersion = token
        };
        Assert.Equal(IvMasterErrorCode.NotFound, (await other.SaveAsync(edit, isNew: false)).ErrorCode);
    }

    [Fact]
    public async Task AssignmentValidation_NeverCrossesCompanyScope()
    {
        var created = (await CreateSut().SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!;
        Assert.True(created.Uid > 0);

        _company = "OTHER";

        Assert.False(await CreateSut().ValidateAssignmentAsync(AdLookupTypes.WarehouseType, "GENERAL", null, allowBlank: true));
    }

    // ── Permission split ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BusinessUserWithoutAdminLookups_CanLoadAssignmentValues()
    {
        await SeedAsync(AdLookupTypes.WarehouseType, ("GENERAL", "General Warehouse", 10));

        var sut = CreateSut(adminAccess: false);

        var rows = await sut.ListForAssignmentAsync(AdLookupTypes.WarehouseType);

        Assert.Contains(rows, x => x.Code == "GENERAL");
    }

    [Fact]
    public async Task BusinessUserWithoutAdminLookups_CannotMaintainLookups()
    {
        await SeedAsync(AdLookupTypes.WarehouseType, ("GENERAL", "General Warehouse", 10));

        var sut = CreateSut(adminAccess: false);

        Assert.Empty(await sut.ListForMaintenanceAsync(AdLookupTypes.WarehouseType));

        var create = await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "NEW", "New"), isNew: true);
        Assert.Equal(IvMasterErrorCode.AccessDenied, create.ErrorCode);

        // A row this caller may not maintain is indistinguishable from a missing row: the family grant
        // is part of the boundary, so GetAsync never confirms the row exists.
        var hidden = await sut.GetAsync(1);
        Assert.Equal(IvMasterErrorCode.NotFound, hidden.ErrorCode);
    }

    // ── Contract / seeding ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void InterfaceExposesNoHardDelete()
    {
        var methodNames = typeof(IAdLookupService).GetMethods().Select(x => x.Name).ToArray();

        Assert.DoesNotContain(methodNames, name => name.Contains("Delete", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EnsureDefaults_SeedsWarehouseTypes_Idempotently()
    {
        var sut = CreateSut();

        await sut.EnsureLookupDefaultsAsync("DEMO");
        await sut.EnsureLookupDefaultsAsync("DEMO");

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.AdLookupValues
            .Where(x => x.CompanyCode == "DEMO" && x.LookupType == AdLookupTypes.WarehouseType)
            .ToListAsync();

        Assert.Equal(7, rows.Count);
        Assert.Contains(rows, x => x.Code == "GENERAL" && x.Description == "General Warehouse");
        Assert.All(rows, x => Assert.True(x.IsActive));
    }

    // ── Per-family grants (least privilege) ─────────────────────────────────────────────────────

    [Fact]
    public async Task FamilyGrant_MaintainsOnlyItsOwnFamily()
    {
        var sut = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.InventoryWarehouseType,
                PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit));

        var created = await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true);
        Assert.True(created.Succeeded, created.Message);

        var rows = await sut.ListForMaintenanceAsync(AdLookupTypes.WarehouseType);
        Assert.Contains(rows, x => x.Code == "GENERAL");

        // Same module, different family: no listing and no creation.
        Assert.Empty(await sut.ListForMaintenanceAsync(AdLookupTypes.MachineType));
        Assert.Equal(
            IvMasterErrorCode.AccessDenied,
            (await sut.SaveAsync(Vm(AdLookupTypes.MachineType, "CNC", "Cnc machine"), isNew: true)).ErrorCode);
    }

    [Fact]
    public async Task ModuleGrant_CannotReachTheSecondFamilyInTheSameModule()
    {
        var sut = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.PlanningMachineType,
                PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit));

        Assert.True((await sut.SaveAsync(Vm(AdLookupTypes.MachineType, "CNC", "Cnc machine"), isNew: true)).Succeeded);

        Assert.Empty(await sut.ListForMaintenanceAsync(AdLookupTypes.WorkCentreClass));
        Assert.Equal(
            IvMasterErrorCode.AccessDenied,
            (await sut.SaveAsync(Vm(AdLookupTypes.WorkCentreClass, "LINE", "Line class"), isNew: true)).ErrorCode);
    }

    [Fact]
    public async Task FamilyGrant_AddWithoutEdit_CannotEdit_AndEditWithoutAdd_CannotCreate()
    {
        var addOnly = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.PurchaseSupplierType,
                PermissionCodes.Access, PermissionCodes.Add));

        var created = (await addOnly.SaveAsync(
            Vm(AdLookupTypes.SupplierType, "LOCAL", "Local supplier"), isNew: true)).Data!;
        var stamped = await StampAndLoadAsync(addOnly, created.Uid);

        var edit = new AdLookupEditVm
        {
            Uid = stamped.Uid,
            LookupType = stamped.LookupType,
            Code = stamped.Code,
            Description = "Renamed",
            IsActive = true,
            RowVersion = stamped.RowVersion
        };
        Assert.Equal(IvMasterErrorCode.NotFound, (await addOnly.SaveAsync(edit, isNew: false)).ErrorCode);

        var editOnly = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.PurchaseSupplierType,
                PermissionCodes.Access, PermissionCodes.Edit));

        Assert.Equal(
            IvMasterErrorCode.AccessDenied,
            (await editOnly.SaveAsync(Vm(AdLookupTypes.SupplierType, "OTHER", "Other"), isNew: true)).ErrorCode);
    }

    [Fact]
    public async Task Edit_ForgedFamily_WithOnlyTargetFamilyGrant_ReturnsNotFound_AndDoesNotMutate()
    {
        var admin = CreateSut();
        var created = (await admin.SaveAsync(
            Vm(AdLookupTypes.MachineType, "CNC", "Cnc machine"), isNew: true)).Data!;
        var stamped = await StampAndLoadAsync(admin, created.Uid);

        // This principal may maintain Warehouse Types only. Claiming that family over a Machine Type UID
        // must not reveal or modify the row.
        var warehouseOnly = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.InventoryWarehouseType,
                PermissionCodes.Access, PermissionCodes.Add, PermissionCodes.Edit));

        var forged = new AdLookupEditVm
        {
            Uid = stamped.Uid,
            LookupType = AdLookupTypes.WarehouseType,
            Code = "GENERAL",
            Description = "Hijacked",
            IsActive = false,
            RowVersion = stamped.RowVersion
        };

        Assert.Equal(IvMasterErrorCode.NotFound, (await warehouseOnly.SaveAsync(forged, isNew: false)).ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.AdLookupValues.SingleAsync(x => x.Uid == stamped.Uid);
        Assert.Equal(AdLookupTypes.MachineType, stored.LookupType);
        Assert.Equal("Cnc machine", stored.Description);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task Edit_ForgedFamily_WithAdminAccess_IsRejectedAsFamilyChange_AndDoesNotMutate()
    {
        var sut = CreateSut();
        var created = (await sut.SaveAsync(
            Vm(AdLookupTypes.MachineType, "CNC", "Cnc machine"), isNew: true)).Data!;
        var stamped = await StampAndLoadAsync(sut, created.Uid);

        var forged = new AdLookupEditVm
        {
            Uid = stamped.Uid,
            LookupType = AdLookupTypes.WarehouseType,
            Code = "GENERAL",
            Description = "Hijacked",
            IsActive = false,
            RowVersion = stamped.RowVersion
        };

        var result = await sut.SaveAsync(forged, isNew: false);

        Assert.False(result.Succeeded);
        Assert.True(result.ValidationErrors.ContainsKey("LookupType"));

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.AdLookupValues.SingleAsync(x => x.Uid == stamped.Uid);
        Assert.Equal(AdLookupTypes.MachineType, stored.LookupType);
        Assert.Equal("Cnc machine", stored.Description);
        Assert.True(stored.IsActive);
    }

    [Fact]
    public async Task SetActive_MixedFamilies_WithOneFamilyGrant_RejectsTheWholeBatch()
    {
        var admin = CreateSut();
        var warehouseUid = (await admin.SaveAsync(
            Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!.Uid;
        var machineUid = (await admin.SaveAsync(
            Vm(AdLookupTypes.MachineType, "CNC", "Cnc machine"), isNew: true)).Data!.Uid;

        var warehouse = await StampAndLoadAsync(admin, warehouseUid);
        var machine = await StampAndLoadAsync(admin, machineUid);

        var warehouseOnly = CreateSut(
            adminAccess: false,
            familyGrants: FamilyGrants(
                MenuCodes.InventoryWarehouseType,
                PermissionCodes.Access, PermissionCodes.Edit));

        var result = await warehouseOnly.SetActiveAsync(
            [
                new AdLookupKeyToken { Uid = warehouse.Uid, RowVersion = warehouse.RowVersion! },
                new AdLookupKeyToken { Uid = machine.Uid, RowVersion = machine.RowVersion! }
            ],
            isActive: false);

        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.AdLookupValues.ToListAsync();
        Assert.All(rows, x => Assert.True(x.IsActive));
    }

    [Fact]
    public async Task ReservedCustomerFamily_IsDeniedEvenWithFullAdminLookups()
    {
        await SeedAsync(AdLookupTypes.CustomerIndustry, ("X", "Industry X", 1));

        var sut = CreateSut(); // full ADMIN_LOOKUPS ACCESS + ADD + EDIT

        Assert.Empty(await sut.ListForMaintenanceAsync(AdLookupTypes.CustomerIndustry));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var uid = await db.AdLookupValues
                .Where(x => x.LookupType == AdLookupTypes.CustomerIndustry)
                .Select(x => x.Uid)
                .SingleAsync();
            Assert.Equal(IvMasterErrorCode.NotFound, (await sut.GetAsync(uid)).ErrorCode);
        }

        var create = await sut.SaveAsync(Vm(AdLookupTypes.CustomerIndustry, "Y", "Industry Y"), isNew: true);
        Assert.True(create.ValidationErrors.ContainsKey("LookupType"));
    }

    [Fact]
    public void MaintainableFamilyWithoutMenuCode_FailsClosed()
    {
        var definition = new AdLookupTypeDefinition("X", "X", "Inventory", 20, UserMaintainable: true);

        Assert.False(AdLookupService.HasMaintainablePlacement(null));
        Assert.False(AdLookupService.HasMaintainablePlacement(definition));
        Assert.False(AdLookupService.HasMaintainablePlacement(definition with { MaintenanceMenuCode = "  " }));
        Assert.False(AdLookupService.HasMaintainablePlacement(
            definition with { UserMaintainable = false, MaintenanceMenuCode = MenuCodes.InventoryWarehouseType }));
        Assert.True(AdLookupService.HasMaintainablePlacement(
            definition with { MaintenanceMenuCode = MenuCodes.InventoryWarehouseType }));
    }

    [Fact]
    public async Task SetActive_DuplicateUid_Rejected()
    {
        var sut = CreateSut();
        var created = await StampAndLoadAsync(
            sut,
            (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!.Uid);
        var token = new AdLookupKeyToken { Uid = created.Uid, RowVersion = created.RowVersion };

        var result = await sut.SetActiveAsync([token, token], isActive: false);

        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
    }

    [Fact]
    public async Task SetActive_MissingUid_RejectsWholeBatch_NoPartialWrite()
    {
        var sut = CreateSut();
        var created = await StampAndLoadAsync(
            sut,
            (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "GENERAL", "General"), isNew: true)).Data!.Uid);

        var result = await sut.SetActiveAsync(
            [
                new AdLookupKeyToken { Uid = created.Uid, RowVersion = created.RowVersion! },
                new AdLookupKeyToken { Uid = created.Uid + 50_000, RowVersion = created.RowVersion! }
            ],
            isActive: false);

        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
        await AssertStillActiveAsync(created.Uid);
    }

    [Fact]
    public async Task SetActive_StaleRowVersion_RejectsWholeBatch_NoPartialWrite()
    {
        var sut = CreateSut();
        var first = await StampAndLoadAsync(
            sut,
            (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "FIRST", "First"), isNew: true)).Data!.Uid);
        var second = await StampAndLoadAsync(
            sut,
            (await sut.SaveAsync(Vm(AdLookupTypes.WarehouseType, "SECOND", "Second"), isNew: true)).Data!.Uid);

        var result = await sut.SetActiveAsync(
            [
                new AdLookupKeyToken { Uid = first.Uid, RowVersion = first.RowVersion! },
                new AdLookupKeyToken { Uid = second.Uid, RowVersion = [9, 9, 9, 9, 9, 9, 9, 9] }
            ],
            isActive: false);

        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
        await AssertStillActiveAsync(first.Uid);
        await AssertStillActiveAsync(second.Uid);
    }

    private async Task AssertStillActiveAsync(long uid)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.AdLookupValues.SingleAsync(x => x.Uid == uid);
        Assert.True(stored.IsActive);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private AdLookupService CreateSut(
        bool adminAccess = true,
        bool adminAdd = true,
        bool adminEdit = true,
        IReadOnlyDictionary<string, HashSet<string>>? familyGrants = null)
    {
        // A closed-world permission set: anything not granted here is denied. This is what makes a
        // missing family mapping and a missing ACCESS prerequisite observable instead of accidental.
        var adminPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (adminAccess)
        {
            adminPermissions.Add(PermissionCodes.Access);
        }
        if (adminAccess && adminAdd)
        {
            adminPermissions.Add(PermissionCodes.Add);
        }
        if (adminAccess && adminEdit)
        {
            adminPermissions.Add(PermissionCodes.Edit);
        }

        var grants = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [MenuCodes.AdminLookups] = adminPermissions
        };

        if (familyGrants is not null)
        {
            foreach (var (menuCode, permissions) in familyGrants)
            {
                grants[menuCode] = permissions;
            }
        }

        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string menuCode, string permissionCode, CancellationToken _) =>
                grants.TryGetValue(menuCode, out var allowed) && allowed.Contains(permissionCode));

        return new AdLookupService(
            _factory,
            new TenantScopeContext(_currentUser.Object),
            access.Object);
    }

    private static HashSet<string> Perms(params string[] permissions) =>
        new(permissions, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, HashSet<string>> FamilyGrants(
        string menuCode,
        params string[] permissions) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [menuCode] = Perms(permissions)
        };

    private async Task SeedAsync(string lookupType, params (string Code, string Description, int SortOrder)[] rows)
    {
        await using var db = await _factory.CreateDbContextAsync();
        foreach (var (code, description, sortOrder) in rows)
        {
            db.AdLookupValues.Add(new AdLookupValue
            {
                CompanyCode = _company,
                LookupType = lookupType,
                Code = code,
                Description = description,
                SortOrder = sortOrder,
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "TEST"
            });
        }

        await db.SaveChangesAsync();
    }

    private async Task<AdLookupEditVm> StampAndLoadAsync(AdLookupService sut, long uid)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var token = Guid.NewGuid().ToByteArray();
            await db.AdLookupValues
                .Where(x => x.Uid == uid)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RowVersion, token));
        }

        return (await sut.GetAsync(uid)).Data!;
    }

    private static AdLookupEditVm Vm(string lookupType, string code, string description) =>
        new()
        {
            LookupType = lookupType,
            Code = code,
            Description = description,
            IsActive = true
        };
}
