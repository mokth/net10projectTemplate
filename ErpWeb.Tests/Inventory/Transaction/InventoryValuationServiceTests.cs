using ErpWeb.Core.Inventory;
using ErpWeb.Core.StockLedger;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Transaction;

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryPosting)]
public sealed class InventoryValuationServiceTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;
    private int _batchNo;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new LocalFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO", BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO", ICode = "ITEM-1", IDesc = "Valued item",
            IClassCode = "RAW", StdUom = "EA", StockControl = true, IsActive = true
        });
        db.StockLedgerEpochs.Add(new StockLedgerEpoch
        {
            CompanyCode = "DEMO", BranchCode = "HQ", EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2, Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('A', 64)
        });
        db.StockCostPolicyRevisions.Add(new StockCostPolicyRevision
        {
            CompanyCode = "DEMO", BranchCode = "HQ",
            CostMethod = StockCostMethods.MovingAverage,
            EffectiveFrom = new DateTime(2026, 10, 1),
            Status = StockCostPolicyStatuses.Active,
            ApprovedBy = "TEST", ApprovedAtUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow, CreatedBy = "TEST"
        });
        await db.SaveChangesAsync();
        db.IvBalLocs.AddRange(
            Balance("MAIN", "A"),
            Balance("MAIN", "B"));
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Moving_average_issues_and_final_depletion_use_authoritative_state()
    {
        await PostReceiptAsync(10m, 2m, new DateTime(2026, 10, 1, 8, 0, 0));
        await PostReceiptAsync(10m, 4m, new DateTime(2026, 10, 2, 8, 0, 0));
        await PostIssueAsync(5m, new DateTime(2026, 10, 3, 8, 0, 0));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal(15m, state.OnHandBaseQty);
            Assert.Equal(45m, state.InventoryValue);
            Assert.Equal(3m, state.AverageUnitCost);
            var issue = await db.StockValuationFacts.SingleAsync(x => x.Direction == -1);
            Assert.Equal(15m, issue.CostAmount);
            Assert.Equal(StockValuationSources.MovingAverage, issue.ValuationSource);
        }

        await PostIssueAsync(15m, new DateTime(2026, 10, 4, 8, 0, 0));
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var state = await db.StockCostStates.SingleAsync();
            Assert.Equal(0m, state.OnHandBaseQty);
            Assert.Equal(0m, state.InventoryValue);
            Assert.Equal(0m, state.AverageUnitCost);
            var lastIssue = await db.StockValuationFacts.Where(x => x.Direction == -1)
                .OrderByDescending(x => x.Id).FirstAsync();
            Assert.Equal(45m, lastIssue.CostAmount);
        }
    }

    [Fact]
    public async Task Moving_average_preserves_six_decimal_cost_after_reload()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.StockCostStates.Add(new StockCostState
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                ItemCode = "ITEM-1",
                CostMethod = StockCostMethods.MovingAverage,
                OnHandBaseQty = 3m,
                InventoryValue = 0.999999m,
                AverageUnitCost = 0.333333m,
                CurrentUnitCost = 0.333333m,
                RowVersion = [1]
            });
            await db.SaveChangesAsync();
        }

        await PostIssueAsync(1m, new DateTime(2026, 10, 2, 8, 0, 0));

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.Direction == -1);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(0.333333m, fact.CostAmount);
        Assert.Equal(0.666666m, state.InventoryValue);
        Assert.Equal(0.333333m, state.AverageUnitCost);
    }

    [Fact]
    public async Task Fifo_issue_splits_layers_and_aggregates_the_production_grain()
    {
        await SetCostMethodAsync(StockCostMethods.Fifo);
        await PostReceiptAsync(2m, 2m, new DateTime(2026, 10, 1, 8, 0, 0));
        await PostReceiptAsync(5m, 3m, new DateTime(2026, 10, 2, 8, 0, 0));
        await PostIssueAsync(4m, new DateTime(2026, 10, 3, 8, 0, 0));

        await using var verify = await _factory.CreateDbContextAsync();
        var issueFacts = await verify.StockValuationFacts
            .Where(x => x.Direction == -1)
            .OrderBy(x => x.Id)
            .ToListAsync();
        Assert.Equal(2, issueFacts.Count);
        Assert.Equal(2m, issueFacts[0].BaseQty);
        Assert.Equal(4m, issueFacts[0].CostAmount);
        Assert.Equal(2m, issueFacts[1].BaseQty);
        Assert.Equal(6m, issueFacts[1].CostAmount);
        Assert.Equal(10m, issueFacts.Sum(x => x.CostAmount));

        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(3m, state.OnHandBaseQty);
        Assert.Equal(9m, state.InventoryValue);
        var layers = await verify.StockFifoLayers.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, layers.Count);
        Assert.Equal(0m, layers[0].RemainingQty);
        Assert.Equal(3m, layers[1].RemainingQty);
        Assert.Equal(9m, layers[1].RemainingValue);
    }

    [Fact]
    public async Task Fifo_finished_good_receipt_uses_exact_transferred_value_for_fact_and_layer()
    {
        await SetCostMethodAsync(StockCostMethods.Fifo);
        await PostFinishedGoodReceiptAsync(3m, 1.000001m, new DateTime(2026, 10, 1, 8, 0, 0));

        await using var verify = await _factory.CreateDbContextAsync();
        var fact = await verify.StockValuationFacts.SingleAsync(x => x.Direction == 1);
        var layer = await verify.StockFifoLayers.SingleAsync();
        Assert.Equal(1.000001m, fact.CostAmount);
        Assert.Equal(StockValuationSources.ProductionActual, fact.ValuationSource);
        Assert.Equal(1.000001m, layer.OriginalValue);
        Assert.Equal(1.000001m, layer.RemainingValue);
    }

    [Fact]
    public async Task Preseal_valuation_then_complete_is_idempotent()
    {
        var balanceId = (await BalanceIdsAsync())[0];
        var effectiveAt = new DateTime(2026, 10, 2, 8, 0, 0);
        var sourceId = (++_batchNo).ToString();
        var command = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = "PRESEAL_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = "PRESEAL",
            SourceDocumentId = sourceId,
            SourceDocumentNo = sourceId,
            DocumentRevision = 1,
            PostingRole = "PRIMARY",
            EffectiveAt = effectiveAt,
            Evidence = StockPostingFingerprint.Create(new { sourceId }, new { effectiveAt })
        };
        var coordinator = new StockPostingCoordinator(
            _factory, new Tenant(), new BranchStockTransactionLock(),
            new StockPeriodGuard(), new NoActiveStockFreezeGuard(),
            new InventoryValuationService());

        await using (var db = await _factory.CreateDbContextAsync())
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var begin = await coordinator.BeginInTransactionAsync(db, command);
            Assert.True(begin.LedgerEnabled, begin.Error?.Message);
            var context = begin.Context ?? throw new InvalidOperationException(begin.Error?.Message ?? "Posting context was not created.");
            var history = History(IvTrxTypes.MiscellaneousReceipt);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = 3m;
            history.ToStdUom = "EA";
            history.UnitPrice = 2m;
            history.PriceEvidence = InventoryCostEvidenceTypes.ManualApproved;
            db.IvTrxHistories.Add(history);
            new IvInventoryHistoryWriter().StampGeneration(context, [history], 1);
            await db.SaveChangesAsync();

            await coordinator.ValuePendingInTransactionAsync(context);
            await coordinator.CompleteInTransactionAsync(context);
            await transaction.CommitAsync();
        }

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Single(await verify.StockValuationFacts.ToListAsync());
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(3m, state.OnHandBaseQty);
        Assert.Equal(6m, state.InventoryValue);
    }

    [Fact]
    public async Task Transfer_creates_equal_out_and_in_facts_without_changing_pool_value()
    {
        await PostReceiptAsync(10m, 7.25m, new DateTime(2026, 10, 1, 8, 0, 0));
        var balances = await BalanceIdsAsync();
        await ExecuteAsync(new DateTime(2026, 10, 2, 8, 0, 0), "TR", (context, writer) =>
        {
            var history = History(IvTrxTypes.StockTransfer);
            history.FromBalLocId = balances[0];
            history.ToBalLocId = balances[1];
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.ToWarehouse = "MAIN";
            history.ToLocation = "B";
            history.FrStdQty = 4m;
            history.ToStdQty = 4m;
            history.FrStdUom = "EA";
            history.ToStdUom = "EA";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });

        await using var db = await _factory.CreateDbContextAsync();
        var transferPosting = await db.StockPostings.SingleAsync(x => x.SourceDocumentType == "TR");
        var facts = await db.StockValuationFacts.Where(x => x.StockPostingId == transferPosting.Id)
            .OrderBy(x => x.SplitOrdinal).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(-1, facts[0].Direction);
        Assert.Equal(1, facts[1].Direction);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        var state = await db.StockCostStates.SingleAsync();
        Assert.Equal(10m, state.OnHandBaseQty);
        Assert.Equal(72.5m, state.InventoryValue);
    }

    [Fact]
    public async Task Reversal_copies_original_value_and_links_the_original_fact()
    {
        var postingId = await PostReceiptAsync(8m, 2.5m, new DateTime(2026, 10, 1, 8, 0, 0));
        int historyId;
        await using (var db = await _factory.CreateDbContextAsync())
            historyId = await db.IvTrxHistories.Select(x => x.Id).SingleAsync();

        await ExecuteAsync(
            new DateTime(2026, 10, 2, 8, 0, 0), "MR_REVERSAL",
            async (context, writer) =>
            {
                var original = await context.Db.IvTrxHistories.SingleAsync(x => x.Id == historyId);
                writer.AppendReversal(context, [original], 1);
            },
            postingId);

        await using var verify = await _factory.CreateDbContextAsync();
        var facts = await verify.StockValuationFacts.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, facts.Count);
        Assert.Equal(facts[0].CostAmount, facts[1].CostAmount);
        Assert.Equal(facts[0].Id, facts[1].ReversesValuationFactId);
        Assert.Equal(StockValuationSources.OriginalReversal, facts[1].ValuationSource);
        Assert.Equal(StockValuationStatuses.Reversed, facts[1].ValuationStatus);
        var state = await verify.StockCostStates.SingleAsync();
        Assert.Equal(0m, state.OnHandBaseQty);
        Assert.Equal(0m, state.InventoryValue);
    }

    [Fact]
    public async Task History_saved_by_route_before_completion_is_still_valued()
    {
        var balanceId = (await BalanceIdsAsync())[0];
        await ExecuteAsync(new DateTime(2026, 10, 1, 8, 0, 0), "MR_SAVED", async (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousReceipt);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = 6m;
            history.ToStdUom = "EA";
            history.UnitPrice = 4.5m;
            history.PriceEvidence = InventoryCostEvidenceTypes.ManualApproved;
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);

            // Production and finished-good routes persist their history rows before the
            // coordinator completes the posting. Valuation must discover those rows too.
            await context.Db.SaveChangesAsync();
        });

        await using var db = await _factory.CreateDbContextAsync();
        var historyId = await db.IvTrxHistories.Select(x => x.Id).SingleAsync();
        var fact = await db.StockValuationFacts.SingleAsync();
        Assert.Equal(historyId, fact.InventoryHistoryId);
        Assert.Equal(27m, fact.CostAmount);
        Assert.Equal(6m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
    }

    [Fact]
    public async Task As_of_value_ignores_mutable_current_cost_fields()
    {
        await PostReceiptAsync(10m, 2m, new DateTime(2026, 10, 1, 8, 0, 0));
        await PostIssueAsync(4m, new DateTime(2026, 10, 2, 8, 0, 0));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.StockCostStates.SingleAsync()).InventoryValue = 999m;
            (await db.StockCostStates.SingleAsync()).AverageUnitCost = 166.5m;
            (await db.IvStockMasters.SingleAsync()).PurchasePrice = 777m;
            (await db.IvBalLocs.OrderBy(x => x.Id).FirstAsync()).UnitPrice = 555m;
            await db.SaveChangesAsync();
        }

        var rows = await new StockValuationQueryService(_factory, new Tenant())
            .GetAsOfAsync(new DateTime(2026, 10, 2));
        var row = Assert.Single(rows);
        Assert.Equal(6m, row.BaseQty);
        Assert.Equal(12m, row.InventoryValue);
        Assert.Equal(2m, row.AverageUnitCost);
    }

    [Fact]
    public async Task Financial_snapshot_uses_sealed_facts_and_reconciles_cost_state()
    {
        await PostReceiptAsync(10m, 3m, new DateTime(2026, 10, 1, 8, 0, 0));
        await PostIssueAsync(4m, new DateTime(2026, 10, 2, 8, 0, 0));

        await using var db = await _factory.CreateDbContextAsync();
        var error = await StockValuationSnapshotBuilder.AppendAsync(
            db, "DEMO", "HQ", new DateTime(2026, 10, 1), new DateTime(2026, 10, 31),
            "tester", CancellationToken.None);
        Assert.Null(error);
        await db.SaveChangesAsync();

        var header = await db.StockValuationPeriodSnapshotHdrs.Include(x => x.Lines).SingleAsync();
        var line = Assert.Single(header.Lines);
        Assert.Equal(10m, line.InQty);
        Assert.Equal(30m, line.InValue);
        Assert.Equal(4m, line.OutQty);
        Assert.Equal(12m, line.OutValue);
        Assert.Equal(6m, line.ClosingQty);
        Assert.Equal(18m, line.ClosingValue);
    }

    [Fact]
    public async Task Mixed_invoice_resolves_linked_do_and_direct_invoice_cogs_without_duplication()
    {
        await PostReceiptAsync(10m, 5m, new DateTime(2026, 10, 1, 8, 0, 0));
        var balanceId = (await BalanceIdsAsync())[0];

        await ExecuteAsync(new DateTime(2026, 10, 2, 8, 0, 0), "DO", (context, writer) =>
        {
            var history = History(IvTrxTypes.SalesOut);
            history.FromBalLocId = balanceId;
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.FrStdQty = 2m;
            history.FrStdUom = "EA";
            history.DoNo = "DO-1";
            history.SoLineNo = 1;
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
        await ExecuteAsync(new DateTime(2026, 10, 3, 8, 0, 0), "INV", (context, writer) =>
        {
            var history = History(IvTrxTypes.SalesOut);
            history.FromBalLocId = balanceId;
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.FrStdQty = 3m;
            history.FrStdUom = "EA";
            history.InvNo = "INV-1";
            history.SoLineNo = 2;
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var invoice = new SaInvoice
            {
                CompanyCode = "DEMO", BranchCode = "HQ", InvNo = "INV-1", DoNo = "INV-1",
                CustCode = "CUST-1", InvDate = new DateTime(2026, 10, 3), Status = "POSTED"
            };
            invoice.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = "DEMO", BranchCode = "HQ", InvNo = "INV-1", Line = 1,
                ICode = "ITEM-1", Qty = 2m, StdQty = 2m, StdUom = "EA", StockControl = true,
                LinkDo = true, DoNo = "DO-1", DoLine = 1, SoNo = string.Empty
            });
            invoice.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = "DEMO", BranchCode = "HQ", InvNo = "INV-1", Line = 2,
                ICode = "ITEM-1", Qty = 3m, StdQty = 3m, StdUom = "EA", StockControl = true,
                LinkDo = false, DoNo = string.Empty, SoNo = string.Empty
            });
            db.SaInvoices.Add(invoice);
            await db.SaveChangesAsync();
        }

        var cogs = await new StockValuationQueryService(_factory, new Tenant())
            .GetInvoiceCogsAsync("INV-1");
        Assert.True(cogs.IsFullyResolved);
        Assert.Equal(25m, cogs.TotalCogs);
        Assert.Equal(10m, Assert.Single(cogs.Lines, x => x.LinkDo).Cogs);
        Assert.Equal(15m, Assert.Single(cogs.Lines, x => !x.LinkDo).Cogs);
    }

    [Fact]
    public async Task Backdated_movement_is_rejected_when_the_pool_has_later_valuation()
    {
        await PostReceiptAsync(5m, 2m, new DateTime(2026, 10, 2, 8, 0, 0));
        var balanceId = (await BalanceIdsAsync())[0];

        var result = await ExecuteResultAsync(
            new DateTime(2026, 10, 1, 8, 0, 0), "MR_BACKDATED", (context, writer) =>
            {
                var history = History(IvTrxTypes.MiscellaneousReceipt);
                history.ToBalLocId = balanceId;
                history.ToWarehouse = "MAIN";
                history.ToLocation = "A";
                history.ToStdQty = 1m;
                history.ToStdUom = "EA";
                history.UnitPrice = 2m;
                history.PriceEvidence = InventoryCostEvidenceTypes.ManualApproved;
                context.Db.IvTrxHistories.Add(history);
                writer.StampGeneration(context, [history], 1);
                return Task.CompletedTask;
            });

        Assert.False(result.Succeeded);
        Assert.Equal(StockLedgerErrorCodes.BackdatedStockEvent, result.Error?.Code);
        Assert.Contains("Cannot post for item", result.Error?.Message);
        Assert.Contains("later cost movement", result.Error?.Message);
        Assert.Contains("Costing Center", result.Error?.Message);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Single(await db.StockValuationFacts.ToListAsync());
        Assert.Equal(5m, (await db.StockCostStates.SingleAsync()).OnHandBaseQty);
    }

    [Fact]
    public void Backdated_message_names_blocking_document_for_users()
    {
        var message = InventoryValuationService.FormatBackdatedPostingMessage(
            "RM003",
            new DateTime(2026, 10, 6, 12, 38, 53),
            IvTrxTypes.GoodsReceive,
            "42",
            "100");
        Assert.Contains("RM003", message);
        Assert.Contains("Goods Receipt 42", message);
        Assert.Contains("2026-10-06 12:38", message);
        Assert.Contains("Roll back", message);
        Assert.Contains("Costing Center", message);
    }

    [Fact]
    public void Backdated_message_lists_multiple_blockers_newest_guidance()
    {
        var message = InventoryValuationService.FormatBackdatedPostingMessage(
            new DateTime(2026, 10, 1),
            [
                new InventoryValuationService.BackdatedBlocker(
                    "RM003", new DateTime(2026, 10, 3, 8, 0, 0), "GR", "10", "1", 1),
                new InventoryValuationService.BackdatedBlocker(
                    "RM003", new DateTime(2026, 10, 5, 9, 0, 0), "MR", "20", "2", 2)
            ]);
        Assert.Contains("Goods Receipt 10", message);
        Assert.Contains("Misc Receipt 20", message);
        Assert.Contains("newest-first", message);
        Assert.Contains("Later movements", message, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<long> PostReceiptAsync(decimal qty, decimal price, DateTime effectiveAt)
    {
        var balanceId = (await BalanceIdsAsync())[0];
        return await ExecuteAsync(effectiveAt, "MR", (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousReceipt);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = qty;
            history.ToStdUom = "EA";
            history.UnitPrice = price;
            history.PriceEvidence = InventoryCostEvidenceTypes.ManualApproved;
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
    }

    private async Task<long> PostIssueAsync(decimal qty, DateTime effectiveAt)
    {
        var balanceId = (await BalanceIdsAsync())[0];
        return await ExecuteAsync(effectiveAt, "MI", (context, writer) =>
        {
            var history = History(IvTrxTypes.MiscellaneousIssue);
            history.FromBalLocId = balanceId;
            history.FrWarehouse = "MAIN";
            history.FrLocation = "A";
            history.FrStdQty = qty;
            history.FrStdUom = "EA";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
    }

    private async Task<long> PostFinishedGoodReceiptAsync(
        decimal qty,
        decimal exactValue,
        DateTime effectiveAt)
    {
        var balanceId = (await BalanceIdsAsync())[0];
        return await ExecuteAsync(effectiveAt, "FG", (context, writer) =>
        {
            var history = History(IvTrxTypes.FinishedGoods);
            history.ToBalLocId = balanceId;
            history.ToWarehouse = "MAIN";
            history.ToLocation = "A";
            history.ToStdQty = qty;
            history.ToStdUom = "EA";
            history.UnitPrice = 0.3333m;
            history.ExactTransferredValue = exactValue;
            history.PriceEvidence = "FG_EXACT_BASE_CURRENCY";
            context.Db.IvTrxHistories.Add(history);
            writer.StampGeneration(context, [history], 1);
            return Task.CompletedTask;
        });
    }

    private async Task SetCostMethodAsync(string costMethod)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var policy = await db.StockCostPolicyRevisions.SingleAsync();
        policy.CostMethod = costMethod;
        await db.SaveChangesAsync();
    }

    private async Task<long> ExecuteAsync(
        DateTime effectiveAt,
        string documentType,
        Func<StockPostingContext, IvInventoryHistoryWriter, Task> handler,
        long? reversesPostingId = null)
    {
        var result = await ExecuteResultAsync(effectiveAt, documentType, handler, reversesPostingId);
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.StockPostingId!.Value;
    }

    private async Task<StockPostingExecutionResult<int>> ExecuteResultAsync(
        DateTime effectiveAt,
        string documentType,
        Func<StockPostingContext, IvInventoryHistoryWriter, Task> handler,
        long? reversesPostingId = null)
    {
        var command = new StockPostingCommand
        {
            RequestId = Guid.NewGuid(),
            CommandType = documentType + "_TEST",
            SourceModule = "INVENTORY",
            SourceDocumentType = documentType,
            SourceDocumentId = (++_batchNo).ToString(),
            SourceDocumentNo = _batchNo.ToString(),
            DocumentRevision = 1,
            PostingRole = reversesPostingId is null ? "PRIMARY" : "REVERSAL",
            EffectiveAt = effectiveAt,
            Evidence = StockPostingFingerprint.Create(new { documentType, _batchNo }, new { effectiveAt }),
            ReversesPostingId = reversesPostingId
        };
        var coordinator = new StockPostingCoordinator(
            _factory, new Tenant(), new BranchStockTransactionLock(),
            new StockPeriodGuard(), new NoActiveStockFreezeGuard(),
            new InventoryValuationService());
        var result = await coordinator.ExecuteAsync(command, async (context, _) =>
        {
            await handler(context, new IvInventoryHistoryWriter());
            return 0;
        });
        return result;
    }

    private async Task<int[]> BalanceIdsAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.IvBalLocs.OrderBy(x => x.LocCode).Select(x => x.Id).ToArrayAsync();
    }

    private IvTrxHistory History(string type) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = ++_batchNo,
        TrxLineNo = 1, TrxDtTime = new DateTime(2026, 10, 1), TrxType = type,
        BatchStatus = IvBatchStatuses.Posted, ICode = "ITEM-1", IStatus = "ACTIVE",
        CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private static IvBalLoc Balance(string warehouse, string location) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", ICode = "ITEM-1",
        WhCode = warehouse, LocCode = location, LotNo = string.Empty, IStatus = "ACTIVE",
        StdQty = 0m, StdUom = "EA"
    };

    private sealed class Tenant : IInventoryTenantContext
    {
        private static readonly InventoryTenantScope Scope = new()
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LocationCode = "SITE", UserId = "tester"
        };
        public InventoryTenantScope? TryCompanyScope() => Scope;
        public InventoryTenantScope? TryBranchScope() => Scope;
        public InventoryTenantScope? TryWriteScope() => Scope;
    }

    private sealed class LocalFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }
}
