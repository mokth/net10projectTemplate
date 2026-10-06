using ErpWeb.Core.Costing;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Entities.StockLedger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ErpWeb.Tests.Inventory.Inquiry;

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed class CostingRepairOwnershipRulesTests
{
    [Fact]
    public void Sales_out_with_invoice_number_belongs_to_the_invoice()
    {
        var result = CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.SalesOut, invNo: "INV-1"));
        Assert.Equal(CostingRepairOwnerTypes.SalesInvoice, result.Owner!.OwnerType);
        Assert.Equal("INV-1", result.Owner.OwnerDocumentNo);
    }

    [Fact]
    public void Sales_out_with_delivery_order_belongs_to_the_delivery_order()
    {
        var result = CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.SalesOut, doNo: "DO-1", invNo: "INV-1"));
        Assert.Equal(CostingRepairOwnerTypes.SalesDeliveryOrder, result.Owner!.OwnerType);
    }

    [Fact]
    public void Force_closed_sales_out_is_not_a_rollback()
    {
        var result = CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.SalesOut, doNo: "DO-1", forceClosed: true));
        Assert.False(result.IsProven);
        Assert.Contains("Force Close", result.BlockingReason);
    }

    [Fact]
    public void Direct_customer_return_stays_on_inventory_and_credit_note_stock_moves_to_sales()
    {
        var direct = CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.CustomerReturn));
        var owned = CostingRepairOwnershipRules.Resolve(Evidence(
            IvTrxTypes.CustomerReturn, salesCreditNoteNo: "CN-9"));
        Assert.Equal(CostingRepairOwnerTypes.CustomerReturn, direct.Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.SalesCreditNote, owned.Owner!.OwnerType);
        Assert.Equal("CN-9", owned.Owner.OwnerDocumentNo);
    }

    [Fact]
    public void Direct_vendor_return_stays_on_inventory_and_purchase_credit_note_owns_its_batch()
    {
        var direct = CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.VendorReturn));
        var owned = CostingRepairOwnershipRules.Resolve(Evidence(
            IvTrxTypes.VendorReturn, purchaseCreditNoteNo: "PCN-4"));
        Assert.Equal(CostingRepairOwnerTypes.VendorReturn, direct.Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.PurchaseCreditNote, owned.Owner!.OwnerType);
    }

    [Fact]
    public void Goods_receipt_types_belong_only_to_purchase_receipt()
    {
        Assert.Equal(CostingRepairOwnerTypes.PurchaseGoodsReceipt,
            CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.GoodsReceive)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.PurchaseGoodsReceipt,
            CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.NonStockGoodsReceive)).Owner!.OwnerType);
    }

    [Fact]
    public void Production_without_a_link_and_unknown_types_cannot_be_repaired()
    {
        Assert.False(CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.IssueToProduction)).IsProven);
        Assert.False(CostingRepairOwnershipRules.Resolve(Evidence(IvTrxTypes.FinishedGoods)).IsProven);
        Assert.True(CostingRepairOwnershipRules.Resolve(
            Evidence(IvTrxTypes.IssueToProduction, productionLink: true)).IsProven);
        Assert.False(CostingRepairOwnershipRules.Resolve(Evidence("XX")).IsProven);
    }

    private static CostingRepairEvidence Evidence(
        string physical,
        string? doNo = null,
        string? invNo = null,
        bool forceClosed = false,
        string? salesCreditNoteNo = null,
        string? purchaseCreditNoteNo = null,
        bool productionLink = false) =>
        new("DEMO", "HQ", 1, physical, "10", "10", doNo, invNo, forceClosed,
            salesCreditNoteNo is not null, salesCreditNoteNo,
            purchaseCreditNoteNo is not null, purchaseCreditNoteNo, productionLink);
}

