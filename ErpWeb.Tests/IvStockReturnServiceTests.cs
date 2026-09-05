using ErpWeb.Core.Inventory;
using ErpWeb.Core.Menus;
using ErpWeb.Core.Numbering;
using ErpWeb.Core.Services;
using ErpWeb.Model.Data;
using ErpWeb.Model.Entities.Inventory;
using ErpWeb.Model.Repositories.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ErpWeb.Tests;

public class IvStockReturnServiceTests : IAsyncLifetime
{
    private static readonly DateTime FixedToday = new(2026, 8, 26);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockReturnServiceTests()
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
        db.IvWarehouses.AddRange(
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                WarehouseDesc = "Main WH",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "EMPTY",
                WarehouseDesc = "No bins",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "INACTIVE",
                IsActive = false
            },
            new IvWarehouse
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                IsActive = true
            },
            new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                WarehouseCode = "MAIN",
                IsActive = true
            });

        db.IvLocations.AddRange(
            new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                LocCode = "BIN1",
                LocDesc = "Bin 1",
                IsActive = true
            },
            new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "MAIN",
                LocCode = "DEAD",
                IsActive = false
            });

        db.MsUoms.AddRange(
            new MsUom { CompanyCode = "DEMO", UomCode = "EA", UomDesc = "Each", IsActive = true },
            new MsUom { CompanyCode = "DEMO", UomCode = "KG", UomDesc = "Kilogram", IsActive = true },
            new MsUom { CompanyCode = "DEMO", UomCode = "OLD", IsActive = false });

        db.IvClasses.AddRange(
            new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IDesc = "Raw", IsActive = true },
            new IvClass { CompanyCode = "DEMO", IClassCode = "FG", IDesc = "Finished", IsActive = true },
            new IvClass { CompanyCode = "DEMO", IClassCode = "X", IsActive = false });

        db.IvStatuses.AddRange(
            new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", StatusDesc = "Active", IsActive = true },
            new IvStatus { CompanyCode = "DEMO", IStatus = "DAMAGED", StatusDesc = "Damaged", IsActive = true },
            new IvStatus { CompanyCode = "DEMO", IStatus = "OLD", IsActive = false });

        db.IvStockMasters.AddRange(
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "A100",
                IDesc = "Widget A",
                IClassCode = "RAW",
                StdUom = "EA",
                LotControl = false,
                IsActive = true,
                PurchasePrice = 12.5m
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "LOT1",
                IDesc = "Lot item",
                IClassCode = "FG",
                StdUom = "KG",
                LotControl = true,
                IsActive = true,
                PurchasePrice = 3m
            },
            new IvStockMaster
            {
                CompanyCode = "DEMO",
                ICode = "DEAD",
                IDesc = "Inactive",
                IClassCode = "RAW",
                StdUom = "EA",
                IsActive = false
            },
            new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "A100",
                IDesc = "Other co",
                IClassCode = "RAW",
                StdUom = "EA",
                IsActive = true
            });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Save_BlankLocation_InvalidScope()
    {
        var sut = CreateSut(location: null);
        var result = await sut.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [ValidNonLotLineRequest()]
        });

        Assert.False(result.Succeeded);
        Assert.Contains("location", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
    }

    [Fact]
    public async Task Save_stamps_batch_location_from_claims_not_to_location()
    {
        var sut = CreateSut(location: "SITE");
        var result = await sut.SaveNewAsync(ValidNonLotLine(new DateTime(2026, 8, 24)));

        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        Assert.Equal("SITE", batch.LocationCode);
        var line = Assert.Single(batch.Details);
        Assert.Equal("BIN1", line.ToLocation);
        Assert.Equal("SITE", line.LocationCode);
    }

    [Fact]
    public async Task Save_writes_NEW_CR_staging_without_lot_or_balance()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(ValidNonLotLine(new DateTime(2026, 8, 24)));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(1, result.BatchNo);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        Assert.Equal(IvBatchStatuses.New, batch.BatchStatus);
        Assert.Null(batch.Remarks);
        var line = Assert.Single(batch.Details);
        Assert.Equal("A100", line.ICode);
        Assert.Equal("MAIN", line.ToWarehouse);
        Assert.Equal("BIN1", line.ToLocation);
        Assert.Equal(string.Empty, line.ToLotNo);
        Assert.Equal("EA", line.ToStdUom);
        Assert.Equal("RAW", line.IClassCode);
        Assert.Null(line.ExpiryDate);
        Assert.Null(line.ToLotId);
        Assert.Null(line.ToBalLocId);
        Assert.Equal(0, await db.IvLots.CountAsync());
        Assert.Equal(0, await db.IvBalLocs.CountAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Save_allows_duplicate_detail_lines()
    {
        var sut = CreateSut();
        var line = ValidNonLotLineRequest();
        var result = await sut.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [Clone(line), Clone(line)]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await db.IvTrxBatchDetails.CountAsync());
    }

    [Fact]
    public async Task Save_writes_header_remark()
    {
        var sut = CreateSut();
        var result = await sut.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            RefNo = "GRN-9",
            Remark = "  found in aisle  ",
            Lines = [ValidNonLotLineRequest()]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync();
        Assert.Equal("found in aisle", batch.Remarks);
        Assert.Equal("GRN-9", batch.RefNo);
    }

    [Fact]
    public async Task Save_uses_class_from_master_ignores_request_override()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.IClassCode = "FG";
        var result = await sut.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            Lines = [req]
        });

        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        var master = await db.IvStockMasters.SingleAsync(x => x.CompanyCode == "DEMO" && x.ICode == "A100");
        Assert.Equal("RAW", master.IClassCode);
        Assert.Equal("RAW", (await db.IvTrxBatchDetails.SingleAsync()).IClassCode);
    }

    [Fact]
    public async Task Save_uses_uom_from_master_ignores_request_override()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Uom = "KG";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal("EA", (await db.IvTrxBatchDetails.SingleAsync()).ToStdUom);
    }

    [Fact]
    public async Task Save_rejects_missing_reason()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Reason = null;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("reason", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Update_rejects_missing_reason()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var line = ValidNonLotLineRequest();
        line.Reason = null;
        var update = await sut.UpdateAsync(saved.BatchNo, Wrap(line));
        Assert.False(update.Succeeded);
        Assert.Contains("reason", update.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_unknown_reason()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Reason = "NOT_VALID";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("not valid", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_inactive_item()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ICode = "DEAD";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_cross_company_item()
    {
        var sut = CreateSut();
        // OTHER company also has A100 but DEMO lookup finds DEMO A100 — use code only on OTHER by removing DEMO? 
        // Actually DEMO has A100. Seed a code only on OTHER:
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvStockMasters.Add(new IvStockMaster
            {
                CompanyCode = "OTHER",
                ICode = "XONLY",
                StdUom = "EA",
                IClassCode = "RAW",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var req = ValidNonLotLineRequest();
        req.ICode = "XONLY";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_rejects_inactive_warehouse()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "INACTIVE";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("warehouse", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_cross_branch_warehouse()
    {
        var sut = CreateSut();
        // BR2 MAIN exists but user is HQ — still named MAIN which exists on HQ. Use EMPTY on wrong branch:
        // Seed WH only on BR2:
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                WarehouseCode = "ONLYBR2",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "ONLYBR2";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_requires_location_when_warehouse_has_active_locations()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("location is required", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_location_from_other_warehouse()
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvWarehouses.Add(new IvWarehouse
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WH2",
                IsActive = true
            });
            db.IvLocations.Add(new IvLocation
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                WarehouseCode = "WH2",
                LocCode = "LOC2",
                IsActive = true
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "WH2";
        req.ToLocation = "BIN1";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
        Assert.Contains("location", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_inactive_location()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLocation = "DEAD";
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Save_empty_warehouse_accepts_empty_location()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToWarehouse = "EMPTY";
        req.ToLocation = string.Empty;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(string.Empty, (await db.IvTrxBatchDetails.SingleAsync()).ToLocation);
    }

    [Fact]
    public async Task Save_rejects_inactive_status()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.IStatus = "OLD";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_rejects_zero_and_negative_quantity()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.Quantity = 0;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
        req.Quantity = -1;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_requires_lot_and_expiry_for_lot_controlled()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ToLotNo = null;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidLotLineRequest();
        req.ExpiryDate = null;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidLotLineRequest();
        req.ExpiryDate = FixedToday.AddDays(-1);
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_lot_today_and_future_accepted_without_creating_IvLot()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ExpiryDate = FixedToday;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        var line = await db.IvTrxBatchDetails.SingleAsync();
        Assert.Equal("260824001", line.ToLotNo);
        Assert.Equal(FixedToday, line.ExpiryDate);
        Assert.Null(line.ToLotId);
        Assert.Equal(0, await db.IvLots.CountAsync());
    }

    [Fact]
    public async Task Save_rejects_backdated_trx_with_expiry_before_app_today()
    {
        var sut = CreateSut();
        var req = ValidLotLineRequest();
        req.ExpiryDate = new DateTime(2026, 8, 23);
        var result = await sut.SaveNewAsync(new IvStockReturnSaveRequest
        {
            TrxDate = new DateTime(2026, 8, 20),
            Lines = [req]
        });
        Assert.False(result.Succeeded);
        Assert.Contains("expiry", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_rejects_non_lot_with_lotno_or_expiry()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLotNo = "SMUGGLE";
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);

        req = ValidNonLotLineRequest();
        req.ExpiryDate = FixedToday;
        Assert.False((await sut.SaveNewAsync(Wrap(req))).Succeeded);
    }

    [Fact]
    public async Task Save_normalizes_null_lot_on_non_lot_item()
    {
        var sut = CreateSut();
        var req = ValidNonLotLineRequest();
        req.ToLotNo = null;
        var result = await sut.SaveNewAsync(Wrap(req));
        Assert.True(result.Succeeded, result.ErrorMessage);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(string.Empty, (await db.IvTrxBatchDetails.SingleAsync()).ToLotNo);
    }

    [Fact]
    public async Task Save_malformed_payload_returns_fail()
    {
        var sut = CreateSut();
        Assert.False((await sut.SaveNewAsync(null!)).Succeeded);
        Assert.False((await sut.SaveNewAsync(new IvStockReturnSaveRequest { Lines = [] })).Succeeded);
        Assert.False((await sut.SaveNewAsync(new IvStockReturnSaveRequest { Lines = [null!] })).Succeeded);

        var missingItem = ValidNonLotLineRequest();
        missingItem.ICode = "";
        Assert.False((await sut.SaveNewAsync(Wrap(missingItem))).Succeeded);

        var missingWh = ValidNonLotLineRequest();
        missingWh.ToWarehouse = "";
        Assert.False((await sut.SaveNewAsync(Wrap(missingWh))).Succeeded);
    }

    [Fact]
    public async Task Save_is_denied_without_ADD()
    {
        var sut = CreateSut(canAdd: false);
        var result = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.False(result.Succeeded);
        Assert.Equal("Not authorized.", result.ErrorMessage);
    }

    [Fact]
    public async Task Sequential_saves_share_global_IV_BATCH_number()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        var second = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(1, first.BatchNo);
        Assert.Equal(2, second.BatchNo);
    }

    [Fact]
    public async Task Transaction_repo_rejects_update_delete_when_not_NEW()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()))).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.Include(x => x.Details).SingleAsync();
        batch.BatchStatus = IvBatchStatuses.Posted;
        await db.SaveChangesAsync();

        var repo = new IvStockTransactionRepository();
        Assert.False(await repo.UpdateAsync(db, batch));
        Assert.False(await repo.DeleteNewAsync(db, "DEMO", "HQ", batch.BatchNo));
    }

    [Fact]
    public async Task Search_is_scoped_to_company_branch_and_CR_only()
    {
        var sut = CreateSut();
        Assert.True((await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()))).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "BR2",
                BatchNo = 9001,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.CustomerReturn,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "OTHER-BR"
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "OTHER",
                BranchCode = "HQ",
                BatchNo = 9002,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.CustomerReturn,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "OTHER-CO"
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9003,
                TrxDtTime = FixedToday,
                TrxType = "XI",
                BatchStatus = IvBatchStatuses.New,
                RefNo = "NOT-CR"
            });
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 9004,
                TrxDtTime = FixedToday,
                TrxType = IvTrxTypes.MiscellaneousReceipt,
                BatchStatus = IvBatchStatuses.New,
                RefNo = "NOT-CR-MR"
            });
            await db.SaveChangesAsync();
        }

        var result = await sut.SearchAsync(new IvStockReturnListQuery { Take = 50 });
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.ListPage);
        Assert.Single(result.ListPage.Rows);
        Assert.Equal(1, result.ListPage.Rows[0].BatchNo);
        Assert.Equal(1, result.ListPage.Rows[0].LineCount);
        Assert.Equal(125m, result.ListPage.Rows[0].TotalAmount);
    }

    [Fact]
    public async Task Get_returns_header_and_lines()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var result = await sut.GetAsync(saved.BatchNo);
        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.Document);
        Assert.Equal(saved.BatchNo, result.Document.BatchNo);
        Assert.Equal(IvBatchStatuses.New, result.Document.BatchStatus);
        Assert.Single(result.Document.Lines);
        Assert.Equal("A100", result.Document.Lines[0].ICode);
        Assert.Equal("BIN1", result.Document.Lines[0].ToLocation);
        Assert.False(result.Document.Lines[0].LotControl);
        Assert.Equal(IvReturnReasons.Return, result.Document.Lines[0].Reason);
        Assert.Equal("found stock", result.Document.Lines[0].Remarks);
    }

    [Fact]
    public async Task Get_fails_for_unknown_or_wrong_type()
    {
        var sut = CreateSut();
        Assert.False((await sut.GetAsync(999)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvTrxBatches.Add(new IvTrxBatch
            {
                CompanyCode = "DEMO",
                BranchCode = "HQ",
                BatchNo = 77,
                TrxDtTime = FixedToday,
                TrxType = "XI",
                BatchStatus = IvBatchStatuses.New,
                RefNo = "XI-77"
            });
            await db.SaveChangesAsync();
        }

        var wrongType = await sut.GetAsync(77);
        Assert.False(wrongType.Succeeded);
        Assert.Equal("Stock return was not found.", wrongType.ErrorMessage);
    }

    [Fact]
    public async Task Update_succeeds_for_NEW_and_fails_for_POSTED()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var updatedLine = ValidNonLotLineRequest();
        updatedLine.Quantity = 4;
        updatedLine.UnitPrice = 2;
        updatedLine.Remarks = "updated";
        var update = await sut.UpdateAsync(saved.BatchNo, new IvStockReturnSaveRequest
        {
            TrxDate = FixedToday,
            RefNo = "REF-UPD",
            Remark = "header note",
            Lines = [updatedLine]
        });
        Assert.True(update.Succeeded, update.ErrorMessage);

        var loaded = await sut.GetAsync(saved.BatchNo);
        Assert.True(loaded.Succeeded, loaded.ErrorMessage);
        Assert.Equal("REF-UPD", loaded.Document!.RefNo);
        Assert.Equal("header note", loaded.Document.Remark);
        Assert.Equal(4m, loaded.Document.Lines[0].Quantity);
        Assert.Equal(2m, loaded.Document.Lines[0].UnitPrice);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var batch = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == saved.BatchNo);
            batch.BatchStatus = IvBatchStatuses.Posted;
            await db.SaveChangesAsync();
        }

        var postedUpdate = await sut.UpdateAsync(saved.BatchNo, Wrap(ValidNonLotLineRequest()));
        Assert.False(postedUpdate.Succeeded);
        Assert.Equal("Only NEW stock returns can be edited.", postedUpdate.ErrorMessage);
    }

    [Fact]
    public async Task Delete_succeeds_for_NEW_and_fails_for_POSTED()
    {
        var sut = CreateSut();
        var first = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        var second = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(first.Succeeded, first.ErrorMessage);
        Assert.True(second.Succeeded, second.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var posted = await db.IvTrxBatches.SingleAsync(x => x.BatchNo == second.BatchNo);
            posted.BatchStatus = IvBatchStatuses.Posted;
            await db.SaveChangesAsync();
        }

        var postedDelete = await sut.DeleteAsync([second.BatchNo]);
        Assert.False(postedDelete.Succeeded);

        var deleted = await sut.DeleteAsync([first.BatchNo]);
        Assert.True(deleted.Succeeded, deleted.ErrorMessage);

        Assert.False((await sut.GetAsync(first.BatchNo)).Succeeded);
        Assert.True((await sut.GetAsync(second.BatchNo)).Succeeded);
    }

    [Fact]
    public async Task Get_is_denied_without_ACCESS()
    {
        var sut = CreateSut(canAccess: false);
        Assert.False((await sut.GetAsync(1)).Succeeded);
        Assert.Equal("Not authorized.", (await sut.GetAsync(1)).ErrorMessage);
    }

    [Fact]
    public async Task Update_is_denied_without_EDIT()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var denied = CreateSut(canEdit: false);
        var update = await denied.UpdateAsync(saved.BatchNo, Wrap(ValidNonLotLineRequest()));
        Assert.False(update.Succeeded);
        Assert.Equal("Not authorized.", update.ErrorMessage);
    }

    [Fact]
    public async Task Delete_is_denied_without_DELETE()
    {
        var sut = CreateSut();
        var saved = await sut.SaveNewAsync(Wrap(ValidNonLotLineRequest()));
        Assert.True(saved.Succeeded, saved.ErrorMessage);

        var denied = CreateSut(canDelete: false);
        var delete = await denied.DeleteAsync([saved.BatchNo]);
        Assert.False(delete.Succeeded);
        Assert.Equal("Not authorized.", delete.ErrorMessage);
    }

    private static IvStockReturnSaveRequest ValidNonLotLine(DateTime trxDate) =>
        new() { TrxDate = trxDate, RefNo = "AUTO", Lines = [ValidNonLotLineRequest()] };

    private static IvStockReturnSaveRequest Wrap(IvStockReturnLineRequest line) =>
        new() { TrxDate = FixedToday, Lines = [line] };

    private static IvStockReturnLineRequest ValidNonLotLineRequest() =>
        new()
        {
            ICode = "A100",
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            Quantity = 10,
            UnitPrice = 12.5m,
            Uom = "EA",
            IClassCode = "RAW",
            IStatus = IvItemStatuses.Active,
            Reason = IvReturnReasons.Return,
            Remarks = "found stock"
        };

    private static IvStockReturnLineRequest ValidLotLineRequest() =>
        new()
        {
            ICode = "LOT1",
            ToWarehouse = "MAIN",
            ToLocation = "BIN1",
            ToLotNo = "260824001",
            Quantity = 2,
            UnitPrice = 3,
            Uom = "KG",
            IClassCode = "FG",
            IStatus = IvItemStatuses.Active,
            ExpiryDate = FixedToday.AddDays(10),
            Reason = IvReturnReasons.Return
        };

    private static IvStockReturnLineRequest Clone(IvStockReturnLineRequest x) =>
        new()
        {
            ICode = x.ICode,
            IDesc = x.IDesc,
            ToWarehouse = x.ToWarehouse,
            ToLocation = x.ToLocation,
            ToLotNo = x.ToLotNo,
            Quantity = x.Quantity,
            Uom = x.Uom,
            IClassCode = x.IClassCode,
            IStatus = x.IStatus,
            UnitPrice = x.UnitPrice,
            ExpiryDate = x.ExpiryDate,
            Reason = x.Reason,
            Remarks = x.Remarks
        };

    private IvStockReturnService CreateSut(
        bool canAdd = true,
        bool canAccess = true,
        bool canEdit = true,
        bool canDelete = true,
        bool canPost = true,
        bool canRollback = true,
        string? location = "SITE")
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Add,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAdd);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Access,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canAccess);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Edit,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canEdit);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Delete,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canDelete);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Post,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canPost);
        access.Setup(x => x.CanAsync(
                MenuCodes.InventoryStockReturn,
                PermissionCodes.Rollback,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(canRollback);

        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: location),
            access.Object,
            postingRepo,
            new IvStockCommonRepository(_factory),
            NullLogger<IvInventoryPostingService>.Instance);

        return new IvStockReturnService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(location: location),
            access.Object,
            new RunningNumberService(),
            new FixedCurrentDateService(FixedToday),
            new IvStockMasterRepository(_factory),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            NullLogger<IvStockReturnService>.Instance);
    }
}
