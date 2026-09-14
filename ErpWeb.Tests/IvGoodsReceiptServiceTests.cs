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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace ErpWeb.Tests;

public class IvGoodsReceiptServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 9, 12);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly FakePoOrderDocumentNumberingService _poNumbering = new();

    public IvGoodsReceiptServiceTests()
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
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.SaCurrencies.Add(new SaCurrency { CompanyCode = "DEMO", CurrCode = "MYR", IsActive = true });
        db.SaTaxGroups.Add(new SaTaxGroup
        {
            CompanyCode = "DEMO",
            TaxGrCode = "SR",
            Percentage = 6m,
            TaxGlCode = "GLTAX"
        });
        db.IvStockMasters.Add(new IvStockMaster
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
            StockControl = true,
            LotControl = false,
            IsActive = true,
            DefWarehouse = "MAIN"
        });
        db.PoSuppliers.Add(new PoSupplier
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            SuppCode = "SUP01",
            SuppName = "Alpha Supplier",
            Currency = "MYR",
            GlCode = "AP001",
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
            UnitPrice = 25m,
            Tolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task SaveNew_draft_does_not_change_PoOrderDetail_RecvQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(0m, detail.RecvQty);
        Assert.Equal(10m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.New, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
    }

    [Fact]
    public async Task Post_updates_RecvQty_and_status()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var post = await sut.PostAsync([save.BatchNo]);
        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.Equal(1, post.SucceededCount);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(4m, detail.RecvQty);
        Assert.Equal(6m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.Received, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
        Assert.Equal(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Double_post_forbidden()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 2m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);

        var second = await sut.PostAsync([save.BatchNo]);
        Assert.False(second.Succeeded);
        Assert.Equal(0, second.SucceededCount);
        Assert.Equal(1, second.FailedCount);
    }

    [Fact]
    public async Task Rollback_returns_batch_to_NEW_and_reverses_RecvQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);

        var rollback = await sut.RollbackAsync([save.BatchNo]);
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var detail = await db.PoOrderDetails.SingleAsync(x => x.PoNo == po.PoNo);
        Assert.Equal(0m, detail.RecvQty);
        Assert.Equal(10m, detail.BalanceQty);
        Assert.Equal(PoOrderStatuses.New, (await db.PoOrders.SingleAsync(x => x.PoNo == po.PoNo)).Status);
        Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync(x => x.BatchNo == save.BatchNo)).BatchStatus);
    }

    [Fact]
    public async Task Double_rollback_forbidden()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(GrRequest(po, recvQty: 2m));
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.True((await sut.PostAsync([save.BatchNo])).Succeeded);
        Assert.True((await sut.RollbackAsync([save.BatchNo])).Succeeded);

        var second = await sut.RollbackAsync([save.BatchNo]);
        Assert.False(second.Succeeded);
        Assert.Equal(0, second.SucceededCount);
        Assert.Equal(1, second.FailedCount);
    }

    [Fact]
    public async Task SearchPoLines_subtracts_NEW_draft_ToPurQty()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();

        var before = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(before.Succeeded, before.ErrorMessage);
        var beforeRow = Assert.Single(before.PoLines);
        Assert.Equal(10m, beforeRow.BalanceQty);
        Assert.Equal(0m, beforeRow.DraftQty);
        Assert.Equal(10m, beforeRow.AvailableQty);

        var draft = await sut.SaveNewAsync(GrRequest(po, recvQty: 4m));
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        var during = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(during.Succeeded, during.ErrorMessage);
        var duringRow = Assert.Single(during.PoLines);
        Assert.Equal(10m, duringRow.BalanceQty);
        Assert.Equal(4m, duringRow.DraftQty);
        Assert.Equal(6m, duringRow.AvailableQty);

        Assert.True((await sut.DeleteAsync([draft.BatchNo])).Succeeded);

        var after = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(after.Succeeded, after.ErrorMessage);
        var afterRow = Assert.Single(after.PoLines);
        Assert.Equal(0m, afterRow.DraftQty);
        Assert.Equal(10m, afterRow.AvailableQty);
    }

    [Fact]
    public async Task Post_with_stale_FrPurQty_validates_locked_balance()
    {
        var po = await CreatePoAsync(qty: 10m);
        var sut = CreateGrSut();

        var draft = await sut.SaveNewAsync(GrRequest(po, recvQty: 8m));
        Assert.True(draft.Succeeded, draft.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batchDetail = await db.IvTrxBatchDetails.SingleAsync(x => x.BatchNo == draft.BatchNo);
            Assert.Equal(10m, batchDetail.FrPurQty);
        }

        var other = await sut.SaveNewAsync(GrRequest(po, recvQty: 3m));
        Assert.True(other.Succeeded, other.ErrorMessage);
        Assert.True((await sut.PostAsync([other.BatchNo])).Succeeded);

        var stalePost = await sut.PostAsync([draft.BatchNo]);
        Assert.False(stalePost.Succeeded);
        Assert.Equal(0, stalePost.SucceededCount);
        Assert.Equal(1, stalePost.FailedCount);
        Assert.Contains(stalePost.Posting!.Batches, b => !b.Succeeded && b.ErrorMessage!.Contains("exceeds allowed receive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SaveNew_uses_po_warehouse_over_item_def_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "ALT",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "ALT",
                LocCode = "BINA",
                IsActive = true
            });
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "ALT");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToLocation = "BINA",
                    ToRecvQty = 2m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);

        var doc = await sut.GetAsync(save.BatchNo);
        Assert.True(doc.Succeeded, doc.ErrorMessage);
        var line = Assert.Single(doc.Document!.Lines);
        Assert.Equal("ALT", line.ToWarehouse);
        Assert.Equal("BINA", line.ToLocation);
    }

    [Fact]
    public async Task SaveNew_uses_item_def_warehouse_when_po_warehouse_blank()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: null);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var line = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines);
        Assert.Equal("MAIN", line.ToWarehouse);
        Assert.Equal("BIN1", line.ToLocation);
    }

    [Fact]
    public async Task SaveNew_requires_warehouse_when_po_and_def_invalid()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MISSING";
            item.DefLocation = null;
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "GONE");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("warehouse is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_ignores_def_location_belonging_to_other_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WHB",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WHB",
                LocCode = "LOCB",
                IsActive = true
            });
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "LOCB";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 5m, warehouse: "MAIN");
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("location is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_allocates_blank_lot_and_preserves_manual_lot()
    {
        await EnsureLotItemAsync("LOT1");
        var po = await CreatePoForItemAsync("LOT1", qty: 10m);
        var sut = CreateGrSut();

        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 2m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(30)
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var autoLot = Assert.Single((await sut.GetAsync(save.BatchNo)).Document!.Lines).ToLotNo;
        Assert.Equal("260912001", autoLot);

        var po2 = await CreatePoForItemAsync("LOT1", qty: 5m);
        var manual = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po2.PoNo,
                    PoRelNo = po2.PoRelNo,
                    PoLineNo = po2.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToLotNo = "SUP-LOT-9",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(10)
                }
            ]
        });
        Assert.True(manual.Succeeded, manual.ErrorMessage);
        Assert.Equal("SUP-LOT-9", Assert.Single((await sut.GetAsync(manual.BatchNo)).Document!.Lines).ToLotNo);
    }

    [Fact]
    public async Task SaveNew_two_blank_lots_same_item_are_unique()
    {
        await EnsureLotItemAsync("LOT2");
        var poA = await CreatePoForItemAsync("LOT2", qty: 5m);
        var poB = await CreatePoForItemAsync("LOT2", qty: 5m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = poA.PoNo,
                    PoRelNo = poA.PoRelNo,
                    PoLineNo = poA.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(5)
                },
                new IvGoodsReceiptLineRequest
                {
                    PoNo = poB.PoNo,
                    PoRelNo = poB.PoRelNo,
                    PoLineNo = poB.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active,
                    ExpiryDate = FixedToday.AddDays(5)
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        var lots = (await sut.GetAsync(save.BatchNo)).Document!.Lines.Select(x => x.ToLotNo).ToList();
        Assert.Equal(2, lots.Count);
        Assert.Equal(2, lots.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("260912001", lots);
        Assert.Contains("260912002", lots);
    }

    [Fact]
    public async Task AllocateLot_skips_existing_IvLot_and_NEW_drafts_including_NG()
    {
        await EnsureLotItemAsync("LOT3");
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvLots.Add(new IvLot
            {
                CompanyCode = "DEMO",
                ICode = "LOT3",
                LotNo = "260912001",
                SourceType = IvTrxTypes.MiscellaneousReceipt,
                IsActive = true
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9001,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.NonStockGoodsReceive,
                BatchStatus = IvBatchStatuses.New,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9001,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.NonStockGoodsReceive,
                        ICode = "LOT3",
                        ToLotNo = "260912002"
                    }
                ]
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9002,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9002,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.MiscellaneousReceipt,
                        ICode = "LOT3",
                        ToLotNo = "260912003"
                    }
                ]
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9003,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.GoodsReceive,
                BatchStatus = IvBatchStatuses.Posted,
                Details =
                [
                    new IvTrxBatchDetail
                    {
                        CompanyCode = "DEMO",
                        BranchCode = "HQ",
                        BatchNo = 9003,
                        TrxLineNo = 1,
                        TrxType = IvTrxTypes.GoodsReceive,
                        ICode = "LOT3",
                        ToLotNo = "260912004"
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateGrSut();
        var allocate = await sut.AllocateLotAsync("LOT3");
        Assert.True(allocate.Succeeded, allocate.ErrorMessage);
        Assert.Equal("260912004", allocate.AllocatedLotNo);
    }

    [Fact]
    public async Task SaveNew_requires_expiry_for_lot_controlled_item()
    {
        await EnsureLotItemAsync("LOT4");
        var po = await CreatePoForItemAsync("LOT4", qty: 3m);
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m,
                    IStatus = IvItemStatuses.Active
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("expiry date is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveNew_rejects_blank_po_line()
    {
        var sut = CreateGrSut();
        var save = await sut.SaveNewAsync(new IvGoodsReceiptSaveRequest
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = "",
                    PoRelNo = 0,
                    PoLineNo = 0,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = 1m
                }
            ]
        });
        Assert.False(save.Succeeded);
        Assert.Contains("PO line is required", save.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SearchPoLines_includes_def_warehouse_and_location()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var item = await db.IvStockMasters.SingleAsync(x => x.ICode == "A100");
            item.DefWarehouse = "MAIN";
            item.DefLocation = "BIN1";
            await db.SaveChangesAsync();
        }

        var po = await CreatePoAsync(qty: 2m);
        var sut = CreateGrSut();
        var rows = await sut.SearchPoLinesAsync(IvTrxTypes.GoodsReceive, po.PoNo);
        Assert.True(rows.Succeeded, rows.ErrorMessage);
        var row = Assert.Single(rows.PoLines);
        Assert.Equal("MAIN", row.DefWarehouse);
        Assert.Equal("BIN1", row.DefLocation);
    }

    private async Task EnsureLotItemAsync(string iCode)
    {
        await using var db = await _factory.CreateDbContextAsync();
        if (await db.IvStockMasters.AnyAsync(x => x.CompanyCode == "DEMO" && x.ICode == iCode))
        {
            return;
        }

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = iCode,
            IDesc = $"Lot item {iCode}",
            IClassCode = "RAW",
            StdUom = "EA",
            PurUom = "EA",
            PurStdPackSize = 1m,
            PurchasePrice = 10m,
            PurchaseTaxGroup = "SR",
            StockControl = true,
            LotControl = true,
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
            UnitPrice = 10m,
            Tolerance = 5m,
            OrdLevel = 0m,
            Status = "A",
            RowVersion = Guid.NewGuid().ToByteArray()
        });
        await db.SaveChangesAsync();
    }

    private async Task<(string PoNo, short PoRelNo, short PoLineNo)> CreatePoForItemAsync(string iCode, decimal qty)
        => await CreatePoAsync(qty, warehouse: "MAIN", iCode: iCode);

    private async Task<(string PoNo, short PoRelNo, short PoLineNo)> CreatePoAsync(
        decimal qty,
        string? warehouse = "MAIN",
        string iCode = "A100")
    {
        var poSut = CreatePoSut();
        var save = await poSut.SaveNewAsync(new PoOrderSaveRequest
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
                    TaxGroup = "SR",
                    ToWarehouse = warehouse
                }
            ]
        });
        Assert.True(save.Succeeded, save.ErrorMessage);
        return (save.PoNo!, save.Document!.PoRelNo, save.Document.Lines[0].Line);
    }

    private static IvGoodsReceiptSaveRequest GrRequest(
        (string PoNo, short PoRelNo, short PoLineNo) po,
        decimal recvQty) =>
        new()
        {
            TrxType = IvTrxTypes.GoodsReceive,
            TrxDate = FixedToday,
            Lines =
            [
                new IvGoodsReceiptLineRequest
                {
                    PoNo = po.PoNo,
                    PoRelNo = po.PoRelNo,
                    PoLineNo = po.PoLineNo,
                    ToWarehouse = "MAIN",
                    ToLocation = "BIN1",
                    ToRecvQty = recvQty
                }
            ]
        };

    private PoOrderService CreatePoSut()
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "MAIN");
        var attachRoot = Path.Combine(Path.GetTempPath(), "gr-po-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(attachRoot);
        var access = GrAccess();
        var attach = new PoOrderAttachmentService(
            _factory,
            tenant,
            access.Object,
            new FixedCurrentDateService(FixedToday),
            Options.Create(new AttachmentStorageOptions { RootPath = attachRoot }),
            Options.Create(new PoOrderOptions()),
            NullLogger<PoOrderAttachmentService>.Instance);

        return new PoOrderService(
            _factory,
            tenant,
            access.Object,
            _poNumbering,
            new FixedCurrentDateService(FixedToday),
            new PoOrderRepository(),
            Options.Create(new PoOrderOptions()),
            attach,
            NullLogger<PoOrderService>.Instance);
    }

    private IvGoodsReceiptService CreateGrSut()
    {
        var tenant = InventoryTenantTestHelper.CreateTenantContext(location: "MAIN");
        var access = GrAccess();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvGoodsReceiptService(
            _factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            NullLogger<IvGoodsReceiptService>.Instance);
    }

    private static Mock<IAccessRightService> GrAccess()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.PurchaseOrder,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryGoodsReceipt,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }
}
