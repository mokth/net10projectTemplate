using ErpWeb.Core.Inventory;
using ErpWeb.Core.Sales;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockReturn)]
public sealed class IvStockReturnLedgerAuthorityTests : IAsyncLifetime
{
    private const decimal TamperPrice = 999999m;
    private const decimal PurchasePrice = 777m;
    private const decimal OriginalUnitCost = 3.5m;
    private const decimal OriginalQty = 10m;
    private const string InvoiceNo = "INV-RET-1";
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockReturnLedgerAuthorityTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _factory = InventoryLedgerTestFixture.CreateSqliteFactory(_connection);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        await InventoryLedgerTestFixture.SeedCompanyAsync(db);
        await InventoryLedgerTestFixture.SeedActiveEpochAsync(db);
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            IsActive = true
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IClassCode = "RAW",
            IsActive = true
        });
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "A100",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            ICode = "B200",
            IDesc = "Other item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = PurchasePrice
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Save_requires_the_original_posted_invoice_line()
    {
        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var missing = await service.SaveNewAsync(Request(4m, sourceInvNo: "", sourceLine: 0));
        Assert.False(missing.Succeeded);
        Assert.Contains(
            "Select the original posted invoice and invoice line before saving this stock return.",
            missing.ErrorMessage);

        await SeedInvoiceAsync("INV-NEW", posted: false);
        var unposted = await service.SaveNewAsync(Request(4m, sourceInvNo: "INV-NEW"));
        Assert.False(unposted.Succeeded);
        Assert.Contains("posted", unposted.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await SeedInvoiceAsync(InvoiceNo, posted: true);
        var wrongItem = await service.SaveNewAsync(Request(4m, iCode: "B200"));
        Assert.False(wrongItem.Succeeded);
        Assert.Contains("does not match", wrongItem.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaInvoices.Add(new SaInvoice
            {
                CompanyCode = "OTHER",
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                InvNo = "INV-OTHER",
                CustCode = "C001",
                InvDate = FixedToday,
                Status = SaInvoiceStatuses.Posted,
                DoNo = string.Empty
            });
            db.SaInvoiceDetails.Add(new SaInvoiceDetail
            {
                CompanyCode = "OTHER",
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                InvNo = "INV-OTHER",
                Line = 1,
                ICode = "A100",
                Qty = 10m,
                StdQty = 10m,
                StdUom = "EA",
                StockControl = true,
                SoNo = string.Empty,
                DoNo = string.Empty
            });
            await db.SaveChangesAsync();
        }

        var otherTenant = await service.SaveNewAsync(Request(1m, sourceInvNo: "INV-OTHER"));
        Assert.False(otherTenant.Succeeded);
        Assert.Contains("was not found", otherTenant.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Moving_average_return_restores_original_cogs_and_ignores_request_price()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true);
        await SeedOutboundSaleAsync(InvoiceNo, "1", OriginalQty, OriginalUnitCost);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.IvTrxBatchDetails.SingleAsync();
            Assert.Equal(0m, detail.UnitPrice);
            Assert.Equal(InvoiceNo, detail.InvNo);
            Assert.Equal((short)1, detail.SoLineNo);
        }

        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "SALE_RETURN_IN");
        Assert.Equal(1, fact.Direction);
        Assert.Equal(OriginalUnitCost, fact.UnitCost);
        Assert.Equal(14m, fact.CostAmount);
        Assert.Equal(StockValuationSources.OriginalSaleReturn, fact.ValuationSource);
        Assert.NotEqual(TamperPrice, fact.UnitCost);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(104m, state.OnHandBaseQty);
        Assert.Equal(214m, state.InventoryValue);
        var history = await verify.IvTrxHistories.SingleAsync();
        Assert.Equal(InvoiceNo, history.InvNo);
        Assert.Equal((short)1, history.SoLineNo);
        var allocation = await verify.SalesReturnCostAllocations.SingleAsync();
        Assert.Equal(4m, allocation.ReturnedBaseQty);
        Assert.Equal(14m, allocation.ReturnedCostAmount);
    }

    [Fact]
    public async Task Do_linked_invoice_restores_the_delivery_outbound_cost()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true, linkDo: true, doNo: "DO-1", doLine: 7);
        await SeedOutboundSaleAsync("DO-1", "7", OriginalQty, OriginalUnitCost, ownerType: "SA_DO");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(4m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "SALE_RETURN_IN");
        Assert.Equal(14m, fact.CostAmount);
        Assert.Equal(StockValuationSources.OriginalSaleReturn, fact.ValuationSource);
        var allocation = await verify.SalesReturnCostAllocations.SingleAsync();
        Assert.Equal("SA_DO", allocation.OriginalOwnerType);
        Assert.Equal("DO-1", allocation.OriginalOwnerDocumentNo);
        Assert.Equal("7", allocation.OriginalOwnerDocumentLine);
    }

    [Fact]
    public async Task Partial_returns_consume_the_original_sale_until_it_is_exhausted()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true, stdQty: 100m);
        await SeedOutboundSaleAsync(InvoiceNo, "1", OriginalQty, OriginalUnitCost);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var first = await service.SaveNewAsync(Request(4m));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True((await service.PostAsync([first.BatchNo])).Succeeded);
        var second = await service.SaveNewAsync(Request(6m));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.True((await service.PostAsync([second.BatchNo])).Succeeded);
        var third = await service.SaveNewAsync(Request(1m));
        Assert.True(third.Succeeded, third.ErrorMessage);
        var blocked = await service.PostAsync([third.BatchNo]);
        Assert.False(blocked.Succeeded);
        Assert.Contains("exceeds the active returnable quantity", blocked.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var returned = await verify.StockValuationFacts
            .Where(x => x.MovementCode == "SALE_RETURN_IN")
            .SumAsync(x => x.CostAmount);
        Assert.Equal(35m, returned);
    }

    [Fact]
    public async Task Over_return_is_rejected_before_posting_when_the_invoice_quantity_is_exhausted()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true, stdQty: 4m);
        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var first = await service.SaveNewAsync(Request(4m));
        Assert.True(first.Succeeded, first.ErrorMessage);
        var second = await service.SaveNewAsync(Request(1m));
        Assert.False(second.Succeeded);
        Assert.Contains("exceeds the remaining quantity", second.ErrorMessage);
    }

    [Fact]
    public async Task Fifo_return_adds_a_layer_at_the_original_sale_cost()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true);
        await SeedOutboundSaleAsync(InvoiceNo, "1", OriginalQty, OriginalUnitCost, StockCostMethods.Fifo);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedFifoPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedFifoPoolAsync(db, "A100",
            [
                new FifoLayerSeed(5m, 3m, new DateTime(2026, 2, 1)),
                new FifoLayerSeed(10m, 4m, new DateTime(2026, 3, 1))
            ]);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(4m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "SALE_RETURN_IN");
        Assert.Equal(OriginalUnitCost, fact.UnitCost);
        Assert.Equal(14m, fact.CostAmount);
        Assert.NotEqual(PurchasePrice, fact.UnitCost);
        var layer = await verify.StockFifoLayers.SingleAsync(x => x.OriginValuationFactId == fact.Id);
        Assert.Equal(4m, layer.RemainingQty);
        Assert.Equal(14m, layer.RemainingValue);
    }

    [Fact]
    public async Task Standard_return_receipt_stays_at_standard_and_records_the_cogs_variance()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true);
        await SeedOutboundSaleAsync(InvoiceNo, "1", OriginalQty, OriginalUnitCost, StockCostMethods.Standard);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedStandardPolicyAsync(db);
            await InventoryLedgerTestFixture.SeedStandardCostAsync(db, "A100", 6.5m);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 6.5m, StockCostMethods.Standard);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(4m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var post = await service.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.MovementCode == "SALE_RETURN_IN");
        Assert.Equal(6.5m, fact.UnitCost);
        Assert.Equal(26m, fact.CostAmount);
        Assert.Equal(StockValuationSources.Standard, fact.ValuationSource);
        Assert.NotEqual(OriginalUnitCost, fact.UnitCost);
        var variance = await verify.SalesReturnStandardCostVariances.SingleAsync();
        Assert.Equal(26m, variance.CurrentStandardReceiptValue);
        Assert.Equal(14m, variance.OriginalCogsReversalValue);
        Assert.Equal(12m, variance.VarianceAmount);
    }

    [Fact]
    public async Task Rollback_reverses_the_return_fact_and_restores_the_returnable_quantity()
    {
        await SeedInvoiceAsync(InvoiceNo, posted: true);
        await SeedOutboundSaleAsync(InvoiceNo, "1", OriginalQty, OriginalUnitCost);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await InventoryLedgerTestFixture.SeedMovingAveragePolicyAsync(db);
            await InventoryLedgerTestFixture.SeedCostStateAsync(db, "A100", 100m, 2m);
        }

        var service = InventoryLedgerTestFixture.CreateStockReturn(_factory, FixedToday);
        var save = await service.SaveNewAsync(Request(OriginalQty));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await service.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await service.RollbackAsync([save.BatchNo])).Succeeded);

        await using (var verify = await _factory.CreateDbContextAsync())
        {
            var facts = await verify.StockValuationFacts
                .Where(x => x.MovementCode == "SALE_RETURN_IN" || x.MovementCode == "SALE_RETURN_IN_REVERSAL")
                .OrderBy(x => x.Id)
                .ToListAsync();
            Assert.Equal(2, facts.Count);
            Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
            Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
            Assert.Equal(2, await verify.SalesReturnCostAllocations.CountAsync());
            Assert.Equal(1, await verify.SalesReturnCostAllocations.CountAsync(x => x.ReversesAllocationId != null));
            var state = await verify.StockCostStates.SingleAsync();
            Assert.Equal(100m, state.OnHandBaseQty);
            Assert.Equal(200m, state.InventoryValue);
        }

        var again = await service.SaveNewAsync(Request(OriginalQty));
        Assert.True(again.Succeeded, again.ErrorMessage);
        var repost = await service.PostAsync([again.BatchNo]);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
    }

    private async Task SeedInvoiceAsync(
        string invNo,
        bool posted,
        decimal stdQty = 100m,
        bool linkDo = false,
        string doNo = "",
        short? doLine = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SaInvoices.Add(new SaInvoice
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            InvNo = invNo,
            CustCode = "C001",
            InvDate = FixedToday,
            Status = posted ? SaInvoiceStatuses.Posted : "NEW",
            TotAmnt = 1_000_000m,
            DoNo = doNo
        });
        db.SaInvoiceDetails.Add(new SaInvoiceDetail
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            InvNo = invNo,
            Line = 1,
            ICode = "A100",
            IDesc = "Stock item",
            Qty = stdQty,
            StdQty = stdQty,
            StdUom = "EA",
            StockControl = true,
            SoNo = string.Empty,
            DoNo = doNo,
            LinkDo = linkDo,
            DoLine = doLine
        });
        await db.SaveChangesAsync();
    }

    private async Task SeedOutboundSaleAsync(
        string ownerNo,
        string ownerLine,
        decimal qty,
        decimal unitCost,
        string costMethod = StockCostMethods.MovingAverage,
        string ownerType = "SA_INVOICE")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var epochId = await db.StockLedgerEpochs
            .Where(x => x.CompanyCode == InventoryLedgerTestFixture.CompanyCode
                        && x.BranchCode == InventoryLedgerTestFixture.BranchCode
                        && x.Status == StockLedgerEpochStatuses.Active)
            .Select(x => x.Id)
            .SingleAsync();
        var sequence = await db.StockPostingBranchSequences
            .SingleOrDefaultAsync(x => x.CompanyCode == InventoryLedgerTestFixture.CompanyCode
                                       && x.BranchCode == InventoryLedgerTestFixture.BranchCode);
        if (sequence is null)
        {
            sequence = new StockPostingBranchSequence
            {
                CompanyCode = InventoryLedgerTestFixture.CompanyCode,
                BranchCode = InventoryLedgerTestFixture.BranchCode,
                LastSequence = 0,
                UpdatedAtUtc = DateTime.UtcNow,
                RowVersion = [1]
            };
            db.StockPostingBranchSequences.Add(sequence);
        }

        sequence.LastSequence++;
        var amount = decimal.Round(qty * unitCost, 6, MidpointRounding.AwayFromZero);
        var posting = new StockPosting
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            LedgerEpochId = epochId,
            PostingSequence = sequence.LastSequence,
            RequestId = Guid.NewGuid(),
            CommandType = "SALE_SEED",
            RequestFingerprint = new string('D', 64),
            SourceModule = "SALES",
            SourceDocumentType = ownerType,
            SourceDocumentId = ownerNo,
            SourceDocumentNo = ownerNo,
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            SourceSnapshotJson = "{}",
            SourceSnapshotHash = new string('E', 64),
            EffectiveAt = FixedToday,
            BusinessDate = FixedToday.Date,
            PeriodKey = FixedToday.ToString("yyyy-MM"),
            PostedAtUtc = DateTime.UtcNow,
            PostedBy = "TEST",
            SealedAtUtc = DateTime.UtcNow
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();
        db.StockValuationFacts.Add(new StockValuationFact
        {
            CompanyCode = InventoryLedgerTestFixture.CompanyCode,
            BranchCode = InventoryLedgerTestFixture.BranchCode,
            LedgerEpochId = epochId,
            StockPostingId = posting.Id,
            PostingLineNo = 1,
            SplitOrdinal = 0,
            SourceLineId = ownerLine,
            SourceDocumentType = ownerType,
            SourceDocumentId = ownerNo,
            SourceDocumentNo = ownerNo,
            SourceDocumentLine = ownerLine,
            EffectiveAt = FixedToday,
            BusinessDate = FixedToday.Date,
            PeriodKey = FixedToday.ToString("yyyy-MM"),
            ItemCode = "A100",
            BaseUom = "EA",
            MovementCode = "SALE_OUT",
            Direction = -1,
            BaseQty = qty,
            CostMethod = costMethod,
            UnitCost = unitCost,
            CostAmount = amount,
            BaseCostAmount = amount,
            BaseCurrency = InventoryLedgerTestFixture.BaseCurrency,
            ValuationSource = StockValuationSources.MovingAverage,
            ValuationStatus = StockValuationStatuses.Valued,
            CreatedAtUtc = DateTime.UtcNow,
            CreatedBy = "TEST"
        });
        await db.SaveChangesAsync();
    }

    private static IvStockReturnSaveRequest Request(
        decimal qty,
        string? sourceInvNo = InvoiceNo,
        short sourceLine = 1,
        string iCode = "A100") =>
        new()
        {
            TrxDate = FixedToday,
            Lines =
            [
                new IvStockReturnLineRequest
                {
                    SourceInvNo = sourceInvNo ?? string.Empty,
                    SourceInvoiceLine = sourceLine,
                    ICode = iCode,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    Quantity = qty,
                    Uom = "EA",
                    IClassCode = "RAW",
                    IStatus = IvItemStatuses.Active,
                    UnitPrice = TamperPrice,
                    Reason = IvReturnReasons.Return
                }
            ]
        };
}
