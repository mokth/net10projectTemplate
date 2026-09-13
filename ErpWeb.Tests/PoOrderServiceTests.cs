using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Purchase;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Purchase;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Purchase;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

internal sealed class FakePoOrderDocumentNumberingService : IDocumentNumberingService
{
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
        if (requestMode == DocumentNumberRequestMode.Edit
            && !string.Equals(currentDocNo, "AUTO", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(currentDocNo))
        {
            return Task.FromResult(new DocumentNumberResult(currentDocNo.Trim(), null));
        }

        if (!string.Equals(module, "PO", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected module '{module}' for FakePoOrderDocumentNumberingService.");
        }

        var n = Interlocked.Increment(ref _poSeq);
        return Task.FromResult(new DocumentNumberResult($"PO{documentDate:yy}{documentDate:MM}-{n:D4}", "PO"));
    }
}

public class PoOrderServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _attachRoot;

    public PoOrderServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _attachRoot = Path.Combine(Path.GetTempPath(), "poorder-attach-" + Guid.NewGuid().ToString("N"));
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
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", CurrDesc = "Ringgit", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            TaxGrDesc = "Standard",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Stock A",
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
                IDesc = "Stock B",
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
        db.PoPurItems.Add(new PoPurItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = "IND1",
            IDesc = "Indirect item",
            PurUom = "EA",
            Vendor = "SUP01",
            VendName = "Alpha Supplier",
            Currency = "MYR",
            UnitPrice = 12.5m,
            Moq = 0m,
            Category = "GEN",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        db.PoSuppliers.AddRange(
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP01",
                SuppName = "Alpha Supplier",
                Currency = "MYR",
                GlCode = "AP001",
                Country = "MY",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            },
            new PoSupplier
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SuppCode = "SUP02",
                SuppName = "Beta Supplier",
                Currency = "MYR",
                GlCode = "AP002",
                Country = "MY",
                IsActive = true,
                RowVersion = Guid.NewGuid().ToByteArray()
            });
        db.PoVendorByItems.Add(new PoVendorByItem
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            Vendor = "SUP01",
            ICode = "A100",
            PurUom = "EA",
            UnitPrice = 18m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        if (Directory.Exists(_attachRoot))
        {
            try { Directory.Delete(_attachRoot, true); } catch { /* ignore */ }
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task SaveNew_creates_PO_with_NEW_status_and_numbered_PoNo()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(Line("A100", qty: 2m, price: 999m)));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(PoOrderStatuses.New, result.Document!.Status);
        Assert.Equal("PO2609-0001", result.PoNo);
        Assert.StartsWith("PO2609-", result.PoNo);
    }

    [Fact]
    public async Task Concurrent_SaveNew_allocates_distinct_PoNo()
    {
        var numbering = new FakePoOrderDocumentNumberingService();
        var sutA = CreateSut(numbering: numbering);
        var sutB = CreateSut(numbering: numbering);
        var results = await Task.WhenAll(
            sutA.SaveNewAsync(Request(Line("A100", qty: 1m))),
            sutB.SaveNewAsync(Request(Line("A100", qty: 1m))));

        Assert.All(results, r => Assert.True(r.Succeeded, r.ErrorMessage));
        Assert.NotEqual(results[0].PoNo, results[1].PoNo);
    }

    [Fact]
    public async Task Update_same_ICode_keeps_DB_UnitPrice_when_client_injects_999999()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 2m, price: 999999m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = save.Document!.Lines[0];
        Assert.Equal(25m, line.PoUnitPrice);

        var updated = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 5m, price: 999999m, line: line.Line),
                rowVersion: save.Document.RowVersion));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal(25m, updated.Document!.Lines[0].PoUnitPrice);
        Assert.Equal(5m, updated.Document.Lines[0].PoPurQty);
    }

    [Fact]
    public async Task Update_supplier_change_without_ClearLinesOnSupplierChange_rejected()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var updated = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 1m, line: save.Document!.Lines[0].Line),
                rowVersion: save.Document.RowVersion,
                vendCode: "SUP02"));
        Assert.False(updated.Succeeded);
        Assert.Equal(PoOrderErrorKind.BusinessRule, updated.ErrorKind);
        Assert.Contains("ClearLinesOnSupplierChange", updated.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancel_blocked_when_RecvQty_gt_0()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == save.PoNo);
            detail.RecvQty = 1m;
            detail.BalanceQty = 0m;
            await db.SaveChangesAsync();
        }

        var cancel = await sut.CancelAsync(new PoOrderKeyedRequest
        {
            PoNo = save.PoNo!,
            RowVersion = (await sut.GetAsync(save.PoNo!)).Document!.RowVersion
        });
        Assert.False(cancel.Succeeded);
        Assert.Equal(PoOrderErrorKind.BusinessRule, cancel.ErrorKind);
    }

    [Fact]
    public async Task ForceClose_then_Reopen()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 10m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var close = await sut.ForceCloseAsync(new PoOrderKeyedRequest
        {
            PoNo = save.PoNo!,
            RowVersion = save.Document!.RowVersion,
            CloseReason = "No longer needed"
        });
        Assert.True(close.Succeeded, close.ErrorMessage);
        Assert.Equal(PoOrderStatuses.Closed, close.Document!.Status);
        Assert.True(close.Document.IsForceClosed);
        Assert.Equal("No longer needed", close.Document.CloseReason);
        Assert.False(string.IsNullOrWhiteSpace(close.Document.ClosedBy));
        Assert.NotNull(close.Document.ClosedOn);

        var reopen = await sut.ReopenAsync(new PoOrderKeyedRequest
        {
            PoNo = save.PoNo!,
            RowVersion = close.Document.RowVersion
        });
        Assert.True(reopen.Succeeded, reopen.ErrorMessage);
        Assert.Equal(PoOrderStatuses.New, reopen.Document!.Status);
        Assert.False(reopen.Document.IsForceClosed);
        Assert.Null(reopen.Document.CloseReason);
        Assert.Null(reopen.Document.ClosedBy);
        Assert.Null(reopen.Document.ClosedOn);
    }

    [Fact]
    public async Task ForceClose_requires_reason()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 10m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var close = await sut.ForceCloseAsync(new PoOrderKeyedRequest
        {
            PoNo = save.PoNo!,
            RowVersion = save.Document!.RowVersion
        });
        Assert.False(close.Succeeded);
        Assert.Equal(PoOrderErrorKind.Validation, close.ErrorKind);
        Assert.Contains("Close reason", close.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copy_clears_PR_recv_and_return()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(
            Line("A100", qty: 2m),
            checkBy: "chk1",
            authorisedBy: "auth1"));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == save.PoNo);
            detail.PrNo = "PR-1";
            detail.PrLineNo = 1;
            detail.RecvQty = 1m;
            detail.ReturnQty = 0.5m;
            detail.BalanceQty = 0.5m;
            await db.SaveChangesAsync();
        }

        var copy = await sut.CopyAsync(save.PoNo!);
        Assert.True(copy.Succeeded, copy.ErrorMessage);
        var line = Assert.Single(copy.Document!.Lines);
        Assert.Null(line.PrNo);
        Assert.Null(line.PrLineNo);
        Assert.Equal(0m, line.RecvQty);
        Assert.Equal(0m, line.ReturnQty);
        Assert.Equal(2m, line.BalanceQty);
        Assert.Equal("AUTO", copy.Document.PoNo);
        Assert.Equal(PoOrderStatuses.New, copy.Document.Status);
    }

    [Fact]
    public async Task SaveNew_rejects_mixed_direct_and_indirect_lines()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m),
            Line("IND1", qty: 1m)));
        Assert.False(result.Succeeded);
        Assert.Equal(PoOrderErrorKind.Validation, result.ErrorKind);
        Assert.Contains(result.ValidationErrors.Values, v => v.Contains("cannot be mixed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Update_received_line_cannot_decrease_PoPurQty()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 10m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == save.PoNo);
            detail.RecvQty = 5m;
            detail.BalanceQty = 5m;
            await db.SaveChangesAsync();
        }

        var current = await sut.GetAsync(save.PoNo!);
        Assert.True(current.Succeeded, current.ErrorMessage);
        var line = current.Document!.Lines[0];

        var updated = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 4m, line: line.Line),
                rowVersion: current.Document.RowVersion));
        Assert.False(updated.Succeeded);
        Assert.Equal(PoOrderErrorKind.Validation, updated.ErrorKind);
        Assert.Contains(updated.ValidationErrors.Values, v => v.Contains("cannot be less than net received", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OPEN_status_edit_save_persists_NEW()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoOrders.SingleAsync(x => x.PoNo == save.PoNo);
            header.Status = PoOrderStatuses.Open;
            await db.SaveChangesAsync();
        }

        var current = await sut.GetAsync(save.PoNo!);
        Assert.Equal(PoOrderStatuses.Open, current.Document!.Status);

        var updated = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 2m, line: current.Document.Lines[0].Line),
                rowVersion: current.Document.RowVersion));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal(PoOrderStatuses.New, updated.Document!.Status);
        Assert.Equal(2m, updated.Document.Lines[0].PoPurQty);
    }

    [Fact]
    public async Task Update_RowVersion_mismatch_returns_Concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        var first = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 2m, line: save.Document.Lines[0].Line),
                rowVersion: save.Document.RowVersion));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.UpdateAsync(
            save.PoNo!,
            Request(
                Line("A100", qty: 3m, line: save.Document.Lines[0].Line),
                rowVersion: stale));
        Assert.False(second.Succeeded);
        Assert.Equal(PoOrderErrorKind.Concurrency, second.ErrorKind);
    }

    private static PoOrderSaveRequest Request(
        PoOrderLineDto line,
        byte[]? rowVersion = null,
        string? vendCode = null,
        string? checkBy = null,
        string? authorisedBy = null) =>
        Request([line], rowVersion, vendCode, checkBy, authorisedBy);

    private static PoOrderSaveRequest Request(
        PoOrderLineDto line1,
        PoOrderLineDto line2,
        byte[]? rowVersion = null,
        string? vendCode = null,
        string? checkBy = null,
        string? authorisedBy = null) =>
        Request([line1, line2], rowVersion, vendCode, checkBy, authorisedBy);

    private static PoOrderSaveRequest Request(
        IReadOnlyList<PoOrderLineDto> lines,
        byte[]? rowVersion = null,
        string? vendCode = null,
        string? checkBy = null,
        string? authorisedBy = null) =>
        new()
        {
            PoDate = FixedToday,
            VendCode = vendCode ?? "SUP01",
            VendName = "Alpha Supplier",
            CurCode = "MYR",
            CheckBy = checkBy,
            AuthorisedBy = authorisedBy,
            RowVersion = rowVersion,
            Lines = lines
        };

    private static PoOrderLineDto Line(
        string iCode,
        decimal qty,
        decimal price = 0m,
        string currency = "MYR",
        short line = 0,
        string? prNo = null,
        short? prLine = null) =>
        new()
        {
            Line = line,
            ICode = iCode,
            PoPurQty = qty,
            PoQty = qty,
            PurchaseUom = "EA",
            StdUom = "EA",
            PackSz = 1m,
            CurCode = currency,
            PoUnitPrice = price,
            TaxGroup = "SR",
            ToWarehouse = "MAIN",
            PrNo = prNo,
            PrLineNo = prLine
        };

    private PoOrderService CreateSut(
        Mock<IAccessRightService>? access = null,
        FakePoOrderDocumentNumberingService? numbering = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: "user");
        var attach = new PoOrderAttachmentService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance);

        return new PoOrderService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            numbering ?? new FakePoOrderDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attach,
            NullLogger<PoOrderService>.Instance);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseOrder,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
