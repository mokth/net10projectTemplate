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

internal sealed class FakePoPrDocumentNumberingService : IDocumentNumberingService
{
    private int _prSeq;

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

        if (!string.Equals(module, "PR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Unexpected module '{module}' for FakePoPrDocumentNumberingService.");
        }

        var n = Interlocked.Increment(ref _prSeq);
        return Task.FromResult(new DocumentNumberResult($"PR{documentDate:yy}{documentDate:MM}-{n:D4}", "PR"));
    }
}

public class PoPrServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly string _attachRoot;

    public PoPrServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _attachRoot = Path.Combine(Path.GetTempPath(), "popr-attach-" + Guid.NewGuid().ToString("N"));
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
        db.MsUoms.Add(new MsUom
        {
            CompanyCode = "DEMO",
            UomCode = "EA",
            IsActive = true
        });
        db.IvClasses.Add(new IvClass
        {
            CompanyCode = "DEMO",
            IClassCode = "RAW",
            IsActive = true
        });
        db.SaCurrencies.AddRange(
            new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", CurrDesc = "Ringgit", IsActive = true },
            new SaCurrency { CompanyCode = "DEMO", CurrCode = "USD", CurrDesc = "US Dollar", IsActive = true });
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
        db.PoSuppliers.Add(new PoSupplier
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
    public void PoPrCalc_exclusive_and_inclusive_tax()
    {
        var excl = PoPrCalc.ComputeTax(100m, 6m, isInclusive: false, taxDecimals: 2);
        Assert.Equal(100m, excl.NetAmount);
        Assert.Equal(6m, excl.TaxAmount);

        var incl = PoPrCalc.ComputeTax(106m, 6m, isInclusive: true, taxDecimals: 2);
        Assert.Equal(100m, incl.NetAmount);
        Assert.Equal(6m, incl.TaxAmount);

        var totals = PoPrCalc.SumTotals([(100m, 6m), (50m, 3m)]);
        Assert.Equal(150m, totals.Gross);
        Assert.Equal(9m, totals.Taxes);
        Assert.Equal(159m, totals.Total);
    }

    [Fact]
    public async Task SaveNew_creates_PR_with_NEW_status_and_numbered_PrNo()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(Line("A100", qty: 2m, price: 999m)));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(PoPrStatuses.New, result.Document!.Status);
        Assert.Equal("PR2609-0001", result.PrNo);
        Assert.StartsWith("PR2609-", result.PrNo);
    }

    [Fact]
    public async Task SaveNew_price_injection_uses_lookup_price_for_new_line()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(Line(
            "A100",
            qty: 2m,
            price: 999999m,
            amount: 999999m,
            taxAmount: 999999m)));
        Assert.True(result.Succeeded, result.ErrorMessage);
        var line = Assert.Single(result.Document!.Lines);
        Assert.Equal(25m, line.UnitPrice);
        Assert.Equal(50m, line.Amount);
        Assert.Equal(3m, line.TaxAmount);
    }

    [Fact]
    public async Task Update_same_ICode_keeps_DB_UnitPrice_when_qty_or_vendor_changes()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m, price: 999999m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = save.Document!.Lines[0];

        var qtyChange = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 5m, price: 999999m, line: line.Line, vendor: "SUP01"),
                rowVersion: save.Document.RowVersion));
        Assert.True(qtyChange.Succeeded, qtyChange.ErrorMessage);
        Assert.Equal(25m, qtyChange.Document!.Lines[0].UnitPrice);
        Assert.Equal(5m, qtyChange.Document.Lines[0].PurchaseQty);
        Assert.Equal(125m, qtyChange.Document.Lines[0].Amount);

        var vendorChange = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 5m, price: 1m, line: line.Line, vendor: null),
                rowVersion: qtyChange.Document.RowVersion));
        Assert.True(vendorChange.Succeeded, vendorChange.ErrorMessage);
        Assert.Equal(25m, vendorChange.Document!.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Update_ICode_change_on_unconsumed_line_re_resolves_price()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = save.Document!.Lines[0];
        Assert.Equal(25m, line.UnitPrice);

        var updated = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A200", qty: 1m, price: 999999m, line: line.Line),
                rowVersion: save.Document.RowVersion));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal("A200", updated.Document!.Lines[0].ICode);
        Assert.Equal(40m, updated.Document.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task SaveNew_rejects_mixed_currencies()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m, currency: "MYR"),
            Line("A200", qty: 1m, currency: "USD")));
        Assert.False(result.Succeeded);
        Assert.Equal(PoPrErrorKind.Validation, result.ErrorKind);
        Assert.Contains(result.ValidationErrors.Values, v => v.Contains("same currency", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Update_all_lines_currency_change_accepted()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m, currency: "MYR"),
            Line("A200", qty: 1m, currency: "MYR")));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var lines = save.Document!.Lines;
        var updated = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 1m, currency: "USD", line: lines[0].Line),
                Line("A200", qty: 1m, currency: "USD", line: lines[1].Line),
                rowVersion: save.Document.RowVersion));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.All(updated.Document!.Lines, l => Assert.Equal("USD", l.Currency));
        Assert.Equal("USD", updated.Document.Currency);
    }

    [Fact]
    public async Task SaveNew_rejects_inclusive_exclusive_mix()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m, inclusive: false),
            Line("A200", qty: 1m, inclusive: true)));
        Assert.False(result.Succeeded);
        Assert.Equal(PoPrErrorKind.Validation, result.ErrorKind);
        Assert.Contains(result.ValidationErrors.Values, v => v.Contains("Inclusive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Consumed_line_immutable_unconsumed_can_mutate_or_delete()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m),
            Line("A200", qty: 2m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var d1 = await db.PoPrDetails.SingleAsync(x =>
                x.CompanyCode == "DEMO" && x.BranchCode == "HQ" && x.PrNo == save.PrNo && x.Line == 1);
            d1.PoNo = "PO-1";
            await db.SaveChangesAsync();
        }

        var current = await sut.GetAsync(save.PrNo!);
        Assert.True(current.Succeeded);
        var consumed = current.Document!.Lines.Single(x => x.Line == 1);
        var free = current.Document.Lines.Single(x => x.Line == 2);

        var mutateConsumed = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 99m, price: 1m, line: consumed.Line),
                Line("A200", qty: 3m, line: free.Line),
                rowVersion: current.Document.RowVersion));
        Assert.True(mutateConsumed.Succeeded, mutateConsumed.ErrorMessage);
        var after = mutateConsumed.Document!.Lines.Single(x => x.Line == 1);
        Assert.Equal(1m, after.PurchaseQty);
        Assert.Equal(25m, after.UnitPrice);
        Assert.Equal("PO-1", after.PoNo);
        Assert.Equal(3m, mutateConsumed.Document.Lines.Single(x => x.Line == 2).PurchaseQty);

        var deleteConsumed = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A200", qty: 3m, line: free.Line),
                rowVersion: mutateConsumed.Document.RowVersion));
        Assert.False(deleteConsumed.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, deleteConsumed.ErrorKind);

        var deleteFree = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 1m, line: consumed.Line),
                rowVersion: mutateConsumed.Document.RowVersion));
        Assert.True(deleteFree.Succeeded, deleteFree.ErrorMessage);
        Assert.Single(deleteFree.Document!.Lines);
        Assert.Equal(1, deleteFree.Document.Lines[0].Line);
    }

    [Fact]
    public async Task Cancel_blocked_when_header_or_detail_PoNo_set()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.PoNo = "PO-H";
            await db.SaveChangesAsync();
        }

        var headerBlocked = await sut.CancelAsync(new PoPrCancelRequest
        {
            PrNo = save.PrNo!,
            RowVersion = (await sut.GetAsync(save.PrNo!)).Document!.RowVersion,
            ApprReason = "stop"
        });
        Assert.False(headerBlocked.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, headerBlocked.ErrorKind);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.PoNo = null;
            var detail = await db.PoPrDetails.SingleAsync(x => x.PrNo == save.PrNo);
            detail.PoNo = "PO-D";
            await db.SaveChangesAsync();
        }

        var detailBlocked = await sut.CancelAsync(new PoPrCancelRequest
        {
            PrNo = save.PrNo!,
            RowVersion = (await sut.GetAsync(save.PrNo!)).Document!.RowVersion,
            ApprReason = "stop"
        });
        Assert.False(detailBlocked.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, detailBlocked.ErrorKind);
    }

    [Fact]
    public async Task Cancel_succeeds_for_NEW_without_PoNo_keeps_lines_and_attachments()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.PoPrAttachFiles.Add(new PoPrAttachFile
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                DocId = save.PrNo!,
                DocName = "note.txt",
                DocKey = PoPrService.AttachDocKey,
                CreatedBy = "user",
                CreatedDate = FixedToday
            });
            await db.SaveChangesAsync();
        }

        var cancel = await sut.CancelAsync(new PoPrCancelRequest
        {
            PrNo = save.PrNo!,
            RowVersion = save.Document!.RowVersion,
            ApprReason = "Not needed"
        });
        Assert.True(cancel.Succeeded, cancel.ErrorMessage);
        Assert.Equal(PoPrStatuses.Cancelled, cancel.Document!.Status);
        Assert.Equal("Not needed", cancel.Document.ApprReason);
        Assert.Single(cancel.Document.Lines);

        await using var check = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await check.PoPrDetails.CountAsync(x => x.PrNo == save.PrNo));
        Assert.Equal(1, await check.PoPrAttachFiles.CountAsync(x => x.DocId == save.PrNo));
    }

    [Fact]
    public async Task Delete_blocked_when_PoNo_present()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.PoPrDetails.SingleAsync(x => x.PrNo == save.PrNo);
            detail.PoNo = "PO-1";
            await db.SaveChangesAsync();
        }

        var del = await sut.DeleteAsync(new PoPrKeyedRequest
        {
            PrNo = save.PrNo!,
            RowVersion = (await sut.GetAsync(save.PrNo!)).Document!.RowVersion
        });
        Assert.False(del.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, del.ErrorKind);
    }

    [Fact]
    public async Task OPEN_status_edit_save_persists_NEW()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.Status = PoPrStatuses.Open;
            await db.SaveChangesAsync();
        }

        var current = await sut.GetAsync(save.PrNo!);
        Assert.Equal(PoPrStatuses.Open, current.Document!.Status);

        var updated = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 2m, line: current.Document.Lines[0].Line),
                rowVersion: current.Document.RowVersion));
        Assert.True(updated.Succeeded, updated.ErrorMessage);
        Assert.Equal(PoPrStatuses.New, updated.Document!.Status);
        Assert.Equal(2m, updated.Document.Lines[0].PurchaseQty);
    }

    [Fact]
    public async Task OPEN_with_PoNo_cancel_and_delete_rejected()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.Status = PoPrStatuses.Open;
            header.PoNo = "PO-OPEN";
            await db.SaveChangesAsync();
        }

        var rv = (await sut.GetAsync(save.PrNo!)).Document!.RowVersion;
        var cancel = await sut.CancelAsync(new PoPrCancelRequest
        {
            PrNo = save.PrNo!,
            RowVersion = rv,
            ApprReason = "nope"
        });
        Assert.False(cancel.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, cancel.ErrorKind);

        var del = await sut.DeleteAsync(new PoPrKeyedRequest { PrNo = save.PrNo!, RowVersion = rv });
        Assert.False(del.Succeeded);
        Assert.Equal(PoPrErrorKind.BusinessRule, del.ErrorKind);
    }

    [Fact]
    public async Task Copy_CANCELLED_and_APPROVED_draft_AUTO_clears_workflow_retains_prices()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(
            Line("A100", qty: 2m),
            requester: "alice",
            checkedBy: "chk1",
            authorisedBy: "auth1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var savedPrice = save.Document!.Lines[0].UnitPrice;

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.Status = PoPrStatuses.Cancelled;
            header.ApprReason = "old";
            header.PoNo = "PO-X";
            var detail = await db.PoPrDetails.SingleAsync(x => x.PrNo == save.PrNo);
            detail.PoNo = "PO-L";
            detail.SoNo = "SO-1";
            detail.SoLine = 1;
            await db.SaveChangesAsync();
        }

        var copyCancelled = await sut.CopyAsync(save.PrNo!);
        Assert.True(copyCancelled.Succeeded, copyCancelled.ErrorMessage);
        Assert.Equal("AUTO", copyCancelled.Document!.PrNo);
        Assert.Equal(PoPrStatuses.New, copyCancelled.Document.Status);
        Assert.Null(copyCancelled.Document.CheckedBy);
        Assert.Null(copyCancelled.Document.AuthorisedBy);
        Assert.Null(copyCancelled.Document.ApprReason);
        Assert.Null(copyCancelled.Document.PoNo);
        Assert.Equal(savedPrice, copyCancelled.Document.Lines[0].UnitPrice);
        Assert.Null(copyCancelled.Document.Lines[0].PoNo);
        Assert.Null(copyCancelled.Document.Lines[0].SoNo);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.PoPrs.SingleAsync(x => x.PrNo == save.PrNo);
            header.Status = PoPrStatuses.Approved;
            header.PoNo = null;
            var detail = await db.PoPrDetails.SingleAsync(x => x.PrNo == save.PrNo);
            detail.PoNo = null;
            await db.SaveChangesAsync();
        }

        var copyApproved = await sut.CopyAsync(save.PrNo!);
        Assert.True(copyApproved.Succeeded, copyApproved.ErrorMessage);
        Assert.Equal("AUTO", copyApproved.Document!.PrNo);
        Assert.Equal(PoPrStatuses.New, copyApproved.Document.Status);
        Assert.Equal(savedPrice, copyApproved.Document.Lines[0].UnitPrice);
    }

    [Fact]
    public async Task Copy_then_SaveNew_gets_new_PrNo_and_NEW_status()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var copy = await sut.CopyAsync(save.PrNo!);
        Assert.True(copy.Succeeded, copy.ErrorMessage);

        var created = await sut.SaveNewAsync(new PoPrSaveRequest
        {
            CreateDt = FixedToday,
            Requester = copy.Document!.Requester,
            PrType = copy.Document.PrType,
            Remarks = copy.Document.Remarks,
            Lines = copy.Document.Lines
        });
        Assert.True(created.Succeeded, created.ErrorMessage);
        Assert.NotEqual(save.PrNo, created.PrNo);
        Assert.Equal(PoPrStatuses.New, created.Document!.Status);
        Assert.Equal("PR2609-0002", created.PrNo);
    }

    [Fact]
    public async Task Update_RowVersion_mismatch_returns_Concurrency()
    {
        var sut = CreateSut();
        var save = await sut.SaveNewAsync(Request(Line("A100", qty: 1m)));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var stale = (byte[])save.Document!.RowVersion.Clone();

        var first = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 2m, line: save.Document.Lines[0].Line),
                rowVersion: save.Document.RowVersion));
        Assert.True(first.Succeeded, first.ErrorMessage);

        var second = await sut.UpdateAsync(
            save.PrNo!,
            Request(
                Line("A100", qty: 3m, line: save.Document.Lines[0].Line),
                rowVersion: stale));
        Assert.False(second.Succeeded);
        Assert.Equal(PoPrErrorKind.Concurrency, second.ErrorKind);
    }

    [Fact]
    public async Task Empty_rows_stripped_half_filled_fails_validation()
    {
        var sut = CreateSut();
        var ok = await sut.SaveNewAsync(Request(
            Line("A100", qty: 1m),
            new PoPrLineDto()));
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.Single(ok.Document!.Lines);

        var half = await sut.SaveNewAsync(Request(
            new PoPrLineDto { Qty = 5m, PurchaseQty = 5m }));
        Assert.False(half.Succeeded);
        Assert.Equal(PoPrErrorKind.Validation, half.ErrorKind);
        Assert.True(half.ValidationErrors.ContainsKey("Lines[0].ICode")
            || half.ValidationErrors.Values.Any(v => v.Contains("Item code", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Attachment_auth_UserB_cannot_list_UserA_TempDocId()
    {
        var userA = CreateAttachmentSut(userId: "userA");
        var temp = await CreateSut(userId: "userA").CreateTempDocIdAsync();
        Assert.True(temp.Succeeded, temp.ErrorMessage);

        await using var pdf = new MemoryStream("%PDF-1.4 test"u8.ToArray());
        var upload = await userA.UploadAsync(temp.TempDocId!, "a.pdf", "application/pdf", pdf, pdf.Length);
        Assert.True(upload.Succeeded, upload.Message);

        var userB = CreateAttachmentSut(userId: "userB");
        var list = await userB.ListAsync(temp.TempDocId!);
        Assert.False(list.Succeeded);
        Assert.Equal(IvMasterErrorCode.AccessDenied, list.ErrorCode);
    }

    [Fact]
    public async Task CreateTempDocId_returns_PRTMP_prefix()
    {
        var sut = CreateSut();
        var result = await sut.CreateTempDocIdAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.StartsWith(PoPrService.TempDocIdPrefix, result.TempDocId);
    }

    [Fact]
    public async Task VIEW_COST_denial_still_persists_real_prices()
    {
        var denied = CreateSut(access: DenyPermission(PermissionCodes.ViewCost));
        var result = await denied.SaveNewAsync(Request(Line(
            "A100",
            qty: 2m,
            price: 999999m,
            amount: 999999m,
            taxAmount: 999999m)));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(25m, result.Document!.Lines[0].UnitPrice);
        Assert.Equal(50m, result.Document.Lines[0].Amount);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.PoPrDetails.SingleAsync(x => x.PrNo == result.PrNo);
        Assert.Equal(25m, stored.UnitPrice);
        Assert.Equal(50m, stored.Amount);
    }

    [Fact]
    public async Task SupplierPrice_mode_3_uses_vendor_item_price_for_new_line()
    {
        var sut = CreateSut(options: new PoPrOptions { SupplierPrice = 3 });
        var result = await sut.SaveNewAsync(Request(Line("A100", qty: 1m, vendor: "SUP01", price: 999999m)));
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(18m, result.Document!.Lines[0].UnitPrice);
    }

    private static PoPrSaveRequest Request(
        PoPrLineDto line,
        byte[]? rowVersion = null,
        string? requester = null,
        string? checkedBy = null,
        string? authorisedBy = null) =>
        Request([line], rowVersion, requester, checkedBy, authorisedBy);

    private static PoPrSaveRequest Request(
        PoPrLineDto line1,
        PoPrLineDto line2,
        byte[]? rowVersion = null,
        string? requester = null,
        string? checkedBy = null,
        string? authorisedBy = null) =>
        Request([line1, line2], rowVersion, requester, checkedBy, authorisedBy);

    private static PoPrSaveRequest Request(
        IReadOnlyList<PoPrLineDto> lines,
        byte[]? rowVersion = null,
        string? requester = null,
        string? checkedBy = null,
        string? authorisedBy = null) =>
        new()
        {
            CreateDt = FixedToday,
            Requester = requester ?? "user",
            CheckedBy = checkedBy,
            AuthorisedBy = authorisedBy,
            PrType = PoPrTypes.Purchasing,
            Remarks = "test",
            RowVersion = rowVersion,
            Lines = lines
        };

    private static PoPrLineDto Line(
        string iCode,
        decimal qty,
        decimal price = 0m,
        decimal amount = 0m,
        decimal taxAmount = 0m,
        string currency = "MYR",
        string? vendor = null,
        short line = 0,
        bool inclusive = false) =>
        new()
        {
            Line = line,
            ICode = iCode,
            PurchaseQty = qty,
            Qty = qty,
            PurchaseUom = "EA",
            StdUom = "EA",
            PackSz = 1m,
            Currency = currency,
            UnitPrice = price,
            Amount = amount,
            TaxAmount = taxAmount,
            TaxGroup = "SR",
            VendorCd = vendor,
            IsInclusive = inclusive,
            ToWarehouse = "MAIN"
        };

    private PoPrService CreateSut(
        Mock<IAccessRightService>? access = null,
        string userId = "user",
        PoPrOptions? options = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: userId);
        var attach = CreateAttachmentSut(userId: userId, access: access);
        return new PoPrService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            new FakePoPrDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new PoPrRepository(),
            Options.Create(options ?? new PoPrOptions()),
            attach,
            NullLogger<PoPrService>.Instance);
    }

    private PoPrAttachmentService CreateAttachmentSut(
        string userId = "user",
        Mock<IAccessRightService>? access = null)
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(
            company: "DEMO",
            branch: "HQ",
            location: "MAIN",
            userId: userId);
        return new PoPrAttachmentService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = _attachRoot }),
            Options.Create(new PoPrOptions()),
            NullLogger<PoPrAttachmentService>.Instance);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private static Mock<IAccessRightService> DenyPermission(string permission)
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => string.Equals(p, permission, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        access.Setup(x => x.CanAsync(
                It.IsAny<string>(),
                It.Is<string>(p => !string.Equals(p, permission, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
