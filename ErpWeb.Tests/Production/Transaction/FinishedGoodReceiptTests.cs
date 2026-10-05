using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Production;
using ErpWeb.Core.Services;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Planning;
using ErpWeb.Model.Entities.Production;
using ErpWeb.Model.Entities.StockLedger;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Production.Transaction;

[Trait(TestCategories.Name, TestCategories.Production)]
public sealed class FinishedGoodReceiptMathTests
{
    [Fact]
    public void Average_pool_transfers_72_and_retains_48()
    {
        var value = FinishedGoodReceiptMath.AllocateValue(10, 120, [(1, 6)]);
        Assert.Equal(72, value[1]); Assert.Equal(48, 120 - value[1]);
    }
    [Fact]
    public void Final_depletion_and_many_lines_conserve_exact_value()
    {
        var value = FinishedGoodReceiptMath.AllocateValue(3, 10, [(9, 1), (1, 1), (2, 1)]);
        Assert.Equal(10, value.Values.Sum()); Assert.Equal(3.3334m, value[9]);
    }
    [Fact]
    public void Tiny_values_do_not_create_negative_remainders()
    {
        var value = FinishedGoodReceiptMath.AllocateValue(4, .0002m, [(1, 1), (2, 1), (3, 1), (4, 1)]);
        Assert.Equal(.0002m, value.Values.Sum()); Assert.All(value.Values, x => Assert.True(x >= 0));
    }
    [Fact]
    public void Conversion_conserves_base_and_rejects_loss()
    {
        Assert.Equal((3m, 1.5m), FinishedGoodReceiptMath.Convert(1, 3, 2));
        Assert.Throws<InvalidOperationException>(() => FinishedGoodReceiptMath.Convert(.0001m, .0001m, 1));
        Assert.Throws<InvalidOperationException>(() => FinishedGoodReceiptMath.Convert(1, 1, 3));
    }
}

