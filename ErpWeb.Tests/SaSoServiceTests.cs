using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Sales;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.CustomerProfile;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Entities.Sales;
using ErpWeb.Model.Repositories.Inventory;
using ErpWeb.Model.Repositories.Sales;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

internal sealed class FakeSalesDocumentNumberingService : IDocumentNumberingService
{
    private int _doSeq;
    private int _invSeq;
    private int _soSeq;

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

        if (string.Equals(module, "SO", StringComparison.OrdinalIgnoreCase))
        {
            var n = Interlocked.Increment(ref _soSeq);
            return Task.FromResult(new DocumentNumberResult($"SO{documentDate:yy}{documentDate:MM}-{n:D4}", "SO"));
        }

        if (string.Equals(module, "DO", StringComparison.OrdinalIgnoreCase))
        {
            var n = Interlocked.Increment(ref _doSeq);
            return Task.FromResult(new DocumentNumberResult($"DO{documentDate:yy}{documentDate:MM}-{n:D4}", "DO"));
        }

        var inv = Interlocked.Increment(ref _invSeq);
        return Task.FromResult(new DocumentNumberResult($"INV{documentDate:yy}{documentDate:MM}-{inv:D4}", "INV"));
    }
}

public class SaSoServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaSoServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;
        _factory = new TestDbContextFactory(options);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
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
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
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
        db.IvStatuses.Add(new IvStatus
        {
            CompanyCode = "DEMO",
            IStatus = "ACTIVE",
            IsActive = true
        });
        db.SaCurrencies.Add(new SaCurrency
        {
            CompanyCode = "DEMO",
            CurrCode = "MYR",
            IsActive = true
        });
        db.IvMsCodes.Add(new IvMsCode
        {
            Code = "NET30",
            Name = "Net 30",
            CodeType = IvMsCodeTypes.PayCode
        });
        db.SaPaymentTerms.Add(new SaPaymentTerm
        {
            CompanyCode = "DEMO",
            PayCode = "NET30",
            PayDesc = "Net 30",
            Days = 30,
            IsActive = true
        });
        db.SaSalesReps.Add(new SaSalesRep
        {
            CompanyCode = "DEMO",
            SrepCode = "SM1",
            SrepName = "Sales One",
            IsActive = true
        });
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
                IDesc = "Stock item",
                IClassCode = "RAW",
                StdUom = "EA",
                SellingUom = "EA",
                StockControl = true,
                IsActive = true,
                SellingPrice = 10m,
                SellingGlCode = "GLSALE",
                Classification = "CLASS-A",
                DefWarehouse = "MAIN"
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "SVC1",
                IDesc = "Service item",
                IClassCode = "RAW",
                StdUom = "EA",
                SellingUom = "EA",
                StockControl = false,
                IsActive = true,
                SellingPrice = 50m,
                SellingGlCode = "GLSVC",
                Classification = "CLASS-S"
            });
        db.SaCusts.AddRange(
            new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST01",
                CustName = "Alpha",
                Currency = "MYR",
                PayCode = "NET30",
                SalesmanCode = "SM1",
                GlCode = "GLAR01",
                TinNo = "TIN01",
                Country = "MY",
                InvName = "Alpha",
                InvAddress1 = "INV ADDR 1",
                InvCity = "INV CITY",
                InvPostalCode = "50000",
                InvCountry = "MY",
                ShipName = "Alpha",
                ShipAddress1 = "SHIP ADDR 1",
                ShipCity = "SHIP CITY",
                ShipPostalCode = "50000",
                ShipCountry = "MY",
                IsActive = true,
                RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
            },
            new SaCust
            {
                CompanyCode = "DEMO",
                CustCode = "CUST02",
                CustName = "Beta",
                Currency = "MYR",
                PayCode = "NET30",
                SalesmanCode = "SM1",
                GlCode = "GLAR02",
                TinNo = "TIN02",
                Country = "MY",
                InvName = "Beta",
                InvAddress1 = "BETA ADDR",
                InvCity = "BETA CITY",
                InvPostalCode = "50001",
                InvCountry = "MY",
                ShipName = "Beta",
                ShipAddress1 = "BETA SHIP",
                ShipCity = "BETA SHIP CITY",
                ShipPostalCode = "50001",
                ShipCountry = "MY",
                IsActive = true,
                RowVersion = [2, 0, 0, 0, 0, 0, 0, 0]
            });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_creates_NEW_SO_with_full_balance_and_custrel()
    {
        var sut = CreateSoSut();

        var save = await sut.SaveNewAsync(SoRequest(qty: 10m, price: 12m));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("SO2609-0001", save.SoNo);
        Assert.Equal(SaSoStatuses.New, save.Document!.Status);
        var line = Assert.Single(save.Document.Lines);
        Assert.Equal(10m, line.OrderQty);
        Assert.Equal(0m, line.ShippedQty);
        Assert.Equal(10m, line.BalanceQty);
        Assert.Equal(1, line.CustRel);

        await using var db = await _factory.CreateDbContextAsync();
        var so = await db.SaSos.Include(x => x.Details).SingleAsync();
        Assert.Equal(1, so.CustRel);
        Assert.True(so.IsCurrent);
        Assert.Equal(1, so.LastCustRel);
        Assert.Null(so.RevisionReason);
        Assert.Equal("DEMO", so.CompanyCode);
        Assert.Equal("HQ", so.BranchCode);
        Assert.Equal("SO", so.Prefix);
        Assert.Equal("DEMO", so.Details.Single().CompanyCode);
        Assert.Equal("HQ", so.Details.Single().BranchCode);
    }

    [Fact]
    public async Task Revise_clones_current_and_supersedes_previous()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 10m, price: 12m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var reviseReq = SoRequest(qty: 8m, price: 15m, rowVersion: save.Document!.RowVersion);
        reviseReq.RevisionReason = "Customer changed qty";
        var revise = await sut.ReviseAsync(save.SoNo!, reviseReq);

        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(2, revise.Document!.CustRel);
        Assert.True(revise.Document.IsCurrent);
        Assert.Equal(2, revise.Document.LastCustRel);
        Assert.Equal("Customer changed qty", revise.Document.RevisionReason);
        Assert.Equal(8m, Assert.Single(revise.Document.Lines).OrderQty);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.SaSos.Where(x => x.SoNo == save.SoNo).OrderBy(x => x.CustRel).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsCurrent);
        Assert.Equal(SaSoStatuses.Superseded, rows[0].Status);
        Assert.True(rows[1].IsCurrent);
        Assert.Equal(SaSoStatuses.New, rows[1].Status);
        Assert.Equal(2, rows[1].LastCustRel);
    }

    [Fact]
    public async Task Delete_rev3_then_rev2_preserves_LastCustRel_and_next_revise_is_4()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        Assert.True((await sut.ReviseAsync(save.SoNo!, SoRequest(qty: 5m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
        Assert.True((await sut.ReviseAsync(save.SoNo!, SoRequest(qty: 5m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(3, await db.SaSos.CountAsync(x => x.SoNo == save.SoNo));
            Assert.Equal(3, (await db.SaSos.SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent)).CustRel);
        }

        var delete3 = await sut.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.True(delete3.Succeeded, delete3.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var current = await db.SaSos.SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
            Assert.Equal(2, current.CustRel);
            Assert.Equal(3, current.LastCustRel);
            Assert.Equal(SaSoStatuses.New, current.Status);
        }

        var delete2 = await sut.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.True(delete2.Succeeded, delete2.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var current = await db.SaSos.SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
            Assert.Equal(1, current.CustRel);
            Assert.Equal(3, current.LastCustRel);
        }

        var revise = await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 5m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(4, revise.Document!.CustRel);
        Assert.Equal(4, revise.Document.LastCustRel);
    }

    [Fact]
    public async Task Delete_rev1_only_removes_entire_chain()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 3m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var delete = await sut.DeleteAsync([SoKeyed(save.SoNo!, save.Document!.RowVersion)]);
        Assert.True(delete.Succeeded, delete.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaSos.CountAsync(x => x.SoNo == save.SoNo));
        Assert.Equal(0, await db.SaSoDetails.CountAsync(x => x.SoNo == save.SoNo));
    }

    [Fact]
    public async Task Historical_lineage_on_rev1_does_not_block_revise_or_delete_of_current()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        // Seed a current unused Rev 2 while Rev 1 retains posted lineage (manual chain).
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var rev1 = await db.SaSos.Include(x => x.Details).SingleAsync(x => x.SoNo == save.SoNo && x.CustRel == 1);
            rev1.IsCurrent = false;
            rev1.Status = SaSoStatuses.Superseded;
            rev1.LastCustRel = 2;

            var rev2 = new SaSo
            {
                CompanyCode = rev1.CompanyCode,
                BranchCode = rev1.BranchCode,
                SoNo = rev1.SoNo,
                CustRel = 2,
                IsCurrent = true,
                LastCustRel = 2,
                SoDate = rev1.SoDate,
                Status = SaSoStatuses.New,
                FulfillmentStatus = SaDualStatuses.None,
                BillingStatus = SaDualStatuses.None,
                CustCode = rev1.CustCode,
                CustName = rev1.CustName,
                Currency = rev1.Currency,
                CurrRate = rev1.CurrRate,
                PayCode = rev1.PayCode,
                SalesRep = rev1.SalesRep,
                GrossAmnt = rev1.GrossAmnt,
                Taxes = rev1.Taxes,
                TotAmnt = rev1.TotAmnt,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = "admin",
                RowVersion = Guid.NewGuid().ToByteArray()
            };
            foreach (var d in rev1.Details)
            {
                rev2.Details.Add(new SaSoDetail
                {
                    CompanyCode = d.CompanyCode,
                    BranchCode = d.BranchCode,
                    SoNo = d.SoNo,
                    CustRel = 2,
                    Line = d.Line,
                    ICode = d.ICode,
                    IDesc = d.IDesc,
                    OrderQty = d.OrderQty,
                    ShippedQty = 0m,
                    BalanceQty = d.OrderQty,
                    DeliveredQty = 0m,
                    InvoicedQty = 0m,
                    UnitPrice = d.UnitPrice,
                    StdQty = d.StdQty,
                    StdPsize = d.StdPsize,
                    StdUom = d.StdUom,
                    SellingUom = d.SellingUom,
                    Amount = d.Amount,
                    NetAmount = d.NetAmount,
                    LocalAmount = d.LocalAmount,
                    StockControl = d.StockControl
                });
            }

            db.SaSos.Add(rev2);
            await db.SaveChangesAsync();
        }

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        var listRow = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.Equal(2, listRow.CustRel);
        Assert.True(listRow.CanRevise);
        Assert.True(listRow.CanDelete);
        Assert.Null(listRow.MutationBlockReason);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(3, revise.Document!.CustRel);

        // After revise, current is unused Rev 3 — Search flags remain eligible until usage appears.
        var pageAfterRevise = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var rowAfterRevise = Assert.Single(pageAfterRevise.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.True(rowAfterRevise.CanRevise);
        Assert.True(rowAfterRevise.CanDelete);

        var deleteCurrent = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.True(deleteCurrent.Succeeded, deleteCurrent.ErrorMessage);

        await using var verify = await _factory.CreateDbContextAsync();
        var current = await verify.SaSos.SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
        Assert.Equal(2, current.CustRel);
    }

    [Fact]
    public async Task Historical_mutate_rejects_Update_Delete_ForceClose_on_superseded()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.ReviseAsync(save.SoNo!, SoRequest(qty: 5m, price: 10m, rowVersion: save.Document!.RowVersion))).Succeeded);

        byte[] rev1Version;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            rev1Version = (await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == save.SoNo && x.CustRel == 1)).RowVersion ?? [];
        }

        var updateReq = SoRequest(qty: 5m, price: 10m, rowVersion: rev1Version);
        updateReq.CustRel = 1;
        var update = await sut.UpdateAsync(save.SoNo!, updateReq);
        Assert.False(update.Succeeded);
        Assert.Contains("historical", update.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var delete = await sut.DeleteAsync([new SaSoKeyedRequest { SoNo = save.SoNo!, CustRel = 1, RowVersion = rev1Version }]);
        Assert.False(delete.Succeeded);
        Assert.Contains("historical", delete.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var close = await sut.ForceCloseAsync([new SaSoKeyedRequest { SoNo = save.SoNo!, CustRel = 1, RowVersion = rev1Version }]);
        Assert.False(close.Succeeded);
        Assert.Contains("historical", close.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Draft_DO_on_current_blocks_revise_until_deleted()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var blocked = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(blocked.Succeeded);
        Assert.Contains("draft", blocked.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        Assert.True((await doService.DeleteAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 9m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(revise.Succeeded, revise.ErrorMessage);
        Assert.Equal(2, revise.Document!.CustRel);
    }

    [Fact]
    public async Task OptionB_DO_with_stale_CustRel_is_rejected_after_revise()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.DeleteAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        Assert.True((await so.ReviseAsync(save.SoNo!, SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);

        var stale = await doService.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                new SaDoLineRequest
                {
                    ICode = "SVC1",
                    Qty = 2m,
                    UnitPrice = 10m,
                    SoNo = save.SoNo,
                    SoLine = 1,
                    CustRel = 1
                }
            ]
        });
        Assert.False(stale.Succeeded);
        Assert.Equal(SaDoErrorKind.Concurrency, stale.ErrorKind);
        Assert.Contains("revised", stale.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revise_rollback_after_supersede_leaves_original_unchanged()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        sut.TestHookAfterSupersedeBeforeInsert = () => throw new InvalidOperationException("forced revise failure");

        var revise = await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 9m, price: 10m, rowVersion: save.Document!.RowVersion));
        Assert.False(revise.Succeeded);
        Assert.Equal(SaSoErrorKind.Unexpected, revise.ErrorKind);

        await using var db = await _factory.CreateDbContextAsync();
        var rows = await db.SaSos.Where(x => x.SoNo == save.SoNo).ToListAsync();
        Assert.Single(rows);
        Assert.True(rows[0].IsCurrent);
        Assert.Equal(1, rows[0].CustRel);
        Assert.Equal(1, rows[0].LastCustRel);
        Assert.Equal(SaSoStatuses.New, rows[0].Status);
        Assert.Equal(1, await db.SaSoDetails.CountAsync(x => x.SoNo == save.SoNo));
    }

    [Fact]
    public async Task Search_returns_current_revision_only()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 4m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 4m, price: 10m, rowVersion: save.Document!.RowVersion))).Succeeded);

        var page = await sut.SearchAsync(new SaSoListQuery { Take = 50 });
        Assert.True(page.Succeeded, page.ErrorMessage);
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.Equal(2, row.CustRel);
        Assert.Equal(SaSoStatuses.New, row.Status);
        Assert.True(row.CanRevise);
        Assert.True(row.CanDelete);
    }

    [Fact]
    public async Task Mutation_gates_unused_NEW_allows_edit_delete_revise_and_search_flags()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var page = await sut.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.True(row.CanRevise);
        Assert.True(row.CanDelete);

        var update = await sut.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 6m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(update.Succeeded, update.ErrorMessage);

        var draft = await sut.GetReviseDraftAsync(save.SoNo!);
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var revise = await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 6m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(revise.Succeeded, revise.ErrorMessage);

        Assert.True((await sut.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);
    }

    [Fact]
    public async Task Mutation_gates_draft_DO_blocks_delete_revise_and_GetReviseDraft_with_draft_message()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1))).Succeeded);

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);
        Assert.Contains("draft Delivery Order", row.MutationBlockReason!, StringComparison.OrdinalIgnoreCase);

        var draft = await so.GetReviseDraftAsync(save.SoNo!);
        Assert.False(draft.Succeeded);
        Assert.Contains("draft Delivery Order", draft.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("draft Delivery Order", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var delete = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.False(delete.Succeeded);
        Assert.Contains("draft Delivery Order", delete.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 11m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.True(update.Succeeded, update.ErrorMessage);
    }

    [Fact]
    public async Task Mutation_gates_draft_Invoice_blocks_delete_revise_with_draft_message()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var invoices = CreateInvoiceSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var inv = await invoices.SaveNewAsync(InvoiceRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(inv.Succeeded, inv.ErrorMessage);

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);
        Assert.Contains("draft Invoice", row.MutationBlockReason!, StringComparison.OrdinalIgnoreCase);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("draft Invoice", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var delete = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.False(delete.Succeeded);
        Assert.Contains("draft Invoice", delete.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        Assert.True((await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 11m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
    }

    [Fact]
    public async Task Mutation_gates_partial_posted_DO_blocks_delete_revise_allows_edit()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);
        Assert.Equal("already in use", row.MutationBlockReason);

        Assert.False((await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
        Assert.False((await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);

        // SHIPPED keeps existing Update rules (customer change blocked; qty still editable when floor allows).
        var get = await so.GetAsync(save.SoNo!);
        Assert.Equal(SaSoStatuses.Shipped, get.Document!.Status);
        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 11m, rowVersion: GetSoRowVersion(save.SoNo!), line: 1));
        Assert.True(update.Succeeded, update.ErrorMessage);
    }

    [Fact]
    public async Task Mutation_gates_partial_Invoice_blocks_delete_revise()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var invoices = CreateInvoiceSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var inv = await invoices.SaveNewAsync(InvoiceRequest(qty: 3m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(inv.Succeeded, inv.ErrorMessage);
        Assert.True((await invoices.PostAsync([inv.InvNo!])).Succeeded);

        Assert.False((await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
        var delete = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.False(delete.Succeeded);
        Assert.Contains("already in use", delete.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 11m, rowVersion: GetSoRowVersion(save.SoNo!), line: 1));
        Assert.True(update.Succeeded, update.ErrorMessage);
    }

    [Fact]
    public async Task Mutation_gates_allocation_only_blocks_with_generic_message_and_batch_matches_single()
    {
        var so = CreateSoSut();
        var save = await so.SaveNewAsync(SoRequest(qty: 8m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaDocApplications.Add(new SaDocApplication
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SourceDocType = SaDocTypes.So,
                SourceDocId = save.SoNo!,
                SourceCustRel = 1,
                SourceLineId = 1,
                TargetDocType = SaDocTypes.Do,
                TargetDocId = "DO-SEED-1",
                TargetCustRel = 0,
                TargetLineId = 1,
                RelatedSoNo = string.Empty,
                RelatedCustRel = 0,
                AppliedQty = 1m,
                Created = DateTime.UtcNow,
                CreatedUid = "test"
            });
            await db.SaveChangesAsync();
        }

        var docs = new SaDocApplicationService(new SaSoRepository(), new SaDoRepository());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.True(await docs.HasAllocationsForSoAsync(db, "DEMO", "HQ", save.SoNo!, 1));
            var batched = await docs.ListAllocatedSoKeysAsync(
                db,
                "DEMO",
                "HQ",
                [new SaDocSoRevisionKey(save.SoNo!, 1)]);
            Assert.Contains(new SaDocSoRevisionKey(save.SoNo!, 1), batched);
        }

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);
        Assert.Equal("already in use", row.MutationBlockReason);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 8m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("already in use", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Delivery Order or Invoice", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Mutation_gates_allocation_batch_includes_DO_to_INV_related_SO_path()
    {
        var so = CreateSoSut();
        var save = await so.SaveNewAsync(SoRequest(qty: 8m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaDocApplications.Add(new SaDocApplication
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SourceDocType = SaDocTypes.Do,
                SourceDocId = "DO-REL-1",
                SourceCustRel = 0,
                SourceLineId = 1,
                TargetDocType = SaDocTypes.Inv,
                TargetDocId = "INV-REL-1",
                TargetCustRel = 0,
                TargetLineId = 1,
                RelatedSoNo = save.SoNo!,
                RelatedCustRel = 1,
                RelatedSoLine = 1,
                AppliedQty = 1m,
                Created = DateTime.UtcNow,
                CreatedUid = "test"
            });
            await db.SaveChangesAsync();
        }

        var docs = new SaDocApplicationService(new SaSoRepository(), new SaDoRepository());
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.True(await docs.HasAllocationsForSoAsync(db, "DEMO", "HQ", save.SoNo!, 1));
            var batched = await docs.ListAllocatedSoKeysAsync(
                db,
                "DEMO",
                "HQ",
                [new SaDocSoRevisionKey(save.SoNo!, 1)]);
            Assert.Contains(new SaDocSoRevisionKey(save.SoNo!, 1), batched);
        }

        Assert.False((await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);
    }

    [Fact]
    public async Task Mutation_gates_combined_usage_uses_generic_message_not_draft_only()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1))).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaSoDetails.SingleAsync(x => x.SoNo == save.SoNo && x.CustRel == 1);
            detail.ShippedQty = 1m;
            db.SaDocApplications.Add(new SaDocApplication
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                SourceDocType = SaDocTypes.So,
                SourceDocId = save.SoNo!,
                SourceCustRel = 1,
                SourceLineId = 1,
                TargetDocType = SaDocTypes.Do,
                TargetDocId = "DO-COMBINED",
                TargetLineId = 1,
                AppliedQty = 1m,
                Created = DateTime.UtcNow,
                CreatedUid = "test"
            });
            await db.SaveChangesAsync();
        }

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);
        Assert.Equal("already in use", row.MutationBlockReason);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("already in use", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("draft", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 11m, rowVersion: GetSoRowVersion(save.SoNo!), line: 1));
        Assert.True(update.Succeeded, update.ErrorMessage);
    }

    [Fact]
    public async Task Mutation_gates_CLOSED_blocks_update_delete_revise()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);
        Assert.True((await so.ForceCloseAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.Equal(SaSoStatuses.Closed, row.Status);
        Assert.False(row.CanRevise);
        Assert.False(row.CanDelete);

        Assert.False((await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
        Assert.False((await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);
        Assert.False((await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)))).Succeeded);
        Assert.False((await so.GetReviseDraftAsync(save.SoNo!)).Succeeded);
    }

    [Fact]
    public async Task Mutation_gates_stale_list_flag_rejects_revise_and_delete_after_usage_created()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var page = await so.SearchAsync(new SaSoListQuery { Take = 50 });
        var row = Assert.Single(page.ListPage!.Rows, x => x.SoNo == save.SoNo);
        Assert.True(row.CanRevise);
        Assert.True(row.CanDelete);

        // Usage appears after list flags were calculated (stale UX hint).
        Assert.True((await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1))).Succeeded);

        var revise = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("draft Delivery Order", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        var delete = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.False(delete.Succeeded);
        Assert.Contains("draft Delivery Order", delete.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revise_rejects_SHIPPED_and_CLOSED()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var shipped = await so.GetAsync(save.SoNo!);
        Assert.Equal(SaSoStatuses.Shipped, shipped.Document!.Status);
        var reviseShipped = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: shipped.Document.RowVersion));
        Assert.False(reviseShipped.Succeeded);
        Assert.Contains("NEW", reviseShipped.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        Assert.True((await so.ForceCloseAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))])).Succeeded);
        var closed = await so.GetAsync(save.SoNo!);
        Assert.Equal(SaSoStatuses.Closed, closed.Document!.Status);
        var reviseClosed = await so.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, rowVersion: closed.Document.RowVersion));
        Assert.False(reviseClosed.Succeeded);
    }

    [Fact]
    public async Task Migration_alter_script_contains_end_assertions_and_trusted_fk_checks()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "scripts", "alter-saso-revision.sql"));
        Assert.True(File.Exists(path), path);
        var sql = await File.ReadAllTextAsync(path);
        Assert.Contains("UX_SaSO_Current", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UQ_SaSODetail_Company_Branch_SoNo_Line", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("is_not_trusted", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Assertion failed", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sys.foreign_keys", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revision_limit_rejects_when_LastCustRel_is_max()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var current = await db.SaSos.SingleAsync(x => x.SoNo == save.SoNo);
            current.LastCustRel = SaSoRevisionLimits.MaxCustRel;
            await db.SaveChangesAsync();
        }

        var revise = await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 2m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!)));
        Assert.False(revise.Succeeded);
        Assert.Contains("maximum revision", revise.ErrorMessage!, StringComparison.OrdinalIgnoreCase);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.SaSos.CountAsync(x => x.SoNo == save.SoNo));
        Assert.True(await verify.SaSos.AnyAsync(x => x.SoNo == save.SoNo && x.IsCurrent && x.CustRel == 1));
    }

    [Fact]
    public async Task Stale_RowVersion_rejects_revise()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 4m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        Assert.True((await sut.ReviseAsync(save.SoNo!, SoRequest(qty: 4m, price: 10m, rowVersion: save.Document!.RowVersion))).Succeeded);

        var stale = await sut.ReviseAsync(
            save.SoNo!,
            SoRequest(qty: 4m, price: 10m, rowVersion: save.Document.RowVersion));
        Assert.False(stale.Succeeded);
        Assert.Equal(SaSoErrorKind.Concurrency, stale.ErrorKind);
    }

    [Fact]
    public async Task GetRemainingLines_after_revise_uses_current_revision_only()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.ReviseAsync(save.SoNo!, SoRequest(qty: 7m, price: 10m, rowVersion: save.Document!.RowVersion))).Succeeded);

        var remaining = await sut.GetRemainingLinesAsync(save.SoNo!);
        Assert.True(remaining.Succeeded, remaining.ErrorMessage);
        var line = Assert.Single(remaining.RemainingLines);
        Assert.Equal(7m, line.BalanceQty);
        Assert.Equal(2, line.CustRel);
    }

    [Fact]
    public async Task Source_fields_carry_CustPo_revision_and_lines_onto_DO_and_invoice()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);
        var invoices = CreateInvoiceSut(numbering: numbering);

        var soReq = SoRequest(qty: 10m, price: 10m);
        soReq.CustPo = "PO-ALPHA-1";
        var save = await so.SaveNewAsync(soReq);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("PO-ALPHA-1", save.Document!.CustPo);
        Assert.Equal("PO-ALPHA-1", Assert.Single(save.Document.Lines).CustPo);

        var remaining = await so.GetRemainingLinesAsync(save.SoNo!);
        Assert.True(remaining.Succeeded, remaining.ErrorMessage);
        Assert.Equal("PO-ALPHA-1", Assert.Single(remaining.RemainingLines).CustPo);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        var doLine = Assert.Single(doSave.Document!.Lines);
        Assert.Equal(save.SoNo, doLine.SoNo);
        Assert.Equal((short)1, doLine.SoLine);
        Assert.Equal((short)1, doLine.CustRel);
        Assert.Equal("PO-ALPHA-1", doLine.CustPo);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var persisted = await db.SaDoDetails.SingleAsync(x => x.DoNo == doSave.DoNo);
            Assert.Equal("PO-ALPHA-1", persisted.CustPo);
        }

        var invSave = await invoices.SaveNewAsync(InvoiceRequest(qty: 3m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(invSave.Succeeded, invSave.ErrorMessage);
        var invLine = Assert.Single(invSave.Document!.Lines);
        Assert.Equal(save.SoNo, invLine.SoNo);
        Assert.Equal((short)1, invLine.SoLine);
        Assert.Equal((short)1, invLine.CustRel);
        Assert.Equal("PO-ALPHA-1", invLine.CustPo);
        Assert.False(invLine.LinkDo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SaveNew_rejects_blank_CustPo_and_does_not_persist(string? custPo)
    {
        var sut = CreateSoSut();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.SaSos.CountAsync());
        }

        var req = SoRequest(qty: 1m, price: 10m);
        req.CustPo = custPo;
        var save = await sut.SaveNewAsync(req);

        Assert.False(save.Succeeded);
        Assert.True(save.ValidationErrors.ContainsKey("CustPo"));
        Assert.Equal("Customer PO is required.", save.ValidationErrors["CustPo"]);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(0, await db.SaSos.CountAsync());
        }
    }

    [Fact]
    public async Task SaveNew_accepts_overlength_CustPo_and_truncates_to_50()
    {
        var sut = CreateSoSut();
        var over = new string('P', 60);
        var req = SoRequest(qty: 1m, price: 10m);
        req.CustPo = over;

        var save = await sut.SaveNewAsync(req);
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(over[..50], save.Document!.CustPo);

        var get = await sut.GetAsync(save.SoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal(over[..50], get.Document!.CustPo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Update_rejects_blank_CustPo_without_partial_mutation(string? custPo)
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("PO-1", save.Document!.CustPo);

        var beforeVersion = save.Document.RowVersion;
        var beforeTot = save.Document.TotAmnt;
        var updateReq = SoRequest(qty: 2m, price: 10m, rowVersion: beforeVersion);
        updateReq.CustPo = custPo;

        var update = await sut.UpdateAsync(save.SoNo!, updateReq);
        Assert.False(update.Succeeded);
        Assert.True(update.ValidationErrors.ContainsKey("CustPo"));

        var get = await sut.GetAsync(save.SoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal("PO-1", get.Document!.CustPo);
        Assert.Equal(beforeTot, get.Document.TotAmnt);
        Assert.Equal(beforeVersion, get.Document.RowVersion);
    }

    [Fact]
    public async Task Update_legacy_blank_CustPo_requires_value_then_accepts()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.SaSos.SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
            row.CustPo = null;
            await db.SaveChangesAsync();
        }

        var blankUpdate = SoRequest(qty: 2m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!));
        blankUpdate.CustPo = null;
        var rejected = await sut.UpdateAsync(save.SoNo!, blankUpdate);
        Assert.False(rejected.Succeeded);
        Assert.True(rejected.ValidationErrors.ContainsKey("CustPo"));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var row = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
            Assert.Null(row.CustPo);
        }

        var filled = SoRequest(qty: 2m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!));
        filled.CustPo = "LEGACY-PO";
        var ok = await sut.UpdateAsync(save.SoNo!, filled);
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.Equal("LEGACY-PO", ok.Document!.CustPo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Revise_rejects_blank_CustPo_without_creating_revision(string? custPo)
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 2m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var reviseReq = SoRequest(qty: 2m, price: 10m, rowVersion: save.Document!.RowVersion);
        reviseReq.CustPo = custPo;
        reviseReq.RevisionReason = "need po";

        var revise = await sut.ReviseAsync(save.SoNo!, reviseReq);
        Assert.False(revise.Succeeded);
        Assert.True(revise.ValidationErrors.ContainsKey("CustPo"));

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(1, await db.SaSos.CountAsync(x => x.SoNo == save.SoNo));
        var current = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == save.SoNo && x.IsCurrent);
        Assert.Equal("PO-1", current.CustPo);
        Assert.True(current.IsCurrent);
        Assert.Equal((short)1, current.CustRel);
    }

    [Fact]
    public async Task SaveNew_round_trips_Ref1_and_ProjId_with_TruncateOptional_contract()
    {
        var sut = CreateSoSut();
        var overRef = new string('R', 60);
        var overProj = new string('P', 30);

        var save = await sut.SaveNewAsync(new SaSoSaveRequest
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            Ref1 = "  ABC  ",
            ProjId = "  PRJ1  ",
            Lines = [SoLine("SVC1", 1m, 10m)]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal("ABC", save.Document!.Ref1);
        Assert.Equal("PRJ1", save.Document.ProjId);

        var get = await sut.GetAsync(save.SoNo!);
        Assert.True(get.Succeeded, get.ErrorMessage);
        Assert.Equal("ABC", get.Document!.Ref1);
        Assert.Equal("PRJ1", get.Document.ProjId);

        var emptySave = await sut.SaveNewAsync(new SaSoSaveRequest
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            Ref1 = "   ",
            ProjId = "",
            Lines = [SoLine("SVC1", 1m, 10m)]
        });
        Assert.True(emptySave.Succeeded, emptySave.ErrorMessage);
        Assert.Null(emptySave.Document!.Ref1);
        Assert.Null(emptySave.Document.ProjId);

        var longSave = await sut.SaveNewAsync(new SaSoSaveRequest
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            Ref1 = overRef,
            ProjId = overProj,
            Lines = [SoLine("SVC1", 1m, 10m)]
        });
        Assert.True(longSave.Succeeded, longSave.ErrorMessage);
        Assert.Equal(overRef[..50], longSave.Document!.Ref1);
        Assert.Equal(overProj[..20], longSave.Document.ProjId);
    }

    [Fact]
    public async Task SaveNew_ignores_client_display_line_numbers()
    {
        var sut = CreateSoSut();

        // UI Renumber() sends 1..n on create; those must not be treated as existing PKs.
        var save = await sut.SaveNewAsync(SoRequest(qty: 5m, price: 10m, line: 1));

        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.Equal(1, Assert.Single(save.Document!.Lines).Line);
    }

    [Theory]
    [InlineData(PermissionCodes.Add)]
    [InlineData(PermissionCodes.Edit)]
    [InlineData(PermissionCodes.Delete)]
    [InlineData(PermissionCodes.Close)]
    [InlineData(PermissionCodes.Access)]
    public async Task Authorization_denied_blocks_required_SO_actions(string permission)
    {
        var denied = CreateSoSut(access: DenyPermission(permission));

        switch (permission)
        {
            case PermissionCodes.Add:
            {
                var save = await denied.SaveNewAsync(SoRequest(qty: 1m, price: 10m));
                Assert.False(save.Succeeded);
                Assert.Equal(SaSoErrorKind.Authorization, save.ErrorKind);
                break;
            }
            case PermissionCodes.Edit:
            {
                var allowed = CreateSoSut();
                var save = await allowed.SaveNewAsync(SoRequest(qty: 1m, price: 10m));
                Assert.True(save.Succeeded, save.ErrorMessage);

                var edit = await denied.UpdateAsync(save.SoNo!, SoRequest(qty: 2m, price: 10m, rowVersion: save.Document!.RowVersion));
                Assert.False(edit.Succeeded);
                Assert.Equal(SaSoErrorKind.Authorization, edit.ErrorKind);
                break;
            }
            case PermissionCodes.Delete:
            {
                var allowed = CreateSoSut();
                var save = await allowed.SaveNewAsync(SoRequest(qty: 1m, price: 10m));
                Assert.True(save.Succeeded, save.ErrorMessage);

                var delete = await denied.DeleteAsync([SoKeyed(save.SoNo!, save.Document!.RowVersion)]);
                Assert.False(delete.Succeeded);
                Assert.Equal(SaSoErrorKind.Authorization, delete.ErrorKind);
                break;
            }
            case PermissionCodes.Close:
            {
                var allowed = CreateSoSut();
                var save = await allowed.SaveNewAsync(SoRequest(qty: 1m, price: 10m));
                Assert.True(save.Succeeded, save.ErrorMessage);

                var close = await denied.ForceCloseAsync([SoKeyed(save.SoNo!, save.Document!.RowVersion)]);
                Assert.False(close.Succeeded);
                Assert.Equal(SaSoErrorKind.Authorization, close.ErrorKind);
                break;
            }
            case PermissionCodes.Access:
            {
                var search = await denied.SearchAsync(new SaSoListQuery { Take = 1 });
                Assert.False(search.Succeeded);
                Assert.Equal(SaSoErrorKind.Authorization, search.ErrorKind);
                break;
            }
            default:
                throw new InvalidOperationException($"Unhandled permission {permission}");
        }
    }

    [Fact]
    public async Task Update_SHIPPED_cannot_change_customer()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 10m, price: 10m, cust: "CUST02", rowVersion: GetSoRowVersion(save.SoNo!)));

        Assert.False(update.Succeeded);
        Assert.Equal(SaSoErrorKind.BusinessRule, update.ErrorKind);
        Assert.Contains("Customer cannot be changed", update.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_OrderQty_reduced_to_delivered_stays_SHIPPED_until_billed()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 6m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!), line: 1));

        Assert.True(update.Succeeded, update.ErrorMessage);
        Assert.Equal(SaSoStatuses.Shipped, update.Document!.Status);
        Assert.Equal(SaDualStatuses.Full, update.Document.FulfillmentStatus);
        Assert.Equal(SaDualStatuses.None, update.Document.BillingStatus);
        Assert.Null(update.Document.ClosedReason);
        Assert.Equal(6m, update.Document.Lines[0].OrderQty);
        Assert.Equal(6m, update.Document.Lines[0].ShippedQty);
        Assert.Equal(6m, update.Document.Lines[0].DeliveredQty);
        Assert.Equal(0m, update.Document.Lines[0].BalanceQty);
    }

    [Fact]
    public async Task Update_OrderQty_increased_above_shipped_stays_SHIPPED()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var update = await so.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 12m, price: 10m, rowVersion: GetSoRowVersion(save.SoNo!), line: 1));

        Assert.True(update.Succeeded, update.ErrorMessage);
        Assert.Equal(SaSoStatuses.Shipped, update.Document!.Status);
        Assert.Null(update.Document.ClosedReason);
        Assert.Equal(12m, update.Document.Lines[0].OrderQty);
        Assert.Equal(6m, update.Document.Lines[0].ShippedQty);
        Assert.Equal(6m, update.Document.Lines[0].BalanceQty);
    }

    [Fact]
    public async Task Delete_NEW_SO_removes_record()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var delete = await sut.DeleteAsync([SoKeyed(save.SoNo!, save.Document!.RowVersion)]);

        Assert.True(delete.Succeeded, delete.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.SaSos.CountAsync());
        Assert.Equal(0, await db.SaSoDetails.CountAsync());
    }

    [Fact]
    public async Task Delete_SO_blocked_when_DO_references_it()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var delete = await so.DeleteAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);

        Assert.False(delete.Succeeded);
        Assert.Equal(SaSoErrorKind.BusinessRule, delete.ErrorKind);
        Assert.True(
            delete.ErrorMessage!.Contains("referenced", StringComparison.OrdinalIgnoreCase)
            || delete.ErrorMessage.Contains("draft", StringComparison.OrdinalIgnoreCase),
            delete.ErrorMessage);
    }

    [Fact]
    public async Task ForceClose_sets_FORCE_CLOSED_without_changing_shipped_qty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var close = await so.ForceCloseAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);

        Assert.True(close.Succeeded, close.ErrorMessage);
        var after = await so.GetAsync(save.SoNo!);
        Assert.True(after.Succeeded, after.ErrorMessage);
        Assert.Equal(SaSoStatuses.Closed, after.Document!.Status);
        Assert.Equal(SaSoClosedReasons.ForceClosed, after.Document.ClosedReason);
        Assert.NotNull(after.Document.ClosedDate);
        Assert.Equal("admin", after.Document.ClosedBy);
        Assert.Equal(6m, after.Document.Lines[0].ShippedQty);
        Assert.Equal(4m, after.Document.Lines[0].BalanceQty);
    }

    [Fact]
    public async Task ForceClose_already_CLOSED_SO_fails()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 6m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var firstClose = await so.ForceCloseAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);
        Assert.True(firstClose.Succeeded, firstClose.ErrorMessage);

        var close = await so.ForceCloseAsync([SoKeyed(save.SoNo!, GetSoRowVersion(save.SoNo!))]);

        Assert.False(close.Succeeded);
        Assert.Equal(SaSoErrorKind.BusinessRule, close.ErrorKind);
        Assert.Contains("already closed", close.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stale_RowVersion_on_Update_fails_concurrency()
    {
        var sut = CreateSoSut();
        var save = await sut.SaveNewAsync(SoRequest(qty: 3m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var update = await sut.UpdateAsync(
            save.SoNo!,
            SoRequest(qty: 4m, price: 10m, rowVersion: [0, 0, 0, 0, 0, 0, 0, 0], line: 1));

        Assert.False(update.Succeeded);
        Assert.Equal(SaSoErrorKind.Concurrency, update.ErrorKind);
    }

    [Fact]
    public async Task Search_paging_clamps_take_and_returns_header_only_rows()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            for (var i = 1; i <= 105; i++)
            {
                var soNo = $"SOSEED-{i:D4}";
                db.SaSos.Add(new SaSo
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    SoNo = soNo,
                    CustRel = 1,
                    IsCurrent = true,
                    LastCustRel = 1,
                    SoDate = FixedToday.AddDays(-(i % 28)),
                    Status = SaSoStatuses.New,
                    CustCode = "CUST01",
                    CustName = "ALPHA",
                    Currency = "MYR",
                    CurrRate = 1m,
                    PayCode = "NET30",
                    TotAmnt = i,
                    CreatedDate = DateTime.UtcNow,
                    CreatedBy = "admin",
                    RowVersion = Guid.NewGuid().ToByteArray()
                });
                db.SaSoDetails.Add(new SaSoDetail
                {
                    CompanyCode = "DEMO",
                    BranchCode = "HQ",
                    SoNo = soNo,
                    Line = 1,
                    CustRel = 1,
                    ICode = "SVC1",
                    IDesc = "Service item",
                    OrderQty = 1m,
                    ShippedQty = 0m,
                    BalanceQty = 1m,
                    UnitPrice = 1m,
                    StdQty = 1m,
                    StdPsize = 1m,
                    StdUom = "EA",
                    SellingUom = "EA",
                    Amount = 1m,
                    NetAmount = 1m,
                    LocalAmount = 1m,
                    StockControl = false
                });
            }

            await db.SaveChangesAsync();
        }

        var sut = CreateSoSut();
        var page = await sut.SearchAsync(new SaSoListQuery { Take = 999 });

        Assert.True(page.Succeeded, page.ErrorMessage);
        Assert.Equal(105, page.ListPage!.TotalCount);
        Assert.Equal(SaSoRepository.MaxPageSize, page.ListPage.Rows.Count);
        Assert.All(page.ListPage.Rows, row => Assert.True(row.LineCount > 0));
    }

    [Fact]
    public async Task GetRemainingLines_returns_balance_only_and_closed_SO_returns_empty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var save = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var firstDo = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(firstDo.Succeeded, firstDo.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(firstDo.DoNo!, firstDo.Document!.RowVersion)])).Succeeded);

        var remaining = await so.GetRemainingLinesAsync(save.SoNo!);
        Assert.True(remaining.Succeeded, remaining.ErrorMessage);
        var line = Assert.Single(remaining.RemainingLines);
        Assert.Equal(4m, line.BalanceQty);
        Assert.Equal(6m, line.ShippedQty);

        var secondDo = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: save.SoNo!, soLine: 1));
        Assert.True(secondDo.Succeeded, secondDo.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(secondDo.DoNo!, secondDo.Document!.RowVersion)])).Succeeded);

        var closedRemaining = await so.GetRemainingLinesAsync(save.SoNo!);
        Assert.True(closedRemaining.Succeeded, closedRemaining.ErrorMessage);
        Assert.Empty(closedRemaining.RemainingLines);
    }

    [Fact]
    public async Task DO_post_consumes_SO_and_sets_detail_consumed_qty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var post = await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)]);

        Assert.True(post.Succeeded, post.ErrorMessage);
        var soAfter = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.Shipped, soAfter.Document!.Status);
        Assert.Equal(6m, soAfter.Document.Lines[0].ShippedQty);
        Assert.Equal(4m, soAfter.Document.Lines[0].BalanceQty);

        await using var db = await _factory.CreateDbContextAsync();
        var doDetail = await db.SaDoDetails.SingleAsync(x => x.DoNo == doSave.DoNo);
        Assert.Equal(6m, doDetail.SoConsumedQty);
    }

    [Fact]
    public async Task DO_rollback_after_full_consume_reopens_SO_to_NEW_and_clears_consumed_qty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 6m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var rollback = await doService.RollbackAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))]);

        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        var soAfter = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.New, soAfter.Document!.Status);
        Assert.Equal(0m, soAfter.Document.Lines[0].ShippedQty);
        Assert.Equal(6m, soAfter.Document.Lines[0].BalanceQty);

        await using var db = await _factory.CreateDbContextAsync();
        var doDetail = await db.SaDoDetails.SingleAsync(x => x.DoNo == doSave.DoNo);
        Assert.Equal(0m, doDetail.SoConsumedQty);
    }

    [Fact]
    public async Task Fully_consumed_SO_rollback_second_DO_reopens_SHIPPED_with_balance()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var first = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        var second = await doService.SaveNewAsync(DoRequest(qty: 4m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(first.DoNo!, first.Document!.RowVersion)])).Succeeded);
        Assert.True((await doService.PostAsync([DoKeyed(second.DoNo!, second.Document!.RowVersion)])).Succeeded);

        var rollback = await doService.RollbackAsync([DoKeyed(second.DoNo!, GetDoRowVersion(second.DoNo!))]);

        Assert.True(rollback.Succeeded, rollback.ErrorMessage);
        var soAfter = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.Shipped, soAfter.Document!.Status);
        Assert.Equal(6m, soAfter.Document.Lines[0].ShippedQty);
        Assert.Equal(4m, soAfter.Document.Lines[0].BalanceQty);
    }

    [Fact]
    public async Task ForceClosed_SO_blocks_DO_rollback_and_DO_stays_POSTED()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);
        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);
        Assert.True((await so.ForceCloseAsync([SoKeyed(soSave.SoNo!, GetSoRowVersion(soSave.SoNo!))])).Succeeded);

        var rollback = await doService.RollbackAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))]);

        Assert.False(rollback.Succeeded);
        Assert.Equal(SaSoReasonCodes.ForceClosed, rollback.Posting[0].ReasonCode);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(SaDoStatuses.Posted, (await db.SaDos.SingleAsync(x => x.DoNo == doSave.DoNo)).Status);
        var soRow = await db.SaSos.SingleAsync(x => x.SoNo == soSave.SoNo);
        Assert.Equal(SaSoClosedReasons.ForceClosed, soRow.ClosedReason);
    }

    [Fact]
    public async Task Over_consume_second_DO_save_fails_ALLOC_OVER()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var first = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(first.Succeeded, first.ErrorMessage);
        var second = await doService.SaveNewAsync(DoRequest(qty: 5m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.False(second.Succeeded);
        Assert.Contains("remaining", second.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        Assert.True((await doService.PostAsync([DoKeyed(first.DoNo!, first.Document!.RowVersion)])).Succeeded);
    }

    [Fact]
    public async Task Invoice_LinkDo_posts_DO_INV_and_rejects_mix_with_SO_INV()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);
        var invoices = CreateInvoiceSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var linkDoSave = await invoices.SaveNewAsync(InvoiceRequest(
            qty: 4m,
            price: 10m,
            soNo: soSave.SoNo!,
            soLine: 1,
            linkDo: true,
            doNo: doSave.DoNo!,
            doLine: 1));
        Assert.True(linkDoSave.Succeeded, linkDoSave.ErrorMessage);
        var linkDoPost = await invoices.PostAsync([linkDoSave.InvNo!]);
        Assert.True(linkDoPost.Succeeded, string.Join("; ", linkDoPost.Posting.Select(x => $"{x.Outcome}:{x.ErrorMessage}")));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var alloc = await db.SaDocApplications.SingleAsync(x =>
                x.TargetDocType == SaDocTypes.Inv && x.TargetDocId == linkDoSave.InvNo);
            Assert.Equal(SaDocTypes.Do, alloc.SourceDocType);
            Assert.Equal(doSave.DoNo, alloc.SourceDocId);
            Assert.Equal(4m, alloc.AppliedQty);
            Assert.Equal(soSave.SoNo, alloc.RelatedSoNo);

            var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
            Assert.Equal(6m, soLine.DeliveredQty);
            Assert.Equal(4m, soLine.InvoicedQty);
            Assert.Equal(6m, soLine.ShippedQty);
            Assert.Equal(4m, soLine.BalanceQty);
        }

        var mix = await invoices.SaveNewAsync(new SaInvoiceSaveRequest
        {
            InvDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            InvName = "Alpha",
            InvAddress1 = "INV ADDR 1",
            InvCity = "INV CITY",
            InvPostalCode = "50000",
            InvCountry = "MY",
            InvTel = "123456",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 10m,
                    SoNo = soSave.SoNo!,
                    SoLine = 1,
                    LinkDo = false
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 10m,
                    SoNo = soSave.SoNo!,
                    SoLine = 1,
                    LinkDo = true,
                    DoNo = doSave.DoNo!,
                    DoLine = 1
                }
            ]
        });
        Assert.False(mix.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.MixForbidden, mix.ErrorMessage);
    }

    [Fact]
    public async Task Invoice_direct_SO_INV_does_not_increase_ShippedQty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var invoices = CreateInvoiceSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 5m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var invSave = await invoices.SaveNewAsync(InvoiceRequest(qty: 3m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(invSave.Succeeded, invSave.ErrorMessage);
        var invPost = await invoices.PostAsync([invSave.InvNo!]);
        Assert.True(invPost.Succeeded, string.Join("; ", invPost.Posting.Select(x => $"{x.Outcome}:{x.ErrorMessage}")));

        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.SaSos.SingleAsync(x => x.SoNo == soSave.SoNo);
        var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
        Assert.Equal(0m, soLine.DeliveredQty);
        Assert.Equal(0m, soLine.ShippedQty);
        Assert.Equal(3m, soLine.InvoicedQty);
        Assert.Equal(5m, soLine.BalanceQty);
        Assert.Equal(SaSoStatuses.New, header.Status);
        Assert.Equal(SaDualStatuses.None, header.FulfillmentStatus);
        Assert.Equal(SaDualStatuses.Partial, header.BillingStatus);
    }

    [Fact]
    public async Task Standalone_DO_post_with_empty_SoNo_does_not_touch_SO()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 2m, price: 10m));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var post = await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)]);

        Assert.True(post.Succeeded, post.ErrorMessage);
        var soAfter = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.New, soAfter.Document!.Status);
        Assert.Equal(0m, soAfter.Document.Lines[0].ShippedQty);
        Assert.Equal(10m, soAfter.Document.Lines[0].BalanceQty);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.SaDoDetails.SingleAsync(x => x.DoNo == doSave.DoNo);
        Assert.Equal(string.Empty, detail.SoNo);
        Assert.Equal(0m, detail.SoConsumedQty);
    }

    [Fact]
    public async Task Duplicate_SO_lines_on_one_DO_consume_and_rollback_full_aggregate()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(new SaSoSaveRequest
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            Lines =
            [
                SoLine("SVC1", 6m, 10m),
                SoLine("SVC1", 4m, 10m)
            ]
        });
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var doSave = await doService.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                DoLine("SVC1", 6m, 10m, soSave.SoNo!, 1),
                DoLine("SVC1", 4m, 10m, soSave.SoNo!, 2)
            ]
        });
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);
        var afterPost = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.Shipped, afterPost.Document!.Status);
        Assert.Equal(SaDualStatuses.Full, afterPost.Document.FulfillmentStatus);
        Assert.Equal(SaDualStatuses.None, afterPost.Document.BillingStatus);
        Assert.Equal(6m, afterPost.Document.Lines.Single(x => x.Line == 1).ShippedQty);
        Assert.Equal(4m, afterPost.Document.Lines.Single(x => x.Line == 2).ShippedQty);

        Assert.True((await doService.RollbackAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);
        var afterRollback = await so.GetAsync(soSave.SoNo!);
        Assert.Equal(SaSoStatuses.New, afterRollback.Document!.Status);
        Assert.All(afterRollback.Document.Lines, line =>
        {
            Assert.Equal(0m, line.ShippedQty);
            Assert.Equal(line.OrderQty, line.BalanceQty);
        });
    }

    [Fact]
    public async Task SO_RowVersion_advances_on_consume_and_old_token_update_fails()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSoSut(numbering: numbering);
        var doService = CreateDoSut(numbering: numbering);

        var soSave = await so.SaveNewAsync(SoRequest(qty: 10m, price: 10m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);
        var stale = (byte[])soSave.Document!.RowVersion.Clone();

        var doSave = await doService.SaveNewAsync(DoRequest(qty: 6m, price: 10m, soNo: soSave.SoNo!, soLine: 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await doService.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var current = GetSoRowVersion(soSave.SoNo!);
        Assert.False(stale.SequenceEqual(current));

        var update = await so.UpdateAsync(
            soSave.SoNo!,
            SoRequest(qty: 11m, price: 10m, rowVersion: stale, line: 1));

        Assert.False(update.Succeeded);
        Assert.Equal(SaSoErrorKind.Concurrency, update.ErrorKind);
    }

    private byte[] GetSoRowVersion(string soNo)
    {
        using var db = _factory.CreateDbContext();
        return db.SaSos.AsNoTracking().Single(x => x.SoNo == soNo && x.IsCurrent).RowVersion ?? [];
    }

    private byte[] GetDoRowVersion(string doNo)
    {
        using var db = _factory.CreateDbContext();
        return db.SaDos.AsNoTracking().Single(x => x.DoNo == doNo).RowVersion ?? [];
    }

    private static SaSoSaveRequest SoRequest(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        string iCode = "SVC1",
        byte[]? rowVersion = null,
        int line = 0) =>
        new()
        {
            SoDate = FixedToday,
            CustCode = cust,
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines = [SoLine(iCode, qty, price, line)]
        };

    private static SaSoLineRequest SoLine(string iCode, decimal qty, decimal price, int line = 0) =>
        new()
        {
            Line = line,
            ICode = iCode,
            OrderQty = qty,
            UnitPrice = price
        };

    private static SaDoSaveRequest DoRequest(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        string iCode = "SVC1",
        string soNo = "",
        short? soLine = null) =>
        new()
        {
            DoDate = FixedToday,
            CustCode = cust,
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines = [DoLine(iCode, qty, price, soNo, soLine)]
        };

    private static SaDoLineRequest DoLine(
        string iCode,
        decimal qty,
        decimal price,
        string soNo = "",
        short? soLine = null) =>
        new()
        {
            ICode = iCode,
            Qty = qty,
            UnitPrice = price,
            SoNo = soNo,
            SoLine = soLine
        };

    private static SaInvoiceSaveRequest InvoiceRequest(
        decimal qty,
        decimal price,
        string cust = "CUST01",
        string iCode = "SVC1",
        string soNo = "",
        short? soLine = null,
        bool linkDo = false,
        string? doNo = null,
        short? doLine = null) =>
        new()
        {
            InvDate = FixedToday,
            CustCode = cust,
            Currency = "MYR",
            PayCode = "NET30",
            SalesmanCode = "SM1",
            InvName = cust == "CUST02" ? "Beta" : "Alpha",
            InvAddress1 = cust == "CUST02" ? "BETA ADDR" : "INV ADDR 1",
            InvCity = cust == "CUST02" ? "BETA CITY" : "INV CITY",
            InvPostalCode = cust == "CUST02" ? "50001" : "50000",
            InvCountry = "MY",
            InvTel = "123456",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = iCode,
                    Qty = qty,
                    UnitPrice = price,
                    SoNo = soNo,
                    SoLine = soLine,
                    LinkDo = linkDo,
                    DoNo = doNo,
                    DoLine = doLine
                }
            ]
        };

    private static SaSoKeyedRequest SoKeyed(string soNo, byte[] rowVersion) =>
        new() { SoNo = soNo, RowVersion = rowVersion };

    private static SaDoKeyedRequest DoKeyed(string doNo, byte[] rowVersion) =>
        new() { DoNo = doNo, RowVersion = rowVersion };

    private SaSoService CreateSoSut(
        IDocumentNumberingService? numbering = null,
        IInventoryTenantContext? tenant = null,
        Mock<IAccessRightService>? access = null)
    {
        tenant ??= InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        return new SaSoService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            numbering ?? new FakeSalesDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new SaSoRepository(),
            new SaCustRepository(_factory),
            new SaDocApplicationService(new SaSoRepository(), new SaDoRepository()),
            NullLogger<SaSoService>.Instance);
    }

    private SaDoService CreateDoSut(
        IDocumentNumberingService? numbering = null,
        IInventoryTenantContext? tenant = null,
        Mock<IAccessRightService>? access = null)
    {
        tenant ??= InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var docApplication = new SaDocApplicationService(salesOrders, new SaDoRepository());
        return new SaDoService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            numbering ?? new FakeSalesDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new SaDoRepository(),
            new SaCustRepository(_factory),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreatePosting(),
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            docApplication,
            new SaCustLookupService(_factory, tenant),
            NullLogger<SaDoService>.Instance);
    }

    private SaInvoiceService CreateInvoiceSut(
        IDocumentNumberingService? numbering = null,
        IInventoryTenantContext? tenant = null,
        Mock<IAccessRightService>? access = null)
    {
        tenant ??= InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var docApplication = new SaDocApplicationService(salesOrders, new SaDoRepository());
        return new SaInvoiceService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            new RunningNumberService(),
            numbering ?? new FakeSalesDocumentNumberingService(),
            new FixedCurrentDateService(FixedToday),
            new SaInvoiceRepository(),
            new SaCustRepository(_factory),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            CreatePosting(),
            new IvSpShipmentService(postingRepo, new IvStockTransactionRepository(), new RunningNumberService()),
            salesOrders,
            docApplication,
            new SaCustLookupService(_factory, tenant),
            NullLogger<SaInvoiceService>.Instance);
    }

    private IvInventoryPostingService CreatePosting() =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            Access().Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

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
