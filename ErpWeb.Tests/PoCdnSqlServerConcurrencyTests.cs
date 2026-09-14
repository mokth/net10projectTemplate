using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

// PoCdnStatuses is duplicated in ErpWeb.Core.Purchase and ErpWeb.Model.Repositories.Purchase, so an
// alias is required for the name to resolve. (The duplication is worth collapsing in the product code.)
using PoCdnStatuses = ErpWeb.Core.Purchase.PoCdnStatuses;

namespace ErpWeb.Tests;

/// <summary>
/// Captures logged exceptions so a failing test can explain itself.
///
/// <c>PoCdnService</c> deliberately returns a generic "Unable to save the document." to callers and
/// logs the real cause, so pairing it with <c>NullLogger</c> makes a genuine failure undiagnosable.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<Exception> Exceptions { get; } = [];
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is not null)
        {
            Exceptions.Add(exception);
        }

        Messages.Add(formatter(state, exception));
    }

    /// <summary>Flattens everything captured for inclusion in an assertion message.</summary>
    public string Describe() => Exceptions.Count == 0
        ? "(nothing logged)"
        : string.Join(" ;; ", Exceptions.Select(e => e.ToString()));

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

/// <summary>
/// SQL Server REQUIRED concurrency tests for the two PoCdn race controls:
/// C41 (invoice-line reservation) and C10 (physical vendor-return ceiling).
///
/// Skipped unless ConnectionStrings:DefaultConnection points at a reachable SQL Server.
/// SQLite cannot validate UPDLOCK/HOLDLOCK or key-range behaviour, so this file is the only
/// place these two controls are actually exercised — the SQLite service tests prove the
/// arithmetic, not the serialization.
///
/// Every row is seeded under a unique generated company code so a live database is never
/// disturbed, and the seeded rows are removed again on disposal.
/// </summary>
public class PoCdnSqlServerConcurrencyTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    // CompanyCode is nvarchar(5) on the inventory masters (10 on purchase/sales), so a longer code
    // is silently truncated on insert and blows up the seed. Five characters is the common limit.
    private readonly string _company = "T" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

    private string? _connString;
    private IDbContextFactory<AppDbContext>? _factory;
    private FakePoCdnDocumentNumberingService _numbering = new();
    private readonly CapturingLogger<PoCdnService> _cdnLog = new();
    private readonly CapturingLogger<IvInventoryPostingService> _postLog = new();

    /// <summary>
    /// Dedicated connection string for these tests, <c>ConnectionStrings:SqlServerTestConnection</c>.
    ///
    /// Deliberately NOT <c>DefaultConnection</c>: that points at the live ERP database, and these
    /// tests write and delete rows. Requiring an explicit key means a stray `dotnet test` can never
    /// run a destructive concurrency race against production.
    /// </summary>
    internal const string TestConnectionKey = "SqlServerTestConnection";

    private static string? GetSqlServerConnectionString()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("../ErpWeb/appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var cs = config.GetConnectionString(TestConnectionKey);
        return string.IsNullOrWhiteSpace(cs) ? null : cs;
    }

    /// <summary>
    /// Second safety rail: the target database name must look like a scratch database. Even with an
    /// explicit key, a copy-pasted production string would otherwise seed rows into live data.
    /// </summary>
    internal static bool IsScratchDatabase(string connectionString)
    {
        var name = DatabaseNameOf(connectionString);
        return name is not null && name.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    private static string? DatabaseNameOf(string connectionString)
    {
        foreach (var raw in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Split('=', 2);
            if (part.Length != 2) continue;
            var key = part[0].Trim();
            if (key.Equals("Database", StringComparison.OrdinalIgnoreCase)
                || key.Equals("Initial Catalog", StringComparison.OrdinalIgnoreCase))
            {
                return part[1].Trim();
            }
        }

        return null;
    }

    public static bool IsSqlServerAvailable()
    {
        var cs = GetSqlServerConnectionString();
        if (cs is null || !IsScratchDatabase(cs))
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

    public async Task InitializeAsync()
    {
        _connString = GetSqlServerConnectionString();
        if (_connString is null || !IsScratchDatabase(_connString) || !IsSqlServerAvailable())
        {
            return;   // skip: no usable scratch SQL Server
        }

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(_connString).Options;
        _factory = new TestDbContextFactory(options);

        // The repo's scripts/ folder holds additive migrations over an existing schema; there is no
        // single "create everything" script. These tests only need the tables to exist with the
        // model's current shape, so build them from the EF model the same way the SQLite suite does.
        await using var db = await _factory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        if (_factory is null)
        {
            return;
        }

        // Remove only what this test created, children before parents so FKs hold.
        // A stock-returning save also creates an owned VR batch, so those rows are cleaned too.
        await using var db = await _factory.CreateDbContextAsync();
        await DeleteByCompanyAsync<PoCdnDetail>(db);
        await DeleteByCompanyAsync<PoCdn>(db);
        await DeleteByCompanyAsync<PoInvoiceDetail>(db);
        await DeleteByCompanyAsync<PoInvoice>(db);
        await DeleteByCompanyAsync<PoOrderDetail>(db);
        await DeleteByCompanyAsync<PoOrder>(db);
        await DeleteByCompanyAsync<IvTrxBatchDetail>(db);
        await DeleteByCompanyAsync<IvTrxBatch>(db);
        // Posting writes IvTrxHistory rows that carry FK_IvTrxHistory_IvBalLoc_FromBalLocId, and
        // IvBalLoc holds FK_IvBalLoc_IvStockMaster_CompanyCode_ICode — so history goes first, then
        // the pile, then the stock master.
        await DeleteByCompanyAsync<IvTrxHistory>(db);
        await DeleteByCompanyAsync<IvBalLoc>(db);
        await DeleteByCompanyAsync<IvStockMaster>(db);
        await DeleteByCompanyAsync<PoSupplier>(db);
        await DeleteByCompanyAsync<SaTaxGroup>(db);
        await DeleteByCompanyAsync<SaPaymentTerm>(db);
        await DeleteByCompanyAsync<IvLocation>(db);
        await DeleteByCompanyAsync<IvWarehouse>(db);
        await DeleteByCompanyAsync<IvStatus>(db);
    }

    /// <summary>
    /// Resolves the table name from the EF model instead of hard-coding it. Several entities map to
    /// legacy table names (<c>PoOrder</c> -&gt; <c>POOrder</c>, <c>PoOrderDetail</c> -&gt; <c>PODetail</c>),
    /// and a hand-written DELETE against the CLR name fails at runtime.
    /// </summary>
    private async Task DeleteByCompanyAsync<TEntity>(AppDbContext db) where TEntity : class
    {
        var entityType = db.Model.FindEntityType(typeof(TEntity));
        if (entityType is null)
        {
            return;   // not part of the model — nothing to clean up
        }

        var table = entityType.GetTableName();
        if (string.IsNullOrWhiteSpace(table))
        {
            return;
        }

        var schema = entityType.GetSchema() ?? "dbo";
        // Identifiers come from the EF model, not user input, and the company code is parameterised.
        // Built into a local so the interpolated string is not passed inline (EF1002).
        var sql = $"DELETE FROM [{schema}].[{table}] WHERE [CompanyCode] = {{0}}";
        await db.Database.ExecuteSqlRawAsync(sql, _company);
    }

    // ─────────────────────────── Seed ───────────────────────────

    private async Task<int> SeedAsync(string vendorCode, string itemCode, decimal invoiceQty,
        decimal orderReceived, decimal orderReturned)
    {
        await using var db = await _factory!.CreateDbContextAsync();

        if (!await db.IvWarehouses.AnyAsync(x => x.CompanyCode == _company))
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = _company, BranchCode = "HQ", WarehouseCode = "MAIN", IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = _company, BranchCode = "HQ", WarehouseCode = "MAIN",
                LocCode = "BIN1", IsActive = true
            });
            db.IvStatuses.Add(new IvStatus { CompanyCode = _company, IStatus = "ACTIVE", IsActive = true });
            db.SaTaxGroups.Add(new SaTaxGroup
            {
                CompanyCode = _company, TaxGrCode = "ZR", TaxGrDesc = "Zero", Percentage = 0m,
                TaxGlCode = "GLTAXZ"
            });
            db.SaPaymentTerms.Add(new SaPaymentTerm
            {
                CompanyCode = _company, PayCode = "NET30", PayDesc = "Net 30", Days = 30, IsActive = true
            });
        }

        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = _company, SuppCode = vendorCode, SuppName = vendorCode,
            Currency = "MYR", PayCode = "NET30", TaxGrCode = "ZR", IsActive = true, Suspend = false
        });

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = _company, ICode = itemCode, IDesc = itemCode,
            IType = "STOCK", StdUom = "EA", PurUom = "EA",
            StockControl = true, LotControl = false, IsActive = true,
            PurStdPackSize = 1m, PurchasePrice = 100m,
            PurchaseGlCode = "GLITEM", PurchaseTaxGroup = "ZR"
        });

        var docNo = "PINV" + itemCode;
        var amount = invoiceQty * 100m;
        var invoice = new PoInvoice
        {
            CompanyCode = _company,
            BranchCode = "HQ",
            DocNo = docNo,
            DocDate = new DateTime(2026, 8, 1),
            Status = "POSTED",
            Type = "INV",
            VendorCode = vendorCode,
            VendorName = vendorCode,
            Currency = "MYR",
            CurrRate = 1m,
            GrossAmnt = amount,
            Taxes = 0m,
            TotAmnt = amount,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        };
        invoice.Details.Add(new PoInvoiceDetail
        {
            CompanyCode = _company,
            BranchCode = "HQ",
            DocNo = docNo,
            Line = 1,
            ICode = itemCode,
            IDesc = itemCode,
            Qty = invoiceQty,
            UnitPrice = 100m,
            StdQty = invoiceQty,
            StdCustPsize = 1m,
            StdUom = "EA",
            Amount = amount,
            NetAmount = amount,
            TaxAmt = 0m,
            TaxGroup = "ZR",
            IsInclusive = false,
            ItemGlCode = "GLITEM",
            PoNo = "PO" + itemCode,
            PoRelNo = 1,
            PoLineNo = 1
        });
        db.PoInvoices.Add(invoice);

        var order = new PoOrder
        {
            CompanyCode = _company,
            BranchCode = "HQ",
            PoNo = "PO" + itemCode,
            PoRelNo = 1,
            Status = "RECEIVED",
            VendCode = vendorCode,
            PoDate = new DateTime(2026, 7, 1)
        };
        order.Details.Add(new PoOrderDetail
        {
            CompanyCode = _company,
            BranchCode = "HQ",
            PoNo = "PO" + itemCode,
            PoRelNo = 1,
            Line = 1,
            ICode = itemCode,
            PoPurQty = orderReceived,
            PoQty = orderReceived,
            PackSz = 1m,
            StdUom = "EA",
            RecvQty = orderReceived,
            ReturnQty = orderReturned
        });
        db.PoOrders.Add(order);

        await db.SaveChangesAsync();

        // A stock-return line must point at a real pile (C43), and FK_IvTrxBatchDetail_IvBalLoc
        // enforces it. Seed one and hand back its identity-assigned id rather than guessing.
        // The item is not lot-controlled, so the lot is empty — and it must match exactly what the
        // line carries, because posting re-reads the pile under lock and rejects any identity drift.
        var balLoc = new IvBalLoc
        {
            CompanyCode = _company,
            BranchCode = "HQ",
            ICode = itemCode,
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = string.Empty,
            IStatus = "ACTIVE",
            StdQty = orderReceived - orderReturned,
            StdUom = "EA",
            TransDate = new DateTime(2026, 8, 1),
            Cost = 100m
        };
        db.IvBalLocs.Add(balLoc);
        await db.SaveChangesAsync();
        return balLoc.Id;
    }

    private PoCdnService CreateSut()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: _company, branch: "HQ", location: "MAIN");
        var postingRepo = new IvStockPostingRepository();
        var common = new IvStockCommonRepository(_factory!);

        return new PoCdnService(
            _factory!,
            tenant,
            access.Object,
            _numbering,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new PoCdnRepository(),
            new PoInvoiceRepository(),
            postingRepo,
            new IvStockMasterRepository(_factory!),
            common,
            new IvInventoryPostingService(
                _factory!, tenant, access.Object, new IvStockPostingRepository(),
                common, new PoOrderRepository(), _postLog),
            _cdnLog);
    }

    private static PoCdnSaveRequest Request(
        string vendorCode,
        string itemCode,
        string docNo,
        decimal qty,
        string supplierDocNo,
        string reasonCode,
        bool returnStock,
        int balLocId = 0) => new()
    {
        Type = "CN",
        DocDate = FixedToday,
        VendorCode = vendorCode,
        InvNo = docNo,
        ReturnStock = returnStock,
        Currency = "MYR",
        ReasonCode = reasonCode,
        SupplierDocNo = supplierDocNo,
        SupplierDocDate = new DateTime(2026, 8, 20),
        TaxGrCode = "ZR",
        Lines =
        [
            new PoCdnLineRequest
            {
                InvLineNo = 1,
                IsStockReturn = returnStock,
                ICode = itemCode,
                Qty = qty,
                UnitPrice = 100m,
                IsInclusive = false,
                TaxGroup = "ZR",
                FromBalLocId = returnStock ? balLocId : null,
                PoNo = returnStock ? "PO" + itemCode : null,
                PoRelNo = returnStock ? (short)1 : null,
                PoLineNo = returnStock ? (short)1 : null,
                FrWarehouse = returnStock ? "MAIN" : null,
                LocCode = returnStock ? "BIN1" : null,
                IStatus = returnStock ? "ACTIVE" : null,
                LotNo = returnStock ? string.Empty : null,
                StdCustPSize = 1m
            }
        ]
    };

    // ─────────────────────────── C41: invoice-line reservation ───────────────────────────

    /// <summary>
    /// C41. Invoice line holds exactly 10 units. Two credit notes ask for all 10 at the same
    /// instant. Serialization happens on the invoice row (PoCdnLockOrder: invoice before PoCdn),
    /// so the loser must observe the winner's reservation and be refused. Without the lock both
    /// would read "remaining = 10" and both would save, silently over-crediting the invoice.
    /// </summary>
    [Fact]
    public async Task C41_two_concurrent_credit_notes_cannot_both_consume_the_same_invoice_line()
    {
        if (_factory is null)
        {
            return; // skip without SQL Server
        }

        var vendor = "V1";
        var item = "C41ITEM";
        var docNo = "PINV" + item;
        await SeedAsync(vendor, item, invoiceQty: 10m, orderReceived: 10m, orderReturned: 0m);

        var sut = CreateSut();
        var a = Request(vendor, item, docNo, 10m, "SUP-A", "RETURN", returnStock: false);
        var b = Request(vendor, item, docNo, 10m, "SUP-B", "RETURN", returnStock: false);

        var results = await Task.WhenAll(
            sut.SaveNewAsync(a),
            sut.SaveNewAsync(b));

        var failures = string.Join(" | ",
            results.Where(r => !r.Succeeded).Select(r => r.ErrorMessage ?? "(no message)"));
        Assert.True(
            results.Count(r => r.Succeeded) == 1,
            $"expected exactly one credit note to be accepted; failures: {failures}");
    }

    /// <summary>
    /// C41 follow-up: the loser is refused, not merely delayed. After the race exactly the
    /// winner's quantity is consumed, so a third request for the remainder must succeed.
    /// </summary>
    [Fact]
    public async Task C41_the_surviving_quantity_is_exactly_what_the_winner_took()
    {
        if (_factory is null)
        {
            return; // skip without SQL Server
        }

        var vendor = "V2";
        var item = "C41REM";
        var docNo = "PINV" + item;
        await SeedAsync(vendor, item, invoiceQty: 10m, orderReceived: 10m, orderReturned: 0m);

        var sut = CreateSut();
        await Task.WhenAll(
            sut.SaveNewAsync(Request(vendor, item, docNo, 6m, "SUP-A", "RETURN", returnStock: false)),
            sut.SaveNewAsync(Request(vendor, item, docNo, 6m, "SUP-B", "RETURN", returnStock: false)));

        // Exactly one 6-unit note survived, so 4 remain and 5 must be refused.
        var tooMany = await sut.SaveNewAsync(
            Request(vendor, item, docNo, 5m, "SUP-C", "RETURN", returnStock: false));
        Assert.False(tooMany.Succeeded);

        var exact = await sut.SaveNewAsync(
            Request(vendor, item, docNo, 4m, "SUP-D", "RETURN", returnStock: false));
        Assert.True(exact.Succeeded, exact.ErrorMessage);
    }

    // ─────────────────────────── C10: physical vendor-return ceiling ───────────────────────────

    /// <summary>
    /// C10. PO line received 100 and already returned 80, so only 20 physically remain.
    ///
    /// The plan is explicit that the save-time ceiling is a <b>soft warning only</b> — the
    /// authoritative revalidation happens inside the posting transaction under the PO lock
    /// (<c>IvInventoryPostingService.ApplyVendorReturnPoQtyAsync</c>). So both drafts are expected
    /// to save, and the race is decided at post: exactly one succeeds, the other fails on the
    /// under-lock revalidation.
    ///
    /// Testing this at save time would have asserted the opposite of the specified design.
    /// </summary>
    [Fact]
    public async Task C10_two_concurrent_stock_return_posts_cannot_exceed_the_received_quantity()
    {
        if (_factory is null)
        {
            return; // skip without SQL Server
        }

        var vendor = "V3";
        var item = "C10ITEM";
        var docNo = "PINV" + item;
        var balLocId = await SeedAsync(vendor, item, invoiceQty: 100m, orderReceived: 100m, orderReturned: 80m);

        var sut = CreateSut();

        // Save is deliberately not authoritative, so both drafts must save.
        var a = await sut.SaveNewAsync(
            Request(vendor, item, docNo, 20m, "SUP-A", "RETURN", returnStock: true, balLocId));
        var b = await sut.SaveNewAsync(
            Request(vendor, item, docNo, 20m, "SUP-B", "RETURN", returnStock: true, balLocId));

        Assert.True(a.Succeeded, $"draft A was expected to save: {a.ErrorMessage} ;; {_cdnLog.Describe()}");
        Assert.True(b.Succeeded, $"draft B was expected to save: {b.ErrorMessage} ;; {_cdnLog.Describe()}");

        var draftA = await LoadAsync(a.DocNo!);
        var draftB = await LoadAsync(b.DocNo!);
        Assert.NotNull(draftA);
        Assert.NotNull(draftB);

        // Only the post revalidates the physical ceiling, under the PO lock.
        var posts = await Task.WhenAll(
            sut.PostAsync([new PoCdnKeyedRequest { DocNo = draftA!.DocNo, RowVersion = draftA.RowVersion }]),
            sut.PostAsync([new PoCdnKeyedRequest { DocNo = draftB!.DocNo, RowVersion = draftB.RowVersion }]));

        var outcomes = string.Join(" | ", posts.SelectMany(p => p.Posting)
            .Select(x => $"{x.DocNo}: {x.Outcome}"));
        Assert.True(
            posts.Count(p => p.Succeeded) == 1,
            $"exactly one post was expected to succeed; outcomes: {outcomes} ;; logged: {_postLog.Describe()}");

        // The loser must be left as a draft, not half-posted.
        var reloadedA = await LoadAsync(draftA.DocNo);
        var reloadedB = await LoadAsync(draftB.DocNo);
        var postedCount = new[] { reloadedA, reloadedB }
            .Count(x => string.Equals(x?.Status, PoCdnStatuses.Posted, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, postedCount);
    }

    /// <summary>
    /// C2. The filtered unique index on (CompanyCode, BranchCode, VendorCode, Type, SupplierDocNo)
    /// is what actually stops two drafts claiming the same supplier document number. It only exists
    /// on SQL Server — <c>AppDbContext.OnModelCreating</c> strips filtered indexes for SQLite — so
    /// this control is unverifiable in the SQLite suite.
    /// </summary>
    [Fact]
    public async Task C2_two_concurrent_saves_of_the_same_supplier_document_number_cannot_both_win()
    {
        if (_factory is null)
        {
            return; // skip without SQL Server
        }

        var vendor = "V4";
        var item = "C2ITEM";
        var docNo = "PINV" + item;
        await SeedAsync(vendor, item, invoiceQty: 100m, orderReceived: 100m, orderReturned: 0m);

        var sut = CreateSut();

        // Same SupplierDocNo, different notes; the index must reject the loser.
        var results = await Task.WhenAll(
            sut.SaveNewAsync(Request(vendor, item, docNo, 1m, "SAME-SUPPLIER-DOC", "PRICE_ADJUSTMENT", returnStock: false)),
            sut.SaveNewAsync(Request(vendor, item, docNo, 1m, "SAME-SUPPLIER-DOC", "PRICE_ADJUSTMENT", returnStock: false)));

        var failures = string.Join(" | ",
            results.Where(r => !r.Succeeded).Select(r => r.ErrorMessage ?? "(no message)"));
        Assert.True(
            results.Count(r => r.Succeeded) == 1,
            $"expected exactly one save to claim the supplier document number; failures: {failures} ;; logged: {_cdnLog.Describe()}");
    }

    private async Task<PoCdn?> LoadAsync(string docNo)
    {
        await using var db = await _factory!.CreateDbContextAsync();
        return await db.PoCdns
            .Include(x => x.Details)
            .FirstOrDefaultAsync(x => x.CompanyCode == _company && x.DocNo == docNo);
    }
}