[Trait(TestCategories.Name, TestCategories.Production)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public sealed class FinishedGoodReceiptSqlServerTests
{
    private static readonly DateTime BusinessDate = new(2026, 10, 4, 10, 0, 0);
    private static async Task<Fixture?> CreateAsync(bool lotControlled = false, string? existingCompany = null, string branch = "HQ")
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__SqlServerTestConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            Assert.False(Environment.GetEnvironmentVariable("ERPWEB_REQUIRE_SQLSERVER_TESTS") == "1", "FG tests require an isolated SQL Server test connection.");
            return null;
        }
        Assert.Contains("test", new SqlConnectionStringBuilder(cs).InitialCatalog, StringComparison.OrdinalIgnoreCase);
        var factory = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options);
        await using var db = await ((IDbContextFactory<AppDbContext>)factory).CreateDbContextAsync(); await db.Database.EnsureCreatedAsync();
        var company = existingCompany ?? "F" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var order = new ProductionWorkOrder { CompanyCode = company, BranchCode = branch, WorkOrderNo = "WO-FG-" + branch, Status = "COMPLETED",
            ProductCode = "FG", PlannedQty = 10, OutputUom = "EA", CreatedBy = "TEST" };
        var bom = await db.PrBomHdrs.FirstOrDefaultAsync(x => x.CompanyCode == company && x.ProdCode == "FG");
        if (bom is null) { bom = new PrBomHdr { CompanyCode = company, ProdCode = "FG", DefinitionCode = "FG", BaseQty = 1, BaseUom = "EA" }; db.PrBomHdrs.Add(bom); await db.SaveChangesAsync(); }
        order.SourceBomHdrId = bom.Uid;
        db.ProductionWorkOrders.Add(order);
        if (!await db.IvStockMasters.AnyAsync(x => x.CompanyCode == company && x.ICode == "FG")) db.IvStockMasters.Add(new() { CompanyCode = company, ICode = "FG", StdUom = "EA", StockControl = true, LotControl = lotControlled });
        db.IvWarehouses.Add(new() { CompanyCode = company, BranchCode = branch, WarehouseCode = "WH" });
        var location = new ProductionLocation { CompanyCode = company, BranchCode = branch, Code = "FG", Description = "FG staging" };
        db.ProductionLocations.Add(location);
        await db.SaveChangesAsync();
        var route = new ProductionWorkOrderRouteStep { WorkOrderId = order.Uid, StageSequence = 1, WorkCentreCode = "WC", OutputItemCode = "FG", OutputUom = "EA", OutputType = "FINISHED_GOODS", OutputBaseUom = "EA", OutputConversionFactorToBase = 1 };
        db.ProductionWorkOrderRouteSteps.Add(route); await db.SaveChangesAsync();
        var operation = new ProductionWorkOrderOperation { WorkOrderId = order.Uid, RouteStepId = route.Uid, ProcessSequence = 1, IsFinalOperation = true,
            OperationCode = "PACK", WorkCentreCode = "WC", ProcessType = "MANUAL", PlannedOutputQty = 10, PlannedOutputUom = "EA" };
        db.ProductionWorkOrderOperations.Add(operation); await db.SaveChangesAsync();
        var pool = new ProductionBalLot { CompanyCode = company, BranchCode = branch, Kind = "WIP", ItemCode = "FG", Uom = "EA", BaseUom = "EA",
            ConversionFactorToBase = 1, WorkOrderId = order.Uid, WorkOrderNo = order.WorkOrderNo, ProducingRouteStepId = route.Uid,
            WorkOrderOperationId = operation.Uid, OutputType = "FINISHED_GOODS", BalanceStage = "FG_STAGING", StockStatusCode = "AVAILABLE",
            OriginType = "PRODUCED", PhysicalLotNo = "PROD-1", LotNo = "PROD-1", ProductionLocationId = location.Id };
        db.ProductionBalLots.Add(pool); await db.SaveChangesAsync();
        db.StockLedgerEpochs.Add(new() { CompanyCode = company, BranchCode = branch, Version = 2, Status = "ACTIVE",
            EffectiveFrom = BusinessDate.AddDays(-1), MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('A', 64) });
        await db.SaveChangesAsync();
        var fixture = new Fixture(factory, company, order.Uid, pool.Uid, route.Uid, operation.Uid, branch);
        // The third contributor remains unused by FIFO for a six-unit receipt, but it must
        // still be retained as a pooled-value dependency.
        await fixture.AddContribution(3, 30); await fixture.AddContribution(3, 42); await fixture.AddContribution(4, 48);
        return fixture;
    }

    [Fact]
    public async Task Partial_receipt_replay_exact_history_reversal_and_correction()
    {
        var f = await CreateAsync(); if (f is null) return;
        var saved = await f.Service.SaveAsync(f.Draft(6)); Assert.True(saved.Succeeded, saved.Message);
        var request = new FinishedGoodReceiptCommand(saved.Data!.Id, saved.Data.RowVersion, Guid.NewGuid());
        var posted = await f.Service.PostAsync(request); Assert.True(posted.Succeeded, posted.Message);
        Assert.Equal(72, posted.Data!.Lines.Single().TotalValue);
        var replay = await f.Service.PostAsync(request); Assert.True(replay.Succeeded, replay.Message); Assert.Equal(posted.Data.PostingId, replay.Data!.PostingId);
        f.CanViewCost = false;
        var restrictedReplay = await f.Service.PostAsync(request);
        Assert.True(restrictedReplay.Succeeded, restrictedReplay.Message); Assert.Null(restrictedReplay.Data!.Lines[0].TotalValue);
        f.CanViewCost = true;
        Assert.False((await f.Service.PostAsync(request with { ExpectedVersion = posted.Data.RowVersion })).Succeeded);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == f.PoolId);
            Assert.Equal(4, pool.BaseQty); Assert.Equal(48, pool.TotalCost);
            var history = await db.IvTrxHistories.SingleAsync(x => x.CompanyCode == f.Company);
            Assert.Equal(72, history.ExactTransferredValue);
            Assert.Equal(3, await db.ProductionPoolDependencyRows.CountAsync(x => x.ConsumerMovementId == db.ProductionFinishedGoodFactRows.Where(y => y.BatchId == saved.Data.Id).Select(y => y.ProductionMovementId).Single()));
        }
        var reverseRequest = new FinishedGoodReceiptCommand(posted.Data.Id, posted.Data.RowVersion, Guid.NewGuid(), "Correction");
        var reversed = await f.Service.RollbackAsync(reverseRequest); Assert.True(reversed.Succeeded, reversed.Message); Assert.Equal("REVERSED", reversed.Data!.Status);
        Assert.True((await f.Service.RollbackAsync(reverseRequest)).Succeeded);
        Assert.Equal(posted.Data.PostingId, (await f.Service.PostAsync(request)).Data!.PostingId);
        var correction = await f.Service.CreateCorrectionAsync(saved.Data.Id); Assert.True(correction.Succeeded, correction.Message);
        Assert.NotEqual(saved.Data.Id, correction.Data!.Id); Assert.Null(correction.Data.PostingId);
        await using var verify = await f.Factory.CreateDbContextAsync();
        Assert.Equal(120, (await verify.ProductionBalLots.SingleAsync(x => x.Uid == f.PoolId)).TotalCost);
        Assert.Equal(0, (await verify.IvBalLocs.SingleAsync(x => x.CompanyCode == f.Company)).StdQty);
        Assert.Empty(await verify.IvLots.Where(x => x.CompanyCode == f.Company).ToListAsync());
    }

    [Fact]
    public async Task All_draft_edits_invalidate_stale_clients_and_restrict_costs()
    {
        var f = await CreateAsync(); if (f is null) return;
        var first = await f.Service.SaveAsync(f.Draft(4)); Assert.True(first.Succeeded, first.Message);
        var update = f.Draft(4); update.Id = first.Data!.Id; update.ExpectedVersion = first.Data.RowVersion; update.RefNo = "Changed header";
        var second = await f.Service.SaveAsync(update); Assert.True(second.Succeeded, second.Message);
        Assert.False(first.Data.RowVersion.SequenceEqual(second.Data!.RowVersion));
        Assert.False((await f.Service.DeleteAsync(first.Data.Id, first.Data.RowVersion)).Succeeded);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var genericWrite = await db.IvTrxBatches.SingleAsync(x => x.Id == first.Data.Id);
            genericWrite.RefNo = "Bypass";
            var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            Assert.Contains("FG service", blocked.Message, StringComparison.OrdinalIgnoreCase);
        }
        f.CanViewCost = false; var restricted = await f.Service.GetAsync(first.Data.Id); Assert.Null(restricted.Data!.Lines[0].TotalValue);
        f.AllowAccess = false; Assert.False((await f.Service.GetAsync(first.Data.Id)).Succeeded);
    }

    [Fact]
    public async Task Concurrent_source_competition_and_lost_response_duplicate_requests()
    {
        var f = await CreateAsync(); if (f is null) return;
        var a = await f.Service.SaveAsync(f.Draft(6)); var b = await f.Service.SaveAsync(f.Draft(6));
        Assert.True(a.Succeeded, a.Message); Assert.True(b.Succeeded, b.Message);
        var outcomes = await Task.WhenAll(f.Service.PostAsync(new(a.Data!.Id, a.Data.RowVersion, Guid.NewGuid())), f.Service.PostAsync(new(b.Data!.Id, b.Data.RowVersion, Guid.NewGuid())));
        Assert.Single(outcomes.Where(x => x.Succeeded));
        var remaining = await f.Service.SaveAsync(f.Draft(4)); Assert.True(remaining.Succeeded, remaining.Message);
        var command = new FinishedGoodReceiptCommand(remaining.Data!.Id, remaining.Data.RowVersion, Guid.NewGuid());
        var duplicates = await Task.WhenAll(f.Service.PostAsync(command), f.Service.PostAsync(command));
        Assert.All(duplicates, x => Assert.True(x.Succeeded, x.Message)); Assert.Equal(duplicates[0].Data!.PostingId, duplicates[1].Data!.PostingId);
        await using var db = await f.Factory.CreateDbContextAsync(); var pool = await db.ProductionBalLots.SingleAsync(x => x.Uid == f.PoolId);
        Assert.Equal(0, pool.BaseQty); Assert.Equal(0, pool.TotalCost);
    }

    [Fact]
    public async Task Matching_lot_topups_inherit_expiry_and_ordered_reversals_restore_price()
    {
        var f = await CreateAsync(true); if (f is null) return;
        var draft = f.Draft(4); draft.Lines[0].ExpiryDate = BusinessDate.AddMonths(1);
        var a = await f.Service.SaveAsync(draft); Assert.True(a.Succeeded, a.Message);
        var ap = await f.Service.PostAsync(new(a.Data!.Id, a.Data.RowVersion, Guid.NewGuid())); Assert.True(ap.Succeeded, ap.Message);
        var b = await f.Service.SaveAsync(f.Draft(6)); var bp = await f.Service.PostAsync(new(b.Data!.Id, b.Data.RowVersion, Guid.NewGuid())); Assert.True(bp.Succeeded, bp.Message);
        Assert.Equal(draft.Lines[0].ExpiryDate!.Value.Date, bp.Data!.Lines[0].ExpiryDate);
        Assert.False((await f.Service.RollbackAsync(new(ap.Data!.Id, ap.Data.RowVersion, Guid.NewGuid(), "Too early"))).Succeeded);
        Assert.True((await f.Service.RollbackAsync(new(bp.Data.Id, bp.Data.RowVersion, Guid.NewGuid(), "Last first"))).Succeeded);
        Assert.True((await f.Service.RollbackAsync(new(ap.Data.Id, ap.Data.RowVersion, Guid.NewGuid(), "Original"))).Succeeded);
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Single(await db.ProductionFinishedGoodLotOriginRows.Where(x => x.CompanyCode == f.Company).ToListAsync());
    }

    [Fact]
    public async Task Unknown_pool_cannot_be_cleansed_by_verified_replenishment()
    {
        var f = await CreateAsync(); if (f is null) return;
        await using (var db = await f.Factory.CreateDbContextAsync())
        { var pool = await db.ProductionPoolValuationRows.SingleAsync(x => x.ProductionBalLotId == f.PoolId); pool.Status = "UNVALUED"; await db.SaveChangesAsync(); }
        await f.AddContribution(2, 0);
        var draft = await f.Service.SaveAsync(f.Draft(1)); Assert.True(draft.Succeeded, draft.Message);
        Assert.False((await f.Service.PostAsync(new(draft.Data!.Id, draft.Data.RowVersion, Guid.NewGuid()))).Succeeded);
    }

    private sealed class FailureWriter(Func<bool> fail) : IProductionStockWriter
    {
        public async Task<IReadOnlyList<ProductionBalLotMovement>> ApplyAsync(StockPostingContext context, IReadOnlyCollection<ProductionStockLeg> legs, CancellationToken cancellationToken = default)
        {
            var result = await new ProductionStockWriter(new StockMovementRegistry()).ApplyAsync(context, legs, cancellationToken);
            if (fail()) throw new InvalidOperationException("Injected failure after stock legs");
            return result;
        }
    }

    [Fact]
    public async Task Failure_after_both_stock_legs_leaves_no_partial_facts_or_lot_ownership()
    {
        var f = await CreateAsync(true); if (f is null) return;
        var saved = await f.Service.SaveAsync(f.Draft(6)); Assert.True(saved.Succeeded, saved.Message);
        f.FailAfterProduction = true;
        var command = new FinishedGoodReceiptCommand(saved.Data!.Id, saved.Data.RowVersion, Guid.NewGuid());
        var failed = await f.Service.PostAsync(command); Assert.False(failed.Succeeded);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            Assert.Equal(10, (await db.ProductionBalLots.SingleAsync(x => x.Uid == f.PoolId)).BaseQty);
            Assert.Empty(await db.IvTrxHistories.Where(x => x.CompanyCode == f.Company).ToListAsync());
            Assert.Empty(await db.IvLots.Where(x => x.CompanyCode == f.Company).ToListAsync());
            Assert.Empty(await db.StockPostings.Where(x => x.RequestId == command.RequestId).ToListAsync());
            Assert.Equal("NEW", (await db.IvTrxBatches.SingleAsync(x => x.Id == saved.Data.Id)).BatchStatus);
        }
        f.FailAfterProduction = false; Assert.True((await f.Service.PostAsync(command)).Succeeded);
    }

    [Fact]
    public async Task Cross_branch_creation_serializes_company_wide_lot_identity()
    {
        var a = await CreateAsync(true); if (a is null) return;
        var b = await CreateAsync(true, a.Company, "B2"); Assert.NotNull(b);
        var da = a.Draft(4); da.Lines[0].LotNo = "SHARED";
        var db = b.Draft(4); db.Lines[0].LotNo = "SHARED";
        var sa = await a.Service.SaveAsync(da); var sb = await b.Service.SaveAsync(db);
        Assert.True(sa.Succeeded, sa.Message); Assert.True(sb.Succeeded, sb.Message);
        var results = await Task.WhenAll(a.Service.PostAsync(new(sa.Data!.Id, sa.Data.RowVersion, Guid.NewGuid())), b.Service.PostAsync(new(sb.Data!.Id, sb.Data.RowVersion, Guid.NewGuid())));
        Assert.Single(results.Where(x => x.Succeeded));
        await using var verify = await a.Factory.CreateDbContextAsync();
        Assert.Single(await verify.IvLots.Where(x => x.CompanyCode == a.Company && x.LotNo == "SHARED").ToListAsync());
        Assert.Single(await verify.ProductionFinishedGoodLotOriginRows.Where(x => x.CompanyCode == a.Company).ToListAsync());
    }

    [Fact]
    public async Task Period_close_and_post_share_the_branch_lock()
    {
        var f = await CreateAsync(); if (f is null) return;
        var saved = await f.Service.SaveAsync(f.Draft(4)); Assert.True(saved.Succeeded, saved.Message);
        await using var db = await f.Factory.CreateDbContextAsync(); await using var tx = await db.Database.BeginTransactionAsync();
        await new BranchStockTransactionLock().AcquireAsync(db, f.Company, "HQ");
        var post = f.Service.PostAsync(new(saved.Data!.Id, saved.Data.RowVersion, Guid.NewGuid()));
        await Task.Delay(150); Assert.False(post.IsCompleted);
        db.IvPeriodCloseHdrs.Add(new() { CompanyCode = f.Company, BranchCode = "HQ", PeriodFrom = BusinessDate.Date, PeriodTo = BusinessDate.Date, Status = "CLOSED" });
        await db.SaveChangesAsync(); await tx.CommitAsync();
        var result = await post; Assert.False(result.Succeeded); Assert.Contains("closed", result.Message!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Fixture
    {
        public IDbContextFactory<AppDbContext> Factory { get; }
        public string Company { get; }
        public long OrderId { get; }
        public long PoolId { get; }
        private readonly long _route, _operation;
        private readonly string _branch;
        public bool FailAfterProduction;
        public bool CanViewCost = true, AllowAccess = true;
        public ProductionFinishedGoodReceiptService Service { get; }
        private readonly StockPostingCoordinator _coordinator;
        public Fixture(TestDbContextFactory factory, string company, long order, long pool, long route, long operation, string branch = "HQ")
        {
            _branch = branch;
            Factory = factory; Company = company; OrderId = order; PoolId = pool; _route = route; _operation = operation;
            var tenant = InventoryTenantTestHelper.CreateTenantContext(company: company, branch: branch);
            var access = new Mock<IAccessRightService>(); access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string _, string permission, CancellationToken _) => Task.FromResult(AllowAccess && (permission != PermissionCodes.ViewCost || CanViewCost)));
            var numbers = new Mock<IRunningNumberService>(); var next = 0;
            numbers.Setup(x => x.GetNextAsync(It.IsAny<AppDbContext>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => Interlocked.Increment(ref next));
            var clock = new Mock<ICurrentDateService>(); clock.SetupGet(x => x.Now).Returns(BusinessDate);
            _coordinator = new(factory, tenant, new BranchStockTransactionLock(), new StockPeriodGuard(), new NoActiveStockFreezeGuard());
            Service = new(factory, tenant, access.Object, clock.Object, numbers.Object, new BranchStockTransactionLock(), _coordinator,
                new IvStockPostingRepository(), new FailureWriter(() => FailAfterProduction), new IvInventoryHistoryWriter(), Options.Create(new FinishedGoodReceiptOptions { PostingEnabled = true }));
        }
        public FinishedGoodReceiptSaveRequest Draft(decimal qty) => new() { WorkOrderId = OrderId, EffectiveDate = BusinessDate,
            Lines = [new() { ProductionBalLotId = PoolId, Quantity = qty, Warehouse = "WH" }] };
        public async Task AddContribution(decimal qty, decimal value)
        {
            var request = Guid.NewGuid();
            var command = new StockPostingCommand { RequestId = request, CommandType = "FG_TEST_SEED", SourceModule = "TEST", SourceDocumentType = "SEED",
                SourceDocumentId = request.ToString(), SourceDocumentNo = "SEED", DocumentRevision = 1, EffectiveAt = BusinessDate.AddHours(-1),
                Evidence = StockPostingFingerprint.Create(new { qty, value }, new { qty, value }) };
            var result = await _coordinator.ExecuteAsync(command, async (context, ct) => {
                var db = context.Db; var lot = await db.ProductionBalLots.SingleAsync(x => x.Uid == PoolId, ct);
                var link = new ProductionPostingLink { CompanyCode = Company, BranchCode = _branch, WorkOrderId = OrderId, CommandType = "TEST", PostingRequestId = request.ToString(), CreatedBy = "TEST", CreatedDate = BusinessDate };
                db.ProductionPostingLinks.Add(link); await db.SaveChangesAsync(ct);
                var movements = await new ProductionStockWriter(new StockMovementRegistry()).ApplyAsync(context,
                    [new(lot, "PRODUCE", qty, qty, OrderId, null, _operation, _route, link.Uid, "SEED", "SEED", request.ToString(), 0, ExactTotalValue: value, ValuationStatus: "VERIFIED")], ct);
                await db.SaveChangesAsync(ct);
                // Explicit fixture evidence represents upstream verification; runtime never upgrades sealed history.
                var projection = await db.ProductionPoolValuationRows.SingleOrDefaultAsync(x => x.ProductionBalLotId == PoolId, ct);
                if (projection is null) { projection = new() { ProductionBalLotId = PoolId, Status = "VERIFIED" }; db.ProductionPoolValuationRows.Add(projection); }
                projection.TrackedBaseQty = lot.BaseQty; projection.TrackedValue = lot.TotalCost;
                db.ProductionValuationEvidenceRows.Add(new() { MovementId = movements[0].Uid, ProductionBalLotId = PoolId, Generation = projection.Generation, Status = "VERIFIED", Basis = "TEST_EXPLICIT" });
                return true;
            });
            Assert.True(result.Succeeded, result.Error?.Message);
        }
    }
}
