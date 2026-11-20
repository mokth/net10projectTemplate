using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Core.Settings;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using ErpWeb.Tests.Procurement.Transaction;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace ErpWeb.Tests.Procurement.Sourcing;

internal sealed class FakeSourcingDocNumberingService : IDocumentNumberingService
{
    private int _rfqSeq;
    private int _prSeq;
    private int _poSeq;

    public Task<DocumentNumberResult> NextAsync(
        AppDbContext db,
        string module,
        string extraPrefix,
        DateTime documentDate,
        DocumentNumberRequestMode requestMode,
        string currentDocNo,
        CancellationToken ct)
    {
        return module.ToUpperInvariant() switch
        {
            "RFQ" => Task.FromResult(new DocumentNumberResult($"RFQ-{documentDate:yyyyMM}-{Interlocked.Increment(ref _rfqSeq):D4}", "RFQ")),
            "PR" => Task.FromResult(new DocumentNumberResult($"PR{documentDate:yyMM}-{Interlocked.Increment(ref _prSeq):D4}", "PR")),
            "PO" => Task.FromResult(new DocumentNumberResult($"PO{documentDate:yyMM}-{Interlocked.Increment(ref _poSeq):D4}", "PO")),
            _ => throw new InvalidOperationException($"Unexpected module '{module}'.")
        };
    }
}

internal sealed class ToggleableSourcingFeatureGuard : IPurchaseSourcingFeatureGuard
{
    public bool Enabled { get; set; } = true;

    public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Enabled);

    public Task<bool> IsEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Enabled);

    public async Task EnsureEnabledAuthoritativeAsync(
        AppDbContext db,
        string companyCode,
        bool lockRowForUpdate = false,
        CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAuthoritativeAsync(db, companyCode, lockRowForUpdate, cancellationToken))
        {
            throw new InvalidOperationException("Purchase Sourcing (RFQ) is disabled for this company.");
        }
    }
}

