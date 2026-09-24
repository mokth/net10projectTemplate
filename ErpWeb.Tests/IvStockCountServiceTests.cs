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

[Trait(TestCategories.Name, TestCategories.Inventory)]
[Trait(TestCategories.Name, TestCategories.InventoryStockCount)]
public class IvStockCountServiceTests : IAsyncLifetime
{
    /// <summary>The company-local "today" every test runs against.</summary>
    private static readonly DateTime Today = new(2026, 9, 24);

    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<AppDbContext> _factory;

    public IvStockCountServiceTests()
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
            WarehouseCode = "MAIN",
            LocCode = "BIN1",
            IsActive = true
        });
        db.MsUoms.Add(new MsUom { CompanyCode = "DEMO", UomCode = "EA", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "RAW", IsActive = true });
        db.IvClasses.Add(new IvClass { CompanyCode = "DEMO", IClassCode = "FIN", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "ACTIVE", IsActive = true });
        db.IvStatuses.Add(new IvStatus { CompanyCode = "DEMO", IStatus = "SCRAPS", IsActive = true });

        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A100",
            IDesc = "Stock item",
            IType = "FINISHED",
            IClassCode = "RAW",
            ISubClassCode = "S1",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = 5m
        });
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "A101",
            IDesc = "Second stock item",
            IType = "FINISHED",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            LotControl = false,
            IsActive = true,
            PurchasePrice = 7m
        });
        // Uncontrolled item — must never be generated.
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "B200",
            IDesc = "Service item",
            IClassCode = "FIN",
            StdUom = "EA",
            StockControl = false,
            IsActive = true,
            PurchasePrice = 1m
        });
        // Inactive item — must never be generated while IncludeInactive is false.
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "C300",
            IDesc = "Retired item",
            IClassCode = "RAW",
            StdUom = "EA",
            StockControl = true,
            IsActive = false,
            PurchasePrice = 2m
        });
        // A second controlled item in a different class/type, for the scope-filter assertions.
        db.IvStockMasters.Add(new IvStockMaster
        {
            CompanyCode = "DEMO",
            ICode = "D400",
            IDesc = "Finished item",
            IType = "SERVICE",
            IClassCode = "FIN",
            StdUom = "EA",
            StockControl = true,
            IsActive = true,
            PurchasePrice = 3m
        });

        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    // ── Generate ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_snapshots_the_scope_and_is_CC_prefixed()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();

        var save = await svc.SaveAsync(Scope());
        Assert.True(save.Succeeded, save.ErrorMessage);
        Assert.StartsWith("CC", save.CountNo!);

        var gen = await svc.GenerateAsync(save.Id, discardCounts: false);
        Assert.True(gen.Succeeded, gen.ErrorMessage);
        Assert.Equal(1, gen.GeneratedLines);

        var doc = await LoadAsync(svc, save.CountNo!);
        var line = Assert.Single(doc.Lines);
        Assert.Equal("A100", line.ICode);
        Assert.Equal(100m, line.SystemQty);
        Assert.Null(line.PhysicalQty);
        Assert.Equal(5m, line.SnapshotUnitPrice);
        Assert.Equal(IvStockCountStatuses.Draft, doc.Status);
    }

    [Fact]
    public async Task Generate_count_numbers_are_unique()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var first = await svc.SaveAsync(Scope());
        var second = await svc.SaveAsync(Scope());
        Assert.NotEqual(first.CountNo, second.CountNo);
    }

    [Fact]
    public async Task IncludeZeroQty_false_excludes_zero_piles()
    {
        await SeedBalLocAsync("A100", 100m, loc: "BIN1");
        await SeedBalLocAsync("A100", 0m, loc: "BIN2");
        var svc = CreateService();

        var withZero = await NewSheetAsync(svc, Scope(includeZero: true));
        Assert.Equal(2, withZero.Lines.Count);

        var withoutZero = await NewSheetAsync(svc, Scope(includeZero: false));
        Assert.Equal(1, withoutZero.Lines.Count);
    }

    [Fact]
    public async Task Scope_excludes_uncontrolled_inactive_and_scrap_by_default()
    {
        await SeedBalLocAsync("A100", 100m);
        await SeedBalLocAsync("A101", 10m);
        await SeedBalLocAsync("B200", 10m);
        await SeedBalLocAsync("C300", 10m);
        await SeedBalLocAsync("A100", 10m, loc: "BIN9", status: "SCRAPS");

        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        Assert.Equal(new[] { "A100", "A101" }, sheet.Lines.Select(l => l.ICode).ToArray());
    }

    [Fact]
    public async Task Scope_status_list_reads_scraps_when_asked()
    {
        await SeedBalLocAsync("A100", 100m);
        await SeedBalLocAsync("A100", 10m, loc: "BIN9", status: "SCRAPS");

        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope(statuses: ["SCRAPS"]));

        var line = Assert.Single(sheet.Lines);
        Assert.Equal("SCRAPS", line.IStatus);
    }

    [Fact]
    public async Task Scope_filters_warehouse_class_subclass_type_and_item_list()
    {
        await SeedBalLocAsync("A100", 100m, wh: "MAIN");
        await SeedBalLocAsync("A101", 100m, wh: "WH2");
        await SeedBalLocAsync("D400", 100m, wh: "MAIN");

        var svc = CreateService();

        Assert.Equal(2, (await NewSheetAsync(svc, Scope(wh: "MAIN"))).Lines.Count);
        Assert.Equal(1, (await NewSheetAsync(svc, Scope(wh: "WH2"))).Lines.Count);
        Assert.Equal(2, (await NewSheetAsync(svc, Scope(wh: null, cls: "RAW"))).Lines.Count);
        Assert.Equal(1, (await NewSheetAsync(svc, Scope(wh: null, cls: "FIN"))).Lines.Count);
        Assert.Equal(2, (await NewSheetAsync(svc, Scope(wh: null, type: "FINISHED"))).Lines.Count);
        Assert.Equal(1, (await NewSheetAsync(svc, Scope(wh: null, type: "SERVICE"))).Lines.Count);
        Assert.Equal(1, (await NewSheetAsync(svc, Scope(wh: null, subClass: "S1"))).Lines.Count);
        Assert.Equal(1, (await NewSheetAsync(svc, Scope(wh: null, iCodes: ["A101"]))).Lines.Count);
    }

    [Fact]
    public async Task Regenerate_is_refused_while_a_count_exists_and_allowed_with_discard()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        Assert.Single(sheet.Lines);

        // A DRAFT sheet carrying a physical quantity cannot be produced through the service
        // (SaveCounts moves the sheet to COUNTED), so the D10 guard is pinned by writing that state
        // directly — it is defence in depth against any future path that writes a count in place.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var stored = await db.IvStockCountLines.SingleAsync();
            stored.PhysicalQty = 90m;
            await db.SaveChangesAsync();
        }

        var refused = await svc.GenerateAsync(sheet.Id, discardCounts: false);
        Assert.False(refused.Succeeded);
        Assert.Contains("already been counted", refused.ErrorMessage);

        var discarded = await svc.GenerateAsync(sheet.Id, discardCounts: true);
        Assert.True(discarded.Succeeded, discarded.ErrorMessage);

        var reloaded = await LoadAsync(svc, sheet.CountNo);
        Assert.Equal(IvStockCountStatuses.Draft, reloaded.Status);
        Assert.Null(Assert.Single(reloaded.Lines).PhysicalQty);
    }

    // ── Count entry ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveCounts_moves_to_COUNTED_and_save_without_a_quantity_stays_DRAFT()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        var empty = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = null }], null);
        Assert.True(empty.Succeeded, empty.ErrorMessage);
        Assert.Equal(IvStockCountStatuses.Draft, (await LoadAsync(svc, sheet.CountNo)).Status);

        var counted = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 95m }], null);
        Assert.True(counted.Succeeded, counted.ErrorMessage);

        var doc = await LoadAsync(svc, sheet.CountNo);
        Assert.Equal(IvStockCountStatuses.Counted, doc.Status);
        Assert.Equal(95m, Assert.Single(doc.Lines).PhysicalQty);
    }

    [Fact]
    public async Task SaveCounts_refuses_a_negative_quantity()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        var result = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = -1m }], null);

        Assert.False(result.Succeeded);
        Assert.Contains("negative", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task COUNTED_can_be_amended_until_posted()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 95m }], null)).Succeeded);
        Assert.Equal(IvStockCountStatuses.Counted, (await LoadAsync(svc, sheet.CountNo)).Status);

        // A day later the operator fixes a mistyped quantity: COUNTED is a working state, not a freeze.
        var amend = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null);
        Assert.True(amend.Succeeded, amend.ErrorMessage);

        var amended = await LoadAsync(svc, sheet.CountNo);
        var amendedLine = Assert.Single(amended.Lines);
        Assert.Equal(90m, amendedLine.PhysicalQty);
        Assert.Equal(1, (int)amendedLine.RecountCount);
        Assert.NotNull(amendedLine.CountedOn);

        // Scope/header stays a DRAFT-only structural edit — that one is not an amendment.
        var update = await svc.UpdateAsync(sheet.Id, Scope(remark: "edited", rowVersion: amended.RowVersion));
        Assert.False(update.Succeeded);

        // Posting still freezes everything.
        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);
        var afterPost = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 1m }], null);
        Assert.False(afterPost.Succeeded);
        Assert.Contains("frozen", afterPost.ErrorMessage!);
    }

    [Fact]
    public async Task SetItemCount_requires_a_slice_when_the_item_has_two_piles()
    {
        await SeedBalLocAsync("A100", 100m, loc: "BIN1");
        await SeedBalLocAsync("A100", 50m, loc: "BIN2");
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        Assert.Equal(2, sheet.Lines.Count);

        var refused = await svc.SetItemCountAsync(sheet.Id, "A100", 140m, null);
        Assert.False(refused.Succeeded);
        Assert.Contains("piles", refused.ErrorMessage);

        var named = await svc.SetItemCountAsync(sheet.Id, "A100", 90m, [sheet.Lines[0].BalLocId]);
        Assert.True(named.Succeeded, named.ErrorMessage);

        var doc = await LoadAsync(svc, sheet.CountNo);
        Assert.Equal(90m, doc.Lines.Single(l => l.BalLocId == sheet.Lines[0].BalLocId).PhysicalQty);
        Assert.Null(doc.Lines.Single(l => l.BalLocId == sheet.Lines[1].BalLocId).PhysicalQty);
    }

    [Fact]
    public async Task SetItemCount_writes_directly_when_the_item_has_one_pile()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        var result = await svc.SetItemCountAsync(sheet.Id, "A100", 80m, null);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(80m, Assert.Single((await LoadAsync(svc, sheet.CountNo)).Lines).PhysicalQty);
    }

    // ── Lifecycle guards ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_requires_DRAFT()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var draft = await NewSheetAsync(svc, Scope());
        Assert.True((await svc.DeleteAsync([draft.Id])).Succeeded);

        var counted = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(counted.Lines);
        Assert.True((await svc.SaveCountsAsync(
            counted.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 1m }], null)).Succeeded);

        var refused = await svc.DeleteAsync([counted.Id]);
        Assert.False(refused.Succeeded);
        Assert.Contains("not DRAFT", refused.ErrorMessage);
    }

    [Fact]
    public async Task CANCELLED_is_terminal()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        Assert.True((await svc.CancelAsync([sheet.Id], "wrong scope")).Succeeded);
        Assert.Equal(IvStockCountStatuses.Cancelled, (await LoadAsync(svc, sheet.CountNo)).Status);

        var line = Assert.Single(sheet.Lines);
        var reCount = await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 1m }], null);
        Assert.False(reCount.Succeeded);

        Assert.False((await svc.GenerateAsync(sheet.Id, discardCounts: true)).Succeeded);
        Assert.False((await svc.PostAsync(sheet.Id)).Succeeded);
    }

    [Fact]
    public async Task Stale_rowversion_is_reported_as_a_concurrency_loss()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        var stale = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]);
        var update = await svc.UpdateAsync(sheet.Id, Scope(remark: "x", rowVersion: stale));
        Assert.False(update.Succeeded);
        Assert.Contains("changed by another user", update.ErrorMessage);

        var missing = await svc.UpdateAsync(sheet.Id, Scope(remark: "x"));
        Assert.False(missing.Succeeded);
        Assert.Contains("Concurrency token is missing", missing.ErrorMessage);
    }

    // ── Post ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Post_decrease_writes_the_pile_down_and_records_history()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);

        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.NotNull(post.PostedBatchNo);
        Assert.Equal(0, post.PostedStaleLines);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(70m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());

        var history = await db.IvTrxHistories.SingleAsync();
        Assert.Equal(IvTrxTypes.StockAdjustment, history.TrxType);
        Assert.Equal(30m, history.FrStdQty);
        Assert.Equal(5m, history.UnitPrice);

        var header = await db.IvStockCountHdrs.SingleAsync();
        Assert.Equal(IvStockCountStatuses.Posted, header.Status);
        Assert.Equal(post.PostedBatchNo, header.PostedBatchNo);

        var batch = await db.IvTrxBatches.SingleAsync();
        Assert.Equal(IvBatchStatuses.Posted, batch.BatchStatus);
        Assert.Equal(IvTrxTypes.StockAdjustment, batch.TrxType);
        Assert.Equal(sheet.CountNo, batch.RefNo);
    }

    [Fact]
    public async Task Post_increase_and_counted_zero_both_work()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var zeroId = await SeedBalLocAsync("A101", 40m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        var inc = sheet.Lines.Single(l => l.BalLocId == balId);
        var zero = sheet.Lines.Single(l => l.BalLocId == zeroId);

        Assert.True((await svc.SaveCountsAsync(sheet.Id,
        [
            new IvStockCountLineCountRequest { BalLocId = inc.BalLocId, PhysicalQty = 130m },
            new IvStockCountLineCountRequest { BalLocId = zero.BalLocId, PhysicalQty = 0m }
        ], null)).Succeeded);

        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(130m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0m, await db.IvBalLocs.Where(x => x.Id == zeroId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(2, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Post_skips_lines_that_were_never_counted()
    {
        var countedId = await SeedBalLocAsync("A100", 100m);
        var untouchedId = await SeedBalLocAsync("A101", 40m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        Assert.True((await svc.SaveCountsAsync(sheet.Id,
            [new IvStockCountLineCountRequest
            {
                BalLocId = sheet.Lines.Single(l => l.BalLocId == countedId).BalLocId,
                PhysicalQty = 90m
            }], null)).Succeeded);

        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(90m, await db.IvBalLocs.Where(x => x.Id == countedId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(40m, await db.IvBalLocs.Where(x => x.Id == untouchedId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, await db.IvTrxHistories.CountAsync());
    }

    [Fact]
    public async Task Post_with_no_variance_is_POSTED_without_a_batch()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 100m }], null)).Succeeded);

        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.Null(post.PostedBatchNo);

        await using var db = await _factory.CreateDbContextAsync();
        var header = await db.IvStockCountHdrs.SingleAsync();
        Assert.Equal(IvStockCountStatuses.Posted, header.Status);
        Assert.Null(header.PostedBatchNo);
        Assert.Equal(0, await db.IvTrxBatches.CountAsync());
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
    }

    [Fact]
    public async Task Post_refuses_a_sheet_with_no_counted_lines()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        var post = await svc.PostAsync(sheet.Id);

        Assert.False(post.Succeeded);
        Assert.Contains("Only a COUNTED", post.ErrorMessage);
    }

    [Fact]
    public async Task Post_fails_hard_when_a_balance_disappeared()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.IvBalLocs.Remove(await db.IvBalLocs.SingleAsync(x => x.Id == balId));
            await db.SaveChangesAsync();
        }

        var post = await svc.PostAsync(sheet.Id);
        Assert.False(post.Succeeded);
        Assert.Contains("was not found", post.ErrorMessage);
    }

    [Fact]
    public async Task Post_fails_when_the_slice_no_longer_matches()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null)).Succeeded);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.WhCode = "WH2";
            await db.SaveChangesAsync();
        }

        var post = await svc.PostAsync(sheet.Id);
        Assert.False(post.Succeeded);
        Assert.Contains("no longer matches", post.ErrorMessage);
        Assert.Contains($"Line {line.LineNumber}", post.ErrorMessage);
    }

    [Fact]
    public async Task Post_flags_a_line_that_moved_after_the_count_and_stores_the_count()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 100m }], null)).Succeeded);

        // Stock moved between Generate and Post: the delta self-corrects against live stock (D1).
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
            bal.StdQty = 130m;
            await db.SaveChangesAsync();
        }

        var preview = await svc.PreviewPostAsync(sheet.Id);
        Assert.True(preview.Succeeded, preview.ErrorMessage);
        Assert.Equal(1, preview.Preview!.StaleLines);
        Assert.True(preview.Preview.RequiresStaleConfirmation);
        Assert.True(Assert.Single(preview.Preview.Lines).IsStale);

        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);
        Assert.Equal(1, post.PostedStaleLines);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(1, (await verify.IvStockCountHdrs.SingleAsync()).PostedStaleLines);
    }

    [Fact]
    public async Task Post_rounds_to_four_decimals_exactly_like_the_ADJ_engine()
    {
        var balId = await SeedBalLocAsync("A100", 10.12346m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 10.12344m }], null)).Succeeded);

        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(0.0001m, (await db.IvTrxHistories.SingleAsync()).FrStdQty);
        Assert.Equal(10.12336m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
    }

    // ── Dates (D9) ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountDate_in_the_future_is_refused()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();

        var result = await svc.SaveAsync(Scope(date: Today.AddDays(1)));

        Assert.False(result.Succeeded);
        Assert.Contains("future", result.ErrorMessage);
    }

    [Fact]
    public async Task CountDate_beyond_the_backdate_window_is_refused()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();

        var save = await svc.SaveAsync(Scope(date: Today.AddDays(-7)));
        Assert.True(save.Succeeded, save.ErrorMessage);

        var tooOld = await svc.SaveAsync(Scope(date: Today.AddDays(-(IvStockCountLimits.MaxBackdateDays + 1))));
        Assert.False(tooOld.Succeeded);
        Assert.Contains("older than the allowed", tooOld.ErrorMessage);
    }

    [Fact]
    public async Task Post_refuses_a_count_that_became_too_old_between_save_and_post()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService(clock: Clock(Today));
        var sheet = await NewSheetAsync(svc, Scope(date: Today.AddDays(-7)));
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null)).Succeeded);

        // The next day the same sheet is one day past the window: the POST-time guard must fire.
        var svcNextDay = CreateService(clock: Clock(Today.AddDays(1)));
        var post = await svcNextDay.PostAsync(sheet.Id);

        Assert.False(post.Succeeded);
        Assert.Contains("older than the allowed", post.ErrorMessage);

        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(IvStockCountStatuses.Counted, (await db.IvStockCountHdrs.SingleAsync()).Status);
    }

    [Fact]
    public async Task Post_dates_the_batch_from_CountDate_and_redates_the_pile()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var countDate = Today.AddDays(-3);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope(date: countDate));
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 90m }], null)).Succeeded);
        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var batch = await db.IvTrxBatches.SingleAsync();
        Assert.Equal(countDate, batch.TrxDtTime);

        // Documented D9 consequence: the stock-move helper writes TrxDtTime into IvBalLoc.TransDate,
        // so a back-dated count re-dates the pile. Pinned so it can never change silently.
        var bal = await db.IvBalLocs.SingleAsync(x => x.Id == balId);
        Assert.Equal(countDate, bal.TransDate);
    }

    // ── Rollback / re-post (I1) ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rollback_then_recount_then_repost_allocates_a_new_batch()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);
        var first = await svc.PostAsync(sheet.Id);
        Assert.True(first.Succeeded, first.ErrorMessage);

        var noReason = await svc.RollbackAsync(sheet.Id, reason: null);
        Assert.False(noReason.Succeeded);

        var rollback = await svc.RollbackAsync(sheet.Id, "mis-counted");
        Assert.True(rollback.Succeeded, rollback.ErrorMessage);

        await using (var db = await _factory.CreateDbContextAsync())
        {
            Assert.Equal(100m, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
            Assert.Equal(IvBatchStatuses.New, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        }

        var rolled = await LoadAsync(svc, sheet.CountNo);
        Assert.Equal(IvStockCountStatuses.RolledBack, rolled.Status);
        Assert.Equal(first.PostedBatchNo, rolled.PostedBatchNo);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 60m }], null)).Succeeded);

        var second = await svc.PostAsync(sheet.Id);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.NotEqual(first.PostedBatchNo, second.PostedBatchNo);

        await using var verify = await _factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.IvTrxBatches.CountAsync());
        Assert.Equal(60m, await verify.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(IvStockCountStatuses.Posted, (await verify.IvStockCountHdrs.SingleAsync()).Status);
    }

    [Fact]
    public async Task POSTED_evidence_is_frozen()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 80m }], null)).Succeeded);
        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);

        var doc = await LoadAsync(svc, sheet.CountNo);
        Assert.False((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 1m }], null)).Succeeded);
        Assert.False((await svc.UpdateAsync(sheet.Id, Scope(remark: "edit", rowVersion: doc.RowVersion))).Succeeded);
        Assert.False((await svc.DeleteAsync([sheet.Id])).Succeeded);
        Assert.False((await svc.CancelAsync([sheet.Id], "nope")).Succeeded);
        Assert.False((await svc.GenerateAsync(sheet.Id, discardCounts: true)).Succeeded);

        await using var db = await _factory.CreateDbContextAsync();
        var stored = await db.IvStockCountLines.SingleAsync();
        Assert.Equal(80m, stored.PhysicalQty);
        Assert.Equal(100m, stored.SystemQty);
    }

    // ── Recover (D8) ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Recover_resets_the_header_when_the_batch_was_rolled_back_elsewhere()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);
        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        // The Stock Adjustment list rolls the SAME batch back under its own menu permission.
        var posting = CreatePostingService();
        Assert.True((await posting.RollbackAsync(IvTrxTypes.StockAdjustment, [post.PostedBatchNo!.Value])).Succeeded);

        var recover = await svc.RecoverAsync(sheet.Id);
        Assert.True(recover.Succeeded, recover.ErrorMessage);
        Assert.Equal(IvStockCountStatuses.Counted, (await LoadAsync(svc, sheet.CountNo)).Status);

        // Re-posting then works and allocates a NEW batch number.
        var repost = await svc.PostAsync(sheet.Id);
        Assert.True(repost.Succeeded, repost.ErrorMessage);
        Assert.NotEqual(post.PostedBatchNo, repost.PostedBatchNo);
    }

    // ── Preview ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Preview_summarises_the_sheet()
    {
        var down = await SeedBalLocAsync("A100", 100m);
        var up = await SeedBalLocAsync("A101", 40m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        Assert.True((await svc.SaveCountsAsync(sheet.Id,
        [
            new IvStockCountLineCountRequest { BalLocId = sheet.Lines.Single(l => l.BalLocId == down).BalLocId, PhysicalQty = 70m },
            new IvStockCountLineCountRequest { BalLocId = sheet.Lines.Single(l => l.BalLocId == up).BalLocId, PhysicalQty = 55m }
        ], null)).Succeeded);

        var preview = await svc.PreviewPostAsync(sheet.Id);

        Assert.True(preview.Succeeded, preview.ErrorMessage);
        var page = preview.Preview!;
        Assert.Equal(1, page.DecreaseLines);
        Assert.Equal(1, page.IncreaseLines);
        Assert.Equal(30m, page.TotalDecrease);
        Assert.Equal(15m, page.TotalIncrease);
        Assert.Equal(0, page.NotCountedLines);
        Assert.Null(page.DateError);
        Assert.True(page.CanPost);
        Assert.False(page.RequiresStaleConfirmation);
    }

    // ── Transaction integrity (both, or neither) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_failure_after_the_stock_move_rolls_everything_back()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var (svc, posting) = CreateServiceWithPosting();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);

        posting.TestHookAfterAdjStockUpdate = () => throw new InvalidOperationException("boom: after stock");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostAsync(sheet.Id));
        posting.TestHookAfterAdjStockUpdate = null;

        await AssertNothingMovedAsync(balId, expectedQty: 100m);
    }

    [Fact]
    public async Task A_failure_after_history_rolls_everything_back()
    {
        var balId = await SeedBalLocAsync("A100", 100m);
        var (svc, posting) = CreateServiceWithPosting();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);

        posting.TestHookAfterAdjHistory = () => throw new InvalidOperationException("boom: after history");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostAsync(sheet.Id));
        posting.TestHookAfterAdjHistory = null;

        await AssertNothingMovedAsync(balId, expectedQty: 100m);
    }

    private async Task AssertNothingMovedAsync(int balId, decimal expectedQty)
    {
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Equal(expectedQty, await db.IvBalLocs.Where(x => x.Id == balId).Select(x => x.StdQty).SingleAsync());
        Assert.Equal(0, await db.IvTrxHistories.CountAsync());
        Assert.Equal(IvStockCountStatuses.Counted, (await db.IvStockCountHdrs.SingleAsync()).Status);
        if (await db.IvTrxBatches.AnyAsync())
        {
            Assert.NotEqual(IvBatchStatuses.Posted, (await db.IvTrxBatches.SingleAsync()).BatchStatus);
        }
    }

    // ── Reconciliation findings (D8) ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reconciliation_flags_a_POSTED_count_whose_batch_is_no_longer_posted()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 70m }], null)).Succeeded);
        var post = await svc.PostAsync(sheet.Id);
        Assert.True(post.Succeeded, post.ErrorMessage);

        var posting = CreatePostingService();
        Assert.True((await posting.RollbackAsync(IvTrxTypes.StockAdjustment, [post.PostedBatchNo!.Value])).Succeeded);

        var reconciliation = await ReconcileAsync();
        Assert.Contains(reconciliation.Findings, f => f.Code == "STOCK_COUNT_BATCH_NOT_POSTED");
    }

    [Fact]
    public async Task Reconciliation_does_not_flag_the_all_zero_variance_post()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());
        var line = Assert.Single(sheet.Lines);

        Assert.True((await svc.SaveCountsAsync(
            sheet.Id, [new IvStockCountLineCountRequest { BalLocId = line.BalLocId, PhysicalQty = 100m }], null)).Succeeded);
        Assert.True((await svc.PostAsync(sheet.Id)).Succeeded);

        var reconciliation = await ReconcileAsync();
        Assert.DoesNotContain(reconciliation.Findings, f => f.Code.StartsWith("STOCK_COUNT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconciliation_flags_a_POSTED_count_with_no_batch_but_a_variance()
    {
        await SeedBalLocAsync("A100", 100m);
        var svc = CreateService();
        var sheet = await NewSheetAsync(svc, Scope());

        // Craft the impossible state directly: POSTED with no batch although a line was counted short.
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var header = await db.IvStockCountHdrs.SingleAsync();
            header.Status = IvStockCountStatuses.Posted;
            header.PostedBatchNo = null;
            header.PostedStaleLines = 0;
            var stored = await db.IvStockCountLines.SingleAsync();
            stored.PhysicalQty = 70m;
            await db.SaveChangesAsync();
        }

        var reconciliation = await ReconcileAsync();
        Assert.Contains(reconciliation.Findings, f => f.Code == "STOCK_COUNT_UNPOSTED_VARIANCE");
    }

    private async Task<IvInventoryReconcileResult> ReconcileAsync()
    {
        var service = new IvInventoryReconciliationService(
            _factory, InventoryTenantTestHelper.CreateTenantContext());
        var result = await service.ReconcileAsync();
        Assert.True(result.Succeeded, result.ErrorMessage);
        return result;
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────────

    private static ICurrentDateService Clock(DateTime today) => new FixedCurrentDateService(today);

    private static IvStockCountSaveRequest Scope(
        string? wh = "MAIN",
        string? cls = null,
        string? subClass = null,
        string? type = null,
        IReadOnlyList<string>? statuses = null,
        IReadOnlyList<string>? iCodes = null,
        bool includeZero = true,
        string? remark = null,
        string? rowVersion = null,
        DateTime? date = null) =>
        new()
        {
            CountDate = date ?? Today,
            WHCode = wh,
            IClassCode = cls,
            ISubClassCode = subClass,
            IType = type,
            Statuses = statuses,
            ICodes = iCodes,
            IncludeZeroQty = includeZero,
            Remark = remark,
            RowVersion = rowVersion
        };

    private async Task<IvStockCountDocument> NewSheetAsync(IvStockCountService svc, IvStockCountSaveRequest request)
        => await LoadAsync(svc, await NewSheetIdAsync(svc, request));

    private async Task<string> NewSheetIdAsync(IvStockCountService svc, IvStockCountSaveRequest request)
    {
        var save = await svc.SaveAsync(request);
        Assert.True(save.Succeeded, save.ErrorMessage);
        var gen = await svc.GenerateAsync(save.Id, discardCounts: false);
        Assert.True(gen.Succeeded, gen.ErrorMessage);
        return save.CountNo!;
    }

    private async Task<IvStockCountDocument> LoadAsync(IvStockCountService svc, string countNo)
    {
        var result = await svc.GetAsync(countNo);
        Assert.True(result.Succeeded, result.ErrorMessage);
        return result.Document!;
    }

    private IvStockCountService CreateService(
        ICurrentDateService? clock = null,
        Mock<IAccessRightService>? access = null)
        => CreateServiceWithPosting(clock, access).Service;

    private (IvStockCountService Service, IvInventoryPostingService Posting) CreateServiceWithPosting(
        ICurrentDateService? clock = null,
        Mock<IAccessRightService>? access = null)
    {
        access ??= Access();
        var tenant = InventoryTenantTestHelper.CreateTenantContext();
        var postingRepo = new IvStockPostingRepository();
        var posting = new IvInventoryPostingService(
            _factory,
            tenant,
            access.Object,
            postingRepo,
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);

        var service = new IvStockCountService(
            _factory,
            tenant,
            access.Object,
            new RunningNumberService(),
            clock ?? Clock(Today),
            new IvStockCommonRepository(_factory),
            new IvStockTransactionRepository(),
            postingRepo,
            posting,
            NullLogger<IvStockCountService>.Instance);

        return (service, posting);
    }

    private IvInventoryPostingService CreatePostingService()
    {
        var access = Access();

        return new IvInventoryPostingService(
            _factory,
            InventoryTenantTestHelper.CreateTenantContext(),
            access.Object,
            new IvStockPostingRepository(),
            new IvStockCommonRepository(_factory),
            new PoOrderRepository(),
            NullLogger<IvInventoryPostingService>.Instance);
    }

    private static Mock<IAccessRightService> Access()
    {
        var access = new Mock<IAccessRightService>();
        access.Setup(x => x.CanAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return access;
    }

    private async Task<int> SeedBalLocAsync(
        string iCode,
        decimal qty,
        string wh = "MAIN",
        string loc = "BIN1",
        string lot = "",
        string status = "ACTIVE",
        decimal? unitPrice = null)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var bal = new IvBalLoc
        {
            CompanyCode = "DEMO",
            BranchCode = "HQ",
            ICode = iCode,
            WhCode = wh,
            LocCode = loc,
            LotNo = lot,
            IStatus = status,
            StdQty = qty,
            StdUom = "EA",
            UnitPrice = unitPrice
        };
        db.IvBalLocs.Add(bal);
        await db.SaveChangesAsync();
        return bal.Id;
    }
}
