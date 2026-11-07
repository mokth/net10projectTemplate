using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests.Procurement.Transaction;

/// <summary>
/// Isolated SQL Server: posting a GR drafted against RelNo 1 after the PO is revised
/// must fail closed. RecvQty and stock stay unchanged. Never DefaultConnection.
/// </summary>
[Collection("PoApprovalSqlGate3")]
[Trait(TestCategories.Name, TestCategories.Purchase)]
[Trait(TestCategories.Name, TestCategories.InventoryGoodsReceipt)]
[Trait(TestCategories.Name, TestCategories.SqlServer)]
public class GoodsReceiptPoRevisionSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);
    private readonly PoApprovalSqlGate3Fixture _harness;

    public GoodsReceiptPoRevisionSqlServerConcurrencyTests(PoApprovalSqlGate3Fixture harness)
    {
        _harness = harness;
    }

    [PoApprovalSqlFact]
    public async Task SqlServer_gr_post_after_po_revise_does_not_write_RecvQty_or_stock()
    {
        var factory = _harness.RequireFactory();
        var iCode = await SeedGrMastersAsync(factory);
        var poSut = CreatePo(factory);
        var savedPo = await poSut.SaveNewAsync(PoRequest(iCode, 10m));
        Assert.True(savedPo.Succeeded, savedPo.ErrorMessage);

        var gr = CreateGr(factory);
        var draft = await gr.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = savedPo.PoNo!,
                    PoRelNo = savedPo.Document!.PoRelNo,
                    PoLineNo = savedPo.Document.Lines[0].Line,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 4m
                }
            ]
        });
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var current = await poSut.GetAsync(savedPo.PoNo!);
        Assert.True(current.Succeeded, current.ErrorMessage);
        var reviseRequest = PoRequest(iCode, 10m);
        reviseRequest.RevisionReason = "Bump revision before GR post";
        reviseRequest.RowVersion = current.Document!.RowVersion;
        reviseRequest.Lines =
        [
            new PoOrderLineDto
            {
                Line = current.Document.Lines[0].Line,
                ICode = iCode,
                PoPurQty = 10m,
                PoQty = 10m,
                PurchaseUom = "EA",
                StdUom = "EA",
                PackSz = 1m,
                CurCode = "MYR",
                PoUnitPrice = 25m,
                TaxGroup = "SR",
                ToWarehouse = "MAIN"
            }
        ];
        var revised = await poSut.ReviseAsync(savedPo.PoNo!, reviseRequest);
        Assert.True(revised.Succeeded, revised.ErrorMessage);
        Assert.Equal(2, revised.Document!.PoRelNo);

        var posted = await gr.PostAsync([draft.BatchNo]);

        Assert.False(posted.Succeeded);
        Assert.Contains("revised", posted.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        await using var db = factory.CreateDbContext();
        Assert.Equal(0m, await db.PoOrderDetails
            .Where(x => x.PoNo == savedPo.PoNo && x.PoRelNo == 1)
            .Select(x => x.RecvQty)
            .SingleAsync());
        Assert.Equal(0m, await db.PoOrderDetails
            .Where(x => x.PoNo == savedPo.PoNo && x.PoRelNo == 2)
            .Select(x => x.RecvQty)
            .SingleAsync());
        Assert.False(await db.IvBalLocs.AnyAsync(x => x.ICode == iCode));
        Assert.Equal(IvBatchStatuses.New, await db.IvTrxBatches
            .Where(x => x.BatchNo == draft.BatchNo)
            .Select(x => x.BatchStatus)
            .SingleAsync());
    }

    private static async Task<string> SeedGrMastersAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();
        if (!await db.Companies.AnyAsync(x => x.CompanyCode == "DEMO"))
        {
            db.Companies.Add(new Company
            {
                CompanyCode = "DEMO",
                CompanyName = "Demo Company",
                CurrencyCode = "MYR",
                IsActive = true
            });
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR"))
        {
            db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        }

        if (!await db.MsUoms.AnyAsync(x => x.CompanyCode == "DEMO" && x.UomCode == "EA"))
        {
            db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        }

        if (!await db.IvClasses.AnyAsync(x => x.CompanyCode == "DEMO" && x.IClassCode == "RAW"))
        {
            db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        }

        if (!await db.IvStatuses.AnyAsync(x => x.CompanyCode == "DEMO" && x.IStatus == "ACTIVE"))
        {
            db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(new SaTaxGroup
            {
                CompanyCode = "DEMO",
                TaxGrCode = "SR",
                Percentage = 6m,
                TaxGlCode = "GLTAX"
            });
        }

        if (!await db.IvWarehouses.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.WarehouseCode == "MAIN"))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            });
        }

        if (!await db.IvLocations.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ"
                && x.WarehouseCode == "MAIN" && x.LocCode == "BIN1"))
        {
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                IsActive = true
            });
        }

        if (!await db.PoSuppliers.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.SuppCode == "SUP01"))
        {
            db.PoSuppliers.Add(new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                Country = "MY",
                IsActive = true
            });
        }

        var iCode = "G" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "GR revise race item",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 25m,
            PurchaseTaxGroup = "SR",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            DefWarehouse = "MAIN",
            DefLocation = "BIN1"
        });
        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = iCode,
            PurUom = "EA",
            UnitPrice = 25m,
            Tolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        await db.SaveChangesAsync();
        return iCode;
    }

    private static PoOrderSaveRequest PoRequest(string iCode, decimal qty) =>
        new()
        {
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Lines =
            [
                new PoOrderLineDto
                {
                    ICode = iCode,
                    PoPurQty = qty,
                    PoQty = qty,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    PackSz = 1m,
                    CurCode = "MYR",
                    PoUnitPrice = 25m,
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN"
                }
            ]
        };

    private static PoOrderService CreatePo(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", "AVUSR1");
        var attachRoot = Path.Combine(Path.GetTempPath(), "gr-rev-sql-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var approval = TestDisabledProcurementApprovalGuard.Instance;
        var attachments = new PoOrderAttachmentService(
            factory,
            tenant,
            Access().Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance,
            approval);
        return new PoOrderService(
            factory,
            tenant,
            Access().Object,
            new FakePoOrderDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attachments,
            NullLogger<PoOrderService>.Instance,
            approval);
    }

    private static IvGoodsReceiptService CreateGr(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext("DEMO", "HQ", "MAIN", "AVUSR1");
        var access = Access();
        var postingRepo = new IvStockPostingRepository();
        var approval = TestDisabledProcurementApprovalGuard.Instance;
        var posting = new IvInventoryPostingService(
            factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance,
            currentDate: new FixedCurrentDateService(DateTime.MaxValue.Date),
            approval: approval);
        return new IvGoodsReceiptService(
            factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(factory),
            new IvStockCommonRepository(factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            NullLogger<IvGoodsReceiptService>.Instance,
            approval: approval);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
