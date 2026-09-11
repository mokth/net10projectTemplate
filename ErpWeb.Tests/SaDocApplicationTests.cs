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

/// <summary>
/// Acceptance matrix for always-on SaDocApplication ledger (SO_DO / SO_INV / DO_INV).
/// </summary>
public class SaDocApplicationTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 2);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public SaDocApplicationTests()
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
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.IvMsCodes.Add(new IvMsCode { Code = "NET30", Name = "Net 30", CodeType = IvMsCodeTypes.PayCode });
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
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "SVC1",
            IDesc = "Service",
            IClassCode = "RAW",
            StdUom = "EA",
            SellingUom = "EA",
            StockControl = false,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSVC",
            Classification = "CLASS-S"
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "STK1",
            IDesc = "Stock item",
            IClassCode = "RAW",
            StdUom = "EA",
            SellingUom = "EA",
            StockControl = true,
            IsActive = true,
            SellingPrice = 10m,
            SellingGlCode = "GLSTK",
            Classification = "CLASS-K",
            DefWarehouse = "MAIN"
        });
        db.IvLocations.Add(new IvLocation
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.SaCusts.Add(new SaCust
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
            InvTel = "123",
            IsActive = true,
            RowVersion = [1, 0, 0, 0, 0, 0, 0, 0]
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SO_DO_exact_and_over_qty_and_split()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);

        var ok = await dos.SaveNewAsync(DoRequest(100m, soSave.SoNo!, 1));
        Assert.True(ok.Succeeded, ok.ErrorMessage);
        Assert.True((await dos.PostAsync([DoKeyed(ok.DoNo!, ok.Document!.RowVersion)])).Succeeded);
        await AssertProjectionOracleAsync(soSave.SoNo!);

        var so2 = await so.SaveNewAsync(SoRequest(100m));
        var over = await dos.SaveNewAsync(DoRequest(101m, so2.SoNo!, 1));
        Assert.False(over.Succeeded);
        Assert.Contains("remaining", over.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var so3 = await so.SaveNewAsync(SoRequest(100m));
        var a = await dos.SaveNewAsync(DoRequest(60m, so3.SoNo!, 1));
        var b = await dos.SaveNewAsync(DoRequest(40m, so3.SoNo!, 1));
        Assert.True(a.Succeeded, a.ErrorMessage);
        Assert.True(b.Succeeded, b.ErrorMessage);
        Assert.True((await dos.PostAsync([DoKeyed(a.DoNo!, a.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.PostAsync([DoKeyed(b.DoNo!, b.Document!.RowVersion)])).Succeeded);
        await AssertProjectionOracleAsync(so3.SoNo!);

        var so4 = await so.SaveNewAsync(SoRequest(100m));
        var c = await dos.SaveNewAsync(DoRequest(60m, so4.SoNo!, 1));
        Assert.True(c.Succeeded, c.ErrorMessage);
        var d = await dos.SaveNewAsync(DoRequest(50m, so4.SoNo!, 1));
        Assert.False(d.Succeeded);
        Assert.Contains("remaining", d.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.True((await dos.PostAsync([DoKeyed(c.DoNo!, c.Document!.RowVersion)])).Succeeded);
    }

    [Fact]
    public async Task SO_INV_and_DO_INV_caps_and_cross_path()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        var invOk = await inv.SaveNewAsync(InvoiceRequest(100m, soSave.SoNo!, 1));
        Assert.True(invOk.Succeeded, invOk.ErrorMessage);
        Assert.True((await inv.PostAsync([invOk.InvNo!])).Succeeded);
        await AssertProjectionOracleAsync(soSave.SoNo!);

        var soOver = await so.SaveNewAsync(SoRequest(100m));
        var invOver = await inv.SaveNewAsync(InvoiceRequest(101m, soOver.SoNo!, 1));
        Assert.False(invOver.Succeeded);
        Assert.Contains("remaining", invOver.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var soDo = await so.SaveNewAsync(SoRequest(100m));
        var doSave = await dos.SaveNewAsync(DoRequest(100m, soDo.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var first = await inv.SaveNewAsync(InvoiceFromDo(60m, soDo.SoNo!, doSave.DoNo!));
        var second = await inv.SaveNewAsync(InvoiceFromDo(40m, soDo.SoNo!, doSave.DoNo!));
        Assert.True((await inv.PostAsync([first.InvNo!])).Succeeded);
        Assert.True((await inv.PostAsync([second.InvNo!])).Succeeded);
        await AssertProjectionOracleAsync(soDo.SoNo!);

        var soCross = await so.SaveNewAsync(SoRequest(100m));
        var doCross = await dos.SaveNewAsync(DoRequest(40m, soCross.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doCross.DoNo!, doCross.Document!.RowVersion)])).Succeeded);
        var direct = await inv.SaveNewAsync(InvoiceRequest(60m, soCross.SoNo!, 1));
        Assert.True(direct.Succeeded, direct.ErrorMessage);
        Assert.True((await inv.PostAsync([direct.InvNo!])).Succeeded);
        var viaDo = await inv.SaveNewAsync(InvoiceFromDo(40m, soCross.SoNo!, doCross.DoNo!));
        Assert.True((await inv.PostAsync([viaDo.InvNo!])).Succeeded);
        await AssertProjectionOracleAsync(soCross.SoNo!);

        var soFail = await so.SaveNewAsync(SoRequest(100m));
        var doFail = await dos.SaveNewAsync(DoRequest(40m, soFail.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doFail.DoNo!, doFail.Document!.RowVersion)])).Succeeded);
        var direct60 = await inv.SaveNewAsync(InvoiceRequest(60m, soFail.SoNo!, 1));
        Assert.True((await inv.PostAsync([direct60.InvNo!])).Succeeded);
        var viaDo50 = await inv.SaveNewAsync(InvoiceFromDo(50m, soFail.SoNo!, doFail.DoNo!));
        Assert.True(viaDo50.Succeeded, viaDo50.ErrorMessage);
        var failPost = await inv.PostAsync([viaDo50.InvNo!]);
        Assert.False(failPost.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.OverAllocate, failPost.Posting[0].ReasonCode);

        var soLeftover = await so.SaveNewAsync(SoRequest(100m));
        var doLeftover = await dos.SaveNewAsync(DoRequest(40m, soLeftover.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doLeftover.DoNo!, doLeftover.Document!.RowVersion)])).Succeeded);
        var tooMuch = await inv.SaveNewAsync(InvoiceRequest(70m, soLeftover.SoNo!, 1));
        Assert.False(tooMuch.Succeeded);
        var ok60 = await inv.SaveNewAsync(InvoiceRequest(60m, soLeftover.SoNo!, 1));
        Assert.True(ok60.Succeeded, ok60.ErrorMessage);

        var soDraft = await so.SaveNewAsync(SoRequest(100m));
        var doDraft = await dos.SaveNewAsync(DoRequest(100m, soDraft.SoNo!, 1));
        Assert.True(doDraft.Succeeded, doDraft.ErrorMessage);
        var blockedInv = await inv.SaveNewAsync(InvoiceRequest(100m, soDraft.SoNo!, 1));
        Assert.False(blockedInv.Succeeded);
        Assert.True((await dos.DeleteAsync([DoKeyed(doDraft.DoNo!, doDraft.Document!.RowVersion)])).Succeeded);
        var afterDelete = await inv.SaveNewAsync(InvoiceRequest(100m, soDraft.SoNo!, 1));
        Assert.True(afterDelete.Succeeded, afterDelete.ErrorMessage);
        Assert.True((await inv.PostAsync([afterDelete.InvNo!])).Succeeded);
    }

    [Fact]
    public async Task Soft_reserve_DO_edit_exclude_and_NEW_to_POSTED()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        var doSave = await dos.SaveNewAsync(DoRequest(60m, soSave.SoNo!, 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var remaining = await so.GetRemainingLinesAsync(soSave.SoNo!);
        Assert.Equal(40m, Assert.Single(remaining.RemainingLines).BalanceQty);

        var remainingExclude = await so.GetRemainingLinesAsync(soSave.SoNo!, excludeDoNo: doSave.DoNo);
        Assert.Equal(100m, Assert.Single(remainingExclude.RemainingLines).BalanceQty);

        var down = await dos.UpdateAsync(
            doSave.DoNo!,
            DoRequest(30m, soSave.SoNo!, 1, rowVersion: doSave.Document!.RowVersion));
        Assert.True(down.Succeeded, down.ErrorMessage);
        Assert.Equal(70m, Assert.Single((await so.GetRemainingLinesAsync(soSave.SoNo!)).RemainingLines).BalanceQty);

        var up = await dos.UpdateAsync(
            doSave.DoNo!,
            DoRequest(70m, soSave.SoNo!, 1, rowVersion: down.Document!.RowVersion));
        Assert.True(up.Succeeded, up.ErrorMessage);
        Assert.Equal(30m, Assert.Single((await so.GetRemainingLinesAsync(soSave.SoNo!)).RemainingLines).BalanceQty);

        Assert.True((await dos.PostAsync([DoKeyed(up.DoNo!, up.Document!.RowVersion)])).Succeeded);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
            Assert.Equal(70m, detail.DeliveredQty);
            Assert.Equal(30m, detail.BalanceQty);
        }

        var afterPost = await so.GetRemainingLinesAsync(soSave.SoNo!);
        Assert.Equal(30m, Assert.Single(afterPost.RemainingLines).BalanceQty);
    }

    [Fact]
    public async Task Soft_reserve_multi_detail_same_SO_line_and_draft_invoice_blocks_DO()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        var multi = await dos.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                new SaDoLineRequest { ICode = "SVC1", Qty = 40m, UnitPrice = 10m, SoNo = soSave.SoNo, SoLine = 1 },
                new SaDoLineRequest { ICode = "SVC1", Qty = 50m, UnitPrice = 10m, SoNo = soSave.SoNo, SoLine = 1 }
            ]
        });
        Assert.True(multi.Succeeded, multi.ErrorMessage);
        Assert.Equal(10m, Assert.Single((await so.GetRemainingLinesAsync(soSave.SoNo!)).RemainingLines).BalanceQty);

        var overMulti = await dos.SaveNewAsync(new SaDoSaveRequest
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            Lines =
            [
                new SaDoLineRequest { ICode = "SVC1", Qty = 60m, UnitPrice = 10m, SoNo = soSave.SoNo, SoLine = 1 },
                new SaDoLineRequest { ICode = "SVC1", Qty = 50m, UnitPrice = 10m, SoNo = soSave.SoNo, SoLine = 1 }
            ]
        });
        Assert.False(overMulti.Succeeded);

        var so2 = await so.SaveNewAsync(SoRequest(100m));
        var draftInv = await inv.SaveNewAsync(InvoiceRequest(100m, so2.SoNo!, 1));
        Assert.True(draftInv.Succeeded, draftInv.ErrorMessage);
        var blockedDo = await dos.SaveNewAsync(DoRequest(100m, so2.SoNo!, 1));
        Assert.False(blockedDo.Succeeded);

        var so3 = await so.SaveNewAsync(SoRequest(100m));
        var inv60 = await inv.SaveNewAsync(InvoiceRequest(60m, so3.SoNo!, 1));
        Assert.True(inv60.Succeeded, inv60.ErrorMessage);
        var do40 = await dos.SaveNewAsync(DoRequest(40m, so3.SoNo!, 1));
        Assert.True(do40.Succeeded, do40.ErrorMessage);
    }

    [Fact]
    public async Task Soft_reserve_force_closed_DO_and_rollback_and_tenant_exclude()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        var doSave = await dos.SaveNewAsync(DoRequest(40m, soSave.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        var billable = await so.GetBillableLinesAsync(soSave.SoNo!);
        Assert.Equal(60m, Assert.Single(billable.RemainingLines).RemainingBillableQty);
        var blocked = await inv.SaveNewAsync(InvoiceRequest(70m, soSave.SoNo!, 1));
        Assert.False(blocked.Succeeded);

        var soRb = await so.SaveNewAsync(SoRequest(50m));
        var doRb = await dos.SaveNewAsync(DoRequest(50m, soRb.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doRb.DoNo!, doRb.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.RollbackAsync([DoKeyed(doRb.DoNo!, GetDoRowVersion(doRb.DoNo!))])).Succeeded);
        var afterRb = await so.GetRemainingLinesAsync(soRb.SoNo!);
        Assert.Empty(afterRb.RemainingLines);
        Assert.False((await inv.SaveNewAsync(InvoiceRequest(50m, soRb.SoNo!, 1))).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var sumsWrongExclude = await SaSoLineReserve.SumBySoLinesAsync(
            db,
            "DEMO",
            "HQ",
            [soRb.SoNo!],
            excludeDo: new SaSoLineReserve.DocIdentity("OTHER", "HQ", doRb.DoNo!),
            excludeInv: null);
        var lineSums = SaSoLineReserve.GetSums(sumsWrongExclude, soRb.SoNo!, soRb.Document!.CustRel, 1);
        Assert.Equal(50m, lineSums.NewDoQty);
        Assert.Equal(50m, lineSums.LiveDoQty);

        var sumsRightExclude = await SaSoLineReserve.SumBySoLinesAsync(
            db,
            "DEMO",
            "HQ",
            [soRb.SoNo!],
            excludeDo: new SaSoLineReserve.DocIdentity("DEMO", "HQ", doRb.DoNo!),
            excludeInv: null);
        Assert.Equal(0m, SaSoLineReserve.GetSums(sumsRightExclude, soRb.SoNo!, soRb.Document!.CustRel, 1).NewDoQty);
    }

    [Fact]
    public async Task Standalone_DO_LinkDo_invoice_posts_and_Uom_mismatch_fails()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soBaseline = await so.SaveNewAsync(SoRequest(10m));
        Assert.True(soBaseline.Succeeded, soBaseline.ErrorMessage);

        var standalone = await dos.SaveNewAsync(DoRequest(5m, soNo: "", soLine: null));
        Assert.True(standalone.Succeeded, standalone.ErrorMessage);
        Assert.True((await dos.PostAsync([DoKeyed(standalone.DoNo!, standalone.Document!.RowVersion)])).Succeeded);

        var link = await inv.SaveNewAsync(InvoiceFromDoStandalone(1m, standalone.DoNo!));
        Assert.True(link.Succeeded, link.ErrorMessage);
        var linkPost = await inv.PostAsync([link.InvNo!]);
        Assert.True(linkPost.Succeeded, string.Join("; ", linkPost.Posting.Select(x => $"{x.Outcome}:{x.ErrorMessage}")));

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var alloc = await db.SaDocApplications.SingleAsync(x =>
                x.TargetDocType == SaDocTypes.Inv && x.TargetDocId == link.InvNo);
            Assert.Equal(SaDocTypes.Do, alloc.SourceDocType);
            Assert.Equal(standalone.DoNo, alloc.SourceDocId);
            Assert.Equal(string.Empty, alloc.RelatedSoNo);
            Assert.Equal(0, alloc.RelatedSoLine);
            Assert.Equal(0, alloc.RelatedCustRel);

            var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == soBaseline.SoNo);
            Assert.Equal(0m, soLine.InvoicedQty);

            Assert.False(await db.IvTrxBatches.AnyAsync(x => x.RefNo == link.InvNo));

            var deliveryOrder = await db.SaDos.SingleAsync(x => x.DoNo == standalone.DoNo);
            Assert.Equal(SaDualStatuses.Partial, deliveryOrder.BillingStatus);
        }

        var soSave = await so.SaveNewAsync(SoRequest(10m));
        var mismatch = await dos.SaveNewAsync(DoRequest(1m, soSave.SoNo!, 1));
        Assert.True(mismatch.Succeeded, mismatch.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var detail = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
            detail.SellingUom = "BOX";
            await db.SaveChangesAsync();
        }

        var post = await dos.PostAsync([DoKeyed(mismatch.DoNo!, mismatch.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.UomMismatch, post.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task Pure_LinkDo_invoice_AddShipment_creates_no_batch()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 50m);

        var soSave = await so.SaveNewAsync(SoRequest(5m, iCode: "STK1"));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);
        var doSave = await dos.SaveNewAsync(DoRequest(5m, soSave.SoNo!, 1, iCode: "STK1"));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var doShip = await dos.AddShipmentAsync(doSave.DoNo!, false, doSave.Document!.RowVersion);
        Assert.True(doShip.Succeeded, doShip.ErrorMessage);
        var doReload = await dos.GetAsync(doSave.DoNo!);
        var doPost = await dos.PostAsync([DoKeyed(doSave.DoNo!, doReload.Document!.RowVersion)]);
        Assert.True(doPost.Succeeded, string.Join("; ", doPost.Posting.Select(x => $"{x.Outcome}:{x.ErrorMessage}")));

        var link = await inv.SaveNewAsync(InvoiceFromDo(5m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);

        var addShip = await inv.AddShipmentAsync(link.InvNo!, false, link.Document!.RowVersion);
        Assert.True(addShip.Succeeded, addShip.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.IvTrxBatches.AnyAsync(x => x.RefNo == link.InvNo));
    }

    [Fact]
    public async Task Mixed_invoice_LinkDo_plus_plain_does_not_double_deduct()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 50m);

        var soSave = await so.SaveNewAsync(SoRequest(5m, iCode: "STK1"));
        Assert.True(soSave.Succeeded, soSave.ErrorMessage);
        var doSave = await dos.SaveNewAsync(DoRequest(5m, soSave.SoNo!, 1, iCode: "STK1"));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await dos.AddShipmentAsync(doSave.DoNo!, false, doSave.Document!.RowVersion)).Succeeded);
        var doReload = await dos.GetAsync(doSave.DoNo!);
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doReload.Document!.RowVersion)])).Succeeded);

        var save = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "STK1", Qty = 5m, UnitPrice = 10m,
                    SoNo = soSave.SoNo, SoLine = 1,
                    LinkDo = true, DoNo = doSave.DoNo, DoLine = 1
                },
                new SaInvoiceLineRequest { ICode = "STK1", Qty = 3m, UnitPrice = 10m, FrWarehouse = "MAIN" }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var addShip = await inv.AddShipmentAsync(save.InvNo!, false, save.Document!.RowVersion);
        Assert.True(addShip.Succeeded, addShip.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync(x => x.RefNo == save.InvNo);
        var details = await db.IvTrxBatchDetails.Where(x => x.BatchId == batch.Id).ToListAsync();
        // Only the plain (non-LinkDo) line is shipped; the LinkDo line was already shipped on the DO.
        Assert.Equal(3m, details.Sum(x => x.FrStdQty ?? 0m));
        Assert.All(details, d => Assert.Equal((short?)2, d.SoLineNo));
    }

    [Fact]
    public async Task Shipment_layer_ignores_LinkDo_required_lines()
    {
        var postingRepo = new IvStockPostingRepository();
        var shipments = new IvSpShipmentService(
            postingRepo, new IvStockTransactionRepository(), new RunningNumberService());
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        _ = tenant;

        await using var db = await _factory.CreateDbContextAsync();
        var result = await shipments.CreateOrReplaceShipmentAsync(
            db,
            new IvSpCreateOrReplaceCommand
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                LocationCode = "SITE",
                UserId = "tester",
                DocumentNo = "INV-LINKDO",
                DocumentDate = FixedToday,
                RequiredLines =
                [
                    new IvSpRequiredLine
                    {
                        Line = 1, ICode = "STK1", StdQty = 5m, StdUom = "EA",
                        FrWarehouse = "MAIN", StockControl = true, LinkDo = true
                    }
                ]
            });

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(0, result.BatchId);
        Assert.False(await db.IvTrxBatches.AnyAsync(x => x.RefNo == "INV-LINKDO"));
    }

    private async Task SeedStockAsync(string iCode, decimal qty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.IvBalLocs.Add(new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = "MAIN",
            LocCode = "BIN1",
            LotNo = "",
            IStatus = "ACTIVE",
            StdQty = qty,
            StdUom = "EA",
            TransDate = FixedToday,
            LocationCode = "SITE"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Mixed_DO_SO_and_standalone_lines_invoice_updates_SO_only_for_bound()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(10m));
        var doSave = await dos.SaveNewAsync(MixedDoRequest(soSave.SoNo!, soQty: 10m, standaloneQty: 10m));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var billable = await dos.GetBillableLinesAsync("CUST01", "MYR");
        Assert.True(billable.Succeeded, billable.ErrorMessage);
        Assert.Equal(2, billable.BillableLines.Count);

        var invoice = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 10m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 10m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 2
                }
            ]
        });
        Assert.True(invoice.Succeeded, invoice.ErrorMessage);
        Assert.True((await inv.PostAsync([invoice.InvNo!])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var allocs = await db.SaDocApplications
            .Where(x => x.TargetDocType == SaDocTypes.Inv && x.TargetDocId == invoice.InvNo)
            .OrderBy(x => x.SourceLineId)
            .ToListAsync();
        Assert.Equal(2, allocs.Count);
        Assert.Equal(soSave.SoNo, allocs[0].RelatedSoNo);
        Assert.Equal(string.Empty, allocs[1].RelatedSoNo);

        var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
        Assert.Equal(10m, soLine.InvoicedQty);

        var deliveryOrder = await db.SaDos.SingleAsync(x => x.DoNo == doSave.DoNo);
        Assert.Equal(SaDualStatuses.Full, deliveryOrder.BillingStatus);
        Assert.Equal(SaDoStatuses.Posted, deliveryOrder.Status);
    }

    [Fact]
    public async Task Mixed_DO_partial_billing_across_two_invoices()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(10m));
        var doSave = await dos.SaveNewAsync(MixedDoRequest(soSave.SoNo!, soQty: 10m, standaloneQty: 10m));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var inv1 = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 4m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 6m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 2
                }
            ]
        });
        Assert.True(inv1.Succeeded, inv1.ErrorMessage);
        Assert.True((await inv.PostAsync([inv1.InvNo!])).Succeeded);

        var inv2 = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 6m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 4m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 2
                }
            ]
        });
        Assert.True(inv2.Succeeded, inv2.ErrorMessage);
        Assert.True((await inv.PostAsync([inv2.InvNo!])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
        Assert.Equal(10m, soLine.InvoicedQty);

        var doInvQty = await db.SaDocApplications
            .Where(x => x.SourceDocType == SaDocTypes.Do && x.SourceDocId == doSave.DoNo && x.TargetDocType == SaDocTypes.Inv)
            .SumAsync(x => x.AppliedQty);
        Assert.Equal(20m, doInvQty);
    }

    [Fact]
    public async Task Standalone_DO_partial_bill_and_over_allocate()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var doSave = await dos.SaveNewAsync(DoRequest(10m, soNo: "", soLine: null));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var first = await inv.SaveNewAsync(InvoiceFromDoStandalone(4m, doSave.DoNo!));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True((await inv.PostAsync([first.InvNo!])).Succeeded);

        var remaining = await dos.GetBillableLinesAsync("CUST01", "MYR");
        Assert.True(remaining.Succeeded);
        Assert.Equal(6m, remaining.BillableLines.Single(x => x.DoNo == doSave.DoNo).RemainingBillableQty);

        var second = await inv.SaveNewAsync(InvoiceFromDoStandalone(6m, doSave.DoNo!));
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.True((await inv.PostAsync([second.InvNo!])).Succeeded);

        var third = await inv.SaveNewAsync(InvoiceFromDoStandalone(1m, doSave.DoNo!));
        Assert.True(third.Succeeded, third.ErrorMessage);
        var fail = await inv.PostAsync([third.InvNo!]);
        Assert.False(fail.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.OverAllocate, fail.Posting[0].ReasonCode);
    }

    [Fact]
    public async Task Combined_SO_INV_and_DO_INV_cap_and_multi_DO_same_SO()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var so10 = await so.SaveNewAsync(SoRequest(10m));
        var do6 = await dos.SaveNewAsync(DoRequest(6m, so10.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(do6.DoNo!, do6.Document!.RowVersion)])).Succeeded);

        var soInv = await inv.SaveNewAsync(InvoiceRequest(4m, so10.SoNo!, 1));
        Assert.True((await inv.PostAsync([soInv.InvNo!])).Succeeded);
        var doInv = await inv.SaveNewAsync(InvoiceFromDo(6m, so10.SoNo!, do6.DoNo!));
        Assert.True((await inv.PostAsync([doInv.InvNo!])).Succeeded);

        var extraSo = await inv.SaveNewAsync(InvoiceRequest(1m, so10.SoNo!, 1));
        Assert.False(extraSo.Succeeded);
        var extraDo = await inv.SaveNewAsync(InvoiceFromDo(1m, so10.SoNo!, do6.DoNo!));
        Assert.True(extraDo.Succeeded, extraDo.ErrorMessage);
        var extraDoPost = await inv.PostAsync([extraDo.InvNo!]);
        Assert.False(extraDoPost.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.OverAllocate, extraDoPost.Posting[0].ReasonCode);

        var so20 = await so.SaveNewAsync(SoRequest(20m));
        var doA = await dos.SaveNewAsync(DoRequest(10m, so20.SoNo!, 1));
        var doB = await dos.SaveNewAsync(DoRequest(10m, so20.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doA.DoNo!, doA.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.PostAsync([DoKeyed(doB.DoNo!, doB.Document!.RowVersion)])).Succeeded);
        Assert.True((await inv.PostAsync([(await inv.SaveNewAsync(InvoiceFromDo(10m, so20.SoNo!, doA.DoNo!))).InvNo!])).Succeeded);
        Assert.True((await inv.PostAsync([(await inv.SaveNewAsync(InvoiceFromDo(10m, so20.SoNo!, doB.DoNo!))).InvNo!])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var soLine = await db.SaSoDetails.SingleAsync(x => x.SoNo == so20.SoNo);
            Assert.Equal(20m, soLine.InvoicedQty);
        }

        var overA = await inv.SaveNewAsync(InvoiceFromDo(1m, so20.SoNo!, doA.DoNo!));
        // SO is fully delivered + invoiced → CLOSED; prepare rejects before allocate.
        Assert.False(overA.Succeeded);
    }

    [Fact]
    public async Task Add_item_plus_standalone_DO_allowed_and_duplicate_DoLine_rejected()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var doSave = await dos.SaveNewAsync(DoRequest(5m, soNo: "", soLine: null));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var mixed = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 10m,
                    LinkDo = false
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 2m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                }
            ]
        });
        Assert.True(mixed.Succeeded, mixed.ErrorMessage);
        Assert.True((await inv.PostAsync([mixed.InvNo!])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var allocs = await db.SaDocApplications
                .Where(x => x.TargetDocType == SaDocTypes.Inv && x.TargetDocId == mixed.InvNo)
                .ToListAsync();
            Assert.Single(allocs);
            Assert.Equal(SaDocTypes.Do, allocs[0].SourceDocType);
            Assert.Equal(string.Empty, allocs[0].RelatedSoNo);
        }

        var dup = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 1m,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doSave.DoNo,
                    DoLine = 1
                }
            ]
        });
        Assert.False(dup.Succeeded);
        Assert.Equal(SaDocAllocationReasonCodes.Duplicate, dup.ErrorMessage);
    }

    [Fact]
    public async Task NEW_DO_absent_from_picker_CLOSED_DO_cannot_DO_INV()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var draft = await dos.SaveNewAsync(DoRequest(3m, soNo: "", soLine: null));
        Assert.True(draft.Succeeded, draft.ErrorMessage);
        var billableNew = await dos.GetBillableLinesAsync("CUST01", "MYR");
        Assert.True(billableNew.Succeeded);
        Assert.DoesNotContain(billableNew.BillableLines, x => x.DoNo == draft.DoNo);

        Assert.True((await dos.PostAsync([DoKeyed(draft.DoNo!, draft.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.ForceCloseAsync([DoKeyed(draft.DoNo!, GetDoRowVersion(draft.DoNo!))])).Succeeded);

        var closedInv = await inv.SaveNewAsync(InvoiceFromDoStandalone(1m, draft.DoNo!));
        Assert.False(closedInv.Succeeded);
        Assert.Contains(
            closedInv.ValidationErrors.Values,
            v => v.Contains("posted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rollback_standalone_LinkDo_clears_allocation_stock_stays_out()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var doSave = await dos.SaveNewAsync(DoRequest(5m, soNo: "", soLine: null));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var invoice = await inv.SaveNewAsync(InvoiceFromDoStandalone(5m, doSave.DoNo!));
        Assert.True((await inv.PostAsync([invoice.InvNo!])).Succeeded);

        Assert.True((await inv.RollbackAsync([invoice.InvNo!])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.False(await db.SaDocApplications.AnyAsync(x =>
            x.TargetDocType == SaDocTypes.Inv && x.TargetDocId == invoice.InvNo));
        var deliveryOrder = await db.SaDos.SingleAsync(x => x.DoNo == doSave.DoNo);
        Assert.Equal(SaDualStatuses.None, deliveryOrder.BillingStatus);
        Assert.Equal(SaDoStatuses.Posted, deliveryOrder.Status);
    }

    [Fact]
    public async Task OrderQty_floor_and_delete_allocated_SO_fail()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(10m));
        var doSave = await dos.SaveNewAsync(DoRequest(4m, soSave.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)])).Succeeded);

        var tooLow = await so.UpdateAsync(
            soSave.SoNo!,
            SoRequest(3m, rowVersion: GetSoRowVersion(soSave.SoNo!), line: 1));
        Assert.False(tooLow.Succeeded);

        var invSave = await inv.SaveNewAsync(InvoiceRequest(2m, soSave.SoNo!, 1));
        Assert.True((await inv.PostAsync([invSave.InvNo!])).Succeeded);

        var delete = await so.DeleteAsync([new SaSoKeyedRequest
        {
            SoNo = soSave.SoNo!,
            RowVersion = GetSoRowVersion(soSave.SoNo!)
        }]);
        Assert.False(delete.Succeeded);

        var fresh = await so.SaveNewAsync(SoRequest(5m));
        Assert.True((await so.DeleteAsync([new SaSoKeyedRequest
        {
            SoNo = fresh.SoNo!,
            RowVersion = GetSoRowVersion(fresh.SoNo!)
        }])).Succeeded);
    }

    [Fact]
    public async Task Invoice_rollback_restores_projections_and_statuses()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(100m));
        var d1 = await dos.SaveNewAsync(DoRequest(40m, soSave.SoNo!, 1));
        var d2 = await dos.SaveNewAsync(DoRequest(30m, soSave.SoNo!, 1));
        var d3 = await dos.SaveNewAsync(DoRequest(20m, soSave.SoNo!, 1));
        Assert.True((await dos.PostAsync([DoKeyed(d1.DoNo!, d1.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.PostAsync([DoKeyed(d2.DoNo!, d2.Document!.RowVersion)])).Succeeded);
        Assert.True((await dos.PostAsync([DoKeyed(d3.DoNo!, d3.Document!.RowVersion)])).Succeeded);

        var invoice = await inv.SaveNewAsync(new SaInvoiceSaveRequest
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 40m,
                    UnitPrice = 10m,
                    SoNo = soSave.SoNo,
                    SoLine = 1,
                    LinkDo = true,
                    DoNo = d1.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 30m,
                    UnitPrice = 10m,
                    SoNo = soSave.SoNo,
                    SoLine = 1,
                    LinkDo = true,
                    DoNo = d2.DoNo,
                    DoLine = 1
                },
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = 20m,
                    UnitPrice = 10m,
                    SoNo = soSave.SoNo,
                    SoLine = 1,
                    LinkDo = true,
                    DoNo = d3.DoNo,
                    DoLine = 1
                }
            ]
        });
        Assert.True(invoice.Succeeded, invoice.ErrorMessage);
        Assert.True((await inv.PostAsync([invoice.InvNo!])).Succeeded);
        await AssertProjectionOracleAsync(soSave.SoNo!);

        Assert.True((await inv.RollbackAsync([invoice.InvNo!])).Succeeded);
        await AssertProjectionOracleAsync(soSave.SoNo!);

        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.SaSos.SingleAsync(x => x.SoNo == soSave.SoNo);
        var line = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo);
        Assert.Equal(90m, line.DeliveredQty);
        Assert.Equal(0m, line.InvoicedQty);
        Assert.Equal(SaSoStatuses.Shipped, header.Status);
        Assert.Equal(SaDualStatuses.Partial, header.FulfillmentStatus);
        Assert.Equal(SaDualStatuses.None, header.BillingStatus);
        Assert.Equal(0, await db.SaDocApplications.CountAsync(x => x.TargetDocId == invoice.InvNo));
    }

    [Fact]
    public async Task Authz_denies_post_without_permission()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var denied = CreateDo(numbering, access: Deny(PermissionCodes.Post));

        var soSave = await so.SaveNewAsync(SoRequest(5m));
        var doSave = await CreateDo(numbering).SaveNewAsync(DoRequest(5m, soSave.SoNo!, 1));
        Assert.True(doSave.Succeeded, doSave.ErrorMessage);

        var post = await denied.PostAsync([DoKeyed(doSave.DoNo!, doSave.Document!.RowVersion)]);
        Assert.False(post.Succeeded);
        Assert.Equal(SaDoErrorKind.Authorization, post.ErrorKind);
    }

    // ────────────────────────────── R3 write-off (force-close) ──────────────────────────────

    [Fact]
    public async Task ForceClose_full_do_writes_off_full_qty()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(10m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 10m);

        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
        Assert.Equal(10m, line.WrittenOffQty);

        var header = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.IsCurrent);
        Assert.Equal(SaDualStatuses.WrittenOff, header.BillingStatus);
        Assert.Equal(SaSoStatuses.Closed, header.Status);
        Assert.Equal(SaSoClosedReasons.FullyConsumed, header.ClosedReason);

        await AssertBillableIdentityAsync(soSave.SoNo!, 10m);
    }

    [Fact]
    public async Task ForceClose_partially_billed_do_writes_off_remainder()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 100m);

        // Bill 60 of the 100 through the DO.
        var link = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);
        var linkPost = await inv.PostAsync([link.InvNo!]);
        Assert.True(linkPost.Succeeded, string.Join("; ", linkPost.Posting.Select(x => x.ErrorMessage)));

        // Partial billing is the normal case, not an error: the unallocated 40 is written off.
        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
        Assert.Equal(40m, line.WrittenOffQty);
        Assert.Equal(60m, line.InvoicedQty);

        var header = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.IsCurrent);
        Assert.Equal(SaDualStatuses.WrittenOff, header.BillingStatus);

        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);
    }

    [Fact]
    public async Task ForceClose_fully_billed_do_writes_off_nothing()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(10m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 10m);

        var link = await inv.SaveNewAsync(InvoiceFromDo(10m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);
        Assert.True((await inv.PostAsync([link.InvNo!])).Succeeded);

        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
        Assert.Equal(0m, line.WrittenOffQty);

        var header = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.IsCurrent);
        Assert.Equal(SaDualStatuses.Full, header.BillingStatus);

        await AssertBillableIdentityAsync(soSave.SoNo!, 10m);
    }

    [Fact]
    public async Task ForceClose_one_of_two_dos_does_not_write_off_the_other()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var do1 = await PostDoAsync(dos, soSave.SoNo!, 40m);
        var do2 = await PostDoAsync(dos, soSave.SoNo!, 60m);

        Assert.True((await dos.ForceCloseAsync([DoKeyed(do1.DoNo!, GetDoRowVersion(do1.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
            Assert.Equal(40m, line.WrittenOffQty);
            // DO2 remains POSTED and billable — its 60 must not have been written off.
            var do2Header = await db.SaDos.AsNoTracking().SingleAsync(x => x.DoNo == do2.DoNo);
            Assert.Equal(SaDoStatuses.Posted, do2Header.Status);
        }

        // Direct SO invoicing is fully blocked (LiveDoQty includes the CLOSED DO by design), but DO2
        // remains billable through the DO path — its 60 must not have been written off.
        var direct = await so.GetBillableLinesAsync(soSave.SoNo!);
        Assert.Empty(direct.RemainingLines);

        var doBillable = await dos.GetBillableLinesAsync("CUST01", "MYR");
        Assert.True(doBillable.Succeeded, doBillable.ErrorMessage);
        var do2Line = Assert.Single(doBillable.BillableLines, x => x.DoNo == do2.DoNo);
        Assert.Equal(60m, do2Line.RemainingBillableQty);

        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);
    }

    [Fact]
    public async Task ForceClose_is_irreversible()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(10m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 10m);
        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        var rv = GetDoRowVersion(doSave.DoNo!);

        // Repeat force-close fails deterministically (no silent no-op) so a double-submit surfaces.
        Assert.False((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, rv)])).Succeeded);
        // Rollback requires POSTED.
        Assert.False((await dos.RollbackAsync([DoKeyed(doSave.DoNo!, rv)])).Succeeded);
        // Delete requires NEW.
        Assert.False((await dos.DeleteAsync([DoKeyed(doSave.DoNo!, rv)])).Succeeded);
        // Re-post requires NEW.
        Assert.False((await dos.PostAsync([DoKeyed(doSave.DoNo!, rv)])).Succeeded);
        // Edit requires NEW.
        Assert.False((await dos.UpdateAsync(
            doSave.DoNo!,
            DoRequest(10m, soSave.SoNo!, 1, rowVersion: rv, iCode: "STK1"))).Succeeded);
    }

    [Fact]
    public async Task So_revision_blocked_when_written_off()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);

        // SO of 100, but only 40 delivered and then force-closed: the SO stays SHIPPED (not CLOSED),
        // so the D15 usage guard — not the status gate — is what must block the revision.
        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 40m);

        // Baseline: an unused NEW revision is revisable.
        var unusedSo = await so.SaveNewAsync(SoRequest(5m, iCode: "STK1"));
        Assert.True((await so.ReviseAsync(
            unusedSo.SoNo!,
            SoRequest(6m, rowVersion: GetSoRowVersion(unusedSo.SoNo!), line: 1, iCode: "STK1"))).Succeeded);

        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.SaSos.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.IsCurrent);
            Assert.Equal(SaSoStatuses.Shipped, header.Status);
            var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
            Assert.Equal(40m, line.WrittenOffQty);
        }

        // D15 — the write-off must survive a revision/delete attempt. A write-off can only arise from a
        // posted DO, which already makes the SO non-NEW, so the status gate blocks these today; the
        // SaSoRevisionUsage guard is the defence-in-depth layer (unit-tested in SaSoRevisionUsageTests).
        var revise = await so.ReviseAsync(
            soSave.SoNo!,
            SoRequest(101m, rowVersion: GetSoRowVersion(soSave.SoNo!), line: 1, iCode: "STK1"));
        Assert.False(revise.Succeeded);

        var delete = await so.DeleteAsync(
            [new SaSoKeyedRequest { SoNo = soSave.SoNo!, CustRel = 1, RowVersion = GetSoRowVersion(soSave.SoNo!) }]);
        Assert.False(delete.Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
            Assert.Equal(40m, line.WrittenOffQty);
        }
    }

    /// <summary>
    /// §3.3: the billable accounting identity must hold for the SO line at every lifecycle step.
    /// </summary>
    [Fact]
    public async Task Billable_identity_holds_at_every_step()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 100m);

        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);                    // fresh

        var draftDo = await dos.SaveNewAsync(DoRequest(100m, soSave.SoNo!, 1, iCode: "STK1"));
        Assert.True(draftDo.Succeeded, draftDo.ErrorMessage);
        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);                    // draft DO reservation

        Assert.True((await dos.AddShipmentAsync(draftDo.DoNo!, false, draftDo.Document!.RowVersion)).Succeeded);
        var reload = await dos.GetAsync(draftDo.DoNo!);
        Assert.True((await dos.PostAsync([DoKeyed(draftDo.DoNo!, reload.Document!.RowVersion)])).Succeeded);
        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);                    // posted, nothing billed

        var link60 = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, draftDo.DoNo!, iCode: "STK1"));
        Assert.True(link60.Succeeded, link60.ErrorMessage);
        Assert.True((await inv.PostAsync([link60.InvNo!])).Succeeded);
        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);                    // 60 billed through the DO

        Assert.True((await dos.ForceCloseAsync([DoKeyed(draftDo.DoNo!, GetDoRowVersion(draftDo.DoNo!))])).Succeeded);
        await AssertBillableIdentityAsync(soSave.SoNo!, 100m);                    // 40 written off
    }

    // ────────────────────────────── E8 allocation reconciliation ──────────────────────────────

    [Fact]
    public async Task Allocation_reconciliation_seeded_has_no_findings()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 100m);

        var link = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);
        Assert.True((await inv.PostAsync([link.InvNo!])).Succeeded);
        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        var result = await CreateReconciler().ReconcileAsync(soSave.SoNo!);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal("OK", result.Status);
        Assert.True(
            result.Findings.Count == 0,
            string.Join(" | ", result.Findings.Select(f => $"{f.Code}/{f.Severity}: {f.Explanation}")));
    }

    [Fact]
    public async Task Reconciliation_reports_no_findings_at_every_lifecycle_step()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);
        var reconcile = CreateReconciler();

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);                    // fresh

        var draftDo = await dos.SaveNewAsync(DoRequest(100m, soSave.SoNo!, 1, iCode: "STK1"));
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);                    // draft DO

        Assert.True((await dos.AddShipmentAsync(draftDo.DoNo!, false, draftDo.Document!.RowVersion)).Succeeded);
        var reloadDraft = await dos.GetAsync(draftDo.DoNo!);
        Assert.True((await dos.PostAsync([DoKeyed(draftDo.DoNo!, reloadDraft.Document!.RowVersion)])).Succeeded);
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);                    // posted

        var link60 = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, draftDo.DoNo!, iCode: "STK1"));
        Assert.True(link60.Succeeded, link60.ErrorMessage);
        Assert.True((await inv.PostAsync([link60.InvNo!])).Succeeded);
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);                    // partially billed

        Assert.True((await dos.ForceCloseAsync([DoKeyed(draftDo.DoNo!, GetDoRowVersion(draftDo.DoNo!))])).Succeeded);
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);                    // written off
    }

    [Fact]
    public async Task WrittenOffQty_reconciles_to_force_closed_do_contributions()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);
        var reconcile = CreateReconciler();

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 100m);

        var link = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);
        Assert.True((await inv.PostAsync([link.InvNo!])).Succeeded);
        Assert.True((await dos.ForceCloseAsync([DoKeyed(doSave.DoNo!, GetDoRowVersion(doSave.DoNo!))])).Succeeded);

        // Clean state: no reconciliation findings at all.
        await AssertNoFindingsAsync(reconcile, soSave.SoNo!);

        // Corrupt the denormalised column: the mismatch must be detected and must not be silently absorbed.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
            line.WrittenOffQty = 35m;
            await db.SaveChangesAsync();
        }

        var mismatched = await reconcile.ReconcileAsync(soSave.SoNo!);
        var finding = Assert.Single(mismatched.Findings, f => f.Code == SaAllocationFindingCodes.WrittenOffQtyMismatch);
        Assert.Equal(SaAllocationSeverities.Warning, finding.Severity);
        Assert.Equal(40m, finding.Expected);
        Assert.Equal(35m, finding.Actual);
    }

    [Fact]
    public async Task Reconciliation_flags_write_off_without_audit()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);

        var soSave = await so.SaveNewAsync(SoRequest(10m, iCode: "STK1"));

        // A write-off with no force-closed DO as its origin — the stamp/guards in §5.4 failed.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var line = await db.SaSoDetails.SingleAsync(x => x.SoNo == soSave.SoNo && x.CustRel == 1);
            line.WrittenOffQty = 10m;
            await db.SaveChangesAsync();
        }

        var result = await CreateReconciler().ReconcileAsync(soSave.SoNo!);
        Assert.Contains(result.Findings, f => f.Code == SaAllocationFindingCodes.WrittenOffNoAudit);
        // A write-off with no originating DO is doubly wrong: nothing blocks those units, so the
        // §3.3 identity fails too and is (correctly) reported as an ERROR.
        Assert.Contains(result.Findings, f => f.Code == SaAllocationFindingCodes.BillableIdentityMismatch);
    }

    private static async Task AssertNoFindingsAsync(SaAllocationReconciliationService reconcile, string soNo)
    {
        var result = await reconcile.ReconcileAsync(soNo);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(
            result.Findings.Count == 0,
            $"{soNo}: " + string.Join(" | ", result.Findings.Select(f => $"{f.Code}/{f.Severity}: {f.Explanation}")));
    }

    private SaAllocationReconciliationService CreateReconciler() =>
        new(_factory, InventoryTenantTestHelper.CreateTenantContext(location: "SITE"));

    // ────────────────────────────── E3 document flow ──────────────────────────────

    [Fact]
    public void Doc_flow_types_normalize()
    {
        Assert.Equal("SO", SaDocFlowTypes.Normalize("so"));
        Assert.Equal("DO", SaDocFlowTypes.Normalize(" DeliveryOrder "));
        Assert.Equal("INV", SaDocFlowTypes.Normalize("invoice"));
        Assert.Equal("CN", SaDocFlowTypes.Normalize("dn"));
        Assert.Equal(string.Empty, SaDocFlowTypes.Normalize(null));
    }

    [Fact]
    public async Task Doc_flow_query_walks_SO_DO_INV_and_CN()
    {
        var numbering = new FakeSalesDocumentNumberingService();
        var so = CreateSo(numbering);
        var dos = CreateDo(numbering);
        var inv = CreateInvoice(numbering);

        await SeedStockAsync("STK1", 100m);
        var soSave = await so.SaveNewAsync(SoRequest(100m, iCode: "STK1"));
        var doSave = await PostDoAsync(dos, soSave.SoNo!, 100m);

        var link = await inv.SaveNewAsync(InvoiceFromDo(60m, soSave.SoNo!, doSave.DoNo!, iCode: "STK1"));
        Assert.True(link.Succeeded, link.ErrorMessage);
        Assert.True((await inv.PostAsync([link.InvNo!])).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.SaCdns.Add(new SaCdn
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                DocNo = "CN0001",
                DocDate = FixedToday,
                Status = ErpWeb.Core.Sales.SaCdnStatuses.New,
                Type = ErpWeb.Core.Sales.SaCdnTypes.CreditNote,
                CustCode = "CUST01",
                InvNo = link.InvNo
            });
            await db.SaveChangesAsync();
        }

        var flow = CreateFlowQuery();

        var soFlow = await flow.QueryAsync("SO", soSave.SoNo!);
        Assert.Equal("SO", soFlow.DocType);
        Assert.Contains(soFlow.Downstream, x => x.DocType == "DO" && x.DocNo == doSave.DoNo);
        Assert.Contains(soFlow.Downstream, x => x.Relationship == "SO → DO");
        Assert.Equal("POSTED", soFlow.Downstream.First(x => x.DocType == "DO").Status);

        var doFlow = await flow.QueryAsync("DO", doSave.DoNo!);
        Assert.Contains(doFlow.Upstream, x => x.DocType == "SO" && x.DocNo == soSave.SoNo);
        Assert.Contains(doFlow.Downstream, x => x.DocType == "INV" && x.DocNo == link.InvNo);

        var invFlow = await flow.QueryAsync("INV", link.InvNo!);
        Assert.Contains(invFlow.Upstream, x => x.DocType == "DO" && x.DocNo == doSave.DoNo);
        Assert.Contains(invFlow.Downstream, x => x.DocType == "CN" && x.DocNo == "CN0001");
        Assert.Contains(invFlow.Downstream, x => x.Relationship == "INV → CN (NEW)");

        var cnFlow = await flow.QueryAsync("CN", "CN0001");
        Assert.Contains(cnFlow.Upstream, x => x.DocType == "INV" && x.DocNo == link.InvNo);

        // Unknown document → empty flow, never a throw.
        var missing = await flow.QueryAsync("INV", "NOPE");
        Assert.False(missing.HasAny);
    }

    private SaDocFlowQuery CreateFlowQuery() =>
        new(_factory, InventoryTenantTestHelper.CreateTenantContext(location: "SITE"));

    private async Task AssertBillableIdentityAsync(string soNo, decimal expectedOrderQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.SaSoDetails.AsNoTracking().SingleAsync(x => x.SoNo == soNo && x.CustRel == 1);
        var map = await SaSoLineReserve.SumBySoLinesAsync(db, "DEMO", "HQ", [soNo]);
        var sums = SaSoLineReserve.GetSums(map, soNo, line.CustRel, line.Line);
        var eval = SaSoLineReserve.Evaluate(
            soNo, line.Line, line.OrderQty, line.DeliveredQty, sums, thisDoQty: 0m, thisSoInvQty: 0m);

        Assert.Equal(expectedOrderQty, eval.OrderQty);
        Assert.True(
            eval.BillableIdentityHolds,
            $"§3.3 identity broken for {soNo}: invoiced={eval.InvoicedQty} writtenOff={eval.WrittenOffQty} "
            + $"postedDoOpen={eval.PostedDoOpenQty} newDo={eval.NewDoQty} newSoInv={eval.NewSoInvQty} "
            + $"remainingBillable={eval.RemainingBillable} total={eval.BillableIdentityTotal} order={eval.OrderQty}");
    }

    /// <summary>Creates, ships and posts a DO of <paramref name="qty"/> for the STK1 line of an SO.</summary>
    private async Task<SaDoOperationResult> PostDoAsync(SaDoService dos, string soNo, decimal qty)
    {
        var save = await dos.SaveNewAsync(DoRequest(qty, soNo, 1, iCode: "STK1"));
        Assert.True(save.Succeeded, save.ErrorMessage);
        var ship = await dos.AddShipmentAsync(save.DoNo!, false, save.Document!.RowVersion);
        Assert.True(ship.Succeeded, ship.ErrorMessage);
        var reload = await dos.GetAsync(save.DoNo!);
        var post = await dos.PostAsync([DoKeyed(save.DoNo!, reload.Document!.RowVersion)]);
        Assert.True(post.Succeeded, string.Join("; ", post.Posting.Select(x => x.ErrorMessage)));
        return save;
    }

    private async Task AssertProjectionOracleAsync(string soNo)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.SaSoDetails.SingleAsync(x => x.SoNo == soNo && x.CustRel == 1);
        var delivered = await db.SaDocApplications
            .Where(x =>
                x.SourceDocType == SaDocTypes.So
                && x.SourceDocId == soNo
                && x.SourceLineId == detail.Line
                && x.TargetDocType == SaDocTypes.Do)
            .SumAsync(x => (decimal?)x.AppliedQty) ?? 0m;
        var invoicedDirect = await db.SaDocApplications
            .Where(x =>
                x.SourceDocType == SaDocTypes.So
                && x.SourceDocId == soNo
                && x.SourceLineId == detail.Line
                && x.TargetDocType == SaDocTypes.Inv)
            .SumAsync(x => (decimal?)x.AppliedQty) ?? 0m;
        var invoicedViaDo = await db.SaDocApplications
            .Where(x =>
                x.SourceDocType == SaDocTypes.Do
                && x.TargetDocType == SaDocTypes.Inv
                && x.RelatedSoNo == soNo
                && x.RelatedSoLine == detail.Line)
            .SumAsync(x => (decimal?)x.AppliedQty) ?? 0m;

        Assert.Equal(delivered, detail.DeliveredQty);
        Assert.Equal(invoicedDirect + invoicedViaDo, detail.InvoicedQty);
        Assert.Equal(detail.DeliveredQty, detail.ShippedQty);
        Assert.Equal(detail.OrderQty - detail.DeliveredQty, detail.BalanceQty);
    }

    private byte[] GetSoRowVersion(string soNo)
    {
        using var db = _factory.CreateDbContext();
        return db.SaSos.AsNoTracking().Single(x => x.SoNo == soNo).RowVersion ?? [];
    }

    private byte[] GetDoRowVersion(string doNo)
    {
        using var db = _factory.CreateDbContext();
        return db.SaDos.AsNoTracking().Single(x => x.DoNo == doNo).RowVersion ?? [];
    }

    private static SaSoSaveRequest SoRequest(decimal qty, byte[]? rowVersion = null, int line = 0, string iCode = "SVC1") =>
        new()
        {
            SoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            CustPo = "PO-1",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines =
            [
                new SaSoLineRequest { Line = line, ICode = iCode, OrderQty = qty, UnitPrice = 10m }
            ]
        };

    private static SaDoSaveRequest DoRequest(
        decimal qty,
        string soNo = "",
        short? soLine = null,
        byte[]? rowVersion = null,
        string iCode = "SVC1") =>
        new()
        {
            DoDate = FixedToday,
            CustCode = "CUST01",
            Currency = "MYR",
            PayCode = "NET30",
            SalesRep = "SM1",
            RowVersion = rowVersion,
            Lines =
            [
                new SaDoLineRequest
                {
                    ICode = iCode,
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = soLine
                }
            ]
        };

    private static SaInvoiceSaveRequest InvoiceRequest(decimal qty, string soNo, short soLine) =>
        new()
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = soLine
                }
            ]
        };

    private static SaInvoiceSaveRequest InvoiceFromDo(decimal qty, string soNo, string doNo, string iCode = "SVC1") =>
        new()
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = iCode,
                    Qty = qty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = 1,
                    LinkDo = true,
                    DoNo = doNo,
                    DoLine = 1
                }
            ]
        };

    private static SaInvoiceSaveRequest InvoiceFromDoStandalone(decimal qty, string doNo) =>
        new()
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
            InvTel = "123",
            Lines =
            [
                new SaInvoiceLineRequest
                {
                    ICode = "SVC1",
                    Qty = qty,
                    UnitPrice = 10m,
                    LinkDo = true,
                    DoNo = doNo,
                    DoLine = 1
                }
            ]
        };

    private static SaDoSaveRequest MixedDoRequest(string soNo, decimal soQty, decimal standaloneQty) =>
        new()
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
                    Qty = soQty,
                    UnitPrice = 10m,
                    SoNo = soNo,
                    SoLine = 1
                },
                new SaDoLineRequest
                {
                    ICode = "SVC1",
                    Qty = standaloneQty,
                    UnitPrice = 10m,
                    SoNo = "",
                    SoLine = null
                }
            ]
        };

    private static SaDoKeyedRequest DoKeyed(string doNo, byte[] rowVersion) =>
        new() { DoNo = doNo, RowVersion = rowVersion };

    private SaSoService CreateSo(IDocumentNumberingService numbering) =>
        new(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: "SITE"),
            Access().Object,
            numbering,
            new FixedCurrentDateService(FixedToday),
            new SaSoRepository(),
            new SaCustRepository(_factory),
            new SaDocApplicationService(new SaSoRepository(), new SaDoRepository()),
            NullLogger<SaSoService>.Instance);

    private SaDoService CreateDo(
        IDocumentNumberingService numbering,
        Mock<IAccessRightService>? access = null)
    {
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        return new SaDoService(
            _factory,
            tenant,
            (access ?? Access()).Object,
            numbering,
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
            new SaDocApplicationService(salesOrders, new SaDoRepository()),
            new SaCustLookupService(_factory, tenant),
            NullLogger<SaDoService>.Instance);
    }

    private SaInvoiceService CreateInvoice(IDocumentNumberingService numbering)
    {
        var postingRepo = new IvStockPostingRepository();
        var salesOrders = new SaSoRepository();
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "SITE");
        return new SaInvoiceService(
            _factory,
            tenant,
            Access().Object,
            new RunningNumberService(),
            numbering,
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
            new SaDocApplicationService(salesOrders, new SaDoRepository()),
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

    private static Mock<IAccessRightService> Deny(string permission)
    {
        var access = Access();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), permission, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        return access;
    }
}
