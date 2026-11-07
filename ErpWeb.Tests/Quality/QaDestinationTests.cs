using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Quality;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Quality;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ErpWeb.Tests.Quality;

[Trait(TestCategories.Name, TestCategories.Quality)]
public class QaDestinationTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly Mock<IInventoryTenantContext> _tenant = new();
    private readonly Mock<IAccessRightService> _access = new();

    public QaDestinationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        var scope = new InventoryTenantScope { CompanyCode = "DEMO", BranchCode = "HQ", UserId = "tester" };
        _tenant.Setup(x => x.TryCompanyScope()).Returns(scope);
        _tenant.Setup(x => x.TryBranchScope()).Returns(scope);
        _tenant.Setup(x => x.TryWriteScope()).Returns(scope);
        _access
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public void Reject_before_grn_is_not_a_vendor_return()
    {
        var destinations = QaDestinationComposer.Compose(Case(
            QaSourceTypes.IncomingPreGrn,
            accepted: 85m,
            rejected: 15m));

        Assert.Contains(destinations, x =>
            x.Kind == QaDestinationKinds.GoodsReceipt
            && x.Status == QaDestinationStatuses.Suggested
            && x.CanNavigate);
        Assert.Contains(destinations, x =>
            x.Kind == QaDestinationKinds.VendorReturn
            && x.Status == QaDestinationStatuses.NotStock
            && !x.CanNavigate);
        Assert.DoesNotContain(destinations, x => x.Kind == QaDestinationKinds.VendorReturn && x.CanNavigate);
        Assert.DoesNotContain(destinations, x => x.Kind == QaDestinationKinds.PurchaseCreditNote);
        Assert.DoesNotContain(destinations, x => x.Kind == QaDestinationKinds.InventoryScrap);
    }

    [Fact]
    public void Posted_gr_allocation_is_executed_not_document_number_matching()
    {
        var entity = Case(QaSourceTypes.IncomingPreGrn, accepted: 85m, rejected: 15m);
        entity.SourceDocNo = "VR-999";
        entity.Grants.First().Allocations.Add(new QaReceiptAllocation
        {
            BatchNo = 42,
            Qty = 85m,
            Status = QaReceiptAllocationStatuses.Posted
        });

        var destinations = QaDestinationComposer.Compose(entity);
        var executed = Assert.Single(destinations, x => x.Status == QaDestinationStatuses.Executed);
        Assert.Equal(QaDestinationKinds.GoodsReceipt, executed.Kind);
        Assert.Equal("42", executed.DocumentNo);
        Assert.Equal("/inventory/goods-receipts/view/42", executed.Href);
        Assert.DoesNotContain(destinations, x => x.Status == QaDestinationStatuses.Suggested);
        Assert.DoesNotContain(destinations, x => x.Href != null && x.Href.Contains("VR-999", StringComparison.Ordinal));
    }

    [Fact]
    public void Reversed_gr_allocation_is_reversed()
    {
        var entity = Case(QaSourceTypes.IncomingPreGrn, accepted: 85m, rejected: 0m);
        entity.Grants.First().Allocations.Add(new QaReceiptAllocation
        {
            BatchNo = 7,
            Qty = 85m,
            Status = QaReceiptAllocationStatuses.Reversed
        });

        var destinations = QaDestinationComposer.Compose(entity);
        Assert.Contains(destinations, x =>
            x.Kind == QaDestinationKinds.GoodsReceipt
            && x.Status == QaDestinationStatuses.Reversed
            && x.DocumentNo == "7");
        Assert.Contains(destinations, x => x.Status == QaDestinationStatuses.Suggested);
    }

    [Fact]
    public void Fg_posted_allocation_is_executed()
    {
        var entity = Case(QaSourceTypes.FgStagingLot, accepted: 10m, rejected: 0m, fg: true);
        entity.FgGrants.First().Allocations.Add(new QaFgAllocation
        {
            BatchId = 88,
            Qty = 10m,
            Status = QaReceiptAllocationStatuses.Posted
        });

        var destinations = QaDestinationComposer.Compose(entity);
        var executed = Assert.Single(destinations);
        Assert.Equal(QaDestinationKinds.FinishedGoodReceipt, executed.Kind);
        Assert.Equal(QaDestinationStatuses.Executed, executed.Status);
        Assert.Equal("/planning/finished-good-receipts/88/view", executed.Href);
    }

    [Fact]
    public void Posted_grn_reject_is_linked_unverified_never_executed()
    {
        var entity = Case(QaSourceTypes.PostedGrn, accepted: 0m, rejected: 12m);
        entity.SourceDocNo = "GRN-100";

        var destinations = QaDestinationComposer.Compose(entity);
        Assert.Equal(2, destinations.Count);
        Assert.All(destinations, x =>
        {
            Assert.Equal(QaDestinationStatuses.LinkedUnverified, x.Status);
            Assert.True(x.CanNavigate);
            Assert.NotEqual(QaDestinationStatuses.Executed, x.Status);
        });
        Assert.Contains(destinations, x => x.Kind == QaDestinationKinds.VendorReturn);
        Assert.Contains(destinations, x => x.Kind == QaDestinationKinds.PurchaseCreditNote);
        Assert.Contains(
            "credit note",
            destinations.Single(x => x.Kind == QaDestinationKinds.PurchaseCreditNote).Guidance,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inventory_reject_scrap_is_unverified_without_a_later_phase_conversion()
    {
        var destinations = QaDestinationComposer.Compose(
            Case(QaSourceTypes.InventoryStock, accepted: 0m, rejected: 3m));

        Assert.Contains(destinations, x =>
            x.Kind == QaDestinationKinds.InventoryScrap
            && x.Status == QaDestinationStatuses.LinkedUnverified);
        Assert.DoesNotContain(destinations, x => x.Status == QaDestinationStatuses.RequiresLaterPhase);
        Assert.DoesNotContain(destinations, x => x.Guidance.Contains("Phase 4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Physical_reservation_is_not_a_qchold_conversion()
    {
        var entity = Case(QaSourceTypes.InventoryStock, accepted: 0m, rejected: 3m);
        entity.PhysicalHolds.Add(new QaPhysicalHold
        {
            ReservedBaseQty = 3m,
            Status = QaLogicalHoldStatuses.Active
        });

        var destinations = QaDestinationComposer.Compose(entity);
        Assert.Contains(destinations, x =>
            !x.CanNavigate
            && x.Caption.Contains("reservation", StringComparison.OrdinalIgnoreCase)
            && x.Guidance.Contains("not a QCHOLD conversion", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Physical_scrap_allocation_is_executed()
    {
        var entity = Case(QaSourceTypes.InventoryStock, accepted: 0m, rejected: 3m);
        entity.PhysicalHolds.Add(new QaPhysicalHold
        {
            ReservedBaseQty = 3m,
            Status = QaLogicalHoldStatuses.Active,
            Allocations =
            {
                new QaPhysicalAllocation
                {
                    Qty = 3m,
                    Status = QaReceiptAllocationStatuses.Posted,
                    TrxType = IvTrxTypes.Scrap,
                    BatchNo = 44
                }
            }
        });

        var destinations = QaDestinationComposer.Compose(entity);
        var executed = Assert.Single(destinations, x => x.Status == QaDestinationStatuses.Executed);
        Assert.Equal(QaDestinationKinds.InventoryScrap, executed.Kind);
        Assert.Equal("44", executed.DocumentNo);
        Assert.True(executed.CanNavigate);
    }

    [Fact]
    public void Non_good_output_is_not_inventory_and_not_a_valued_lot()
    {
        var entity = new QaCase
        {
            CaseNo = "QC00000009",
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.ProductionOutputNonGood,
            Lines = { new QaCaseLine { Qty = 4m } }
        };

        var destinations = QaDestinationComposer.Compose(entity);
        var blocked = Assert.Single(destinations);
        Assert.Equal(QaDestinationStatuses.NotStock, blocked.Status);
        Assert.Equal(QaDestinationKinds.DailyProduction, blocked.Kind);
        Assert.False(blocked.CanNavigate);
        Assert.Contains("counter", blocked.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phase 5", blocked.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(destinations, x => x.Kind == QaDestinationKinds.InventoryScrap);
    }

    [Fact]
    public void Non_good_posted_allocation_is_executed_daily_production()
    {
        var grant = new QaNonGoodGrant { AcceptedQty = 4m };
        grant.Allocations.Add(new QaNonGoodAllocation
        {
            ProductionOutputId = 88,
            Qty = 4m,
            Status = QaReceiptAllocationStatuses.Posted
        });
        var entity = new QaCase
        {
            CaseNo = "QC00000010",
            Status = QaCaseStatuses.Approved,
            SourceType = QaSourceTypes.ProductionOutputNonGood,
            Lines = { new QaCaseLine { Qty = 4m } },
            NonGoodGrants = { grant }
        };

        var destinations = QaDestinationComposer.Compose(entity);
        var executed = Assert.Single(destinations, x => x.Status == QaDestinationStatuses.Executed);
        Assert.Equal(QaDestinationKinds.DailyProduction, executed.Kind);
        Assert.Equal("88", executed.DocumentNo);
        Assert.Equal("/planning/daily-production/view/88", executed.Href);
        Assert.Contains("valued lot", executed.Guidance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(destinations, x => x.Status == QaDestinationStatuses.NotStock);
    }

    [Fact]
    public void Composer_never_emits_executed_for_vendor_return_scrap_or_credit_note()
    {
        var sources = new[]
        {
            Case(QaSourceTypes.IncomingPreGrn, 10m, 2m),
            Case(QaSourceTypes.PostedGrn, 0m, 2m),
            Case(QaSourceTypes.InventoryStock, 0m, 2m),
            Case(QaSourceTypes.FgStagingLot, 10m, 2m, fg: true)
        };

        Assert.DoesNotContain(
            sources.SelectMany(QaDestinationComposer.Compose),
            x => x.Status == QaDestinationStatuses.Executed
                 && x.Kind is QaDestinationKinds.VendorReturn
                     or QaDestinationKinds.InventoryScrap
                     or QaDestinationKinds.PurchaseCreditNote);
    }

    [Fact]
    public void Opened_from_case_banner_does_not_claim_posting()
    {
        var banner = QaOpenedFromCase.Banner("qc1");
        Assert.Contains("QC1", banner);
        Assert.Contains("does not post", banner, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not create lines", banner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void First_pass_yield_uses_explicit_inspection_denominator()
    {
        var pass = Case(QaSourceTypes.IncomingPreGrn, 10m, 0m);
        pass.InspectedAtUtc = DateTime.UtcNow;
        var fail = Case(QaSourceTypes.IncomingPreGrn, 8m, 2m);
        fail.CaseNo = "QC00000002";
        fail.InspectedAtUtc = DateTime.UtcNow;
        var draft = Case(QaSourceTypes.IncomingPreGrn, 0m, 0m);
        draft.CaseNo = "QC00000003";
        draft.Status = QaCaseStatuses.Draft;
        draft.Grants.Clear();

        Assert.True(QaDestinationComposer.ReachedInspection(pass));
        Assert.True(QaDestinationComposer.IsFirstPass(pass));
        Assert.True(QaDestinationComposer.ReachedInspection(fail));
        Assert.False(QaDestinationComposer.IsFirstPass(fail));
        Assert.False(QaDestinationComposer.ReachedInspection(draft));
        Assert.Equal(3, QaDestinationComposer.AgingDays(pass, pass.CaseDate.Date.AddDays(3)));
        Assert.Equal(0, QaDestinationComposer.AgingDays(new QaCase
        {
            Status = QaCaseStatuses.Closed,
            CaseDate = DateTime.Today.AddDays(-9)
        }, DateTime.Today));
    }

    [Fact]
    public async Task Inquiry_does_not_mutate_stock_or_auto_link_by_document_number()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var entity = Case(QaSourceTypes.PostedGrn, 0m, 5m);
            entity.CompanyCode = "DEMO";
            entity.BranchCode = "HQ";
            entity.SourceDocNo = "VR-AUTO";
            entity.CreatedDate = DateTime.UtcNow;
            entity.CreatedBy = "tester";
            foreach (var disposition in entity.Lines.SelectMany(x => x.Dispositions).ToList())
            {
                disposition.Case = entity;
                entity.Dispositions.Add(disposition);
            }

            db.QaCases.Add(entity);
            await db.SaveChangesAsync();
        }

        var result = await new QaCaseService(_factory, _tenant.Object, _access.Object, new RunningNumberService())
            .GetInquiryAsync(new QaInquiryQuery());
        Assert.True(result.Succeeded, result.ErrorMessage);
        var row = Assert.Single(result.InquiryPage!.Rows);
        Assert.DoesNotContain(row.Destinations, x => x.Status == QaDestinationStatuses.Executed);
        Assert.Contains(row.Destinations, x => x.Status == QaDestinationStatuses.LinkedUnverified);
        Assert.Contains("divided by cases that reached inspection", result.InquiryPage.FirstPassYieldNote);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.IvBalLocs.CountAsync());
            Assert.Equal(0, await db.IvTrxBatches.CountAsync());
            Assert.Equal(0, await db.QaReceiptAllocations.CountAsync());
            Assert.Equal(0, await db.QaFgAllocations.CountAsync());
            Assert.Equal(1, await db.QaCases.CountAsync());
        }
    }

    private static QaCase Case(string sourceType, decimal accepted, decimal rejected, bool fg = false)
    {
        var entity = new QaCase
        {
            CaseNo = "QC00000001",
            CaseDate = DateTime.Today.AddDays(-2),
            Status = QaCaseStatuses.Approved,
            SourceType = sourceType,
            PoNo = "PO1",
            PoRelNo = 0,
            WorkOrderNo = "WO1",
            ProductionBalLotId = 44
        };
        var line = new QaCaseLine
        {
            LineNo = 1,
            ItemCode = "A100",
            Uom = "EA",
            Qty = accepted + rejected,
            InspectedQty = accepted + rejected
        };
        line.Dispositions.Add(new QaDispositionLine { Kind = QaDispositionKinds.Accept, Qty = accepted });
        line.Dispositions.Add(new QaDispositionLine { Kind = QaDispositionKinds.Reject, Qty = rejected });
        entity.Lines.Add(line);
        if (fg)
        {
            entity.FgGrants.Add(new QaFgGrant
            {
                AcceptedQty = accepted,
                RejectedQty = rejected,
                Status = QaReceiptGrantStatuses.Open,
                CaseLine = line
            });
        }
        else if (!string.Equals(sourceType, QaSourceTypes.PostedGrn, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sourceType, QaSourceTypes.InventoryStock, StringComparison.OrdinalIgnoreCase))
        {
            entity.Grants.Add(new QaReceiptGrant
            {
                AcceptedQty = accepted,
                RejectedQty = rejected,
                Status = QaReceiptGrantStatuses.Open,
                CaseLine = line
            });
        }

        return entity;
    }
}