[Trait(TestCategories.Name, TestCategories.Inventory)]
public sealed partial class CostingDiagnosticCenterTests : IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private IDbContextFactory<AppDbContext> _factory = null!;
    private long _epochId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _factory = new LocalFactory(options);
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
        var epoch = new StockLedgerEpoch
        {
            CompanyCode = "DEMO", BranchCode = "HQ", EffectiveFrom = new DateTime(2026, 10, 1),
            Version = 2, Status = StockLedgerEpochStatuses.Active,
            MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('A', 64),
            ActivatedAtUtc = DateTime.UtcNow, ActivatedBy = "tester"
        };
        db.StockLedgerEpochs.Add(epoch);
        await db.SaveChangesAsync();
        _epochId = epoch.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Healthy_moving_average_pool_has_no_critical_findings()
    {
        var postingId = await AddPostingAsync("MR", "1", isSealed: true);
        await AddFactAsync(postingId, direction: 1, qty: 10m, value: 25m);
        await AddStateAsync(10m, 25m, 2.5m);

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync(new CostingHealthQuery("ITEM-1"));

        Assert.False(page.Denied);
        Assert.Equal(CostingEpochCoverage.V2, page.EpochCoverage);
        Assert.DoesNotContain(page.Findings, x => x.Severity == CostingFindingSeverity.Critical);
    }

    [Fact]
    public async Task Missing_facts_unsealed_postings_and_broken_reversals_are_reported()
    {
        await AddPostingAsync("MR", "2", isSealed: false);
        await AddPostingAsync("MI", "3", isSealed: true);
        var unsealed = await AddPostingAsync("SP", "4", isSealed: false);
        var host = await AddPostingAsync("MR", "31", isSealed: false);
        var reversalHost = await AddPostingAsync("MR", "32", isSealed: true);
        await AddFactAsync(unsealed, direction: -1, qty: 1m, value: 1m);
        var original = await AddFactAsync(host, direction: 1, qty: 1m, value: 1m);
        await AddFactAsync(reversalHost, direction: -1, qty: 1m, value: 1m, reverses: original);

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync();

        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.UnsealedPosting);
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.HistoryMissingValuation);
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.FactWithoutSealedPosting);
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.ReversalLineageBroken);
    }

    [Fact]
    public async Task Cost_state_mismatches_and_zero_quantity_residue_are_reported()
    {
        var postingId = await AddPostingAsync("MR", "5", isSealed: true);
        await AddFactAsync(postingId, direction: 1, qty: 4m, value: 8m);
        await AddStateAsync(9m, 3m, 1m);
        await AddStateAsync(0m, 1.5m, 0m, item: "ITEM-RESIDUE");

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: false))
            .SearchAsync();

        var qty = Assert.Single(page.Findings, x => x.Code == CostingFindingCodes.CostStateQuantityMismatch);
        var value = Assert.Single(page.Findings, x => x.Code == CostingFindingCodes.CostStateValueMismatch && x.ItemCode == "ITEM-1");
        var average = Assert.Single(page.Findings, x => x.Code == CostingFindingCodes.CostStateAverageMismatch && x.ItemCode == "ITEM-1");
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.ZeroQuantityResidue);
        Assert.Null(value.ActualValue);
        Assert.Null(average.ActualValue);
        Assert.False(page.MonetaryValuesVisible);
        Assert.Equal(4m, qty.ExpectedQty);
        Assert.Equal(9m, qty.ActualQty);
    }

    [Fact]
    public async Task View_price_does_not_reveal_cost()
    {
        var postingId = await AddPostingAsync("MR", "6", isSealed: true);
        await AddFactAsync(postingId, direction: 1, qty: 2m, value: 9m);
        await AddStateAsync(2m, 4m, 2m);

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: false, viewPrice: true))
            .SearchAsync(new CostingHealthQuery("ITEM-1"));

        Assert.False(page.MonetaryValuesVisible);
        Assert.All(page.Findings, x => Assert.Null(x.ActualValue));
        Assert.All(page.Findings, x => Assert.Null(x.ExpectedValue));
    }

    [Fact]
    public async Task Cross_epoch_facts_skip_the_cost_state_rebuild()
    {
        long retiredId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var retired = new StockLedgerEpoch
            {
                CompanyCode = "DEMO", BranchCode = "HQ", EffectiveFrom = new DateTime(2026, 1, 1),
                Version = 2, Status = StockLedgerEpochStatuses.Retired,
                MigrationBatchId = Guid.NewGuid(), ReconciliationManifestHash = new string('B', 64)
            };
            db.StockLedgerEpochs.Add(retired);
            await db.SaveChangesAsync();
            retiredId = retired.Id;
        }

        var retiredPosting = await AddPostingAsync("MR", "7", isSealed: true, epochId: retiredId);
        var activePosting = await AddPostingAsync("MR", "8", isSealed: true);
        await AddFactAsync(retiredPosting, direction: 1, qty: 1m, value: 1m, epochId: retiredId);
        await AddFactAsync(activePosting, direction: 1, qty: 1m, value: 1m);
        await AddStateAsync(99m, 99m, 1m);

        var page = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .SearchAsync();

        Assert.Equal(CostingEpochCoverage.UnresolvedCrossEpoch, page.EpochCoverage);
        Assert.Contains(page.Findings, x => x.Code == CostingFindingCodes.EpochCoverage);
        Assert.DoesNotContain(page.Findings, x => x.Code == CostingFindingCodes.CostStateQuantityMismatch);
    }

    [Fact]
    public async Task Invoice_cogs_requires_exact_owner_line_and_quantity()
    {
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
                LinkDo = false, DoNo = string.Empty, SoNo = string.Empty
            });
            invoice.Details.Add(new SaInvoiceDetail
            {
                CompanyCode = "DEMO", BranchCode = "HQ", InvNo = "INV-1", Line = 2,
                ICode = "ITEM-1", Qty = 5m, StdQty = 5m, StdUom = "EA", StockControl = true,
                LinkDo = true, DoNo = "DO-1", DoLine = 4, SoNo = string.Empty
            });
            db.SaInvoices.Add(invoice);
            await db.SaveChangesAsync();
        }

        var direct = await AddPostingAsync("SP", "9", isSealed: true);
        var delivery = await AddPostingAsync("SP", "10", isSealed: true);
        await AddFactAsync(direct, direction: -1, qty: 2m, value: 6m, documentType: "SA_INVOICE", documentNo: "INV-1", line: "1", split: 0);
        await AddFactAsync(delivery, direction: -1, qty: 1m, value: 3m, documentType: "SA_DO", documentNo: "DO-1", line: "4", split: 0);
        await AddFactAsync(direct, direction: -1, qty: 5m, value: 15m, documentType: "SA_INVOICE", documentNo: "INV-1", line: "9", split: 1);

        var proof = await new CostingDiagnosticService(_factory, new Tenant(), new Access(viewCost: true))
            .ProveInvoiceCogsAsync("INV-1");

        Assert.False(proof.IsFullyProven);
        Assert.True(Assert.Single(proof.Lines, x => x.InvoiceLine == 1).QuantityProven);
        var ambiguous = Assert.Single(proof.Lines, x => x.InvoiceLine == 2);
        Assert.False(ambiguous.QuantityProven);
        Assert.Equal("LEGACY_OR_AMBIGUOUS_COGS_LINEAGE", ambiguous.Lineage);
        Assert.Equal(1m, ambiguous.ActualQty);
        Assert.Contains(proof.Findings, x => x.Code == CostingFindingCodes.SalesCogsLineageIncomplete);
    }

    [Fact]
    public async Task Trace_keeps_the_branch_item_pool_and_uses_an_opening_anchor()
    {
        var first = await AddPostingAsync("MR", "11", isSealed: true, effective: new DateTime(2026, 10, 1));
        var second = await AddPostingAsync("MR", "12", isSealed: true, effective: new DateTime(2026, 10, 2));
        await AddFactAsync(first, direction: 1, qty: 10m, value: 20m, warehouse: "MAIN", effective: new DateTime(2026, 10, 1));
        await AddFactAsync(second, direction: 1, qty: 5m, value: 10m, warehouse: "OTHER", effective: new DateTime(2026, 10, 2));

        var page = await new CostingTraceService(_factory, new Tenant(), new Access(viewCost: true))
            .GetItemTimelineAsync(new CostingTraceQuery("ITEM-1", From: new DateTime(2026, 10, 2), WarehouseCode: "OTHER"));

        Assert.Equal(10m, page.Anchor.OpeningQty);
        Assert.Equal(20m, page.Anchor.OpeningValue);
        var line = Assert.Single(page.Lines);
        Assert.Equal("OTHER", line.WarehouseCode);
        Assert.Equal(15m, line.QuantityAfter);
        Assert.Equal(30m, line.InventoryValueAfter);
    }

    [Fact]
    public async Task Resolver_routes_sales_returns_and_receipts_from_stored_evidence()
    {
        var invoice = await AddPostingAsync("SP", "21", isSealed: true);
        var delivery = await AddPostingAsync("SP", "22", isSealed: true);
        var directReturn = await AddPostingAsync("CR", "23", isSealed: true);
        var creditReturn = await AddPostingAsync("CR", "24", isSealed: true);
        var directVendor = await AddPostingAsync("VR", "25", isSealed: true);
        var purchaseReturn = await AddPostingAsync("VR", "26", isSealed: true);
        var receipt = await AddPostingAsync("GR", "27", isSealed: true);
        var nonStock = await AddPostingAsync("NG", "28", isSealed: true);
        var forceClosed = await AddPostingAsync("SP", "29", isSealed: true);
        var linkedIssue = await AddPostingAsync("IP", "30", isSealed: true, productionLink: true);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxHistories.AddRange(
                History(21, IvTrxTypes.SalesOut, invNo: "INV-21"),
                History(22, IvTrxTypes.SalesOut, doNo: "DO-22", invNo: "INV-22"));
            db.IvTrxBatches.AddRange(
                Batch(23, IvTrxTypes.CustomerReturn, "ADJ-23"),
                Batch(24, IvTrxTypes.CustomerReturn, "CN/CN-24"),
                Batch(29, IvTrxTypes.SalesOut, "DO/DO-29", forceClosed: true));
            db.SaCdns.Add(new SaCdn
            {
                CompanyCode = "DEMO", BranchCode = "HQ", DocNo = "CN-24",
                DocDate = new DateTime(2026, 10, 2), Status = "POSTED", Type = "CN",
                CustCode = "CUST-1", ReturnStock = true, RowVersion = [1]
            });
            db.PoCdns.Add(new PoCdn
            {
                CompanyCode = "DEMO", BranchCode = "HQ", DocNo = "PCN-26",
                DocDate = new DateTime(2026, 10, 2), Status = "POSTED", Type = "PCN",
                VendorCode = "VEND-1", ReturnStock = true, VrBatchNo = 26, RowVersion = [1]
            });
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.ProductionPostingLinks.Add(new ErpWeb.Model.Entities.Production.ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", CommandType = "MATERIAL_ISSUE_POST",
                PostingRequestId = "req-30", WorkOrderId = 1, ProductionDocumentType = "MATERIAL_ISSUE",
                ProductionDocumentNo = "MI-30", InventoryBatchNo = 30, Status = "Succeeded",
                CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
            });
            await db.SaveChangesAsync();
        }

        var resolver = new CostingRepairOwnershipResolver(_factory, new Tenant());
        Assert.Equal(CostingRepairOwnerTypes.SalesInvoice, (await Resolve(resolver, invoice)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.SalesDeliveryOrder, (await Resolve(resolver, delivery)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.CustomerReturn, (await Resolve(resolver, directReturn)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.SalesCreditNote, (await Resolve(resolver, creditReturn)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.VendorReturn, (await Resolve(resolver, directVendor)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.PurchaseCreditNote, (await Resolve(resolver, purchaseReturn)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.PurchaseGoodsReceipt, (await Resolve(resolver, receipt)).Owner!.OwnerType);
        Assert.Equal(CostingRepairOwnerTypes.PurchaseGoodsReceipt, (await Resolve(resolver, nonStock)).Owner!.OwnerType);
        Assert.False((await Resolve(resolver, forceClosed)).IsProven);
        Assert.Equal(CostingRepairOwnerTypes.ProductionMaterialIssue, (await Resolve(resolver, linkedIssue)).Owner!.OwnerType);
    }

    [Fact]
    public async Task Stale_preview_does_not_call_the_module_adapter()
    {
        var postingId = await AddPostingAsync("MR", "40", isSealed: true);
        var adapter = new RecordingAdapter();
        var access = new Access(viewCost: true);
        var planner = new CostingRepairPlanner(
            _factory, new Tenant(), access, new CostingRepairOwnershipResolver(_factory, new Tenant()), [adapter]);
        var plan = await planner.PlanAsync(postingId);
        Assert.True(plan.CanRepair);

        var service = new CostingRepairService(
            _factory, new Tenant(), access, planner, [adapter]);
        var stale = await service.ExecuteReverseAsync(postingId, "not-the-preview", "operator reason");
        Assert.True(stale.Stale);
        Assert.Equal(0, adapter.Calls);

        var executed = await service.ExecuteReverseAsync(postingId, plan.PreviewHash, "operator reason");
        Assert.Equal("REPLAN_REQUIRED", executed.Status);
        Assert.Equal(1, adapter.Calls);
        await using var db = await _factory.CreateDbContextAsync();
        var events = await db.CostingRepairAuditEvents.OrderBy(x => x.Sequence).ToListAsync();
        Assert.Equal(["STEP_PREPARED", "STEP_FAILED"], events.Select(x => x.EventType).ToArray());
    }

    [Fact]
    public async Task Production_preview_adapter_does_not_offer_execution()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            db.ProductionPostingLinks.Add(new ErpWeb.Model.Entities.Production.ProductionPostingLink
            {
                CompanyCode = "DEMO", BranchCode = "HQ", CommandType = "MATERIAL_ISSUE_POST",
                PostingRequestId = "req-41", WorkOrderId = 1, ProductionDocumentType = "MATERIAL_ISSUE",
                ProductionDocumentNo = "41", InventoryBatchNo = 41, Status = "Succeeded",
                CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
            });
            await db.SaveChangesAsync();
        }
        var postingId = await AddPostingAsync("IP", "90041", isSealed: true, sourceDocumentNo: "41", sequence: 41);
        var planner = new CostingRepairPlanner(
            _factory, new Tenant(), new Access(viewCost: true),
            new CostingRepairOwnershipResolver(_factory, new Tenant()),
            [new ProductionMaterialIssueRepairAdapter()]);
        var plan = await planner.PlanAsync(postingId);
        Assert.False(plan.CanRepair);
        Assert.Contains("preview-only", plan.BlockingReason, StringComparison.OrdinalIgnoreCase);
    }

    private static Task<CostingRepairOwnershipResult> Resolve(ICostingRepairOwnershipResolver resolver, long postingId) =>
        resolver.ResolveAsync(new CostingRepairNode(postingId, "ignored", "ignored", "ignored"));

    private async Task<long> AddPostingAsync(
        string type, string id, bool isSealed, long? epochId = null, DateTime? effective = null, bool productionLink = false,
        string? sourceDocumentNo = null, string? postingRole = null, long? reversesPostingId = null, long? sequence = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var when = effective ?? new DateTime(2026, 10, 2);
        var posting = new StockPosting
        {
            CompanyCode = "DEMO", BranchCode = "HQ", LedgerEpochId = epochId ?? _epochId,
            PostingSequence = sequence ?? long.Parse(id), RequestId = Guid.NewGuid(),
            CommandType = type, RequestFingerprint = new string('C', 64),
            SourceModule = "INVENTORY", SourceDocumentType = type, SourceDocumentId = id, SourceDocumentNo = sourceDocumentNo ?? id,
            DocumentRevision = 1, PostingRole = postingRole ?? "PRIMARY",
            SourceSnapshotJson = "{}", SourceSnapshotHash = new string('D', 64),
            EffectiveAt = when, BusinessDate = when.Date, PeriodKey = "2026-10",
            PostedAtUtc = DateTime.UtcNow, PostedBy = "tester",
            SealedAtUtc = isSealed ? DateTime.UtcNow : null,
            ProductionPostingLinkId = productionLink ? 9001 : null,
            ReversesPostingId = reversesPostingId
        };
        db.StockPostings.Add(posting);
        await db.SaveChangesAsync();
        return posting.Id;
    }

    private async Task<long> AddFactAsync(
        long postingId, int direction, decimal qty, decimal value,
        string? company = null, long? reverses = null, long? epochId = null,
        string? documentType = null, string? documentNo = null, string? line = null,
        string? warehouse = null, DateTime? effective = null, int split = 0, string? costMethod = null,
        int postingLineNo = 1, string item = "ITEM-1")
    {
        await using var db = await _factory.CreateDbContextAsync();
        var posting = await db.StockPostings.SingleAsync(x => x.Id == postingId);
        var when = effective ?? posting.EffectiveAt;
        var fact = new StockValuationFact
        {
            CompanyCode = company ?? "DEMO", BranchCode = "HQ", LedgerEpochId = epochId ?? posting.LedgerEpochId,
            StockPostingId = postingId, PostingLineNo = postingLineNo, SplitOrdinal = split,
            SourceLineId = line ?? "1", SourceDocumentType = documentType ?? posting.SourceDocumentType,
            SourceDocumentId = documentNo ?? posting.SourceDocumentId,
            SourceDocumentNo = documentNo ?? posting.SourceDocumentNo, SourceDocumentLine = line,
            EffectiveAt = when, BusinessDate = when.Date, PeriodKey = "2026-10",
            ItemCode = item, WarehouseCode = warehouse, BaseUom = "EA",
            MovementCode = posting.SourceDocumentType, Direction = direction, BaseQty = qty,
            CostMethod = costMethod ?? StockCostMethods.MovingAverage, UnitCost = qty == 0m ? 0m : value / qty,
            CostAmount = value, BaseCostAmount = value,
            ValuationSource = StockValuationSources.MovingAverage, ValuationStatus = StockValuationStatuses.Valued,
            ReversesValuationFactId = reverses, CreatedAtUtc = DateTime.UtcNow, CreatedBy = "tester"
        };
        db.StockValuationFacts.Add(fact);
        await db.SaveChangesAsync();
        return fact.Id;
    }

    private async Task AddStateAsync(decimal qty, decimal value, decimal average, string item = "ITEM-1", string? costMethod = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.StockCostStates.Add(new StockCostState
        {
            CompanyCode = "DEMO", BranchCode = "HQ", ItemCode = item,
            CostMethod = costMethod ?? StockCostMethods.MovingAverage,
            OnHandBaseQty = qty, InventoryValue = value, AverageUnitCost = average, CurrentUnitCost = average,
            RowVersion = [1]
        });
        await db.SaveChangesAsync();
    }

    private static IvTrxHistory History(int batchNo, string type, string? doNo = null, string? invNo = null) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = batchNo, TrxLineNo = 1,
        TrxDtTime = new DateTime(2026, 10, 2), TrxType = type, BatchStatus = IvBatchStatuses.Posted,
        ICode = "ITEM-1", IStatus = "ACTIVE", DoNo = doNo, InvNo = invNo,
        CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private static IvTrxBatch Batch(int batchNo, string type, string? reference, bool forceClosed = false) => new()
    {
        CompanyCode = "DEMO", BranchCode = "HQ", BatchNo = batchNo,
        TrxDtTime = new DateTime(2026, 10, 2), TrxType = type, BatchStatus = IvBatchStatuses.Posted,
        RefNo = reference, ForceCloseDate = forceClosed ? new DateTime(2026, 10, 3) : null,
        CreatedDate = DateTime.UtcNow, CreatedBy = "tester"
    };

    private sealed class RecordingAdapter : ICostingRepairAdapter
    {
        public int Calls { get; private set; }
        public IReadOnlySet<string> OwnerTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            CostingRepairOwnerTypes.MiscReceipt
        };

        public Task<CostingRepairCapability> CanHandleAsync(
            CostingRepairNode node, CostingRepairOwner owner, CancellationToken cancellationToken) =>
            Task.FromResult(new CostingRepairCapability(true, null));

        public Task<CostingRepairStepResult> ReverseAsync(
            CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CostingRepairStepResult(false, true, "STALE_PREVIEW", null));
        }

        public Task<CostingRepairStepResult> RepostAsync(
            CostingRepairNode node, CostingRepairOwner owner, CostingRepairExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new CostingRepairStepResult(false, false, "not used", null));
    }

    private sealed class Access(bool viewCost, bool viewPrice = false, bool repairCost = true, bool rollback = true) : IAccessRightService
    {
        public Task<bool> CanAccessAsync(string menuCode, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> CanAsync(string menuCode, string permissionCode, CancellationToken cancellationToken = default)
        {
            if (permissionCode == PermissionCodes.ViewCost)
                return Task.FromResult(viewCost);
            if (permissionCode == PermissionCodes.ViewPrice)
                return Task.FromResult(viewPrice);
            if (permissionCode == PermissionCodes.RepairCost)
                return Task.FromResult(repairCost);
            if (permissionCode == PermissionCodes.Rollback)
                return Task.FromResult(rollback);
            return Task.FromResult(true);
        }

        public Task<IReadOnlySet<string>> GetPermissionsAsync(string menuCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());

        public Task<MenuAccessRight?> GetAccessAsync(string menuCode, CancellationToken cancellationToken = default) =>
            Task.FromResult<MenuAccessRight?>(null);

        public Task<IReadOnlyDictionary<string, MenuAccessRight>> GetAllAccessAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, MenuAccessRight>>(new Dictionary<string, MenuAccessRight>());

        public Task RefreshPermissionsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

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