[Trait(TestCategories.Name, TestCategories.Purchase)]
public sealed class PoSourcingTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 15);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _attachRoot;
    private readonly ToggleableSourcingFeatureGuard _featureGuard = new() { Enabled = true };
    private readonly FakeSourcingDocNumberingService _numbering = new();

    public PoSourcingTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _attachRoot = Path.Combine(Path.GetTempPath(), "posourcing-attach-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_attachRoot);
    }

    public async Task InitializeAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();

        db.IvWarehouses.Add(new IvWarehouse
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            WarehouseDesc = "Main Warehouse",
            IsActive = true
        });
        db.MsUoms.AddRange(
            new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true },
            new MsUom { CompanyCode = "DEMO", UomCode = "BOX", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.SaCurrencies.AddRange(
            new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", CurrDesc = "Ringgit", IsActive = true },
            new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", CurrDesc = "US Dollar", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            TaxGrDesc = "Standard 6%",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Steel Bearing A100",
                IClassCode = "RAW",
                StdUom = "EA",
                PurUom = "EA",
                PurStdPackSize = 1m,
                PurchasePrice = 25m,
                PurchaseTaxGroup = "SR",
                IsActive = true,
                DefWarehouse = "MAIN",
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A200",
                IDesc = "Copper Coil A200",
                IClassCode = "RAW",
                StdUom = "EA",
                PurUom = "EA",
                PurStdPackSize = 1m,
                PurchasePrice = 40m,
                PurchaseTaxGroup = "SR",
                IsActive = true,
                DefWarehouse = "MAIN",
                RowVersion = Guid.NewGuid().ToByteArray()
            });
        db.PoSuppliers.AddRange(
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Industrial Supply",
                Currency = "MYR",
                TaxGrCode = "SR",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP02",
                SuppName = "Beta Global Metals",
                Currency = "USD",
                TaxGrCode = "SR",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        try
        {
            if (Directory.Exists(_attachRoot))
            {
                Directory.Delete(_attachRoot, recursive: true);
            }
        }
        catch
        {
            // ignore cleanup errors in temp
        }

        return Task.CompletedTask;
    }

    [Fact]
    public void AppSettingCatalogue_DefinesProcurementSourcingEnabled_DefaultFalse()
    {
        var def = AppSettingCatalogue.Find(AppSettingModules.Procurement, AppSettingCatalogue.ProcurementKeys.SourcingEnabled);
        Assert.NotNull(def);
        Assert.Equal(AppSettingType.Flag, def!.Type);
        Assert.Equal(AppSettingScope.Company, def.AllowedScopes);
        Assert.Equal("false", def.DefaultValue);
    }

    [Fact]
    public async Task FeatureOff_FailsClosed_WithoutBreakingPrOrPoCreation()
    {
        _featureGuard.Enabled = false;
        var (sourcing, prService, poService) = CreateServices();

        var rfqRes = await sourcing.SaveDraftAsync(new PoSourcingSaveRequest
        {
            Title = "Blocked RFQ",
            Lines = [new PoSourcingLineDto { ItemCode = "A100", ItemDescription = "Steel Bearing A100", RequestedPurchaseQty = 10m }]
        });
        Assert.False(rfqRes.Succeeded);
        Assert.Equal(PoSourcingErrorKind.FeatureDisabled, rfqRes.ErrorKind);

        // Existing PR and PO creation must work normally when Sourcing is OFF
        var prRes = await prService.SaveNewAsync(new PoPrSaveRequest
        {
            CreateDt = FixedToday,
            Requester = "user",
            PrType = PoPrTypes.Purchasing,
            Lines =
            [
                new PoPrLineDto
                {
                    ICode = "A100",
                    PurchaseQty = 5m,
                    Qty = 5m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    Currency = "MYR",
                    ToWarehouse = "MAIN"
                }
            ]
        });
        Assert.True(prRes.Succeeded, prRes.ErrorMessage);

        var poRes = await poService.SaveNewAsync(new PoOrderSaveRequest
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Industrial Supply",
            CurCode = "MYR",
            Lines =
            [
                new PoOrderLineDto
                {
                    ICode = "A100",
                    PoPurQty = 5m,
                    PoQty = 5m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = "MYR",
                    PoUnitPrice = 24m,
                    ToWarehouse = "MAIN"
                }
            ]
        });
        Assert.True(poRes.Succeeded, poRes.ErrorMessage);
    }

    [Fact]
    public void ComparisonCalculator_NormalizesMultiCurrency_Freight_Tax_AndFlagsLowestCost()
    {
        var header = new PoSourcingHdr
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SourcingId = 1,
            BaseCurrency = "MYR"
        };
        var lines = new List<PoSourcingLine>
        {
            new()
            {
                SourcingLineId = 10,
                LineNo = 1,
                ICode = "A100",
                Description = "Steel Bearing",
                RequestedQty = 100m,
                RequestedUom = "EA",
                PackSz = 1m,
                StdUom = "EA",
                RequestedStdQty = 100m
            }
        };
        var suppliers = new List<PoSourcingSupplier>
        {
            new() { SourcingSupplierId = 1, SuppCode = "SUP01", SupplierName = "Alpha" },
            new() { SourcingSupplierId = 2, SuppCode = "SUP02", SupplierName = "Beta" }
        };

        // Quote 1: MYR 20.00 + MYR 100 freight => Landed MYR 2,100 => 21.00 / EA, Lead time 10 days
        var quote1 = new PoSourcingQuote
        {
            QuoteId = 101,
            SourcingSupplierId = 1,
            SupplierQuoteRef = "Q-ALPHA",
            QuoteRevNo = 1,
            Status = PoSourcingQuoteStatuses.Received,
            Currency = "MYR",
            ExchangeRateToBase = 1m,
            ExchangeRateDate = FixedToday,
            HeaderFreightAmount = 100m,
            FreightAllocationMode = PoSourcingFreightAllocationModes.ValueProRata,
            Lines =
            [
                new PoSourcingQuoteLine
                {
                    QuoteLineId = 1001,
                    QuoteId = 101,
                    SourcingLineId = 10,
                    LineStatus = PoSourcingQuoteLineStatuses.Quoted,
                    QuotedQty = 100m,
                    QuotedUom = "EA",
                    QuotedPackSz = 1m,
                    QuotedStdQty = 100m,
                    UnitPrice = 20m,
                    LeadTimeDays = 10
                }
            ]
        };

        // Quote 2: USD 4.50 @ FX 4.40 = MYR 19.80 / EA, Lead time 14 days
        var quote2 = new PoSourcingQuote
        {
            QuoteId = 102,
            SourcingSupplierId = 2,
            SupplierQuoteRef = "Q-BETA",
            QuoteRevNo = 1,
            Status = PoSourcingQuoteStatuses.Received,
            Currency = "USD",
            ExchangeRateToBase = 4.40m,
            ExchangeRateDate = FixedToday,
            HeaderFreightAmount = 0m,
            FreightAllocationMode = PoSourcingFreightAllocationModes.ValueProRata,
            Lines =
            [
                new PoSourcingQuoteLine
                {
                    QuoteLineId = 1002,
                    QuoteId = 102,
                    SourcingLineId = 10,
                    LineStatus = PoSourcingQuoteLineStatuses.Quoted,
                    QuotedQty = 100m,
                    QuotedUom = "EA",
                    QuotedPackSz = 1m,
                    QuotedStdQty = 100m,
                    UnitPrice = 4.50m,
                    LeadTimeDays = 14
                }
            ]
        };

        var matrix = PoSourcingComparisonCalculator.Evaluate(header, lines, suppliers, [quote1, quote2], FixedToday);
        var lineRow = Assert.Single(matrix.Lines);
        Assert.Equal(1002, lineRow.RecommendedQuoteLineId);
        Assert.Equal(1001, lineRow.ShortestLeadTimeQuoteLineId);

        var cell1 = lineRow.Offers.Single(c => c.QuoteLineId == 1001);
        var cell2 = lineRow.Offers.Single(c => c.QuoteLineId == 1002);
        Assert.Equal(21.00m, cell1.NormalizedBaseUnitCost);
        Assert.Equal(19.80m, cell2.NormalizedBaseUnitCost);
        Assert.True(cell2.IsLowestComparableOffer);
        Assert.True(cell1.IsShortestLeadTimeOffer);
    }

    [Fact]
    public async Task AwardAndConvertToPr_PreservesAwardedUnitPrice_AndEnforcesIdempotency()
    {
        var (sourcing, prService, _) = CreateServices();

        var draftRes = await sourcing.SaveDraftAsync(new PoSourcingSaveRequest
        {
            SourceType = PoSourcingSourceTypes.Direct,
            RequestDate = FixedToday,
            Title = "RFQ for Bearing A100",
            BaseCurrency = "MYR",
            DefaultToWarehouse = "MAIN",
            MarkAsOpen = true,
            Lines =
            [
                new PoSourcingLineDto
                {
                    ItemCode = "A100",
                    ItemDescription = "Steel Bearing A100",
                    RequestedPurchaseQty = 10m,
                    RequestedPurchaseUom = "EA",
                    PackSize = 1m,
                    StdUom = "EA",
                    ToWarehouse = "MAIN"
                }
            ],
            Suppliers =
            [
                new PoSourcingSupplierDto
                {
                    SupplierCode = "SUP01",
                    SupplierName = "Alpha Industrial Supply",
                    DefaultCurrency = "MYR"
                }
            ]
        });
        Assert.True(draftRes.Succeeded, draftRes.ErrorMessage);
        var doc = draftRes.Document!;
        var rfqLineId = doc.Lines[0].SourcingLineId;
        var supId = doc.Suppliers[0].SourcingSupplierId;

        // Record supplier quote at 18.75 (different from IvStockMaster.PurchasePrice = 25.00)
        var quoteRes = await sourcing.SaveQuoteAsync(new PoSourcingQuoteSaveRequest
        {
            SourcingId = doc.SourcingId,
            SourcingSupplierId = supId,
            SupplierQuoteNo = "QT-SUP01-01",
            QuoteDate = FixedToday,
            Currency = "MYR",
            ExchangeRate = 1m,
            Lines =
            [
                new PoSourcingQuoteLineDto
                {
                    SourcingLineId = rfqLineId,
                    LineStatus = PoSourcingQuoteLineStatuses.Quoted,
                    OfferedQty = 10m,
                    QuoteUom = "EA",
                    PackSize = 1m,
                    QuotedUnitPrice = 18.75m
                }
            ]
        });
        Assert.True(quoteRes.Succeeded, quoteRes.ErrorMessage);
        var quoteLineId = quoteRes.Document!.Quotes[0].Lines[0].QuoteLineId;

        var awardRes = await sourcing.SaveAwardAsync(new PoSourcingAwardRequest
        {
            SourcingId = doc.SourcingId,
            ApproveImmediately = true,
            SourcingRowVersion = quoteRes.Document.RowVersion,
            Lines =
            [
                new PoSourcingAwardLineInputDto
                {
                    SourcingLineId = rfqLineId,
                    QuoteLineId = quoteLineId,
                    AwardedPurchaseQty = 10m
                }
            ]
        });
        Assert.True(awardRes.Succeeded, awardRes.ErrorMessage);
        var awardId = awardRes.Document!.Awards[0].AwardId;

        var reqId = Guid.NewGuid();
        var convRes = await sourcing.ConvertToPrAsync(new PoSourcingConvertToPrRequest
        {
            SourcingId = doc.SourcingId,
            AwardId = awardId,
            RequestId = reqId
        });
        Assert.True(convRes.Succeeded, convRes.ErrorMessage);
        var prNo = Assert.Single(convRes.ConversionResult!.CreatedDocumentNumbers);
        Assert.False(convRes.ConversionResult.WasIdempotentReplay);

        // Verify the created PR preserved the awarded unit price 18.75 (not master 25.00)
        var prDoc = await prService.GetAsync(prNo);
        Assert.True(prDoc.Succeeded);
        Assert.Equal(18.75m, prDoc.Document!.Lines[0].UnitPrice);

        // Replay with identical RequestId and payload returns idempotent replay without creating a 2nd PR
        var replayRes = await sourcing.ConvertToPrAsync(new PoSourcingConvertToPrRequest
        {
            SourcingId = doc.SourcingId,
            AwardId = awardId,
            RequestId = reqId
        });
        Assert.True(replayRes.Succeeded);
        Assert.True(replayRes.ConversionResult!.WasIdempotentReplay);
        Assert.Equal(prNo, Assert.Single(replayRes.ConversionResult.CreatedDocumentNumbers));

        // Replay with same RequestId but altered quantity fails with IdempotencyConflict
        var conflictRes = await sourcing.ConvertToPrAsync(new PoSourcingConvertToPrRequest
        {
            SourcingId = doc.SourcingId,
            AwardId = awardId,
            RequestId = reqId,
            SelectedLines =
            [
                new PoSourcingConversionLineSelectionDto
                {
                    AwardLineId = awardRes.Document.Awards[0].Lines[0].AwardLineId,
                    ConvertPurchaseQty = 5m
                }
            ]
        });
        Assert.False(conflictRes.Succeeded);
        Assert.Equal(PoSourcingErrorKind.IdempotencyConflict, conflictRes.ErrorKind);
    }

    [Fact]
    public async Task ConvertToPo_SplitsMultiSupplierAwards_EnforcesTargetLifecycleSync_AndSupportsFeatureOffQuarantine()
    {
        var (sourcing, _, poService) = CreateServices();

        var draftRes = await sourcing.SaveDraftAsync(new PoSourcingSaveRequest
        {
            SourceType = PoSourcingSourceTypes.Direct,
            RequestDate = FixedToday,
            Title = "Multi-Supplier RFQ",
            BaseCurrency = "MYR",
            DefaultToWarehouse = "MAIN",
            MarkAsOpen = true,
            Lines =
            [
                new PoSourcingLineDto { ItemCode = "A100", ItemDescription = "Steel Bearing A100", RequestedPurchaseQty = 10m, RequestedPurchaseUom = "EA", PackSize = 1m, StdUom = "EA" },
                new PoSourcingLineDto { ItemCode = "A200", ItemDescription = "Copper Coil A200", RequestedPurchaseQty = 20m, RequestedPurchaseUom = "EA", PackSize = 1m, StdUom = "EA" }
            ],
            Suppliers =
            [
                new PoSourcingSupplierDto { SupplierCode = "SUP01", SupplierName = "Alpha Industrial Supply", DefaultCurrency = "MYR" },
                new PoSourcingSupplierDto { SupplierCode = "SUP02", SupplierName = "Beta Global Metals", DefaultCurrency = "USD" }
            ]
        });
        Assert.True(draftRes.Succeeded, draftRes.ErrorMessage);
        var doc = draftRes.Document!;
        var line1Id = doc.Lines[0].SourcingLineId;
        var line2Id = doc.Lines[1].SourcingLineId;
        var sup1Id = doc.Suppliers.Single(s => s.SupplierCode == "SUP01").SourcingSupplierId;
        var sup2Id = doc.Suppliers.Single(s => s.SupplierCode == "SUP02").SourcingSupplierId;

        var q1Res = await sourcing.SaveQuoteAsync(new PoSourcingQuoteSaveRequest
        {
            SourcingId = doc.SourcingId,
            SourcingSupplierId = sup1Id,
            SupplierQuoteNo = "Q-SUP01",
            QuoteDate = FixedToday,
            Currency = "MYR",
            ExchangeRate = 1m,
            Lines =
            [
                new PoSourcingQuoteLineDto { SourcingLineId = line1Id, LineStatus = PoSourcingQuoteLineStatuses.Quoted, OfferedQty = 10m, QuoteUom = "EA", PackSize = 1m, QuotedUnitPrice = 22m },
                new PoSourcingQuoteLineDto { SourcingLineId = line2Id, LineStatus = PoSourcingQuoteLineStatuses.Quoted, OfferedQty = 20m, QuoteUom = "EA", PackSize = 1m, QuotedUnitPrice = 45m }
            ]
        });
        Assert.True(q1Res.Succeeded, q1Res.ErrorMessage);

        var q2Res = await sourcing.SaveQuoteAsync(new PoSourcingQuoteSaveRequest
        {
            SourcingId = doc.SourcingId,
            SourcingSupplierId = sup2Id,
            SupplierQuoteNo = "Q-SUP02",
            QuoteDate = FixedToday,
            Currency = "USD",
            ExchangeRate = 4.20m,
            Lines =
            [
                new PoSourcingQuoteLineDto { SourcingLineId = line1Id, LineStatus = PoSourcingQuoteLineStatuses.Quoted, OfferedQty = 10m, QuoteUom = "EA", PackSize = 1m, QuotedUnitPrice = 6m },
                new PoSourcingQuoteLineDto { SourcingLineId = line2Id, LineStatus = PoSourcingQuoteLineStatuses.Quoted, OfferedQty = 20m, QuoteUom = "EA", PackSize = 1m, QuotedUnitPrice = 9m }
            ]
        });
        Assert.True(q2Res.Succeeded, q2Res.ErrorMessage);

        var q1Line1 = q1Res.Document!.Quotes.Single(q => q.SupplierCode == "SUP01").Lines.Single(l => l.SourcingLineId == line1Id);
        var q2Line2 = q2Res.Document!.Quotes.Single(q => q.SupplierCode == "SUP02").Lines.Single(l => l.SourcingLineId == line2Id);

        var awardRes = await sourcing.SaveAwardAsync(new PoSourcingAwardRequest
        {
            SourcingId = doc.SourcingId,
            ApproveImmediately = true,
            SourcingRowVersion = q2Res.Document.RowVersion,
            Lines =
            [
                new PoSourcingAwardLineInputDto { SourcingLineId = line1Id, QuoteLineId = q1Line1.QuoteLineId, AwardedPurchaseQty = 10m },
                new PoSourcingAwardLineInputDto { SourcingLineId = line2Id, QuoteLineId = q2Line2.QuoteLineId, AwardedPurchaseQty = 20m }
            ]
        });
        Assert.True(awardRes.Succeeded, awardRes.ErrorMessage);
        var awardId = awardRes.Document!.Awards[0].AwardId;

        var convRes = await sourcing.ConvertToPoAsync(new PoSourcingConvertToPoRequest
        {
            SourcingId = doc.SourcingId,
            AwardId = awardId,
            RequestId = Guid.NewGuid()
        });
        Assert.True(convRes.Succeeded, convRes.ErrorMessage);
        Assert.Equal(2, convRes.ConversionResult!.CreatedDocumentNumbers.Count);
        Assert.Equal(PoSourcingStatuses.Converted, convRes.Document!.Status);

        var po1No = convRes.ConversionResult.CreatedDocumentNumbers[0];
        var po1Get = await poService.GetAsync(po1No);
        Assert.True(po1Get.Succeeded);
        var po1Doc = po1Get.Document!;

        // While feature is ON, increasing PO line quantity above awarded quantity (10 -> 15) must be blocked
        var illegalIncrease = await poService.UpdateAsync(po1No, new PoOrderSaveRequest
        {
            PoDate = po1Doc.PoDate,
            VendCode = po1Doc.VendCode,
            VendName = po1Doc.VendName,
            CurCode = po1Doc.CurCode,
            RowVersion = po1Doc.RowVersion,
            Lines =
            [
                new PoOrderLineDto
                {
                    Line = 1,
                    ICode = po1Doc.Lines[0].ICode,
                    PoPurQty = 15m,
                    PoQty = 15m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = po1Doc.CurCode,
                    PoUnitPrice = po1Doc.Lines[0].PoUnitPrice,
                    ToWarehouse = "MAIN"
                }
            ]
        });
        Assert.False(illegalIncrease.Succeeded);
        Assert.Contains("exceeds remaining approved sourcing award quantity", illegalIncrease.ErrorMessage ?? string.Empty);

        // Turn feature OFF -> editing the PO is allowed and quarantines/detaches the conversion link!
        _featureGuard.Enabled = false;
        var reloadPo1 = (await poService.GetAsync(po1No)).Document!;
        var editWhileOff = await poService.UpdateAsync(po1No, new PoOrderSaveRequest
        {
            PoDate = reloadPo1.PoDate,
            VendCode = reloadPo1.VendCode,
            VendName = reloadPo1.VendName,
            CurCode = reloadPo1.CurCode,
            RowVersion = reloadPo1.RowVersion,
            Lines =
            [
                new PoOrderLineDto
                {
                    Line = 1,
                    ICode = reloadPo1.Lines[0].ICode,
                    PoPurQty = 15m,
                    PoQty = 15m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = reloadPo1.CurCode,
                    PoUnitPrice = reloadPo1.Lines[0].PoUnitPrice,
                    ToWarehouse = "MAIN"
                }
            ]
        });
        Assert.True(editWhileOff.Succeeded, editWhileOff.ErrorMessage);

        _featureGuard.Enabled = true;
        var afterReenable = await sourcing.GetAsync(doc.SourcingId);
        Assert.True(afterReenable.Succeeded);
        var quarantinedConv = afterReenable.Document!.Conversions.Single(c => c.TargetDocNo == po1No);
        Assert.Equal(PoSourcingConversionStatuses.DetachedQuarantined, quarantinedConv.Status);
    }

    [Fact]
    public async Task ViewCostPermission_RedactsPricingAndBlocksSensitiveAttachments()
    {
        var denyViewCost = new Mock<IAccessRightService>();
        denyViewCost
            .Setup(x => x.CanAsync(It.IsAny<string>(), It.Is<string>(p => p != PermissionCodes.ViewCost), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        denyViewCost
            .Setup(x => x.CanAsync(It.IsAny<string>(), PermissionCodes.ViewCost, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var (sourcingAdmin, _, _) = CreateServices();
        var draftRes = await sourcingAdmin.SaveDraftAsync(new PoSourcingSaveRequest
        {
            Title = "Confidential Quote RFQ",
            Lines = [new PoSourcingLineDto { ItemCode = "A100", ItemDescription = "Steel Bearing A100", RequestedPurchaseQty = 5m, TargetUnitPrice = 25m }],
            Suppliers = [new PoSourcingSupplierDto { SupplierCode = "SUP01", SupplierName = "Alpha" }]
        });
        var sourcingId = draftRes.Document!.SourcingId;
        var lineId = draftRes.Document.Lines[0].SourcingLineId;
        var supId = draftRes.Document.Suppliers[0].SourcingSupplierId;

        await sourcingAdmin.SaveQuoteAsync(new PoSourcingQuoteSaveRequest
        {
            SourcingId = sourcingId,
            SourcingSupplierId = supId,
            SupplierQuoteNo = "CONF-01",
            Currency = "MYR",
            ExchangeRate = 1m,
            Lines = [new PoSourcingQuoteLineDto { SourcingLineId = lineId, OfferedQty = 5m, QuotedUnitPrice = 19.50m }]
        });

        var uploadRes = await sourcingAdmin.UploadAttachmentAsync(new PoSourcingAttachmentUploadRequest
        {
            SourcingId = sourcingId,
            ScopeType = PoSourcingAttachmentScopes.Quote,
            FileName = "supplier-quote.pdf",
            Content = "%PDF-1.4 sample"u8.ToArray(),
            ContainsSensitivePricing = true
        });
        Assert.True(uploadRes.Succeeded, uploadRes.ErrorMessage);
        var attId = uploadRes.Document!.Attachments[0].AttachmentId;

        var (sourcingNoCost, _, _) = CreateServices(denyViewCost);
        var getRes = await sourcingNoCost.GetAsync(sourcingId);
        Assert.True(getRes.Succeeded);
        Assert.True(getRes.Document!.PricingRedacted);
        Assert.Null(getRes.Document.Lines[0].TargetUnitPrice);
        Assert.Null(getRes.Document.Quotes[0].TotalQuoteAmount);
        Assert.Null(getRes.Document.Quotes[0].Lines[0].QuotedUnitPrice);

        var dlRes = await sourcingNoCost.DownloadAttachmentAsync(sourcingId, attId);
        Assert.False(dlRes.Succeeded);
        Assert.Equal(PoSourcingErrorKind.Authorization, dlRes.ErrorKind);
    }

    private (PoSourcingService Sourcing, PoPrService Pr, PoOrderService Po) CreateServices(
        Mock<IAccessRightService>? accessOverride = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");

        var access = accessOverride ?? DefaultAccess();
        var dates = new FixedCurrentDateService(FixedToday);
        var attachOpts = Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot });

        var prAttach = new PoPrAttachmentService(
            _factory,
            tenant,
            access.Object,
            dates,
            attachOpts,
            Options.Create(new PoPrOptions()),
            NullLogger<PoPrAttachmentService>.Instance,
            TestDisabledProcurementApprovalGuard.Instance);

        var poRepo = new PoOrderRepository();
        var prService = new PoPrService(
            _factory,
            tenant,
            access.Object,
            _numbering,
            dates,
            new PoPrRepository(),
            Options.Create(new PoPrOptions()),
            prAttach,
            NullLogger<PoPrService>.Instance,
            TestDisabledProcurementApprovalGuard.Instance,
            poRepo,
            materialAvailability: null,
            sourcingGuard: _featureGuard);

        var poAttach = new PoOrderAttachmentService(
            _factory,
            tenant,
            access.Object,
            dates,
            attachOpts,
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance,
            TestDisabledProcurementApprovalGuard.Instance);

        var poService = new PoOrderService(
            _factory,
            tenant,
            access.Object,
            _numbering,
            dates,
            poRepo,
            Options.Create(new PoOrderOptions()),
            poAttach,
            NullLogger<PoOrderService>.Instance,
            TestDisabledProcurementApprovalGuard.Instance,
            _featureGuard);

        var sourcingService = new PoSourcingService(
            _factory,
            tenant,
            access.Object,
            _featureGuard,
            _numbering,
            dates,
            prService,
            poService,
            attachOpts,
            NullLogger<PoSourcingService>.Instance);

        return (sourcingService, prService, poService);
    }

    private static Mock<IAccessRightService> DefaultAccess()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
