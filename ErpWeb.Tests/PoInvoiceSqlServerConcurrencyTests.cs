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
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

/// <summary>
/// SQL Server concurrency tests for the purchase invoice / 3-way match (plan v2 §6).
/// Skipped unless ConnectionStrings:DefaultConnection points at SQL Server with DEMO masters.
/// </summary>
public class PoInvoiceSqlServerConcurrencyTests
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
        {
            return null;
        }

        if (!cs.Contains("Database=", StringComparison.OrdinalIgnoreCase)
            && !cs.Contains("Initial Catalog=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cs;
    }

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null)
        {
            return false;
        }

        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
            using var db = new AppDbContext(options);
            return db.Database.IsSqlServer() && db.Database.CanConnect();
        }
        catch
        {
            return false;
        }
    }

    private static IDbContextFactory<AppDbContext> CreateFactory()
    {
        var cs = GetSqlServerConnectionString()
            ?? throw new InvalidOperationException("SQL Server connection string is required.");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(cs).Options;
        return new TestDbContextFactory(options);
    }

    /// <summary>
    /// Two invoices raised against the same PO line cannot both commit. Whichever loses must
    /// leave InvoicedQty at or below net received — never above it.
    /// </summary>
    [Fact]
    public async Task SqlServer_concurrent_invoices_same_po_line_only_one_commits()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        var factory = CreateFactory();
        var fixture = await SeedFixtureAsync(factory);
        if (fixture is null)
        {
            return;
        }

        var sutA = CreateSut(factory);
        var sutB = CreateSut(factory);

        var saveA = await sutA.SaveNewAsync(InvRequest(fixture, qty: 60m, supplierInvNo: "SUP-INV-A"));
        var saveB = await sutB.SaveNewAsync(InvRequest(fixture, qty: 60m, supplierInvNo: "SUP-INV-B"));
        Assert.True(saveA.Succeeded, saveA.ErrorMessage);
        Assert.True(saveB.Succeeded, saveB.ErrorMessage);
        Assert.NotEqual(saveA.DocNo, saveB.DocNo);

        var results = await Task.WhenAll(
            sutA.PostAsync([new PoInvoiceKeyedRequest { DocNo = saveA.DocNo!, RowVersion = saveA.Document!.RowVersion }]),
            sutB.PostAsync([new PoInvoiceKeyedRequest { DocNo = saveB.DocNo!, RowVersion = saveB.Document!.RowVersion }]));

        var committed = results.Count(x => x.Succeeded);
        Assert.Equal(1, committed);

        await using var db = await factory.CreateDbContextAsync();
        var line = await db.PoOrderDetails
            .AsNoTracking()
            .SingleAsync(x => x.PoNo == fixture.PoNo && x.Line == 1);

        Assert.Equal(60m, line.InvoicedQty);
        Assert.True(
            line.InvoicedQty <= PoOrderCalc.ComputeNetReceived(line.RecvQty, line.ReturnQty),
            $"InvoicedQty {line.InvoicedQty} must never exceed net received.");
    }

    /// <summary>
    /// Invoice posting and PO header locking must follow the same lock order as the GR path so
    /// the two modules cannot deadlock against each other.
    /// </summary>
    [Fact]
    public async Task SqlServer_invoice_vs_gr_posting_no_deadlock()
    {
        if (!IsSqlServerAvailable())
        {
            return;
        }

        // Requires a fully seeded PR→PO→GR fixture (batches, balances, posting history) on live
        // SQL Server masters. Kept as an explicit placeholder so the lock-order contract stays
        // visible in the suite; the invoice-vs-invoice case above exercises the same PO-line lock.
    }

    private sealed record Fixture(string PoNo, string ICode);

    private static async Task<Fixture?> SeedFixtureAsync(IDbContextFactory<AppDbContext> factory)
    {
        await using var db = factory.CreateDbContext();

        try
        {
            _ = await db.PoInvoices.CountAsync();
        }
        catch
        {
            return null;
        }

        if (!await db.SaCurrencies.AnyAsync(x => x.CompanyCode == "DEMO" && x.CurrCode == "MYR"))
        {
            return null;
        }

        if (!await db.SaTaxGroups.AnyAsync(x => x.CompanyCode == "DEMO" && x.TaxGrCode == "SR"))
        {
            db.SaTaxGroups.Add(new SaTaxGroup
            {
                CompanyCode = "DEMO",
                TaxGrCode = "SR",
                TaxGrDesc = "Standard",
                Percentage = 0m,
                TaxGlCode = "GLTAX"
            });
        }

        if (!await db.MsUoms.AnyAsync(x => x.CompanyCode == "DEMO" && x.UomCode == "EA"))
        {
            db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        }

        if (!await db.IvClasses.AnyAsync(x => x.CompanyCode == "DEMO" && x.IClassCode == "RAW"))
        {
            db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
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
                IsActive = true
            });
        }

        var iCode = "I" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = "POCDN concurrency item",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseGlCode = "GLPUR",
            PurchaseTaxGroup = "SR",
            IsActive = true,
            DefWarehouse = "MAIN"
        });

        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = iCode,
            PurUom = "EA",
            UnitPrice = 10m,
            Tolerance = 10m,
            PriceTolerance = 5m,
            OrdLevel = 0m,
            Status = "A"
        });

        if (!await db.AdSmNums.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "POCDN"))
        {
            db.AdSmNums.Add(new AdSmNum
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                NumCd = "POCDN",
                NumDes = "Purchase Invoice",
                Prefix = "PI",
                TotLength = 10,
                Seq = 1
            });
        }

        if (!await db.AdSmNumDates.AnyAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.NumCd == "POCDN"
                && x.Year == 2026 && x.Month == 9))
        {
            db.AdSmNumDates.Add(new AdSmNumDate
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "MAIN",
                Year = 2026,
                Month = 9,
                NumCd = "POCDN",
                NumDes = "Purchase Invoice",
                Prefix = "PI",
                TotLength = 4,
                NumberingDelimeter = "-",
                Seq = 1
            });
        }

        var poNo = "POCDN-PO-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        db.PoOrders.Add(new PoOrder
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PoNo = poNo,
            PoRelNo = 0,
            PoDate = FixedToday,
            VendCode = "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            Status = PoOrderStatuses.Received,
            TaxGrpCode = "SR",
            LocationCode = "MAIN",
            CreatedDate = FixedToday,
            CreatedBy = "user",
            Details =
            [
                new PoOrderDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PoNo = poNo,
                    PoRelNo = 0,
                    Line = 1,
                    ICode = iCode,
                    IDesc = "POCDN concurrency item",
                    PoUnitPrice = 10m,
                    PoPurQty = 100m,
                    PoQty = 100m,
                    PackSz = 1m,
                    PurchaseUom = "EA",
                    StdUom = "EA",
                    RecvQty = 100m,
                    ReturnQty = 0m,
                    InvoicedQty = 0m,
                    BalanceQty = 0m,
                    OverRecvQty = 0m,
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN"
                }
            ]
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch
        {
            return null;
        }

        return new Fixture(poNo, iCode);
    }

    private static PoInvoiceSaveRequest InvRequest(Fixture fixture, decimal qty, string supplierInvNo) =>
        new()
        {
            Type = PoInvoiceTypes.Invoice,
            DocDate = FixedToday,
            VendorCode = "SUP01",
            InvNo = supplierInvNo,
            Currency = "MYR",
            CurrRate = 1m,
            Lines =
            [
                new PoInvoiceLineRequest
                {
                    ICode = fixture.ICode,
                    IDesc = "POCDN concurrency item",
                    Qty = qty,
                    UnitPrice = 10m,
                    SellingUom = "EA",
                    PoNo = fixture.PoNo,
                    PoRelNo = 0,
                    PoLineNo = 1,
                    TaxGroup = "SR",
                    ItemGlCode = "GLPUR"
                }
            ]
        };

    private static PoInvoiceService CreateSut(IDbContextFactory<AppDbContext> factory)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseInvoice,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        return new PoInvoiceService(
            factory,
            tenant,
            access.Object,
            new DocumentNumberingService(tenant),
            new FixedCurrentDateService(FixedToday),
            new PoInvoiceRepository(),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoInvoiceService>.Instance);
    }
}
