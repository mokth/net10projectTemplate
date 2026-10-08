using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Sales.Transaction;

[Trait(TestCategories.Name, TestCategories.Sales)]
public sealed class SaDeliveryRequestServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaDeliveryRequestServiceTests()
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
        var so = new SaSo
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SoNo = "SO-DR-001",
            CustRel = 1,
            IsCurrent = true,
            LastCustRel = 1,
            SoDate = new DateTime(2026, 9, 1),
            Status = SaSoStatuses.New,
            FulfillmentStatus = "NONE",
            BillingStatus = "NONE",
            CustCode = "CUST01",
            CustName = "Delivery Request Customer",
            CurrRate = 1m,
            CreatedDate = DateTime.UtcNow,
            CreatedBy = "seed",
            RowVersion = [1]
        };
        so.Details.Add(new SaSoDetail
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SoNo = so.SoNo,
            CustRel = 1,
            Line = 1,
            ICode = "FG-DR",
            IDesc = "Delivery Request product",
            StdQty = 10m,
            StdUom = "EA",
            OrderQty = 12m,
            BalanceQty = 10m,
            DeliveryDate = new DateTime(2026, 9, 15),
            Warehouse = "MAIN"
        });
        await db.SaSos.AddAsync(so);
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _connection.DisposeAsync().AsTask();

    [Fact]
    public async Task Create_uses_current_so_std_demand_and_audits_lineage()
    {
        var result = await Service().CreateDraftAsync(new SaDeliveryRequestDraftRequest
        {
            Sources =
            [
                new SaDeliveryRequestSourceInput
                {
                    SoNo = "so-dr-001",
                    CustRel = 1,
                    SoLine = 1,
                    AllocatedProductionQty = 4m
                }
            ]
        });

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal("DR-001", result.Data!.DeliveryRequestNo);
        Assert.Equal("FG-DR", result.Data.ProductCode);
        Assert.Equal("EA", result.Data.ProductionUom);
        Assert.Equal(4m, result.Data.RequestedQty);
        Assert.Equal(new DateTime(2026, 9, 15), result.Data.RequiredDate);
        var source = Assert.Single(result.Data.Sources);
        Assert.Equal("SO-DR-001", source.SoNo);
        Assert.Equal(12m, source.SourceQty);
        Assert.Equal(10m, source.ProductionDemandQty);
        Assert.Equal(4m, source.AllocatedProductionQty);
        Assert.Equal(6m, source.AvailableForDr);
        Assert.Contains(result.Data.AuditEvents, x => x.EventType == SaDeliveryRequestAuditEventTypes.Created);
        Assert.Contains(result.Data.AuditEvents, x => x.EventType == SaDeliveryRequestAuditEventTypes.SourceAdded);

        var page = await Service().SearchAsync(new SaDeliveryRequestListQuery
        {
            Status = SaDeliveryRequestStatuses.Draft
        });
        Assert.True(page.Succeeded, page.Message);
        Assert.Single(page.Data!.Rows);
        Assert.Equal(SaDeliveryRequestStatuses.Draft, page.Data.Rows[0].Status);
    }

    [Fact]
    public async Task Active_source_allocation_is_reserved_across_delivery_requests()
    {
        var first = await Service().CreateDraftAsync(Request(6m));
        Assert.True(first.Succeeded, first.Message);

        var second = await Service().CreateDraftAsync(Request(5m));

        Assert.False(second.Succeeded);
        Assert.Contains("available", second.Message!, StringComparison.OrdinalIgnoreCase);

        var eligible = await Service().ListEligibleSalesOrderDemandAsync(new SaDeliveryRequestEligibleSourceQuery());
        Assert.True(eligible.Succeeded, eligible.Message);
        Assert.Equal(4m, Assert.Single(eligible.Data!).AvailableForDr);
    }

    [Fact]
    public async Task Draft_update_reuses_existing_source_identity_without_duplicate_lineage()
    {
        var created = await Service().CreateDraftAsync(Request(4m));
        Assert.True(created.Succeeded, created.Message);
        var originalSource = Assert.Single(created.Data!.Sources);

        var updated = await Service().UpdateDraftAsync(new SaDeliveryRequestUpdateRequest
        {
            Uid = created.Data.Uid,
            RowVersion = created.Data.RowVersion,
            RequestedQty = 5m,
            Sources =
            [
                new SaDeliveryRequestSourceInput
                {
                    SoNo = "SO-DR-001",
                    CustRel = 1,
                    SoLine = 1,
                    AllocatedProductionQty = 5m
                }
            ]
        });

        Assert.True(updated.Succeeded, updated.Message);
        var source = Assert.Single(updated.Data!.Sources);
        Assert.Equal(originalSource.Uid, source.Uid);
        Assert.Equal(5m, source.AllocatedProductionQty);
        Assert.Equal(5m, updated.Data.RequestedQty);
    }

    [Fact]
    public async Task Release_stamps_source_lifecycle_and_blocks_draft_edit()
    {
        var created = await Service().CreateDraftAsync(Request(4m));
        Assert.True(created.Succeeded, created.Message);

        var released = await Service().ReleaseAsync(new SaDeliveryRequestCommandRequest
        {
            Uid = created.Data!.Uid,
            RowVersion = created.Data.RowVersion,
            Reason = "Approved for production"
        });

        Assert.True(released.Succeeded, released.Message);
        Assert.Equal(SaDeliveryRequestStatuses.Released, released.Data!.Status);
        var source = Assert.Single(released.Data.Sources);
        Assert.NotNull(source.ReleasedDate);
        Assert.Equal("admin", source.ReleasedBy);
        Assert.Contains(released.Data.AuditEvents, x => x.EventType == SaDeliveryRequestAuditEventTypes.Released);

        var update = await Service().UpdateDraftAsync(new SaDeliveryRequestUpdateRequest
        {
            Uid = released.Data.Uid,
            RowVersion = released.Data.RowVersion,
            RequestedQty = 4m,
            Sources =
            [
                new SaDeliveryRequestSourceInput
                {
                    SoNo = "SO-DR-001",
                    CustRel = 1,
                    SoLine = 1,
                    AllocatedProductionQty = 4m
                }
            ]
        });

        Assert.False(update.Succeeded);
        Assert.Contains("Draft", update.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delete_physically_removes_never_released_draft_and_bridge_rows()
    {
        var created = await Service().CreateDraftAsync(Request(3m));
        Assert.True(created.Succeeded, created.Message);

        var deleted = await Service().DeleteDraftAsync(new SaDeliveryRequestCommandRequest
        {
            Uid = created.Data!.Uid,
            RowVersion = created.Data.RowVersion,
            Reason = "Entered in error"
        });

        Assert.True(deleted.Succeeded, deleted.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaDeliveryRequests.AnyAsync(x => x.Uid == created.Data.Uid));
        Assert.False(await db.SaDeliveryRequestSources.AnyAsync(x => x.DeliveryRequestId == created.Data.Uid));
        Assert.False(await db.SaDeliveryRequestAuditEvents.AnyAsync(x => x.DeliveryRequestId == created.Data.Uid));
    }

    [Fact]
    public async Task Historical_so_revision_is_not_a_valid_delivery_request_source()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var current = await db.SaSos.SingleAsync(x => x.SoNo == "SO-DR-001");
            current.IsCurrent = false;
            var revised = new SaSo
            {
                CompanyCode = current.CompanyCode,
                BranchCode = current.BranchCode,
                SoNo = current.SoNo,
                CustRel = 2,
                IsCurrent = true,
                LastCustRel = 2,
                SoDate = current.SoDate,
                Status = current.Status,
                FulfillmentStatus = current.FulfillmentStatus,
                BillingStatus = current.BillingStatus,
                CustCode = current.CustCode,
                CurrRate = 1m,
                RowVersion = [2]
            };
            revised.Details.Add(new SaSoDetail
            {
                CompanyCode = current.CompanyCode,
                BranchCode = current.BranchCode,
                SoNo = current.SoNo,
                CustRel = 2,
                Line = 1,
                ICode = "FG-DR",
                IDesc = "Delivery Request product",
                StdQty = 10m,
                StdUom = "EA",
                DeliveryDate = new DateTime(2026, 9, 16)
            });
            db.SaSos.Add(revised);
            await db.SaveChangesAsync();
        }

        var result = await Service().CreateDraftAsync(Request(3m, custRel: 1));

        Assert.False(result.Succeeded);
        Assert.Contains("current revision", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    private SaDeliveryRequestDraftRequest Request(decimal quantity, short custRel = 1) => new()
    {
        Sources =
        [
            new SaDeliveryRequestSourceInput
            {
                SoNo = "SO-DR-001",
                CustRel = custRel,
                SoLine = 1,
                AllocatedProductionQty = quantity
            }
        ]
    };

    private SaDeliveryRequestService Service()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return new SaDeliveryRequestService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            access.Object,
            new TestNumberingService());
    }

    private sealed class TestNumberingService : IDocumentNumberingService
    {
        private static int _sequence;

        public Task<DocumentNumberResult> NextAsync(
            AppDbContext db,
            string module,
            string extraPrefix,
            DateTime documentDate,
            DocumentNumberRequestMode requestMode,
            string currentDocNo,
            CancellationToken ct)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            return Task.FromResult(new DocumentNumberResult($"DR-{sequence:000}", module));
        }
    }
}
