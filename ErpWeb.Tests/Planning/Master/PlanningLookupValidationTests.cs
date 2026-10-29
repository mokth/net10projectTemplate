using ErpWeb.Core.Admin;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Planning;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests.Planning.Master;

[Trait(TestCategories.Name, TestCategories.Planning)]
public class PlanningLookupValidationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PlanningLookupValidationTests()
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
        db.PrWorkCentres.Add(new PrWorkCentre
        {
            WrkCtrCd = "WC1",
            WrkCtrDes = "Work centre 1",
            Class = "LEGACY",
            CompCode = "DEMO",
            BranchCode = "HQ",
            LocCode = "SITE"
        });
        db.PrProcesses.Add(new PrProcess
        {
            ProcessCd = "PROC1",
            ProcessDes = "Process 1",
            WorkCentre = "WC1",
            CompCode = "DEMO",
            BranchCode = "HQ",
            LocCode = "SITE"
        });
        db.PrMachines.Add(new PrMachine
        {
            MachineCd = "MC1",
            ProcessCd = "PROC1",
            MachineDes = "Machine 1",
            MachineType = "LEGACY",
            CompCode = "DEMO",
            BranchCode = "HQ",
            LocCode = "SITE"
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Machine_Create_AcceptsAnActiveLookupType()
    {
        await SeedLookupAsync(AdLookupTypes.MachineType, "MIXER", "Mixer");
        var sut = CreateMachineSut();

        var result = await sut.SaveBatchAsync(
        [
            new PrMachineEditVm
            {
                ClientNodeId = "1",
                MachineCd = "MC2",
                ProcessCd = "PROC1",
                MachineDes = "Machine 2",
                MachineType = "MIXER",
                IsNew = true
            }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PrMachines.SingleAsync(x => x.MachineCd == "MC2" && x.CompCode == "DEMO");
        Assert.Equal("MIXER", row.MachineType);
    }

    [Fact]
    public async Task Machine_Create_RejectsAnUnknownLookupType()
    {
        await SeedLookupAsync(AdLookupTypes.MachineType, "MIXER", "Mixer");
        var sut = CreateMachineSut();

        var result = await sut.SaveBatchAsync(
        [
            new PrMachineEditVm
            {
                ClientNodeId = "1",
                MachineCd = "MC2",
                ProcessCd = "PROC1",
                MachineDes = "Machine 2",
                MachineType = "NOSUCH",
                IsNew = true
            }
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(PlanningErrorCode.ValidationFailed, result.ErrorCode);
        Assert.Contains(result.Issues, x => x.FieldName == nameof(PrMachineEditVm.MachineType));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.PrMachines.AnyAsync(x => x.MachineCd == "MC2"));
    }

    [Fact]
    public async Task Machine_Update_KeepsAnInactiveLegacyTypeOnAnUnrelatedEdit()
    {
        await using (var seed = await _factory.CreateDbContextAsync())
        {
            await seed.AdLookupValues.AddAsync(new AdLookupValue
            {
                CompanyCode = "DEMO",
                LookupType = AdLookupTypes.MachineType,
                Code = "LEGACY",
                Description = "Retired type",
                IsActive = false
            });
            await seed.SaveChangesAsync();
        }

        var sut = CreateMachineSut();
        var result = await sut.SaveBatchAsync(
        [
            new PrMachineEditVm
            {
                ClientNodeId = "1",
                MachineCd = "MC1",
                ProcessCd = "PROC1",
                MachineDes = "Machine 1 renamed",
                MachineType = "LEGACY",
                IsNew = false
            }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PrMachines.SingleAsync(x => x.MachineCd == "MC1" && x.CompCode == "DEMO");
        Assert.Equal("LEGACY", row.MachineType);
        Assert.Equal("Machine 1 renamed", row.MachineDes);
    }

    [Fact]
    public async Task WorkCentre_Create_AcceptsAnActiveLookupClass()
    {
        await SeedLookupAsync(AdLookupTypes.WorkCentreClass, "ASSY", "Assembly");
        var sut = CreateWorkCentreSut();

        var result = await sut.SaveBatchAsync(
        [
            new PrWorkCentreEditVm
            {
                ClientNodeId = "1",
                WrkCtrCd = "WC2",
                WrkCtrDes = "Work centre 2",
                Class = "ASSY",
                IsNew = true
            }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PrWorkCentres.SingleAsync(x => x.WrkCtrCd == "WC2" && x.CompCode == "DEMO");
        Assert.Equal("ASSY", row.Class);
    }

    [Fact]
    public async Task WorkCentre_Create_RejectsAnUnknownLookupClass()
    {
        await SeedLookupAsync(AdLookupTypes.WorkCentreClass, "ASSY", "Assembly");
        var sut = CreateWorkCentreSut();

        var result = await sut.SaveBatchAsync(
        [
            new PrWorkCentreEditVm
            {
                ClientNodeId = "1",
                WrkCtrCd = "WC2",
                WrkCtrDes = "Work centre 2",
                Class = "NOSUCH",
                IsNew = true
            }
        ]);

        Assert.False(result.Succeeded);
        Assert.Equal(PlanningErrorCode.ValidationFailed, result.ErrorCode);
        Assert.Contains(result.Issues, x => x.FieldName == nameof(PrWorkCentreEditVm.Class));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.PrWorkCentres.AnyAsync(x => x.WrkCtrCd == "WC2"));
    }

    [Fact]
    public async Task WorkCentre_Update_BlankClassKeepsTheStoredLegacyValue()
    {
        var sut = CreateWorkCentreSut();

        var result = await sut.SaveBatchAsync(
        [
            new PrWorkCentreEditVm
            {
                ClientNodeId = "1",
                WrkCtrCd = "WC1",
                WrkCtrDes = "Work centre 1 renamed",
                Class = null,
                IsNew = false
            }
        ]);

        Assert.True(result.Succeeded, result.Message);
        await using var db = await _factory.CreateDbContextAsync();
        var row = await db.PrWorkCentres.SingleAsync(x => x.WrkCtrCd == "WC1" && x.CompCode == "DEMO");
        Assert.Equal("LEGACY", row.Class);
        Assert.Equal("Work centre 1 renamed", row.WrkCtrDes);
    }

    private PrMachineService CreateMachineSut() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            new AllowAllPlanningAccess(),
            new Mock<IPlanningDependencyChecker>().Object,
            CreateLookupService(),
            NullLogger<PrMachineService>.Instance);

    private PrWorkCentreService CreateWorkCentreSut() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            new AllowAllPlanningAccess(),
            new Mock<IPlanningDependencyChecker>().Object,
            CreateLookupService(),
            NullLogger<PrWorkCentreService>.Instance);

    private AdLookupService CreateLookupService()
    {
        var current = new Mock<ErpWeb.Core.Services.ICurrentUserService>();
        current.SetupGet(x => x.IsAuthenticated).Returns(true);
        current.SetupGet(x => x.SubjectUid).Returns("1");
        current.SetupGet(x => x.UserId).Returns("admin");
        current.SetupGet(x => x.CompanyCode).Returns("DEMO");
        current.SetupGet(x => x.BranchCode).Returns("HQ");
        current.SetupGet(x => x.LocationCode).Returns("SITE");

        var access = new Mock<ErpWeb.Core.Menus.IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new AdLookupService(
            _factory,
            new ErpWeb.Core.Services.TenantScopeContext(current.Object),
            access.Object);
    }

    private async Task SeedLookupAsync(string lookupType, string code, string description)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.AdLookupValues.AddAsync(new AdLookupValue
        {
            CompanyCode = "DEMO",
            LookupType = lookupType,
            Code = code,
            Description = description,
            IsActive = true
        });
        await db.SaveChangesAsync();
    }

    private sealed class AllowAllPlanningAccess : IPlanningMasterAccess
    {
        public Task<bool> CanAccessAsync(string menuCode, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanAddAsync(string menuCode, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanEditAsync(string menuCode, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> CanDeleteAsync(string menuCode, CancellationToken ct = default) => Task.FromResult(true);
    }
}
