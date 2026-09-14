using ErpWeb.Core.Admin;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// Department + Project reference masters (Phase 8 of the Department &amp; Project plan).
///
/// Covers the locked behaviors: Company + Branch scope isolation, admin-only access, CRUD /
/// duplicate / concurrency handling, header and line delete-reference guards, the master-UI
/// lookup bundle, and the shared legacy-orphan rule (MsRefLookupRules) that every sales and
/// purchase save path calls.
/// </summary>
public class MsRefServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 11);
    private static readonly byte[] Rv1 = [1, 2, 3, 4, 5, 6, 7, 8];

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public MsRefServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        // ── Departments ───────────────────────────────────────────────────────
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DeptCode = "DEPT1",
            DeptName = "Sales",
            IsActive = true,
            RowVersion = Rv1
        });
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DeptCode = "DEPT2",
            DeptName = "Retired",
            IsActive = false,
            RowVersion = Rv1
        });
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DeptCode = "DEPT3",
            DeptName = "Used as project default",
            IsActive = true,
            RowVersion = Rv1
        });
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            DeptCode = "DEPT4",
            DeptName = "Unreferenced",
            IsActive = true,
            RowVersion = Rv1
        });
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "DEMO",
            BranchCode = "BR2",
            DeptCode = "BDEPT",
            DeptName = "Other branch",
            IsActive = true,
            RowVersion = Rv1
        });
        db.MsDepts.Add(new MsDept
        {
            CompanyCode = "OTHER",
            BranchCode = "HQ",
            DeptCode = "ODEPT",
            DeptName = "Other company",
            IsActive = true,
            RowVersion = Rv1
        });

        // ── Projects ──────────────────────────────────────────────────────────
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProjCode = "PROJ1",
            ProjName = "Referenced by SO header",
            Status = MsProjectStatus.Active,
            RowVersion = Rv1
        });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProjCode = "PROJ2",
            ProjName = "Referenced by PO line",
            Status = MsProjectStatus.Closed,
            RowVersion = Rv1
        });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProjCode = "PROJ3",
            ProjName = "Uses DEPT3 as default",
            DeptCode = "DEPT3",
            Status = MsProjectStatus.Active,
            RowVersion = Rv1
        });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ProjCode = "PROJ9",
            ProjName = "Unreferenced",
            Status = MsProjectStatus.Active,
            RowVersion = Rv1
        });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "DEMO",
            BranchCode = "BR2",
            ProjCode = "BPROJ",
            ProjName = "Other branch",
            Status = MsProjectStatus.Active,
            RowVersion = Rv1
        });
        db.MsProjects.Add(new MsProject
        {
            CompanyCode = "OTHER",
            BranchCode = "HQ",
            ProjCode = "OPROJ",
            ProjName = "Other company",
            Status = MsProjectStatus.Active,
            RowVersion = Rv1
        });

        // ── Header reference: Sales Order -> PROJ1 ────────────────────────────
        db.SaSos.Add(new SaSo
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SoNo = "SO1",
            CustRel = 1,
            IsCurrent = true,
            LastCustRel = 1,
            SoDate = FixedToday,
            Status = "NEW",
            CustCode = "C1",
            ProjId = "PROJ1",
            RowVersion = Rv1
        });

        // ── Line reference: PO detail -> PROJ2 ────────────────────────────────
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO1",
            PoRelNo = 1,
            PoDate = FixedToday,
            Status = "NEW",
            VendCode = "V1",
            RowVersion = Rv1
        });
        db.PoOrderDetails.Add(new PoOrderDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = "PO1",
            PoRelNo = 1,
            Line = 1,
            ICode = "ITEM1",
            ProjId = "PROJ2"
        });

        // ── Customers for the master-UI lookup bundle ─────────────────────────
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "C1",
            CustName = "Active customer",
            IsActive = true,
            RowVersion = Rv1
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "DEMO",
            CustCode = "C2",
            CustName = "Inactive customer",
            IsActive = false,
            RowVersion = Rv1
        });
        db.SaCusts.Add(new SaCust
        {
            CompanyCode = "OTHER",
            CustCode = "OC1",
            CustName = "Other company customer",
            IsActive = true,
            RowVersion = Rv1
        });

        await db.SaveChangesAsync();
    }

    // ===================== Department =====================

    [Fact]
    public async Task Department_List_IsCompanyAndBranchScoped()
    {
        var sut = CreateSut();
        var result = await sut.ListDepartmentsAsync();

        Assert.True(result.Succeeded, result.Message);
        var codes = result.Data!.Select(x => x.Code).ToList();
        Assert.Contains("DEPT1", codes);
        Assert.DoesNotContain("BDEPT", codes);  // other branch
        Assert.DoesNotContain("ODEPT", codes);  // other company
    }

    [Fact]
    public async Task Department_List_OtherBranchSeesOnlyItsOwnRows()
    {
        var sut = CreateSut(branch: "BR2");
        var result = await sut.ListDepartmentsAsync();

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(result.Data!);
        Assert.Equal("BDEPT", result.Data![0].Code);
    }

    [Fact]
    public async Task Department_Create_StampsTenant_AndUppercasesCode()
    {
        var sut = CreateSut();
        var result = await sut.SaveDepartmentAsync(
            new MsDeptEditVm { Code = "fin", Name = "Finance", IsActive = true }, isNew: true);

        Assert.True(result.Succeeded, result.Message);

        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.MsDepts.SingleAsync(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.DeptCode == "FIN");
        Assert.Equal("Finance", row.DeptName);
        Assert.Equal("admin", row.CreatedBy);
    }

    [Fact]
    public async Task Department_Create_Duplicate_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveDepartmentAsync(new MsDeptEditVm { Code = "DEPT1" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Department_Create_SameCodeInOtherBranch_Allowed()
    {
        // Company + branch is the key — BR2 may reuse HQ's code.
        var sut = CreateSut(branch: "BR2");
        var result = await sut.SaveDepartmentAsync(new MsDeptEditVm { Code = "DEPT1", Name = "BR2 sales" }, isNew: true);

        Assert.True(result.Succeeded, result.Message);
    }

    [Fact]
    public async Task Department_Update_StaleRowVersion_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveDepartmentAsync(
            new MsDeptEditVm { Code = "DEPT1", Name = "Changed", RowVersion = [9, 9] }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Department_Update_MissingToken_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveDepartmentAsync(
            new MsDeptEditVm { Code = "DEPT1", Name = "Changed" }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, result.ErrorCode);
    }

    [Fact]
    public async Task Department_Update_WithToken_Succeeds()
    {
        var sut = CreateSut();
        var result = await sut.SaveDepartmentAsync(
            new MsDeptEditVm { Code = "DEPT1", Name = "Renamed", GlCode = "4000", IsActive = true, RowVersion = Rv1 },
            isNew: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("Renamed", result.Data!.Name);
        Assert.Equal("4000", result.Data.GlCode);
    }

    [Fact]
    public async Task Department_Deactivate_PersistsIsActive()
    {
        var sut = CreateSut();
        var result = await sut.SetDepartmentActiveAsync(
            [new IvMasterKeyToken { Code = "DEPT1", RowVersion = Rv1 }], isActive: false);

        Assert.True(result.Succeeded, result.Message);

        var after = await sut.GetDepartmentAsync("DEPT1");
        Assert.False(after.Data!.IsActive);
    }

    [Fact]
    public async Task Department_Delete_BlockedByProjectDefaultDept()
    {
        var sut = CreateSut();
        var check = await sut.CanDeleteDepartmentsAsync(["DEPT3"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, x => x.ReferenceType == "Project (default dept)");

        var delete = await sut.DeleteDepartmentsAsync(
            [new IvMasterKeyToken { Code = "DEPT3", RowVersion = Rv1 }]);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delete.ErrorCode);
    }

    [Fact]
    public async Task Department_Delete_Unreferenced_Succeeds()
    {
        var sut = CreateSut();
        var check = await sut.CanDeleteDepartmentsAsync(["DEPT4"]);
        Assert.True(check.CanDelete);

        var delete = await sut.DeleteDepartmentsAsync(
            [new IvMasterKeyToken { Code = "DEPT4", RowVersion = Rv1 }]);
        Assert.True(delete.Succeeded, delete.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.MsDepts.AnyAsync(x => x.DeptCode == "DEPT4"));
    }

    [Fact]
    public async Task Department_Delete_CrossBranchCode_CannotBeDeleted()
    {
        // The code exists, but not in the caller's branch — delete must not touch it.
        var sut = CreateSut();
        var delete = await sut.DeleteDepartmentsAsync(
            [new IvMasterKeyToken { Code = "BDEPT", RowVersion = Rv1 }]);

        Assert.False(delete.Succeeded);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.True(await db.MsDepts.AnyAsync(x => x.DeptCode == "BDEPT" && x.BranchCode == "BR2"));
    }

    // ===================== Project =====================

    [Fact]
    public async Task Project_List_IsCompanyAndBranchScoped()
    {
        var sut = CreateSut();
        var result = await sut.ListProjectsAsync();

        Assert.True(result.Succeeded, result.Message);
        var codes = result.Data!.Select(x => x.Code).ToList();
        Assert.Contains("PROJ1", codes);
        Assert.Contains("PROJ2", codes);        // closed projects still list
        Assert.DoesNotContain("BPROJ", codes);  // other branch
        Assert.DoesNotContain("OPROJ", codes);  // other company
    }

    [Fact]
    public async Task Project_List_DerivesActiveFromStatus()
    {
        var sut = CreateSut();
        var result = await sut.ListProjectsAsync();

        var closed = result.Data!.Single(x => x.Code == "PROJ2");
        Assert.Equal(MsProjectStatus.Closed, closed.Status);
        Assert.False(closed.IsActive);

        var active = result.Data!.Single(x => x.Code == "PROJ1");
        Assert.True(active.IsActive);
    }

    [Fact]
    public async Task Project_Create_Duplicate_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(new MsProjectEditVm { Code = "PROJ1" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.DuplicateKey, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Project_Create_TrimsAndUppercasesCode()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "  p-100 ", Name = "Tower B", Status = MsProjectStatus.Active }, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("P-100", result.Data!.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Project_Create_BlankCode_Rejected(string code)
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(new MsProjectEditVm { Code = code }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Code"));
    }

    [Fact]
    public async Task Project_Save_EndBeforeStart_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm
            {
                Code = "P-200",
                StartDate = new DateTime(2026, 6, 1),
                EndDate = new DateTime(2026, 1, 1)
            },
            isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("EndDate"));
    }

    [Fact]
    public async Task Project_Save_InvalidStatus_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "P-300", Status = "WHATEVER" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("Status"));
    }

    [Fact]
    public async Task Project_Save_NegativeBudget_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "P-400", BudgetAmnt = -1m }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("BudgetAmnt"));
    }

    [Fact]
    public async Task Project_Save_UnknownDefaultDepartment_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "P-500", DeptCode = "NOPE" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("DeptCode"));
    }

    [Fact]
    public async Task Project_Save_InactiveDefaultDepartment_Rejected()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "P-501", DeptCode = "DEPT2" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.Validation, result.ErrorCode);
        Assert.True(result.ValidationErrors.ContainsKey("DeptCode"));
    }

    [Fact]
    public async Task Project_Save_ValidDefaultDepartment_Accepted()
    {
        var sut = CreateSut();
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "P-502", DeptCode = "DEPT1" }, isNew: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("DEPT1", result.Data!.DeptCode);
    }

    [Fact]
    public async Task Project_Deactivate_SetsStatusClosed()
    {
        var sut = CreateSut();
        var result = await sut.SetProjectActiveAsync(
            [new IvMasterKeyToken { Code = "PROJ9", RowVersion = Rv1 }], isActive: false);

        Assert.True(result.Succeeded, result.Message);

        var after = await sut.GetProjectAsync("PROJ9");
        Assert.Equal(MsProjectStatus.Closed, after.Data!.Status);
    }

    [Fact]
    public async Task Project_Delete_BlockedByHeaderReference()
    {
        // PROJ1 is on SaSo SO1.
        var sut = CreateSut();
        var check = await sut.CanDeleteProjectsAsync(["PROJ1"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, x => x.ReferenceType == "Sales Order");

        var delete = await sut.DeleteProjectsAsync(
            [new IvMasterKeyToken { Code = "PROJ1", RowVersion = Rv1 }]);
        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.InUse, delete.ErrorCode);
    }

    [Fact]
    public async Task Project_Delete_BlockedByLineReference()
    {
        // PROJ2 is only on a PoOrderDetail line, not on any header.
        var sut = CreateSut();
        var check = await sut.CanDeleteProjectsAsync(["PROJ2"]);

        Assert.False(check.CanDelete);
        Assert.Contains(check.References, x => x.ReferenceType == "Purchase Order (line)");
    }

    [Fact]
    public async Task Project_Delete_Unreferenced_Succeeds()
    {
        var sut = CreateSut();
        var check = await sut.CanDeleteProjectsAsync(["PROJ9"]);
        Assert.True(check.CanDelete);

        var delete = await sut.DeleteProjectsAsync(
            [new IvMasterKeyToken { Code = "PROJ9", RowVersion = Rv1 }]);
        Assert.True(delete.Succeeded, delete.Message);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.MsProjects.AnyAsync(x => x.ProjCode == "PROJ9"));
    }

    [Fact]
    public async Task Project_Delete_StaleRowVersion_Rejected()
    {
        var sut = CreateSut();
        var delete = await sut.DeleteProjectsAsync(
            [new IvMasterKeyToken { Code = "PROJ9", RowVersion = [7, 7, 7] }]);

        Assert.False(delete.Succeeded);
        Assert.Equal(IvMasterErrorCode.Concurrency, delete.ErrorCode);
    }

    // ===================== Scope / access =====================

    [Fact]
    public async Task List_WithoutAccessPermission_AccessDenied()
    {
        var sut = CreateSut(canAccess: false);

        var depts = await sut.ListDepartmentsAsync();
        Assert.False(depts.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, depts.ErrorCode);

        var projects = await sut.ListProjectsAsync();
        Assert.False(projects.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, projects.ErrorCode);
    }

    [Fact]
    public async Task Save_Create_WithoutAddPermission_AccessDenied()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveDepartmentAsync(new MsDeptEditVm { Code = "X1" }, isNew: true);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Save_Update_WithoutEditPermission_AccessDenied()
    {
        var sut = CreateSut(canEdit: false);
        var result = await sut.SaveProjectAsync(
            new MsProjectEditVm { Code = "PROJ1", RowVersion = Rv1 }, isNew: false);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task Delete_WithoutDeletePermission_AccessDenied()
    {
        var sut = CreateSut(canDelete: false);
        var result = await sut.DeleteDepartmentsAsync(
            [new IvMasterKeyToken { Code = "DEPT4", RowVersion = Rv1 }]);

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    [Fact]
    public async Task MissingBranchContext_InvalidScope()
    {
        var sut = CreateSut(branch: "");
        var result = await sut.ListDepartmentsAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.InvalidScope, result.ErrorCode);
    }

    [Fact]
    public async Task Get_DepartmentFromAnotherBranch_NotFound()
    {
        var sut = CreateSut();
        var result = await sut.GetDepartmentAsync("BDEPT");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Get_ProjectFromAnotherCompany_NotFound()
    {
        var sut = CreateSut();
        var result = await sut.GetProjectAsync("OPROJ");

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
    }

    // ===================== Master-UI lookup bundle =====================

    [Fact]
    public async Task ListActiveLookups_ReturnsActiveScopedRowsOnly()
    {
        var sut = CreateSut();
        var result = await sut.ListActiveLookupsAsync();

        Assert.True(result.Succeeded, result.Message);

        var depts = result.Data!.Departments.Select(x => x.Code).ToList();
        Assert.Contains("DEPT1", depts);
        Assert.Contains("DEPT3", depts);
        Assert.DoesNotContain("DEPT2", depts);  // inactive
        Assert.DoesNotContain("BDEPT", depts);  // other branch
        Assert.DoesNotContain("ODEPT", depts);  // other company

        var projects = result.Data!.Projects.Select(x => x.Code).ToList();
        Assert.Contains("PROJ1", projects);
        Assert.DoesNotContain("PROJ2", projects);  // CLOSED
        Assert.DoesNotContain("BPROJ", projects);  // other branch
        Assert.DoesNotContain("OPROJ", projects);  // other company

        // Customers follow SaCust's own company scope (SaCust is not branch-scoped).
        var customers = result.Data!.Customers.Select(x => x.Code).ToList();
        Assert.Contains("C1", customers);
        Assert.DoesNotContain("C2", customers);   // inactive
        Assert.DoesNotContain("OC1", customers);  // other company
    }

    [Fact]
    public async Task ListActiveLookups_WithoutAccessPermission_AccessDenied()
    {
        var sut = CreateSut(canAccess: false);
        var result = await sut.ListActiveLookupsAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, result.ErrorCode);
    }

    // ===================== Legacy-orphan rule (shared by all 8 save paths) =====================

    [Fact]
    public async Task LegacyOrphan_Blank_AlwaysAccepted()
    {
        await using var db = await _factory.CreateDbContextAsync();

        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, priorValue: "OLD-DEPT", incomingValue: null));
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, priorValue: "OLD-DEPT", incomingValue: "   "));
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, priorValue: "OLD-PROJ", incomingValue: null));
    }

    [Fact]
    public async Task LegacyOrphan_UnchangedOnEdit_Accepted()
    {
        // Historical documents carry free-text codes that predate the masters. Leaving the value
        // alone must not block the edit.
        await using var db = await _factory.CreateDbContextAsync();

        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, "OLD-DEPT", "OLD-DEPT"));
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, "OLD-PROJ", "OLD-PROJ"));
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, " old-dept ", "OLD-DEPT"));
    }

    [Fact]
    public async Task LegacyOrphan_ChangedToAnotherUnknown_Rejected()
    {
        await using var db = await _factory.CreateDbContextAsync();

        var dept = await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, "OLD-DEPT", "STILL-UNKNOWN");
        Assert.NotNull(dept);
        Assert.Contains("STILL-UNKNOWN", dept);

        var proj = await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, null, "GHOST");
        Assert.NotNull(proj);
        Assert.Contains("GHOST", proj);
    }

    [Fact]
    public async Task LegacyOrphan_ChangedToValidActiveMaster_Accepted()
    {
        await using var db = await _factory.CreateDbContextAsync();

        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, "OLD-DEPT", "DEPT1"));
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, "OLD-PROJ", "PROJ1"));
        // Surrounding whitespace is trimmed before the master match.
        Assert.Null(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, null, "  DEPT1  "));
    }

    [Fact]
    public async Task LegacyOrphan_ChangedToInactiveOrCrossScopeMaster_Rejected()
    {
        await using var db = await _factory.CreateDbContextAsync();

        // Inactive department.
        Assert.NotNull(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, null, "DEPT2"));
        // Closed project.
        Assert.NotNull(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, null, "PROJ2"));
        // Valid in another branch only.
        Assert.NotNull(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, null, "BDEPT"));
        // Valid in another company only.
        Assert.NotNull(await MsRefLookupRules.ValidateAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, null, "OPROJ"));
    }

    [Fact]
    public async Task LegacyOrphan_ExistsActive_MatchesSharedRule()
    {
        await using var db = await _factory.CreateDbContextAsync();

        Assert.True(await MsRefLookupRules.ExistsActiveAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, "DEPT1"));
        Assert.False(await MsRefLookupRules.ExistsActiveAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Department, "DEPT2"));
        Assert.True(await MsRefLookupRules.ExistsActiveAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, "PROJ1"));
        Assert.False(await MsRefLookupRules.ExistsActiveAsync(
            db, "DEMO", "HQ", MsRefLookupKind.Project, "PROJ2"));
    }

    [Fact]
    public void LegacyOrphan_Normalize_TrimsAndTruncatesToColumnWidth()
    {
        // MsDept.DeptCode / MsProject.ProjCode are nvarchar(20).
        Assert.Equal(20, MsRefLookupRules.Normalize(new string('A', 40))!.Length);
        Assert.Equal("abc", MsRefLookupRules.Normalize("  abc  "));
        Assert.Null(MsRefLookupRules.Normalize("   "));
        Assert.Null(MsRefLookupRules.Normalize(null));
    }

    // Case-insensitive master matching ("dept1" vs "DEPT1") relies on the SQL Server CI collation
    // and is asserted in the SQL Server concurrency suite. SQLite's default BINARY collation treats
    // them as distinct through EF `==`, so it is not asserted here.

    // ===================== Helpers =====================

    private MsRefService CreateSut(
        string company = "DEMO",
        string branch = "HQ",
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

        return new MsRefService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(company, branch, "SITE"),
            access.Object,
            new FixedCurrentDateService(FixedToday));
    }
}
