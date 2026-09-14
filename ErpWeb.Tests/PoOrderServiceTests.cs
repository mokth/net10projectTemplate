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
    public async Task PR_partial_then_remaining_then_fully_ordered()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);

        var first = await sut.SaveNewAsync(Request(Line("A100", qty: 6m, prNo: prNo, prLine: 1)));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.Equal(PoPrStatuses.PartiallyOrdered, await GetPrStatusAsync(prNo));

        var second = await sut.SaveNewAsync(Request(Line("A100", qty: 4m, prNo: prNo, prLine: 1)));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(PoPrStatuses.FullyOrdered, await GetPrStatusAsync(prNo));

        var third = await sut.SaveNewAsync(Request(Line("A100", qty: 1m, prNo: prNo, prLine: 1)));
        Assert.False(third.Succeeded);
        // FULLY_ORDERED is a status gate (not a quantity message).
        Assert.Contains("is not available for PO", third.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PoPrStatuses.FullyOrdered, await GetPrStatusAsync(prNo));
    }

    [Fact]
    public async Task PR_over_consumption_rejected_leaves_PR_unchanged()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);
        var first = await sut.SaveNewAsync(Request(Line("A100", qty: 6m, prNo: prNo, prLine: 1)));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var over = await sut.SaveNewAsync(Request(Line("A100", qty: 5m, prNo: prNo, prLine: 1)));
        Assert.False(over.Succeeded);
        Assert.Contains("remaining quantity is insufficient", over.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PoPrStatuses.PartiallyOrdered, await GetPrStatusAsync(prNo));
        Assert.Equal(6m, await LiveConsumedAsync(prNo, 1));
    }

    [Fact]
    public async Task PR_stale_partially_ordered_with_zero_remaining_rejected()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);
        var first = await sut.SaveNewAsync(Request(Line("A100", qty: 10m, prNo: prNo, prLine: 1)));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.Equal(PoPrStatuses.FullyOrdered, await GetPrStatusAsync(prNo));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == prNo);
            header.Status = PoPrStatuses.PartiallyOrdered;
            await db.SaveChangesAsync();
        }

        var again = await sut.SaveNewAsync(Request(Line("A100", qty: 1m, prNo: prNo, prLine: 1)));
        Assert.False(again.Succeeded);
        Assert.Contains("remaining quantity is insufficient", again.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10m, await LiveConsumedAsync(prNo, 1));
    }

    [Theory]
    [InlineData(PoPrStatuses.FullyOrdered)]
    [InlineData(PoPrStatuses.Cancelled)]
    [InlineData(PoPrStatuses.Open)]
    public async Task PR_rejected_status_not_available_for_PO(string status)
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: status);

        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m, prNo: prNo, prLine: 1)));
        Assert.False(save.Succeeded);
        Assert.Contains("is not available for PO", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(status, await GetPrStatusAsync(prNo));
        Assert.Equal(0m, await LiveConsumedAsync(prNo, 1));
    }

    [Fact]
    public async Task SearchPrForPo_includes_allowed_excludes_rejected()
    {
        var sut = CreateSut();
        var allowedNew = await SeedPrAsync(qty: 5m, status: PoPrStatuses.New, prNo: "PR-NEW-1");
        var allowedApproved = await SeedPrAsync(qty: 5m, status: PoPrStatuses.Approved, prNo: "PR-APR-1");
        var allowedPartial = await SeedPrAsync(qty: 5m, status: PoPrStatuses.PartiallyOrdered, prNo: "PR-PART-1");
        var rejectedFull = await SeedPrAsync(qty: 5m, status: PoPrStatuses.FullyOrdered, prNo: "PR-FULL-1");
        var rejectedCancel = await SeedPrAsync(qty: 5m, status: PoPrStatuses.Cancelled, prNo: "PR-CAN-1");
        var rejectedOpen = await SeedPrAsync(qty: 5m, status: PoPrStatuses.Open, prNo: "PR-OPEN-1");

        var result = await sut.SearchPrForPoAsync("SUP01", null);
        Assert.True(result.Succeeded, result.ErrorMessage);
        var nos = result.PrRows.Select(x => x.PrNo).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains(allowedNew, nos);
        Assert.Contains(allowedApproved, nos);
        Assert.Contains(allowedPartial, nos);
        Assert.DoesNotContain(rejectedFull, nos);
        Assert.DoesNotContain(rejectedCancel, nos);
        Assert.DoesNotContain(rejectedOpen, nos);
    }

    [Fact]
    public async Task GetPrRemainingLines_status_and_qty_semantics()
    {
        var sut = CreateSut();
        var missing = await sut.GetPrRemainingLinesAsync("PR-MISSING");
        Assert.False(missing.Succeeded);
        Assert.Equal(PoOrderErrorKind.NotFound, missing.ErrorKind);

        var cancelled = await SeedPrAsync(qty: 5m, status: PoPrStatuses.Cancelled, prNo: "PR-CAN-REM");
        var cancelledResult = await sut.GetPrRemainingLinesAsync(cancelled);
        Assert.False(cancelledResult.Succeeded);
        Assert.Contains("is not available for PO", cancelledResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var partial = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New, prNo: "PR-PART-REM");
        Assert.True((await sut.SaveNewAsync(Request(Line("A100", qty: 6m, prNo: partial, prLine: 1)))).Succeeded);
        var rem = await sut.GetPrRemainingLinesAsync(partial);
        Assert.True(rem.Succeeded, rem.ErrorMessage);
        var row = Assert.Single(rem.PrRemainingLines);
        Assert.Equal(4m, row.RemainingQty);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == partial);
            header.Status = PoPrStatuses.PartiallyOrdered;
            await db.SaveChangesAsync();
            // Consume the rest via a second PO then force stale status for empty remaining.
        }

        Assert.True((await sut.SaveNewAsync(Request(Line("A100", qty: 4m, prNo: partial, prLine: 1)))).Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == partial);
            header.Status = PoPrStatuses.PartiallyOrdered;
            await db.SaveChangesAsync();
        }

        var stale = await sut.GetPrRemainingLinesAsync(partial);
        Assert.True(stale.Succeeded, stale.ErrorMessage);
        Assert.Empty(stale.PrRemainingLines);
    }

    [Fact]
    public async Task Update_excludePo_excludes_current_but_counts_other_POs()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);

        var po1 = await sut.SaveNewAsync(Request(Line("A100", qty: 6m, prNo: prNo, prLine: 1)));
        Assert.True(po1.Succeeded, po1.ErrorMessage);
        var po2 = await sut.SaveNewAsync(Request(Line("A100", qty: 4m, prNo: prNo, prLine: 1)));
        Assert.True(po2.Succeeded, po2.ErrorMessage);

        // Editing PO1 to keep qty 6 must succeed (excludePo) even though other PO already took 4.
        var updateOk = await sut.UpdateAsync(
            po1.PoNo!,
            Request(
                Line("A100", qty: 6m, line: po1.Document!.Lines[0].Line, prNo: prNo, prLine: 1),
                rowVersion: (await sut.GetAsync(po1.PoNo!)).Document!.RowVersion));
        Assert.True(updateOk.Succeeded, updateOk.ErrorMessage);

        // Editing PO1 to 7 would need remaining 1 after excluding itself, but other PO holds 4 → insufficient.
        var updateFail = await sut.UpdateAsync(
            po1.PoNo!,
            Request(
                Line("A100", qty: 7m, line: po1.Document.Lines[0].Line, prNo: prNo, prLine: 1),
                rowVersion: updateOk.Document!.RowVersion));
        Assert.False(updateFail.Succeeded);
        Assert.Contains("remaining quantity is insufficient", updateFail.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6m, await LiveConsumedForPoAsync(po1.PoNo!, 1));
    }

    [Fact]
    public async Task Revise_excludePo_excludes_current_revision()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);
        var po1 = await sut.SaveNewAsync(Request(Line("A100", qty: 6m, prNo: prNo, prLine: 1)));
        Assert.True(po1.Succeeded, po1.ErrorMessage);
        Assert.True((await sut.SaveNewAsync(Request(Line("A100", qty: 4m, prNo: prNo, prLine: 1)))).Succeeded);

        var request = Request(
            Line("A100", qty: 6m, line: po1.Document!.Lines[0].Line, prNo: prNo, prLine: 1),
            rowVersion: (await sut.GetAsync(po1.PoNo!)).Document!.RowVersion);
        request.RevisionReason = "Keep same PR qty";
        var revise = await sut.ReviseAsync(po1.PoNo!, request);
        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(2, revise.Document!.PoRelNo);
    }

    [Fact]
    public async Task SaveNew_zero_qty_rejected_by_existing_validation()
    {
        var sut = CreateSut();
        var prNo = await SeedPrAsync(qty: 10m, status: PoPrStatuses.New);
        var result = await sut.SaveNewAsync(Request(Line("A100", qty: 0m, prNo: prNo, prLine: 1)));
        Assert.False(result.Succeeded);
        Assert.Equal(PoOrderErrorKind.Validation, result.ErrorKind);
        Assert.Equal(0m, await LiveConsumedAsync(prNo, 1));
        Assert.Equal(PoPrStatuses.New, await GetPrStatusAsync(prNo));
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

    private async Task<string> SeedPrAsync(decimal qty, string status, string? prNo = null)
    {
        var no = prNo ?? ("PR-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant());
        await using var db = await _factory.CreateDbContextAsync();
        db.PoPrs.Add(new PoPr
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            PrNo = no,
            CreateDt = FixedToday,
            Requester = "tester",
            Status = status,
            PrType = PoPrTypes.Purchasing,
            CreatedDate = FixedToday,
            CreatedBy = "user",
            RowVersion = Guid.NewGuid().ToByteArray(),
            Details =
            [
                new PoPrDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    PrNo = no,
                    Line = 1,
                    ICode = "A100",
                    IDesc = "Stock A",
                    Qty = qty,
                    PurchaseQty = qty,
                    StdQty = qty,
                    PackSz = 1m,
                    StdUom = "EA",
                    PurchaseUom = "EA",
                    Currency = "MYR",
                    UnitPrice = 25m,
                    Amount = qty * 25m,
                    VendorCd = "SUP01",
                    VendNm = "Alpha Supplier",
                    TaxGroup = "SR",
                    ToWarehouse = "MAIN",
                    Status = status
                }
            ]
        });
        await db.SaveChangesAsync();
        return no;
    }

    private async Task<string> GetPrStatusAsync(string prNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoPrs.AsNoTracking()
            .Where(x => x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.PrNo == prNo)
            .Select(x => x.Status)
            .SingleAsync();
    }

    private async Task<decimal> LiveConsumedAsync(string prNo, short prLine)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoOrderDetails.AsNoTracking()
            .Where(d => d.CompanyCode == "DEMO"
                && d.BranchCode == "HQ"
                && d.PrNo == prNo
                && d.PrLineNo == prLine
                && d.Order.Status != PoOrderStatuses.Cancelled)
            .SumAsync(d => (decimal?)d.PoPurQty) ?? 0m;
    }

    private async Task<decimal> LiveConsumedForPoAsync(string poNo, short poRelNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.PoOrderDetails.AsNoTracking()
            .Where(d => d.CompanyCode == "DEMO"
                && d.BranchCode == "HQ"
                && d.PoNo == poNo
                && d.PoRelNo == poRelNo)
            .SumAsync(d => (decimal?)d.PoPurQty) ?? 0m;
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
