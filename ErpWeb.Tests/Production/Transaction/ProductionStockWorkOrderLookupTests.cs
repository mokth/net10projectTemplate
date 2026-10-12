using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Core.StockLedger.Costing;
using ErpWeb.Core.Transactions;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Tests.Infrastructure.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class ProductionStockWorkOrderLookupTests : IAsyncDisposable
{
    private static readonly DateTime EffectiveAt = new(2026, 10, 2, 8, 0, 0);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly IStockPostingCoordinator _coordinator;

    public ProductionStockWorkOrderLookupTests()
    {
        _connection.Open();
        _factory = ProductionLedgerTestFixture.CreateSqliteFactory(_connection);
        _coordinator = ProductionLedgerTestFixture.CreateCoordinator(_factory);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        ProductionLedgerTestFixture.SeedActiveEpochAsync(db, effectiveFrom: new DateTime(2026, 10, 1))
            .GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Return_lookup_requires_scoped_released_snapshot_and_structural_material_in_source()
    {
        var released = await SeedOrderAsync("WO-RT-RELEASED", ProductionWorkOrderStatuses.Released, materialIn: true);
        var inProgress = await SeedOrderAsync("WO-RT-INPROGRESS", ProductionWorkOrderStatuses.InProgress, materialIn: true);
        await SeedOrderAsync("WO-RT-DRAFT", ProductionWorkOrderStatuses.Draft, materialIn: true);
        await SeedOrderAsync("WO-RT-COMPLETED", ProductionWorkOrderStatuses.Completed, materialIn: true);
        await SeedOrderAsync("WO-RT-CLOSED", ProductionWorkOrderStatuses.Closed, materialIn: true);
        await SeedOrderAsync("WO-RT-CANCELLED", ProductionWorkOrderStatuses.Cancelled, materialIn: true);
        await SeedOrderAsync("WO-RT-LEGACY", ProductionWorkOrderStatuses.Released, materialIn: true, isLegacy: true);
        await SeedOrderAsync("WO-RT-OLD-FORMAT", ProductionWorkOrderStatuses.Released, materialIn: true,
            snapshotFormatVersion: ProductionSnapshotFormatVersions.Legacy);
        await SeedOrderAsync("WO-RT-EMPTY-HASH", ProductionWorkOrderStatuses.Released, materialIn: true, snapshotHash: "");
        await SeedOrderAsync("WO-RT-REVISION-ZERO", ProductionWorkOrderStatuses.Released, materialIn: true, snapshotRevision: 0);
        await SeedOrderAsync("WO-RT-NO-SOURCE", ProductionWorkOrderStatuses.Released, materialIn: false);
        await SeedOrderAsync("WO-RT-MALFORMED-SOURCE", ProductionWorkOrderStatuses.Released, materialIn: true,
            structurallyValidMaterialIn: false);
        await SeedOrderAsync("WO-RT-OTHER-COMPANY", ProductionWorkOrderStatuses.Released, materialIn: true, company: "OTHER");
        await SeedOrderAsync("WO-RT-OTHER-BRANCH", ProductionWorkOrderStatuses.Released, materialIn: true, branch: "OTHER");

        var service = CreateReturnService();
        var result = await service.SearchEligibleWorkOrdersAsync(new() { Take = 100 });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(new[] { released.WorkOrderNo, inProgress.WorkOrderNo }.Order(StringComparer.Ordinal),
            result.Rows.Select(x => x.WorkOrderNo).Order(StringComparer.Ordinal));
        Assert.All(result.Rows, row => Assert.Equal(1, row.CandidateSourceCount));
        Assert.Equal(released.SnapshotHash, result.Rows.Single(x => x.WorkOrderId == released.WorkOrderId).SnapshotHash);

        var resolved = await service.ResolveEligibleWorkOrderAsync(released.WorkOrderNo);
        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Equal(released.WorkOrderId, resolved.Item!.WorkOrderId);
        Assert.Equal(released.SnapshotHash, resolved.Item.SnapshotHash);

        var ineligible = await service.ResolveEligibleWorkOrderAsync("WO-RT-COMPLETED");
        Assert.False(ineligible.Succeeded);
        Assert.Contains("not currently eligible for Production Stock Return", ineligible.ErrorMessage);
    }

    [Fact]
    public async Task Return_lookup_searches_code_product_and_description_and_pages_blank_search()
    {
        for (var i = 0; i < 25; i++)
        {
            await SeedOrderAsync($"WO-RT-PAGE-{i:D2}", ProductionWorkOrderStatuses.Released,
                materialIn: true, productCode: $"FG-RT-{i:D2}", description: $"Return lookup product {i:D2}");
        }

        var service = CreateReturnService();
        var first = await service.SearchEligibleWorkOrdersAsync(new() { Take = 20 });
        var second = await service.SearchEligibleWorkOrdersAsync(new() { Skip = 20, Take = 20 });

        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(25, first.TotalCount);
        Assert.Equal(20, first.Rows.Count);
        Assert.Equal(5, second.Rows.Count);
        Assert.Empty(first.Rows.Select(x => x.WorkOrderId).Intersect(second.Rows.Select(x => x.WorkOrderId)));

        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "WO-RT-PAGE-11" })).Rows);
        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "FG-RT-11" })).Rows);
        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "lookup product 11" })).Rows);

        var resolved = await service.ResolveEligibleWorkOrderAsync("WO-RT-PAGE-11");
        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Equal("FG-RT-11", resolved.Item!.ProductCode);
        Assert.Equal("Return lookup product 11", resolved.Item.ProductDescription);
    }

    [Fact]
    public async Task Adjustment_lookup_accepts_material_in_and_empty_wip_but_applies_scope_status_and_snapshot_rules()
    {
        var materialIn = await SeedOrderAsync("WO-AJ-MATERIAL-IN", ProductionWorkOrderStatuses.Released, materialIn: true);
        var emptyWip = await SeedOrderAsync("WO-AJ-EMPTY-WIP", ProductionWorkOrderStatuses.InProgress, wip: true, wipBaseQty: 0m);
        await SeedOrderAsync("WO-AJ-NO-SOURCE", ProductionWorkOrderStatuses.Released, materialIn: false, wip: false);
        await SeedOrderAsync("WO-AJ-DRAFT", ProductionWorkOrderStatuses.Draft, wip: true);
        await SeedOrderAsync("WO-AJ-LEGACY", ProductionWorkOrderStatuses.Released, wip: true, isLegacy: true);
        await SeedOrderAsync("WO-AJ-OLD-FORMAT", ProductionWorkOrderStatuses.Released, wip: true,
            snapshotFormatVersion: ProductionSnapshotFormatVersions.Legacy);
        await SeedOrderAsync("WO-AJ-OTHER-COMPANY", ProductionWorkOrderStatuses.Released, wip: true, company: "OTHER");
        await SeedOrderAsync("WO-AJ-OTHER-BRANCH", ProductionWorkOrderStatuses.Released, wip: true, branch: "OTHER");

        var service = CreateAdjustmentService();
        var result = await service.SearchEligibleWorkOrdersAsync(new() { Take = 100 });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(new[] { materialIn.WorkOrderNo, emptyWip.WorkOrderNo }.Order(StringComparer.Ordinal),
            result.Rows.Select(x => x.WorkOrderNo).Order(StringComparer.Ordinal));
        Assert.All(result.Rows, row => Assert.Equal(1, row.CandidateSourceCount));

        var resolved = await service.ResolveEligibleWorkOrderAsync(emptyWip.WorkOrderNo);
        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Equal(emptyWip.WorkOrderId, resolved.Item!.WorkOrderId);
        Assert.Equal(emptyWip.SnapshotHash, resolved.Item.SnapshotHash);

        var ineligible = await service.ResolveEligibleWorkOrderAsync("WO-AJ-DRAFT");
        Assert.False(ineligible.Succeeded);
        Assert.Contains("not currently eligible for Production Stock Adjustment", ineligible.ErrorMessage);
    }

    [Fact]
    public async Task Adjustment_lookup_searches_code_product_description_and_returns_stable_bounded_pages()
    {
        for (var i = 0; i < 25; i++)
        {
            await SeedOrderAsync($"WO-AJ-PAGE-{i:D2}", ProductionWorkOrderStatuses.Released,
                wip: true, productCode: $"FG-AJ-{i:D2}", description: $"Adjustment lookup product {i:D2}");
        }

        var service = CreateAdjustmentService();
        var first = await service.SearchEligibleWorkOrdersAsync(new() { Take = 20 });
        var second = await service.SearchEligibleWorkOrdersAsync(new() { Skip = 20, Take = 20 });

        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(25, first.TotalCount);
        Assert.Equal(20, first.Rows.Count);
        Assert.Equal(5, second.Rows.Count);
        Assert.Empty(first.Rows.Select(x => x.WorkOrderId).Intersect(second.Rows.Select(x => x.WorkOrderId)));

        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "WO-AJ-PAGE-11" })).Rows);
        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "FG-AJ-11" })).Rows);
        Assert.Single((await service.SearchEligibleWorkOrdersAsync(new() { SearchText = "lookup product 11" })).Rows);

        var resolved = await service.ResolveEligibleWorkOrderAsync("WO-AJ-PAGE-11");
        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Equal("FG-AJ-11", resolved.Item!.ProductCode);
        Assert.Equal("Adjustment lookup product 11", resolved.Item.ProductDescription);
    }

    private async Task<(long WorkOrderId, string WorkOrderNo, string SnapshotHash)> SeedOrderAsync(
        string workOrderNo,
        string status,
        bool materialIn = false,
        bool wip = false,
        decimal wipBaseQty = 0m,
        bool structurallyValidMaterialIn = true,
        string company = "DEMO",
        string branch = "HQ",
        string productCode = "FG-LOOKUP",
        string description = "Production stock lookup product",
        int snapshotFormatVersion = ProductionSnapshotFormatVersions.Current,
        int snapshotRevision = 1,
        string? snapshotHash = null,
        bool isLegacy = false)
    {
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
        try
        {
            var hash = snapshotHash ?? new string('A', 64);
            var order = new ProductionWorkOrder
            {
                CompanyCode = company,
                BranchCode = branch,
                WorkOrderNo = workOrderNo,
                ProductCode = productCode,
                ProductDescription = description,
                OutputUom = "EA",
                Status = status,
                PlannedQty = 10m,
                RemainingQty = 10m,
                SnapshotRevision = snapshotRevision,
                SnapshotHash = hash,
                SnapshotHashVersion = ProductionSnapshotHashVersions.Current,
                SnapshotFormatVersion = snapshotFormatVersion,
                IsLegacySnapshot = isLegacy,
                DefinitionEffectiveDate = EffectiveAt.Date,
                PlannedStartDateTime = EffectiveAt.Date,
                PlannedCompletionDateTime = EffectiveAt.Date.AddDays(1),
                RowVersion = [1],
            };
            db.ProductionWorkOrders.Add(order);
            await db.SaveChangesAsync();

            if (materialIn)
            {
                var pool = CreatePool(order, ProductionBalLotKinds.MaterialIn,
                    10m,
                    structurallyValidMaterialIn ? 10L : null,
                    structurallyValidMaterialIn ? 10L : null,
                    structurallyValidMaterialIn ? 10 : null);
                db.ProductionBalLots.Add(pool);
            }

            if (wip)
                db.ProductionBalLots.Add(CreatePool(order, ProductionBalLotKinds.Wip, wipBaseQty, null, null, null));

            await db.SaveChangesAsync();
            return (order.Uid, order.WorkOrderNo, hash);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
    }

    private static ProductionBalLot CreatePool(
        ProductionWorkOrder order,
        string kind,
        decimal baseQty,
        long? issueMovementId,
        long? workOrderMaterialId,
        int? sourceBalanceId) => new()
    {
        CompanyCode = order.CompanyCode,
        BranchCode = order.BranchCode,
        Kind = kind,
        ItemCode = $"MAT-{order.WorkOrderNo}",
        Description = order.ProductDescription,
        Qty = baseQty,
        Uom = "EA",
        BaseQty = baseQty,
        BaseUom = "EA",
        ConversionFactorToBase = 1m,
        TotalCost = 0m,
        AverageUnitCost = 0m,
        WorkOrderId = order.Uid,
        WorkOrderNo = order.WorkOrderNo,
        WorkOrderMaterialId = workOrderMaterialId,
        OriginalIssueMovementId = issueMovementId,
        SourceIvBalLocId = sourceBalanceId,
        WarehouseCode = "MAIN",
        LocationCode = "SITE",
        LotNo = $"LOT-{order.WorkOrderNo}",
        LastMovementDate = EffectiveAt,
        RowVersion = [1],
    };

    private ProductionStockReturnService CreateReturnService() => new(
        _factory,
        InventoryTenantTestHelper.CreateTenantContext(),
        ProductionLedgerTestFixture.Allow(MenuCodes.PlanningStockReturn),
        new FixedCurrentDateService(EffectiveAt.AddDays(1)),
        new TestRunningNumberService(),
        new BranchStockTransactionLock(),
        _coordinator,
        new Mock<IIvStockPostingRepository>().Object,
        new ProductionStockWriter(new StockMovementRegistry()),
        new Mock<IIvInventoryHistoryWriter>().Object,
        new Mock<IIvInventoryPostingService>().Object,
        Options.Create(new ProductionStockTransactionOptions { ReturnPostingEnabled = true }));

    private ProductionStockAdjustmentService CreateAdjustmentService() => new(
        _factory,
        InventoryTenantTestHelper.CreateTenantContext(),
        ProductionLedgerTestFixture.Allow(MenuCodes.PlanningStockAdjustment),
        new FixedCurrentDateService(EffectiveAt.AddDays(1)),
        new TestRunningNumberService(),
        new BranchStockTransactionLock(),
        _coordinator,
        new ProductionStockWriter(new StockMovementRegistry()),
        new ItemStandardCostResolver(),
        Options.Create(new ProductionStockTransactionOptions { AdjustmentPostingEnabled = true }));

    public ValueTask DisposeAsync() => _connection.DisposeAsync();

    private sealed class TestRunningNumberService : IRunningNumberService
    {
        public Task<int> PeekNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(100);
        public Task<int> GetNextAsync(AppDbContext db, string companyCode, string docKey, CancellationToken cancellationToken = default) => Task.FromResult(100);
    }
}
