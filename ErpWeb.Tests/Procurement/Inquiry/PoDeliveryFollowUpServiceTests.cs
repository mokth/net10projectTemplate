using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;

namespace ErpWeb.Tests.Procurement.Inquiry;

[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.PurchaseOrder)]
public sealed class PoDeliveryFollowUpServiceTests : IAsyncLifetime
{
    private const string Company = "DEMO";
    private const string Branch = "HQ";
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public PoDeliveryFollowUpServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Add_AppendsOnlyScopedFollowUp_AndHistoryIsLatestFirst()
    {
        await SeedPoAsync(Branch);
        var service = CreateSut();

        var first = await service.AddAsync(Request("Delayed", next: new DateOnly(2026, 10, 15), note: "Call back"));
        var second = await service.AddAsync(Request("Resolved", note: "Delivered"));

        Assert.True(first.Succeeded, first.Message);
        Assert.True(second.Succeeded, second.Message);
        var history = await service.ListAsync("PO1", 1, 1);
        Assert.True(history.Succeeded, history.Message);
        var historyRows = history.Data!;
        Assert.Equal(new[] { "Resolved", "Delayed" }, historyRows.Select(x => x.Outcome));
        Assert.Equal("BUYER1", historyRows[0].CreatedBy);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.PoDeliveryFollowUps.CountAsync());
        Assert.Equal(2m, await db.PoOrderDetails.Select(x => x.BalanceQty).SingleAsync());
        Assert.Equal(PoOrderStatuses.Open, await db.PoOrders.Select(x => x.Status).SingleAsync());
    }

    [Fact]
    public async Task Add_RejectsInvalidOutcomeDatesAndUnauthorizedWriter()
    {
        await SeedPoAsync(Branch);
        var service = CreateSut();

        var badOutcome = await service.AddAsync(Request("Ignore validation"));
        var badDate = await service.AddAsync(Request("Confirmed", next: new DateOnly(2026, 10, 10)));
        var denied = await CreateSut(canAdd: false).AddAsync(Request("Confirmed"));

        Assert.Equal(IvMasterErrorCode.Validation, badOutcome.ErrorCode);
        Assert.Equal(IvMasterErrorCode.Validation, badDate.ErrorCode);
        Assert.Equal(IvMasterErrorCode.AccessDenied, denied.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PoDeliveryFollowUps.CountAsync());
    }

    [Fact]
    public async Task Add_RejectsTargetFromAnotherBranch()
    {
        await SeedPoAsync("OTHER");

        var result = await CreateSut().AddAsync(Request("Confirmed"));

        Assert.Equal(IvMasterErrorCode.NotFound, result.ErrorCode);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.PoDeliveryFollowUps.CountAsync());
    }

    private PoDeliveryFollowUpService CreateSut(bool canAccess = true, bool canAdd = true, bool featureEnabled = true)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Access, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(MenuCodes.PurchaseDeliveryFollowUp, PermissionCodes.Add, It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:SupplierDeliveryFollowUp"] = featureEnabled.ToString()
            })
            .Build();

        return new PoDeliveryFollowUpService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(Company, Branch, userId: "BUYER1"),
            access.Object,
            configuration,
            new FixedCurrentDateService(new DateTime(2026, 10, 11)));
    }

    private async Task SeedPoAsync(string branch)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = Company,
            BranchCode = branch,
            PoNo = "PO1",
            PoRelNo = 1,
            PoDate = new DateTime(2026, 10, 1),
            Status = PoOrderStatuses.Open,
            RowVersion = [1]
        });
        db.PoOrderDetails.Add(new PoOrderDetail
        {
            CompanyCode = Company,
            BranchCode = branch,
            PoNo = "PO1",
            PoRelNo = 1,
            Line = 1,
            PoPurQty = 5m,
            BalanceQty = 2m,
            EtaDate = new DateTime(2026, 10, 10)
        });
        await db.SaveChangesAsync();
    }

    private static PoDeliveryFollowUpRequest Request(
        string outcome,
        DateOnly? next = null,
        string? note = null) => new()
    {
        PoNo = "PO1",
        PoRelNo = 1,
        PoLineNo = 1,
        Outcome = outcome,
        NextFollowUpDate = next,
        Note = note
    };
}
